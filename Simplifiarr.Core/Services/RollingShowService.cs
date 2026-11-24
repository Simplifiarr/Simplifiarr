using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public sealed class RollingShowState
{
    public List<RollingShowRule> Rules { get; set; } = [];

    public List<RollingSeed> Seeds { get; set; } = [];

    /// <summary>Part of the webhook address Sonarr calls, so nothing else can.</summary>
    public string WebhookKey { get; set; } = string.Empty;
}

/// <summary>What a rule would do, or did.</summary>
public sealed record RollingPreview(
    List<RollingDeletion> Deletions,
    int EpisodesWithFiles,
    string? Problem);

/// <summary>
/// Rolling Shows: per-show rules that delete older episodes as new ones
/// arrive. Sonarr stops looking for each episode before its file goes, so
/// it isn't downloaded again. An episode still seeding loses its library
/// copy at once; its torrent is tagged and removed once it has seeded
/// enough.
/// </summary>
public interface IRollingShowService
{
    event Action? Changed;

    IReadOnlyList<RollingShowRule> Rules { get; }

    IReadOnlyList<RollingSeed> Seeds { get; }

    string WebhookKey { get; }

    void Save(RollingShowRule rule);

    void Remove(Guid ruleId);

    /// <summary>What the rule would delete right now. Changes nothing.</summary>
    Task<RollingPreview> PreviewAsync(RollingShowRule rule);

    /// <summary>Runs every rule that is on (or only the ones for this show).</summary>
    Task RunAsync(int? seriesId = null);

    /// <summary>Runs one rule now, even if it is off. For "Turn on and run".</summary>
    Task<string> RunRuleAsync(Guid ruleId);

    /// <summary>Removes tagged torrents that have seeded enough.</summary>
    Task CleanUpSeedsAsync();

    /// <summary>
    /// Tags a torrent whose library copy has gone, and removes it once it has
    /// seeded enough. Also used by Rolling Movies.
    /// </summary>
    Task KeepSeedingThenRemoveAsync(TorrentRecord torrent, Guid ruleId, int? seedDays, double? seedRatio);
}

public class RollingShowService : IRollingShowService
{
    public const string DeleteTag = "simplifiarr-delete";

    private readonly ISonarrShowClient _sonarr;
    private readonly IJellyfinClient _jellyfin;
    private readonly ISettingsService _settings;
    private readonly ITorrentClient _client;
    private readonly IHistoryService _history;

    private readonly object _sync = new();
    private readonly SemaphoreSlim _work = new(1, 1);
    private readonly string _path;

    private RollingShowState _state;

    public RollingShowService(
        ISonarrShowClient sonarr,
        IJellyfinClient jellyfin,
        ISettingsService settings,
        ITorrentClient client,
        IHistoryService history)
    {
        _sonarr = sonarr;
        _jellyfin = jellyfin;
        _settings = settings;
        _client = client;
        _history = history;

        _path = AppPaths.DataFile("rolling-shows.json");
        _state = JsonFileStore.Load<RollingShowState>(_path) ?? new RollingShowState();

        if (string.IsNullOrWhiteSpace(_state.WebhookKey))
        {
            _state.WebhookKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            Persist();
        }
    }

    /// <summary>
    /// For tests: how an episode is removed. Returns where it went in
    /// Quarantine, empty when deleted for good, or null when it couldn't be.
    /// </summary>
    public Func<string, bool, Task<string?>>? DeleteFile { get; set; }

    private Task<string?> RemoveAsync(string path, bool permanent) =>
        DeleteFile is not null ? DeleteFile(path, permanent)
        : permanent ? Task.FromResult<string?>(FileRemoval.Delete(path) ? string.Empty : null)
        : QuarantineService.MoveIntoAsync(_settings, path);

    /// <summary>For tests: the clock.</summary>
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public event Action? Changed;

    public IReadOnlyList<RollingShowRule> Rules
    {
        get { lock (_sync) { return _state.Rules.OrderBy(rule => rule.SeriesTitle, StringComparer.OrdinalIgnoreCase).ToList(); } }
    }

    public IReadOnlyList<RollingSeed> Seeds
    {
        get { lock (_sync) { return _state.Seeds.ToList(); } }
    }

    public string WebhookKey => _state.WebhookKey;

    public void Save(RollingShowRule rule)
    {
        lock (_sync)
        {
            _state.Rules.RemoveAll(existing => existing.Id == rule.Id);
            _state.Rules.Add(rule);
        }

        Persist();
    }

    public void Remove(Guid ruleId)
    {
        lock (_sync)
        {
            _state.Rules.RemoveAll(rule => rule.Id == ruleId);
        }

        Persist();
    }

