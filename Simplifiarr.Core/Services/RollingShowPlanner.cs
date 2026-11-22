using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Decides which episodes a rule deletes. Pure: nothing is read or changed.
/// </summary>
public static class RollingShowPlanner
{
    /// <param name="episodes">The show's episodes that have a file.</param>
    /// <param name="isWatched">
    /// Whether the rule's watch requirement is met for an episode. Not
    /// asked when the rule has none.
    /// </param>
    public static List<RollingDeletion> Plan(
        RollingShowRule rule,
        IReadOnlyList<ShowEpisode> episodes,
        Func<ShowEpisode, bool> isWatched,
        DateTime nowUtc)
    {
        if (!rule.HasAnyLimit)
        {
            return [];
        }

        // Specials are left alone.
        var newestFirst =
            episodes
                .Where(episode => episode.Season > 0)
                .OrderByDescending(episode => episode.AiredUtc ?? DateTime.MinValue)
                .ThenByDescending(episode => episode.Season)
                .ThenByDescending(episode => episode.Number)
                .ToList();

        var floor = Math.Max(0, rule.MinimumKeep ?? 0);

        var currentSeason = newestFirst.Count == 0 ? 0 : newestFirst.Max(episode => episode.Season);
        var seasonHasSwitched = SeasonHasSwitched(rule, newestFirst, currentSeason, nowUtc);

        // Keeping 2 seasons with season 36 started keeps 35 and 36.
        var oldestKept = currentSeason - ((rule.SeasonsToKeep ?? 1) - 1);

        var deletions = new List<RollingDeletion>();

        for (var index = 0; index < newestFirst.Count; index++)
        {
            var episode = newestFirst[index];

            if (index < floor)
            {
                continue;
            }

            var reason = TooOld(rule, episode, index, oldestKept, seasonHasSwitched, nowUtc);

            if (reason is null)
            {
                continue;
            }

            if (rule.Watched != WatchRequirement.None && !isWatched(episode))
            {
                // Unwatched: it waits, unless it has waited long enough.
                if (rule.UnwatchedAfterDays is not { } days ||
                    episode.AiredUtc is not { } aired ||
                    aired > nowUtc.AddDays(-days))
                {
                    continue;
                }

                reason += $"; unwatched, but aired over {days} days ago";
            }

            deletions.Add(new RollingDeletion(episode, reason));
        }

        return deletions;
    }

    private static string? TooOld(
        RollingShowRule rule,
        ShowEpisode episode,
        int index,
        int oldestKept,
        bool seasonHasSwitched,
        DateTime nowUtc)
    {
        if (rule.KeepLatest is > 0 and var latest && index >= latest)
        {
            return $"Not one of the newest {latest}";
        }

        if (rule.KeepDays is > 0 and var days &&
            episode.AiredUtc is { } aired &&
            aired < nowUtc.AddDays(-days))
        {
            return $"Aired over {days} days ago";
        }

        if (rule.SeasonsToKeep is > 0 && seasonHasSwitched && episode.Season < oldestKept)
        {
            return $"Season {episode.Season}; a newer season has started";
        }

        return null;
    }

    private static bool SeasonHasSwitched(
        RollingShowRule rule,
        IReadOnlyList<ShowEpisode> episodes,
        int currentSeason,
        DateTime nowUtc)
    {
        if (rule.SeasonsToKeep is not > 0 || episodes.All(episode => episode.Season == currentSeason))
        {
            return false;
        }

        var current = episodes.Where(episode => episode.Season == currentSeason).ToList();

        return rule.SeasonSwitch switch
        {
            SeasonSwitch.AfterEpisodes => current.Count >= Math.Max(1, rule.SeasonSwitchValue),
            SeasonSwitch.AfterDays =>
                current.Min(episode => episode.AiredUtc) is { } started &&
                started <= nowUtc.AddDays(-Math.Max(0, rule.SeasonSwitchValue)),
            _ => current.Count > 0
        };
    }

    /// <summary>The rule in a sentence, for the page: "Keeps the last 7 days".</summary>
    public static string Describe(RollingShowRule rule)
    {
        if (!rule.HasAnyLimit)
        {
            return "No limit set";
        }

        var (count, unit) = rule.Simple;

        var text = $"Keeps the last {count} {UnitName(unit, count)}";

        if (rule.MinimumKeep is > 0 and var floor && unit != KeepUnit.Episodes)
        {
            text += $" (at least {floor} episode{(floor == 1 ? "" : "s")})";
        }

        text += rule.Watched switch
        {
            WatchRequirement.Anyone => ", once watched",
            WatchRequirement.User when rule.Watchers.Count > 0 =>
                $", once {JoinNames(rule.Watchers)} {(rule.Watchers.Count == 1 ? "has" : "have")} watched",
            WatchRequirement.User => ", once watched",
            _ => string.Empty
        };

        return text;
    }

    /// <summary>
    /// The episodes (by file path) that meet the rule's watch requirement:
    /// watched by anyone, or by every one of the rule's people.
    /// </summary>
    public static HashSet<string> WatchedPaths(
        RollingShowRule rule,
        IEnumerable<(string Name, IEnumerable<string> Paths)> perUser)
    {
        var watched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (rule.Watched == WatchRequirement.None)
        {
            return watched;
        }

        var watchers = rule.Watched == WatchRequirement.User ? rule.Watchers : [];

        if (watchers.Count == 0)
        {
            foreach (var (_, paths) in perUser)
            {
                watched.UnionWith(paths);
            }

            return watched;
        }

        var byName = perUser
            .GroupBy(user => user.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.SelectMany(user => user.Paths).ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < watchers.Count; index++)
        {
            // Someone who's gone from Jellyfin can never watch: nothing qualifies.
            if (!byName.TryGetValue(watchers[index], out var theirs))
            {
                return [];
            }

            if (index == 0)
            {
                watched.UnionWith(theirs);
            }
            else
            {
                watched.IntersectWith(theirs);
            }
        }

        return watched;
    }

    public static string JoinNames(IReadOnlyList<string> names) =>
        names.Count switch
        {
            0 => string.Empty,
            1 => names[0],
            _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}"
        };

    public static string UnitName(KeepUnit unit, int count) =>
        (unit, count == 1) switch
        {
            (KeepUnit.Episodes, true) => "episode",
            (KeepUnit.Episodes, false) => "episodes",
            (KeepUnit.Days, true) => "day",
            (KeepUnit.Days, false) => "days",
            (KeepUnit.Weeks, true) => "week",
            (KeepUnit.Weeks, false) => "weeks",
            (KeepUnit.Seasons, true) => "season",
            _ => "seasons"
        };
}