using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Reads Series/Number/Title/Writer/Year from a CBZ's ComicInfo.xml - the
/// ComicRack/Kavita/Komga standard sidecar stored inside the zip itself.
/// Unlike EPUB's OPF, ComicInfo.xml is flat and unnamespaced, so no
/// container.xml indirection or XNamespace lookups are needed.
/// </summary>
public static class CbzComicInfoReader
{
    public static ComicMetadata? TryExtract(
        string filePath)
    {
        try
        {
            using var archive =
                ZipFile.OpenRead(filePath);

            var entry =
                archive.Entries.FirstOrDefault(entry =>
                    string.Equals(
                        entry.FullName,
                        "ComicInfo.xml",
                        StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                return null;
            }

            using var stream =
                entry.Open();

            var xml =
                XDocument.Load(stream);

            var series =
                xml.Descendants("Series")
                    .Select(element => element.Value)
                    .FirstOrDefault(value =>
                        !string.IsNullOrWhiteSpace(value));

            var title =
                xml.Descendants("Title")
                    .Select(element => element.Value)
                    .FirstOrDefault(value =>
                        !string.IsNullOrWhiteSpace(value));

            if (string.IsNullOrWhiteSpace(series) &&
                string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var writer =
                xml.Descendants("Writer")
                    .Select(element => element.Value)
                    .FirstOrDefault(value =>
                        !string.IsNullOrWhiteSpace(value));

            var issue =
                ParseIssue(
                    xml.Descendants("Number")
                        .Select(element => element.Value)
                        .FirstOrDefault(value =>
                            !string.IsNullOrWhiteSpace(value)));

            var year =
                ParseYear(
                    xml.Descendants("Year")
                        .Select(element => element.Value)
                        .FirstOrDefault(value =>
                            !string.IsNullOrWhiteSpace(value)));

            return new ComicMetadata
            {
                Title =
                    string.IsNullOrWhiteSpace(title)
                        ? series!.Trim()
                        : title.Trim(),
                Series =
                    string.IsNullOrWhiteSpace(series)
                        ? null
                        : series.Trim(),
                Issue = issue,
                Author =
                    string.IsNullOrWhiteSpace(writer)
                        ? null
                        : writer.Trim(),
                Year = year,
                Source = ComicMetadataSource.Embedded
            };
        }
        catch
        {
            return null;
        }
    }

    private static double? ParseIssue(
        string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return double.TryParse(
            raw.Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var issue)
                ? issue
                : null;
    }

    private static int? ParseYear(
        string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return int.TryParse(raw.Trim(), out var year)
            ? year
            : null;
    }
}
