using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public class JunkFileScannerService
    : IJunkFileScannerService
{
    // .nfo is deliberately absent: Jellyfin/Plex/Kodi read it for real
    // metadata (tvshow.nfo, movie.nfo, ...), so it's never treated as junk.
    private static readonly string[] JunkMetadataExtensions =
    [
        ".sfv", ".md5", ".torrent", ".url", ".txt", ".json", ".xml"
    ];

    private static readonly string[] ImageExtensions =
    [
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".ico", ".tbn"
    ];

    private static readonly string[] ArtworkNames =
    [
        "folder", "poster", "backdrop", "landscape", "banner", "logo",
        "clearlogo", "disc", "thumb", "background", "fanart", "cover"
    ];

    private readonly DriveCandidateCache<JunkFileCandidate> _cache =
        new(
            "junk-file-cache.json",
            candidate => candidate.FilePath,
            candidate =>
                !string.IsNullOrWhiteSpace(candidate.FilePath) &&
                File.Exists(candidate.FilePath));

    private readonly ISettingsService _settingsService;
    private readonly IFileInventoryService _fileInventory;

    public JunkFileScannerService(
        ISettingsService settingsService,
        IFileInventoryService fileInventory)
    {
        _settingsService = settingsService;
        _fileInventory = fileInventory;
    }

    // Scans from before .nfo was excluded may still sit in the cache - hide
    // those, except a .nfo that's only flagged for being inside a Sample
    // folder.
    public IReadOnlyList<JunkFileCandidate> GetCandidates(
        string driveName) =>
        _cache.GetCandidates(driveName)
            .Where(candidate =>
                !string.Equals(
                    Path.GetExtension(candidate.FileName),
                    ".nfo",
                    StringComparison.OrdinalIgnoreCase) ||
                candidate.Reason.StartsWith(
                    "Leftover file in a Sample folder",
                    StringComparison.Ordinal))
            .ToList();

    public DateTime? GetLastScanUtc(
        string driveName) =>
        _cache.GetLastScanUtc(driveName);

    public async Task<List<JunkFileCandidate>> ScanAsync(
        string driveName)
    {
        var settings =
            await _settingsService.GetSettingsAsync();

        var profile =
            settings.DriveProfiles.FirstOrDefault(profile =>
                profile.Enabled &&
                string.Equals(
                    profile.Name,
                    driveName,
                    StringComparison.OrdinalIgnoreCase));

        if (profile is null)
        {
            return [];
        }

        var libraryPaths =
            new[]
            {
                (Path: profile.MovieLibraryPath, IsBookLibrary: false),
                (Path: profile.TvLibraryPath, IsBookLibrary: false),
                (Path: profile.BookLibraryPath, IsBookLibrary: true),
                (Path: profile.AudiobookLibraryPath, IsBookLibrary: true),
                (Path: profile.MusicLibraryPath, IsBookLibrary: false),
                (Path: profile.ComicLibraryPath, IsBookLibrary: true)
            }
            .Where(entry =>
                !string.IsNullOrWhiteSpace(entry.Path) &&
                Directory.Exists(entry.Path))
            .DistinctBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase);

        var candidates =
            new List<JunkFileCandidate>();

        // The promise of this feature is that downloads are never touched,
        // because a torrent may still be seeding them. If a library folder
        // is set so wide that it contains a downloads folder, that promise
        // still has to hold - so anything inside one is skipped.
        var downloadFolders =
            new[]
            {
                profile.DownloadPath,
                profile.SeedOnlyPath,
                profile.AudiobookDownloadPath,
                profile.MusicDownloadPath,
                profile.ComicDownloadPath
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();

        var seenPaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (libraryPath, isBookLibrary) in libraryPaths)
        {
            var files =
                await _fileInventory.GetInventoryAsync(
                    libraryPath,
                    forceRefresh: true);

            foreach (var file in files)
            {
                if (downloadFolders.Any(folder =>
                        PathContainment.IsPathUnderRoot(file.FullPath, folder)))
                {
                    continue;
                }

                // One library folder inside another would list a file twice.
                if (!seenPaths.Add(file.FullPath))
                {
                    continue;
                }

                var reason =
                    Classify(file, isBookLibrary);

                if (reason is null)
                {
                    continue;
                }

                candidates.Add(
                    new JunkFileCandidate
                    {
                        FilePath = file.FullPath,
                        FileName = file.FileName,
                        SizeBytes = file.SizeBytes,
                        Reason = reason,
                        DriveProfileName = profile.Name
                    });
            }
        }

        _cache.SetCandidates(profile.Name, candidates);

        return candidates;
    }

    public void RemoveCandidate(
        string filePath) =>
        _cache.RemoveCandidate(filePath);

    private static string? Classify(
        FileRecord file,
        bool isBookLibrary)
    {
        if (file.FileType == FileType.Video &&
            SampleFileDetector.IsSample(file))
        {
            return "Sample video file";
        }

        // Thumbnails, .nfo and other files left behind in a release's
        // Sample folder describe the sample clip, not the real media - even
        // ones that would otherwise look like artwork (sample-thumb.jpg) or
        // be skipped (.nfo, .nfo-orig).
        if (SampleFileDetector.IsInSampleFolder(file))
        {
            return "Leftover file in a Sample folder";
        }

        if (file.FileType != FileType.Metadata)
        {
            return null;
        }

        var extension =
            Path.GetExtension(file.FileName).ToLowerInvariant();

        if (extension == ".metathumb")
        {
            return null;
        }

        // Book libraries (Calibre, Audiobookshelf, etc.) use .json/.xml
        // for their own library metadata (e.g. metadata_db_prefs_backup.json),
        // not scene-release junk - only flag the unambiguous ones there.
        var junkExtensions =
            isBookLibrary
                ? JunkMetadataExtensions.Where(ext => ext is not (".json" or ".xml"))
                : JunkMetadataExtensions;

        if (junkExtensions.Contains(extension))
        {
            return "Release metadata/checksum file";
        }

        if (ImageExtensions.Contains(extension))
        {
            var nameWithoutExtension =
                Path.GetFileNameWithoutExtension(file.FileName);

            var isKnownArtwork =
                ArtworkNames.Any(name =>
                    string.Equals(
                        name,
                        nameWithoutExtension,
                        StringComparison.OrdinalIgnoreCase) ||
                    nameWithoutExtension.EndsWith(
                        $"-{name}",
                        StringComparison.OrdinalIgnoreCase));

            return isKnownArtwork
                ? null
                : "Unrecognized image (not standard artwork naming)";
        }

        return null;
    }
}
