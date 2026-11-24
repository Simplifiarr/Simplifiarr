using System.Diagnostics;
using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public interface IMusicCleanupService
{
    event Action? Changed;

    MusicScanResult? Result { get; }

    bool IsBusy { get; }

    /// <summary>"Reading tags: 1,200 of 8,000" while a scan or check runs.</summary>
    string? Status { get; }

    /// <summary>Reads every music library and works out what's wrong.</summary>
    Task ScanAsync();

    /// <summary>
    /// Plays every track in these folders through ffmpeg (without sound)
    /// to find ones that are damaged part way through. Slow: about as long
    /// as it takes to read the files.
    /// </summary>
    Task DeepCheckAsync(IReadOnlyCollection<string>? folders = null);

    /// <summary>Moves a folder problem's files to where their tags say they belong.</summary>
    Task<string> TidyAsync(MusicIssue issue);

    /// <summary>Sends files to Quarantine (empty folders are just removed).</summary>
    Task<string> QuarantineAsync(MusicIssue issue, IReadOnlyCollection<string>? paths = null);

    /// <summary>Stops a deep check after the current track.</summary>
    void Stop();

    /// <summary>The MusicBrainz releases that best fit an album folder, best first.</summary>
    Task<IReadOnlyList<MusicMatch>> LookUpAsync(string albumFolder);

    /// <summary>Writes a match's tags to the folder's tracks. Seeding tracks are left alone.</summary>
    Task<string> WriteTagsAsync(string albumFolder, MusicMatch match);
}

public sealed class MusicCleanupService : IMusicCleanupService
{
    private readonly ISettingsService _settings;
    private readonly IQuarantineService _quarantine;
    private readonly IHistoryService _history;
    private readonly IMusicBrainzClient? _musicBrainz;
    private readonly ITorrentClient? _torrents;

    private readonly SemaphoreSlim _busy = new(1, 1);
    private volatile bool _stop;

    private static string CacheFile => Path.Combine(AppPaths.DataDirectory, "music-tags.json");

    public MusicCleanupService(ISettingsService settings, IQuarantineService quarantine, IHistoryService history, IMusicBrainzClient? musicBrainz = null, ITorrentClient? torrents = null)
    {
        _torrents = torrents;
        _settings = settings;
        _quarantine = quarantine;
        _history = history;
        _musicBrainz = musicBrainz;
    }

    private List<MusicTrack> TracksOf(string albumFolder) =>
        Result?.AllTracks.Where(track => string.Equals(MusicAnalyzer.AlbumFolder(track.Folder), albumFolder, StringComparison.OrdinalIgnoreCase)).ToList() ?? [];

    public async Task<IReadOnlyList<MusicMatch>> LookUpAsync(string albumFolder)
    {
        var tracks = TracksOf(albumFolder);

        if (_musicBrainz is null || tracks.Count == 0)
        {
            return [];
        }

        // The album and artist the tracks agree on most; the folder names when there are no tags.
        string? Most(Func<MusicTrack, string?> pick) =>
            tracks.Select(pick).Where(value => value is not null).GroupBy(value => value!, StringComparer.OrdinalIgnoreCase).OrderByDescending(group => group.Count()).FirstOrDefault()?.Key;

        var album = Most(track => track.Tags.Album) ?? Path.GetFileName(albumFolder);
        var artist = Most(track => track.Tags.AlbumArtist) ?? Most(track => track.Tags.Artist) ?? Path.GetFileName(Path.GetDirectoryName(albumFolder)) ?? string.Empty;

        var year = Most(track => track.Tags.Year);

        // "Album (Remastered) (Deluxe Edition)" is listed as "Album": only
        // brackets at the end are editions, "(Something)" at the
        // start is part of the name.
        var plain = MusicAnalyzer.WithoutEdition(album);

        var releases = await _musicBrainz.FindReleasesAsync(artist, album, tracks.Count, year);

        if (plain != album)
        {
            releases = releases.Concat(await _musicBrainz.FindReleasesAsync(artist, plain, tracks.Count, year)).DistinctBy(release => release.Id).ToList();
        }

        return releases
            // Only releases of this album, whatever edition; never another album by the same artist.
            .Where(release => MusicAnalyzer.NameMatches(release.Title, plain) || MusicAnalyzer.NameMatches(plain, release.Title))
            .Select(release => MusicBrainzMatcher.Match(tracks, release))
            // At least a third of the folder must be on it to be worth showing.
            .Where(match => match.Tracks.Count * 3 >= tracks.Count)
            .OrderByDescending(match => match.Score)
            .Take(4)
            .ToList();
    }

