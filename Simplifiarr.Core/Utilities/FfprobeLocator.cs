namespace Simplifiarr.Core.Utilities;

/// <summary>
/// Finds ffprobe.exe. Simplifiarr usually runs as a Windows service under
/// the system account, which does not see the PATH of the person who
/// installed ffmpeg - so "ffprobe" alone often can't be started even though
/// it is on the machine. This looks in the places it is commonly installed.
/// </summary>
public static class FfprobeLocator
{
    private const string FileName = "ffprobe.exe";

    private static readonly object Sync = new();
    private static string? _configuredPath;
    private static string? _found;
    private static bool _searched;

    /// <summary>
    /// An explicit location from settings (the file itself, or the folder
    /// it is in). Checked before anything else.
    /// </summary>
    public static void Configure(string? configuredPath)
    {
        lock (Sync)
        {
            if (string.Equals(
                    _configuredPath ?? string.Empty,
                    configuredPath ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _configuredPath = configuredPath;
            _searched = false;
            _found = null;
        }
    }

    /// <summary>
    /// Full path to ffprobe.exe, or null if it isn't installed anywhere
    /// this looks. The search runs once; the answer is remembered.
    /// </summary>
    public static string? Find()
    {
        lock (Sync)
        {
            if (_searched)
            {
                return _found;
            }

            _searched = true;
            _found = Search(_configuredPath, GetCandidateFolders());

            if (_found is null)
            {
                AppLog.Warn(
                    "ffprobe was not found, so video resolution and codec can't be read. " +
                    "Install ffmpeg, or set FfprobePath in settings.json to where ffprobe.exe is.");
            }
            else
            {
                AppLog.Info($"Using ffprobe at '{_found}'.");
            }

            return _found;
        }
    }

    /// <summary>
    /// The search itself, separated so it can be tested with made-up folders.
    /// </summary>
    public static string? Search(
        string? configuredPath,
        IEnumerable<string> folders)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var configured = configuredPath.Trim().Trim('"');

            if (File.Exists(configured))
            {
                return Path.GetFullPath(configured);
            }

            var inFolder = Path.Combine(configured, FileName);

            if (File.Exists(inFolder))
            {
                return Path.GetFullPath(inFolder);
            }
        }

        foreach (var folder in folders)
        {
            try
            {
                var candidate = Path.Combine(folder, FileName);

                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch
            {
                // A malformed PATH entry is not worth failing over.
            }
        }

        return null;
    }

    private static IEnumerable<string> GetCandidateFolders()
    {
        // 1. Whatever PATH this process has.
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return folder.Trim();
        }

        // 2. Common machine-wide installs, including the copy that ships
        //    with Jellyfin.
        var programFiles =
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        yield return Path.Combine(programFiles, "ffmpeg", "bin");
        yield return Path.Combine(programFiles, "Jellyfin", "Server");
        yield return @"C:\ffmpeg\bin";
        yield return @"C:\ProgramData\chocolatey\bin";

        // 3. Per-user installs (winget, scoop) in any user's profile.
        //    The service's own profile is a system folder, so find the real
        //    Users folder from the shared "Public" profile next to them.
        var publicProfile =
            Environment.GetEnvironmentVariable("PUBLIC");

        var usersRoot =
            (string.IsNullOrWhiteSpace(publicProfile)
                ? null
                : Path.GetDirectoryName(publicProfile.TrimEnd(Path.DirectorySeparatorChar)))
            ?? @"C:\Users";

        foreach (var folder in PerUserFolders(usersRoot))
        {
            yield return folder;
        }
    }

    private static List<string> PerUserFolders(string usersRoot)
    {
        var folders = new List<string>();

        try
        {
            if (!Directory.Exists(usersRoot))
            {
                return folders;
            }

            foreach (var profile in Directory.EnumerateDirectories(usersRoot))
            {
                folders.Add(Path.Combine(profile, "scoop", "shims"));

                var wingetPackages =
                    Path.Combine(profile, "AppData", "Local", "Microsoft", "WinGet", "Packages");

                if (!Directory.Exists(wingetPackages))
                {
                    continue;
                }

                foreach (var package in Directory.EnumerateDirectories(wingetPackages, "*ffmpeg*"))
                {
                    foreach (var build in Directory.EnumerateDirectories(package))
                    {
                        folders.Add(Path.Combine(build, "bin"));
                    }
                }
            }
        }
        catch
        {
            // Another user's profile may be unreadable; just skip it.
        }

        return folders;
    }
}
