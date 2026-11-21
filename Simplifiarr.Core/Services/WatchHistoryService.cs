using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

/// <summary>Who has watched a movie or show, and who asked for it.</summary>
public sealed class WatchRecord
{
    /// <summary>Jellyfin's path: a movie's file, or a show's folder.</summary>
    public string Path { get; set; } = string.Empty;

    public bool IsSeries { get; set; }

    public DateTime? AddedUtc { get; set; }

    /// <summary>Users who watched all of it.</summary>
    public List<string> WatchedBy { get; set; } = [];

    /// <summary>Users who watched some of it (a show), including those who watched all.</summary>
    public List<string> StartedBy { get; set; } = [];

    public DateTime? LastWatchedUtc { get; set; }

    /// <summary>From Seerr; null when nobody requested it there.</summary>
    public string? RequestedBy { get; set; }

    /// <summary>Null when not requested; otherwise whether the requester watched all of it.</summary>
    public bool? RequesterWatched { get; set; }

    public bool? RequesterStarted { get; set; }

    public int SeerrMediaId { get; set; }
}

public sealed class WatchHistoryState
{
    public DateTime? RefreshedUtc { get; set; }

    public List<WatchRecord> Records { get; set; } = [];
}

/// <summary>A title worth deciding about, and why.</summary>
public sealed record StorageReview(
    StorageTitle Title,
    WatchRecord Watch,
    string Reason);

public interface IWatchHistoryService
{
    DateTime? RefreshedUtc { get; }

    /// <summary>
    /// Reads every user's watch history from Jellyfin and who requested
    /// what from Seerr. Does nothing when Jellyfin isn't set up.
    /// </summary>
    Task RefreshAsync();

    WatchRecord? Find(StorageTitle title);
}

public class WatchHistoryService : IWatchHistoryService
{
    private readonly ISettingsService _settings;
    private readonly IJellyfinClient _jellyfin;
    private readonly ISeerrClient _seerr;

    private readonly object _sync = new();
    private readonly string _path = AppPaths.DataFile("watch-history.json");

    private WatchHistoryState _state;

    public WatchHistoryService(ISettingsService settings, IJellyfinClient jellyfin, ISeerrClient seerr)
    {
        _settings = settings;
        _jellyfin = jellyfin;
        _seerr = seerr;

        _state = JsonFileStore.Load<WatchHistoryState>(_path) ?? new WatchHistoryState();
    }

    public DateTime? RefreshedUtc
    {
        get { lock (_sync) { return _state.RefreshedUtc; } }
    }

    public async Task RefreshAsync()
    {
        var settings = await _settings.GetSettingsAsync();

        if (string.IsNullOrWhiteSpace(settings.JellyfinUrl) || string.IsNullOrWhiteSpace(settings.JellyfinApiKey))
        {
            return;
        }

        var users = await _jellyfin.GetUsersAsync();

        var perUser = new List<(string UserId, string UserName, IReadOnlyList<JellyfinWatchItem> Items)>();

        foreach (var (id, name) in users)
        {
            perUser.Add((id, name, await _jellyfin.GetWatchItemsAsync(id)));
        }

        IReadOnlyList<SeerrRequest> requests = [];

        try
        {
            requests = await _seerr.GetRequestsAsync();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            AppLog.Warn("Couldn't read requests from Seerr.", exception);
        }

        var records = Combine(perUser, requests);

        lock (_sync)
        {
            _state = new WatchHistoryState { RefreshedUtc = DateTime.UtcNow, Records = records };
        }

        JsonFileStore.Save(_path, _state);
    }

