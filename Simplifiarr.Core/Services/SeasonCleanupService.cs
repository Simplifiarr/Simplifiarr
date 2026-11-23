using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

/// <summary>One of the folders a split season is in.</summary>
public sealed record SeasonFolderInfo(
    string Name,
    string Path,
    int Episodes,
    int OtherFiles,
    DateTime CreatedUtc,
    bool Keep);

/// <summary>
/// A show with one season in two or more folders ("Season 06" and
/// "Season 6"), which Jellyfin shows as two seasons.
/// </summary>
public sealed record SplitSeason(
    string Show,
    string ShowFolder,
    int Season,
    string KeepName,
    IReadOnlyList<SeasonFolderInfo> Folders,
    string Why,
    ArrItem? SonarrShow,
    IReadOnlyList<string> Warnings)
{
    public string Key => $"{ShowFolder}|{Season}";

    public string SeasonLabel => Season == 0 ? "Specials" : $"Season {Season}";

    public IEnumerable<SeasonFolderInfo> Strays => Folders.Where(folder => !folder.Keep);
}

/// <summary>How a merge went.</summary>
public sealed record SeasonMergeResult(
    bool Merged,
    int Moved,
    int Quarantined,
    IReadOnlyList<string> Skipped,
    string Message);

public interface ISeasonCleanupService
{
    /// <summary>Every show on the enabled drives (and in Sonarr) with a season in more than one folder.</summary>
    Task<List<SplitSeason>> FindAsync();

    /// <summary>
    /// Moves everything in the other folders into the one kept, then asks
    /// Sonarr and Jellyfin to look again. Nothing is deleted: a picture or
    /// .nfo the kept folder already has goes to Quarantine, and a file a
    /// torrent is seeding from, or a video with the same name, is left where
    /// it is.
    /// </summary>
    Task<SeasonMergeResult> MergeAsync(SplitSeason split);
}

