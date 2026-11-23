using System.Text.RegularExpressions;

namespace Simplifiarr.Core.Utilities;

/// <summary>
/// Takes private details out of log text before it is written or shared.
/// Always: API keys, passwords and tokens (both the values Simplifiarr
/// knows and anything that looks like one), IP addresses, email addresses
/// and the Windows user name. With names hidden as well: titles, people's
/// names and media file names.
/// </summary>
public sealed partial class LogRedactor
{
    public const string Hidden = "[hidden]";

    private readonly List<string> _secrets;
    private readonly List<(string Name, string Replacement)> _names;
    private readonly string? _userName;

    // All the names in one pattern, whole words only, so a show called
    // "Rent" doesn't turn "Torrent" into "Tor[title]".
    private readonly Regex? _namePattern;
    private readonly Dictionary<string, string> _replacements = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _hideNames;

    public LogRedactor(
        IEnumerable<string?> secrets,
        string? userName = null,
        IEnumerable<string>? titles = null,
        IEnumerable<string>? people = null)
    {
        // Longest first, so a key that contains another is replaced whole.
        _secrets =
            secrets
                .Where(secret => !string.IsNullOrWhiteSpace(secret) && secret.Trim().Length >= 6)
                .Select(secret => secret!.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(secret => secret.Length)
                .ToList();

        _userName = string.IsNullOrWhiteSpace(userName) || userName.Length < 3 ? null : userName;

        _hideNames = titles is not null || people is not null;

        _names =
            (titles ?? [])
                .SelectMany(TitleVariants)
                .Select(title => (Name: title, Replacement: "[title]"))
                .Concat((people ?? []).Select(person => (Name: person, Replacement: "[person]")))
                .Where(entry => entry.Name.Trim().Length >= 3)
                .DistinctBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(entry => entry.Name.Length)
                .ToList();

        foreach (var (name, replacement) in _names)
        {
            _replacements[name] = replacement;
        }

        if (_names.Count > 0)
        {
            _namePattern =
                new Regex(
                    @"(?<![\w])(?:" + string.Join("|", _names.Select(entry => Regex.Escape(entry.Name))) + @")(?![\w])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }

    /// <summary>Cleans a piece of text.</summary>
    public string Clean(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        foreach (var secret in _secrets)
        {
            text = text.Replace(secret, Hidden, StringComparison.Ordinal);
        }

        text = SecretSetting().Replace(text, match => match.Groups["name"].Value + match.Groups["sep"].Value + Hidden);
        text = ApiKeyLike().Replace(text, Hidden);
        text = Email().Replace(text, "[email]");
        text = IpAddress().Replace(text, match => IsLocal(match.Value) ? match.Value : "[ip]");
        text = UserFolder().Replace(text, match => match.Groups["start"].Value + "[user]");

        if (_userName is not null)
        {
            text = Regex.Replace(text, $@"\b{Regex.Escape(_userName)}\b", "[user]", RegexOptions.IgnoreCase);
        }

        if (_hideNames)
        {
            if (_namePattern is not null)
            {
                text = _namePattern.Replace(text, match => _replacements[match.Value]);
            }

            text = MediaFileName().Replace(text, match => "[file]" + match.Groups["ext"].Value);
            text = TorrentHash().Replace(text, "[hash]");
        }

        return text;
    }

    /// <summary>"http://host:7878/api/v3/movie?apikey=..." becomes "http://host:7878/api/v3/movie?apikey=…".</summary>
    public static string Address(Uri? uri)
    {
        if (uri is null)
        {
            return "(no address)";
        }

        var address = uri.GetLeftPart(UriPartial.Path);

        if (string.IsNullOrEmpty(uri.Query) || uri.Query == "?")
        {
            return address;
        }

        var names =
            uri.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2)[0] + "=…");

        return address + "?" + string.Join("&", names);
    }

    // "Night Shift (2021)" is also written "Night Shift" and "Night.Shift.2021".
    private static IEnumerable<string> TitleVariants(string title)
    {
        title = title.Trim();

        yield return title;

        var withoutYear = YearSuffix().Replace(title, string.Empty).Trim();

        if (withoutYear.Length >= 4 && withoutYear != title)
        {
            yield return withoutYear;
        }

        var dotted = Regex.Replace(Regex.Replace(title, @"[()\[\]]", string.Empty).Trim(), @"\s+", ".");

        if (dotted != title)
        {
            yield return dotted;
        }
    }

    private static bool IsLocal(string address) =>
        address is "127.0.0.1" or "0.0.0.0";

    // apikey=..., "ApiKey": "...", X-Api-Key: ..., Token="...", password=...
    [GeneratedRegex(@"(?<name>(?:x-)?api[_-]?key|apikey|access[_-]?token|token|password|passwd|pwd|secret)(?<sep>[""']?\s*[:=]\s*[""']?)[^\s""'&,;}]+", RegexOptions.IgnoreCase)]
    private static partial Regex SecretSetting();

    // Radarr, Sonarr, Prowlarr and Jellyfin keys are 32 hex characters.
    [GeneratedRegex(@"\b[0-9a-fA-F]{32}\b")]
    private static partial Regex ApiKeyLike();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)(?![\d.])")]
    private static partial Regex IpAddress();

    [GeneratedRegex(@"(?<start>[\\/](?:Users|home)[\\/])[^\\/\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UserFolder();

    [GeneratedRegex(@"[\w.\-()\[\]]+(?<ext>\.(?:mkv|mp4|m4v|avi|ts|m2ts|mov|wmv|webm|mpg|mpeg|srt|ass|ssa|sub|idx|vtt|nfo|torrent|cbr|cbz|cb7|epub|mobi|azw3|pdf|m4b|mp3|flac|m4a|ogg|opus))\b", RegexOptions.IgnoreCase)]
    private static partial Regex MediaFileName();

    [GeneratedRegex(@"\b[0-9a-fA-F]{40}\b")]
    private static partial Regex TorrentHash();

    [GeneratedRegex(@"\s*\(\d{4}\)\s*$")]
    private static partial Regex YearSuffix();
}
