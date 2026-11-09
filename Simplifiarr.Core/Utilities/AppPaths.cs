namespace Simplifiarr.Core.Utilities;

/// <summary>
/// The one place that knows where Simplifiarr keeps its own files
/// (settings, history, caches, logs). Everything defaults to
/// %ProgramData%\Simplifiarr; <see cref="Configure"/> can point a second
/// install at a different folder so two copies never share data.
/// </summary>
public static class AppPaths
{
    private const string LegacyCacheFolderName = "PromotionCache";
    private const string DataFolderName = "Data";

    private static string _dataRoot = DefaultDataRoot();

    public static string DataRoot => _dataRoot;

    public static string SettingsFile =>
        Path.Combine(_dataRoot, "settings.json");

    /// <summary>
    /// Catalog, file inventory and every scan-result cache.
    /// </summary>
    public static string DataDirectory =>
        Path.Combine(_dataRoot, DataFolderName);

    public static string LogsDirectory =>
        Path.Combine(_dataRoot, "Logs");

    public static string DefaultQuarantinePath =>
        Path.Combine(_dataRoot, "Quarantine");

    public static string DefaultHistoryFile =>
        Path.Combine(_dataRoot, "History", "history.json");

    public static string DefaultCrossSeedTorrentPath =>
        Path.Combine(_dataRoot, "CrossSeedTorrents");

    public static string DataFile(string fileName) =>
        Path.Combine(DataDirectory, fileName);

    /// <summary>
    /// Call once at startup, before any service is created. A blank value
    /// keeps the default location.
    /// </summary>
    public static void Configure(string? dataRoot)
    {
        _dataRoot =
            string.IsNullOrWhiteSpace(dataRoot)
                ? DefaultDataRoot()
                : Path.GetFullPath(dataRoot.Trim());

        MigrateLegacyCacheFolder();
    }

    private static string DefaultDataRoot() =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData),
            "Simplifiarr");

    /// <summary>
    /// Versions before 2.0 kept everything under "PromotionCache", a name
    /// left over from when promotion candidates were the only thing cached.
    /// Rename it once so existing scan results and the catalog carry over.
    /// </summary>
    private static void MigrateLegacyCacheFolder()
    {
        try
        {
            var legacy = Path.Combine(_dataRoot, LegacyCacheFolderName);

            if (Directory.Exists(legacy) && !Directory.Exists(DataDirectory))
            {
                Directory.Move(legacy, DataDirectory);
            }
        }
        catch (Exception exception)
        {
            AppLog.Error(
                $"Could not rename the old '{LegacyCacheFolderName}' folder to '{DataFolderName}'. Scan results will be rebuilt on the next scan.",
                exception);
        }
    }
}
