using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Works out what is wrong in a music library from its tracks and other
/// files. Pure: nothing is read or changed here.
/// </summary>
public static partial class MusicAnalyzer
{
    /// <summary>Rip and download leftovers nobody plays.</summary>
    private static readonly HashSet<string> JunkExtensions =
        new([".log", ".m3u", ".m3u8", ".nfo", ".sfv", ".md5", ".ffp", ".url", ".accurip", ".db", ".ini", ".ds_store", ".torrent", ".part", ".tmp"], StringComparer.OrdinalIgnoreCase);

    private const string VariousArtists = "various artists";

    // Folders that hold albums by many artists: an album there is where it belongs.
    private static readonly HashSet<string> CollectionFolders =
        new(["compilations", "compilation", "variousartists", "va", "soundtracks", "soundtrack", "ost", "singles", "mixes", "mixtapes"], StringComparer.Ordinal);

    // Folders that only wrap a library ("D:\music\Music"): Artist folders are inside them.
    private static readonly HashSet<string> WrapperFolders =
        new(["music", "musiclibrary", "library", "media", "audio", "albums", "artists", "flac", "mp3", "lossless"], StringComparer.Ordinal);

    /// <summary>
    /// The folder the Artist folders are really in. A library set to
    /// "D:\music" whose music is all in "D:\music\Music" is organised from there,
    /// so tidying never moves albums out of that folder.
    /// </summary>
    public static string EffectiveRoot(string root, IEnumerable<string> trackFolders)
    {
        var folders = trackFolders.ToList();
        var current = Path.TrimEndingDirectorySeparator(root);

        while (true)
        {
            if (folders.Count == 0 ||
                folders.Any(folder => string.Equals(Path.TrimEndingDirectorySeparator(folder), current, StringComparison.OrdinalIgnoreCase)))
            {
                return current;
            }

            var children =
                folders
                    .Where(folder => PathContainment.IsPathUnderRoot(folder, current))
                    .Select(folder => Path.GetRelativePath(current, folder).Split(Path.DirectorySeparatorChar)[0])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

            if (children.Count != 1 || !WrapperFolders.Contains(Key(children[0])))
            {
                return current;
            }

            current = Path.Combine(current, children[0]);
        }
    }

    /// <summary>
    /// Whether a folder name means that album or artist, allowing for
    /// "Album (Deluxe Edition)" in the tags, names Windows cut short, and
    /// extra words like "[FLAC]" or "2019 -" in the folder.
    /// </summary>
    public static bool NameMatches(string folderName, string tagName)
    {
        var folder = Key(folderName);
        var tag = Key(tagName);

        if (tag.Length == 0 || folder.Length == 0)
        {
            return true;
        }

        if (folder.Contains(tag, StringComparison.Ordinal) ||
            (folder.Length >= 6 && tag.Contains(folder, StringComparison.Ordinal)))
        {
            return true;
        }

        // "Harbour Lights (Remastered) (Deluxe Edition)" is "Harbour Lights".
        var baseTitle = Key(EditionSuffix().Replace(tagName, string.Empty));

        if (baseTitle.Length >= 3 && folder.Contains(baseTitle, StringComparison.Ordinal))
        {
            return true;
        }

        // Most of the words in common: "Summer Mix 2021" and "Summer Mashup Mix 2021".
        var tagWords = Words(tagName);
        var folderWords = Words(folderName);

        return tagWords.Count > 0 && tagWords.Count(folderWords.Contains) >= Math.Ceiling(tagWords.Count * 0.7);
    }

    /// <summary>"Album (Remastered) [Deluxe]" without the brackets at the end; the name itself when that's all it is.</summary>
    public static string WithoutEdition(string album)
    {
        var plain = album.Trim();

        while (TrailingBrackets().Match(plain) is { Success: true } match && match.Index > 0)
        {
            plain = plain[..match.Index].Trim();
        }

        return plain.Length == 0 ? album.Trim() : plain;
    }

    private static HashSet<string> Words(string text) =>
        WordSplit().Split(text.ToLowerInvariant()).Select(Key).Where(word => word.Length > 0).ToHashSet(StringComparer.Ordinal);

