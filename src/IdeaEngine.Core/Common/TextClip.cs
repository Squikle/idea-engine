namespace IdeaEngine.Core.Common;

/// <summary>Human-friendly truncation: word boundary + ellipsis, never a mid-word cut.</summary>
public static class TextClip
{
    public static string Clip(string? value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value ?? string.Empty;
        }

        var cut = value[..(max - 1)];
        var lastSpace = cut.LastIndexOf(' ');
        if (lastSpace > max - 20 && lastSpace > 0)
        {
            cut = cut[..lastSpace];
        }

        return cut.TrimEnd() + "…";
    }
}
