using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

internal static class QuarantinePathResolver
{
    public static async Task<string> ResolveAsync(
        ISettingsService settingsService)
    {
        var settings =
            await settingsService.GetSettingsAsync();

        return string.IsNullOrWhiteSpace(settings.QuarantinePath)
            ? AppPaths.DefaultQuarantinePath
            : settings.QuarantinePath;
    }
}
