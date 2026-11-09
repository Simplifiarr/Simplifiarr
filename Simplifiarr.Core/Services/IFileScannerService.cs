using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IFileScannerService
{
    Task<List<FileRecord>> ScanAsync(string path);
}

