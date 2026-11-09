using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public class HardlinkService : IHardlinkService
{
    public Task<int?> GetHardlinkCountAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return Task.FromResult<int?>(null);
        }

        return Task.FromResult(
            NativeHardlinks.GetIdentity(filePath)?.LinkCount);
    }

    public Task<HardlinkCreationResult> CreateHardlinkAsync(
        string existingFilePath,
        string newLinkPath)
    {
        return Task.FromResult(
            CreateHardlink(existingFilePath, newLinkPath));
    }

    private static HardlinkCreationResult CreateHardlink(
        string existingFilePath,
        string newLinkPath)
    {
        if (!File.Exists(existingFilePath))
        {
            return new HardlinkCreationResult(false, "Source file does not exist.");
        }

        if (File.Exists(newLinkPath))
        {
            return new HardlinkCreationResult(false, "A file already exists at the target path.");
        }

        try
        {
            var directory =
                Path.GetDirectoryName(newLinkPath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var error =
                NativeHardlinks.CreateHardlink(
                    existingFilePath,
                    newLinkPath);

            if (error is not null)
            {
                return new HardlinkCreationResult(false, error);
            }

            RestoreInheritedPermissions(newLinkPath);

            return new HardlinkCreationResult(true, null);
        }
        catch (Exception ex)
        {
            return new HardlinkCreationResult(false, ex.Message);
        }
    }

    // A hardlink shares one security descriptor with the download file, so a
    // download that carries restrictive permissions (for example one user account,
    // SYSTEM and Administrators only) leaves the library copy unreadable by the Jellyfin
    // service account. Re-enabling inheritance makes the file pick up the
    // library folder's access rules; because both names are the same file,
    // the download copy is repaired too.
    private static void RestoreInheritedPermissions(string filePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var fileInfo = new FileInfo(filePath);
            var security = fileInfo.GetAccessControl();

            security.SetAccessRuleProtection(
                isProtected: false,
                preserveInheritance: false);

            fileInfo.SetAccessControl(security);
        }
        catch (Exception exception)
        {
            // Permissions are best effort; the hardlink itself already succeeded.
            AppLog.Warn(
                $"Hardlink created, but its permissions could not be reset to inherit from the library folder: '{filePath}'.",
                exception);
        }
    }

    public Task<bool> AreHardlinkedAsync(
        string pathA,
        string pathB)
    {
        if (!File.Exists(pathA) || !File.Exists(pathB))
        {
            return Task.FromResult(false);
        }

        if (string.Equals(pathA, pathB, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(true);
        }

        var identityA = NativeHardlinks.GetIdentity(pathA);
        var identityB = NativeHardlinks.GetIdentity(pathB);

        // Two names are the same file exactly when Windows reports the same
        // file id on the same volume for both.
        return Task.FromResult(
            identityA is not null &&
            identityB is not null &&
            identityA.Value.VolumeSerialNumber == identityB.Value.VolumeSerialNumber &&
            identityA.Value.FileIndex == identityB.Value.FileIndex);
    }
}
