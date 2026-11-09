using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public class HistoryService : IHistoryService
{
    private readonly ISettingsService _settingsService;

    // History is appended to from several pages; without this two actions
    // finishing together could each read the old file and one would be lost.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public HistoryService(
        ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public async Task<IReadOnlyList<HistoryRecord>> GetHistoryAsync()
    {
        var historyFile =
            await GetHistoryFileAsync();

        return JsonFileStore.Load<List<HistoryRecord>>(historyFile)
               ?? [];
    }

    public async Task AddRecordAsync(
        HistoryRecord record)
    {
        var historyFile =
            await GetHistoryFileAsync();

        await _writeLock.WaitAsync();

        try
        {
            var history =
                JsonFileStore.Load<List<HistoryRecord>>(historyFile)
                ?? [];

            history.Add(record);

            JsonFileStore.Save(historyFile, history, indented: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task ClearHistoryAsync()
    {
        var historyFile =
            await GetHistoryFileAsync();

        await _writeLock.WaitAsync();

        try
        {
            JsonFileStore.Save(
                historyFile,
                new List<HistoryRecord>(),
                indented: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<string> GetHistoryFileAsync()
    {
        var settings =
            await _settingsService.GetSettingsAsync();

        return string.IsNullOrWhiteSpace(settings.HistoryPath)
            ? AppPaths.DefaultHistoryFile
            : settings.HistoryPath;
    }
}
