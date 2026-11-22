using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Decides which movies a Rolling Movies rule deletes. Pure: nothing is read
/// or changed.
/// </summary>
public static class RollingMoviePlanner
{
    /// <summary>Nothing newer than this goes just because a drive is full.</summary>
    public const int DriveFullGraceDays = 7;

    /// <param name="overLimitBytes">Per drive root: how much has to go to get under its limit.</param>
    public static MoviePlan Plan(
        RollingMovieRule rule,
        IReadOnlyList<RollingMovieFacts> movies,
        string keepTag,
        IReadOnlyDictionary<string, long> overLimitBytes,
        DateTime nowUtc)
    {
        if (!rule.HasAnyMovies || !rule.HasAnyTrigger)
        {
            return new MoviePlan([], 0, 0);
        }

        var matched = movies.Where(movie => Matches(rule, movie)).ToList();
        var open = matched.Where(movie => !IsProtected(movie, keepTag)).ToList();

        var deletions = new List<MovieDeletion>();

        foreach (var movie in open)
        {
            if (Due(rule, movie, nowUtc) is { } reason)
            {
                deletions.Add(new MovieDeletion(movie, reason));
            }
        }

        if (rule.WhenDriveFull)
        {
            AddForFullDrives(rule, open, deletions, overLimitBytes, nowUtc);
        }

        return new MoviePlan(
            deletions.OrderBy(deletion => deletion.Facts.Movie.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            matched.Count,
            matched.Count - open.Count);
    }

    public static bool Matches(RollingMovieRule rule, RollingMovieFacts facts)
    {
        var movie = facts.Movie;

        return rule.MovieIds.Contains(movie.Id) ||
               movie.Tags.Any(tag => rule.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) ||
               (facts.RequestedBy is { } requester &&
                (rule.AnyRequest || rule.RequestedBy.Contains(requester, StringComparer.OrdinalIgnoreCase))) ||
               rule.Folders.Any(folder => PathContainment.IsPathUnderRoot(movie.FolderPath, folder));
    }

    public static bool IsProtected(RollingMovieFacts facts, string keepTag) =>
        facts.Favorite ||
        facts.Kept ||
        (!string.IsNullOrWhiteSpace(keepTag) && facts.Movie.Tags.Contains(keepTag.Trim(), StringComparer.OrdinalIgnoreCase));

    /// <summary>When it counted as watched for this rule; null when it hasn't.</summary>
    public static DateTime? WatchedAt(RollingMovieRule rule, RollingMovieFacts facts)
    {
        // A watch with no date counts from when the movie arrived.
        var fallback = facts.Movie.AddedUtc ?? DateTime.MinValue;

        if (rule.WatchedBy == MovieWatcher.People && rule.WatchedByUsers.Count > 0)
        {
            DateTime? latest = DateTime.MinValue;

            foreach (var person in rule.WatchedByUsers)
            {
                var match = facts.WatchedOn.FirstOrDefault(pair => string.Equals(pair.Key, person, StringComparison.OrdinalIgnoreCase));

                if (match.Key is null)
                {
                    return null;
                }

                var when = match.Value ?? fallback;
                latest = when > latest ? when : latest;
            }

            return latest;
        }

        if (rule.WatchedBy == MovieWatcher.Requester && facts.RequestedBy is not null)
        {
            return facts.RequesterWatchedUtc;
        }

        return facts.WatchedOn.Count == 0
            ? null
            : facts.WatchedOn.Values.Select(value => value ?? fallback).Max();
    }

    private static string? Due(RollingMovieRule rule, RollingMovieFacts facts, DateTime nowUtc)
    {
        if (rule.DaysAfterWatched is > 0 and var days &&
            WatchedAt(rule, facts) is { } watched &&
            watched <= nowUtc.AddDays(-days))
        {
            return string.Join(" ", new[] { "Watched", WhoWatched(rule, facts), Ago(watched, nowUtc) }.Where(part => part.Length > 0));
        }

        if (rule.UnwatchedMonths is > 0 and var months &&
            !facts.AnyoneStarted &&
            facts.Movie.AddedUtc is { } arrived &&
            arrived <= nowUtc.AddMonths(-months))
        {
            return $"Nobody has watched it since it arrived {Ago(arrived, nowUtc)}";
        }

        if (rule.DaysAfterAdded is > 0 and var added &&
            facts.Movie.AddedUtc is { } addedUtc &&
            addedUtc <= nowUtc.AddDays(-added))
        {
            return $"Arrived {Ago(addedUtc, nowUtc)}";
        }

        return null;
    }

    // Longest since watched first, then the longest unwatched, until each
    // full drive is under its limit. A seeding movie frees nothing yet, so
    // it isn't chosen for this.
    private static void AddForFullDrives(
        RollingMovieRule rule,
        List<RollingMovieFacts> open,
        List<MovieDeletion> deletions,
        IReadOnlyDictionary<string, long> overLimitBytes,
        DateTime nowUtc)
    {
        var chosen = deletions.Select(deletion => deletion.Facts.Movie.Id).ToHashSet();

        foreach (var (root, over) in overLimitBytes)
        {
            var stillOver =
                over - deletions
                    .Where(deletion => SameRoot(deletion.Facts.DriveRoot, root) && !deletion.Facts.Seeding)
                    .Sum(deletion => deletion.Facts.Movie.SizeBytes);

            if (stillOver <= 0)
            {
                continue;
            }

            var candidates =
                open
                    .Where(movie => SameRoot(movie.DriveRoot, root) && !movie.Seeding && !chosen.Contains(movie.Movie.Id))
                    .Where(movie => movie.Movie.AddedUtc is not { } added || added <= nowUtc.AddDays(-DriveFullGraceDays))
                    .Select(movie => (Movie: movie, Watched: WatchedAt(rule, movie)))
                    .OrderBy(entry => entry.Watched is null ? 1 : 0)
                    .ThenBy(entry => entry.Watched ?? entry.Movie.Movie.AddedUtc ?? DateTime.MinValue);

            foreach (var (movie, watched) in candidates)
            {
                if (stillOver <= 0)
                {
                    break;
                }

                deletions.Add(
                    new MovieDeletion(
                        movie,
                        $"{root.TrimEnd('\\')} is over its limit; " +
                        (watched is { } when ? $"watched {Ago(when, nowUtc)}" : "never watched")));

                chosen.Add(movie.Movie.Id);
                stillOver -= movie.Movie.SizeBytes;
            }
        }
    }

    private static string WhoWatched(RollingMovieRule rule, RollingMovieFacts facts) =>
        rule.WatchedBy switch
        {
            MovieWatcher.People when rule.WatchedByUsers.Count > 0 => "by " + RollingShowPlanner.JoinNames(rule.WatchedByUsers),
            MovieWatcher.Requester when facts.RequestedBy is { } requester => $"by {requester}, who asked for it,",
            _ => string.Empty
        };

    private static string Ago(DateTime then, DateTime nowUtc)
    {
        var days = (int)Math.Floor((nowUtc - then).TotalDays);

        return days switch
        {
            < 1 => "today",
            1 => "yesterday",
            < 60 => $"{days} days ago",
            < 730 => $"{days / 30} months ago",
            _ => $"{days / 365} years ago"
        };
    }

    private static bool SameRoot(string a, string b) =>
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------ Sentences

    /// <summary>Which movies, in a few words: "Tagged kids, or requested by Sam".</summary>
    public static string DescribeWhich(RollingMovieRule rule)
    {
        var parts = new List<string>();

        if (rule.Tags.Count > 0)
        {
            parts.Add("tagged " + RollingShowPlanner.JoinNames(rule.Tags).Replace(" and ", " or "));
        }

        if (rule.AnyRequest)
        {
            parts.Add("requested in Seerr");
        }
        else if (rule.RequestedBy.Count > 0)
        {
            parts.Add("requested by " + RollingShowPlanner.JoinNames(rule.RequestedBy).Replace(" and ", " or "));
        }

        if (rule.Folders.Count > 0)
        {
            parts.Add("in " + RollingShowPlanner.JoinNames(rule.Folders).Replace(" and ", " or "));
        }

        if (rule.MovieIds.Count > 0)
        {
            parts.Add(
                rule.MovieIds.Count <= 2
                    ? RollingShowPlanner.JoinNames(rule.MovieIds.Select(id => rule.MovieNames.GetValueOrDefault(id, $"movie {id}")).ToList())
                    : $"{rule.MovieIds.Count} picked movies");
        }

        if (parts.Count == 0)
        {
            return "No movies picked";
        }

        var text = string.Join(", or ", parts);

        return "Movies " + text;
    }

    /// <summary>When they go: "14 days after the requester watches, or 6 months unwatched".</summary>
    public static string DescribeWhen(RollingMovieRule rule)
    {
        var parts = new List<string>();

        if (rule.DaysAfterWatched is > 0 and var days)
        {
            var who = rule.WatchedBy switch
            {
                MovieWatcher.People when rule.WatchedByUsers.Count > 0 => RollingShowPlanner.JoinNames(rule.WatchedByUsers) + (rule.WatchedByUsers.Count == 1 ? " watches" : " have all watched"),
                MovieWatcher.Requester => "whoever asked for it watches",
                _ => "someone watches"
            };

            parts.Add($"{days} day{(days == 1 ? "" : "s")} after {who}");
        }

        if (rule.UnwatchedMonths is > 0 and var months)
        {
            parts.Add($"after {months} month{(months == 1 ? "" : "s")} unwatched");
        }

        if (rule.DaysAfterAdded is > 0 and var added)
        {
            parts.Add($"{added} day{(added == 1 ? "" : "s")} after arriving");
        }

        if (rule.WhenDriveFull)
        {
            parts.Add("when the drive is over its limit");
        }

        return parts.Count == 0 ? "Nothing set, so nothing is deleted" : "Deleted " + string.Join(", or ", parts);
    }

    public static string FolderName(string folder) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } name ? name : folder;
}
