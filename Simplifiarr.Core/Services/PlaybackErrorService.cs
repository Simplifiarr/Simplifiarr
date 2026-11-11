using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

// Permissions and Windows marks only exist on Windows, which is all Simplifiarr runs on.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class PlaybackErrorService : IPlaybackErrorService
{
    /// <summary>The longest path older Windows programs can open.</summary>
    public const int MaxPathLength = 259;

    private static readonly string[] MediaExtensions =
    [
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".webm", ".mpg", ".mpeg",
        ".mp3", ".m4a", ".m4b", ".flac", ".ogg", ".opus", ".wav", ".aac", ".wma",
        ".epub", ".pdf", ".mobi", ".azw3", ".cbz", ".cbr", ".cb7"
    ];

    private static readonly string[] UnfinishedExtensions =
        [".!qb", ".part", ".partial", ".crdownload", ".!ut", ".aria2", ".downloading"];

    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    ];

    // Names of media server services, newest naming first.
    private static readonly string[] MediaServerServices =
        ["JellyfinServer", "Jellyfin", "EmbyServer", "Emby", "Plex Media Server", "PlexService"];

    private readonly DriveCandidateCache<PlaybackError> _cache =
        new(
            "playback-errors-cache.json",
            error => $"{error.Kind}|{error.Path}",
            error => File.Exists(error.Path));

    private readonly object _backupSync = new();

    private readonly ISettingsService _settings;
    private readonly IFileInventoryService _inventory;
    private readonly IHistoryService _history;

    public PlaybackErrorService(
        ISettingsService settings,
        IFileInventoryService inventory,
        IHistoryService history)
    {
        _settings = settings;
        _inventory = inventory;
        _history = history;
    }

    private static string BackupFile =>
        AppPaths.DataFile("permission-backups.json");

    // ------------------------------------------------------------------ Read

    public async Task<List<PlaybackError>> GetAllAsync() =>
        (await _settings.GetEnabledDriveProfilesAsync())
            .SelectMany(profile => _cache.GetCandidates(profile.Name))
            .ToList();

    public IReadOnlyList<PlaybackError> GetForDrive(
        string driveName) =>
        _cache.GetCandidates(driveName);

    public DateTime? GetLastScanUtc(
        string driveName) =>
        _cache.GetLastScanUtc(driveName);

    public bool CanFix(
        PlaybackErrorKind kind) =>
        kind is PlaybackErrorKind.Blocked
            or PlaybackErrorKind.MediaServerCantRead
            or PlaybackErrorKind.PermissionsDontMatchFolder
            or PlaybackErrorKind.Packed;

    public async Task<string?> GetMediaServerAccountAsync()
    {
        var settings = await _settings.GetSettingsAsync();

        if (!string.IsNullOrWhiteSpace(settings.MediaServerAccount))
        {
            return settings.MediaServerAccount.Trim();
        }

        return FindMediaServerServiceAccount();
    }

    public async Task<string?> FindSevenZipAsync()
    {
        var settings = await _settings.GetSettingsAsync();

        return FindSevenZip(settings.SevenZipPath);
    }

    // ------------------------------------------------------------------ Scan

    public async Task<List<PlaybackError>> ScanAsync(
        string driveName)
    {
        var settings = await _settings.GetSettingsAsync();

        var profile =
            settings.DriveProfiles.FirstOrDefault(candidate =>
                candidate.Enabled &&
                string.Equals(candidate.Name, driveName, StringComparison.OrdinalIgnoreCase));

        if (profile is null)
        {
            return [];
        }

        var account = await GetMediaServerAccountAsync();
        var identities = FilePermissions.IdentitiesFor(account);

        var libraries =
            new[]
            {
                profile.MovieLibraryPath,
                profile.TvLibraryPath,
                profile.BookLibraryPath,
                profile.AudiobookLibraryPath,
                profile.MusicLibraryPath,
                profile.ComicLibraryPath
            }
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Downloads are what torrents seed from; they are never reported
        // here, even if a library folder is set wide enough to contain one.
        var downloads =
            new[]
            {
                profile.DownloadPath,
                profile.SeedOnlyPath,
                profile.AudiobookDownloadPath,
                profile.MusicDownloadPath,
                profile.ComicDownloadPath
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();

        var errors = new List<PlaybackError>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folderSecurity = new Dictionary<string, DirectorySecurity?>(StringComparer.OrdinalIgnoreCase);

        foreach (var library in libraries)
        {
            // Comic readers open .zip, .rar and .7z comics directly, so in
            // a comic library an archive is the real format, not a packed
            // release.
            var isComicLibrary =
                string.Equals(library, profile.ComicLibraryPath, StringComparison.OrdinalIgnoreCase);

            var files =
                await _inventory.GetInventoryAsync(library, forceRefresh: true);

            var filesByFolder =
                files
                    .GroupBy(file => Path.GetDirectoryName(file.FullPath) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Select(file => (file.FullPath, file.SizeBytes)).ToList(),
                        StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                if (!seen.Add(file.FullPath) ||
                    downloads.Any(folder => PathContainment.IsPathUnderRoot(file.FullPath, folder)))
                {
                    continue;
                }

                CheckFile(
                    file,
                    profile.Name,
                    identities,
                    account,
                    folderSecurity,
                    filesByFolder,
                    isComicLibrary,
                    errors);
            }
        }

        _cache.SetCandidates(profile.Name, errors);

        return errors;
    }

    private static void CheckFile(
        FileRecord file,
        string driveName,
        IReadOnlyCollection<SecurityIdentifier>? identities,
        string? account,
        Dictionary<string, DirectorySecurity?> folderSecurity,
        Dictionary<string, List<(string Path, long Size)>> filesByFolder,
        bool isComicLibrary,
        List<PlaybackError> errors)
    {
        void Add(PlaybackErrorKind kind, string detail, long? size = null, string? sourceUrl = null) =>
            errors.Add(
                new PlaybackError
                {
                    Kind = kind,
                    Path = file.FullPath,
                    FileName = file.FileName,
                    SizeBytes = size ?? file.SizeBytes,
                    DriveProfileName = driveName,
                    Detail = detail,
                    SourceUrl = sourceUrl,
                    DiscoveredUtc = DateTime.UtcNow
                });

        var extension = Path.GetExtension(file.FullPath).ToLowerInvariant();
        var folder = Path.GetDirectoryName(file.FullPath) ?? string.Empty;

        // ---- Names and paths: any file, since a player may be asked for it.
        if (file.FullPath.Length > MaxPathLength)
        {
            Add(
                PlaybackErrorKind.PathTooLong,
                $"The full path is {file.FullPath.Length} characters. Many programs, including FFmpeg, can't open paths over {MaxPathLength}.");
        }

        if (BadNamePart(file.FullPath) is { } badPart)
        {
            Add(PlaybackErrorKind.BadName, badPart);
        }

        // ---- Unfinished downloads.
        if (UnfinishedExtensions.Contains(extension))
        {
            Add(
                PlaybackErrorKind.Unfinished,
                $"A {extension} file is a download that never finished. Re-download it, or remove it once the finished file is there.");

            return;
        }

        // ---- Packed releases: report the set once, by its first part.
        if (ArchiveSets.IsArchivePart(file.FullPath))
        {
            if (ArchiveSets.IsFirstVolume(file.FullPath) && !isComicLibrary && !IsBookArchive(extension))
            {
                var setSize =
                    ArchiveSets.SetSize(
                        file.FullPath,
                        filesByFolder.TryGetValue(folder, out var siblings) ? siblings : []);

                Add(
                    PlaybackErrorKind.Packed,
                    extension is ".iso" or ".img"
                        ? "A disc image. Most players, including Jellyfin on most devices, can't play what's inside."
                        : "The release is still packed in an archive. Media servers can't play what's inside until it is unpacked.",
                    setSize);
            }

            return;
        }

        if (!MediaExtensions.Contains(extension))
        {
            return;
        }

        // ---- Media files: what Windows thinks of them.
        if (file.SizeBytes == 0)
        {
            Add(PlaybackErrorKind.Empty, "The file has nothing in it. Replace it with a fresh download.");
        }

        FileAttributes attributes;

        try
        {
            attributes = File.GetAttributes(file.FullPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if ((attributes & FileAttributes.Encrypted) != 0)
        {
            Add(
                PlaybackErrorKind.Encrypted,
                "Encrypted with Windows (EFS). Only the Windows user who encrypted it can open it, so a media server can't.");
        }

        const FileAttributes recallOnOpen = (FileAttributes)0x40000;
        const FileAttributes recallOnDataAccess = (FileAttributes)0x400000;

        if ((attributes & (FileAttributes.Offline | recallOnOpen | recallOnDataAccess)) != 0)
        {
            Add(
                PlaybackErrorKind.CloudOnly,
                "Only a placeholder is on the disk; the real file is in the cloud (OneDrive or similar). Set it to \"Always keep on this device\".");
        }

        if (ZoneIdentifierHelper.IsBlocked(file.FullPath))
        {
            var source = ZoneIdentifierHelper.TryReadSourceUrl(file.FullPath);

            Add(
                PlaybackErrorKind.Blocked,
                "Windows has marked it as downloaded from the internet or copied from another computer. Some players refuse to open it.",
                sourceUrl: source);
        }

        // ---- Permissions.
        FileSecurity security;

        try
        {
            security = new FileInfo(file.FullPath).GetAccessControl();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return;
        }

        if (identities is not null && !FilePermissions.CanRead(security, identities))
        {
            Add(
                PlaybackErrorKind.MediaServerCantRead,
                $"Its permissions don't let {account} (the media server) open it, so it won't play.");

            return;
        }

        if (!folderSecurity.TryGetValue(folder, out var parent))
        {
            try
            {
                parent = new DirectoryInfo(folder).GetAccessControl();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                parent = null;
            }

            folderSecurity[folder] = parent;
        }

        if (parent is not null && !FilePermissions.MatchesFolder(security, parent))
        {
            Add(
                PlaybackErrorKind.PermissionsDontMatchFolder,
                security.AreAccessRulesProtected
                    ? "It doesn't take its folder's permissions (inheritance is turned off), so changes to the folder don't reach it."
                    : "Its permissions don't match its folder's - usually because it was moved here from somewhere else and kept that place's permissions.");
        }
    }

    // Comic and book libraries keep .cbz/.cbr/.zip as the real format.
    private static bool IsBookArchive(string extension) =>
        extension is ".cbz" or ".cbr" or ".cb7";

    /// <summary>
    /// A sentence about the first part of the path Windows programs will
    /// struggle with, or null.
    /// </summary>
    public static string? BadNamePart(
        string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;

        foreach (var part in path[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.EndsWith(' ') || part.EndsWith('.'))
            {
                return $"\"{part}\" ends with a {(part.EndsWith(' ') ? "space" : "dot")}. Windows programs often can't open names like that. Renaming it in Radarr or Sonarr fixes it.";
            }

            var stem = part.Split('.')[0].TrimEnd();

            if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
            {
                return $"\"{part}\" uses {stem.ToUpperInvariant()}, a name Windows reserves. Most programs can't open it. Rename it in Radarr or Sonarr.";
            }
        }

        return null;
    }

    // ------------------------------------------------------------------- Fix

    public async Task<PlaybackFixResult> FixAsync(
        PlaybackError error,
        CancellationToken cancellationToken = default)
    {
        var result =
            error.Kind switch
            {
                PlaybackErrorKind.Blocked => Unblock(error),
                PlaybackErrorKind.MediaServerCantRead or PlaybackErrorKind.PermissionsDontMatchFolder =>
                    await FixPermissionsAsync(error),
                PlaybackErrorKind.Packed => await ExtractAsync(error, cancellationToken),
                _ => new PlaybackFixResult(false, "Simplifiarr can't fix this one itself.")
            };

        if (result.Succeeded)
        {
            _cache.RemoveCandidate($"{error.Kind}|{error.Path}");
        }

        await _history.AddRecordAsync(
            new HistoryRecord
            {
                ActionType = HistoryActionType.PlaybackErrorFixed,
                FileName = error.FileName,
                LibraryPath = error.Path,
                SizeBytes = error.SizeBytes,
                Success = result.Succeeded,
                Message = result.Message
            });

        return result;
    }

    private static PlaybackFixResult Unblock(
        PlaybackError error) =>
        ZoneIdentifierHelper.Unblock(error.Path)
            ? new PlaybackFixResult(true, $"Removed Windows' download mark from {error.FileName}.")
            : new PlaybackFixResult(false, $"Couldn't remove the mark from {error.FileName}. {ServiceNote}");

    private const string ServiceNote =
        "Changing permissions needs Simplifiarr to run as a Windows service (installed with scripts\\install-service.ps1), which has the rights to do it.";

    private async Task<PlaybackFixResult> FixPermissionsAsync(
        PlaybackError error)
    {
        string before;

        try
        {
            before = FilePermissions.Reset(error.Path);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PrivilegeNotHeldException or InvalidOperationException)
        {
            return new PlaybackFixResult(false, $"Windows didn't allow the change to {error.FileName}. {ServiceNote}");
        }
        catch (Exception exception) when (exception is IOException)
        {
            return new PlaybackFixResult(false, $"Couldn't change {error.FileName}: {exception.Message}");
        }

        SaveBackup(error.Path, before);

        // Taking the folder's permissions is normally enough. If even the
        // folder doesn't let the media server in, give it read access to
        // this one file.
        var account = await GetMediaServerAccountAsync();
        var identities = FilePermissions.IdentitiesFor(account);

        if (identities is not null &&
            !FilePermissions.CanRead(new FileInfo(error.Path).GetAccessControl(), identities))
        {
            try
            {
                FilePermissions.GrantRead(error.Path, identities.First());
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or PrivilegeNotHeldException or IOException)
            {
                return new PlaybackFixResult(
                    false,
                    $"{error.FileName} now takes its folder's permissions, but the folder itself doesn't let {account} read it, and adding that wasn't allowed. {ServiceNote}");
            }

            return new PlaybackFixResult(
                true,
                $"{error.FileName} now takes its folder's permissions, and {account} was given read access to it.");
        }

        return new PlaybackFixResult(true, $"{error.FileName} now takes its folder's permissions.");
    }

    public async Task<PlaybackFixResult> UndoPermissionsAsync(
        string path)
    {
        Dictionary<string, string> backups;

        lock (_backupSync)
        {
            backups = LoadBackups();
        }

        if (!backups.TryGetValue(path, out var sddl))
        {
            return new PlaybackFixResult(false, "There are no saved permissions for that file.");
        }

        PlaybackFixResult result;

        try
        {
            FilePermissions.Restore(path, sddl);

            lock (_backupSync)
            {
                var current = LoadBackups();

                current.Remove(path);

                JsonFileStore.Save(BackupFile, current);
            }

            result = new PlaybackFixResult(true, $"Put back the earlier permissions on {Path.GetFileName(path)}.");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PrivilegeNotHeldException or IOException or InvalidOperationException)
        {
            result = new PlaybackFixResult(false, $"Couldn't put the permissions back: {exception.Message} {ServiceNote}");
        }

        await _history.AddRecordAsync(
            new HistoryRecord
            {
                ActionType = HistoryActionType.PermissionsRestored,
                FileName = Path.GetFileName(path),
                LibraryPath = path,
                Success = result.Succeeded,
                Message = result.Message
            });

        return result;
    }

    public IReadOnlyCollection<string> GetUndoablePaths()
    {
        lock (_backupSync)
        {
            return LoadBackups().Keys.ToList();
        }
    }

    private void SaveBackup(
        string path,
        string sddl)
    {
        lock (_backupSync)
        {
            var backups = LoadBackups();

            // Keep the first one: that is what the file had before
            // Simplifiarr ever touched it.
            backups.TryAdd(path, sddl);

            JsonFileStore.Save(BackupFile, backups);
        }
    }

    private static Dictionary<string, string> LoadBackups() =>
        new(
            JsonFileStore.Load<Dictionary<string, string>>(BackupFile) ?? [],
            StringComparer.OrdinalIgnoreCase);

    // --------------------------------------------------------------- Unpack

    private async Task<PlaybackFixResult> ExtractAsync(
        PlaybackError error,
        CancellationToken cancellationToken)
    {
        var sevenZip = await FindSevenZipAsync();

        if (sevenZip is null)
        {
            return new PlaybackFixResult(
                false,
                "7-Zip isn't installed, so archives can't be unpacked. Install it from 7-zip.org, or set where 7z.exe is in Settings, under Storage.");
        }

        var folder = Path.GetDirectoryName(error.Path)!;

        // Look inside first: a password-protected or broken archive is
        // reported, not half-unpacked.
        var listing =
            await RunSevenZipAsync(sevenZip, ["l", "-slt", "-pSIMPLIFIARR_NO_PASSWORD", error.Path], cancellationToken);

        if (listing.ExitCode != 0)
        {
            return new PlaybackFixResult(
                false,
                $"7-Zip couldn't read {error.FileName}. It may be incomplete, damaged or password-protected; re-download the release.");
        }

        var contents = ParseListing(listing.Output);

        if (contents.Count == 0)
        {
            return new PlaybackFixResult(false, $"{error.FileName} has nothing in it to unpack.");
        }

        var clashes =
            contents
                .Where(entry => File.Exists(Path.Combine(folder, entry.Name)))
                .Select(entry => entry.Name)
                .ToList();

        if (clashes.Count > 0)
        {
            return new PlaybackFixResult(
                false,
                $"{clashes[0]} is already in that folder, so nothing was unpacked. It may have been unpacked before; if so, the archive can be removed.");
        }

        var needed = contents.Sum(entry => entry.Size);
        var free = new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace;

        if (needed > free)
        {
            return new PlaybackFixResult(
                false,
                $"Unpacking needs {SizeFormatter.FormatBytes(needed)} free on that drive, and there is {SizeFormatter.FormatBytes(free)}.");
        }

        // -aos: never overwrite anything already there.
        var run =
            await RunSevenZipAsync(
                sevenZip,
                ["x", error.Path, "-o" + folder, "-aos", "-bd", "-pSIMPLIFIARR_NO_PASSWORD"],
                cancellationToken);

        if (run.ExitCode != 0)
        {
            return new PlaybackFixResult(
                false,
                $"7-Zip stopped while unpacking {error.FileName} (code {run.ExitCode}). Anything it had written is still in the folder; check it before trying again.");
        }

        var names = string.Join(", ", contents.Select(entry => entry.Name).Take(3));

        return new PlaybackFixResult(
            true,
            $"Unpacked {names}{(contents.Count > 3 ? $" and {contents.Count - 3} more" : string.Empty)} next to the archive. The archive was left where it is, in case a torrent is still seeding it.");
    }

    private static async Task<(int ExitCode, string Output)> RunSevenZipAsync(
        string sevenZip,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        var start =
            new ProcessStartInfo(sevenZip)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;

        // Nothing is ever typed into it, so it can't wait on a question.
        process.StandardInput.Close();

        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already ended.
            }

            throw;
        }

        return (process.ExitCode, await output + await errors);
    }

    /// <summary>
    /// The files (not folders) in 7-Zip's technical listing.
    /// </summary>
    public static List<(string Name, long Size)> ParseListing(
        string output)
    {
        var entries = new List<(string Name, long Size)>();

        var start = output.IndexOf("----------", StringComparison.Ordinal);

        if (start < 0)
        {
            return entries;
        }

        string? path = null;
        long size = 0;
        var isFolder = false;

        void Flush()
        {
            if (path is not null && !isFolder)
            {
                entries.Add((path.Replace('/', '\\'), size));
            }

            path = null;
            size = 0;
            isFolder = false;
        }

        foreach (var raw in output[start..].Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.Length == 0)
            {
                Flush();
            }
            else if (line.StartsWith("Path = ", StringComparison.Ordinal))
            {
                path = line[7..];
            }
            else if (line.StartsWith("Size = ", StringComparison.Ordinal))
            {
                long.TryParse(line[7..], out size);
            }
            else if (line.StartsWith("Folder = +", StringComparison.Ordinal) ||
                     line.StartsWith("Attributes = D", StringComparison.Ordinal))
            {
                isFolder = true;
            }
        }

        Flush();

        return entries;
    }

    // ---------------------------------------------------------------- Tools

    private static string? FindSevenZip(
        string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var candidate =
                Directory.Exists(configured)
                    ? Path.Combine(configured, "7z.exe")
                    : configured;

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var candidate = Path.Combine(root, "7-Zip", "7z.exe");

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// The logon account of the Jellyfin, Emby or Plex Windows service.
    /// </summary>
    private static string? FindMediaServerServiceAccount()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        // Windows keeps each service's account in the registry as ObjectName.
        try
        {
            using var services =
                Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");

            foreach (var name in MediaServerServices)
            {
                using var service = services?.OpenSubKey(name);

                if (service?.GetValue("ObjectName") is string account &&
                    !string.IsNullOrWhiteSpace(account))
                {
                    return account;
                }
            }
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            AppLog.Warn("Couldn't look up the media server's Windows service.", exception);
        }

        return null;
    }
}
