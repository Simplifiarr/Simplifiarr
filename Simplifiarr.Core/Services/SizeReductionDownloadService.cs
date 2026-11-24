using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public enum SizeReductionDownloadStatus
{
    Downloading,
    ReadyToSwap,
    Swapped,
    Failed,
    Cancelled
}

/// <summary>A smaller release being downloaded to replace a library file.</summary>
public sealed class SizeReductionDownload
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string TorrentHash { get; set; } = string.Empty;

    public string ReleaseTitle { get; set; } = string.Empty;

    /// <summary>The library file it will replace.</summary>
    public string LibraryFilePath { get; set; } = string.Empty;

    public long LibraryFileBytes { get; set; }

    public bool IsTv { get; set; }

    public string DriveName { get; set; } = string.Empty;

    /// <summary>Where the torrent downloads to, on the library's drive.</summary>
    public string SavePath { get; set; } = string.Empty;

    public long ReleaseBytes { get; set; }

    public double Progress { get; set; }

    // The swarm as qBittorrent last saw it. Not kept between restarts.
    [System.Text.Json.Serialization.JsonIgnore]
    public TorrentRecord? Live { get; set; }

    public SizeReductionDownloadStatus Status { get; set; }

    public string Message { get; set; } = string.Empty;

    /// <summary>The new library file, once swapped.</summary>
    public string? NewLibraryFilePath { get; set; }

    /// <summary>
    /// Space the swap really freed: nothing while the old file lives on
    /// under another name (the download it came from, still seeding or not).
    /// </summary>
    public long SavedBytes { get; set; }

    /// <summary>Where the old library file went in Quarantine, once swapped.</summary>
    public string OldFileQuarantinePath { get; set; } = string.Empty;

    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? FinishedUtc { get; set; }
}

/// <summary>
/// Downloads a smaller release found by Size Reduction and, once it has
/// finished, swaps it in for the library file: the new file is hardlinked
/// into the library (so it keeps seeding), the old one goes to Quarantine,
/// and Radarr or Sonarr is asked to rescan. The old file's torrent, if
/// any, is left alone. An old file that is also a download (a hardlink) has
/// only its library name removed: the bytes stay under the download's name,
/// so nothing is counted as saved, and Copies lists that download.
/// </summary>
public interface ISizeReductionDownloadService
{
    event Action? Changed;

    IReadOnlyList<SizeReductionDownload> Downloads { get; }

    /// <summary>Adds the release to the torrent client, started. Null on success, otherwise why not.</summary>
    Task<string?> StartAsync(SizeReductionCandidate candidate);

    /// <summary>
    /// Updates each download's progress. A finished one is swapped in when
    /// automatic swapping is on, otherwise it waits for Swap now.
    /// </summary>
    Task CheckAsync();

    Task<string?> SwapAsync(Guid id);

    /// <summary>
    /// Stops a download that hasn't been swapped in: the torrent and what
    /// it downloaded are removed from the client. Your library file stays.
    /// </summary>
    Task<string?> CancelAsync(Guid id);

    /// <summary>Stops tracking a download. The torrent itself is left alone.</summary>
    void Forget(Guid id);
}

public class SizeReductionDownloadService : ISizeReductionDownloadService
{
    public const string Tag = "simplifiarr-replace";

    private static readonly string[] VideoExtensions = [".mkv", ".mp4", ".m4v", ".avi", ".ts", ".mov", ".wmv"];

    private readonly ISettingsService _settings;
    private readonly IProwlarrClient _prowlarr;
    private readonly ITorrentClient _client;
    private readonly IArrPathService _arr;
    private readonly IHistoryService _history;

    private readonly object _sync = new();
    private readonly SemaphoreSlim _work = new(1, 1);
    private readonly string _path;

    private List<SizeReductionDownload> _downloads;

    public SizeReductionDownloadService(
        ISettingsService settings,
        IProwlarrClient prowlarr,
        ITorrentClient client,
        IArrPathService arr,
        IHistoryService history)
    {
        _settings = settings;
        _prowlarr = prowlarr;
        _client = client;
        _arr = arr;
        _history = history;

        _path = AppPaths.DataFile("size-reduction-downloads.json");
        _downloads = JsonFileStore.Load<List<SizeReductionDownload>>(_path) ?? [];
    }

    /// <summary>
    /// For tests: how the old library file is removed. Unset, it goes to
    /// Quarantine on its own drive, where it can be restored.
    /// </summary>
    public Func<string, bool>? RemoveOldFile { get; set; }

