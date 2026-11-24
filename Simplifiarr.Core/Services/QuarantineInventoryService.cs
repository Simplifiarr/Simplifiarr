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
        // The one in Settings, and one at the top of each other drive used.
        var roots =
            (await QuarantineService.AllRootsAsync(_settingsService))
                .Where(Directory.Exists)
                .ToList();

        var items = roots
            .SelectMany(root => Directory.EnumerateFiles(
                root,
                "*",
                SearchOption.AllDirectories))
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