    public async Task<string> WriteTagsAsync(string albumFolder, MusicMatch match)
    {
        var written = 0;
        var left = new List<string>();

        if (!match.FitsWholeFolder)
        {
            return $"Only {match.Tracks.Count} of the {match.Tracks.Count + match.Unmatched.Count} tracks are on that release, so nothing was written.";
        }

        var seeding = await SeedingCheck.CreateAsync(_torrents);

        foreach (var proposal in match.Tracks.Where(track => track.Changes.Count > 0))
        {
            var problem = await MusicTagWriter.WriteAsync(proposal.Path, proposal.NewTags, seeding);

            if (problem is null)
            {
                written++;

                if (Result?.AllTracks.FirstOrDefault(track => track.Path == proposal.Path) is { } track)
                {
                    track.Tags = proposal.NewTags;
                }
            }
            else
            {
                left.Add($"{Path.GetFileName(proposal.Path)}: {problem}");
            }
        }

        var message =
            $"Wrote MusicBrainz tags ({match.Release.Title}, {match.Release.Url}) to {written} track(s) in {albumFolder}" +
            (left.Count > 0 ? $"; left {left.Count}: {string.Join("; ", left.Take(3))}" : ".");

        await _history.AddRecordAsync(
            new HistoryRecord
            {
                ActionType = HistoryActionType.MusicTagged,
                FileName = Path.GetFileName(albumFolder),
                LibraryPath = albumFolder,
                Success = left.Count == 0,
                Message = message
            });

        if (Result is not null && left.Count == 0)
        {
            Result.Issues.RemoveAll(issue => issue.Kind == MusicIssueKind.Tags && string.Equals(issue.Folder, albumFolder, StringComparison.OrdinalIgnoreCase));
        }

        Changed?.Invoke();

        return message;
    }

    public event Action? Changed;

    public MusicScanResult? Result { get; private set; }

    public bool IsBusy { get; private set; }

    public string? Status { get; private set; }

    public void Stop() => _stop = true;

    private sealed class CachedTags
    {
        public long Size { get; set; }
        public DateTime WriteUtc { get; set; }
        public MusicTags Tags { get; set; } = new();
    }

    public async Task ScanAsync()
    {
        if (!await _busy.WaitAsync(0))
        {
            return;
        }

        try
        {
            IsBusy = true;
            _stop = false;
            Report("Listing music folders");

            var settings = await _settings.GetSettingsAsync();
            var roots = LibraryRoots(settings);
            var result = new MusicScanResult { ScannedUtc = DateTime.UtcNow };

            await Task.Run(() => Scan(roots, result));

            Result = result;

            AppLog.Info($"Music scan: {result.Tracks:N0} tracks in {result.Albums:N0} album folders, {result.Issues.Count} things to look at.");
        }
        finally
        {
            IsBusy = false;
            Status = null;
            _busy.Release();
            Changed?.Invoke();
        }
    }

