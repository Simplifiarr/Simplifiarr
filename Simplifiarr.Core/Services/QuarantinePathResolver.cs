namespace Simplifiarr.Core.Services;

internal static class QuarantinePathResolver
{
    private const string DefaultQuarantineRoot =
        @"C:\ProgramData\Simplifiarr\Quarantine";

    public static async Task<string> ResolveAsync(
        ISettingsService settingsService)
    {
        var settings =
            await settingsService.GetSettingsAsync();

        return string.IsNullOrWhiteSpace(settings.QuarantinePath)
            ? DefaultQuarantineRoot
            : settings.QuarantinePath;
    }
}
