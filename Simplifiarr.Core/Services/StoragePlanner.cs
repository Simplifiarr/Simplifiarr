using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Works out which titles to move, and where, to bring every drive back
/// under its limit. Pure arithmetic on sizes: nothing is read or changed.
/// </summary>
public static class StoragePlanner
{
    /// <summary>
    /// Where a title of each kind can go: drive root, drive entry name and
    /// its library folder for that kind.
    /// </summary>
    public sealed record Destination(
        string Root,
        string DriveName,
        string Kind,
        string LibraryPath);

    private const long Gb = 1024L * 1024 * 1024;

    /// <summary>Titles added or changed after this are left where they are.</summary>
    public static DateTime RecentAfterUtc(SimplifiarrSettings settings, DateTime nowUtc) =>
        settings.StorageRecentDays <= 0 ? DateTime.MaxValue : nowUtc.AddDays(-settings.StorageRecentDays);

    /// <summary>
    /// Added to Jellyfin, or its video written, after the cutoff. New
    /// artwork or an .nfo doesn't count.
    /// </summary>
    public static bool IsRecent(StorageTitle title, WatchRecord? watch, DateTime recentAfterUtc) =>
        title.ContentChangedUtc > recentAfterUtc ||
        watch?.AddedUtc > recentAfterUtc;

    /// <summary>Titles bigger than this are left to move by hand.</summary>
    public static long LargestToMoveBytes(SimplifiarrSettings settings) =>
        settings.StorageLargestToMoveGb <= 0 ? long.MaxValue : settings.StorageLargestToMoveGb * Gb;

    /// <summary>
    /// Brings every drive that is over its limit back to its target.
    /// Protected drives give titles and never take any; each title goes to
    /// the overflow drive with the most free space that still keeps its
    /// own free space afterwards. Least watched titles go first, then the
    /// smallest; titles over <paramref name="largestBytes"/> are left alone.
    /// </summary>
    public static List<StorageMoveSuggestion> Suggest(
        IReadOnlyList<StorageDrive> drives,
        IReadOnlyList<Destination> destinations,
        Func<StorageTitle, WatchRecord?>? watch = null,
        long largestBytes = long.MaxValue,
        DateTime? recentAfterUtc = null)
    {
        var recent = recentAfterUtc ?? DateTime.MaxValue;

        var suggestions = new List<StorageMoveSuggestion>();

        // Space each drive would have once the suggestions so far are done.
        var used =
            drives.ToDictionary(drive => drive.Root, drive => drive.UsedBytes, StringComparer.OrdinalIgnoreCase);

        foreach (var drive in drives.Where(drive => drive.IsAvailable && drive.IsOverLimit).OrderByDescending(drive => drive.ToFreeBytes))
        {
            var needed = used[drive.Root] - drive.TargetBytes;

            if (needed <= 0)
            {
                continue;
            }

            var movable =
                drive.Titles.Where(title =>
                    title.CanMove &&
                    title.SizeBytes <= largestBytes &&
                    !IsRecent(title, watch?.Invoke(title), recent));

            foreach (var title in MoveOrder(movable, watch))
            {
                if (needed <= 0)
                {
                    break;
                }

                var target =
                    destinations
                        .Where(destination =>
                            !string.Equals(destination.Root, drive.Root, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(destination.Kind, title.Kind, StringComparison.OrdinalIgnoreCase))
                        .Select(destination => (
                            Destination: destination,
                            Drive: drives.FirstOrDefault(other =>
                                string.Equals(other.Root, destination.Root, StringComparison.OrdinalIgnoreCase))))
                        .Where(pair => pair.Drive is { IsAvailable: true, IsProtected: false })
                        // It must still keep its own free space afterwards.
                        .Where(pair => pair.Drive!.LimitBytes - used[pair.Drive.Root] >= title.SizeBytes)
                        // The most free space in bytes, not the lowest percentage.
                        .OrderByDescending(pair => pair.Drive!.TotalBytes - used[pair.Drive.Root])
                        .Select(pair => pair.Destination)
                        .FirstOrDefault();

                if (target is null)
                {
                    continue;
                }

                suggestions.Add(
                    new StorageMoveSuggestion(
                        title,
                        drive.Root,
                        target.Root,
                        target.DriveName,
                        target.LibraryPath));

                used[drive.Root] -= title.SizeBytes;
                used[target.Root] += title.SizeBytes;
                needed -= title.SizeBytes;
            }
        }

        return suggestions;
    }

    /// <summary>
    /// The order titles leave a full drive: favorites last, then fewest
    /// people watching, longest since anyone played it, and smallest.
    /// </summary>
    public static List<StorageTitle> MoveOrder(IEnumerable<StorageTitle> titles, Func<StorageTitle, WatchRecord?>? watch) =>
        titles
            .Select(title => (Title: title, Watch: watch?.Invoke(title)))
            .OrderBy(entry => entry.Watch is { FavoriteOf.Count: > 0 } ? 1 : 0)
            .ThenBy(entry => entry.Watch?.StartedBy.Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0)
            .ThenBy(entry => entry.Watch?.LastWatchedUtc ?? DateTime.MinValue)
            .ThenBy(entry => entry.Title.SizeBytes)
            .Select(entry => entry.Title)
            .ToList();

    /// <summary>
    /// Movies and shows on full drives that are held back from suggestions,
    /// and why: still seeding, still airing, added recently, or too big.
    /// </summary>
    public static List<(StorageTitle Title, string Reason)> HeldBack(
        IEnumerable<StorageDrive> drives,
        Func<StorageTitle, WatchRecord?>? watch,
        long largestBytes,
        DateTime recentAfterUtc) =>
        drives
            .Where(drive => drive.IsAvailable && drive.IsOverLimit)
            .SelectMany(drive => drive.Titles)
            .Where(title => title.Kind is StorageKinds.Movies or StorageKinds.Tv)
            .Select(title => (
                Title: title,
                Reason:
                    title.IsSeeding ? "Still seeding"
                    : title.StaysForNewEpisodes ? "Still airing"
                    : IsRecent(title, watch?.Invoke(title), recentAfterUtc) ? "Added or changed recently"
                    : title.SizeBytes > largestBytes ? "Too big to move by itself"
                    : null))
            .Where(entry => entry.Reason is not null)
            .Select(entry => (entry.Title, entry.Reason!))
            .OrderBy(entry => entry.Item2)
            .ThenByDescending(entry => entry.Title.SizeBytes)
            .ToList();

    /// <summary>
    /// The library folders, by kind, that titles can be moved into.
    /// </summary>
    public static List<Destination> Destinations(IEnumerable<DriveProfile> profiles) =>
        profiles
            .Where(profile => profile.Enabled)
            .SelectMany(profile => new[]
            {
                (Kind: StorageKinds.Movies, Path: profile.MovieLibraryPath, Profile: profile),
                (Kind: StorageKinds.Tv, Path: profile.TvLibraryPath, Profile: profile)
            })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Path) && Directory.Exists(entry.Path))
            .Select(entry => new Destination(
                RootOf(entry.Path),
                entry.Profile.Name,
                entry.Kind,
                entry.Path))
            .ToList();

    public static string RootOf(string path) =>
        (Path.GetPathRoot(Path.GetFullPath(path)) ?? path).ToUpperInvariant();
}