    private void Scan(List<string> roots, MusicScanResult result)
    {
        var cache = JsonFileStore.Load<Dictionary<string, CachedTags>>(CacheFile) ?? new(StringComparer.OrdinalIgnoreCase);
        var fresh = new Dictionary<string, CachedTags>(StringComparer.OrdinalIgnoreCase);

        var tracks = new List<MusicTrack>();
        var others = new List<string>();
        var empty = new List<string>();
        var files = new List<(string Root, FileInfo File)>();

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                result.Problems.Add($"{root} isn't there.");
                continue;
            }

            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };

                files.AddRange(new DirectoryInfo(root).EnumerateFiles("*", options).Select(file => (root, file)));

                empty.AddRange(
                    Directory.EnumerateDirectories(root, "*", options)
                        .Where(folder => !Directory.EnumerateFileSystemEntries(folder).Any()));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                result.Problems.Add($"Couldn't read {root}: {exception.Message}");
            }
        }

        var audio = files.Where(entry => MusicFormats.IsAudio(entry.File.Extension)).ToList();
        var done = 0;

        foreach (var (root, file) in audio)
        {
            if (++done % 200 == 0)
            {
                Report($"Reading tags: {done:N0} of {audio.Count:N0}");
            }

            var tags =
                cache.TryGetValue(file.FullName, out var cached) && cached.Size == file.Length && cached.WriteUtc == file.LastWriteTimeUtc
                    ? cached.Tags
                    : MusicTagReader.Read(file.FullName);

            fresh[file.FullName] = new CachedTags { Size = file.Length, WriteUtc = file.LastWriteTimeUtc, Tags = tags };

            tracks.Add(
                new MusicTrack
                {
                    Path = file.FullName,
                    Folder = file.DirectoryName ?? root,
                    Extension = file.Extension,
                    SizeBytes = file.Length,
                    LastWriteUtc = file.LastWriteTimeUtc,
                    LibraryRoot = root,
                    Tags = tags
                });
        }

        others.AddRange(files.Where(entry => !MusicFormats.IsAudio(entry.File.Extension)).Select(entry => entry.File.FullName));

        // Organise from where the Artist folders really are.
        var effective =
            roots.ToDictionary(
                root => root,
                root => MusicAnalyzer.EffectiveRoot(root, tracks.Where(track => track.LibraryRoot == root).Select(track => track.Folder)),
                StringComparer.OrdinalIgnoreCase);

        foreach (var track in tracks)
        {
            track.LibraryRoot = effective[track.LibraryRoot];
        }

        roots = effective.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        JsonFileStore.Save(CacheFile, fresh);

        Report("Looking for problems");

        result.AllTracks = tracks;
        result.Tracks = tracks.Count;
        result.Albums = tracks.Select(track => MusicAnalyzer.AlbumFolder(track.Folder)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        result.Bytes = tracks.Sum(track => track.SizeBytes);
        result.Formats =
            tracks.GroupBy(track => track.Extension.TrimStart('.').ToUpperInvariant())
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        result.Issues = MusicAnalyzer.Analyze(roots, tracks, others, empty);
    }

    public async Task DeepCheckAsync(IReadOnlyCollection<string>? folders = null)
    {
        if (Result is not { } result || FfprobeLocator.FindFfmpeg() is not { } ffmpeg || !await _busy.WaitAsync(0))
        {
            return;
        }

        try
        {
            IsBusy = true;
            _stop = false;

            var tracks =
                result.AllTracks
                    .Where(track => track.Tags.Problem is null)
                    .Where(track => folders is null || folders.Contains(MusicAnalyzer.AlbumFolder(track.Folder), StringComparer.OrdinalIgnoreCase))
                    .ToList();

            var found = 0;

            for (var i = 0; i < tracks.Count && !_stop; i++)
            {
                Report($"Checking {i + 1:N0} of {tracks.Count:N0}{(found > 0 ? $" ({found} damaged so far)" : null)}");

                var problem = await Task.Run(() => DecodeProblem(ffmpeg, tracks[i].Path));

                if (problem is null)
                {
                    continue;
                }

                found++;
                tracks[i].Tags.Problem = problem;

                result.Issues.Add(
                    new MusicIssue
                    {
                        Kind = MusicIssueKind.Broken,
                        Key = "broken|" + tracks[i].Path,
                        Title = Path.GetFileName(tracks[i].Path),
                        Detail = problem + ".",
                        Folder = tracks[i].Folder,
                        Paths = [tracks[i].Path],
                        Bytes = tracks[i].SizeBytes
                    });
            }

            AppLog.Info($"Music deep check: {tracks.Count:N0} tracks, {found} damaged{(_stop ? " (stopped early)" : null)}.");
        }
        finally
        {
            IsBusy = false;
            Status = null;
            _busy.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>What ffmpeg says is wrong when it plays the file through; null when nothing.</summary>
    public static string? DecodeProblem(string ffmpeg, string path)
    {
        try
        {
            using var process =
                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName = ffmpeg,
                        ArgumentList = { "-v", "error", "-nostdin", "-i", path, "-map", "0:a:0", "-f", "null", "-" },
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });

            if (process is null)
            {
                return null;
            }

            var errors = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();

            if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
            {
                process.Kill(entireProcessTree: true);

                return "It took over 5 minutes to play through, so it's probably damaged";
            }

            var lines =
                errors.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(line => line.Length > 0)
                    .ToList();

            if (process.ExitCode != 0 || lines.Count > 0)
            {
                var first = lines.FirstOrDefault() ?? $"ffmpeg stopped with code {process.ExitCode}";

                // ffmpeg starts each line with "[decoder @ 0x...]"; that part means nothing to a person.
                var bracket = first.StartsWith('[') ? first.IndexOf(']') : -1;

                return $"Damaged: {(bracket > 0 ? first[(bracket + 1)..].Trim() : first)}{(lines.Count > 1 ? $" (and {lines.Count - 1} more error(s))" : null)}";
            }

            return null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<string> TidyAsync(MusicIssue issue)
    {
        var moves = issue.Moves.ToList();

        // A misnamed album folder moves whole, pictures and all - but only
        // when nothing else (another album) is inside it; disc folders are fine.
        if (issue.Key.StartsWith("named|", StringComparison.Ordinal) && moves.Count > 0 &&
            Directory.Exists(issue.Folder) &&
            Directory.EnumerateDirectories(issue.Folder).All(sub => MusicAnalyzer.AlbumFolder(sub) != sub))
        {
            var target = TargetFolderOf(issue);

            moves =
                Directory.EnumerateFiles(issue.Folder, "*", SearchOption.AllDirectories)
                    .Select(file => new MusicMove(file, Path.Combine(target, Path.GetRelativePath(issue.Folder, file))))
                    .ToList();
        }

        var moved = 0;
        var skipped = new List<string>();

        foreach (var move in moves)
        {
            try
            {
                if (File.Exists(move.To))
                {
                    skipped.Add($"{Path.GetFileName(move.From)} (one is already there)");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(move.To)!);

                // Same drive, so this is a rename: instant, and a file still
                // seeding keeps seeding (the torrent has its own name for it).
                File.Move(move.From, move.To);
                moved++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                skipped.Add($"{Path.GetFileName(move.From)} ({exception.Message})");
            }
        }

        // A split album's tracks that were in both folders: the extra copy goes.
        var quarantined = 0;

        if (issue.Key.StartsWith("split|", StringComparison.Ordinal))
        {
            foreach (var path in issue.Paths)
            {
                var size = File.Exists(path) ? new FileInfo(path).Length : 0;
                var where = await _quarantine.QuarantineFileAsync(path);

                await _history.AddRecordAsync(
                    new HistoryRecord
                    {
                        ActionType = HistoryActionType.DuplicateQuarantined,
                        FileName = Path.GetFileName(path),
                        LibraryPath = path,
                        QuarantinePath = where ?? string.Empty,
                        SizeBytes = size,
                        Success = where is not null,
                        Message = where is not null ? $"Music: {Path.GetFileName(path)} was in two folders of {issue.Title}." : "Music: couldn't move it to Quarantine."
                    });

                if (where is null)
                {
                    skipped.Add($"{Path.GetFileName(path)} (couldn't go to Quarantine)");
                }
                else
                {
                    quarantined++;
                }
            }
        }

        foreach (var folder in moves.Select(move => Path.GetDirectoryName(move.From)!).Concat(issue.Paths.Select(path => Path.GetDirectoryName(path)!)).Append(issue.Folder).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            RemoveEmptyFolders(folder);
        }

        var message =
            $"Tidied {issue.Folder}: moved {moved} file(s)" +
            (quarantined > 0 ? $", {quarantined} extra copy(ies) to Quarantine" : null) +
            (skipped.Count > 0 ? $"; left {skipped.Count}: {string.Join(", ", skipped.Take(5))}" : ".");

        await _history.AddRecordAsync(
            new HistoryRecord
            {
                ActionType = HistoryActionType.MusicTidied,
                FileName = Path.GetFileName(issue.Folder),
                LibraryPath = issue.Folder,
                Success = skipped.Count == 0,
                Message = message
            });

        Forget(issue);

        return message;
    }

    // Every move of a misnamed folder goes under one target folder.
    private static string TargetFolderOf(MusicIssue issue)
    {
        var first = issue.Moves[0];
        var relative = Path.GetRelativePath(issue.Folder, first.From);
        var target = first.To;

        foreach (var _ in relative.Split(Path.DirectorySeparatorChar))
        {
            target = Path.GetDirectoryName(target)!;
        }

        return target;
    }

    public async Task<string> QuarantineAsync(MusicIssue issue, IReadOnlyCollection<string>? paths = null)
    {
        var chosen = (paths ?? issue.Paths).ToList();
        var done = 0;
        var failed = 0;

        foreach (var path in chosen)
        {
            // Empty folders aren't worth keeping in Quarantine.
            if (Directory.Exists(path))
            {
                try
                {
                    Directory.Delete(path);
                    done++;
                }
                catch (IOException)
                {
                    failed++;
                }

                continue;
            }

            var size = File.Exists(path) ? new FileInfo(path).Length : 0;
            var quarantined = await _quarantine.QuarantineFileAsync(path);

            await _history.AddRecordAsync(
                new HistoryRecord
                {
                    ActionType = issue.Kind == MusicIssueKind.Duplicate ? HistoryActionType.DuplicateQuarantined : HistoryActionType.JunkFileQuarantined,
                    FileName = Path.GetFileName(path),
                    LibraryPath = path,
                    QuarantinePath = quarantined ?? string.Empty,
                    SizeBytes = size,
                    Success = quarantined is not null,
                    Message = quarantined is not null ? $"Music: {issue.Title} - {issue.Detail}" : "Music: couldn't move it to Quarantine."
                });

            if (quarantined is null)
            {
                failed++;
            }
            else
            {
                done++;
            }
        }

        RemoveEmptyFolders(issue.Folder);
        Forget(issue);

        return failed == 0
            ? $"{done} sent to Quarantine."
            : $"{done} sent to Quarantine; {failed} couldn't be moved (open in another program?).";
    }

    private void Forget(MusicIssue issue)
    {
        Result?.Issues.Remove(issue);
        Changed?.Invoke();
    }

    // Folders left with nothing in them, up to (not including) the library.
    private void RemoveEmptyFolders(string folder)
    {
        var roots = Result is null ? [] : Result.AllTracks.Select(track => track.LibraryRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var current = folder;

        while (!string.IsNullOrEmpty(current) &&
               Directory.Exists(current) &&
               !roots.Any(root => string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(current), StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(current).Any())
                {
                    // Only leftovers like Thumbs.db: not worth keeping the folder for.
                    if (Directory.EnumerateFiles(current, "*", SearchOption.AllDirectories).All(file => Path.GetFileName(file).Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(file).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)))
                    {
                        Directory.Delete(current, recursive: true);
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    Directory.Delete(current);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                break;
            }

            current = Path.GetDirectoryName(current);
        }
    }

    /// <summary>Every music library folder on an enabled drive.</summary>
    public static List<string> LibraryRoots(SimplifiarrSettings settings) =>
        settings.DriveProfiles
            .Where(profile => profile.Enabled && !string.IsNullOrWhiteSpace(profile.MusicLibraryPath))
            .Select(profile => Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile.MusicLibraryPath)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void Report(string status)
    {
        Status = status;
        Changed?.Invoke();
    }
}
