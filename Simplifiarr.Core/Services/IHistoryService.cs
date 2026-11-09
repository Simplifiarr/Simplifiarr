using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IHistoryService
{
    Task<IReadOnlyList<HistoryRecord>> GetHistoryAsync();

    Task AddRecordAsync(
        HistoryRecord record);

    Task ClearHistoryAsync();
}