    public async Task<RollingPreview> PreviewAsync(RollingShowRule rule)
    {
        try
        {
            var episodes = await _sonarr.GetEpisodesWithFilesAsync(rule.SeriesId);
            var watched = await WatchedPathsAsync(rule);

            return new RollingPreview(
                RollingShowPlanner.Plan(rule, episodes, episode => watched.Contains(episode.FilePath), UtcNow()),
                episodes.Count(episode => episode.Season > 0),
                null);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return new RollingPreview([], 0, $"Couldn't read the show: {exception.Message}");
        }
    }

    public async Task RunAsync(int? seriesId = null)
    {
        foreach (var rule in Rules.Where(rule => rule.IsOn && (seriesId is null || rule.SeriesId == seriesId)))
        {
            await RunRuleAsync(rule.Id);
        }

        await CleanUpSeedsAsync();
    }

    public async Task<string> RunRuleAsync(Guid ruleId)
    {
        await _work.WaitAsync();

        try
        {
            var rule = Rules.FirstOrDefault(entry => entry.Id == ruleId);

            if (rule is null)
            {
                return "That rule is gone.";
            }

            var message = await ApplyAsync(rule);

            rule.LastRunUtc = UtcNow();
            rule.LastRunMessage = message;

            Save(rule);

            return message;
        }
        finally
        {
            _work.Release();
        }
    }

    private async Task<string> ApplyAsync(RollingShowRule rule)
    {
        var preview = await PreviewAsync(rule);

        if (preview.Problem is not null)
        {
            return preview.Problem;
        }

        if (preview.Deletions.Count == 0)
        {
            return "Nothing to delete.";
        }

        // First Sonarr stops looking for them, so they aren't downloaded again.
        if (await _sonarr.UnmonitorAsync(preview.Deletions.Select(deletion => deletion.Episode.EpisodeId).ToList()) is { } refusal)
        {
            return $"{refusal} Nothing was deleted.";
        }

        var torrents = await _client.TryGetTorrentsAsync() ?? [];
        var downloadRoots = await DownloadRootsAsync();

        var deleted = 0;
        var freed = 0L;
        var seeding = 0;
        var problems = new List<string>();

        foreach (var deletion in preview.Deletions)
        {
            var file = deletion.Episode.FilePath;

            if (!File.Exists(file))
            {
                continue;
            }

            // Other names of this file in a downloads folder: still seeding.
            var seedNames =
                NativeHardlinks.GetIdentity(file) is { LinkCount: > 1 }
                    ? NativeHardlinks.GetLinkPaths(file)
                        .Where(name => !string.Equals(name, file, StringComparison.OrdinalIgnoreCase))
                        .Where(name => downloadRoots.Any(root => PathContainment.IsPathUnderRoot(name, root)))
                        .ToList()
                    : [];

            var quarantined = await RemoveAsync(file, rule.Permanent);

            if (quarantined is null)
            {
                problems.Add($"{Path.GetFileName(file)} couldn't be deleted");
                continue;
            }

            deleted++;

            if (seedNames.Count == 0)
            {
                freed += deletion.Episode.SizeBytes;
            }

            foreach (var torrent in TorrentsHolding(torrents, seedNames))
            {
                await TagForRemovalAsync(rule, torrent);
                seeding++;
            }

            await _history.AddRecordAsync(
                new HistoryRecord
                {
                    ActionType = HistoryActionType.EpisodeRolledOff,
                    FileName = Path.GetFileName(file),
                    LibraryPath = file,
                    QuarantinePath = quarantined,
                    SizeBytes = deletion.Episode.SizeBytes,
                    Success = true,
                    Message = $"{rule.SeriesTitle} S{deletion.Episode.Season:00}E{deletion.Episode.Number:00}: {deletion.Reason}. " +
                              (rule.Permanent ? "Deleted for good." : "Moved to Quarantine.")
                });
        }

        await _sonarr.RescanAsync(rule.SeriesId);

        var message = $"Deleted {deleted} episode(s), freeing {SizeFormatter.FormatBytes(freed)}.";

        if (seeding > 0)
        {
            message += $" {seeding} still seeding; tagged {DeleteTag} and removed once seeded enough.";
        }

        if (problems.Count > 0)
        {
            message += " " + string.Join("; ", problems) + ".";
        }

        return message;
    }

    private static IEnumerable<TorrentRecord> TorrentsHolding(IEnumerable<TorrentRecord> torrents, IReadOnlyCollection<string> names) =>
        torrents.Where(torrent =>
            !string.IsNullOrWhiteSpace(torrent.ContentPath) &&
            names.Any(name =>
                string.Equals(name, torrent.ContentPath, StringComparison.OrdinalIgnoreCase) ||
                PathContainment.IsPathUnderRoot(name, torrent.ContentPath)));

    private Task TagForRemovalAsync(RollingShowRule rule, TorrentRecord torrent) =>
        KeepSeedingThenRemoveAsync(torrent, rule.Id, rule.SeedDays, rule.SeedRatio);

