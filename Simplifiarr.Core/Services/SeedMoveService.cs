using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

/// <inheritdoc cref="ISeedMoveService"/>
public class SeedMoveService : ISeedMoveService
{
    // Space left free on the target drive on top of whatever qBittorrent
    // has to copy, so a move never fills a drive.
    private const long FreeSpaceMargin = 2L * 1024 * 1024 * 1024;

    // Only worth moving when most of the torrent is already in the library;
    // otherwise the move mostly copies files across.
    private const double LeastShareInLibrary = 0.5;

    // Generous: a recheck reads every byte. Allows for a slow drive.
    private const long SlowestBytesPerSecond = 20L * 1024 * 1024;

    private readonly DriveCandidateCache<SeedMoveCandidate> _cache =
        new(
            "seed-move-cache.json",
            candidate => candidate.TorrentHash,
            candidate =>
                candidate.Files.Count > 0 &&
                File.Exists(Path.Combine(candidate.SavePath, candidate.Files[0].RelativePath)));

    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    private readonly ISettingsService _settings;
    private readonly IFileInventoryService _inventory;
    private readonly IQbittorrentScanner _qbittorrent;
    private readonly IHardlinkService _hardlinks;
    private readonly IHistoryService _history;

    public SeedMoveService(
        ISettingsService settings,
        IFileInventoryService inventory,
        IQbittorrentScanner qbittorrent,
        IHardlinkService hardlinks,
        IHistoryService history)
    {
        _settings = settings;
        _inventory = inventory;
        _qbittorrent = qbittorrent;
        _hardlinks = hardlinks;
        _history = history;
    }

    public event Action? Changed;

    public SeedMoveProgress? Current { get; private set; }

    /// <summary>
    /// Small files (.nfo, subtitles, artwork) share sizes by chance, so a
    /// size match below this means nothing. Tests lower it.
    /// </summary>
    public long SmallestMatchedFileBytes { get; set; } = 10L * 1024 * 1024;

    /// <summary>How often qBittorrent is asked whether it has finished. Tests shorten it.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Removes an old seed file once the move is proven. Sends it to the
    /// Recycle Bin; tests replace it so nothing lands in a real one.
    /// </summary>
    public Func<string, bool> RemoveOldFile { get; set; } = RecycleBinHelper.Send;

    public IReadOnlyList<SeedMoveCandidate> GetCandidates(
        string driveName) =>
        _cache.GetCandidates(driveName);

    public DateTime? GetLastScanUtc(
        string driveName) =>
        _cache.GetLastScanUtc(driveName);

    // ------------------------------------------------------------- finding

