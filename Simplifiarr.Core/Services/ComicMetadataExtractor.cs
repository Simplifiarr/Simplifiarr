using System.Globalization;
using System.Text.RegularExpressions;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class ComicMetadataExtractor
    : IComicMetadataExtractor
{
    public ComicMetadata Extract(
        string filePath)
    {
        var extension =
            Path.GetExtension(filePath)
                .ToLowerInvariant();

        // Only CBZ (a zip) is first-party readable - CBR/CB7/CBT/CBA have
        // no BCL archive support and reading them would require a
        // third-party package, so they go straight to filename parsing.
        var embedded =
            extension == ".cbz"
                ? CbzComicInfoReader.TryExtract(filePath)
                : null;

        return embedded ?? ExtractFromFilename(filePath);
    }

    private static ComicMetadata ExtractFromFilename(
        string filePath)
    {
        var name =
            Path.GetFileNameWithoutExtension(filePath);

        var cleaned =
            Regex.Replace(name, @"[._]+", " ")
                .Trim();

        cleaned =
            Regex.Replace(cleaned, @"\s+", " ");

        var year =
            ParseYear(cleaned);

        var issueMatch =
            Regex.Match(cleaned, @"#\s*(?<issue>\d+(?:\.\d+)?)");

        var cutMatch =
            issueMatch.Success
                ? issueMatch
                : Regex.Match(
                    cleaned,
                    @"\bVol(?:ume)?\.?\s*(?<issue>\d+)\b",
                    RegexOptions.IgnoreCase);

        if (!cutMatch.Success)
        {
            return new ComicMetadata
            {
                Title = cleaned,
                Series = null,
                Issue = null,
                Author = null,
                Year = year,
                Source = ComicMetadataSource.Filename
            };
        }

        var series =
            cleaned[..cutMatch.Index]
                .TrimEnd('-', '#', ' ')
                .Trim();

        var issue =
            double.TryParse(
                cutMatch.Groups["issue"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsedIssue)
                    ? parsedIssue
                    : (double?)null;

        return new ComicMetadata
        {
            Title =
                string.IsNullOrWhiteSpace(series)
                    ? cleaned
                    : series,
            Series =
                string.IsNullOrWhiteSpace(series)
                    ? null
                    : series,
            Issue = issue,
            Author = null,
            Year = year,
            Source = ComicMetadataSource.Filename
        };
    }

    private static int? ParseYear(
        string cleaned)
    {
        var yearMatch =
            Regex.Match(cleaned, @"\((19\d{2}|20\d{2})\)");

        return yearMatch.Success
            ? int.Parse(yearMatch.Groups[1].Value)
            : null;
    }
}