    /// <summary>One record per movie or show, from every user's view of it.</summary>
    public static List<WatchRecord> Combine(
        IEnumerable<(string UserId, string UserName, IReadOnlyList<JellyfinWatchItem> Items)> perUser,
        IReadOnlyList<SeerrRequest> requests)
    {
        var byItem = new Dictionary<string, (WatchRecord Record, HashSet<string> WatchedIds, HashSet<string> StartedIds)>();

        foreach (var (userId, userName, items) in perUser)
        {
            foreach (var item in items)
            {
                if (!byItem.TryGetValue(item.Id, out var entry))
                {
                    entry = (new WatchRecord { Path = item.Path, IsSeries = item.IsSeries, AddedUtc = item.AddedUtc }, [], []);
                    byItem[item.Id] = entry;
                }

                if (item.Watched)
                {
                    entry.Record.WatchedBy.Add(userName);
                    entry.WatchedIds.Add(NormaliseId(userId));
                }

                if (item.Started || item.Watched)
                {
                    entry.Record.StartedBy.Add(userName);
                    entry.StartedIds.Add(NormaliseId(userId));
                }

                if (item.LastWatchedUtc is { } last &&
                    (entry.Record.LastWatchedUtc is null || last > entry.Record.LastWatchedUtc))
                {
                    entry.Record.LastWatchedUtc = last;
                }
            }
        }

        foreach (var request in requests)
        {
            if (!byItem.TryGetValue(request.JellyfinItemId, out var entry) &&
                !byItem.TryGetValue(NormaliseId(request.JellyfinItemId), out entry))
            {
                // Jellyfin ids come with and without dashes.
                entry = byItem.FirstOrDefault(pair => NormaliseId(pair.Key) == NormaliseId(request.JellyfinItemId)).Value;

                if (entry.Record is null)
                {
                    continue;
                }
            }

            entry.Record.RequestedBy = request.RequestedBy;
            entry.Record.SeerrMediaId = request.MediaId;

            var requester = NormaliseId(request.RequesterJellyfinUserId);

            entry.Record.RequesterWatched = requester.Length > 0 && entry.WatchedIds.Contains(requester);
            entry.Record.RequesterStarted = requester.Length > 0 && entry.StartedIds.Contains(requester);
        }

        return byItem.Values.Select(entry => entry.Record).ToList();
    }

    public WatchRecord? Find(StorageTitle title)
    {
        lock (_sync)
        {
            return Match(title, _state.Records);
        }
    }

    /// <summary>A show's record has its folder; a movie's has a file inside the folder.</summary>
    public static WatchRecord? Match(StorageTitle title, IEnumerable<WatchRecord> records) =>
        records.FirstOrDefault(record =>
            string.Equals(
                System.IO.Path.TrimEndingDirectorySeparator(record.Path),
                System.IO.Path.TrimEndingDirectorySeparator(title.FolderPath),
                StringComparison.OrdinalIgnoreCase) ||
            PathContainment.IsPathUnderRoot(record.Path, title.FolderPath));

    /// <summary>
    /// Titles worth deciding about: the person who asked for it has watched
    /// it, or nobody has watched it in all the months it has been here.
    /// Largest first.
    /// </summary>
    public static List<StorageReview> Review(
        IEnumerable<StorageTitle> titles,
        Func<StorageTitle, WatchRecord?> find,
        int unwatchedMonths,
        DateTime nowUtc)
    {
        var reviews = new List<StorageReview>();

        foreach (var title in titles.Where(title => title.Kind is StorageKinds.Movies or StorageKinds.Tv))
        {
            if (find(title) is not { } watch)
            {
                continue;
            }

            if (watch.RequestedBy is not null && watch.RequesterWatched == true)
            {
                reviews.Add(new StorageReview(title, watch, $"{watch.RequestedBy} asked for it and has watched it"));
            }
            else if (watch.StartedBy.Count == 0 &&
                     watch.AddedUtc is { } added &&
                     added < nowUtc.AddMonths(-unwatchedMonths))
            {
                var months = (int)((nowUtc - added).TotalDays / 30.4);

                reviews.Add(
                    new StorageReview(
                        title,
                        watch,
                        watch.RequestedBy is null
                            ? $"Nobody has watched it in the {months} months it has been here"
                            : $"{watch.RequestedBy} asked for it {months} months ago and nobody has watched it"));
            }
        }

        return reviews.OrderByDescending(review => review.Title.SizeBytes).ToList();
    }

    private static string NormaliseId(string id) =>
        id.Replace("-", string.Empty).ToLowerInvariant();
}
