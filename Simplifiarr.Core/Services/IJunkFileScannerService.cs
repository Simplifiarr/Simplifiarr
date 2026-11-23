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

    /// <summary>Check results against these rules from now on (just saved on the page).</summary>
    void UseRules(
        JunkRules rules);

    void RemoveCandidate(
        string filePath);
}
