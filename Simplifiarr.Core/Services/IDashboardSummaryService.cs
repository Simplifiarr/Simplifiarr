using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IDashboardSummaryService
{
    Task<DashboardSummary> GetDashboardSummaryAsync();
}

