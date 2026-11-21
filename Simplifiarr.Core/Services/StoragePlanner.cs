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

    public static List<StorageMoveSuggestion> Suggest(
        IReadOnlyList<StorageDrive> drives,
        IReadOnlyList<Destination> destinations)
    {
        var suggestions = new List<StorageMoveSuggestion>();

        // Space each drive would have once the suggestions so far are done.
        var used =
            drives.ToDictionary(drive => drive.Root, drive => drive.UsedBytes, StringComparer.OrdinalIgnoreCase);

        foreach (var drive in drives.Where(drive => drive.IsAvailable).OrderByDescending(drive => drive.OverLimitBytes))
        {
            var needed = used[drive.Root] - drive.LimitBytes;

            if (needed <= 0)
            {
                continue;
            }

            // Largest first: the fewest moves that free enough.
            foreach (var title in drive.Titles.Where(title => title.CanMove).OrderByDescending(title => title.SizeBytes))
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
                        .Where(pair => pair.Drive is { IsAvailable: true })
                        // It must still be under its own limit afterwards.
                        .Select(pair => (pair.Destination, Room: pair.Drive!.LimitBytes - used[pair.Drive.Root]))
                        .Where(pair => pair.Room >= title.SizeBytes)
                        .OrderByDescending(pair => pair.Room)
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
