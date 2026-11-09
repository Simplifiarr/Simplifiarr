using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public class TorrentClassificationService
    : ITorrentClassificationService
{
    public TorrentAnalysis Analyze(
        TorrentRecord torrent,
        IReadOnlyList<DriveProfile> profiles)
    {
        if (torrent is null)
        {
            throw new ArgumentNullException(
                nameof(torrent));
        }

        ArgumentNullException.ThrowIfNull(profiles);

        var classification =
            TorrentClassification.Unknown;

        var reason =
            "No classification rules matched";

        var owningProfile =
            FindProfileForPath(
                torrent.SavePath,
                profiles);

        if (owningProfile is not null &&
            !string.IsNullOrWhiteSpace(owningProfile.SeedOnlyPath) &&
            PathContainment.IsPathUnderRoot(torrent.SavePath, owningProfile.SeedOnlyPath))
        {
            classification =
                TorrentClassification.SeedOnly;

            reason =
                $"Torrent stored under the {owningProfile.Name} seed-only path.";
        }

        return new TorrentAnalysis
        {
            TorrentId = torrent.Id,
            TorrentName = torrent.Name,
            Classification = classification,
            Reason = reason,
            SavePath = torrent.SavePath
        };
    }

    private static DriveProfile? FindProfileForPath(
        string path,
        IReadOnlyList<DriveProfile> profiles)
    {
        var pathRoot =
            string.IsNullOrWhiteSpace(path)
                ? null
                : Path.GetPathRoot(path);

        if (string.IsNullOrWhiteSpace(pathRoot))
        {
            return null;
        }

        return profiles.FirstOrDefault(profile =>
            string.Equals(
                Path.GetPathRoot(profile.DriveRoot),
                pathRoot,
                StringComparison.OrdinalIgnoreCase));
    }

}

