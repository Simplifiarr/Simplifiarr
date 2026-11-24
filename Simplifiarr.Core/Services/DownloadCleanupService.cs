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

    // One entry per torrent folder (or loose file): what the page shows.
    private readonly DriveCandidateCache<DownloadLeftover> _leftovers =
        new(
            "download-leftovers-cache.json",
            leftover => leftover.Path,
            leftover => leftover.IsFolder ? Directory.Exists(leftover.Path) : File.Exists(leftover.Path));

    // Files that go with a download and mean nothing once it's done.
    private static readonly HashSet<string> JunkExtensions =
        new([".srt", ".sub", ".idx", ".ass", ".ssa", ".vtt", ".sup", ".nfo", ".txt", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp",
             ".sfv", ".md5", ".sha1", ".url", ".!qb", ".part", ".torrent", ".m3u", ".m3u8", ".log", ".cue", ".db", ".ini", ".ds_store", ".xml", ".html", ".htm"],
            StringComparer.OrdinalIgnoreCase);

    // Folders that hold torrents rather than being one ("Torrents\Movies\<torrent>").
    private static readonly HashSet<string> CategoryFolders =
        new(["movies", "movie", "films", "tv", "shows", "series", "anime", "music", "books", "ebooks", "audiobooks", "comics",
             "radarr", "sonarr", "lidarr", "readarr", "complete", "completed", "done", "finished", "downloads", "seedonly", "seed only", "cross-seed", "crossseed"],
            StringComparer.OrdinalIgnoreCase);

    private enum FileRole
    {
        Media,
        Junk,
        Other
    }

    private sealed record FileOutcome(FileRecord File, string Group, FileRole Role, bool Active, DownloadCleanupCandidate? Match);

    public IReadOnlyList<DownloadLeftover> GetLeftovers(string driveName) =>
        _leftovers.GetCandidates(driveName);

    private readonly ISettingsService _settingsService;
    private readonly IFileInventoryService _fileInventory;
    private readonly ITorrentClient _client;
    private readonly IHardlinkService _hardlinkService;
    private readonly IHistoryService _historyService;
    private readonly IReleaseParser _parser;
    private readonly IQuarantineService? _quarantine;

    public DownloadCleanupService(
        ISettingsService settingsService,
        IFileInventoryService fileInventory,
        ITorrentClient client,
        IHardlinkService hardlinkService,
        IHistoryService historyService,
        IReleaseParser? parser = null,
        IQuarantineService? quarantine = null)
    {
        _settingsService = settingsService;
        _fileInventory = fileInventory;
        _client = client;
        _hardlinkService = hardlinkService;
        _historyService = historyService;
        _parser = parser ?? new ReleaseParser();
        _quarantine = quarantine;
    }

    /// <summary>
    /// Videos smaller than this are never offered as another release:
    /// samples and extras. Tests lower it.
    /// </summary>
    public long OtherReleaseMinimumBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>How old another drive's saved file listing may be before it is read again.</summary>
    public static readonly TimeSpan OtherDriveListingMaxAge = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Which movie or episode a video is, from its name (or, for a movie,
    /// its folder's): "movie|nightpier|2021" or "tv|show|1|2". Null
    /// when the name doesn't say clearly enough to be sure.
    /// </summary>
    public static string? IdentityOf(IReleaseParser parser, string path)
    {
        static string Title(ParsedRelease release) =>
            MusicAnalyzer.Key(release.NormalizedTitle);

        var parsed = parser.Parse(Path.GetFileNameWithoutExtension(path));

        if (parsed.IsTv)
        {
            return parsed.Season is { } season && parsed.Episode is { } episode && Title(parsed).Length > 0
                ? $"tv|{Title(parsed)}|{season}|{episode}"
                : null;
        }

        var title = Title(parsed);
        var year = parsed.Year;

        // A library file is often just "Film.mkv" inside "Film (2021)".
        if (year is null || title.Length == 0)
        {
            var folder = parser.Parse(Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty);

            if (!folder.IsTv && folder.Year is not null && (title.Length == 0 || Title(folder) == title || year is null))
            {
                year = folder.Year;
                title = Title(folder).Length > 0 ? Title(folder) : title;
            }
        }

        return year is not null && title.Length >= 2 ? $"movie|{title}|{year}" : null;
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
            ExistingFolders(profile.AllDownloadPaths().ToArray());

        var libraryRoots =
            ExistingFolders(
                profile.MovieLibraryPath,
                profile.TvLibraryPath,
                profile.BookLibraryPath,
                profile.AudiobookLibraryPath,
                profile.MusicLibraryPath,
                profile.ComicLibraryPath);

        // A library copy on another drive counts too: the download can't be
        // hardlinked to it, so once the client is done with the download it
        // is a whole second copy.
        var otherLibraryRoots =
            settings.DriveProfiles
                .Where(other => other.Enabled && !ReferenceEquals(other, profile))
                .SelectMany(other =>
                    ExistingFolders(
                        other.MovieLibraryPath,
                        other.TvLibraryPath,
                        other.BookLibraryPath,
                        other.AudiobookLibraryPath,
                        other.MusicLibraryPath,
                        other.ComicLibraryPath))
                .Where(root => !libraryRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        if (downloadRoots.Count == 0 || libraryRoots.Count + otherLibraryRoots.Count == 0)
        {
            return new DownloadCleanupScanResult
            {
                Error =
                    "This drive needs at least one existing download folder, and a library folder here or on another drive (see Settings)."
            };
        }

        // Without the client's torrent list every download would look
        // inactive - so an unreachable client stops the scan outright.
        var torrents =
            await _client.TryGetTorrentsAsync();

        if (torrents is null)
        {
            return new DownloadCleanupScanResult
            {
                Error =
                    $"Couldn't reach {_client.Name} (check its address in Settings). Nothing was scanned: without its torrent list, every download would look inactive."
            };
        }

        var active =
            new ActivePathSet(torrents);

        var libraryFilesBySize =
            new Dictionary<long, List<FileRecord>>();

        // Every movie and episode in the libraries, by what it is rather than
        // its bytes, to find downloads that are another release of one.
        var libraryByIdentity =
            new Dictionary<string, FileRecord>(StringComparer.Ordinal);

        foreach (var root in libraryRoots.Concat(otherLibraryRoots))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // This drive's folders are read fresh. Other drives' folders use
            // the listing their own scan saved, so checking six drives
            // doesn't read every library six times; they are only read here
            // if no listing has been saved yet.
            var ownFolder =
                libraryRoots.Contains(root, StringComparer.OrdinalIgnoreCase);

            // Another drive's saved listing is used only while it is fresh:
            // an old one misses what was added or moved there since, and
            // the download that is a copy of it would be missed too. Reading
            // a listing is quick; the first drive checked refreshes it, and
            // the drives after reuse that.
            var stale =
                _fileInventory.GetLastScanUtc(root) is not { } listed ||
                DateTime.UtcNow - listed > OtherDriveListingMaxAge;

            var files =
                await _fileInventory.GetInventoryAsync(
                    root,
                    forceRefresh: ownFolder || stale);

            if (!ownFolder && files.Count == 0)
            {
                files =
                    await _fileInventory.GetInventoryAsync(
                        root,
                        forceRefresh: true);
            }

            foreach (var file in files.Where(file => file.SizeBytes >= OtherReleaseMinimumBytes && IsVideo(file)))
            {
                if (IdentityOf(_parser, file.FullPath) is { } identity &&
                    (!libraryByIdentity.TryGetValue(identity, out var known) || file.SizeBytes > known.SizeBytes))
                {
                    libraryByIdentity[identity] = file;
                }
            }

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

        // Where torrents are saved: every drive's download folders and the
        // client's own save paths. A torrent's folder is the one just inside.
        var containers =
            settings.DriveProfiles
                .SelectMany(other => other.AllDownloadPaths().Append(other.MovieDownloadPath).Append(other.TvDownloadPath).Append(other.SeedOnlyPath))
                .Concat(torrents.Select(torrent => torrent.SavePath))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.TrimEndingDirectorySeparator(path.Trim()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        var outcomes = new List<FileOutcome>();

        foreach (var root in downloadRoots)
        {
            var downloadFiles =
                await _fileInventory.GetInventoryAsync(
                    root,
                    forceRefresh: true);

            foreach (var file in downloadFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!seenDownloadPaths.Add(file.FullPath))
                {
                    continue;
                }

                // A download folder nested inside (or equal to) a library
                // folder isn't a "download copy" of anything.
                if (libraryRoots.Concat(otherLibraryRoots).Any(libraryRoot =>
                        PathContainment.IsPathUnderRoot(file.FullPath, libraryRoot)))
                {
                    continue;
                }

                var group = GroupOf(file.FullPath, containers);
                var isActive = active.Contains(file.FullPath);
                var role = RoleOf(file);

                if (role != FileRole.Media || file.SizeBytes <= 0)
                {
                    outcomes.Add(new FileOutcome(file, group, file.SizeBytes <= 0 ? FileRole.Junk : role, isActive, null));
                    continue;
                }

                result.DownloadFilesChecked++;

                if (isActive)
                {
                    if (libraryFilesBySize.ContainsKey(file.SizeBytes))
                    {
                        result.SkippedStillInClient++;
                    }

                    outcomes.Add(new FileOutcome(file, group, role, true, null));
                    continue;
                }

                (FileRecord Library, DownloadMatchKind Kind)? match = null;

                if (libraryFilesBySize.TryGetValue(file.SizeBytes, out var libraryMatches))
                {
                    match =
                        await FindLibraryMatchAsync(
                            file,
                            libraryMatches,
                            cancellationToken);
                }

                DownloadCleanupCandidate? candidate = null;

                if (match is not null)
                {
                    candidate =
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
                        };

                    result.Candidates.Add(candidate);
                }
                else if (FindOtherRelease(file, libraryByIdentity, active) is { } other)
                {
                    other.DriveProfileName = profile.Name;
                    candidate = other;
                    result.Candidates.Add(other);
                }
                else if (IsVideo(file) && file.SizeBytes < OtherReleaseMinimumBytes)
                {
                    // A short clip the library doesn't have: an extra or a
                    // sample, not something worth keeping a folder for.
                    outcomes.Add(new FileOutcome(file, group, FileRole.Junk, false, null));
                    continue;
                }

                outcomes.Add(new FileOutcome(file, group, role, false, candidate));
            }
        }

        result.Candidates =
            result.Candidates
                .OrderByDescending(candidate => candidate.SizeBytes)
                .ThenBy(candidate => candidate.DownloadPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

        result.Leftovers = Leftovers(outcomes, profile.Name);

        _cache.SetCandidates(profile.Name, result.Candidates);
        _leftovers.SetCandidates(profile.Name, result.Leftovers);

        return result;
    }

    /// <summary>
    /// The torrent a download file belongs to: the folder just inside the
    /// downloads folder holding it (going past category folders such as
    /// "Movies"), or the file itself when it sits loose there.
    /// </summary>
    public static string GroupOf(string path, IReadOnlyList<string> containers)
    {
        var container =
            containers
                .Where(candidate => PathContainment.IsPathUnderRoot(path, candidate))
                .OrderByDescending(candidate => candidate.Length)
                .FirstOrDefault();

        if (container is null)
        {
            return path;
        }

        var parts = Path.GetRelativePath(container, path).Split(Path.DirectorySeparatorChar);
        var current = container;
        var index = 0;

        while (index < parts.Length - 1 && CategoryFolders.Contains(parts[index]))
        {
            current = Path.Combine(current, parts[index]);
            index++;
        }

        return Path.Combine(current, parts[index]);
    }

    private static FileRole RoleOf(FileRecord file)
    {
        if (JunkExtensions.Contains(file.Extension) ||
            file.FileName.StartsWith("._", StringComparison.Ordinal) ||
            (file.FileType == FileType.Video && file.FileName.Contains("sample", StringComparison.OrdinalIgnoreCase)))
        {
            return FileRole.Junk;
        }

        return file.FileType is FileType.Video or FileType.Audio or FileType.Book or FileType.Comic
            ? FileRole.Media
            : FileRole.Other;
    }

    // One leftover per torrent nobody uses, with the weakest proof among its media.
    private static List<DownloadLeftover> Leftovers(List<FileOutcome> outcomes, string driveName)
    {
        var leftovers = new List<DownloadLeftover>();

        foreach (var group in outcomes.GroupBy(outcome => outcome.Group, StringComparer.OrdinalIgnoreCase))
        {
            // Anything a torrent still uses keeps the whole folder.
            if (group.Any(outcome => outcome.Active))
            {
                continue;
            }

            var media = group.Where(outcome => outcome.Role == FileRole.Media).ToList();
            var junk = group.Where(outcome => outcome.Role == FileRole.Junk).ToList();
            var kept = group.Where(outcome => outcome.Role == FileRole.Other).ToList();

            DownloadLeftoverKind kind;

            if (media.Count == 0)
            {
                // Only leftovers, with nothing unknown among them.
                if (junk.Count == 0 || kept.Count > 0)
                {
                    continue;
                }

                kind = DownloadLeftoverKind.LeftoversOnly;
            }
            else
            {
                kind =
                    media.Any(outcome => outcome.Match is null) ? DownloadLeftoverKind.NotInLibrary
                    : media.Any(outcome => outcome.Match!.MatchKind == DownloadMatchKind.OtherRelease) ? DownloadLeftoverKind.OtherRelease
                    : media.Any(outcome => outcome.Match!.MatchKind == DownloadMatchKind.IdenticalCopy) ? DownloadLeftoverKind.IdenticalCopy
                    : DownloadLeftoverKind.SameFile;
            }

            var isFolder = !string.Equals(group.Key, group.First().File.FullPath, StringComparison.OrdinalIgnoreCase);
            var junkBytes = junk.Sum(outcome => outcome.File.SizeBytes);

            leftovers.Add(
                new DownloadLeftover
                {
                    Path = group.Key,
                    IsFolder = isFolder,
                    DriveProfileName = driveName,
                    Kind = kind,
                    Media =
                        media.Select(outcome => outcome.Match ?? new DownloadCleanupCandidate
                            {
                                DownloadPath = outcome.File.FullPath,
                                FileName = outcome.File.FileName,
                                SizeBytes = outcome.File.SizeBytes,
                                MatchKind = DownloadMatchKind.NotInLibrary,
                                DriveProfileName = driveName,
                                DownloadModifiedUtc = outcome.File.LastModifiedUtc
                            })
                            .ToList(),
                    Junk = junk.Select(outcome => outcome.File.FullPath).ToList(),
                    JunkBytes = junkBytes,
                    Kept = kept.Select(outcome => outcome.File.FullPath).ToList(),
                    SizeBytes = group.Sum(outcome => outcome.File.SizeBytes),
                    FreedBytes =
                        junkBytes +
                        media.Where(outcome => outcome.Match is { MatchKind: DownloadMatchKind.IdenticalCopy or DownloadMatchKind.OtherRelease })
                            .Sum(outcome => outcome.File.SizeBytes)
                });
        }

        return leftovers
            .OrderBy(leftover => leftover.Kind)
            .ThenByDescending(leftover => leftover.FreedBytes)
            .ToList();
    }

    public async Task<DownloadCleanupRemoveResult> RemoveAsync(
        IReadOnlyCollection<DownloadCleanupCandidate> candidates)
    {
        var result =
            new DownloadCleanupRemoveResult();

        var torrents =
            await _client.TryGetTorrentsAsync();

        if (torrents is null)
        {
            result.Error =
                $"Couldn't reach {_client.Name}, so nothing was removed: Simplifiarr won't delete downloads without confirming {_client.Name} no longer has them.";

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

            if (candidate.MatchKind == DownloadMatchKind.OtherRelease)
            {
                await QuarantineOtherReleaseAsync(candidate, result);
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
                            ? $"Download copy deleted - {_client.Name} no longer has it and the library has {DescribeMatch(candidate.MatchKind)}."
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

    public async Task<DownloadCleanupRemoveResult> RemoveLeftoversAsync(
        IReadOnlyCollection<DownloadLeftover> leftovers)
    {
        var result = new DownloadCleanupRemoveResult();
        var torrents = await _client.TryGetTorrentsAsync();

        if (torrents is null)
        {
            result.Error =
                $"Couldn't reach {_client.Name}, so nothing was removed: Simplifiarr won't remove downloads without confirming {_client.Name} no longer has them.";

            return result;
        }

        var active = new ActivePathSet(torrents);

        foreach (var leftover in leftovers)
        {
            var problem = await LeftoverProblemAsync(leftover, torrents, active);

            if (problem is not null)
            {
                result.Skipped++;
                result.SkipReasons.Add($"{leftover.Name}: {problem}");
                continue;
            }

            var failures = new List<string>();

            foreach (var media in leftover.Media)
            {
                var single = new DownloadCleanupRemoveResult();

                if (media.MatchKind == DownloadMatchKind.OtherRelease)
                {
                    await QuarantineOtherReleaseAsync(media, single);
                }
                else
                {
                    await DeleteCopyAsync(media, single);
                }

                failures.AddRange(single.SkipReasons);
            }

            var junkFreed = 0L;

            foreach (var junk in leftover.Junk)
            {
                var size = File.Exists(junk) ? new FileInfo(junk).Length : 0;

                if (TryDeleteFile(junk) is { } error && File.Exists(junk))
                {
                    failures.Add($"{Path.GetFileName(junk)}: {error}");
                }
                else
                {
                    junkFreed += size;
                }
            }

            var folderGone = leftover.RemovesFolder && failures.Count == 0 && TryRemoveFolder(leftover.Path);

            if (junkFreed > 0 || folderGone)
            {
                await _historyService.AddRecordAsync(
                    new HistoryRecord
                    {
                        ActionType = HistoryActionType.DownloadCopyRemoved,
                        FileName = leftover.Name,
                        DownloadPath = leftover.Path,
                        SizeBytes = junkFreed,
                        Success = true,
                        Message =
                            $"Leftover download files deleted ({leftover.Junk.Count} file(s): subtitles, pictures, .nfo and the like)" +
                            (folderGone ? " and the empty folder removed." : ".")
                    });
            }

            if (failures.Count == 0)
            {
                result.Removed++;
                _leftovers.RemoveCandidate(leftover.Path);
            }
            else
            {
                result.Skipped++;
                result.SkipReasons.Add($"{leftover.Name}: {string.Join("; ", failures.Take(3))}");
            }
        }

        return result;
    }

    // Everything that made it safe must still be true.
    private async Task<string?> LeftoverProblemAsync(DownloadLeftover leftover, List<TorrentRecord> torrents, ActivePathSet active)
    {
        if (leftover.Kind == DownloadLeftoverKind.NotInLibrary)
        {
            return "your library doesn't have it, so it was left alone.";
        }

        if (active.Contains(leftover.Path) ||
            torrents.Any(torrent => !string.IsNullOrWhiteSpace(torrent.ContentPath) && PathContainment.IsPathUnderRoot(torrent.ContentPath, leftover.Path)))
        {
            return $"{_client.Name} uses it again.";
        }

        foreach (var media in leftover.Media)
        {
            if (await FindProblemAsync(media, active) is { } problem)
            {
                return $"{media.FileName}: {problem}";
            }
        }

        return null;
    }

    // The folder goes only when nothing is left in it that wasn't listed:
    // a file added since the scan keeps it.
    private static bool TryRemoveFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return true;
            }

            if (Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any())
            {
                return false;
            }

            Directory.Delete(folder, recursive: true);

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Exact copies and hardlinks are deleted (see RemoveAsync for why not Quarantine).
    private async Task DeleteCopyAsync(DownloadCleanupCandidate candidate, DownloadCleanupRemoveResult result)
    {
        var deleteError = TryDeleteFile(candidate.DownloadPath);
        var success = deleteError is null;

        await _historyService.AddRecordAsync(
            new HistoryRecord
            {
                ActionType = HistoryActionType.DownloadCopyRemoved,
                FileName = candidate.FileName,
                LibraryPath = candidate.LibraryPath,
                DownloadPath = candidate.DownloadPath,
                SizeBytes = candidate.SizeBytes,
                Success = success,
                Message =
                    success
                        ? $"Download copy deleted - {_client.Name} no longer has it and the library has {DescribeMatch(candidate.MatchKind)}."
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

    // Not proven identical, so it goes somewhere it can come back from.
    private async Task QuarantineOtherReleaseAsync(DownloadCleanupCandidate candidate, DownloadCleanupRemoveResult result)
    {
        var quarantined =
            _quarantine is null ? null : await _quarantine.QuarantineFileAsync(candidate.DownloadPath);

        var success = quarantined is not null;
        var versions =
            candidate.DownloadQuality.Length > 0 && candidate.LibraryQuality.Length > 0
                ? $" ({candidate.DownloadQuality}; the library has {candidate.LibraryQuality})"
                : string.Empty;

        await _historyService.AddRecordAsync(
            new HistoryRecord
            {
                ActionType = HistoryActionType.DownloadCopyRemoved,
                FileName = candidate.FileName,
                LibraryPath = candidate.LibraryPath,
                DownloadPath = candidate.DownloadPath,
                QuarantinePath = quarantined ?? string.Empty,
                SizeBytes = candidate.SizeBytes,
                Success = success,
                Message =
                    success
                        ? $"Another release of a title the library has, moved to Quarantine{versions} - {_client.Name} no longer has it."
                        : "Couldn't move this other release to Quarantine."
            });

        if (success)
        {
            result.Removed++;
            _cache.RemoveCandidate(candidate.DownloadPath);
        }
        else
        {
            result.Skipped++;
            result.SkipReasons.Add($"{candidate.FileName}: couldn't be moved to Quarantine (open in another program?).");
        }
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
            return $"{_client.Name} has this file again.";
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

        if (candidate.MatchKind == DownloadMatchKind.OtherRelease)
        {
            return NativeHardlinks.GetIdentity(candidate.DownloadPath) is { LinkCount: > 1 }
                ? "it is hardlinked somewhere now, so moving it would free nothing."
                : null;
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
        var others =
            libraryCandidates
                .Where(library =>
                    !string.Equals(
                        library.FullPath,
                        download.FullPath,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();

        // The quick check first: a hardlink is found without reading either
        // file, so look for one before comparing any file byte for byte.
        foreach (var library in others)
        {
            if (await _hardlinkService.AreHardlinkedAsync(
                    download.FullPath,
                    library.FullPath))
            {
                return (library, DownloadMatchKind.Hardlink);
            }
        }

        foreach (var library in others)
        {
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

    private static bool IsVideo(FileRecord file) =>
        file.FileType == FileType.Video;

    // A big video no torrent uses and nothing else links to, that is a
    // movie or episode the library already has as a different file.
    private DownloadCleanupCandidate? FindOtherRelease(
        FileRecord file,
        Dictionary<string, FileRecord> libraryByIdentity,
        ActivePathSet active)
    {
        if (!IsVideo(file) ||
            file.SizeBytes < OtherReleaseMinimumBytes ||
            file.FileName.Contains("sample", StringComparison.OrdinalIgnoreCase) ||
            active.Contains(file.FullPath) ||
            IdentityOf(_parser, file.FullPath) is not { } identity ||
            !libraryByIdentity.TryGetValue(identity, out var library) ||
            string.Equals(library.FullPath, file.FullPath, StringComparison.OrdinalIgnoreCase) ||
            NativeHardlinks.GetIdentity(file.FullPath) is { LinkCount: > 1 })
        {
            return null;
        }

        return new DownloadCleanupCandidate
        {
            DownloadPath = file.FullPath,
            FileName = file.FileName,
            SizeBytes = file.SizeBytes,
            LibraryPath = library.FullPath,
            LibrarySizeBytes = library.SizeBytes,
            MatchKind = DownloadMatchKind.OtherRelease,
            DownloadModifiedUtc = file.LastModifiedUtc,
            LibraryModifiedUtc = library.LastModifiedUtc,
            DownloadQuality = Quality(file.FileName),
            LibraryQuality = Quality(library.FileName)
        };
    }

    // "720P" from the parser, shown the way people write it: "720p".
    private string Quality(string fileName) =>
        _parser.Parse(fileName).Resolution is { Length: > 1 } resolution && resolution.EndsWith('P')
            ? resolution[..^1] + "p"
            : _parser.Parse(fileName).Resolution;

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
    /// Every path a torrent occupies on disk. A file counts as
    /// "still in the client" if it IS a torrent's content path or sits
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
