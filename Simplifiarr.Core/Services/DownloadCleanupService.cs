using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public class DownloadCleanupService
    : IDownloadCleanupService
{
    // Modified times round-trip through JSON and file systems with slightly
    // different precision; anything within this is "unchanged".
    private static readonly TimeSpan ModifiedTolerance =
        TimeSpan.FromSeconds(2);

    private readonly DriveCandidateCache<DownloadCleanupCandidate> _cache =
        new(
            "download-cleanup-cache.json",
            candidate => candidate.DownloadPath,
            candidate =>
                !string.IsNullOrWhiteSpace(candidate.DownloadPath) &&
                File.Exists(candidate.DownloadPath));

    private readonly ISettingsService _settingsService;
    private readonly IFileInventoryService _fileInventory;
    private readonly IQbittorrentScanner _qbittorrentScanner;
    private readonly IHardlinkService _hardlinkService;
    private readonly IHistoryService _historyService;

    public DownloadCleanupService(
        ISettingsService settingsService,
        IFileInventoryService fileInventory,
        IQbittorrentScanner qbittorrentScanner,
        IHardlinkService hardlinkService,
        IHistoryService historyService)
    {
        _settingsService = settingsService;
        _fileInventory = fileInventory;
        _qbittorrentScanner = qbittorrentScanner;
        _hardlinkService = hardlinkService;
        _historyService = historyService;
    }

    public IReadOnlyList<DownloadCleanupCandidate> GetCandidates(
        string driveName) =>
        _cache.GetCandidates(driveName);

    public DateTime? GetLastScanUtc(
        string driveName) =>
        _cache.GetLastScanUtc(driveName);

    public async Task<DownloadCleanupScanResult> ScanAsync(
        string driveName,
        CancellationToken cancellationToken = default)
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
            return new DownloadCleanupScanResult
            {
                Error = $"Drive profile \"{driveName}\" is not enabled."
            };
        }

        var downloadRoots =
            ExistingFolders(
                profile.DownloadPath,
                profile.AudiobookDownloadPath,
                profile.MusicDownloadPath,
                profile.ComicDownloadPath);

        var libraryRoots =
            ExistingFolders(
                profile.MovieLibraryPath,
                profile.TvLibraryPath,
                profile.BookLibraryPath,
                profile.AudiobookLibraryPath,
                profile.MusicLibraryPath,
                profile.ComicLibraryPath);

        if (downloadRoots.Count == 0 || libraryRoots.Count == 0)
        {
            return new DownloadCleanupScanResult
            {
                Error =
                    "This drive needs at least one existing download folder and one existing library folder (see Settings)."
            };
        }

        // Without qBittorrent's torrent list every download would look
        // inactive - so an unreachable client stops the scan outright.
        var torrents =
            await _qbittorrentScanner.TryGetTorrentsAsync();

        if (torrents is null)
        {
            return new DownloadCleanupScanResult
            {
                Error =
                    "Couldn't reach qBittorrent (check its URL in Settings). Nothing was scanned: without its torrent list, every download would look inactive."
            };
        }

        var active =
            new ActivePathSet(torrents);

        var libraryFilesBySize =
            new Dictionary<long, List<FileRecord>>();

        foreach (var root in libraryRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var files =
                await _fileInventory.GetInventoryAsync(
                    root,
                    forceRefresh: true);

            foreach (var file in files.Where(file => file.SizeBytes > 0))
            {
                if (!libraryFilesBySize.TryGetValue(file.SizeBytes, out var bucket))
                {
                    bucket = [];
                    libraryFilesBySize[file.SizeBytes] = bucket;
                }

                bucket.Add(file);
            }
        }

        var result =
            new DownloadCleanupScanResult
            {
                TorrentCount = torrents.Count
            };

        var seenDownloadPaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in downloadRoots)
        {
            var downloadFiles =
                await _fileInventory.GetInventoryAsync(
                    root,
                    forceRefresh: true);

            foreach (var file in downloadFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (file.SizeBytes <= 0 ||
                    !seenDownloadPaths.Add(file.FullPath))
                {
                    continue;
                }

                // A download folder nested inside (or equal to) a library
                // folder isn't a "download copy" of anything.
                if (libraryRoots.Any(libraryRoot =>
                        PathContainment.IsPathUnderRoot(file.FullPath, libraryRoot)))
                {
                    continue;
                }

                result.DownloadFilesChecked++;

                if (!libraryFilesBySize.TryGetValue(file.SizeBytes, out var libraryMatches))
                {
                    continue;
                }

                if (active.Contains(file.FullPath))
                {
                    result.SkippedStillInQbittorrent++;
                    continue;
                }

                var match =
                    await FindLibraryMatchAsync(
                        file,
                        libraryMatches,
                        cancellationToken);

                if (match is null)
                {
                    continue;
                }

                result.Candidates.Add(
                    new DownloadCleanupCandidate
                    {
                        DownloadPath = file.FullPath,
                        FileName = file.FileName,
                        SizeBytes = file.SizeBytes,
                        LibraryPath = match.Value.Library.FullPath,
                        MatchKind = match.Value.Kind,
                        DriveProfileName = profile.Name,
                        DownloadModifiedUtc = file.LastModifiedUtc,
                        LibraryModifiedUtc = match.Value.Library.LastModifiedUtc
                    });
            }
        }

        result.Candidates =
            result.Candidates
                .OrderByDescending(candidate => candidate.SizeBytes)
                .ThenBy(candidate => candidate.DownloadPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

        _cache.SetCandidates(profile.Name, result.Candidates);

        return result;
    }

    public async Task<DownloadCleanupRemoveResult> RemoveAsync(
        IReadOnlyCollection<DownloadCleanupCandidate> candidates)
    {
        var result =
            new DownloadCleanupRemoveResult();

        var torrents =
            await _qbittorrentScanner.TryGetTorrentsAsync();

        if (torrents is null)
        {
            result.Error =
                "Couldn't reach qBittorrent, so nothing was removed: Simplifiarr won't delete downloads without confirming qBittorrent no longer has them.";

            return result;
        }

        var active =
            new ActivePathSet(torrents);

        foreach (var candidate in candidates)
        {
            var problem =
                await FindProblemAsync(candidate, active);

            if (problem is not null)
            {
                result.Skipped++;
                result.SkipReasons.Add($"{candidate.FileName}: {problem}");
                continue;
            }

            // Deleted directly, NOT quarantined: Quarantine lives on one
            // drive, and moving a hardlink across drives silently turns it
            // into a full copy (and deleting a hardlinked name frees nothing
            // anyway). Every candidate has already been proven to exist in
            // the library, so nothing is lost.
            var deleteError =
                TryDeleteFile(candidate.DownloadPath);

            var success =
                deleteError is null;

            await _historyService.AddRecordAsync(
                new HistoryRecord
                {
                    ActionType = HistoryActionType.DownloadCopyRemoved,
                    FileName = candidate.FileName,
                    LibraryPath = candidate.LibraryPath,
                    DownloadPath = candidate.DownloadPath,
                    QuarantinePath = string.Empty,
                    SizeBytes = candidate.SizeBytes,
                    Success = success,
                    Message =
                        success
                            ? $"Download copy deleted - qBittorrent no longer has it and the library has {DescribeMatch(candidate.MatchKind)}."
                            : $"Failed to delete the download copy: {deleteError}"
                });

            if (success)
            {
                result.Removed++;
                _cache.RemoveCandidate(candidate.DownloadPath);
            }
            else
            {
                result.Skipped++;
                result.SkipReasons.Add($"{candidate.FileName}: couldn't be deleted ({deleteError}).");
            }
        }

        return result;
    }

    /// <summary>Returns null on success, otherwise why it failed.</summary>
    private static string? TryDeleteFile(
        string path)
    {
        try
        {
            var attributes =
                File.GetAttributes(path);

            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }

            File.Delete(path);

            return null;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Everything that made this a safe candidate has to still be true right
    /// now. Returns why not, or null when it is.
    /// </summary>
    private async Task<string?> FindProblemAsync(
        DownloadCleanupCandidate candidate,
        ActivePathSet active)
    {
        if (active.Contains(candidate.DownloadPath))
        {
            return "qBittorrent has this file again.";
        }

        var download = new FileInfo(candidate.DownloadPath);

        if (!download.Exists)
        {
            return "the download file is already gone.";
        }

        if (download.Length != candidate.SizeBytes ||
            !SameModifiedTime(download.LastWriteTimeUtc, candidate.DownloadModifiedUtc))
        {
            return "the download file changed since the scan - rescan first.";
        }

        var library = new FileInfo(candidate.LibraryPath);

        if (!library.Exists)
        {
            return "the library copy is missing - the download copy may be the only one.";
        }

        if (candidate.MatchKind == DownloadMatchKind.Hardlink)
        {
            // Still the same file on disk?
            return await _hardlinkService.AreHardlinkedAsync(
                       candidate.DownloadPath,
                       candidate.LibraryPath)
                ? null
                : "the library file is no longer linked to this download.";
        }

        if (library.Length != candidate.SizeBytes ||
            !SameModifiedTime(library.LastWriteTimeUtc, candidate.LibraryModifiedUtc))
        {
            return "the library copy changed since the scan - rescan first.";
        }

        return null;
    }

    private async Task<(FileRecord Library, DownloadMatchKind Kind)?> FindLibraryMatchAsync(
        FileRecord download,
        List<FileRecord> libraryCandidates,
        CancellationToken cancellationToken)
    {
        foreach (var library in libraryCandidates)
        {
            if (string.Equals(
                    library.FullPath,
                    download.FullPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (await _hardlinkService.AreHardlinkedAsync(
                    download.FullPath,
                    library.FullPath))
            {
                return (library, DownloadMatchKind.Hardlink);
            }

            if (await FileContentComparer.AreIdenticalAsync(
                    download.FullPath,
                    library.FullPath,
                    cancellationToken))
            {
                return (library, DownloadMatchKind.IdenticalCopy);
            }
        }

        return null;
    }

    private static bool SameModifiedTime(
        DateTime actualUtc,
        DateTime recordedUtc) =>
        (actualUtc - recordedUtc).Duration() <= ModifiedTolerance;

    private static string DescribeMatch(
        DownloadMatchKind kind) =>
        kind == DownloadMatchKind.Hardlink
            ? "the same file (hardlinked)"
            : "an identical copy (verified byte for byte)";

    private static List<string> ExistingFolders(
        params string[] paths) =>
        paths
            .Where(path =>
                !string.IsNullOrWhiteSpace(path) &&
                Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Every path a qBittorrent torrent occupies on disk. A file counts as
    /// "still in qBittorrent" if it IS a torrent's content path or sits
    /// inside one - whatever state that torrent is in (seeding, paused,
    /// errored, checking...). Removing any of those would break the torrent.
    /// </summary>
    internal sealed class ActivePathSet
    {
        private readonly List<string> _roots = [];

        public ActivePathSet(
            IEnumerable<TorrentRecord> torrents)
        {
            foreach (var torrent in torrents)
            {
                var path =
                    !string.IsNullOrWhiteSpace(torrent.ContentPath)
                        ? torrent.ContentPath
                        : !string.IsNullOrWhiteSpace(torrent.SavePath) &&
                          !string.IsNullOrWhiteSpace(torrent.Name)
                            ? Path.Combine(torrent.SavePath, torrent.Name)
                            : string.Empty;

                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                try
                {
                    _roots.Add(Normalize(path));
                }
                catch (ArgumentException)
                {
                    // Unparseable path - can't be one of our files.
                }
            }
        }

        public bool Contains(
            string filePath)
        {
            string normalized;

            try
            {
                normalized = Normalize(filePath);
            }
            catch (ArgumentException)
            {
                // Can't tell - treat as in use rather than risk it.
                return true;
            }

            foreach (var root in _roots)
            {
                if (normalized.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                    normalized.StartsWith(
                        root + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string Normalize(
            string path) =>
            Path.GetFullPath(path)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
    }
}
