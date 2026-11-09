using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IFileScannerService
{
    /// <summary>
    /// Lists every file under <paramref name="path"/>.
    /// <paramref name="onProgress"/> is told how many files have been read
    /// so far, every few hundred files and once more at the end.
    /// </summary>
    Task<List<FileRecord>> ScanAsync(
        string path,
        CancellationToken cancellationToken = default,
        Action<int>? onProgress = null);
}
