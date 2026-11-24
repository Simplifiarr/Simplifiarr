using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public interface IStorageService
{
    /// <summary>
    /// Measures every title folder in this drive entry's libraries, from
    /// the listing Scan has already read. Nothing is changed.
    /// </summary>
    Task ScanAsync(string driveName);

    DateTime? GetLastScanUtc(string driveName);

    /// <summary>
    /// Every drive with libraries on it: its space right now, its limit and
    /// its titles as last measured.
    /// </summary>
    Task<List<StorageDrive>> GetDrivesAsync();

    /// <summary>
    /// What to move where to bring full drives back down, least watched
    /// and smallest first. <paramref name="watch"/> says who watched a title.
    /// </summary>
    Task<List<StorageMoveSuggestion>> GetSuggestionsAsync(Func<StorageTitle, WatchRecord?>? watch = null);

    /// <summary>Drops a title that has been moved or deleted until the next Scan.</summary>
    void Forget(StorageTitle title);
}

public class StorageService : IStorageService
{
    // Small files (subtitles, artwork) can't be seeding anything worth
    // freeing, and asking Windows about each one would slow Scan down.
    private const long SmallestCheckedForSeedingBytes = 50L * 1024 * 1024;

    private const long Gb = 1024L * 1024 * 1024;

    private static readonly HashSet<string> VideoExtensions =
        new([".mkv", ".mp4", ".m4v", ".avi", ".ts", ".m2ts", ".mov", ".wmv", ".webm", ".mpg", ".mpeg"], StringComparer.OrdinalIgnoreCase);

    private readonly ISettingsService _settings;
    private readonly IFileInventoryService _inventory;

    private readonly DriveCandidateCache<StorageTitle> _cache =
        new(
            "storage-cache.json",
            title => title.FolderPath,
            title => Directory.Exists(title.FolderPath));

    private readonly ITorrentClient? _client;
    private readonly IArrPathService? _arr;

    public StorageService(
        ISettingsService settings,
        IFileInventoryService inventory,
        ITorrentClient? client = null,
        IArrPathService? arr = null)
    {
        _settings = settings;
        _inventory = inventory;
        _client = client;
        _arr = arr;
    }

    public DateTime? GetLastScanUtc(string driveName) =>
        _cache.GetLastScanUtc(driveName);

    public void Forget(StorageTitle title) =>
        _cache.RemoveCandidate(title.FolderPath);

    public async Task ScanAsync(string driveName)
    {
        var settings = await _settings.GetSettingsAsync();

        var profile =
            settings.DriveProfiles.FirstOrDefault(candidate =>
                candidate.Enabled &&
                string.Equals(candidate.Name, driveName, StringComparison.OrdinalIgnoreCase));

        if (profile is null)
        {
            return;
        }

        var downloadRoots =
            settings.DriveProfiles
                .Where(candidate => candidate.Enabled)
                .SelectMany(candidate => candidate.AllDownloadPaths().Append(candidate.SeedOnlyPath))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        var titles = new List<StorageTitle>();

        // Seeding means a torrent in the client uses the file. A hardlink in
        // a downloads folder alone doesn't: it can be a leftover whose torrent
        // was removed long ago, which a move takes care of. Without the
        // client, any hardlink into downloads counts, to be safe.
        var torrents = _client is null ? null : await _client.TryGetTorrentsAsync();
        var seeding = torrents is null ? null : SeedingCheck.From(torrents.Select(torrent => (torrent.ContentPath, torrent.Name)));

        foreach (var (kind, libraryPath) in Libraries(profile))
        {
            if (!Directory.Exists(libraryPath))
            {
                continue;
            }

            var files = await _inventory.GetInventoryAsync(libraryPath, forceRefresh: true);

            titles.AddRange(
                seeding is null
                    ? Measure(profile.Name, kind, libraryPath, files, downloadRoots)
                    : Measure(profile.Name, kind, libraryPath, files, path => seeding.Check(path).State == SeedState.Seeding));
        }

        await MarkShowsStillAiringAsync(settings, profile, titles);

        _cache.SetCandidates(profile.Name, titles);
        _cache.SetLastScanUtc(profile.Name, MediaKind.All);
    }

