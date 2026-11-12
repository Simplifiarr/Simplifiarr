using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class PromotionPlanService
    : IPromotionPlanService
{
    private readonly ILibraryTargetResolver _libraryTargetResolver;
    private readonly IAssetIdentityService _assetIdentityService;
    private readonly IMediaActionAnalysisService _mediaActionAnalysisService;
    private readonly IMediaCatalogService _mediaCatalogService;

    public PromotionPlanService(
        ILibraryTargetResolver libraryTargetResolver,
        IAssetIdentityService assetIdentityService,
        IMediaActionAnalysisService mediaActionAnalysisService,
        IMediaCatalogService mediaCatalogService)
    {
        _libraryTargetResolver = libraryTargetResolver;
        _assetIdentityService = assetIdentityService;
        _mediaActionAnalysisService = mediaActionAnalysisService;
        _mediaCatalogService = mediaCatalogService;
    }

    public async Task<PromotionPlan> CreateAsync(
        ParsedRelease release,
        string sourcePath)
    {
        var existingAsset =
            release.IsTv
                ? _mediaCatalogService.FindTvEpisodeAsset(
                    release.NormalizedTitle,
                    release.Season,
                    release.Episode)
                : _mediaCatalogService.FindAssetByTitleAndYear(
                    release.NormalizedTitle,
                    release.Year);

        var target =
            await _libraryTargetResolver.ResolveAsync(
                release,
                sourcePath);

        if (target.KeptElsewhere is not null)
        {
            return new PromotionPlan
            {
                SourcePath = sourcePath,
                CanPromote = false,
                Reason = $"{release.IdentifiedBy} keeps this in {target.KeptElsewhere}, on another drive. A hardlink can't reach it; see Move Seeds."
            };
        }

        if (existingAsset is not null &&
            File.Exists(existingAsset.CanonicalPath))
        {
            target.TargetFilePath =
                existingAsset.CanonicalPath;

            target.TargetDirectory =
                Path.GetDirectoryName(
                    existingAsset.CanonicalPath)
                ?? target.TargetDirectory;

            target.FileExists = true;
            target.DirectoryExists =
                Directory.Exists(target.TargetDirectory);
        }

        var identity =
            await _assetIdentityService.AnalyzeAsync(
                release,
                target,
                sourcePath);

        var action =
            _mediaActionAnalysisService.Analyze(
                release,
                target,
                identity,
                false);

        return new PromotionPlan
        {
            SourcePath = sourcePath,
            TargetDirectory = target.TargetDirectory,
            TargetFilePath = target.TargetFilePath,
            CanPromote =
                action.ActionType == MediaActionType.Promote,
            Reason = existingAsset is null ? action.Reason : $"Catalog match: {existingAsset.Title}"
        };
    }
}

