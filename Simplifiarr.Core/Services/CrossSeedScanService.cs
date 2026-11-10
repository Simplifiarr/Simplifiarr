using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public class CrossSeedScanService : ICrossSeedScanService
{
    // Torrents in these states don't have settled files to compare against.
    private static readonly string[] UnsettledStates =
        ["checking", "error", "missingfiles", "allocating", "moving", "unknown", "metadl"];

    // Per torrent, only the closest-sized releases are worth downloading.
    private const int CandidatesPerTorrent = 20;

    private readonly IQbittorrentScanner _qbittorrent;
    private readonly IProwlarrClient _prowlarr;
    private readonly IReleaseParser _releaseParser;
    private readonly IFileScannerService _fileScanner;
    private readonly IMediaCatalogService _catalog;
    private readonly IWholeTorrentCandidateService _wholeTorrent;
    private readonly ISettingsService _settings;

    private readonly object _sync = new();

    private CancellationTokenSource? _cancellation;

    private List<CrossSeedScanResult> _matches = [];
    private List<string> _alreadyInQbittorrent = [];

    public CrossSeedScanService(
        IQbittorrentScanner qbittorrent,
        IProwlarrClient prowlarr,
        IReleaseParser releaseParser,
        IFileScannerService fileScanner,
        IMediaCatalogService catalog,
        IWholeTorrentCandidateService wholeTorrent,
        ISettingsService settings)
    {
        _qbittorrent = qbittorrent;
        _prowlarr = prowlarr;
        _releaseParser = releaseParser;
        _fileScanner = fileScanner;
        _catalog = catalog;
        _wholeTorrent = wholeTorrent;
        _settings = settings;
    }

    public bool IsScanning { get; private set; }

    public CrossSeedScanProgress? Progress { get; private set; }

    public string? LastMessage { get; private set; }

    public bool LastSucceeded { get; private set; }

    public IReadOnlyList<CrossSeedScanResult> Matches
    {
        get
        {
            lock (_sync)
            {
                return _matches.ToList();
            }
        }
    }

    public IReadOnlyList<string> AlreadyInQbittorrent
    {
        get
        {
            lock (_sync)
            {
                return _alreadyInQbittorrent.ToList();
            }
        }
    }

    public event Action? Changed;

    public async Task ScanAsync()
    {
        CancellationToken token;

        lock (_sync)
        {
            if (IsScanning)
            {
                return;
            }

            IsScanning = true;

            _cancellation = new CancellationTokenSource();

            token = _cancellation.Token;

            _matches = [];
            _alreadyInQbittorrent = [];
        }

        LastMessage = null;

        Changed?.Invoke();

        try
        {
            var allTorrents =
                await _qbittorrent.TryGetTorrentsAsync();

            if (allTorrents is null)
            {
                Finish(
                    false,
                    "Couldn't reach qBittorrent. Check its address and sign-in in Settings, under Connections.");

                return;
            }

            if (!await _prowlarr.TestConnectionAsync())
            {
                Finish(
                    false,
                    "Couldn't reach Prowlarr. Check its address and API key in Settings, under Connections.");

                return;
            }

            var checkedCount =
                await ScanTorrentsAsync(allTorrents, token);

            var found = Matches.Count;
            var skipped = AlreadyInQbittorrent.Count;

            var message =
                token.IsCancellationRequested
                    ? $"Scan stopped after {checkedCount.Done} of {checkedCount.Total} torrent(s). {found} match(es) found so far."
                    : $"Scan finished. {found} match(es) found across {checkedCount.Total} torrent(s).";

            if (skipped > 0)
            {
                message +=
                    $" {skipped} result(s) were left out because that torrent is already in qBittorrent.";
            }

            Finish(!token.IsCancellationRequested, message);
        }
        catch (Exception exception)
        {
            AppLog.Warn("Cross-seed scan failed.", exception);

            Finish(false, $"The scan failed: {exception.Message}");
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _cancellation?.Cancel();
        }
    }

    public async Task<CrossSeedAddSummary> AddAsync(
        IReadOnlyCollection<CrossSeedScanResult> matches,
        Action<string>? onProgress = null)
    {
        var settings =
            await _settings.GetSettingsAsync();

        var linked = 0;
        var failedToLink = 0;
        var added = 0;
        var failedToAdd = 0;

        foreach (var match in matches)
        {
            onProgress?.Invoke($"Linking {match.CandidateResult.Title}...");

            var result =
                await _wholeTorrent.CommitAsync(
                    match.CandidateMetadata,
                    match.Match,
                    match.CandidateTorrentBytes,
                    settings.CrossSeedTorrentStoragePath);

            if (!result.Success)
            {
                failedToLink++;

                continue;
            }

            linked++;

            lock (_sync)
            {
                _matches.Remove(match);
            }

            onProgress?.Invoke($"Adding {match.CandidateResult.Title} to qBittorrent...");

            var addResult =
                await _qbittorrent.AddTorrentAsync(
                    match.CandidateTorrentBytes,
                    result.OutputRoot ?? settings.CrossSeedTorrentStoragePath);

            if (addResult.Success)
            {
                added++;
            }
            else
            {
                failedToAdd++;
            }
        }

        Changed?.Invoke();

        return new CrossSeedAddSummary(linked, failedToLink, added, failedToAdd);
    }

    private async Task<(int Done, int Total)> ScanTorrentsAsync(
        List<TorrentRecord> allTorrents,
        CancellationToken token)
    {
        var torrents =
            allTorrents
                .Where(torrent =>
                    File.Exists(torrent.ContentPath) ||
                    Directory.Exists(torrent.ContentPath))
                .Where(IsSettled)
                .ToList();

        // Torrents of the same title are searched once, together.
        var groups =
            torrents
                .GroupBy(
                    torrent => _releaseParser.Parse(torrent.Name).NormalizedTitle,
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

        var catalogAssets =
            _catalog.GetAssets();

        // Every torrent in qBittorrent, whatever its state. A release with
        // one of these hashes is a torrent you already have (including one
        // just cross-seeded that is still checking), not a new one.
        var knownHashes =
            allTorrents
                .SelectMany(torrent => new[] { torrent.TorrentHash, torrent.InfoHashV1, torrent.InfoHashV2 })
                .Where(hash => !string.IsNullOrWhiteSpace(hash))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var done = 0;

        foreach (var group in groups)
        {
            if (token.IsCancellationRequested)
            {
                break;
            }

            var releases =
                await _prowlarr.SearchAsync(group.Key);

            // The files of every torrent of this title are fair sources for
            // any release of it, not just the one whose search found it.
            var ownFiles = new List<string>();

            var toTry =
                new Dictionary<string, ProwlarrSearchResult>(StringComparer.Ordinal);

            foreach (var torrent in group)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                Report(done, torrents.Count, torrent.Name);

                done++;

                try
                {
                    // A single-file torrent's content is the file itself,
                    // not a folder to walk.
                    if (File.Exists(torrent.ContentPath))
                    {
                        ownFiles.Add(torrent.ContentPath);
                    }

                    ownFiles.AddRange(
                        (await _fileScanner.ScanAsync(torrent.ContentPath))
                            .Select(file => file.FullPath));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The library and the title's other torrents may still
                    // supply the files.
                }

                // Only a release of the same total size (exact or within
                // 1%) can be the same files.
                foreach (var release in ProwlarrCandidateRanker
                             .FilterSameContentCandidates(releases, torrent.SizeBytes)
                             .Take(CandidatesPerTorrent))
                {
                    toTry[release.DownloadUrl] = release;
                }
            }

            foreach (var release in toTry.Values)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                await TryReleaseAsync(
                    group.Key,
                    release,
                    ownFiles,
                    catalogAssets,
                    allTorrents,
                    knownHashes);
            }
        }

        return (done, torrents.Count);
    }

    private async Task TryReleaseAsync(
        string searchedTitle,
        ProwlarrSearchResult release,
        List<string> ownFiles,
        IReadOnlyCollection<MediaAsset> catalogAssets,
        List<TorrentRecord> allTorrents,
        HashSet<string> knownHashes)
    {
        var bytes =
            await _prowlarr.DownloadTorrentAsync(release.DownloadUrl);

        var metadata =
            bytes is null ? null : BencodeTorrentReader.TryParse(bytes);

        if (bytes is null || metadata is null)
        {
            return;
        }

        if (metadata.InfoHashHex is not null &&
            knownHashes.Contains(metadata.InfoHashHex))
        {
            lock (_sync)
            {
                _alreadyInQbittorrent.Add($"{release.Title} ({release.Indexer})");
            }

            return;
        }

        var match =
            await _wholeTorrent.EvaluateAsync(metadata, ownFiles, catalogAssets);

        if (!match.IsFullMatch)
        {
            return;
        }

        var seeding =
            FindSeedingTorrents(match, allTorrents);

        var releaseTrackers =
            metadata.TrackerUrls
                .Select(TrackerDomain)
                .OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        lock (_sync)
        {
            _matches.Add(
                new CrossSeedScanResult
                {
                    SourceQueryTitle = searchedTitle,
                    CandidateResult = release,
                    CandidateMetadata = metadata,
                    CandidateTorrentBytes = bytes,
                    Match = match,
                    SeedingTorrents = seeding,
                    SameTrackerTorrents =
                        seeding
                            .Where(torrent =>
                                TrackerDomain(torrent.Tracker) is { } domain &&
                                releaseTrackers.Contains(domain))
                            .ToList()
                });
        }

        if (Progress is { } progress)
        {
            Progress = progress with { Found = Matches.Count };
        }

        Changed?.Invoke();
    }

    private void Report(
        int done,
        int total,
        string torrentName)
    {
        Progress =
            new CrossSeedScanProgress(done, total, torrentName, Matches.Count);

        Changed?.Invoke();
    }

    private void Finish(
        bool succeeded,
        string message)
    {
        lock (_sync)
        {
            IsScanning = false;

            _cancellation?.Dispose();
            _cancellation = null;
        }

        Progress = null;
        LastSucceeded = succeeded;
        LastMessage = message;

        Changed?.Invoke();
    }

    private static bool IsSettled(
        TorrentRecord torrent)
    {
        var state =
            torrent.State.ToLowerInvariant();

        return !UnsettledStates.Any(state.Contains);
    }

    /// <summary>
    /// The torrents in qBittorrent whose data holds any of the matched
    /// files - what these bytes are being seeded as today.
    /// </summary>
    private static List<TorrentRecord> FindSeedingTorrents(
        WholeTorrentMatchResult match,
        IReadOnlyCollection<TorrentRecord> allTorrents)
    {
        var matchedPaths =
            match.EntryMatches
                .Select(entry => NormalizePath(entry.ResolvedPath))
                .ToList();

        return allTorrents
            .Where(torrent =>
            {
                if (string.IsNullOrWhiteSpace(torrent.ContentPath))
                {
                    return false;
                }

                var content = NormalizePath(torrent.ContentPath);
                var contentAsFolder = content + Path.DirectorySeparatorChar;

                return matchedPaths.Any(path =>
                    string.Equals(path, content, StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith(contentAsFolder, StringComparison.OrdinalIgnoreCase));
            })
            .ToList();
    }

    private static string NormalizePath(
        string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd('\\', '/');
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>
    /// The last two parts of a tracker's host name, so tracker.example.org
    /// and announce.example.org count as the same tracker.
    /// </summary>
    public static string? TrackerDomain(
        string trackerUrl)
    {
        if (!Uri.TryCreate(trackerUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.HostNameType != UriHostNameType.Dns)
        {
            return uri.Host;
        }

        var labels = uri.Host.Split('.');

        return labels.Length <= 2
            ? uri.Host
            : string.Join('.', labels[^2..]);
    }
}
