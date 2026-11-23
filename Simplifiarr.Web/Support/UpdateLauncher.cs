using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Win32;
using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;

namespace Simplifiarr.Web.Support;

/// <summary>
/// Finds which Windows service this is, and runs the new version's
/// installer silently over it. The installer stops the service, replaces
/// the files and starts it again; the service's port and data folder are
/// left as they are.
/// </summary>
[SupportedOSPlatform("windows")]
public class UpdateLauncher : IUpdateLauncher
{
    public InstallInfo Describe()
    {
        try
        {
            if (!WindowsServiceHelpers.IsWindowsService())
            {
                return new InstallInfo(false, "Simplifiarr isn't running as a Windows service here, so it can't update itself. Download the new version from the release page instead.", null, null);
            }

            var exe = Environment.ProcessPath;

            if (exe is null || FindService(exe) is not { } service)
            {
                return new InstallInfo(false, "Couldn't work out which Windows service this is, so it can't update itself.", null, null);
            }

            return new InstallInfo(true, null, service, Path.GetDirectoryName(exe));
        }
        catch (Exception exception)
        {
            return new InstallInfo(false, $"Couldn't check how Simplifiarr is installed: {exception.Message}", null, null);
        }
    }

    public void Launch(string setupPath, string logPath)
    {
        var install = Describe();

        if (!install.CanUpdateItself)
        {
            throw new InvalidOperationException(install.WhyNot);
        }

        var start = new ProcessStartInfo(setupPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(setupPath)!
        };

        foreach (var argument in new[]
                 {
                     "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-",
                     $"/DIR={install.InstallFolder}",
                     $"/SERVICE={install.ServiceName}",
                     $"/LOG={logPath}"
                 })
        {
            start.ArgumentList.Add(argument);
        }

        Process.Start(start);
    }

    /// <summary>The service whose program is <paramref name="exe"/>.</summary>
    private static string? FindService(string exe)
    {
        using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");

        if (services is null)
        {
            return null;
        }

        foreach (var name in services.GetSubKeyNames())
        {
            try
            {
                using var key = services.OpenSubKey(name);

                if (key?.GetValue("ImagePath") is string image && UpdateService.ProgramOf(image) is { } program &&
                    string.Equals(Path.GetFullPath(program), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }
            catch (Exception)
            {
                // A key we can't read, or a command line that isn't a path: not ours.
            }
        }

        return null;
    }
}
