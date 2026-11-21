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

    /// <summary>What to move where to bring full drives under their limit.</summary>
    Task<List<StorageMoveSuggestion>> GetSuggestionsAsync();

    /// <summary>Drops a title that has been moved or deleted until the next Scan.</summary>
    void Forget(StorageTitle title);
}

public class StorageService : IStorageService
{
    // Small files (subtitles, artwork) can't be seeding anything worth
    // freeing, and asking Windows about each one would slow Scan down.
    private const long SmallestCheckedForSeedingBytes = 50L * 1024 * 1024;

    private readonly ISettingsService _settings;
    private readonly IFileInventoryService _inventory;

    private readonly DriveCandidateCache<StorageTitle> _cache =
        new(
            "storage-cache.json",
            title => title.FolderPath,
            title => Directory.Exists(title.FolderPath));

    public StorageService(
        ISettingsService settings,
        IFileInventoryService inventory)
    {
        _settings = settings;
        _inventory = inventory;
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

        foreach (var (kind, libraryPath) in Libraries(profile))
        {
            if (!Directory.Exists(libraryPath))
            {
                continue;
            }

            var files = await _inventory.GetInventoryAsync(libraryPath, forceRefresh: true);

            titles.AddRange(Measure(profile.Name, kind, libraryPath, files, downloadRoots));
        }

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
                SeedingBytes =
                    group
                        .Where(file => file.SizeBytes >= SmallestCheckedForSeedingBytes)
                        .Where(file => IsSeeding(file.FullPath, downloadRoots))
                        .Sum(file => file.SizeBytes)
            })
            .ToList();
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

        foreach (var group in profiles.GroupBy(RootOfProfile, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(group.Key))
            {
                continue;
            }

            var drive = new StorageDrive
            {
                Root = group.Key,
                ProfileNames = group.Select(profile => profile.Name).ToList(),
                LimitPercent =
                    Math.Clamp(
                        group.Select(profile => profile.FullAtPercent).FirstOrDefault(value => value.HasValue)
                            ?? settings.StorageFullAtPercent,
                        10,
                        100),
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

    public async Task<List<StorageMoveSuggestion>> GetSuggestionsAsync()
    {
        var settings = await _settings.GetSettingsAsync();

        return StoragePlanner.Suggest(
            await GetDrivesAsync(),
            StoragePlanner.Destinations(settings.DriveProfiles));
    }

    private static string RootOfProfile(DriveProfile profile)
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
