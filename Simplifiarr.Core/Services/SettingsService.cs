using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public class SettingsService : ISettingsService
{
    public Task<SimplifiarrSettings> GetSettingsAsync()
    {
        var settings =
            JsonFileStore.Load<SimplifiarrSettings>(AppPaths.SettingsFile);

        if (settings is null)
        {
            settings = new SimplifiarrSettings();

            // First run (or an unreadable file): write the defaults out so
            // there is a file to look at - but never overwrite one that
            // exists and merely failed to parse.
            if (!File.Exists(AppPaths.SettingsFile))
            {
                JsonFileStore.Save(
                    AppPaths.SettingsFile,
                    settings,
                    indented: true);
            }
        }

        return Task.FromResult(settings);
    }

    public async Task<List<DriveProfile>> GetEnabledDriveProfilesAsync()
    {
        var settings =
            await GetSettingsAsync();

        return settings.DriveProfiles
            .Where(profile => profile.Enabled)
            .ToList();
    }

    public Task SaveSettingsAsync(
        SimplifiarrSettings settings)
    {
        if (!JsonFileStore.Save(
                AppPaths.SettingsFile,
                settings,
                indented: true))
        {
            // The Settings page shows this to the user, so it must not be
            // swallowed the way a failed cache save is.
            throw new IOException(
                $"Could not save settings to '{AppPaths.SettingsFile}'. Check that the folder exists and is writable.");
        }

        return Task.CompletedTask;
    }
}
