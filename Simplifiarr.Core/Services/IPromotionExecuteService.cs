using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IPromotionExecuteService
{
    Task<PromotionExecutionResult> ExecuteAsync(
        PromotionPlan plan);
}
