using System.Text.RegularExpressions;

namespace Simplifiarr.Core.Models;

/// <summary>
/// Which smaller releases Size Reduction offers: the encodes someone is
/// happy to swap to, and the releases and files they've chosen to ignore.
/// Set on the Size Reduction page.
/// </summary>
public class EncodeRules
{
    /// <summary>The video codecs that can be ticked, with what they're called.</summary>
    public static readonly IReadOnlyList<(string Codec, string Label)> CodecChoices =
    [
        ("HEVC", "HEVC / x265"),
        ("AV1", "AV1"),
        ("H264", "H.264 / x264"),
        ("VP9", "VP9")
    ];

    /// <summary>Words that mark a release no one wants in a library.</summary>
    public static readonly IReadOnlyList<string> DefaultAvoidWords =
        ["CAM", "HDCAM", "TELESYNC", "HDTS", "TELECINE"];

    /// <summary>Offered as one-click additions to the words to avoid.</summary>
    public static readonly IReadOnlyList<(string Word, string Note)> AvoidSuggestions =
    [
        ("HDTV", "Recorded from TV broadcasts: often logos and lower quality."),
        ("YIFY", "Very small encodes; quality varies."),
        ("YTS", "Very small encodes; quality varies."),
        ("3D", "3D versions."),
        ("Hardcoded", "Subtitles burnt into the picture."),
        ("Dubbed", "Dubbed audio only.")
    ];

    /// <summary>
    /// Video codecs a release may be. Empty: any codec.
    /// </summary>
    public List<string> Codecs { get; set; } = [];

    /// <summary>
    /// With codecs ticked, also allow releases whose name doesn't say.
    /// </summary>
    public bool AllowUnstatedCodec { get; set; } = true;

    /// <summary>A release whose name has any of these words is left out.</summary>
    public List<string> AvoidWords { get; set; } = [.. DefaultAvoidWords];

    /// <summary>
    /// When there are any, a release must have at least one of these words
    /// (a group you trust, "REPACK"...).
    /// </summary>
    public List<string> RequireWords { get; set; } = [];

    /// <summary>Release names ignored by hand; they stay hidden after later searches.</summary>
    public List<string> IgnoredReleases { get; set; } = [];

    /// <summary>Library files that never get offered a smaller copy.</summary>
    public List<string> IgnoredFiles { get; set; } = [];

    /// <summary>"x265", "H.265" or "hevc" -> "HEVC"; empty when there's no codec.</summary>
    public static string CodecFamily(string? codec)
    {
        var value =
            Regex.Replace(codec ?? string.Empty, @"[^a-zA-Z0-9]", string.Empty).ToUpperInvariant();

        return value switch
        {
            "HEVC" or "H265" or "X265" => "HEVC",
            "H264" or "X264" or "AVC" => "H264",
            "AV1" => "AV1",
            "VP9" => "VP9",
            "" => string.Empty,
            _ => value
        };
    }

    /// <summary>
    /// Why this release is left out, or null when it is offered. The file
    /// itself being ignored is checked separately.
    /// </summary>
    public string? WhyLeftOut(
        string releaseTitle,
        string? releaseCodec)
    {
        if (IgnoredReleases.Contains(releaseTitle, StringComparer.OrdinalIgnoreCase))
        {
            return "Ignored";
        }

        if (Codecs.Count > 0)
        {
            var family = CodecOf(releaseTitle, releaseCodec);

            if (family.Length == 0
                    ? !AllowUnstatedCodec
                    : !Codecs.Contains(family, StringComparer.OrdinalIgnoreCase))
            {
                return family.Length == 0 ? "Codec not stated" : $"{Label(family)} isn't a codec you want";
            }
        }

        if (AvoidWords.FirstOrDefault(word => HasWord(releaseTitle, word)) is { } avoided)
        {
            return $"Has \"{avoided}\"";
        }

        if (RequireWords.Count > 0 && !RequireWords.Any(word => HasWord(releaseTitle, word)))
        {
            return "Has none of your required words";
        }

        return null;
    }

    /// <summary>
    /// The codec family a release is, from what was read from its name or,
    /// failing that, from spellings like "H.265" and "AVC" in the name.
    /// </summary>
    public static string CodecOf(
        string releaseTitle,
        string? parsedCodec)
    {
        var family = CodecFamily(parsedCodec);

        if (family.Length > 0)
        {
            return family;
        }

        foreach (var (spelling, codec) in new[] { ("H 265", "HEVC"), ("H 264", "H264"), ("AVC", "H264") })
        {
            if (HasWord(releaseTitle, spelling))
            {
                return codec;
            }
        }

        return string.Empty;
    }

    public bool IsFileIgnored(string path) =>
        IgnoredFiles.Contains(path, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the release name has this word (or words) on its own, not as
    /// part of a longer one: "CAM" matches "Film.2020.CAM.x264" but not
    /// "Film.Camera.2020". Dots, dashes and the like count as spaces.
    /// </summary>
    public static bool HasWord(
        string releaseTitle,
        string word)
    {
        var words =
            Words(word);

        if (words.Length == 0)
        {
            return false;
        }

        var title =
            Words(releaseTitle);

        for (var start = 0; start + words.Length <= title.Length; start++)
        {
            if (words.Select((part, offset) => string.Equals(part, title[start + offset], StringComparison.OrdinalIgnoreCase)).All(same => same))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Tidies a word typed on the page; null when there's nothing in it.</summary>
    public static string? NormalizeWord(string? text)
    {
        var words = Words(text ?? string.Empty);

        return words.Length == 0 ? null : string.Join(' ', words);
    }

    private static string[] Words(string text) =>
        Regex.Split(text, @"[^\p{L}\p{N}]+")
            .Where(part => part.Length > 0)
            .ToArray();

    private static string Label(string family) =>
        CodecChoices.FirstOrDefault(choice => choice.Codec == family).Label ?? family;
}