    private async Task<string?> TakeOutAsync(string path) =>
        RemoveOldFile is not null ? (RemoveOldFile(path) ? string.Empty : null)
        : await QuarantineService.MoveIntoAsync(_settings, path);

    // Removes one name of a file that has others: its bytes stay under them.
    private static bool DeleteLinkedName(string path)
    {
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            File.Delete(path);

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Size Reduction couldn't remove the old library name {path}: {exception.Message}");

            return false;
        }
    }

    public event Action? Changed;

    public IReadOnlyList<SizeReductionDownload> Downloads
    {
        get { lock (_sync) { return _downloads.OrderByDescending(download => download.AddedUtc).ToList(); } }
    }

    public async Task<string?> StartAsync(SizeReductionCandidate candidate)
    {
        if (!_client.Capabilities.CanAddTorrents)
        {
            return $"Adding torrents isn't supported with {_client.Name} yet.";
        }

        lock (_sync)
        {
            if (_downloads.Any(download =>
                    download.Status is SizeReductionDownloadStatus.Downloading or SizeReductionDownloadStatus.ReadyToSwap &&
                    string.Equals(download.LibraryFilePath, candidate.CanonicalPath, StringComparison.OrdinalIgnoreCase)))
            {
                return "A smaller release of this file is already downloading.";
            }
        }

        var settings = await _settings.GetSettingsAsync();
        var isTv = candidate.Season.HasValue;

        var profile =
            settings.DriveProfiles.FirstOrDefault(entry =>
                entry.Enabled &&
                PathContainment.IsPathUnderRoot(candidate.CanonicalPath, isTv ? entry.TvLibraryPath : entry.MovieLibraryPath));

        if (profile is null)
        {
            return "Its library isn't one of the drives in Settings.";
        }

        // The same drive as the library, so the finished file can be
        // hardlinked in rather than copied.
        var savePath = isTv ? profile.TvDownloads : profile.MovieDownloads;

        if (string.IsNullOrWhiteSpace(savePath) ||
            !string.Equals(StoragePlanner.RootOf(savePath), StoragePlanner.RootOf(candidate.CanonicalPath), StringComparison.OrdinalIgnoreCase))
        {
            return $"{profile.Name} has no downloads folder on the same drive as its library, so the new file couldn't be linked in.";
        }

        var source = await _prowlarr.GetReleaseAsync(candidate.Candidate.DownloadUrl);

        string hash;
        long size;
        TorrentAddResult added;

        if (source?.TorrentBytes is { } bytes &&
            BencodeTorrentReader.TryParse(bytes) is { InfoHashHex: { } fileHash } metadata)
        {
            hash = fileHash;
            size = metadata.TotalLengthBytes;
            added = await _client.DownloadTorrentAsync(bytes, savePath, Tag);
        }
        else if (source?.Magnet is { } magnet && MagnetHash(magnet) is { } magnetHash)
        {
            hash = magnetHash;
            size = candidate.Candidate.SizeBytes;
            added = await _client.DownloadMagnetAsync(magnet, savePath, Tag);
        }
        else
        {
            return $"{candidate.Candidate.Indexer} didn't give a .torrent file or magnet link for it. Try another release.";
        }

        if (!added.Success)
        {
            return added.ErrorMessage ?? $"{_client.Name} didn't take the torrent.";
        }

        var download =
            new SizeReductionDownload
            {
                TorrentHash = hash.ToLowerInvariant(),
                ReleaseTitle = candidate.Candidate.Title,
                LibraryFilePath = candidate.CanonicalPath,
                LibraryFileBytes = candidate.AssetSizeBytes,
                IsTv = isTv,
                DriveName = profile.Name,
                SavePath = savePath,
                ReleaseBytes = size,
                Message = $"Downloading in {_client.Name}."
            };

        lock (_sync)
        {
            _downloads.Add(download);
        }

        Save();

        return null;
    }

    public async Task CheckAsync()
    {
        if (Downloads.All(download => download.Status != SizeReductionDownloadStatus.Downloading &&
                                      download.Status != SizeReductionDownloadStatus.ReadyToSwap))
        {
            return;
        }

        var torrents = await _client.TryGetTorrentsAsync();

        if (torrents is null)
        {
            return;
        }

        var settings = await _settings.GetSettingsAsync();

        foreach (var download in Downloads.Where(download => download.Status == SizeReductionDownloadStatus.Downloading))
        {
            var torrent =
                torrents.FirstOrDefault(entry =>
                    string.Equals(entry.TorrentHash, download.TorrentHash, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entry.InfoHashV1, download.TorrentHash, StringComparison.OrdinalIgnoreCase));

            if (torrent is null)
            {
                Update(download, SizeReductionDownloadStatus.Failed, $"The torrent is no longer in {_client.Name}.");
                continue;
            }

            download.Progress = torrent.Progress;
            download.Live = torrent;

            if (torrent.Progress >= 1)
            {
                Update(download, SizeReductionDownloadStatus.ReadyToSwap, "Downloaded. Ready to swap in.");
            }
        }

        Save();

        if (settings.SizeReductionAutoSwap)
        {
            foreach (var ready in Downloads.Where(download => download.Status == SizeReductionDownloadStatus.ReadyToSwap))
            {
                await SwapAsync(ready.Id);
            }
        }
    }

    public async Task<string?> SwapAsync(Guid id)
    {
        await _work.WaitAsync();

        try
        {
            var download = Downloads.FirstOrDefault(entry => entry.Id == id);

            if (download is null || download.Status != SizeReductionDownloadStatus.ReadyToSwap)
            {
                return "That download isn't ready to swap.";
            }

            var problem = await SwapCoreAsync(download);

            await _history.AddRecordAsync(
                new HistoryRecord
                {
                    ActionType = HistoryActionType.SizeReduced,
                    FileName = Path.GetFileName(download.NewLibraryFilePath ?? download.LibraryFilePath),
                    LibraryPath = download.NewLibraryFilePath ?? download.LibraryFilePath,
                    DownloadPath = download.LibraryFilePath,
                    QuarantinePath = problem is null ? download.OldFileQuarantinePath : string.Empty,
                    SizeBytes = problem is null ? download.SavedBytes : 0,
                    Success = problem is null,
                    Message = download.Message
                });

            return problem;
        }
        finally
        {
            _work.Release();
        }
    }

    private async Task<string?> SwapCoreAsync(SizeReductionDownload download)
    {
        var torrents = await _client.TryGetTorrentsAsync();

        var torrent =
            torrents?
                .FirstOrDefault(entry => string.Equals(entry.TorrentHash, download.TorrentHash, StringComparison.OrdinalIgnoreCase));

        if (torrent is null || torrent.Progress < 1)
        {
            return Keep(download, $"The torrent isn't finished in {_client.Name}.");
        }

        var files = await _client.GetTorrentFilesAsync(torrent.TorrentHash);

        var video =
            files?
                .Where(file => VideoExtensions.Contains(Path.GetExtension(file.RelativePath), StringComparer.OrdinalIgnoreCase))
                .Where(file => !file.RelativePath.Contains("sample", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.SizeBytes)
                .FirstOrDefault();

        if (video is null)
        {
            return Keep(download, "No video file was found in the torrent.");
        }

        var newFile = Path.Combine(torrent.SavePath, video.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(newFile) || new FileInfo(newFile).Length != video.SizeBytes)
        {
            return Keep(download, $"The downloaded file isn't where {_client.Name} says it is: {newFile}");
        }

        var oldFile = download.LibraryFilePath;
        var folder = Path.GetDirectoryName(oldFile)!;

        if (!File.Exists(oldFile))
        {
            return Keep(download, "The library file it replaces is no longer there.");
        }

        var target = Path.Combine(folder, Path.GetFileName(newFile));
        var staging = target + ".simplifiarr-new";

        if (!string.Equals(target, oldFile, StringComparison.OrdinalIgnoreCase) && File.Exists(target))
        {
            return Keep(download, $"{target} already exists.");
        }

        // The old file may have other names: the download it was linked from,
        // seeding or left behind. Its bytes then stay on disk whatever
        // happens to the library name. Sending that name to the Recycle Bin
        // would only hide one more name, so it is deleted instead (nothing is
        // lost: the other name has the same bytes) and nothing counts as saved.
        var old = SeedingCheck.From(torrents?.Select(entry => (entry.ContentPath, entry.Name))).Check(oldFile);
        var linked = old.State != SeedState.NotLinked;

        if (NativeHardlinks.CreateHardlink(newFile, staging) is { } linkError)
        {
            return Keep(download, $"Couldn't link the new file into the library: {linkError}");
        }

        string? quarantined = string.Empty;

        if (linked ? !DeleteLinkedName(oldFile) : (quarantined = await TakeOutAsync(oldFile)) is null)
        {
            File.Delete(staging);

            return Keep(
                download,
                linked
                    ? "The old library file couldn't be removed, so nothing was changed."
                    : "The old file couldn't be moved to Quarantine, so nothing was changed.");
        }

        download.OldFileQuarantinePath = quarantined ?? string.Empty;

        File.Move(staging, target);

        download.NewLibraryFilePath = target;

        // Radarr and Sonarr find the new file and forget the old one.
        var kind = download.IsTv ? StorageKinds.Tv : StorageKinds.Movies;

        try
        {
            if (await FindArrItemAsync(kind, folder) is { } item)
            {
                await _arr.RescanAsync(kind, item);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            AppLog.Warn("Couldn't ask Radarr or Sonarr to rescan after a size reduction.", exception);
        }

        download.SavedBytes = linked ? 0 : Math.Max(0, download.LibraryFileBytes - video.SizeBytes);

        Update(
            download,
            SizeReductionDownloadStatus.Swapped,
            old.State switch
            {
                SeedState.Seeding =>
                    $"Swapped in. The old file is still seeding as \"{old.Torrent}\", so no space is freed until that torrent is removed; Copies lists it then.",
                SeedState.NotLinked =>
                    $"Swapped in. {SizeFormatter.FormatBytes(download.SavedBytes)} saved once you empty Quarantine, where the old file is.",
                _ =>
                    $"Swapped in. The old file is also at {old.OtherNames.FirstOrDefault() ?? "another name"}, so no space is freed yet; Copies lists it to remove."
            });

        Save();

        return null;
    }

    /// <summary>A movie's folder, or the show folder above a season folder.</summary>
    private async Task<ArrItem?> FindArrItemAsync(string kind, string folder)
    {
        for (var current = folder; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (await _arr.FindByFolderAsync(kind, current) is { } item)
            {
                return item;
            }

            if (kind == StorageKinds.Movies)
            {
                break;
            }
        }

        return null;
    }

    /// <summary>
    /// The info-hash in a magnet link, as 40 hex characters (qBittorrent's
    /// form). Links carry it as hex or as 32 base32 characters.
    /// </summary>
    public static string? MagnetHash(string magnet)
    {
        var match = System.Text.RegularExpressions.Regex.Match(magnet, @"xt=urn:btih:([A-Za-z0-9]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups[1].Value;

        if (value.Length == 40)
        {
            return value.ToLowerInvariant();
        }

        if (value.Length != 32)
        {
            return null;
        }

        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        var bytes = new byte[20];
        var buffer = 0;
        var bits = 0;
        var index = 0;

        foreach (var character in value.ToUpperInvariant())
        {
            var digit = alphabet.IndexOf(character);

            if (digit < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | digit;
            bits += 5;

            if (bits >= 8)
            {
                bytes[index++] = (byte)(buffer >> (bits - 8));
                bits -= 8;
                buffer &= (1 << bits) - 1;
            }
        }

        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public async Task<string?> CancelAsync(Guid id)
    {
        var download = Downloads.FirstOrDefault(entry => entry.Id == id);

        if (download is null ||
            download.Status is not (SizeReductionDownloadStatus.Downloading or SizeReductionDownloadStatus.ReadyToSwap))
        {
            return "Only a download that hasn't been swapped in can be cancelled.";
        }

        // Nothing it downloaded is in the library yet, so its files go too.
        if (await _client.RemoveTorrentAsync(download.TorrentHash, deleteFiles: true) is { } problem)
        {
            return Keep(download, $"{_client.Name} didn't remove it: {problem}");
        }

        Update(download, SizeReductionDownloadStatus.Cancelled, $"Cancelled. The torrent and what it downloaded were removed from {_client.Name}; your file is untouched.");
        download.Live = null;

        Save();

        return null;
    }

    public void Forget(Guid id)
    {
        lock (_sync)
        {
            _downloads.RemoveAll(download => download.Id == id);
        }

        Save();
    }

    private string Keep(SizeReductionDownload download, string message)
    {
        download.Message = message;
        Save();

        return message;
    }

    private void Update(SizeReductionDownload download, SizeReductionDownloadStatus status, string message)
    {
        download.Status = status;
        download.Message = message;

        if (status is SizeReductionDownloadStatus.Swapped or SizeReductionDownloadStatus.Failed or SizeReductionDownloadStatus.Cancelled)
        {
            download.FinishedUtc = DateTime.UtcNow;
        }
    }

    private void Save()
    {
        lock (_sync)
        {
            JsonFileStore.Save(_path, _downloads);
        }

        Changed?.Invoke();
    }
}
