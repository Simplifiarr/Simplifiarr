using System.Text.Json;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class SettingsService : ISettingsService
{
    private const string SettingsDirectory =
        @"C:\ProgramData\Simplifiarr";

    private const string SettingsFile =
        @"C:\ProgramData\Simplifiarr\settings.json";

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            WriteIndented = true
        };

    public async Task<SimplifiarrSettings> GetSettingsAsync()
    {
        EnsureSettingsExist();

        var json =
            await File.ReadAllTextAsync(SettingsFile);

        return JsonSerializer.Deserialize<SimplifiarrSettings>(
                   json,
                   JsonOptions)
               ?? CreateDefaultSettings();
    }

    public async Task<List<DriveProfile>> GetEnabledDriveProfilesAsync()
    {
        var settings =
            await GetSettingsAsync();

        return settings.DriveProfiles
            .Where(profile => profile.Enabled)
            .ToList();
    }

    public async Task SaveSettingsAsync(
        SimplifiarrSettings settings)
    {
        Directory.CreateDirectory(
            SettingsDirectory);

        var json =
            JsonSerializer.Serialize(
                settings,
                JsonOptions);

        await File.WriteAllTextAsync(
            SettingsFile,
            json);
    }

    private static void EnsureSettingsExist()
    {
        Directory.CreateDirectory(SettingsDirectory);

        if (File.Exists(SettingsFile))
        {
            return;
        }

        var settings =
            CreateDefaultSettings();

        var json =
            JsonSerializer.Serialize(
                settings,
                JsonOptions);

        File.WriteAllText(
            SettingsFile,
            json);
    }

    private static SimplifiarrSettings CreateDefaultSettings()
    {
        return new SimplifiarrSettings
        {
            QbittorrentUrl = "http://localhost:8080",

            QuarantinePath =
                @"C:\ProgramData\Simplifiarr\Quarantine",

            HistoryPath =
                @"C:\ProgramData\Simplifiarr\History\history.json"
        };
    }
}