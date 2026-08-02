namespace IdeaEngine.Core.Common;

/// <summary>Extracts one version's section from CHANGELOG.md (## x.y.z headings).</summary>
public static class ChangelogParser
{
    public static string? TryGetSection(string changelog, string version)
    {
        if (string.IsNullOrWhiteSpace(changelog) || string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var lines = changelog.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var collected = new List<string>();
        var inSection = false;

        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inSection)
                {
                    break;
                }

                var heading = line[3..].TrimStart();
                inSection = heading.StartsWith(version, StringComparison.Ordinal)
                    && (heading.Length == version.Length || !char.IsAsciiDigit(heading[version.Length]));
                continue;
            }

            if (inSection)
            {
                collected.Add(line);
            }
        }

        var section = string.Join('\n', collected).Trim();
        return section.Length > 0 ? section : null;
    }
}
