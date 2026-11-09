namespace Simplifiarr.Core.Utilities;

/// <summary>
/// Normalizes a title down to lowercase letters/digits only (plus a
/// leading article strip), for matching a release-parsed title against a
/// catalog title (e.g. Arr-sourced) that may differ only in punctuation
/// or article placement the parser can't reliably preserve - a hyphen or
/// period that's part of the real title ("K-Pop", "Your Name.",
/// "Spider-Verse") gets treated the same as a scene-release word
/// separator and stripped/spaced differently, and a release folder name
/// commonly keeps/adds a leading "The" that the Arr-sourced canonical
/// title omits (or vice versa).
/// </summary>
public static class TitleNormalizer
{
    private static readonly string[] LeadingArticles =
        ["the ", "a ", "an "];

    public static string NormalizeForMatch(
        string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var trimmed =
            title.Trim();

        foreach (var article in LeadingArticles)
        {
            if (trimmed.StartsWith(article, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[article.Length..];

                break;
            }
        }

        return new string(
                trimmed
                    .Where(char.IsLetterOrDigit)
                    .ToArray())
            .ToLowerInvariant();
    }
}
