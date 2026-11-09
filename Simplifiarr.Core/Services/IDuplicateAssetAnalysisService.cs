using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IDuplicateAssetAnalysisService
{
    Task<List<DuplicateAssetGroup>> FindDuplicatesAsync(
        IReadOnlyCollection<MediaAsset> assets,
        IReadOnlyList<DriveProfile> profiles);
}
