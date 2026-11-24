using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public sealed class RollingMovieState
{
    public List<RollingMovieRule> Rules { get; set; } = [];

    /// <summary>Movies with this Radarr tag are never deleted.</summary>
    public string KeepTag { get; set; } = "keep";
}

/// <summary>What a movie rule would do right now.</summary>
public sealed record MovieRulePreview(
    MoviePlan Plan,
    string? Problem);

/// <summary>
/// Rolling Movies: rules that delete movies once they've been watched, sat
/// unwatched too long, or their drive is full. A movie leaves Radarr (so
/// it isn't downloaded again) and Seerr (so it can be asked for again),
/// then its folder goes. Favorites, kept movies and the keep tag always stay.
/// </summary>
public interface IRollingMovieService
{
    event Action? Changed;

    IReadOnlyList<RollingMovieRule> Rules { get; }

    string KeepTag { get; set; }

    void Save(RollingMovieRule rule);

    void Remove(Guid ruleId);

    /// <summary>Every movie in Radarr with a file. Kept for a couple of minutes.</summary>
    Task<IReadOnlyList<RollingMovie>> GetMoviesAsync(bool fresh = false);

    /// <summary>Reads watch history again when it's over half an hour old.</summary>
    Task RefreshWatchHistoryIfOldAsync();

    /// <summary>What the rule would delete right now. Changes nothing.</summary>
    Task<MovieRulePreview> PreviewAsync(RollingMovieRule rule);

    /// <summary>Runs every rule that is on.</summary>
    Task RunAsync();

    /// <summary>Runs one rule now.</summary>
    Task<string> RunRuleAsync(Guid ruleId);
}

public class RollingMovieService : IRollingMovieService
{
    private static readonly TimeSpan MoviesKeptFor = TimeSpan.FromMinutes(2);

    private readonly IRadarrMovieClient _radarr;
    private readonly IArrPathService _arr;
    private readonly IWatchHistoryService _watch;
    private readonly ISeerrClient _seerr;
    private readonly IIgnoredItemsService _ignored;
    private readonly IStorageService _storage;
    private readonly ITorrentClient _client;
    private readonly IRollingShowService _seeds;
    private readonly ISettingsService _settings;
    private readonly IHistoryService _history;

    private readonly object _sync = new();
    private readonly SemaphoreSlim _work = new(1, 1);
    private readonly string _path;

    private RollingMovieState _state;
    private (DateTime Utc, IReadOnlyList<RollingMovie> Movies)? _movies;

    public RollingMovieService(
        IRadarrMovieClient radarr,
        IArrPathService arr,
        IWatchHistoryService watch,
        ISeerrClient seerr,
        IIgnoredItemsService ignored,
        IStorageService storage,
        ITorrentClient client,
        IRollingShowService seeds,
        ISettingsService settings,
        IHistoryService history)
    {
        _radarr = radarr;
        _arr = arr;
        _watch = watch;
        _seerr = seerr;
        _ignored = ignored;
        _storage = storage;
        _client = client;
        _seeds = seeds;
        _settings = settings;
        _history = history;

        _path = AppPaths.DataFile("rolling-movies.json");
        _state = JsonFileStore.Load<RollingMovieState>(_path) ?? new RollingMovieState();
    }

    /// <summary>
    /// For tests: how a movie's folder is removed. Returns where it went in
    /// Quarantine, empty when deleted for good, or null when it couldn't be.
    /// </summary>
    public Func<string, bool, Task<string?>>? DeleteFolder { get; set; }

    private Task<string?> RemoveAsync(string folder, bool permanent) =>
        DeleteFolder is not null ? DeleteFolder(folder, permanent)
        : permanent ? Task.FromResult<string?>(FileRemoval.Delete(folder) ? string.Empty : null)
        : QuarantineService.MoveIntoAsync(_settings, folder);

    /// <summary>For tests: the clock.</summary>
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public event Action? Changed;

    public IReadOnlyList<RollingMovieRule> Rules
    {
        get { lock (_sync) { return _state.Rules.ToList(); } }
    }

    public string KeepTag
    {
        get { lock (_sync) { return _state.KeepTag; } }
        set
        {
            lock (_sync) { _state.KeepTag = (value ?? string.Empty).Trim(); }
            Persist();
        }
    }

