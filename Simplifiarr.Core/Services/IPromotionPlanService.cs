using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IPromotionPlanService
{
    Task<PromotionPlan> CreateAsync(
        ParsedRelease release,
        string sourcePath);
}