using System.Text.RegularExpressions;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>A tag change for one track.</summary>
public sealed record MusicTagChange(string Field, string? Before, string After);

/// <summary>What MusicBrainz would change on one track.</summary>
public sealed record MusicTrackProposal(string Path, MusicTags NewTags, IReadOnlyList<MusicTagChange> Changes);

/// <summary>How well a release fits an album folder, and the tags it would write.</summary>
public sealed record MusicMatch(
    MbRelease Release,
    double Score,
    IReadOnlyList<MusicTrackProposal> Tracks,
    IReadOnlyList<string> Unmatched)
{
    public int Changing => Tracks.Count(track => track.Changes.Count > 0);

    /// <summary>
    /// Nearly every track is on this release. Writing a release that only
    /// fits part of a folder would give half the album a new name and
    /// split it in two in every player.
    /// </summary>
    public bool FitsWholeFolder => Unmatched.Count == 0 || Tracks.Count >= (Tracks.Count + Unmatched.Count) * 0.9;
}

/// <summary>
/// Lines a folder's tracks up with a MusicBrainz release: by title first,
/// then by disc and track number, then by the number in the file name.
/// Pure: nothing is read or written.
/// </summary>
public static class MusicBrainzMatcher
{
    public static MusicMatch Match(IReadOnlyList<MusicTrack> tracks, MbRelease release)
    {
        var all =
            release.Media
                .SelectMany(medium => medium.Tracks.Select(track => (Medium: medium.Position, Track: track)))
                .ToList();

        var used = new HashSet<(int, int)>();
        var found = new Dictionary<string, (int Medium, MbTrack Track)>(StringComparer.OrdinalIgnoreCase);
        var multiDisc = release.Media.Count > 1;

        var ordered =
            tracks.OrderBy(track => track.Tags.Disc ?? 1).ThenBy(track => track.Tags.Track ?? int.MaxValue).ThenBy(track => track.Path).ToList();

        (int Medium, MbTrack Track)? Pick(MusicTrack track, Func<(int Medium, MbTrack Track), bool> fits)
        {
            var disc = track.Tags.Disc ?? 1;

            return all.Where(candidate => !used.Contains((candidate.Medium, candidate.Track.Position)) && fits(candidate))
                .OrderBy(candidate => candidate.Medium == disc ? 0 : 1)
                .ThenBy(candidate => Math.Abs(candidate.Track.Position - (track.Tags.Track ?? candidate.Track.Position)))
                .Cast<(int Medium, MbTrack Track)?>()
                .FirstOrDefault();
        }

        // Each pass only looks at tracks the earlier passes left, so an exact
        // title always wins over a looser one.
        var passes = new List<Func<MusicTrack, (int Medium, MbTrack Track)?>>
        {
            // 1. The same title.
            track => Pick(track, candidate => MusicAnalyzer.Key(candidate.Track.Title) == MusicAnalyzer.TrackKey(track)),

            // 2. The same title apart from "Remastered" and the like - never
            //    "(Live)", "(Demo)", "(Writing Session)": those are other recordings.
            track => Pick(track, candidate => Plain(candidate.Track.Title) == Plain(track.Tags.Title ?? MusicAnalyzer.TrackKey(track)) && Plain(candidate.Track.Title).Length > 0),

            // 3. No title at all: the same disc and position.
            track => track.Tags.Title is null && (track.Tags.Track ?? NumberInName(track.Path)) is { } position
                ? Pick(track, candidate => candidate.Medium == (track.Tags.Disc ?? 1) && candidate.Track.Position == position)
                : null
        };

        for (var index = 0; index < passes.Count; index++)
        {
            var pass = passes[index];

            // Lining up untitled tracks by number is only safe once the
            // titles have shown this is the right release.
            if (index == 2)
            {
                var titled = ordered.Count(track => track.Tags.Title is not null);

                if (titled > 0 && found.Count * 2 < titled)
                {
                    break;
                }
            }

            foreach (var track in ordered.Where(track => !found.ContainsKey(track.Path)))
            {
                if (pass(track) is { } match)
                {
                    used.Add((match.Medium, match.Track.Position));
                    found[track.Path] = match;
                }
            }
        }


        var proposals = new List<MusicTrackProposal>();
        var unmatched = new List<string>();

        foreach (var track in ordered)
        {
            if (!found.TryGetValue(track.Path, out var match))
            {
                unmatched.Add(track.Path);
                continue;
            }

            var tags = new MusicTags
            {
                Title = match.Track.Title,
                Artist = match.Track.Artist,
                AlbumArtist = release.Artist,
                Album = release.Title,
                Track = match.Track.Position,
                Disc = multiDisc ? match.Medium : track.Tags.Disc,
                Year = release.Year ?? track.Tags.Year
            };

            proposals.Add(new MusicTrackProposal(track.Path, tags, Changes(track.Tags, tags)));
        }

        // Most of the folder matched, and the release isn't far bigger or smaller.
        var matched = proposals.Count / (double)Math.Max(1, tracks.Count);
        var sizeFit = Math.Min(tracks.Count, release.TrackCount) / (double)Math.Max(1, Math.Max(tracks.Count, release.TrackCount));

        return new MusicMatch(release, matched * 0.8 + sizeFit * 0.2, proposals, unmatched);
    }

    /// <summary>The best fit among the releases, if any fits at all.</summary>
    public static MusicMatch? Best(IReadOnlyList<MusicTrack> tracks, IEnumerable<MbRelease> releases) =>
        releases
            .Select(release => Match(tracks, release))
            .Where(match => match.Tracks.Count > 0)
            .OrderByDescending(match => match.Score)
            .FirstOrDefault();

    private static List<MusicTagChange> Changes(MusicTags before, MusicTags after)
    {
        var changes = new List<MusicTagChange>();

        void Compare(string field, string? old, string? value)
        {
            if (value is not null && !string.Equals(old?.Trim(), value.Trim(), StringComparison.Ordinal))
            {
                changes.Add(new MusicTagChange(field, old, value));
            }
        }

        Compare("Title", before.Title, after.Title);
        Compare("Artist", before.Artist, after.Artist);
        Compare("Album artist", before.AlbumArtist, after.AlbumArtist);
        Compare("Album", before.Album, after.Album);
        Compare("Track", before.Track?.ToString(), after.Track?.ToString());
        Compare("Disc", before.Disc?.ToString(), after.Disc?.ToString());
        Compare("Year", before.Year, after.Year);

        return changes;
    }

    // "Pier (Remastered)", "Pier - 2014 Remaster" and "Pier" are one recording.
    private static string Plain(string title) =>
        MusicAnalyzer.Key(Regex.Replace(title, @"\s*[\(\[-][^\)\]]*\bremaster(ed)?\b[^\)\]]*[\)\]]?\s*$", string.Empty, RegexOptions.IgnoreCase));

    private static int? NumberInName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var digits = new string(name.TakeWhile(char.IsDigit).ToArray());

        return digits.Length is > 0 and <= 3 && int.TryParse(digits, out var number) ? number : null;
    }
}
