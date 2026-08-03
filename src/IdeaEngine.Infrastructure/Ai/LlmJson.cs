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
        if (balanced is not null && !ReferenceEquals(balanced, trimmed))
        {
            yield return balanced;
        }

        if (balanced is not null)
        {
            var sanitized = EscapeControlCharsInStrings(balanced);
            if (sanitized != balanced)
            {
                yield return sanitized;
            }
        }
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
}