    public void Save(RollingMovieRule rule)
    {
        lock (_sync)
        {
            var index = _state.Rules.FindIndex(existing => existing.Id == rule.Id);

            if (index >= 0)
            {
                _state.Rules[index] = rule;
            }
            else
            {
                _state.Rules.Add(rule);
            }
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

    public async Task<IReadOnlyList<RollingMovie>> GetMoviesAsync(bool fresh = false)
    {
        if (!fresh && _movies is { } kept && UtcNow() - kept.Utc < MoviesKeptFor)
        {
            return kept.Movies;
        }

        var movies = await _radarr.GetMoviesWithFilesAsync();
        _movies = (UtcNow(), movies);

        return movies;
    }

    public async Task RefreshWatchHistoryIfOldAsync()
    {
        if (_watch.RefreshedUtc is { } refreshed && UtcNow() - refreshed < TimeSpan.FromMinutes(30))
        {
            return;
        }

        try
        {
            await _watch.RefreshAsync();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {
            AppLog.Warn("Couldn't read watch history for Rolling Movies.", exception);
        }
    }

    public async Task<MovieRulePreview> PreviewAsync(RollingMovieRule rule)
    {
        try
        {
            var (facts, overLimit) = await GatherAsync(fresh: false);

            return new MovieRulePreview(RollingMoviePlanner.Plan(rule, facts, KeepTag, overLimit, UtcNow()), null);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return new MovieRulePreview(new MoviePlan([], 0, 0), $"Couldn't read Radarr: {exception.Message}");
        }
    }

    public async Task RunAsync()
    {
        foreach (var rule in Rules.Where(rule => rule.IsOn))
        {
            await RunRuleAsync(rule.Id);
        }
    }

    public async Task<string> RunRuleAsync(Guid ruleId)
    {
        await _work.WaitAsync();

        try
        {
            if (Rules.FirstOrDefault(entry => entry.Id == ruleId) is not { } rule)
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

    // Everything the planner needs, from Radarr, Jellyfin, Seerr and Storage.
    private async Task<(List<RollingMovieFacts> Facts, Dictionary<string, long> OverLimit)> GatherAsync(bool fresh)
    {
        var movies = await GetMoviesAsync(fresh);

        var records =
            _watch.Records
                .Where(record => !record.IsSeries && record.Path.Length > 0)
                .GroupBy(record => Folder(Path.GetDirectoryName(record.Path) ?? record.Path), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var drives = await _storage.GetDrivesAsync();

        var titles =
            drives.SelectMany(drive => drive.Titles)
                .GroupBy(title => Folder(title.FolderPath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var overLimit =
            drives.Where(drive => drive.IsAvailable && drive.IsOverLimit)
                .ToDictionary(drive => drive.Root, drive => drive.OverLimitBytes, StringComparer.OrdinalIgnoreCase);

        var facts = new List<RollingMovieFacts>();

        foreach (var movie in movies)
        {
            var folder = Folder(movie.FolderPath);
            var watch = records.GetValueOrDefault(folder);

            facts.Add(
                new RollingMovieFacts(
                    movie,
                    watch?.RequestedBy,
                    watch?.WatchedOn ?? new Dictionary<string, DateTime?>(),
                    watch?.RequesterWatchedUtc,
                    watch?.StartedBy.Count > 0,
                    watch?.FavoriteOf.Count > 0,
                    _ignored.IsIgnored(movie.FolderPath) || _ignored.IsIgnored(folder),
                    titles.GetValueOrDefault(folder)?.IsSeeding == true,
                    Path.GetPathRoot(movie.FolderPath) ?? string.Empty,
                    watch?.SeerrMediaId ?? 0));
        }

        return (facts, overLimit);
    }

    private async Task<string> ApplyAsync(RollingMovieRule rule)
    {
        MoviePlan plan;

        try
        {
            var (facts, overLimit) = await GatherAsync(fresh: true);
            plan = RollingMoviePlanner.Plan(rule, facts, KeepTag, overLimit, UtcNow());
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return $"Couldn't read Radarr: {exception.Message}";
        }

        if (plan.Deletions.Count == 0)
        {
            return "Nothing to delete.";
        }

        var torrents = await _client.TryGetTorrentsAsync() ?? [];
        var downloadRoots = await DownloadRootsAsync();

        var deleted = 0;
        var freed = 0L;
        var seeding = 0;
        var problems = new List<string>();

        foreach (var deletion in plan.Deletions)
        {
            var movie = deletion.Facts.Movie;
            var folder = Folder(movie.FolderPath);

            if (!Directory.Exists(folder))
            {
                continue;
            }

            // Other names of its files in a downloads folder: still seeding.
            var seedNames =
                Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    .Where(file => NativeHardlinks.GetIdentity(file) is { LinkCount: > 1 })
                    .SelectMany(NativeHardlinks.GetLinkPaths)
                    .Where(name => !PathContainment.IsPathUnderRoot(name, folder))
                    .Where(name => downloadRoots.Any(root => PathContainment.IsPathUnderRoot(name, root)))
                    .ToList();

            // First Radarr lets go of it, so it isn't downloaded again.
            if (await _arr.RemoveAsync(StorageKinds.Movies, new ArrItem("Radarr", movie.Id, movie.Title, movie.FolderPath, movie.Year)) is { } refusal)
            {
                problems.Add($"{movie.Name}: {refusal}");
                continue;
            }

            string? quarantined;

            try
            {
                quarantined = await RemoveAsync(folder, rule.Permanent);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"Couldn't delete {folder}.", exception);
                quarantined = null;
            }

            if (quarantined is null)
            {
                problems.Add($"{movie.Name} left Radarr but its folder couldn't be deleted; delete {folder} yourself");
                await RecordAsync(movie, folder, false, $"{deletion.Reason}. Removed from Radarr, but the folder couldn't be deleted.");
                continue;
            }

            deleted++;

            if (seedNames.Count == 0)
            {
                freed += movie.SizeBytes;
            }

            await _seerr.ForgetMediaAsync(deletion.Facts.SeerrMediaId);
            _storage.Forget(new StorageTitle { FolderPath = movie.FolderPath });

            foreach (var torrent in TorrentsHolding(torrents, seedNames))
            {
                await _seeds.KeepSeedingThenRemoveAsync(torrent, rule.Id, rule.SeedDays, rule.SeedRatio);
                seeding++;
            }

            await RecordAsync(
                movie,
                folder,
                true,
                $"{deletion.Reason}. " + (rule.Permanent ? "Deleted for good." : "Moved to Quarantine."),
                quarantined);
        }

        _movies = null;

        var message = $"Deleted {deleted} movie(s), freeing {SizeFormatter.FormatBytes(freed)}.";

        if (seeding > 0)
        {
            message += $" {seeding} still seeding; tagged {RollingShowService.DeleteTag} and removed once seeded enough.";
        }

        if (problems.Count > 0)
        {
            message += " " + string.Join("; ", problems) + ".";
        }

        return message;
    }

    private Task RecordAsync(RollingMovie movie, string folder, bool success, string message, string? quarantinePath = null) =>
        _history.AddRecordAsync(
            new HistoryRecord
            {
                ActionType = HistoryActionType.MovieRolledOff,
                FileName = movie.Name,
                LibraryPath = folder,
                QuarantinePath = quarantinePath ?? string.Empty,
                SizeBytes = movie.SizeBytes,
                Success = success,
                Message = message
            });

    private static IEnumerable<TorrentRecord> TorrentsHolding(IEnumerable<TorrentRecord> torrents, IReadOnlyCollection<string> names) =>
        torrents.Where(torrent =>
            !string.IsNullOrWhiteSpace(torrent.ContentPath) &&
            names.Any(name =>
                string.Equals(name, torrent.ContentPath, StringComparison.OrdinalIgnoreCase) ||
                PathContainment.IsPathUnderRoot(name, torrent.ContentPath)));

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

    private static string Folder(string path) =>
        Path.TrimEndingDirectorySeparator(path.Replace('/', '\\'));

    private void Persist()
    {
        RollingMovieState copy;

        lock (_sync)
        {
            copy = new RollingMovieState { Rules = _state.Rules.ToList(), KeepTag = _state.KeepTag };
        }

        JsonFileStore.Save(_path, copy);
        Changed?.Invoke();
    }
}
