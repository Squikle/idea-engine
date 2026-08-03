using System.Text.Json;
using System.Text.Json.Serialization;

namespace IdeaEngine.Infrastructure.Ai;

/// <summary>Tolerant JSON extraction from LLM output (code fences, leading chatter).</summary>
public static class LlmJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>For OUR OWN serialized jsonb columns (strict, no fence tolerance).</summary>
    public static T? SafeDeserialize<T>(string? json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deserializes the first JSON object found in the content, or null. Tolerates code
    /// fences, leading/trailing prose (balanced-brace extraction) and raw control
    /// characters inside string literals (a frequent LLM slip that is invalid JSON).
    /// </summary>
    public static T? TryParse<T>(string? content)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var trimmed = content.Trim();
        foreach (var candidate in Candidates(trimmed))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<T>(candidate, Options);
                if (parsed is not null)
                {
                    return parsed;
                }
            }
            catch (JsonException)
            {
                // try the next, more forgiving candidate
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates(string trimmed)
    {
        yield return trimmed;

        var balanced = ExtractBalancedObject(trimmed);
        var basis = balanced ?? BeheadToFirstBrace(trimmed);
        if (basis is null)
        {
            yield break;
        }

        if (!ReferenceEquals(basis, trimmed))
        {
            yield return basis;
        }

        var sanitized = EscapeControlCharsInStrings(basis);
        if (sanitized != basis)
        {
            yield return sanitized;
        }

        var quoteFixed = EscapeInnerQuotes(sanitized);
        if (quoteFixed != sanitized)
        {
            yield return quoteFixed;
        }

        // Last resort: models drop closers ("}" before "]", or truncate mid-scope).
        // Inserting the expected closers is a no-op on valid JSON (job #54 shape).
        var bracketFixed = RepairBrackets(quoteFixed);
        if (bracketFixed != quoteFixed)
        {
            yield return bracketFixed;
        }
    }

    /// <summary>When no balanced object exists (missing closers), start at the first brace.</summary>
    private static string? BeheadToFirstBrace(string text)
    {
        var start = text.IndexOf('{', StringComparison.Ordinal);
        return start < 0 ? null : text[start..];
    }

    /// <summary>First balanced {...} block, string-and-escape aware. Null when unbalanced.</summary>
    private static string? ExtractBalancedObject(string text)
    {
        var start = text.IndexOf('{', StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\' && inString)
            {
                escaped = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}' && --depth == 0)
            {
                return text[start..(i + 1)];
            }
        }

        return null;
    }

    /// <summary>Escapes raw control characters INSIDE string literals (invalid JSON, common LLM slip).</summary>
    private static string EscapeControlCharsInStrings(string json)
    {
        var builder = new System.Text.StringBuilder(json.Length + 16);
        var inString = false;
        var escaped = false;
        foreach (var c in json)
        {
            if (escaped)
            {
                builder.Append(c);
                escaped = false;
                continue;
            }

            if (c == '\\' && inString)
            {
                builder.Append(c);
                escaped = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                builder.Append(c);
                continue;
            }

            if (inString && c < ' ')
            {
                builder.Append(c switch
                {
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    _ => " ",
                });
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Escapes quotes that are CONTENT, not delimiters: a closing quote must be followed
    /// by a structural character (, : ] }) — anything else means the model forgot \".
    /// </summary>
    private static string EscapeInnerQuotes(string json)
    {
        var builder = new System.Text.StringBuilder(json.Length + 8);
        var inString = false;
        var escaped = false;
        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (escaped)
            {
                builder.Append(c);
                escaped = false;
                continue;
            }

            if (c == '\\' && inString)
            {
                builder.Append(c);
                escaped = true;
                continue;
            }

            if (c == '"' && inString)
            {
                var j = i + 1;
                while (j < json.Length && json[j] is ' ' or '\n' or '\r' or '\t')
                {
                    j++;
                }

                if (j < json.Length && json[j] is not (',' or ':' or ']' or '}'))
                {
                    builder.Append("\\\"");
                    continue; // content quote, string stays open
                }

                inString = false;
                builder.Append(c);
                continue;
            }

            if (c == '"')
            {
                inString = true;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Inserts missing closers: a mismatched closer pops the stack with the EXPECTED
    /// closers first; EOF closes an open string and every open scope. No-op when valid.
    /// </summary>
    private static string RepairBrackets(string json)
    {
        var builder = new System.Text.StringBuilder(json.Length + 8);
        var stack = new Stack<char>();
        var inString = false;
        var escaped = false;
        foreach (var c in json)
        {
            if (escaped)
            {
                builder.Append(c);
                escaped = false;
                continue;
            }

            if (c == '\\' && inString)
            {
                builder.Append(c);
                escaped = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                builder.Append(c);
                continue;
            }

            if (inString)
            {
                builder.Append(c);
                continue;
            }

            switch (c)
            {
                case '{' or '[':
                    stack.Push(c);
                    builder.Append(c);
                    break;
                case '}' or ']':
                    var expectedOpener = c == '}' ? '{' : '[';
                    while (stack.Count > 0 && stack.Peek() != expectedOpener)
                    {
                        builder.Append(stack.Pop() == '{' ? '}' : ']');
                    }

                    if (stack.Count == 0)
                    {
                        break; // stray closer: drop it
                    }

                    stack.Pop();
                    builder.Append(c);
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        if (inString)
        {
            builder.Append('"');
        }

        while (stack.Count > 0)
        {
            builder.Append(stack.Pop() == '{' ? '}' : ']');
        }

        return builder.ToString();
    }
}