    public static List<MusicIssue> Analyze(
        IReadOnlyCollection<string> libraryRoots,
        IReadOnlyList<MusicTrack> tracks,
        IReadOnlyList<string> otherFiles,
        IReadOnlyList<string> emptyFolders)
    {
        var issues = new List<MusicIssue>();

        // An album folder is the one holding the tracks, except that disc
        // folders ("CD1", "Disc 2") belong to the folder above them.
        var albums =
            tracks
                .GroupBy(track => AlbumFolder(track.Folder), StringComparer.OrdinalIgnoreCase)
                .ToList();

        foreach (var album in albums)
        {
            var root = album.First().LibraryRoot;
            var list = album.ToList();

            AddFolderIssues(issues, root, album.Key, list);
            AddTagIssues(issues, album.Key, list);
            AddSameTracksTwice(issues, album.Key, list);
        }

        AddDuplicateAlbums(issues, albums);
        AddJunk(issues, tracks, otherFiles, emptyFolders);
        AddBroken(issues, tracks);

        return issues;
    }

    // --------------------------------------------------------------- Folders

    private static void AddFolderIssues(List<MusicIssue> issues, string root, string folder, List<MusicTrack> tracks)
    {
        var depth = Depth(root, folder);
        var named = tracks.Where(track => track.Tags.Album is not null).ToList();

        var byAlbum =
            named
                .GroupBy(track => Key(AlbumArtistOf(track) + "|" + track.Tags.Album))
                .ToList();

        // Tracks straight in the library folder.
        if (depth == 0)
        {
            issues.Add(
                new MusicIssue
                {
                    Kind = MusicIssueKind.Folder,
                    Key = "loose|" + folder,
                    Title = $"{tracks.Count} track(s) not in any album folder",
                    Detail = "They're straight in the music library folder. Tidying moves each into an Artist\\Album folder from its tags.",
                    Folder = folder,
                    Paths = tracks.Select(track => track.Path).ToList(),
                    Bytes = tracks.Sum(track => track.SizeBytes),
                    Moves = MovesByTags(root, tracks)
                });

            return;
        }

        // Many albums in one folder.
        var bigAlbums = byAlbum.Count(group => group.Count() >= 2);

        if (byAlbum.Count >= 3 || bigAlbums >= 2)
        {
            issues.Add(
                new MusicIssue
                {
                    Kind = MusicIssueKind.Folder,
                    Key = "mixed|" + folder,
                    Title = $"{byAlbum.Count} albums in one folder",
                    Detail = $"\"{Path.GetFileName(folder)}\" holds tracks from {string.Join(", ", byAlbum.Take(4).Select(group => group.First().Tags.Album))}{(byAlbum.Count > 4 ? " and more" : null)}. Tidying gives each album its own folder.",
                    Folder = folder,
                    Paths = tracks.Select(track => track.Path).ToList(),
                    Bytes = tracks.Sum(track => track.SizeBytes),
                    Moves = MovesByTags(root, tracks)
                });

            return;
        }

        // One album: does the folder say what it is?
        if (byAlbum.Count != 1 || named.Count < tracks.Count * 0.8)
        {
            return;
        }

        var sample = byAlbum[0].First();
        var albumName = sample.Tags.Album!;
        var artistName = AlbumArtistOf(sample);
        var folderName = Path.GetFileName(folder);
        var parent = Path.GetDirectoryName(folder) ?? root;
        var parentName = Path.GetFileName(parent);

        var folderSaysAlbum = NameMatches(folderName, albumName);

        // "Artist - Album" straight under the library counts as named.
        var flatButNamed = depth == 1 && folderSaysAlbum;

        var artistFits =
            artistName is null ||
            flatButNamed ||
            Key(artistName) == VariousArtists.Replace(" ", string.Empty) && depth >= 2 ||
            CollectionFolders.Contains(Key(parentName)) ||
            NameMatches(parentName, artistName);

        if (folderSaysAlbum && artistFits)
        {
            return;
        }

        var target = Path.Combine(root, SafeName(artistName ?? "Unknown Artist"), AlbumFolderName(albumName, sample.Tags.Year));

        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // The album's tracks are straight in the artist's own folder.
        var inArtistFolder = artistName is not null && !folderSaysAlbum && NameMatches(folderName, artistName);

        var why =
            inArtistFolder ? $"The tracks of \"{albumName}\" are straight in the artist folder, not in an album folder of their own."
            : !folderSaysAlbum && !artistFits ? $"The folder doesn't name the album \"{albumName}\" or sit under its artist \"{artistName}\"."
            : !folderSaysAlbum ? $"The folder name doesn't say it's \"{albumName}\"."
            : $"It isn't in a folder for its artist, \"{artistName}\".";

        issues.Add(
            new MusicIssue
            {
                Kind = MusicIssueKind.Folder,
                // Only a folder that is the album moves whole; an artist folder keeps everything else in it.
                Key = (inArtistFolder ? "inartist|" : "named|") + folder,
                Title = inArtistFolder ? $"\"{albumName}\" is loose in the {folderName} folder" : $"\"{RelativeTo(root, folder)}\" is misnamed",
                Detail = $"{why} Tidying moves {(inArtistFolder ? "its tracks" : "it")} to \"{RelativeTo(root, target)}\".",
                Folder = folder,
                Paths = tracks.Select(track => track.Path).ToList(),
                Bytes = tracks.Sum(track => track.SizeBytes),
                Moves = tracks.Select(track => new MusicMove(track.Path, Path.Combine(target, Path.GetRelativePath(folder, track.Path)))).ToList()
            });
    }

