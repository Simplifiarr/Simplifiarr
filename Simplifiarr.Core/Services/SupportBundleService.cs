using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public interface ISupportBundleService
{
    /// <summary>
    /// A zip to attach to a problem report: the last week of logs, a short
    /// description of the setup, Settings without its secrets, and the
    /// actions that didn't work. Keys, passwords, IP addresses, emails and
    /// the Windows user name are always taken out; with
    /// <paramref name="hideNames"/>, titles, people and file names too.
    /// </summary>
    Task<byte[]> CreateAsync(bool hideNames);

    /// <summary>The newest lines of the log, keys and the like taken out.</summary>
    IReadOnlyList<string> RecentLines(int count, bool problemsOnly);
}

public sealed class SupportBundleService : ISupportBundleService
{
    private const int DaysOfLogs = 7;

    private readonly ISettingsService _settings;
    private readonly IStorageService _storage;
    private readonly IWatchHistoryService _watch;
    private readonly IHistoryService _history;
    private readonly string _version;

    public SupportBundleService(
        ISettingsService settings,
        IStorageService storage,
        IWatchHistoryService watch,
        IHistoryService history,
        IUpdateService updates)
        : this(settings, storage, watch, history, updates.CurrentVersion)
    {
    }

    public SupportBundleService(
        ISettingsService settings,
        IStorageService storage,
        IWatchHistoryService watch,
        IHistoryService history,
        string version)
    {
        _settings = settings;
        _storage = storage;
        _watch = watch;
        _history = history;
        _version = version;
    }

    /// <summary>For tests: where the log files are.</summary>
    public string LogsDirectory { get; set; } = AppPaths.LogsDirectory;

    public async Task<byte[]> CreateAsync(bool hideNames)
    {
        var settings = await _settings.GetSettingsAsync();
        var drives = await _storage.GetDrivesAsync();

        var redactor =
            hideNames
                ? new LogRedactor(
                    LogSwitch.Secrets(settings),
                    Environment.UserName,
                    drives.SelectMany(drive => drive.Titles).Select(title => title.Name),
                    People())
                : new LogRedactor(LogSwitch.Secrets(settings), Environment.UserName);

        using var memory = new MemoryStream();

        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "README.txt", Readme(hideNames));
            Add(zip, "about.txt", redactor.Clean(About(settings, drives)));
            Add(zip, "settings.json", redactor.Clean(SettingsWithoutSecrets(settings)));
            Add(zip, "problems.txt", redactor.Clean(await ProblemsAsync()));

