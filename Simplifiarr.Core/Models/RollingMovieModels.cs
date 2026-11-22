using System.Text.Json.Serialization;

namespace Simplifiarr.Core.Models;

/// <summary>Whose watching starts the "days after watched" clock.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MovieWatcher>))]
public enum MovieWatcher
{
    /// <summary>The person who asked for it in Seerr; anyone, when nobody asked.</summary>
    Requester,

    /// <summary>Anyone at all.</summary>
    Anyone,

    /// <summary>Every one of <see cref="RollingMovieRule.WatchedByUsers"/>.</summary>
    People
}

/// <summary>
/// Which movies a rule covers, and when they go. A movie is covered when it
/// matches any of the "which" choices, and goes when any of the "when"
/// choices says so. Favorites, kept movies and the keep tag always stay.
/// </summary>
public sealed class RollingMovieRule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // ------------------------------------------------------------ Which

    /// <summary>Radarr tags (by name).</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Anything requested in Seerr, by anyone.</summary>
    public bool AnyRequest { get; set; }

    /// <summary>Requested in Seerr by one of these people.</summary>
    public List<string> RequestedBy { get; set; } = [];

    /// <summary>Movies whose folder is inside one of these.</summary>
    public List<string> Folders { get; set; } = [];

    /// <summary>Picked one by one: Radarr ids.</summary>
    public List<int> MovieIds { get; set; } = [];

    /// <summary>Names for <see cref="MovieIds"/>, so the page can show them without Radarr.</summary>
    public Dictionary<int, string> MovieNames { get; set; } = [];

    // ------------------------------------------------------------- When

    /// <summary>Goes this many days after it has been watched.</summary>
    public int? DaysAfterWatched { get; set; }

    public MovieWatcher WatchedBy { get; set; } = MovieWatcher.Requester;

    /// <summary>For <see cref="MovieWatcher.People"/>: all of them must have watched.</summary>
    public List<string> WatchedByUsers { get; set; } = [];

    /// <summary>Goes when nobody has watched it in this many months since it arrived.</summary>
    public int? UnwatchedMonths { get; set; }

    /// <summary>Goes this many days after it arrived, watched or not.</summary>
    public int? DaysAfterAdded { get; set; }

    /// <summary>
    /// Goes when its drive is over its full limit: longest since watched
    /// first, then the longest unwatched, until the drive is under.
    /// </summary>
    public bool WhenDriveFull { get; set; }

    // ------------------------------------------------------------ How

    /// <summary>Delete for good instead of sending to the Recycle Bin.</summary>
    public bool Permanent { get; set; }

    /// <summary>For a deleted movie still seeding: remove its torrent after this many days.</summary>
    public int? SeedDays { get; set; } = 7;

    /// <summary>Or once it reaches this ratio, whichever comes first.</summary>
    public double? SeedRatio { get; set; }

    public bool IsOn { get; set; }

    public DateTime? LastRunUtc { get; set; }

    public string LastRunMessage { get; set; } = string.Empty;

    [JsonIgnore]
    public bool HasAnyMovies =>
        Tags.Count > 0 || AnyRequest || RequestedBy.Count > 0 || Folders.Count > 0 || MovieIds.Count > 0;

    [JsonIgnore]
    public bool HasAnyTrigger =>
        DaysAfterWatched is > 0 || UnwatchedMonths is > 0 || DaysAfterAdded is > 0 || WhenDriveFull;
}

/// <summary>A movie in Radarr that has a file.</summary>
public sealed record RollingMovie(
    int Id,
    string Title,
    int? Year,
    string FolderPath,
    long SizeBytes,
    DateTime? AddedUtc,
    IReadOnlyList<string> Tags)
{
    public string Name => Year is { } year ? $"{Title} ({year})" : Title;
}

/// <summary>A movie with what is known about it from Jellyfin, Seerr and the disk.</summary>
public sealed record RollingMovieFacts(
    RollingMovie Movie,
    string? RequestedBy,
    IReadOnlyDictionary<string, DateTime?> WatchedOn,
    DateTime? RequesterWatchedUtc,
    bool AnyoneStarted,
    bool Favorite,
    bool Kept,
    bool Seeding,
    string DriveRoot,
    int SeerrMediaId);

/// <summary>A movie a rule would delete, and why.</summary>
public sealed record MovieDeletion(
    RollingMovieFacts Facts,
    string Reason);

/// <summary>What a rule would delete right now.</summary>
public sealed record MoviePlan(
    List<MovieDeletion> Deletions,
    int Matched,
    int Protected);
