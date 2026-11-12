using System.Text;
using System.Text.RegularExpressions;

namespace Simplifiarr.Core.Utilities;

/// <summary>
/// Builds folder names from Sonarr's and Radarr's folder formats (Settings,
/// Media Management), so a folder Simplifiarr creates is named exactly as
/// they would name it. Covers the tokens folder formats use; anything else
/// is left out rather than guessed.
/// </summary>
public static partial class ArrFolderNamer
{
    public static string Series(
        string format,
        string title,
        int? year,
        int? tvdbId = null,
        string? imdbId = null) =>
        Render(format, name => name switch
        {
            "series title" => title,
            "series titleyear" => WithYear(title, year),
            "series titlewithoutyear" => WithoutYear(title, year),
            "series cleantitle" => Clean(title),
            "series cleantitleyear" => WithYear(Clean(title), year),
            "series cleantitlewithoutyear" => Clean(WithoutYear(title, year)),
            "series titlethe" => TheLast(title),
            "series titletheyear" => WithYear(TheLast(title), year),
            "series titlefirstcharacter" => FirstCharacter(title),
            "series year" => year?.ToString(),
            "tvdbid" or "tvdb id" => tvdbId?.ToString(),
            "imdbid" or "imdb id" => imdbId,
            _ => null
        });

    public static string Season(
        string format,
        int season) =>
        Render(format, (name, suffix) => name == "season"
            ? season.ToString(suffix is { Length: > 0 } && suffix.All(character => character == '0') ? new string('0', suffix.Length) : "0")
            : null);

    public static string Movie(
        string format,
        string title,
        int? year,
        int? tmdbId = null,
        string? imdbId = null) =>
        Render(format, name => name switch
        {
            "movie title" or "movie originaltitle" => title,
            "movie titleyear" => WithYear(title, year),
            "movie cleantitle" => Clean(title),
            "movie cleantitleyear" => WithYear(Clean(title), year),
            "movie titlethe" => TheLast(title),
            "movie titlefirstcharacter" => FirstCharacter(title),
            "release year" or "movie year" => year?.ToString(),
            "tmdbid" or "tmdb id" => tmdbId?.ToString(),
            "imdbid" or "imdb id" => imdbId,
            _ => null
        });

    private static string Render(
        string format,
        Func<string, string?> token) =>
        Render(format, (name, _) => token(name));

    // {Series Title}, {Series.Title} (dots between words), {season:00}.
    private static string Render(
        string format,
        Func<string, string?, string?> token)
    {
        var rendered =
            TokenPattern().Replace(format, match =>
            {
                var raw = match.Groups["name"].Value;
                var suffix = match.Groups["suffix"].Success ? match.Groups["suffix"].Value : null;

                // {Series.Title} means "join the words with dots".
                var separator = raw.Contains('.') ? '.' : raw.Contains('_') ? '_' : ' ';
                var name = Regex.Replace(raw.Replace('.', ' ').Replace('_', ' ').Replace('-', ' ').Trim(), @"\s+", " ").ToLowerInvariant();

                var value = token(name, suffix) ?? string.Empty;

                return separator == ' ' ? value : value.Replace(' ', separator);
            });

        return Tidy(rendered);
    }

    private static string WithYear(string title, int? year) =>
        year is null || EndsWithYear(title, year.Value)
            ? title
            : $"{title} ({year})";

    private static string WithoutYear(string title, int? year) =>
        year is null
            ? title
            : Regex.Replace(title, $@"\s*\(?{year}\)?$", string.Empty).Trim();

    // A title that already ends in its year ("Show 2019", "Show (2019)")
    // doesn't get it twice.
    private static bool EndsWithYear(string title, int year) =>
        Regex.IsMatch(title, $@"\(?{year}\)?\s*$");

    private static string TheLast(string title) =>
        Regex.Match(title, @"^(The|A|An)\s+(.+)$", RegexOptions.IgnoreCase) is { Success: true } match
            ? $"{match.Groups[2].Value}, {match.Groups[1].Value}"
            : title;

    private static string Clean(string title)
    {
        var text = title.Replace("&", " and ", StringComparison.Ordinal);

        text = Regex.Replace(text, @"['’`]", string.Empty);
        text = Regex.Replace(text, @"[:;,.!?""]", " ");

        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static string FirstCharacter(string title)
    {
        var first = TheLast(title).TrimStart().FirstOrDefault();

        return char.IsLetter(first) ? char.ToUpperInvariant(first).ToString() : "#";
    }

    // What Windows won't allow, replaced the way Sonarr and Radarr's
    // default "smart" colon replacement does, then empty brackets left by
    // tokens with no value removed.
    private static string Tidy(string name)
    {
        var text = name.Replace(": ", " - ", StringComparison.Ordinal).Replace(':', '-');

        var builder = new StringBuilder(text.Length);

        foreach (var character in text)
        {
            if (Path.GetInvalidFileNameChars().Contains(character))
            {
                continue;
            }

            builder.Append(character);
        }

        text = builder.ToString();

        // "[tvdbid-]", "()", "{}" and the like.
        text = Regex.Replace(text, @"[\[\(\{][^\[\]\(\)\{\}]*?-?\s*[\]\)\}]", match =>
            Regex.IsMatch(match.Value, @"^[\[\(\{]\s*([A-Za-z]+-)?\s*[\]\)\}]$") ? string.Empty : match.Value);

        text = Regex.Replace(text, @"\s+", " ").Trim();

        // Windows drops trailing dots and spaces from folder names.
        return text.TrimEnd('.', ' ');
    }

    [GeneratedRegex(@"\{(?<name>[^{}:]+)(:(?<suffix>[^{}]*))?\}")]
    private static partial Regex TokenPattern();
}
