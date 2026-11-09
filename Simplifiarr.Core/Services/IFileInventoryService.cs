using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IFileInventoryService
{
    Task<IReadOnlyList<FileRecord>> GetInventoryAsync(
        string folderPath,
        bool forceRefresh = false);

    DateTime? GetLastScanUtc(
        string folderPath);
}
