using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class MusicLibraryTargetResolver
    : IMusicLibraryTargetResolver
{
    public LibraryTarget Resolve(
        MusicMetadata metadata,
        string format,
        string musicLibraryPath,
        string? originalFileName = null,
        bool preserveOriginalFileName = false)
    {
        if (string.IsNullOrWhiteSpace(musicLibraryPath))
        {
            return new LibraryTarget();
        }

        var safeTitle =
            SanitizeForPath(metadata.Title);

        var extension =
            format.StartsWith('.')
                ? format
                : $".{format}";

        var hasArtist = !string.IsNullOrWhiteSpace(metadata.Artist);
        var hasAlbum = !string.IsNullOrWhiteSpace(metadata.Album);

        var targetDirectory = musicLibraryPath;

        if (hasArtist)
        {
            targetDirectory =
                Path.Combine(
                    targetDirectory,
                    SanitizeForPath(metadata.Artist!));
        }

        if (hasAlbum)
        {
            targetDirectory =
                Path.Combine(
                    targetDirectory,
                    SanitizeForPath(metadata.Album!));
        }

        var trackPrefix =
            metadata.TrackNumber is > 0
                ? $"{metadata.TrackNumber:00} - "
                : string.Empty;

        var targetFileName =
            preserveOriginalFileName && !string.IsNullOrWhiteSpace(originalFileName)
                ? originalFileName
                : $"{trackPrefix}{safeTitle}{extension}";

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
