using System.Diagnostics;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class HardlinkService : IHardlinkService
{
    public async Task<int?> GetHardlinkCountAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "fsutil",
                Arguments = $"hardlink list \"{filePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);

            if (process is null)
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync();

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                return null;
            }

            var links = output
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();

            return links.Count;
        }
        catch
        {
            return null;
        }
    }

    public async Task<HardlinkCreationResult> CreateHardlinkAsync(
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

            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd",
                Arguments = $"/c mklink /H \"{newLinkPath}\" \"{existingFilePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);

            if (process is null)
            {
                return new HardlinkCreationResult(false, "Unable to start the mklink process.");
            }

            var stdOut = await process.StandardOutput.ReadToEndAsync();
            var stdErr = await process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                RestoreInheritedPermissions(newLinkPath);

                return new HardlinkCreationResult(true, null);
            }

            var detail =
                string.Join(
                        " ",
                        new[] { stdErr, stdOut }
                            .Where(text => !string.IsNullOrWhiteSpace(text)))
                    .Trim();

            return new HardlinkCreationResult(
                false,
                string.IsNullOrWhiteSpace(detail)
                    ? $"mklink exited with code {process.ExitCode}."
                    : detail);
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
        catch
        {
            // Permissions are best effort; the hardlink itself already succeeded.
        }
    }

    public async Task<bool> AreHardlinkedAsync(
        string pathA,
        string pathB)
    {
        if (!File.Exists(pathA) || !File.Exists(pathB))
        {
            return false;
        }

        if (string.Equals(pathA, pathB, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootA = Path.GetPathRoot(pathA);
        var rootB = Path.GetPathRoot(pathB);

        if (string.IsNullOrWhiteSpace(rootA) ||
            !string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase))
        {
            // Hardlinks can't cross volumes - different drives can never
            // be the same underlying file.
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "fsutil",
                Arguments = $"hardlink list \"{pathA}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);

            if (process is null)
            {
                return false;
            }

            var output = await process.StandardOutput.ReadToEndAsync();

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                return false;
            }

            var relativeB =
                pathB[rootA.Length..].TrimStart('\\', '/');

            return output
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim().TrimStart('\\', '/'))
                .Any(link =>
                    string.Equals(link, relativeB, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}

