using System.Text.Json;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class HistoryService : IHistoryService
{
    private const string DefaultHistoryFile =
        @"C:\ProgramData\Simplifiarr\History\history.json";

    private readonly ISettingsService _settingsService;

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            WriteIndented = true
        };

    public HistoryService(
        ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public async Task<IReadOnlyList<HistoryRecord>> GetHistoryAsync()
    {
        var historyFile =
            await GetHistoryFileAsync();

        EnsureStorageExists(
            historyFile);

        if (!File.Exists(historyFile))
        {
            return Array.Empty<HistoryRecord>();
        }

        var json =
            await File.ReadAllTextAsync(historyFile);

        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<HistoryRecord>();
        }

        return JsonSerializer.Deserialize<List<HistoryRecord>>(
                   json,
                   JsonOptions)
               ?? new List<HistoryRecord>();
    }

    public async Task AddRecordAsync(
        HistoryRecord record)
    {
        var historyFile =
            await GetHistoryFileAsync();

        EnsureStorageExists(
            historyFile);

        var history =
            (await GetHistoryAsync()).ToList();

        history.Add(record);

        var json =
            JsonSerializer.Serialize(
                history,
                JsonOptions);

        await File.WriteAllTextAsync(
            historyFile,
            json);
    }

    public async Task ClearHistoryAsync()
    {
        var historyFile =
            await GetHistoryFileAsync();

        EnsureStorageExists(
            historyFile);

        await File.WriteAllTextAsync(
            historyFile,
            "[]");
    }

    private async Task<string> GetHistoryFileAsync()
    {
        var settings =
            await _settingsService.GetSettingsAsync();

        if (string.IsNullOrWhiteSpace(settings.HistoryPath))
        {
            return DefaultHistoryFile;
        }

        return settings.HistoryPath;
    }

    private static void EnsureStorageExists(
        string historyFile)
    {
        var directory =
            Path.GetDirectoryName(historyFile);

        if (string.IsNullOrWhiteSpace(directory))
        {
            directory =
                Path.GetDirectoryName(DefaultHistoryFile);
        }

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!File.Exists(historyFile))
        {
            File.WriteAllText(
                historyFile,
                "[]");
        }
    }
}