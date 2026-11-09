namespace Simplifiarr.Core.Services;

public class QuarantineService : IQuarantineService
{
    private readonly ISettingsService _settingsService;

    public QuarantineService(
        ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public async Task<string?> QuarantineFileAsync(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var quarantineRoot =
                await QuarantinePathResolver.ResolveAsync(_settingsService);

            var quarantineFolder =
                Path.Combine(
                    quarantineRoot,
                    DateTime.UtcNow.ToString("yyyyMMdd"),
                    Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(quarantineFolder);

            var quarantinePath =
                Path.Combine(
                    quarantineFolder,
                    Path.GetFileName(path));

            File.Move(path, quarantinePath);

            return quarantinePath;
        }
        catch
        {
            return null;
        }
    }
}