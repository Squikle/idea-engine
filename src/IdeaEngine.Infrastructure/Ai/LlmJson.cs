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

    /// <summary>Deserializes the first JSON object found in the content, or null.</summary>
    public static T? TryParse<T>(string? content)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var json = content.Trim();
        if (!json.StartsWith('{'))
        {
            var start = json.IndexOf('{', StringComparison.Ordinal);
            var end = json.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                return null;
            }

            json = json[start..(end + 1)];
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
}