public class SeasonCleanupService : ISeasonCleanupService
{
    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".ts", ".m2ts", ".wmv", ".webm", ".mpg", ".mpeg" };

    private readonly ISettingsService _settings;
    private readonly IArrPathService _arrPaths;
    private readonly ISonarrClient _sonarr;
    private readonly ITorrentClient _torrents;
    private readonly IJellyfinClient _jellyfin;
    private readonly IQuarantineService _quarantine;
    private readonly IHistoryService _history;

    public SeasonCleanupService(
        ISettingsService settings,
        IArrPathService arrPaths,
        ISonarrClient sonarr,
        ITorrentClient torrents,
        IJellyfinClient jellyfin,
        IQuarantineService quarantine,
        IHistoryService history)
    {
        _settings = settings;
        _arrPaths = arrPaths;
        _sonarr = sonarr;
        _torrents = torrents;
        _jellyfin = jellyfin;
        _quarantine = quarantine;
        _history = history;
    }

    public async Task<List<SplitSeason>> FindAsync()
    {
        var shows =
            await _arrPaths.GetAllAsync(StorageKinds.Tv);

        var formats =
            shows.Count > 0 ? await _sonarr.GetFolderFormatsAsync() : null;

        var showFolders =
            new Dictionary<string, ArrItem?>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in await _settings.GetEnabledDriveProfilesAsync())
        {
            if (!string.IsNullOrWhiteSpace(profile.TvLibraryPath) && Directory.Exists(profile.TvLibraryPath))
            {
                foreach (var folder in Directory.EnumerateDirectories(profile.TvLibraryPath))
                {
                    showFolders[Path.TrimEndingDirectorySeparator(folder)] = null;
                }
            }
        }

        foreach (var show in shows)
        {
            var folder = Path.TrimEndingDirectorySeparator(show.Path.Replace('/', '\\'));

            if (Directory.Exists(folder))
            {
                showFolders[folder] = show;
            }
        }

        var found = new List<SplitSeason>();

        foreach (var (folder, show) in showFolders.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            found.AddRange(Find(folder, show, formats));
        }

        return found;
    }

    /// <summary>The split seasons in one show's folder.</summary>
    public static List<SplitSeason> Find(
        string showFolder,
        ArrItem? sonarrShow,
        ArrFolderFormats? sonarrFormats)
    {
        List<string> folders;

        try
        {
            folders = Directory.GetDirectories(showFolder).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var splits = new List<SplitSeason>();

        var bySeason =
            folders
                .Select(path => (Path: path, Number: SeasonFolders.NumberOf(Path.GetFileName(path))))
                .Where(entry => entry.Number is not null)
                .GroupBy(entry => entry.Number!.Value.Season)
                .Where(group => group.Count() > 1);

        foreach (var group in bySeason)
        {
            var season = group.Key;

            var infos =
                group
                    .Select(entry => Describe(entry.Path))
                    .ToList();

            // Sonarr's name when Sonarr has the show: it puts the next
            // episode there whatever else is on disk. Otherwise the folder
            // with the most episodes.
            var sonarrName =
                sonarrShow is not null && sonarrFormats is not null
                    ? ArrFolderNamer.Season(season == 0 ? sonarrFormats.SpecialsFolderFormat : sonarrFormats.SeasonFolderFormat, season)
                    : null;

            var keepName =
                sonarrName ??
                infos
                    .OrderByDescending(info => info.Episodes)
                    .ThenBy(info => info.CreatedUtc)
                    .First()
                    .Name;

            infos =
                infos
                    .Select(info => info with { Keep = string.Equals(info.Name, keepName, StringComparison.OrdinalIgnoreCase) })
                    .OrderByDescending(info => info.Keep)
                    .ThenBy(info => info.CreatedUtc)
                    .ToList();

            var warnings = new List<string>();

            var episodesInBoth =
                infos
                    .SelectMany(info => EpisodeCodes(info.Path).Select(code => (code, info.Name)))
                    .GroupBy(entry => entry.code)
                    .Where(entry => entry.Select(item => item.Name).Distinct().Count() > 1)
                    .Select(entry => entry.Key)
                    .OrderBy(code => code)
                    .ToList();

            if (episodesInBoth.Count > 0)
            {
                warnings.Add(
                    $"{string.Join(", ", episodesInBoth.Take(5))}{(episodesInBoth.Count > 5 ? "..." : "")} " +
                    "are in more than one of these folders. After merging, Duplicates can pick which copy to keep.");
            }

            splits.Add(
                new SplitSeason(
                    sonarrShow?.Title ?? Path.GetFileName(showFolder),
                    showFolder,
                    season,
                    keepName,
                    infos,
                    Explain(infos, keepName, sonarrShow, sonarrFormats, season),
                    sonarrShow,
                    warnings));
        }

        return splits;
    }

    public async Task<SeasonMergeResult> MergeAsync(SplitSeason split)
    {
        var keepFolder = Path.Combine(split.ShowFolder, split.KeepName);
        var seeding = await SeedingPathsAsync();

        Directory.CreateDirectory(keepFolder);

        var moved = 0;
        var quarantined = 0;
        var skipped = new List<string>();

        foreach (var stray in split.Strays)
        {
            if (!Directory.Exists(stray.Path))
            {
                continue;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(stray.Path).ToList())
            {
                var name = Path.GetFileName(entry);
                var target = Path.Combine(keepFolder, name);
                var isFolder = Directory.Exists(entry);

                if (seeding.Any(path => PathContainment.IsPathUnderRoot(entry, path) || PathContainment.IsPathUnderRoot(path, entry) || string.Equals(path, entry, StringComparison.OrdinalIgnoreCase)))
                {
                    skipped.Add($"{name}: a torrent is seeding from it");
                    continue;
                }

                try
                {
                    if (File.Exists(target) || Directory.Exists(target))
                    {
                        // The kept folder's own picture or .nfo wins; this
                        // copy goes where it can be put back from.
                        if (!isFolder && !VideoExtensions.Contains(Path.GetExtension(name)) &&
                            await _quarantine.QuarantineFileAsync(entry) is not null)
                        {
                            quarantined++;
                        }
                        else
                        {
                            skipped.Add($"{name}: {split.KeepName} already has one");
                        }

                        continue;
                    }

                    if (isFolder)
                    {
                        Directory.Move(entry, target);
                    }
                    else
                    {
                        File.Move(entry, target);
                    }

                    moved++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    skipped.Add($"{name}: {exception.Message}");
                }
            }

            try
            {
                if (!Directory.EnumerateFileSystemEntries(stray.Path).Any())
                {
                    Directory.Delete(stray.Path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                skipped.Add($"{stray.Name}: couldn't remove the empty folder ({exception.Message})");
            }
        }

        if (split.SonarrShow is not null)
        {
            await _arrPaths.RescanAsync(StorageKinds.Tv, split.SonarrShow);
        }

        try
        {
            await _jellyfin.FoldersChangedAsync([split.ShowFolder]);
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException)
        {
            // Jellyfin not set up or not running: its next library scan finds it.
        }

        var strayNames = string.Join(" and ", split.Strays.Select(stray => $"\"{stray.Name}\""));
        var merged = skipped.Count == 0;

        var message =
            $"Moved {moved} item(s) from {strayNames} into \"{split.KeepName}\"" +
            (quarantined > 0 ? $"; {quarantined} picture(s) or .nfo file(s) it already had went to Quarantine" : string.Empty) +
            (merged ? "." : $". Left where they were: {string.Join("; ", skipped.Take(5))}{(skipped.Count > 5 ? "..." : "")}");

        await _history.AddRecordAsync(
            new HistoryRecord
            {
                ActionType = HistoryActionType.SeasonMerged,
                FileName = $"{split.Show} {split.SeasonLabel}",
                LibraryPath = keepFolder,
                Success = merged,
                Message = message
            });

        return new SeasonMergeResult(merged, moved, quarantined, skipped, message);
    }

    private async Task<List<string>> SeedingPathsAsync()
    {
        try
        {
            return (await _torrents.TryGetTorrentsAsync() ?? [])
                .Select(torrent => torrent.ContentPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.TrimEndingDirectorySeparator(path.Replace('/', '\\')))
                .ToList();
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            return [];
        }
    }

    private static SeasonFolderInfo Describe(string path)
    {
        var files =
            Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToList();

        var episodes =
            files.Count(file => VideoExtensions.Contains(Path.GetExtension(file)) && !file.Contains("sample", StringComparison.OrdinalIgnoreCase));

        return new SeasonFolderInfo(
            Path.GetFileName(path),
            path,
            episodes,
            files.Count - episodes,
            Directory.GetCreationTimeUtc(path),
            false);
    }

    private static IEnumerable<string> EpisodeCodes(string folder) =>
        Directory.EnumerateFiles(folder)
            .Where(file => VideoExtensions.Contains(Path.GetExtension(file)))
            .Select(file => System.Text.RegularExpressions.Regex.Match(Path.GetFileName(file), @"S(\d{1,3})E(\d{1,3})", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .Where(match => match.Success)
            .Select(match => $"S{int.Parse(match.Groups[1].Value):00}E{int.Parse(match.Groups[2].Value):00}")
            .Distinct();

    /// <summary>Why there are two folders, as far as can be told, and which is kept.</summary>
    private static string Explain(
        IReadOnlyList<SeasonFolderInfo> folders,
        string keepName,
        ArrItem? sonarrShow,
        ArrFolderFormats? sonarrFormats,
        int season)
    {
        var keep = folders.FirstOrDefault(folder => folder.Keep);
        var strays = folders.Where(folder => !folder.Keep).ToList();

        if (sonarrShow is null || sonarrFormats is null)
        {
            return $"Sonarr doesn't have this show, so the folder with the most episodes, \"{keepName}\", is kept.";
        }

        var format = season == 0 ? sonarrFormats.SpecialsFolderFormat : sonarrFormats.SeasonFolderFormat;
        var text = $"Sonarr's season folder format is \"{format}\", so it puts new episodes in \"{keepName}\".";

        if (keep is null)
        {
            return text + $" There's no \"{keepName}\" folder yet; it is made and everything moved into it.";
        }

        foreach (var stray in strays)
        {
            text +=
                stray.CreatedUtc < keep.CreatedUtc
                    ? $" \"{stray.Name}\" was made earlier ({stray.CreatedUtc.ToLocalTime():MMM d, yyyy}), before Sonarr's folder: most likely imported by hand or by another tool, or Sonarr's format has changed since."
                    : $" \"{stray.Name}\" was made later ({stray.CreatedUtc.ToLocalTime():MMM d, yyyy}), so not by Sonarr: by hand or by another tool.";
        }

        return text;
    }
}
