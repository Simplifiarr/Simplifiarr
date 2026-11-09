namespace Simplifiarr.Core.Services;

public interface IQuarantineManagementService
{
    Task<bool> DeleteAsync(string fullPath);

    /// <summary>
    /// Moves a quarantined file back to its original library path,
    /// overwriting whatever currently sits there (normally the hardlink
    /// that replaced it). Fails if the quarantined file no longer exists.
    /// </summary>
    Task<bool> RestoreAsync(
        string quarantinePath,
        string originalLibraryPath);
}

