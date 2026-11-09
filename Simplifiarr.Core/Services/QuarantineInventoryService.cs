using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class QuarantineInventoryService : IQuarantineInventoryService
{
    private readonly ISettingsService _settingsService;

    public QuarantineInventoryService(
        ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public async Task<List<QuarantineItem>> GetItemsAsync()
    {
        var quarantineRoot =
            await QuarantinePathResolver.ResolveAsync(_settingsService);

        if (!Directory.Exists(quarantineRoot))
        {
            return new List<QuarantineItem>();
        }

        var items = Directory
            .EnumerateFiles(
                quarantineRoot,
                "*",
                SearchOption.AllDirectories)
            .Select(path =>
            {
                var info = new FileInfo(path);

                return new QuarantineItem
                {
                    FileName = info.Name,
                    FullPath = info.FullName,
                    SizeBytes = info.Length,
                    CreatedUtc = info.CreationTimeUtc
                };
            })
            .OrderByDescending(item => item.CreatedUtc)
            .ToList();

        return items;
    }
}