    public async Task KeepSeedingThenRemoveAsync(TorrentRecord torrent, Guid ruleId, int? seedDays, double? seedRatio)
    {
        lock (_sync)
        {
            if (_state.Seeds.Any(seed => string.Equals(seed.TorrentHash, torrent.TorrentHash, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            _state.Seeds.Add(
                new RollingSeed
                {
                    TorrentHash = torrent.TorrentHash,
                    Name = torrent.Name,
                    RuleId = ruleId,
                    TaggedUtc = UtcNow(),
                    SeedDays = seedDays,
                    SeedRatio = seedRatio
                });
        }

        Persist();

        await _client.AddTagAsync(torrent.TorrentHash, DeleteTag);
    }

    public async Task CleanUpSeedsAsync()
    {
        if (Seeds.Count == 0)
        {
            return;
        }

        var torrents = await _client.TryGetTorrentsAsync();

        // Can't see the client: never treat that as "gone".
        if (torrents is null)
        {
            return;
        }

        foreach (var seed in Seeds)
        {
            var torrent =
                torrents.FirstOrDefault(entry => string.Equals(entry.TorrentHash, seed.TorrentHash, StringComparison.OrdinalIgnoreCase));

            if (torrent is null)
            {
                Forget(seed);
                continue;
            }

            if (!HasSeededEnough(seed, torrent, UtcNow()))
            {
                continue;
            }

            // A season pack may still hold episodes that are in the library:
            // it keeps seeding until they have gone too.
            if (await StillInLibraryAsync(torrent))
            {
                continue;
            }

            if (await _client.RemoveTorrentAsync(torrent.TorrentHash, deleteFiles: true) is null)
            {
                Forget(seed);

                await _history.AddRecordAsync(
                    new HistoryRecord
                    {
                        ActionType = HistoryActionType.EpisodeRolledOff,
                        FileName = torrent.Name,
                        DownloadPath = torrent.ContentPath,
                        SizeBytes = torrent.SizeBytes,
                        Success = true,
                        Message = $"Removed {torrent.Name} from {_client.Name} after seeding (ratio {torrent.Ratio:0.##})."
                    });
            }
        }
    }

    /// <summary>Its seed target is met, or the client has stopped it after finishing.</summary>
    public static bool HasSeededEnough(RollingSeed seed, TorrentRecord torrent, DateTime nowUtc)
    {
        if (torrent.Progress < 1)
        {
            return false;
        }

        if (torrent.Status == TorrentStatus.Stopped)
        {
            return true;
        }

        var byDays =
            seed.SeedDays is { } days &&
            (torrent.SeedingTime >= TimeSpan.FromDays(days) || seed.TaggedUtc <= nowUtc.AddDays(-days));

        var byRatio = seed.SeedRatio is { } ratio && torrent.Ratio >= ratio;

        return byDays || byRatio;
    }

    private async Task<bool> StillInLibraryAsync(TorrentRecord torrent)
    {
        var files = await _client.GetTorrentFilesAsync(torrent.TorrentHash);

        if (files is null)
        {
            return true;
        }

        return files.Any(file =>
            NativeHardlinks.GetIdentity(Path.Combine(torrent.SavePath, file.RelativePath.Replace('/', Path.DirectorySeparatorChar))) is { LinkCount: > 1 });
    }

    private async Task<HashSet<string>> WatchedPathsAsync(RollingShowRule rule)
    {
        if (rule.Watched == WatchRequirement.None)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var watchers = rule.Watched == WatchRequirement.User ? rule.Watchers : [];
        var perUser = new List<(string Name, IEnumerable<string> Paths)>();

        foreach (var (id, name) in await _jellyfin.GetUsersAsync())
        {
            if (watchers.Count > 0 && !watchers.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            perUser.Add((name, (await _jellyfin.GetWatchedEpisodePathsAsync(id))
                .Where(path => PathContainment.IsPathUnderRoot(path, rule.SeriesPath))
                .ToList()));
        }

        return RollingShowPlanner.WatchedPaths(rule, perUser);
    }

    private async Task<List<string>> DownloadRootsAsync()
    {
        var settings = await _settings.GetSettingsAsync();

        return settings.DriveProfiles
            .Where(profile => profile.Enabled)
            .SelectMany(profile => profile.AllDownloadPaths().Append(profile.SeedOnlyPath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void Forget(RollingSeed seed)
    {
        lock (_sync)
        {
            _state.Seeds.RemoveAll(entry => entry.TorrentHash == seed.TorrentHash);
        }

        Persist();
    }

    private void Persist()
    {
        lock (_sync)
        {
            JsonFileStore.Save(_path, _state);
        }

        Changed?.Invoke();
    }
}
