using System.Text.Json.Serialization;

namespace Simplifiarr.Core.Models;

/// <summary>When the previous season goes, for a "current season only" rule.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SeasonSwitch>))]
public enum SeasonSwitch
{
    /// <summary>As soon as episode 1 of the new season is in.</summary>
    FirstEpisode,

    /// <summary>Once a number of the new season's episodes are in.</summary>
    AfterEpisodes,

    /// <summary>A number of days after the new season's first episode aired.</summary>
    AfterDays
}

/// <summary>The unit in "Keep the last N ...".</summary>
[JsonConverter(typeof(JsonStringEnumConverter<KeepUnit>))]
public enum KeepUnit
{
    Episodes,
    Days,
    Weeks,
    Seasons
}

/// <summary>Whose watching counts before an episode may go.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WatchRequirement>))]
public enum WatchRequirement
{
    /// <summary>Episodes go when the rule says, watched or not.</summary>
    None,

    /// <summary>Only once someone has watched it.</summary>
    Anyone,

    /// <summary>Only once a particular Jellyfin user has watched it.</summary>
    User
}

/// <summary>
/// How many episodes of one show to keep. Every limit that is set applies:
/// an episode goes when any of them says it's too old, except that the
/// newest <see cref="MinimumKeep"/> episodes always stay.
/// </summary>
public sealed class RollingShowRule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public int SeriesId { get; set; }

    public string SeriesTitle { get; set; } = string.Empty;

    /// <summary>Sonarr's folder for the show.</summary>
    public string SeriesPath { get; set; } = string.Empty;

    /// <summary>Keep only the newest this many episodes.</summary>
    public int? KeepLatest { get; set; }

    /// <summary>Keep only episodes that aired within this many days.</summary>
    public int? KeepDays { get; set; }

    /// <summary>
    /// Keep only the newest this many seasons: an older season goes once a
    /// newer one has started (see <see cref="SeasonSwitch"/>).
    /// </summary>
    public int? KeepSeasons { get; set; }

    /// <summary>From before seasons could be counted: the same as 1 season.</summary>
    public bool KeepCurrentSeasonOnly { get; set; }

    [JsonIgnore]
    public int? SeasonsToKeep => KeepSeasons is > 0 ? KeepSeasons : KeepCurrentSeasonOnly ? 1 : null;

    /// <summary>How the page shows the limit; days and weeks are both kept as days.</summary>
    public KeepUnit? Unit { get; set; }

    /// <summary>"Keep the last N unit", from whichever limit is set.</summary>
    [JsonIgnore]
    public (int Count, KeepUnit Unit) Simple =>
        SeasonsToKeep is { } seasons ? (seasons, KeepUnit.Seasons)
        : KeepLatest is > 0 and var latest ? (latest, KeepUnit.Episodes)
        : KeepDays is > 0 and var days
            ? Unit == KeepUnit.Weeks && days % 7 == 0 ? (days / 7, KeepUnit.Weeks) : (days, KeepUnit.Days)
            : (7, KeepUnit.Days);

    /// <summary>Sets the one limit the sentence describes, clearing the others.</summary>
    public void SetSimple(int count, KeepUnit unit)
    {
        count = Math.Max(1, count);

        KeepLatest = unit == KeepUnit.Episodes ? count : null;
        KeepDays = unit switch { KeepUnit.Days => count, KeepUnit.Weeks => count * 7, _ => null };
        KeepSeasons = unit == KeepUnit.Seasons ? count : null;
        KeepCurrentSeasonOnly = false;
        Unit = unit;
    }

    public SeasonSwitch SeasonSwitch { get; set; } = SeasonSwitch.FirstEpisode;

    /// <summary>For <see cref="SeasonSwitch.AfterEpisodes"/> or <see cref="SeasonSwitch.AfterDays"/>.</summary>
    public int SeasonSwitchValue { get; set; } = 1;

    /// <summary>Never keep fewer than this many episodes, whatever the other limits say.</summary>
    public int? MinimumKeep { get; set; }

    public WatchRequirement Watched { get; set; } = WatchRequirement.None;

    /// <summary>
    /// For <see cref="WatchRequirement.User"/>: the Jellyfin users who must
    /// all have watched an episode before it goes.
    /// </summary>
    public List<string> WatchedByUsers { get; set; } = [];

    /// <summary>From before several people could be picked: one user's name.</summary>
    public string WatchedBy { get; set; } = string.Empty;

    /// <summary>Everyone who must have watched, from either field.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Watchers =>
        WatchedByUsers.Count > 0 ? WatchedByUsers
        : string.IsNullOrWhiteSpace(WatchedBy) ? []
        : [WatchedBy];

    /// <summary>
    /// With a watch requirement: an unwatched episode goes anyway once it
    /// aired this many days ago. Null: unwatched episodes wait forever.
    /// </summary>
    public int? UnwatchedAfterDays { get; set; }

    /// <summary>Delete for good instead of sending to the Recycle Bin.</summary>
    public bool Permanent { get; set; }

    /// <summary>
    /// For a deleted episode that is still seeding: remove its torrent once
    /// it has seeded this many days. Null with no ratio: once the client
    /// stops it.
    /// </summary>
    public int? SeedDays { get; set; } = 7;

    /// <summary>Or once it reaches this ratio, whichever comes first.</summary>
    public double? SeedRatio { get; set; }

    /// <summary>
    /// False until you've seen the preview and turned it on. Off rules and
    /// paused rules never delete anything.
    /// </summary>
    public bool IsOn { get; set; }

    public DateTime? LastRunUtc { get; set; }

    public string LastRunMessage { get; set; } = string.Empty;

    public bool HasAnyLimit =>
        KeepLatest is > 0 || KeepDays is > 0 || SeasonsToKeep is > 0;
}

/// <summary>An episode with a file, as Sonarr has it.</summary>
public sealed record ShowEpisode(
    int EpisodeId,
    int EpisodeFileId,
    int Season,
    int Number,
    string Title,
    DateTime? AiredUtc,
    string FilePath,
    long SizeBytes,
    bool Monitored);

/// <summary>An episode a rule would delete, and why.</summary>
public sealed record RollingDeletion(
    ShowEpisode Episode,
    string Reason);

/// <summary>A torrent kept seeding after its episode left the library.</summary>
public sealed class RollingSeed
{
    public string TorrentHash { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public Guid RuleId { get; set; }

    public DateTime TaggedUtc { get; set; } = DateTime.UtcNow;

    public int? SeedDays { get; set; }

    public double? SeedRatio { get; set; }
}
