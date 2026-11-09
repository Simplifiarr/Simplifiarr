using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class MediaActionAnalysisService
    : IMediaActionAnalysisService
{
    public MediaActionAnalysis Analyze(
        ParsedRelease release,
        LibraryTarget target,
        AssetIdentityResult identity,
        bool isSeedOnly)
    {
        if (isSeedOnly)
        {
            return CreateResult(
                release,
                MediaActionType.SeedOnly,
                "Torrent is classified as seed-only.");
        }

        if (string.IsNullOrWhiteSpace(target.TargetFilePath))
        {
            return CreateResult(
                release,
                MediaActionType.Unknown,
                "No compatible library target was resolved.");
        }

        if (identity.IsRepresented)
        {
            return CreateResult(
                release,
                MediaActionType.Healthy,
                $"Asset already represented. Matches: {identity.MatchingFileCount}");
        }

        return CreateResult(
            release,
            MediaActionType.Promote,
            identity.Reason);
    }

    private static MediaActionAnalysis CreateResult(
        ParsedRelease release,
        MediaActionType actionType,
        string reason)
    {
        return new MediaActionAnalysis
        {
            Title = release.NormalizedTitle,
            Year = release.Year,
            ActionType = actionType,
            Reason = reason
        };
    }
}
