using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IQuarantineInventoryService
{
    Task<List<QuarantineItem>> GetItemsAsync();
}

