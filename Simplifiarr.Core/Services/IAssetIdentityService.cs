using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IAssetIdentityService
{
    Task<AssetIdentityResult> AnalyzeAsync(
        ParsedRelease release,
        LibraryTarget target,
        string sourcePath);
}
