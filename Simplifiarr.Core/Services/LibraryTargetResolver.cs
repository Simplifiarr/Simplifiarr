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
                .Where(profile => profile.Enabled)
                .Select(profile => (
                    Profile: profile,
                    Root: profile.AllDownloadPaths()
                        .Where(root => PathContainment.IsPathUnderRoot(sourcePath, root))
                        .OrderByDescending(root => root.Length)
                        .FirstOrDefault()))
                .Where(match => match.Root is not null)
                .OrderByDescending(match => match.Root!.Length)
                .Select(match => match.Profile)
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

        // Sonarr or Radarr already has it: their folder, or nothing if
        // that folder isn't in this drive's library.
        if (!string.IsNullOrWhiteSpace(release.KnownFolder) &&
            !PathContainment.IsPathUnderRoot(release.KnownFolder, libraryRoot))
        {
            return new LibraryTarget
            {
                IsTv = release.IsTv,
                KeptElsewhere = release.KnownFolder
            };
        }

        var titleFolder =
            !string.IsNullOrWhiteSpace(release.KnownFolder)
                ? release.KnownFolder
                : Path.Combine(
                    libraryRoot,
                    release.FolderName ??
                        (release.IsTv || !release.Year.HasValue
                            ? release.NormalizedTitle
                            : $"{release.NormalizedTitle} ({release.Year.Value})"));

        var targetDirectory =
            release.IsTv && release.UsesSeasonFolders
                ? Path.Combine(titleFolder, SeasonFolder(titleFolder, release))
                : titleFolder;

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

    // With Sonarr's own format, always Sonarr's name: Sonarr puts the next
    // episode there whatever else is on disk, so anything else splits the
    // season in two ("Season 06" and "Season 6"). Otherwise the season
    // folder the show already has, whichever way it's written; then the
    // way the show's other seasons are written; then "Season 01".
    private static string SeasonFolder(
        string titleFolder,
        ParsedRelease release)
    {
        var season = release.Season ?? 1;

        if (release.SeasonFolderFromSonarr && !string.IsNullOrWhiteSpace(release.SeasonFolderName))
        {
            return release.SeasonFolderName;
        }

        var preferred =
            release.SeasonFolderName ?? $"Season {season:00}";

        if (!Directory.Exists(titleFolder))
        {
            return preferred;
        }

        foreach (var name in new[] { preferred, $"Season {season}", $"Season {season:00}" })
        {
            if (Directory.Exists(Path.Combine(titleFolder, name)))
            {
                return name;
            }
        }

        if (release.SeasonFolderName is null && season > 0)
        {
            var others =
                Directory.GetDirectories(titleFolder)
                    .Select(Path.GetFileName)
                    .Select(name => SeasonFolders.NumberOf(name ?? string.Empty))
                    .Where(found => found is not null && found.Value.Season > 0)
                    .ToList();

            // "Season 1", "Season 2": unpadded like them. Anything padded
            // or nothing to go by: "Season 01".
            if (others.Count > 0 && others.All(found => !found!.Value.Padded))
            {
                return $"Season {season}";
            }
        }

        return preferred;
    }
}

