using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public class LibraryTargetResolver
    : ILibraryTargetResolver
{
    private readonly ISettingsService _settingsService;
    private readonly IFileScannerService _fileScanner;

    public LibraryTargetResolver(
        ISettingsService settingsService,
        IFileScannerService fileScanner)
    {
        _settingsService = settingsService;
        _fileScanner = fileScanner;
    }

    public async Task<LibraryTarget> ResolveAsync(
        ParsedRelease release,
        string sourcePath)
    {
        var settings =
            await _settingsService.GetSettingsAsync();

        var sourceRoot =
            Path.GetPathRoot(sourcePath);

        if (string.IsNullOrWhiteSpace(sourceRoot))
        {
            return new LibraryTarget();
        }

        // A download belongs to the drive entry whose downloads folder it
        // is in, and goes to that entry's library. Only if no entry claims
        // it (it was found some other way) fall back to "the first entry on
        // the same drive letter" - which on its own would send a file to
        // the wrong library whenever two entries share a drive.
        var profile =
            settings.DriveProfiles
                .Where(profile =>
                    profile.Enabled &&
                    PathContainment.IsPathUnderRoot(sourcePath, profile.DownloadPath))
                .OrderByDescending(profile => profile.DownloadPath.Length)
                .FirstOrDefault()
            ??
            settings.DriveProfiles
                .FirstOrDefault(profile =>
                    profile.Enabled &&
                    !string.IsNullOrWhiteSpace(profile.DriveRoot) &&
                    string.Equals(
                        Path.GetPathRoot(profile.DriveRoot),
                        sourceRoot,
                        StringComparison.OrdinalIgnoreCase));

        if (profile is null)
        {
            return new LibraryTarget();
        }

        var libraryRoot =
            release.IsTv
                ? profile.TvLibraryPath
                : profile.MovieLibraryPath;

        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            return new LibraryTarget
            {
                IsTv = release.IsTv
            };
        }

        var targetDirectory =
            release.IsTv
                ? Path.Combine(
                    libraryRoot,
                    release.NormalizedTitle,
                    $"Season {release.Season ?? 1:00}")
                : Path.Combine(
                    libraryRoot,
                    release.Year.HasValue
                        ? $"{release.NormalizedTitle} ({release.Year.Value})"
                        : release.NormalizedTitle);

        var sourceExtension =
            Path.GetExtension(sourcePath);

        var targetFileName =
            profile.PreserveOriginalFileName
                ? Path.GetFileName(sourcePath)
                : release.IsTv
                    ? $"{release.NormalizedTitle} - S{release.Season ?? 1:00}E{release.Episode ?? 1:00}{sourceExtension}"
                    : release.Year.HasValue
                        ? $"{release.NormalizedTitle} ({release.Year.Value}){sourceExtension}"
                        : $"{release.NormalizedTitle}{sourceExtension}";

        var targetFilePath =
            Path.Combine(
                targetDirectory,
                targetFileName);

        return new LibraryTarget
        {
            IsTv = release.IsTv,
            TargetDirectory = targetDirectory,
            TargetFilePath = targetFilePath,
            DirectoryExists = Directory.Exists(targetDirectory),
            FileExists = File.Exists(targetFilePath)
        };
    }
}

