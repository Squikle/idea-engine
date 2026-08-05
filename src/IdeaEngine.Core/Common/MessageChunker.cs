namespace IdeaEngine.Core.Common;

/// <summary>
/// Splits oversized Telegram messages at line boundaries (hard limit 4096; we chunk at
/// 4000 for headroom). Content is never truncated - it arrives as multiple messages.
/// </summary>
public static class MessageChunker
{
    public const int ChunkLimit = 4000;

    public static IEnumerable<string> Split(string text)
    {
        if (text.Length <= ChunkLimit)
        {
            yield return text;
            yield break;
        }

        var current = new System.Text.StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (current.Length + line.Length + 1 > ChunkLimit && current.Length > 0)
            {
                yield return current.ToString().TrimEnd();
                current.Clear();
            }

            current.Append(line).Append('\n');
        }

        if (current.Length > 0)
        {
            yield return current.ToString().TrimEnd();
        }
    }
}