            foreach (var log in LogFiles())
            {
                Add(zip, "logs/" + Path.GetFileName(log), redactor.Clean(ReadShared(log)));
            }
        }

        return memory.ToArray();
    }

    public IReadOnlyList<string> RecentLines(int count, bool problemsOnly)
    {
        var lines = new List<string>();

        // Newest file first, until there are enough lines.
        foreach (var log in LogFiles().OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var entries = Entries(ReadShared(log));

            if (problemsOnly)
            {
                entries = entries.Where(entry => entry.Contains("[Warning]") || entry.Contains("[Error]") || entry.Contains("[Critical]")).ToList();
            }

            lines.InsertRange(0, entries);

            if (lines.Count >= count)
            {
                break;
            }
        }

        return lines.TakeLast(count).Select(LogSwitch.Clean).ToList();
    }

    // A log entry is its first line plus any stack trace lines under it.
    private static List<string> Entries(string text)
    {
        var entries = new List<string>();

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');

            if (trimmed.Length > 20 && char.IsDigit(trimmed[0]) && trimmed[4] == '-')
            {
                entries.Add(trimmed);
            }
            else if (entries.Count > 0 && trimmed.Length > 0)
            {
                entries[^1] += "\n" + trimmed;
            }
        }

        return entries;
    }

    private IEnumerable<string> LogFiles()
    {
        if (!Directory.Exists(LogsDirectory))
        {
            return [];
        }

        var cutoff = DateTime.Now.AddDays(-DaysOfLogs);

        return Directory.EnumerateFiles(LogsDirectory, "*.log")
            .Where(path => File.GetLastWriteTime(path) >= cutoff)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // The logger may be writing to today's file right now.
    private static string ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            return reader.ReadToEnd();
        }
        catch (IOException exception)
        {
            return $"(couldn't read this log: {exception.Message})";
        }
    }

    private IEnumerable<string> People() =>
        _watch.Records
            .SelectMany(record =>
                record.StartedBy
                    .Concat(record.WatchedBy)
                    .Concat(record.FavoriteOf)
                    .Concat(record.RequestedBy is null ? [] : [record.RequestedBy]))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private string About(SimplifiarrSettings settings, List<StorageDrive> drives)
    {
        var text = new StringBuilder();

        text.AppendLine($"Simplifiarr {_version}");
        text.AppendLine($"Made {DateTime.Now:yyyy-MM-dd HH:mm} (UTC{DateTimeOffset.Now:zzz})");
        text.AppendLine($"Windows: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        text.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}");
        text.AppendLine($"Runs as: {(Environment.UserName.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) || Environment.UserName.EndsWith('$') ? "Windows service" : "a signed-in user")}");
        text.AppendLine($"Data folder: {AppPaths.DataRoot}");
        text.AppendLine($"Detailed logging: {(LogSwitch.IsDetailed ? $"on until {LogSwitch.DetailedUntilUtc!.Value.ToLocalTime():yyyy-MM-dd HH:mm}" : "off")}");
        text.AppendLine();

        text.AppendLine("Connections (set up or not):");
        text.AppendLine($"  Torrent client: {SetUp(settings.TorrentClientUrl)}");
        text.AppendLine($"  Radarr: {SetUp(settings.RadarrUrl, settings.RadarrApiKey)}");
        text.AppendLine($"  Sonarr: {SetUp(settings.SonarrUrl, settings.SonarrApiKey)}");
        text.AppendLine($"  Prowlarr: {SetUp(settings.ProwlarrUrl, settings.ProwlarrApiKey)}");
        text.AppendLine($"  Seerr: {SetUp(settings.SeerrUrl, settings.SeerrApiKey)}");
        text.AppendLine($"  Jellyfin: {SetUp(settings.JellyfinUrl, settings.JellyfinApiKey)}");
        text.AppendLine($"  Watch history last read: {(_watch.RefreshedUtc is { } read ? read.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "never")}");
        text.AppendLine();

        text.AppendLine($"Drive entries: {settings.DriveProfiles.Count} ({settings.DriveProfiles.Count(profile => profile.Enabled)} on)");

        foreach (var drive in drives)
        {
            text.AppendLine(
                drive.IsAvailable
                    ? $"  {drive.Root} {(drive.IsProtected ? "protected" : "overflow")}: {drive.UsedPercent:0}% used, {SizeFormatter.FormatBytes(drive.FreeBytes)} free of {SizeFormatter.FormatBytes(drive.TotalBytes)}, {drive.Titles.Count} titles"
                    : $"  {drive.Root}: not found");
        }

        return text.ToString();
    }

    private static string SetUp(string? url, string? key = "x") =>
        string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key) ? "no" : "yes";

    // Every secret is replaced before the settings are written out at all.
    private static string SettingsWithoutSecrets(SimplifiarrSettings settings)
    {
        var json = JsonSerializer.SerializeToNode(settings)!.AsObject();

        foreach (var name in json.Select(pair => pair.Key).ToList())
        {
            if (name.Contains("Key", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Secret", StringComparison.OrdinalIgnoreCase))
            {
                json[name] = string.IsNullOrEmpty(json[name]?.ToString()) ? "" : LogRedactor.Hidden;
            }
        }

        return json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task<string> ProblemsAsync()
    {
        var failed =
            (await _history.GetHistoryAsync())
                .Where(record => !record.Success)
                .OrderByDescending(record => record.TimestampUtc)
                .Take(200)
                .ToList();

        if (failed.Count == 0)
        {
            return "Nothing in History has failed.";
        }

        var text = new StringBuilder($"The {failed.Count} most recent actions in History that didn't work, newest first:\n\n");

        foreach (var record in failed)
        {
            text.AppendLine($"{record.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} {record.ActionType}: {record.Message}");
        }

        return text.ToString();
    }

    private static string Readme(bool hideNames) =>
        $"""
        Simplifiarr logs for a problem report

        about.txt      Simplifiarr's version, Windows, which apps are connected, and the drives.
        settings.json  Your settings, with every API key and password replaced by [hidden].
        problems.txt   The most recent actions in History that didn't work.
        logs/          The last {DaysOfLogs} days of Simplifiarr's log.

        Always taken out: API keys, passwords, tokens, IP addresses, email addresses
        and your Windows user name.
        {(hideNames
            ? "Also taken out: movie and show titles Simplifiarr knows about, the names of people\nwho watch them, and media file names. Names Simplifiarr doesn't know about can\nstill appear, so have a quick look before posting this anywhere public."
            : "Titles, people's names and file names were left in, to make the problem easier\nto find. Send this by email rather than posting it publicly.")}
        """;

    private static void Add(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);

        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));

        writer.Write(text);
    }
}
