namespace Simplifiarr.Core.Services;

public class QuarantineService : IQuarantineService
{
    /// <summary>
    /// The Quarantine folder at the top of a drive other than the one the
    /// Quarantine folder in Settings is on.
    /// </summary>
    public const string DriveFolderName = "Simplifiarr Quarantine";

    private readonly ISettingsService _settingsService;

    public QuarantineService(
        ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public Task<string?> QuarantineFileAsync(string path) =>
        MoveIntoAsync(_settingsService, path);

    /// <summary>
    /// Where something at <paramref name="path"/> is quarantined: the
    /// Quarantine folder in Settings when it is on that drive, otherwise
    /// "Simplifiarr Quarantine" at the top of the file's own drive. Either
    /// way it is a rename, never a copy: the drive's space doesn't change and
    /// nothing lands on another drive.
    /// </summary>
    public static string RootFor(string mainRoot, string path)
    {
        try
        {
            var drive = Path.GetPathRoot(Path.GetFullPath(path));

            return string.IsNullOrEmpty(drive) ||
                   string.Equals(drive, Path.GetPathRoot(Path.GetFullPath(mainRoot)), StringComparison.OrdinalIgnoreCase)
                ? mainRoot
                : Path.Combine(drive, DriveFolderName);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return mainRoot;
        }
    }

    /// <summary>
    /// Every Quarantine folder there is: the one in Settings and one at the
    /// top of each drive that has been used.
    /// </summary>
    public static async Task<List<string>> AllRootsAsync(ISettingsService settings)
    {
        var roots = new List<string> { await QuarantinePathResolver.ResolveAsync(settings) };

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && Directory.Exists(Path.Combine(drive.RootDirectory.FullName, DriveFolderName)))
                {
                    roots.Add(Path.Combine(drive.RootDirectory.FullName, DriveFolderName));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A drive that went away; nothing of ours is reachable there.
            }
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Moves a file, or a whole folder, into Quarantine on its own drive.
    /// Returns where it now is, or null when it couldn't be moved (nothing
    /// was changed then).
    /// </summary>
    public static async Task<string?> MoveIntoAsync(ISettingsService settings, string path)
    {
        var source = Path.TrimEndingDirectorySeparator(path);
        var isFolder = Directory.Exists(source);

        if (!isFolder && !File.Exists(source))
        {
            return null;
        }

        try
        {
            var main = await QuarantinePathResolver.ResolveAsync(settings);

            var folder =
                Path.Combine(
                    RootFor(main, source),
                    DateTime.UtcNow.ToString("yyyyMMdd"),
                    Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(folder);

            var target = Path.Combine(folder, Path.GetFileName(source));

            if (isFolder)
            {
                Directory.Move(source, target);
            }
            else
            {
                File.Move(source, target);
            }

            return target;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Utilities.AppLog.Warn($"Couldn't move {source} into Quarantine: {exception.Message}");

            return null;
        }
    }
}
