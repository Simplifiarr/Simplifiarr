using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class AudiobookLibraryTargetResolver
    : IAudiobookLibraryTargetResolver
{
    public LibraryTarget Resolve(
        AudiobookMetadata metadata,
        string format,
        string audiobookLibraryPath,
        string? originalFileName = null,
        bool preserveOriginalFileName = false)
    {
        if (string.IsNullOrWhiteSpace(audiobookLibraryPath))
        {
            return new LibraryTarget();
        }

        var safeTitle =
            SanitizeForPath(metadata.Title);

        var extension =
            format.StartsWith('.')
                ? format
                : $".{format}";

        var targetDirectory =
            Path.Combine(
                audiobookLibraryPath,
                safeTitle);

        var targetFileName =
            preserveOriginalFileName && !string.IsNullOrWhiteSpace(originalFileName)
                ? originalFileName
                : $"{safeTitle}{extension}";

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