    // Where each track goes by its own tags: Artist\Album (Year)\file.
    private static List<MusicMove> MovesByTags(string root, IEnumerable<MusicTrack> tracks) =>
        tracks
            .Where(track => track.Tags.Album is not null)
            .Select(track => new MusicMove(
                track.Path,
                Path.Combine(
                    root,
                    SafeName(AlbumArtistOf(track) ?? "Unknown Artist"),
                    AlbumFolderName(track.Tags.Album!, track.Tags.Year),
                    Path.GetFileName(track.Path))))
            .Where(move => !string.Equals(move.From, move.To, StringComparison.OrdinalIgnoreCase))
            .ToList();

    // ------------------------------------------------------------------ Tags

    private static void AddTagIssues(List<MusicIssue> issues, string folder, List<MusicTrack> tracks)
    {
        var readable = tracks.Where(track => track.Tags.Problem is null).ToList();

        if (readable.Count == 0)
        {
            return;
        }

        var problems = new List<string>();

        void Missing(string what, Func<MusicTags, object?> value)
        {
            var count = readable.Count(track => value(track.Tags) is null);

            if (count > 0)
            {
                problems.Add(count == readable.Count ? $"no {what}" : $"{count} without {what}");
            }
        }

        Missing("title", tags => tags.Title);
        Missing("artist", tags => tags.Artist);
        Missing("album", tags => tags.Album);
        Missing("track number", tags => tags.Track);

        var albumArtists = readable.Select(track => track.Tags.AlbumArtist).Where(value => value is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var artists = readable.Select(track => track.Tags.Artist).Where(value => value is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var albumNames = readable.Select(track => track.Tags.Album).Where(value => value is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (albumNames.Count == 1)
        {
            if (albumArtists.Count > 1)
            {
                problems.Add($"{albumArtists.Count} different album artists");
            }
            else if (albumArtists.Count == 0 && artists.Count > 1)
            {
                problems.Add("several artists but no album artist, so players may split it into several albums");
            }

            var numbered = readable.Where(track => track.Tags.Track is not null).ToList();

            var twice =
                numbered
                    .GroupBy(track => (track.Tags.Disc ?? 1, track.Tags.Track!.Value))
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key.Item2)
                    .ToList();

            if (twice.Count > 0)
            {
                problems.Add($"track number {string.Join(", ", twice.Take(5))} used twice");
            }

            foreach (var disc in numbered.GroupBy(track => track.Tags.Disc ?? 1))
            {
                var have = disc.Select(track => track.Tags.Track!.Value).ToHashSet();
                var gaps = Enumerable.Range(1, have.Max()).Where(number => !have.Contains(number)).ToList();

                if (gaps.Count > 0 && gaps.Count <= 10)
                {
                    problems.Add($"track {string.Join(", ", gaps)} missing{(disc.Key > 1 ? $" on disc {disc.Key}" : null)}");
                }
            }
        }

        if (problems.Count == 0)
        {
            return;
        }

        issues.Add(
            new MusicIssue
            {
                Kind = MusicIssueKind.Tags,
                Key = "tags|" + folder,
                Title = AlbumLabel(folder, readable),
                Detail = Capitalize(string.Join("; ", problems)) + ".",
                Folder = folder,
                Paths = readable.Select(track => track.Path).ToList(),
                Bytes = readable.Sum(track => track.SizeBytes)
            });
    }

    // ------------------------------------------------------------ Duplicates

    // The same tracks in two formats in one folder (Song.flac and Song.mp3).
    private static void AddSameTracksTwice(List<MusicIssue> issues, string folder, List<MusicTrack> tracks)
    {
        var pairs =
            tracks
                .GroupBy(track => Key(Path.GetFileNameWithoutExtension(track.Path)))
                .Where(group => group.Select(track => track.Extension).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                .ToList();

        if (pairs.Count == 0)
        {
            return;
        }

        // Keep the best of each pair: lossless, then biggest.
        var extra =
            pairs
                .SelectMany(group => group.OrderByDescending(track => track.IsLossless).ThenByDescending(track => track.SizeBytes).Skip(1))
                .ToList();

        issues.Add(
            new MusicIssue
            {
                Kind = MusicIssueKind.Duplicate,
                Key = "twice|" + folder,
                Title = $"{pairs.Count} track(s) in two formats in one folder",
                Detail = $"Each is there as {string.Join(" and ", pairs[0].Select(track => track.Extension.TrimStart('.').ToUpperInvariant()))}. Quarantine takes the extra {string.Join("/", extra.Select(track => track.Extension.TrimStart('.').ToUpperInvariant()).Distinct())} copies and keeps the better one.",
                Folder = folder,
                Paths = extra.Select(track => track.Path).ToList(),
                Bytes = extra.Sum(track => track.SizeBytes)
            });
    }

    // One album in two folders.
    private static void AddDuplicateAlbums(List<MusicIssue> issues, List<IGrouping<string, MusicTrack>> albums)
    {
        var byName =
            albums
                .Select(album => (Folder: album.Key, Tracks: album.ToList()))
                .Select(album => (album.Folder, album.Tracks, Name: MainAlbumKey(album.Tracks)))
                .Where(album => album.Name is not null)
                .GroupBy(album => album.Name!)
                .Where(group => group.Count() > 1);

        foreach (var group in byName)
        {
            // Lossless first, then the most tracks, then the biggest.
            var ordered =
                group
                    .OrderByDescending(album => album.Tracks.Count(track => track.IsLossless) * 2 >= album.Tracks.Count)
                    .ThenByDescending(album => album.Tracks.Count)
                    .ThenByDescending(album => album.Tracks.Sum(track => track.SizeBytes))
                    .ToList();

            // Copies share more than half their tracks. Folders that don't are
            // parts of one album (a bonus disc, or tracks split in two).
            var main = ordered[0];
            var mainTitles = main.Tracks.Select(TrackKey).ToHashSet(StringComparer.Ordinal);

            var duplicates = ordered.Skip(1).Where(album => Overlap(album.Tracks, mainTitles, main.Tracks.Count) > 0.5).ToList();
            var parts = ordered.Skip(1).Except(duplicates).ToList();

            var sample = main.Tracks.First(track => track.Tags.Album is not null);
            var albumLabel = $"{(AlbumArtistOf(sample) is { } artist ? artist + " - " : null)}{sample.Tags.Album}";

            if (parts.Count > 0)
            {
                AddSplitAlbum(issues, group.Key, albumLabel, main, parts, mainTitles);
            }

            if (duplicates.Count == 0)
            {
                continue;
            }

            var copies =
                new[] { main }.Concat(duplicates)
                    .Select((album, index) => new MusicCopy(
                        album.Folder,
                        string.Join("/", album.Tracks.Select(track => track.Extension.TrimStart('.').ToUpperInvariant()).Distinct()),
                        album.Tracks.Count,
                        album.Tracks.Sum(track => track.SizeBytes),
                        index == 0,
                        album.Tracks.Select(track => track.Path).ToList()))
                    .ToList();

            issues.Add(
                new MusicIssue
                {
                    Kind = MusicIssueKind.Duplicate,
                    Key = "album|" + group.Key,
                    Title = $"{albumLabel} is here {copies.Count} times",
                    Detail = "These copies have mostly the same tracks. Keep the best (lossless, then the most tracks) and send the others to Quarantine.",
                    Folder = main.Folder,
                    Paths = copies.Skip(1).SelectMany(copy => copy.Paths).ToList(),
                    Bytes = copies.Skip(1).Sum(copy => copy.Bytes),
                    Copies = copies
                });
        }
    }

    // One album whose tracks are spread over several folders: they move
    // into the main one, each other disc in a "CD n" folder of its own.
    // A track that's in both stays in the main folder; the extra copy
    // goes to Quarantine.
    private static void AddSplitAlbum(
        List<MusicIssue> issues,
        string albumKey,
        string albumLabel,
        (string Folder, List<MusicTrack> Tracks, string? Name) main,
        List<(string Folder, List<MusicTrack> Tracks, string? Name)> parts,
        HashSet<string> mainTitles)
    {
        var mainDiscs = main.Tracks.Select(track => track.Tags.Disc ?? 1).ToHashSet();
        var moves = new List<MusicMove>();
        var extra = new List<string>();

        foreach (var part in parts)
        {
            foreach (var track in part.Tracks)
            {
                if (mainTitles.Contains(TrackKey(track)))
                {
                    extra.Add(track.Path);
                    continue;
                }

                var disc = track.Tags.Disc ?? 1;
                var into = mainDiscs.Contains(disc) ? main.Folder : Path.Combine(main.Folder, $"CD{disc}");

                moves.Add(new MusicMove(track.Path, Path.Combine(into, Path.GetFileName(track.Path))));
            }
        }

        string Describe((string Folder, List<MusicTrack> Tracks, string? Name) album)
        {
            var discs = album.Tracks.Select(track => track.Tags.Disc).Where(disc => disc is not null).Distinct().OrderBy(disc => disc).ToList();

            return $"\"{Path.GetFileName(album.Folder)}\" has {album.Tracks.Count} track(s){(discs.Count > 0 ? $" from disc {string.Join(", ", discs)}" : null)}";
        }

        issues.Add(
            new MusicIssue
            {
                Kind = MusicIssueKind.Folder,
                Key = "split|" + albumKey,
                Title = $"{albumLabel} is split across {parts.Count + 1} folders",
                Detail =
                    $"{string.Join("; ", new[] { main }.Concat(parts).Select(Describe))}. They're different tracks of one album, so tidying moves them into \"{Path.GetFileName(main.Folder)}\"" +
                    (extra.Count > 0 ? $"; {extra.Count} track(s) in both go to Quarantine." : "."),
                Folder = main.Folder,
                Paths = extra,
                Bytes = parts.Sum(part => part.Tracks.Sum(track => track.SizeBytes)),
                Moves = moves
            });
    }

    // How much of the smaller folder is also in the main one.
    private static double Overlap(List<MusicTrack> tracks, HashSet<string> mainTitles, int mainCount)
    {
        var shared = tracks.Count(track => mainTitles.Contains(TrackKey(track)));

        return shared / (double)Math.Max(1, Math.Min(tracks.Count, mainCount));
    }

    /// <summary>A track by its title ("[untitled]" and "(Untitled)" are the same), or its file name without the number.</summary>
    public static string TrackKey(MusicTrack track) =>
        Key(track.Tags.Title ?? LeadingNumber().Replace(Path.GetFileNameWithoutExtension(track.Path), string.Empty));

    // --------------------------------------------------------- Junk, broken

    private static void AddJunk(List<MusicIssue> issues, IReadOnlyList<MusicTrack> tracks, IReadOnlyList<string> otherFiles, IReadOnlyList<string> emptyFolders)
    {
        var audioPerFolder =
            tracks.GroupBy(track => track.Folder, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var folder in otherFiles.GroupBy(file => Path.GetDirectoryName(file) ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            var audio = audioPerFolder.GetValueOrDefault(folder.Key);

            var junk =
                folder
                    .Where(file =>
                    {
                        var name = Path.GetFileName(file);
                        var extension = Path.GetExtension(file);

                        // A cue sheet is needed to play a whole-album rip.
                        if (extension.Equals(".cue", StringComparison.OrdinalIgnoreCase))
                        {
                            return audio > 2;
                        }

                        return JunkExtensions.Contains(extension) ||
                               name.StartsWith("._", StringComparison.Ordinal) ||
                               name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase);
                    })
                    .ToList();

            if (junk.Count == 0)
            {
                continue;
            }

            issues.Add(
                new MusicIssue
                {
                    Kind = MusicIssueKind.Junk,
                    Key = "junk|" + folder.Key,
                    Title = Path.GetFileName(folder.Key),
                    Detail = $"{junk.Count} leftover file(s): {string.Join(", ", junk.Select(Path.GetExtension).Distinct(StringComparer.OrdinalIgnoreCase).Take(6))}",
                    Folder = folder.Key,
                    Paths = junk,
                    Bytes = junk.Sum(SizeOf)
                });
        }

        if (emptyFolders.Count > 0)
        {
            issues.Add(
                new MusicIssue
                {
                    Kind = MusicIssueKind.Junk,
                    Key = "empty",
                    Title = $"{emptyFolders.Count} empty folder(s)",
                    Detail = string.Join(", ", emptyFolders.Take(5).Select(Path.GetFileName)) + (emptyFolders.Count > 5 ? " and more" : null),
                    Folder = Path.GetDirectoryName(emptyFolders[0]) ?? string.Empty,
                    Paths = emptyFolders
                });
        }
    }

    private static void AddBroken(List<MusicIssue> issues, IReadOnlyList<MusicTrack> tracks)
    {
        foreach (var track in tracks.Where(track => track.Tags.Problem is not null))
        {
            issues.Add(
                new MusicIssue
                {
                    Kind = MusicIssueKind.Broken,
                    Key = "broken|" + track.Path,
                    Title = Path.GetFileName(track.Path),
                    Detail = track.Tags.Problem! + ".",
                    Folder = track.Folder,
                    Paths = [track.Path],
                    Bytes = track.SizeBytes
                });
        }
    }

    // --------------------------------------------------------------- Helpers

    /// <summary>"…\Album\CD1" is part of "…\Album".</summary>
    public static string AlbumFolder(string folder) =>
        DiscFolder().IsMatch(Path.GetFileName(folder)) && Path.GetDirectoryName(folder) is { } parent
            ? parent
            : folder;

    private static string? AlbumArtistOf(MusicTrack track) =>
        track.Tags.AlbumArtist ?? track.Tags.Artist;

    private static string? MainAlbumKey(List<MusicTrack> tracks)
    {
        var top =
            tracks
                .Where(track => track.Tags.Album is not null)
                .GroupBy(track => Key(track.Tags.Album!) + "|" + Key(AlbumArtistOf(track) ?? string.Empty))
                .OrderByDescending(group => group.Count())
                .FirstOrDefault();

        // Only a folder that is mostly one album counts as a copy of it.
        return top is not null && top.Count() * 2 > tracks.Count ? top.Key : null;
    }

    private static string AlbumLabel(string folder, List<MusicTrack> tracks)
    {
        var sample = tracks.FirstOrDefault(track => track.Tags.Album is not null);

        return sample is null
            ? Path.GetFileName(folder)
            : $"{(AlbumArtistOf(sample) is { } artist ? artist + " - " : null)}{sample.Tags.Album}";
    }

    public static string AlbumFolderName(string album, string? year) =>
        SafeName(year is null ? album : $"{album} ({year})");

    /// <summary>Takes out characters Windows won't allow in a folder name.</summary>
    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim().TrimEnd('.');

        return cleaned.Length == 0 ? "_" : cleaned;
    }

    /// <summary>Lower case letters and digits only, so names compare however they're written.</summary>
    public static string Key(string value)
    {
        var text = value.ToLowerInvariant().Replace("&", "and");

        if (text.StartsWith("the ", StringComparison.Ordinal))
        {
            text = text[4..];
        }

        var builder = new StringBuilder();

        foreach (var character in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static int Depth(string root, string folder) =>
        PathContainment.IsPathUnderRoot(folder, root) || string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(root, folder) is "." ? 0 : Path.GetRelativePath(root, folder).Split(Path.DirectorySeparatorChar).Length
            : 0;

    private static string RelativeTo(string root, string path) =>
        Path.GetRelativePath(root, path);

    private static long SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    [GeneratedRegex(@"\s*[\(\[].*$")]
    private static partial Regex EditionSuffix();

    [GeneratedRegex(@"[\s\-_.,:;!?/\\]+")]
    private static partial Regex WordSplit();

    [GeneratedRegex(@"\s*[\(\[][^\(\)\[\]]*[\)\]]\s*$")]
    private static partial Regex TrailingBrackets();

    [GeneratedRegex(@"^[\d\s.\-_]+")]
    private static partial Regex LeadingNumber();

    [GeneratedRegex(@"^(?:cd|disc|disk)\s*\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex DiscFolder();
}
