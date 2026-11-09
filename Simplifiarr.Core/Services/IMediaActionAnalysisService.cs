using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IMediaActionAnalysisService
{
    MediaActionAnalysis Analyze(
        ParsedRelease release,
        LibraryTarget target,
        AssetIdentityResult identity,
        bool isSeedOnly);
}
