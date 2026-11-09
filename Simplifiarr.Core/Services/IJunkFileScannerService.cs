using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IJunkFileScannerService
{
    IReadOnlyList<JunkFileCandidate> GetCandidates(
        string driveName);

    DateTime? GetLastScanUtc(
        string driveName);

    Task<List<JunkFileCandidate>> ScanAsync(
        string driveName);

    void RemoveCandidate(
        string filePath);
}
