using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class ComicLibraryTargetResolver
    : IComicLibraryTargetResolver
{
    public LibraryTarget Resolve(
        ComicMetadata metadata,
        string format,
        string comicLibraryPath,
        string? originalFileName = null,
        bool preserveOriginalFileName = false)
    {
        if (string.IsNullOrWhiteSpace(comicLibraryPath))
        {
            return new LibraryTarget();
        }

        var extension =
            format.StartsWith('.')
                ? format
                : $".{format}";

        var hasSeries = !string.IsNullOrWhiteSpace(metadata.Series);

        var safeSeries =
            hasSeries
                ? SanitizeForPath(metadata.Series!)
                : null;

        var safeTitle =
            SanitizeForPath(metadata.Title);

        var targetDirectory =
            hasSeries
                ? Path.Combine(comicLibraryPath, safeSeries!)
                : comicLibraryPath;

        var defaultFileName =
            hasSeries && metadata.Issue.HasValue
                ? $"{safeSeries} #{FormatIssue(metadata.Issue.Value)}{extension}"
                : $"{safeTitle}{extension}";

        var targetFileName =
            preserveOriginalFileName && !string.IsNullOrWhiteSpace(originalFileName)
                ? originalFileName
                : defaultFileName;

        var targetFilePath =
            Path.Combine(
                targetDirectory,
                targetFileName);

        return new LibraryTarget
        {
            TargetDirectory = targetDirectory,
            TargetFilePath = targetFilePath,
            DirectoryExists = Directory.Exists(targetDirectory),
            FileExists = File.Exists(targetFilePath)
        };
    }

    /// <summary>
    /// Zero-pads a comic issue number to 3 digits, keeping fractional
    /// annuals (e.g. "#0.5") readable - "1" -> "001", "0.5" -> "000.5",
    /// "12.1" -> "012.1". Whole-number issues never show a decimal point.
    /// </summary>
    private static string FormatIssue(
        double issue)
    {
        return issue == Math.Floor(issue)
            ? issue.ToString("000")
            : issue.ToString("000.0#");
    }

    private static string SanitizeForPath(
        string value)
    {
        var invalidChars =
            Path.GetInvalidFileNameChars();

        var sanitized =
            new string(
                value
                    .Select(ch => invalidChars.Contains(ch) ? ' ' : ch)
                    .ToArray());

        sanitized =
            string.Join(
                " ",
                sanitized.Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries));

        return string.IsNullOrWhiteSpace(sanitized)
            ? "Unknown"
            : sanitized;
    }
}
