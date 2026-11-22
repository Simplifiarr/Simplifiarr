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

        var deletions = new List<RollingDeletion>();

        for (var index = 0; index < newestFirst.Count; index++)
        {
            var episode = newestFirst[index];

            if (index < floor)
            {
                continue;
            }

            var reason = TooOld(rule, episode, index, currentSeason, seasonHasSwitched, nowUtc);

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
        int currentSeason,
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

        if (rule.KeepCurrentSeasonOnly && seasonHasSwitched && episode.Season < currentSeason)
        {
            return $"Season {episode.Season}; season {currentSeason} has started";
        }

        return null;
    }

    private static bool SeasonHasSwitched(
        RollingShowRule rule,
        IReadOnlyList<ShowEpisode> episodes,
        int currentSeason,
        DateTime nowUtc)
    {
        if (!rule.KeepCurrentSeasonOnly || episodes.All(episode => episode.Season == currentSeason))
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

    /// <summary>The rule in a sentence, for the page.</summary>
    public static string Describe(RollingShowRule rule)
    {
        var parts = new List<string>();

        if (rule.KeepLatest is > 0 and var latest)
        {
            parts.Add($"the newest {latest} episode(s)");
        }

        if (rule.KeepDays is > 0 and var days)
        {
            parts.Add($"episodes from the last {days} day(s)");
        }

        if (rule.KeepCurrentSeasonOnly)
        {
            parts.Add(
                rule.SeasonSwitch switch
                {
                    SeasonSwitch.AfterEpisodes => $"only the current season, once {rule.SeasonSwitchValue} episode(s) of a new one are in",
                    SeasonSwitch.AfterDays => $"only the current season, {rule.SeasonSwitchValue} day(s) after a new one starts",
                    _ => "only the current season, from the first episode of a new one"
                });
        }

        if (parts.Count == 0)
        {
            return "No limit set.";
        }

        var text = "Keeps " + string.Join(" and ", parts);

        if (rule.MinimumKeep is > 0 and var floor)
        {
            text += $", never fewer than {floor}";
        }

        text += rule.Watched switch
        {
            WatchRequirement.Anyone => ", once someone has watched them",
            WatchRequirement.User => $", once {rule.WatchedBy} has watched them",
            _ => string.Empty
        };

        if (rule.Watched != WatchRequirement.None && rule.UnwatchedAfterDays is > 0 and var wait)
        {
            text += $" (or after {wait} days anyway)";
        }

        return text + (rule.Permanent ? ". Deleted for good." : ". Deleted to the Recycle Bin.");
    }
}