    public async Task ScanAsync(
        string driveName,
        CancellationToken cancellationToken = default)
    {
        var settings = await _settings.GetSettingsAsync();
        var enabled = settings.DriveProfiles.Where(profile => profile.Enabled).ToList();

        var profile =
            enabled.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, driveName, StringComparison.OrdinalIgnoreCase));

        if (profile is null)
        {
            return;
        }

        var torrents = await _qbittorrent.TryGetTorrentsAsync();

        if (torrents is null)
        {
            // Can't tell without qBittorrent; keep what the last scan found.
            return;
        }

        var sourceRoots = DownloadRoots(profile);
        var ownLibraries = LibraryRoots(profile);

        var others = enabled.Where(other => !ReferenceEquals(other, profile)).ToList();

        Dictionary<long, List<(string Path, DriveProfile Owner)>>? librarySizes = null;

        var found = new List<SeedMoveCandidate>();

        foreach (var torrent in torrents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Only finished torrents: an unfinished one still has data to fetch.
            if (torrent.Progress < 1 || string.IsNullOrWhiteSpace(torrent.SavePath))
            {
                continue;
            }

            var sourceRoot =
                sourceRoots.FirstOrDefault(root => PathContainment.IsPathUnderRoot(torrent.SavePath, root.Path));

            if (sourceRoot.Path is null ||
                ownLibraries.Any(library => PathContainment.IsPathUnderRoot(torrent.SavePath, library)))
            {
                continue;
            }

            librarySizes ??= await IndexLibrariesAsync(others);

            if (librarySizes.Count == 0)
            {
                break;
            }

            var files = await _qbittorrent.GetTorrentFilesAsync(torrent.TorrentHash);

            if (files is null || files.Count == 0)
            {
                continue;
            }

            // The drive holding the most of this torrent is where it goes.
            var target =
                files
                    .SelectMany(file =>
                        (librarySizes.TryGetValue(file.SizeBytes, out var matches) ? matches : [])
                            .Select(match => match.Owner)
                            .Distinct()
                            .Select(owner => (Owner: owner, file.SizeBytes)))
                    .GroupBy(match => match.Owner)
                    .OrderByDescending(group => group.Sum(match => match.SizeBytes))
                    .Select(group => group.Key)
                    .FirstOrDefault();

            if (target is null)
            {
                continue;
            }

            var targetRoot = MatchingDownloadRoot(target, sourceRoot.Kind);

            if (string.IsNullOrWhiteSpace(targetRoot))
            {
                continue;
            }

            var relative = Path.GetRelativePath(sourceRoot.Path, torrent.SavePath);

            var candidate =
                new SeedMoveCandidate
                {
                    TorrentHash = torrent.TorrentHash,
                    TorrentName = torrent.Name,
                    TrackerHost = Uri.TryCreate(torrent.Tracker, UriKind.Absolute, out var tracker) ? tracker.Host : string.Empty,
                    Category = torrent.Category,
                    SavePath = torrent.SavePath,
                    TargetSavePath = relative == "." ? targetRoot : Path.GetFullPath(Path.Combine(targetRoot, relative)),
                    SourceDriveName = profile.Name,
                    TargetDriveName = target.Name,
                    Files =
                        files
                            .Select(file =>
                                new SeedMoveFile
                                {
                                    RelativePath = file.RelativePath,
                                    SizeBytes = file.SizeBytes,
                                    LibraryCandidates =
                                        librarySizes.TryGetValue(file.SizeBytes, out var matches)
                                            ? matches.Where(match => ReferenceEquals(match.Owner, target)).Select(match => match.Path).ToList()
                                            : []
                                })
                            .ToList()
                };

            if (candidate.TotalBytes > 0 &&
                (double)candidate.LinkedBytes / candidate.TotalBytes >= LeastShareInLibrary)
            {
                found.Add(candidate);
            }
        }

        _cache.SetCandidates(
            profile.Name,
            found.OrderByDescending(candidate => candidate.LinkedBytes));
    }

    private async Task<Dictionary<long, List<(string Path, DriveProfile Owner)>>> IndexLibrariesAsync(
        IReadOnlyList<DriveProfile> profiles)
    {
        var index = new Dictionary<long, List<(string, DriveProfile)>>();

        foreach (var profile in profiles)
        {
            foreach (var root in LibraryRoots(profile))
            {
                // The listing the drive's own scan saved; read now only if
                // there isn't one yet.
                var files = await _inventory.GetInventoryAsync(root, forceRefresh: false);

                if (files.Count == 0)
                {
                    files = await _inventory.GetInventoryAsync(root, forceRefresh: true);
                }

                foreach (var file in files.Where(file => file.SizeBytes >= SmallestMatchedFileBytes))
                {
                    if (!index.TryGetValue(file.SizeBytes, out var bucket))
                    {
                        index[file.SizeBytes] = bucket = [];
                    }

                    bucket.Add((file.FullPath, profile));
                }
            }
        }

        return index;
    }

    // -------------------------------------------------------------- moving

    public async Task<SeedMoveResult> MoveAsync(
        SeedMoveCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        if (!await _oneAtATime.WaitAsync(0, cancellationToken))
        {
            return new SeedMoveResult(false, "Another move is still running. Wait for it to finish.");
        }

        try
        {
            Report(candidate, "Checking the torrent is unchanged");

            var result = await MoveCoreAsync(candidate, cancellationToken);

            await _history.AddRecordAsync(
                new HistoryRecord
                {
                    ActionType = HistoryActionType.SeedMoved,
                    FileName = candidate.TorrentName,
                    DownloadPath = candidate.SavePath,
                    LibraryPath = candidate.TargetSavePath,
                    SizeBytes = result.FreedBytes,
                    Success = result.Succeeded,
                    Message = result.Message
                });

            if (result.Succeeded)
            {
                _cache.RemoveCandidate(candidate.TorrentHash);
            }

            return result;
        }
        finally
        {
            Current = null;
            _oneAtATime.Release();
            Changed?.Invoke();
        }
    }

    private async Task<SeedMoveResult> MoveCoreAsync(
        SeedMoveCandidate candidate,
        CancellationToken cancellationToken)
    {
        // 1. Everything the scan saw must still be true.
        var torrent = await FindTorrentAsync(candidate.TorrentHash);

        if (torrent is null)
        {
            return new(false, "qBittorrent can't be reached, or no longer has this torrent. Nothing was changed.");
        }

        if (!SamePath(torrent.SavePath, candidate.SavePath))
        {
            return new(false, $"The torrent has moved since the scan (it is now in {torrent.SavePath}). Scan again. Nothing was changed.");
        }

        if (torrent.Progress < 1 || IsBusy(torrent.State))
        {
            return new(false, "The torrent isn't finished, or qBittorrent is busy with it. Try again later. Nothing was changed.");
        }

        var files = await _qbittorrent.GetTorrentFilesAsync(candidate.TorrentHash);

        if (files is null ||
            files.Count != candidate.Files.Count ||
            files.Zip(candidate.Files).Any(pair =>
                !string.Equals(pair.First.RelativePath, pair.Second.RelativePath, StringComparison.OrdinalIgnoreCase) ||
                pair.First.SizeBytes != pair.Second.SizeBytes))
        {
            return new(false, "The torrent's files have changed since the scan. Scan again. Nothing was changed.");
        }

        // 2. Pick the library file for each, and plan the links.
        var links = new List<(string Library, string Link, string Source, long Size)>();

        foreach (var file in candidate.Files)
        {
            var source = Path.Combine(candidate.SavePath, file.RelativePath);

            if (!File.Exists(source) || new FileInfo(source).Length != file.SizeBytes)
            {
                return new(false, $"{file.RelativePath} is missing or has changed. Scan again. Nothing was changed.");
            }

            if (!file.InLibrary)
            {
                continue;
            }

            Report(candidate, $"Finding the library copy of {Path.GetFileName(file.RelativePath)}");

            var library = await PickLibraryFileAsync(file, source, cancellationToken);

            if (library is null)
            {
                continue;
            }

            links.Add((library, Path.Combine(candidate.TargetSavePath, file.RelativePath), source, file.SizeBytes));
        }

        if (links.Count == 0)
        {
            return new(false, "None of this torrent's files are in the library any more. Nothing was changed.");
        }

        // 3. Room for whatever qBittorrent has to copy.
        var copied = candidate.TotalBytes - links.Sum(link => link.Size);

        if (copied > 0)
        {
            var free = FreeSpace(candidate.TargetSavePath);

            if (free is not null && free < copied + FreeSpaceMargin)
            {
                return new(false, $"{candidate.TargetDriveName} needs {SizeFormatter.FormatBytes(copied)} free for the files that aren't in the library, plus some room to spare. Nothing was changed.");
            }
        }

        foreach (var link in links)
        {
            if (File.Exists(link.Link) && !await _hardlinks.AreHardlinkedAsync(link.Link, link.Library))
            {
                return new(false, $"A different file is already at {link.Link}. Nothing was changed.");
            }
        }

        // 4. Pause, link, move, recheck.
        var wasStopped = IsStopped(torrent.State);

        Report(candidate, "Pausing the torrent");

        if (await _qbittorrent.StopTorrentAsync(candidate.TorrentHash) is { } stopError)
        {
            return new(false, $"qBittorrent didn't pause the torrent: {stopError}. Nothing was changed.");
        }

        var created = new List<(string Library, string Link)>();

        foreach (var link in links)
        {
            if (File.Exists(link.Link))
            {
                continue;
            }

            Report(candidate, $"Linking {Path.GetFileName(link.Link)}");

            var made = await _hardlinks.CreateHardlinkAsync(link.Library, link.Link);

            if (!made.Success)
            {
                await RemoveLinksAsync(created);

                if (!wasStopped)
                {
                    await _qbittorrent.StartTorrentAsync(candidate.TorrentHash);
                }

                return new(false, $"Couldn't link {Path.GetFileName(link.Link)}: {made.ErrorMessage} Nothing else was changed.");
            }

            created.Add((link.Library, link.Link));
        }

        Report(candidate, copied > 0
            ? $"Moving the torrent; qBittorrent is copying {SizeFormatter.FormatBytes(copied)}"
            : "Moving the torrent");

        var moved =
            await _qbittorrent.SetLocationAsync(candidate.TorrentHash, candidate.TargetSavePath) is null &&
            await WaitAsync(
                candidate.TorrentHash,
                current => SamePath(current.SavePath, candidate.TargetSavePath) && !IsMoving(current.State),
                TimeFor(copied),
                cancellationToken);

        var proven = false;

        if (moved)
        {
            Report(candidate, "qBittorrent is checking every piece");

            proven =
                await _qbittorrent.RecheckTorrentAsync(candidate.TorrentHash) is null &&
                await WaitForRecheckAsync(candidate.TorrentHash, TimeFor(candidate.TotalBytes), cancellationToken);
        }

        if (!proven)
        {
            return await RollBackAsync(candidate, created, moved);
        }

        // 5. The torrent now seeds from the library's drive. The old files
        // are no longer used by anything.
        Report(candidate, "Removing the old copy");

        long freed = 0;
        var notRemoved = 0;

        foreach (var link in links)
        {
            if (!File.Exists(link.Source))
            {
                continue;
            }

            if (RemoveOldFile(link.Source))
            {
                freed += link.Size;
            }
            else
            {
                notRemoved++;
            }
        }

        RemoveEmptyFolders(candidate);

        var shared =
            await CountSharedAsync(links);

        var message =
            $"Now seeding from {candidate.TargetSavePath}, sharing {shared} of {candidate.Files.Count} file(s) with the library. It is paused; start it in qBittorrent when you're ready.";

        if (copied > 0)
        {
            message += $" qBittorrent copied {SizeFormatter.FormatBytes(copied)} that the library doesn't have.";
        }

        if (notRemoved > 0)
        {
            message += $" {notRemoved} old file(s) in {candidate.SavePath} couldn't be removed; delete them yourself.";
        }

        if (shared < links.Count)
        {
            message += " Some files ended up as copies rather than links; Potential Waste will offer to convert them after the next scan.";
        }

        return new(true, message, freed);
    }

    private async Task<SeedMoveResult> RollBackAsync(
        SeedMoveCandidate candidate,
        List<(string Library, string Link)> created,
        bool moved)
    {
        Report(candidate, "That didn't work; putting everything back");

        var note = moved
            ? "qBittorrent's check didn't pass."
            : "qBittorrent didn't finish moving the torrent.";

        // The old files were never touched, so pointing the torrent back at
        // them restores it. Anything qBittorrent moved comes back with it.
        var back =
            await _qbittorrent.SetLocationAsync(candidate.TorrentHash, candidate.SavePath) is null &&
            await WaitAsync(
                candidate.TorrentHash,
                current => SamePath(current.SavePath, candidate.SavePath) && !IsMoving(current.State),
                TimeFor(candidate.TotalBytes),
                CancellationToken.None);

        if (!back)
        {
            return new(false, $"{note} Simplifiarr couldn't move the torrent back to {candidate.SavePath}; in qBittorrent, set its location to that folder and recheck it. Nothing was deleted.");
        }

        await RemoveLinksAsync(created);
        await _qbittorrent.RecheckTorrentAsync(candidate.TorrentHash);

        return new(false, $"{note} The torrent is back in {candidate.SavePath}, paused, and is being rechecked. Nothing was deleted.");
    }

    private async Task RemoveLinksAsync(
        IEnumerable<(string Library, string Link)> created)
    {
        foreach (var (library, link) in created)
        {
            // Only a name Simplifiarr made that still points at the library file.
            if (File.Exists(link) && await _hardlinks.AreHardlinkedAsync(link, library))
            {
                try
                {
                    File.Delete(link);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    AppLog.Warn($"Couldn't remove the link {link}.", exception);
                }
            }
        }
    }

    private async Task<string?> PickLibraryFileAsync(
        SeedMoveFile file,
        string source,
        CancellationToken cancellationToken)
    {
        var existing = file.LibraryCandidates.Where(File.Exists).ToList();

        // One file of exactly this size is it; the recheck proves it. With
        // several, compare each to find the right one.
        if (existing.Count <= 1)
        {
            return existing.FirstOrDefault();
        }

        foreach (var library in existing)
        {
            if (await FileContentComparer.AreIdenticalAsync(source, library, cancellationToken))
            {
                return library;
            }
        }

        return null;
    }

    private async Task<int> CountSharedAsync(
        IEnumerable<(string Library, string Link, string Source, long Size)> links)
    {
        var shared = 0;

        foreach (var link in links)
        {
            if (await _hardlinks.AreHardlinkedAsync(link.Link, link.Library))
            {
                shared++;
            }
        }

        return shared;
    }

    // Folders the torrent itself brought (its own top folder and any below
    // it) are removed once empty. The downloads folder itself never is.
    private static void RemoveEmptyFolders(
        SeedMoveCandidate candidate)
    {
        var folders =
            candidate.Files
                .Select(file => Path.GetDirectoryName(Path.Combine(candidate.SavePath, file.RelativePath)))
                .Where(folder => folder is not null && !SamePath(folder, candidate.SavePath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(folder => folder!.Length);

        foreach (var folder in folders)
        {
            var current = folder;

            while (current is not null &&
                   !SamePath(current, candidate.SavePath) &&
                   PathContainment.IsPathUnderRoot(current, candidate.SavePath))
            {
                try
                {
                    if (!Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any())
                    {
                        break;
                    }

                    Directory.Delete(current);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    break;
                }

                current = Path.GetDirectoryName(current);
            }
        }
    }

    private async Task<bool> WaitAsync(
        string hash,
        Func<TorrentRecord, bool> done,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;

        // Give qBittorrent a moment to start before reading its state.
        await Task.Delay(PollInterval, cancellationToken);

        while (DateTime.UtcNow < deadline)
        {
            var current = await FindTorrentAsync(hash);

            if (current is not null && done(current))
            {
                return true;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        return false;
    }

    /// <summary>
    /// True once qBittorrent has rechecked the torrent and found every
    /// piece. Right after a move it can still show the old progress for a
    /// moment, and a small torrent can finish checking between two looks,
    /// so the answer has to be the same twice in a row.
    /// </summary>
    private async Task<bool> WaitForRecheckAsync(
        string hash,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        var completeInARow = 0;
        var incompleteInARow = 0;

        await Task.Delay(PollInterval, cancellationToken);

        while (DateTime.UtcNow < deadline)
        {
            var current = await FindTorrentAsync(hash);

            if (current is null || IsChecking(current.State))
            {
                completeInARow = incompleteInARow = 0;
            }
            else if (current.Progress >= 1 && !IsFaulted(current.State))
            {
                incompleteInARow = 0;

                if (++completeInARow >= 2)
                {
                    return true;
                }
            }
            else
            {
                completeInARow = 0;

                if (++incompleteInARow >= 2)
                {
                    return false;
                }
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        return false;
    }

    private async Task<TorrentRecord?> FindTorrentAsync(
        string hash) =>
        (await _qbittorrent.TryGetTorrentsAsync())?
            .FirstOrDefault(torrent => string.Equals(torrent.TorrentHash, hash, StringComparison.OrdinalIgnoreCase));

    private void Report(
        SeedMoveCandidate candidate,
        string step)
    {
        Current = new SeedMoveProgress(candidate.TorrentHash, candidate.TorrentName, step);
        Changed?.Invoke();
    }

    private static TimeSpan TimeFor(
        long bytes) =>
        TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds((double)bytes / SlowestBytesPerSecond);

    private static long? FreeSpace(
        string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));

            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // qBittorrent 4 says "paused", 5 says "stopped".
    private static bool IsStopped(string state) =>
        state.StartsWith("stopped", StringComparison.OrdinalIgnoreCase) ||
        state.StartsWith("paused", StringComparison.OrdinalIgnoreCase);

    private static bool IsMoving(string state) =>
        state.Equals("moving", StringComparison.OrdinalIgnoreCase);

    private static bool IsChecking(string state) =>
        state.StartsWith("checking", StringComparison.OrdinalIgnoreCase);

    private static bool IsFaulted(string state) =>
        state.Equals("error", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("missingFiles", StringComparison.OrdinalIgnoreCase);

    private static bool IsBusy(string state) =>
        IsMoving(state) || IsChecking(state);

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------- folders

    private enum RootKind { Downloads, Audiobooks, Music, Comics, SeedOnly }

    // Most specific first, so a music download folder inside the main one
    // is recognised as music.
    private static List<(string Path, RootKind Kind)> DownloadRoots(
        DriveProfile profile) =>
        new (string Path, RootKind Kind)[]
            {
                (profile.AudiobookDownloadPath, RootKind.Audiobooks),
                (profile.MusicDownloadPath, RootKind.Music),
                (profile.ComicDownloadPath, RootKind.Comics),
                (profile.SeedOnlyPath, RootKind.SeedOnly),
                (profile.DownloadPath, RootKind.Downloads)
            }
            .Where(root => !string.IsNullOrWhiteSpace(root.Path))
            .ToList();

    private static string MatchingDownloadRoot(
        DriveProfile target,
        RootKind kind)
    {
        var specific =
            kind switch
            {
                RootKind.Audiobooks => target.AudiobookDownloadPath,
                RootKind.Music => target.MusicDownloadPath,
                RootKind.Comics => target.ComicDownloadPath,
                RootKind.SeedOnly => target.SeedOnlyPath,
                _ => target.DownloadPath
            };

        return string.IsNullOrWhiteSpace(specific)
            ? target.DownloadPath
            : specific;
    }

    private static List<string> LibraryRoots(
        DriveProfile profile) =>
        new[]
            {
                profile.MovieLibraryPath,
                profile.TvLibraryPath,
                profile.BookLibraryPath,
                profile.AudiobookLibraryPath,
                profile.MusicLibraryPath,
                profile.ComicLibraryPath
            }
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
