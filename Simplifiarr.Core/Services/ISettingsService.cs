using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface ISettingsService
{
    Task<SimplifiarrSettings> GetSettingsAsync();

    Task<List<DriveProfile>> GetEnabledDriveProfilesAsync();

    Task SaveSettingsAsync(
        SimplifiarrSettings settings);
}