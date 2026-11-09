using System.Text.RegularExpressions;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Utilities;

/// <summary>
/// Picks which cataloged files are worth piece-hashing for a torrent entry:
/// same exact byte size AND same file extension (a rename never turns an
/// .mkv into an .mp4), ordered so the file whose name/folders most resemble
/// the torrent is tried first. Nothing is excluded on name alone - a renamed
/// file is the whole point - so the order only affects which is hashed first;
/// the piece hashes still decide every match.
/// </summary>
public static partial class CrossSeedCandidateFilter
{
    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();

    public static List<MediaAsset> SameSizeCandidates(
        IEnumerable<MediaAsset> assets,
        long sizeBytes,
        string expectedFileName,
        string? torrentName = null)
    {
        var extension =
            Path.GetExtension(expectedFileName);

        var tokens =
            Tokenize($"{expectedFileName} {torrentName}");

        return assets
            .Where(asset =>
                asset.FileSizeBytes == sizeBytes &&
                (string.IsNullOrEmpty(extension) ||
                 string.Equals(
                     Path.GetExtension(asset.CanonicalPath),
                     extension,
                     StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(asset => Score(asset, tokens))
            .ToList();
    }

    private static int Score(
        MediaAsset asset,
        HashSet<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return 0;
        }

        var assetTokens =
            Tokenize(asset.CanonicalPath);

        return tokens.Count(assetTokens.Contains);
    }

    private static HashSet<string> Tokenize(
        string text) =>
        NonAlphanumeric()
            .Split(text.ToLowerInvariant())
            .Where(token => token.Length >= 2)
            .ToHashSet();
}
