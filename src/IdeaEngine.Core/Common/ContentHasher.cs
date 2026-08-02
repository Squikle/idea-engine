using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace IdeaEngine.Core.Common;

/// <summary>
/// Produces stable content hashes used to deduplicate items across fetches and sources.
/// Normalization is intentionally aggressive (case- and whitespace-insensitive) so that
/// near-identical reposts collapse to the same hash. Semantic near-duplicates are handled
/// separately via embeddings; this is only the cheap exact-ish layer.
/// </summary>
public static partial class ContentHasher
{
    /// <returns>64-char lowercase hex SHA-256 of the normalized content.</returns>
    public static string Compute(string title, string? body = null)
    {
        ArgumentNullException.ThrowIfNull(title);

        var normalized = $"{Normalize(title)}\n{Normalize(body ?? string.Empty)}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static string Normalize(string value) =>
        Whitespace().Replace(value.Trim().ToLowerInvariant(), " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
