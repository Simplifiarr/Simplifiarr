using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

/// <summary>Starts the downloaded installer. Windows-service specific, so it lives in the web app.</summary>
public interface IUpdateLauncher
{
    InstallInfo Describe();

    /// <summary>
    /// Runs the installer on its own. It stops this service, replaces the
    /// files and starts it again, so this process ends soon after.
    /// </summary>
    void Launch(string setupPath, string logPath);
}

public interface IUpdateService
{
    event Action? Changed;

    string CurrentVersion { get; }

    UpdateState State { get; }

    InstallInfo Install { get; }

    /// <summary>A newer release than this one, if the last check found one.</summary>
    ReleaseInfo? Available { get; }

    bool IsChecking { get; }

    /// <summary>"Downloading 40%..." while an update is under way; otherwise null.</summary>
    string? Progress { get; }

    /// <summary>Said once after a restart: "Updated to 1.2.0" or why it didn't.</summary>
    string? LastResult { get; }

    void SavePreferences(bool checkAutomatically, bool installAutomatically, string installTime);

    Task CheckAsync();

    /// <summary>Downloads, checks and starts the installer. Returns a problem, or null once it has started.</summary>
    Task<string?> InstallAsync();
}

public class UpdateService : IUpdateService
{
    public const string Repository = "Simplifiarr/Simplifiarr";

    private static readonly string StateFile = AppPaths.DataFile("updates.json");

    private readonly HttpClient _http;
    private readonly IUpdateLauncher _launcher;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly object _saveLock = new();

    public UpdateService(HttpClient http, IUpdateLauncher launcher)
    {
        _launcher = launcher;

        CurrentVersion = VersionOf(Assembly.GetEntryAssembly());

        _http = http;
        _http.Timeout = TimeSpan.FromMinutes(10);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Simplifiarr/{CurrentVersion}");
        State = JsonFileStore.Load<UpdateState>(StateFile) ?? new UpdateState();
        Install = launcher.Describe();
        LastResult = ResultOfLastInstall();
    }

    public event Action? Changed;

    public string CurrentVersion { get; }

    public UpdateState State { get; }

    public InstallInfo Install { get; }

    public ReleaseInfo? Available =>
        State.Latest is { } latest && IsNewer(latest.Version, CurrentVersion) ? latest : null;

    public bool IsChecking { get; private set; }

    public string? Progress { get; private set; }

    public string? LastResult { get; }

    public void SavePreferences(bool checkAutomatically, bool installAutomatically, string installTime)
    {
        State.CheckAutomatically = checkAutomatically;
        State.InstallAutomatically = installAutomatically;
        State.InstallTime = TimeOnly.TryParse(installTime, out var time) ? time.ToString("HH:mm") : State.InstallTime;
        Save();
    }