    /// <summary>
    /// Groups a library's files by their top folder, one title each. Files
    /// sitting straight in the library folder are titles of their own.
    /// </summary>
    public static List<StorageTitle> Measure(
        string driveName,
        string kind,
        string libraryPath,
        IEnumerable<FileRecord> files,
        IReadOnlyCollection<string> downloadRoots)
    {
        var library = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryPath));

        return files
            .Where(file => PathContainment.IsPathUnderRoot(file.FullPath, library))
            .GroupBy(file => TopEntry(library, file.FullPath), StringComparer.OrdinalIgnoreCase)
            .Select(group => new StorageTitle
            {
                DriveName = driveName,
                Kind = kind,
                LibraryPath = library,
                FolderPath = group.Key,
                Name = Path.GetFileName(group.Key),
                SizeBytes = group.Sum(file => file.SizeBytes),
                FileCount = group.Count(),
                LastWriteUtc = group.Max(file => file.LastModifiedUtc),
                LastVideoWriteUtc =
                    group
                        .Where(file => VideoExtensions.Contains(Path.GetExtension(file.FullPath)))
                        .Select(file => file.LastModifiedUtc)
                        .DefaultIfEmpty(DateTime.MinValue)
                        .Max(),
                SeedingBytes =
                    group
                        .Where(file => file.SizeBytes >= SmallestCheckedForSeedingBytes)
                        .Where(file => IsSeeding(file.FullPath, downloadRoots))
                        .Sum(file => file.SizeBytes)
            })
            .ToList();
    }

    /// <summary>
    /// The same, with seeding decided by <paramref name="isSeeding"/>.
    /// </summary>
    public static List<StorageTitle> Measure(
        string driveName,
        string kind,
        string libraryPath,
        IEnumerable<FileRecord> files,
        Func<string, bool> isSeeding)
    {
        var titles = Measure(driveName, kind, libraryPath, files, []);
        var byFolder = files.ToLookup(file => TopEntry(Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryPath)), file.FullPath), StringComparer.OrdinalIgnoreCase);

        foreach (var title in titles)
        {
            title.SeedingBytes =
                byFolder[title.FolderPath]
                    .Where(file => file.SizeBytes >= SmallestCheckedForSeedingBytes)
                    .Where(file => NativeHardlinks.GetIdentity(file.FullPath) is { LinkCount: > 1 } && isSeeding(file.FullPath))
                    .Sum(file => file.SizeBytes);
        }

        return titles;
    }

    // With a downloads drive, shows Sonarr still gets episodes of stay on it:
    // moved away, every new episode would be copied across drives.
    private async Task MarkShowsStillAiringAsync(SimplifiarrSettings settings, DriveProfile profile, List<StorageTitle> titles)
    {
        if (_arr is null ||
            DownloadsDriveRoot(settings) is not { } downloadsRoot ||
            !string.Equals(RootOfProfile(profile), downloadsRoot, StringComparison.OrdinalIgnoreCase) ||
            !titles.Any(title => title.Kind == StorageKinds.Tv))
        {
            return;
        }

        try
        {
            var airing =
                (await _arr.GetAllAsync(StorageKinds.Tv))
                    .Where(show => show.Airing)
                    .Select(show => Path.TrimEndingDirectorySeparator(show.Path))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var title in titles.Where(title => title.Kind == StorageKinds.Tv))
            {
                title.StaysForNewEpisodes = airing.Contains(Path.TrimEndingDirectorySeparator(title.FolderPath));
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // Without Sonarr every show stays put rather than risk copies.
            AppLog.Warn("Storage couldn't ask Sonarr which shows are still airing, so no show on the downloads drive will move this time.", exception);

            foreach (var title in titles.Where(title => title.Kind == StorageKinds.Tv))
            {
                title.StaysForNewEpisodes = true;
            }
        }
    }

    /// <summary>The root ("C:\") of the drive new downloads land on, or null when each drive has its own.</summary>
    public static string? DownloadsDriveRoot(SimplifiarrSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.DownloadsDrive))
        {
            return null;
        }

        var profile =
            settings.DriveProfiles.FirstOrDefault(candidate =>
                candidate.Enabled && string.Equals(candidate.Name, settings.DownloadsDrive, StringComparison.OrdinalIgnoreCase));

        return profile is null || RootOfProfile(profile) is not { Length: > 0 } root ? null : root;
    }

    private static string TopEntry(string library, string filePath)
    {
        var relative = Path.GetRelativePath(library, filePath);
        var first = relative.Split(Path.DirectorySeparatorChar, 2)[0];

        return Path.Combine(library, first);
    }

    /// <summary>The file is also linked into a downloads folder.</summary>
    private static bool IsSeeding(string path, IReadOnlyCollection<string> downloadRoots)
    {
        if (downloadRoots.Count == 0 ||
            NativeHardlinks.GetIdentity(path) is not { LinkCount: > 1 })
        {
            return false;
        }

        return NativeHardlinks.GetLinkPaths(path)
            .Any(name => downloadRoots.Any(root => PathContainment.IsPathUnderRoot(name, root)));
    }

    public async Task<List<StorageDrive>> GetDrivesAsync()
    {
        var settings = await _settings.GetSettingsAsync();

        var profiles =
            settings.DriveProfiles
                .Where(profile => profile.Enabled && Libraries(profile).Any())
                .ToList();

        var drives = new List<StorageDrive>();
        var downloadsRoot = DownloadsDriveRoot(settings);

        foreach (var group in profiles.GroupBy(RootOfProfile, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(group.Key))
            {
                continue;
            }

            var limit =
                Math.Clamp(
                    group.Select(profile => profile.FullAtPercent).FirstOrDefault(value => value.HasValue)
                        ?? settings.StorageFullAtPercent,
                    10,
                    100);

            var drive = new StorageDrive
            {
                Root = group.Key,
                ProfileNames = group.Select(profile => profile.Name).ToList(),
                IsProtected =
                    downloadsRoot is null
                        ? group.Any(profile => profile.IsProtected)
                        : string.Equals(group.Key, downloadsRoot, StringComparison.OrdinalIgnoreCase),
                IsDownloadsDrive = string.Equals(group.Key, downloadsRoot, StringComparison.OrdinalIgnoreCase),
                LimitPercent = limit,
                BringBackToPercent =
                    Math.Clamp(
                        group.Select(profile => profile.BringBackToPercent).FirstOrDefault(value => value.HasValue)
                            ?? limit - 10,
                        5,
                        limit),
                KeepFreeSetBytes =
                    group.Select(profile => profile.KeepFreeGb).FirstOrDefault(value => value.HasValue) is { } keepFree
                        ? Math.Max(0, keepFree) * Gb
                        : null,
                Titles =
                    group
                        .SelectMany(profile => _cache.GetCandidates(profile.Name))
                        .GroupBy(title => title.FolderPath, StringComparer.OrdinalIgnoreCase)
                        .Select(titles => titles.First())
                        .OrderByDescending(title => title.SizeBytes)
                        .ToList()
            };

            try
            {
                var info = new DriveInfo(group.Key);

                if (info.IsReady)
                {
                    drive.TotalBytes = info.TotalSize;
                    drive.FreeBytes = info.AvailableFreeSpace;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Unplugged or unreadable: shown as unavailable.
            }

            drives.Add(drive);
        }

        return drives.OrderBy(drive => drive.Root, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<List<StorageMoveSuggestion>> GetSuggestionsAsync(Func<StorageTitle, WatchRecord?>? watch = null)
    {
        var settings = await _settings.GetSettingsAsync();

        return StoragePlanner.Suggest(
            await GetDrivesAsync(),
            StoragePlanner.Destinations(settings.DriveProfiles),
            watch,
            StoragePlanner.LargestToMoveBytes(settings),
            StoragePlanner.RecentAfterUtc(settings, DateTime.UtcNow));
    }

    /// <summary>The root ("C:\") of the drive a drive entry is on, or empty.</summary>
    public static string RootOfProfile(DriveProfile profile)
    {
        var anyPath =
            new[] { profile.DriveRoot }
                .Concat(Libraries(profile).Select(library => library.Path))
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));

        try
        {
            return anyPath is null ? string.Empty : StoragePlanner.RootOf(anyPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return string.Empty;
        }
    }

    private static IEnumerable<(string Kind, string Path)> Libraries(DriveProfile profile) =>
        new[]
            {
                (StorageKinds.Movies, profile.MovieLibraryPath),
                (StorageKinds.Tv, profile.TvLibraryPath),
                (StorageKinds.Books, profile.BookLibraryPath),
                (StorageKinds.Audiobooks, profile.AudiobookLibraryPath),
                (StorageKinds.Music, profile.MusicLibraryPath),
                (StorageKinds.Comics, profile.ComicLibraryPath)
            }
            .Where(library => !string.IsNullOrWhiteSpace(library.Item2));
}
