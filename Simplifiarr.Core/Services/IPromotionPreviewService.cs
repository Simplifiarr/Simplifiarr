using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IPromotionPreviewService
{
    Task<PromotionPreview> CreateAsync(
        PromotionPlan plan);
}