    public async Task CheckAsync()
    {
        if (!await _busy.WaitAsync(0))
        {
            return;
        }

        IsChecking = true;
        Changed?.Invoke();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await _http.SendAsync(request);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // No release published yet.
                State.Latest = null;
                State.LastError = null;
            }
            else
            {
                response.EnsureSuccessStatusCode();

                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                State.Latest = ParseRelease(json.RootElement);
                State.LastError = null;
            }
        }
        catch (Exception exception)
        {
            State.LastError = $"Couldn't reach GitHub: {exception.Message}";
            AppLog.Warn("Checking for updates failed.", exception);
        }
        finally
        {
            State.LastCheckedUtc = DateTime.UtcNow;
            Save();

            IsChecking = false;
            _busy.Release();
            Changed?.Invoke();
        }
    }

    public async Task<string?> InstallAsync()
    {
        if (!Install.CanUpdateItself)
        {
            return Install.WhyNot;
        }

        if (Available is not { } release)
        {
            return "There's no newer version to install.";
        }

        if (release.SetupUrl is null || release.SetupName is null || release.ChecksumsUrl is null)
        {
            return "This release has no installer attached, so it can't be installed from here.";
        }

        if (!await _busy.WaitAsync(0))
        {
            return "An update check or install is already running.";
        }

        try
        {
            var folder = Path.Combine(AppPaths.DataRoot, "Updates");
            Directory.CreateDirectory(folder);

            foreach (var old in Directory.EnumerateFiles(folder, "*.exe"))
            {
                File.Delete(old);
            }

            var setupPath = Path.Combine(folder, release.SetupName);

            SetProgress("Downloading...");

            var sums = await _http.GetStringAsync(release.ChecksumsUrl);

            if (ChecksumFor(sums, release.SetupName) is not { } expected)
            {
                return "The release's checksum list doesn't include the installer, so it wasn't trusted.";
            }

            await DownloadAsync(_http, release.SetupUrl, setupPath, release.SetupBytes);

            SetProgress("Checking the download...");

            string actual;

            await using (var file = File.OpenRead(setupPath))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(file));
            }

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(setupPath);
                return "The download didn't match the release's checksum, so it was thrown away. Try again later.";
            }

            State.InstallingVersion = release.Version;
            State.InstallFromVersion = CurrentVersion;
            State.InstallStartedUtc = DateTime.UtcNow;
            Save();

            SetProgress($"Installing {release.Version}. Simplifiarr will restart; this page reconnects by itself in a minute or two.");
            AppLog.Info($"Installing update {release.Version} (from {CurrentVersion}).");

            _launcher.Launch(setupPath, Path.Combine(AppPaths.LogsDirectory, "update-setup.log"));

            return null;
        }
        catch (Exception exception)
        {
            AppLog.Warn("Installing the update failed.", exception);
            SetProgress(null);
            return $"The update couldn't be installed: {exception.Message}";
        }
        finally
        {
            _busy.Release();
        }
    }

    private async Task DownloadAsync(HttpClient client, string url, string path, long expectedBytes)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? expectedBytes;

        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = File.Create(path);

        var buffer = new byte[81920];
        long done = 0;
        var lastPercent = -1;
        int read;

        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read));
            done += read;

            var percent = total > 0 ? (int)(done * 100 / total) : 0;

            if (percent != lastPercent)
            {
                lastPercent = percent;
                SetProgress($"Downloading {percent}%...");
            }
        }
    }

    private void SetProgress(string? text)
    {
        Progress = text;
        Changed?.Invoke();
    }

    private void Save()
    {
        lock (_saveLock)
        {
            JsonFileStore.Save(StateFile, State, indented: true);
        }
    }

    private string? ResultOfLastInstall()
    {
        if (State.InstallingVersion is not { } wanted || State.InstallStartedUtc is not { } started)
        {
            return null;
        }

        // Say it once, then forget it.
        var from = State.InstallFromVersion;
        State.InstallingVersion = null;
        State.InstallFromVersion = null;
        State.InstallStartedUtc = null;
        Save();

        if (DateTime.UtcNow - started > TimeSpan.FromDays(1))
        {
            return null;
        }

        if (string.Equals(NormalizeVersion(wanted), NormalizeVersion(CurrentVersion), StringComparison.OrdinalIgnoreCase))
        {
            return $"Updated to {CurrentVersion}" + (from is null ? "." : $" from {from}.");
        }

        return $"The update to {wanted} didn't finish, so {CurrentVersion} is still running. Its log is Logs\\update-setup.log in the data folder.";
    }

    // ------------------------------------------------------------ Pure helpers

    /// <summary>"1.2.0" from the assembly; "0.0.0" if it has none.</summary>
    public static string VersionOf(Assembly? assembly)
    {
        var informational = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational.Split('+')[0];
        }

        return assembly?.GetName().Version is { } version ? $"{version.Major}.{version.Minor}.{version.Build}" : "0.0.0";
    }

    /// <summary>"v1.2" -> "1.2.0"; anything after a "-" (a pre-release) kept.</summary>
    public static string NormalizeVersion(string version)
    {
        var text = version.Trim().TrimStart('v', 'V');
        var dash = text.IndexOf('-');
        var core = dash < 0 ? text : text[..dash];
        var suffix = dash < 0 ? string.Empty : text[dash..];

        return Version.TryParse(core.Contains('.') ? core : core + ".0", out var parsed)
            ? $"{parsed.Major}.{parsed.Minor}.{Math.Max(0, parsed.Build)}{suffix}"
            : text;
    }

    /// <summary>True when <paramref name="latest"/> is a later version than <paramref name="current"/>.</summary>
    public static bool IsNewer(string latest, string current)
    {
        static (Version Core, bool PreRelease)? Parse(string text)
        {
            var normal = NormalizeVersion(text);
            var dash = normal.IndexOf('-');

            return Version.TryParse(dash < 0 ? normal : normal[..dash], out var version)
                ? (version, dash >= 0)
                : null;
        }

        if (Parse(latest) is not { } a || Parse(current) is not { } b)
        {
            return false;
        }

        var compared = a.Core.CompareTo(b.Core);

        // 1.2.0 is newer than 1.2.0-beta.
        return compared > 0 || (compared == 0 && b.PreRelease && !a.PreRelease);
    }

    /// <summary>Reads GitHub's release JSON.</summary>
    public static ReleaseInfo ParseRelease(JsonElement release)
    {
        string Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

        JsonElement? setup = null;
        JsonElement? sums = null;

        if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = Text(asset, "name");

                if (name.StartsWith("Simplifiarr-Setup-", StringComparison.OrdinalIgnoreCase) &&
                    name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    setup = asset;
                }
                else if (string.Equals(name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    sums = asset;
                }
            }
        }

        var tag = Text(release, "tag_name");
        var title = Text(release, "name");

        return new ReleaseInfo(
            NormalizeVersion(tag),
            title.Length > 0 ? title : tag,
            Text(release, "body").Trim(),
            Text(release, "html_url"),
            DateTime.TryParse(Text(release, "published_at"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var published) ? published : null,
            setup is { } s ? Text(s, "browser_download_url") : null,
            setup is { } n ? Text(n, "name") : null,
            setup is { } b && b.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : 0,
            sums is { } c ? Text(c, "browser_download_url") : null);
    }

    /// <summary>The hash for <paramref name="fileName"/> in a "hash  name" list, as sha256sum writes it.</summary>
    public static string? ChecksumFor(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 2 &&
                parts[0].Length == 64 &&
                string.Equals(parts[1].Trim().TrimStart('*'), fileName, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0];
            }
        }

        return null;
    }

    /// <summary>The program part of a service command line: "C:\X\app.exe" --a=b -> C:\X\app.exe.</summary>
    public static string? ProgramOf(string imagePath)
    {
        var text = Environment.ExpandEnvironmentVariables(imagePath.Trim());

        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        var exeEnd = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeEnd > 0 ? text[..(exeEnd + 4)] : null;
    }
}
