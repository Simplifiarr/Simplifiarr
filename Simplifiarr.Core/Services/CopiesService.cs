using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public sealed class CopiesService : ICopiesService
{
    private const string CacheFileName = "copies.json";

    private static readonly TimeSpan ModifiedTolerance = TimeSpan.FromSeconds(2);

    // Jellyfin's names for a movie's extras, as folders and as name endings.
    private static readonly HashSet<string> ExtraFolders =
        new(["extras", "extra", "featurettes", "behind the scenes", "deleted scenes", "interviews", "scenes", "samples", "sample",
             "shorts", "trailers", "clips", "other", "bonus", "bonus features"],
            StringComparer.OrdinalIgnoreCase);

    private static readonly Regex ExtraEnding =
        new(@"[-_. ](trailer|sample|scene|clip|interview|behindthescenes|deleted|deletedscene|featurette|short|other|extra)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "CD1", "Part 2", "Disc 1": a movie split across files.
    private static readonly Regex PartNumber =
        new(@"(?:^|[ ._\-\[(])(?:cd|dis[ck]|part|pt)[ ._\-]?(\d{1,2})(?=$|[ ._\-\])])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TrailingYear =
        new(@"(19|20)\d\d$", RegexOptions.Compiled);

    // A daily show's episode: "Show.2026.11.16.Guest.720p".
    private static readonly Regex AirDate =
        new(@"(?<![0-9])((?:19|20)\d\d)[ ._\-](0[1-9]|1[0-2])[ ._\-](0[1-9]|[12]\d|3[01])(?![0-9])", RegexOptions.Compiled);

    /// <summary>What a library folder holds, for telling an episode from a movie.</summary>
    public enum LibraryKind
    {
        Unknown,
        Movies,
        Tv
    }

    private readonly ISettingsService _settings;
    private readonly IFileInventoryService _inventory;
    private readonly ITorrentClient _client;
    private readonly IReleaseParser _parser;
    private readonly IDownloadCleanupService _downloads;
    private readonly IHardlinkConversionPlanner? _planner;
    private readonly IHardlinkRemediationService? _remediation;
    private readonly IQuarantineService? _quarantine;
    private readonly IHistoryService _history;
    private readonly IMediaCatalogService? _catalog;
    private readonly IDuplicateAssetAnalysisService? _duplicates;
    private readonly IIgnoredItemsService? _ignored;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _cachePath;

    private CopiesScanResult? _last;

    public CopiesService(
        ISettingsService settings,
        IFileInventoryService inventory,
        ITorrentClient client,
        IReleaseParser parser,
        IDownloadCleanupService downloads,
        IHistoryService history,
        IHardlinkConversionPlanner? planner = null,
        IHardlinkRemediationService? remediation = null,
        IQuarantineService? quarantine = null,
        IMediaCatalogService? catalog = null,
        IDuplicateAssetAnalysisService? duplicates = null,
        IIgnoredItemsService? ignored = null)
    {
        _settings = settings;
        _inventory = inventory;
        _client = client;
        _parser = parser;
        _downloads = downloads;
        _history = history;
        _planner = planner;
        _remediation = remediation;
        _quarantine = quarantine;
        _catalog = catalog;
        _duplicates = duplicates;
        _ignored = ignored;

        _cachePath = AppPaths.DataFile(CacheFileName);
        _last = JsonFileStore.Load<CopiesScanResult>(_cachePath);
    }

    /// <summary>Smaller videos are extras and samples, never a version of a title.</summary>
    public long VersionMinimumBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>How old a folder's saved listing may be before it is read again.</summary>
    public TimeSpan ListingMaxAge { get; set; } = TimeSpan.FromMinutes(30);

    public CopiesScanResult? Last => _last;

    // ---------------------------------------------------------------- scan

    private sealed record Root(string Path, bool IsLibrary, string DriveName, LibraryKind Kind = LibraryKind.Unknown);

    private sealed class Seen
    {
        public required FileRecord File { get; init; }

        public required Root Root { get; init; }
    }

    public async Task<CopiesScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var result = await BuildAsync(cancellationToken);

            _last = result;
            Save();

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CopiesScanResult> BuildAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.GetSettingsAsync();
        var profiles = settings.DriveProfiles.Where(profile => profile.Enabled).ToList();
        var torrents = await _client.TryGetTorrentsAsync();
        var lookup = new TorrentLookup(torrents ?? []);

        var result =
            new CopiesScanResult
            {
                ScannedUtc = DateTime.UtcNow,
                ClientReachable = torrents is not null,
                ClientName = _client.Name
            };

        var roots = Roots(profiles, torrents ?? []);

        // A torrent's folder is the one just inside wherever torrents are saved.
        var containers =
            profiles
                .SelectMany(profile => profile.AllDownloadPaths().Append(profile.SeedOnlyPath))
                .Concat((torrents ?? []).Select(torrent => torrent.SavePath))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.TrimEndingDirectorySeparator(path.Trim()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        var files = await ReadAsync(roots, cancellationToken);
        var library = files.Where(seen => seen.Root.IsLibrary).ToList();
        var downloads = files.Where(seen => !seen.Root.IsLibrary).ToList();

        // ---- one CopyFile per file on disk, hardlinked names together
        var media =
            files
                .Where(seen => seen.File.SizeBytes > 0 && IsMedia(seen.File))
                .ToList();

        var physical = new List<CopyFile>();
        var byName = new Dictionary<string, CopyFile>(StringComparer.OrdinalIgnoreCase);
        var linkCounts = new Dictionary<CopyFile, int>();
        var types = new Dictionary<CopyFile, FileType>();
        var kinds = new Dictionary<CopyFile, LibraryKind>();

        foreach (var bucket in media.GroupBy(seen => seen.File.SizeBytes))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entries = bucket.ToList();

            // Two names can only be one file when they are the same size, so
            // a size nothing else has needs no question to Windows.
            List<List<Seen>> groups =
                entries.Count == 1
                    ? [[entries[0]]]
                    : entries
                        .GroupBy(seen => NativeHardlinks.GetIdentity(seen.File.FullPath) is { } identity
                            ? $"{identity.VolumeSerialNumber}:{identity.FileIndex}"
                            : seen.File.FullPath, StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.ToList())
                        .ToList();

            foreach (var group in groups)
            {
                var first = group[0];

                var file =
                    new CopyFile
                    {
                        SizeBytes = first.File.SizeBytes,
                        ModifiedUtc = first.File.LastModifiedUtc,
                        LibraryNames = group.Where(seen => seen.Root.IsLibrary).Select(seen => seen.File.FullPath).ToList(),
                        DownloadNames = group.Where(seen => !seen.Root.IsLibrary).Select(seen => seen.File.FullPath).ToList(),
                        DriveName = (group.FirstOrDefault(seen => seen.Root.IsLibrary) ?? first).Root.DriveName,
                        Quality = Quality(first.File.FileName)
                    };

                file.Torrent = file.Names.Select(lookup.Find).FirstOrDefault(name => name is not null) ?? string.Empty;
                file.SeedingUnknown = torrents is null && (file.InDownloads || group.Count > 1);

                physical.Add(file);
                types[file] = first.File.FileType;
                kinds[file] = (group.FirstOrDefault(seen => seen.Root.IsLibrary) ?? first).Root.Kind;

                foreach (var seen in group)
                {
                    byName[seen.File.FullPath] = file;
                }

                if (entries.Count > 1 && NativeHardlinks.GetIdentity(first.File.FullPath) is { } known)
                {
                    linkCounts[file] = known.LinkCount;
                }
            }
        }

        // ---- downloads with the same bytes as a library file
        var fingerprints = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        async Task<string?> Fingerprint(string path)
        {
            if (!fingerprints.TryGetValue(path, out var value))
            {
                value = await FingerprintAsync(path, 3, cancellationToken);
                fingerprints[path] = value;
            }

            return value;
        }

        // Download names that can go because the library has them: the
        // same file under another name, or the same bytes.
        var removable = new Dictionary<string, (string LibraryPath, DownloadMatchKind Kind, DateTime LibraryModified)>(StringComparer.OrdinalIgnoreCase);
        var sameAsLibrary = new HashSet<CopyFile>();

        foreach (var file in physical.Where(file => file.InLibrary && file.InDownloads))
        {
            result.MatchedDownloads.AddRange(file.DownloadNames);
            sameAsLibrary.Add(file);

            result.AlreadyLinkedCount++;
            result.LinkedByDrive[file.DriveName] = result.LinkedByDrive.GetValueOrDefault(file.DriveName) + 1;

            if (file.Seeding)
            {
                continue;
            }

            foreach (var name in file.DownloadNames)
            {
                removable[name] = (file.LibraryNames[0], DownloadMatchKind.Hardlink, file.ModifiedUtc);
            }
        }

        foreach (var bucket in physical.GroupBy(file => file.SizeBytes).Where(bucket => bucket.Count() > 1))
        {
            var libraryFiles = bucket.Where(file => file.InLibrary).ToList();

            if (libraryFiles.Count == 0)
            {
                continue;
            }

            foreach (var download in bucket.Where(file => !file.InLibrary))
            {
                var print = await Fingerprint(download.DownloadNames[0]);

                if (print is null)
                {
                    continue;
                }

                var downloadRoot = Path.GetPathRoot(download.DownloadNames[0]);

                CopyFile? match = null;

                foreach (var candidate in libraryFiles.OrderByDescending(file => string.Equals(Path.GetPathRoot(file.LibraryNames[0]), downloadRoot, StringComparison.OrdinalIgnoreCase)))
                {
                    if (await Fingerprint(candidate.LibraryNames[0]) == print)
                    {
                        match = candidate;
                        break;
                    }
                }

                if (match is null)
                {
                    continue;
                }

                sameAsLibrary.Add(download);
                result.MatchedDownloads.AddRange(download.DownloadNames);

                var sameDrive = string.Equals(Path.GetPathRoot(match.LibraryNames[0]), downloadRoot, StringComparison.OrdinalIgnoreCase);

                if (download.Seeding)
                {
                    // Seeding from another drive can't be linked: Move Seeds
                    // moves the torrent next to the library copy instead.
                    if (sameDrive)
                    {
                        result.ToLink.Add(
                            new CopyToLink
                            {
                                Library = match,
                                Download = download,
                                LibraryHasOtherNames =
                                    match.LibraryNames.Count > 1 ||
                                    (linkCounts.TryGetValue(match, out var count) && count > match.Names.Count())
                            });
                    }

                    continue;
                }

                foreach (var name in download.DownloadNames)
                {
                    removable[name] = (match.LibraryNames[0], DownloadMatchKind.IdenticalCopy, match.ModifiedUtc);
                }
            }
        }

        result.ToLink.RemoveAll(link => link.LibraryHasOtherNames);

        // ---- torrent folders: what a whole download folder holds
        var folders =
            downloads
                .GroupBy(seen => DownloadCleanupService.GroupOf(seen.File.FullPath, containers), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        if (torrents is not null)
        {
            foreach (var (folder, entries) in folders)
            {
                if (ToDelete(folder, entries, removable, lookup) is { } leftover)
                {
                    result.ToDelete.Add(leftover);
                }
            }
        }

        // ---- versions: the same title as different files
        var bigVideosPerFolder =
            physical
                .Where(file => types[file] == FileType.Video && file.SizeBytes >= VersionMinimumBytes)
                .SelectMany(file => file.Names)
                .GroupBy(name => Path.GetDirectoryName(name) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        var assets =
            (_catalog?.GetAssets() ?? [])
                .Where(asset => !string.IsNullOrWhiteSpace(asset.CanonicalPath))
                .GroupBy(asset => asset.CanonicalPath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var byIdentity = new Dictionary<string, List<CopyFile>>(StringComparer.Ordinal);

        foreach (var file in physical)
        {
            if (types[file] != FileType.Video ||
                file.SizeBytes < VersionMinimumBytes ||
                (!file.InLibrary && sameAsLibrary.Contains(file)) ||
                file.Names.Any(name => _ignored?.IsIgnored(name) == true))
            {
                continue;
            }

            var name = file.Path;
            var alone = bigVideosPerFolder.GetValueOrDefault(Path.GetDirectoryName(name) ?? string.Empty) <= 1;

            if (VideoIdentity(_parser, name, alone, kinds[file]) is not { } identity)
            {
                continue;
            }

            if (!byIdentity.TryGetValue(identity, out var list))
            {
                list = [];
                byIdentity[identity] = list;
            }

            list.Add(file);
        }

        foreach (var (identity, versions) in byIdentity)
        {
            if (versions.Count < 2 || !versions.Any(version => version.InLibrary))
            {
                continue;
            }

            foreach (var version in versions.Where(version => !version.InLibrary))
            {
                DescribeTorrentFolder(version, folders, containers, lookup, VersionMinimumBytes);
                result.MatchedDownloads.AddRange(version.DownloadNames);
            }

            result.Versions.Add(Group(identity, versions, assets));
        }

        result.Versions.AddRange(await OtherMediaVersionsAsync(profiles));

        result.Versions =
            result.Versions
                .OrderByDescending(group => group.ReclaimableBytes)
                .ThenBy(group => group.DisplayTitle, StringComparer.OrdinalIgnoreCase)
                .ToList();

        result.ToDelete =
            result.ToDelete
                .OrderBy(leftover => leftover.Kind)
                .ThenByDescending(leftover => leftover.FreedBytes)
                .ToList();

        result.ToLink = result.ToLink.OrderByDescending(link => link.FreedBytes).ToList();

        result.MatchedDownloads =
            result.MatchedDownloads.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        AppLog.Info(
            $"Copies: {files.Count:N0} files on {roots.Count} folder(s); {result.ToDelete.Count} download(s) to delete, " +
            $"{result.ToLink.Count} to link, {result.Versions.Count} title(s) with more than one version" +
            (torrents is null ? $"; {_client.Name} couldn't be reached." : "."));

        return result;
    }

    // Library folders and download folders, each once. A folder inside
    // another is left out: reading the outer one lists it already.
    private static List<Root> Roots(List<DriveProfile> profiles, List<TorrentRecord> torrents)
    {
        var library =
            profiles.SelectMany(profile =>
                new[]
                {
                    new Root(profile.MovieLibraryPath, true, profile.Name, LibraryKind.Movies),
                    new Root(profile.TvLibraryPath, true, profile.Name, LibraryKind.Tv),
                    new Root(profile.BookLibraryPath, true, profile.Name),
                    new Root(profile.AudiobookLibraryPath, true, profile.Name),
                    new Root(profile.MusicLibraryPath, true, profile.Name),
                    new Root(profile.ComicLibraryPath, true, profile.Name)
                });

        var download =
            profiles.SelectMany(profile =>
                profile.AllDownloadPaths()
                    .Append(profile.SeedOnlyPath)
                    .Select(path => new Root(path, false, profile.Name)));

        var all =
            library.Concat(download)
                .Where(root => !string.IsNullOrWhiteSpace(root.Path))
                .Select(root => root with { Path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Path.Trim())) })
                .Where(root => Directory.Exists(root.Path))
                .DistinctBy(root => root.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

        // Torrents saved somewhere no drive entry lists are downloads too,
        // unless that is a whole drive or holds a library.
        foreach (var savePath in torrents.Select(torrent => torrent.SavePath).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string full;

            try
            {
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(savePath.Trim()));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!Directory.Exists(full) ||
                string.Equals(Path.GetPathRoot(full)?.TrimEnd('\\'), full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
                all.Any(root => PathContainment.IsPathUnderRoot(full, root.Path)) ||
                all.Any(root => root.IsLibrary && PathContainment.IsPathUnderRoot(root.Path, full)))
            {
                continue;
            }

            var drive =
                profiles.FirstOrDefault(profile =>
                    string.Equals(Path.GetPathRoot(profile.DriveRoot), Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))?.Name ?? string.Empty;

            all.Add(new Root(full, false, drive));
        }

        return all;
    }

    private async Task<List<Seen>> ReadAsync(List<Root> roots, CancellationToken cancellationToken)
    {
        var deepestFirst = roots.OrderByDescending(root => root.Path.Length).ToList();

        var outer =
            roots.Where(root => !roots.Any(other => !ReferenceEquals(other, root) && PathContainment.IsPathUnderRoot(root.Path, other.Path) &&
                                                     !string.Equals(other.Path, root.Path, StringComparison.OrdinalIgnoreCase)))
                .ToList();

        var seen = new Dictionary<string, Seen>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in outer)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stale = _inventory.GetLastScanUtc(root.Path) is not { } listed || DateTime.UtcNow - listed > ListingMaxAge;
            var files = await _inventory.GetInventoryAsync(root.Path, forceRefresh: stale);

            foreach (var file in files)
            {
                if (seen.ContainsKey(file.FullPath))
                {
                    continue;
                }

                // The folder closest to the file says what it is: a downloads
                // folder inside a library folder holds downloads.
                var owner = deepestFirst.FirstOrDefault(candidate => PathContainment.IsPathUnderRoot(file.FullPath, candidate.Path));

                if (owner is not null)
                {
                    seen[file.FullPath] = new Seen { File = file, Root = owner };
                }
            }
        }

        return seen.Values.ToList();
    }

    private static bool IsMedia(FileRecord file) =>
        (file.FileType is FileType.Video or FileType.Audio or FileType.Book or FileType.Comic) &&
        DownloadCleanupService.RoleOf(file) == DownloadCleanupService.FileRole.Media;

    // A torrent folder nothing uses, holding files the library has.
    private DownloadLeftover? ToDelete(
        string folder,
        List<Seen> entries,
        Dictionary<string, (string LibraryPath, DownloadMatchKind Kind, DateTime LibraryModified)> removable,
        TorrentLookup lookup)
    {
        if (entries.Any(seen => lookup.Find(seen.File.FullPath) is not null))
        {
            return null;
        }

        var media = new List<DownloadCleanupCandidate>();
        var junk = new List<FileRecord>();
        var unmatched = new List<FileRecord>();
        var other = new List<FileRecord>();

        foreach (var seen in entries)
        {
            var file = seen.File;

            if (removable.TryGetValue(file.FullPath, out var match))
            {
                media.Add(
                    new DownloadCleanupCandidate
                    {
                        DownloadPath = file.FullPath,
                        FileName = file.FileName,
                        SizeBytes = file.SizeBytes,
                        LibraryPath = match.LibraryPath,
                        LibrarySizeBytes = file.SizeBytes,
                        MatchKind = match.Kind,
                        DriveProfileName = seen.Root.DriveName,
                        DownloadModifiedUtc = file.LastModifiedUtc,
                        LibraryModifiedUtc = match.LibraryModified
                    });

                continue;
            }

            var role = file.SizeBytes <= 0 ? DownloadCleanupService.FileRole.Junk : DownloadCleanupService.RoleOf(file);

            // A short clip the library doesn't have is a sample or an extra.
            if (role == DownloadCleanupService.FileRole.Media && file.FileType == FileType.Video && file.SizeBytes < VersionMinimumBytes)
            {
                role = DownloadCleanupService.FileRole.Junk;
            }

            (role switch
            {
                DownloadCleanupService.FileRole.Junk => junk,
                DownloadCleanupService.FileRole.Media => unmatched,
                _ => other
            }).Add(file);
        }

        var onlyLeftovers = media.Count == 0 && unmatched.Count == 0 && other.Count == 0 && junk.Count > 0;

        if (media.Count == 0 && !onlyLeftovers)
        {
            return null;
        }

        // Subtitles and pictures may belong to a file that stays, so they go
        // only when no media is left behind.
        var junkGoes = unmatched.Count == 0 ? junk : [];
        var kept = unmatched.Concat(other).Concat(junk.Except(junkGoes)).Select(file => file.FullPath).ToList();
        var junkBytes = junkGoes.Sum(file => file.SizeBytes);
        var isFolder = !string.Equals(folder, entries[0].File.FullPath, StringComparison.OrdinalIgnoreCase);

        return new DownloadLeftover
        {
            Path = folder,
            IsFolder = isFolder,
            DriveProfileName = entries[0].Root.DriveName,
            Kind =
                media.Count == 0 ? DownloadLeftoverKind.LeftoversOnly
                : media.Any(candidate => candidate.MatchKind == DownloadMatchKind.IdenticalCopy) ? DownloadLeftoverKind.IdenticalCopy
                : DownloadLeftoverKind.SameFile,
            Media = media,
            Junk = junkGoes.Select(file => file.FullPath).ToList(),
            JunkBytes = junkBytes,
            Kept = kept,
            SizeBytes = entries.Sum(seen => seen.File.SizeBytes),
            FreedBytes = junkBytes + media.Where(candidate => candidate.MatchKind == DownloadMatchKind.IdenticalCopy).Sum(candidate => candidate.SizeBytes)
        };
    }

    // A version in downloads that is alone in its torrent folder takes the
    // folder and its leftovers with it.
    private static void DescribeTorrentFolder(CopyFile version, Dictionary<string, List<Seen>> folders, List<string> containers, TorrentLookup lookup, long minimumBytes)
    {
        if (version.DownloadNames.Count != 1)
        {
            return;
        }

        var name = version.DownloadNames[0];
        var folder = DownloadCleanupService.GroupOf(name, containers);

        if (!folders.TryGetValue(folder, out var entries) || entries.Any(seen => lookup.Find(seen.File.FullPath) is not null))
        {
            return;
        }

        var others = entries.Where(seen => !string.Equals(seen.File.FullPath, name, StringComparison.OrdinalIgnoreCase)).ToList();

        if (others.Any(seen => DownloadCleanupService.RoleOf(seen.File) != DownloadCleanupService.FileRole.Junk &&
                               !(seen.File.FileType == FileType.Video && seen.File.SizeBytes < minimumBytes)))
        {
            return;
        }

        version.TorrentFolder = folder;
        version.TorrentFolderIsFolder = !string.Equals(folder, name, StringComparison.OrdinalIgnoreCase);
        version.TorrentFolderJunk = others.Select(seen => seen.File.FullPath).ToList();
    }

    private CopyVersionGroup Group(string identity, List<CopyFile> versions, Dictionary<string, MediaAsset> assets)
    {
        var ordered = versions.OrderByDescending(version => version.SizeBytes).ToList();
        var parts = identity.Split('|');
        var isTv = parts[0] == "tv";
        var airDate = isTv && parts.Length > 3 && parts[2] == "d" ? parts[3] : null;

        // Sonarr's and Radarr's name for it when the scan has one.
        var asset =
            ordered.SelectMany(version => version.LibraryNames)
                .Select(name => assets.GetValueOrDefault(name))
                .FirstOrDefault(found => found is not null && !string.IsNullOrWhiteSpace(found.Title));

        var parsed = _parser.Parse(Path.GetFileNameWithoutExtension(ordered.First(version => version.InLibrary).Path));

        string title;

        if (asset is not null)
        {
            title = asset.Title;
        }
        else if (isTv)
        {
            title = ShowFolder(ordered.First(version => version.InLibrary).Path) ?? Titled(parsed.NormalizedTitle);
        }
        else
        {
            var folder = Path.GetFileName(Path.GetDirectoryName(ordered.First(version => version.InLibrary).Path)) ?? string.Empty;
            var folderParsed = _parser.Parse(folder);

            title = folderParsed.Year is not null && folderParsed.NormalizedTitle.Length > 0
                ? Titled(folderParsed.NormalizedTitle)
                : Titled(parsed.NormalizedTitle);
        }

        return new CopyVersionGroup
        {
            MediaType = isTv ? MediaType.TvEpisode : MediaType.Movie,
            Identity = identity,
            Title = airDate is null ? title : $"{title} - {airDate}",
            Year = isTv ? null : int.TryParse(parts[2], out var year) ? year : null,
            Season = isTv && airDate is null && int.TryParse(parts[2], out var season) ? season : null,
            Episode = isTv && airDate is null && int.TryParse(parts[3], out var episode) ? episode : null,
            Versions = ordered
        };
    }

    private static string Titled(string text) =>
        string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Length > 1 && word.All(char.IsLower) ? char.ToUpperInvariant(word[0]) + word[1..] : word));

    // "Show (2017)" from "...\Show (2017)\Season 01\episode.mkv".
    private static string? ShowFolder(string path)
    {
        var folder = Path.GetDirectoryName(path);
        var name = Path.GetFileName(folder) ?? string.Empty;

        if (name.StartsWith("season", StringComparison.OrdinalIgnoreCase) || name.StartsWith("specials", StringComparison.OrdinalIgnoreCase))
        {
            name = Path.GetFileName(Path.GetDirectoryName(folder)) ?? string.Empty;
        }

        name = Regex.Replace(name, @"\s*[\(\[]\d{4}[\)\]]\s*$", string.Empty).Trim();

        return name.Length > 0 ? name : null;
    }

    // Books, audiobooks, music and comics in the libraries, the way
    // Duplicates has always worked them out from the scan's catalog.
    private async Task<List<CopyVersionGroup>> OtherMediaVersionsAsync(List<DriveProfile> profiles)
    {
        if (_catalog is null || _duplicates is null)
        {
            return [];
        }

        var assets =
            _catalog.GetAssets()
                .Where(asset => asset.MediaType is MediaType.Book or MediaType.Audiobook or MediaType.Music or MediaType.Comic)
                .ToList();

        var groups = await _duplicates.FindDuplicatesAsync(assets, profiles);

        if (_ignored is not null)
        {
            groups = DuplicateAssetAnalysisService.RemoveIgnored(groups, _ignored);
        }

        return groups
            .Select(group =>
                new CopyVersionGroup
                {
                    MediaType = group.MediaType,
                    Identity = $"{group.MediaType}|{group.Title}|{group.Author}|{group.Year}",
                    Title = group.Title,
                    Year = group.Year,
                    Author = group.Author,
                    Versions =
                        group.Entries
                            .Select(entry =>
                                new CopyFile
                                {
                                    LibraryNames = [entry.CanonicalPath],
                                    SizeBytes = entry.SizeBytes,
                                    ModifiedUtc = File.Exists(entry.CanonicalPath) ? File.GetLastWriteTimeUtc(entry.CanonicalPath) : default,
                                    DriveName = entry.DriveProfileName,
                                    Quality = entry.Resolution is { Length: > 0 } resolution && !resolution.Equals("unknown", StringComparison.OrdinalIgnoreCase) ? resolution : string.Empty
                                })
                            .ToList()
                })
            .ToList();
    }

    /// <summary>
    /// Which movie or episode a video is: "movie|title|year[|pN]" or
    /// "tv|show|season|episode". Null when the name doesn't say clearly
    /// enough, or it is an extra (a trailer, a featurette, a sample).
    /// <paramref name="onlyBigVideoInFolder"/>: whether a name that says
    /// nothing may take its folder's name ("abc123.mkv" in "Film (2021)").
    /// </summary>
    public static string? VideoIdentity(IReleaseParser parser, string path, bool onlyBigVideoInFolder, LibraryKind kind = LibraryKind.Unknown)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var folder = Path.GetDirectoryName(path) ?? string.Empty;

        if (ExtraEnding.IsMatch(name) ||
            name.Contains("sample", StringComparison.OrdinalIgnoreCase) ||
            ExtraFolders.Contains(Path.GetFileName(folder)))
        {
            return null;
        }

        // A date in the name is a daily show's episode, never a movie's year.
        if (AirDate.Match(name) is { Success: true } date)
        {
            if (kind == LibraryKind.Movies)
            {
                return null;
            }

            var show = ShowKey(parser.Parse(name[..date.Index]).NormalizedTitle is { Length: > 0 } before ? before : name[..date.Index]);

            if (show.Length < 2 && ShowFolder(path) is { } fromFolder)
            {
                show = ShowKey(fromFolder);
            }

            return show.Length >= 2
                ? $"tv|{show}|d|{date.Groups[1].Value}-{date.Groups[2].Value}-{date.Groups[3].Value}"
                : null;
        }

        var parsed = parser.Parse(name);

        // Whatever the name says, a TV library holds episodes and a movie library movies.
        if ((kind == LibraryKind.Tv && !parsed.IsTv) || (kind == LibraryKind.Movies && parsed.IsTv))
        {
            return null;
        }

        if (parsed.IsTv)
        {
            var show = ShowKey(parsed.NormalizedTitle);

            if (show.Length == 0 && ShowFolder(path) is { } fromFolder)
            {
                show = ShowKey(parser.Parse(fromFolder).NormalizedTitle is { Length: > 0 } parsedFolder ? parsedFolder : fromFolder);
            }

            return parsed.Season is { } season && parsed.Episode is { } episode && show.Length > 0
                ? $"tv|{show}|{season}|{episode}"
                : null;
        }

        var title = MusicAnalyzer.Key(parsed.NormalizedTitle);
        var year = parsed.Year;

        if (year is null)
        {
            var folderParsed = parser.Parse(Path.GetFileName(folder));
            var folderTitle = MusicAnalyzer.Key(folderParsed.NormalizedTitle);

            if (folderParsed.IsTv || folderParsed.Year is null || folderTitle.Length < 2 ||
                !(title == folderTitle || (onlyBigVideoInFolder && !PartNumber.IsMatch(name))))
            {
                return null;
            }

            title = folderTitle;
            year = folderParsed.Year;
        }

        if (title.Length < 2)
        {
            return null;
        }

        var part = PartNumber.Match(name);

        return part.Success ? $"movie|{title}|{year}|p{int.Parse(part.Groups[1].Value)}" : $"movie|{title}|{year}";
    }

    // Shows are often named with their year in one place and not another.
    private static string ShowKey(string title)
    {
        var key = MusicAnalyzer.Key(title);
        var withoutYear = TrailingYear.Replace(key, string.Empty);

        return withoutYear.Length >= 2 ? withoutYear : key;
    }

    private string Quality(string fileName) =>
        _parser.Parse(fileName).Resolution is { Length: > 1 } resolution
            ? resolution.EndsWith('P') ? resolution[..^1] + "p" : resolution
            : string.Empty;

    // --------------------------------------------------------------- link

    public async Task<CopiesActionResult> LinkAsync(IReadOnlyCollection<CopyToLink> links)
    {
        var result = new CopiesActionResult();

        if (_planner is null || _remediation is null)
        {
            result.Error = "Linking isn't available.";
            return result;
        }

        await _gate.WaitAsync();

        try
        {
            foreach (var link in links)
            {
                var library = link.Library.LibraryNames.FirstOrDefault() ?? string.Empty;
                var download = link.Download.DownloadNames.FirstOrDefault() ?? string.Empty;
                var name = Path.GetFileName(library);

                if (await LinkProblemAsync(link, library, download) is { } problem)
                {
                    Skip(result, $"{name}: {problem}");
                    continue;
                }

                var plan =
                    await _planner.CreatePlanAsync(
                        new DuplicateCandidate
                        {
                            LibraryPath = library,
                            DownloadPath = download,
                            LibraryFileName = name,
                            DownloadFileName = Path.GetFileName(download),
                            SizeBytes = link.Library.SizeBytes,
                            LibraryHardlinkCount = 1
                        });

                if (plan is null || !plan.CanConvert)
                {
                    Skip(result, $"{name}: {plan?.Reason ?? "it can't be linked."}");
                    continue;
                }

                if (await _remediation.ExecuteAsync(plan))
                {
                    result.Done++;
                    result.FreedBytes += link.Library.SizeBytes;
                    Forget(last => last.ToLink.RemoveAll(row => string.Equals(row.Library.Path, library, StringComparison.OrdinalIgnoreCase)));
                }
                else
                {
                    Skip(result, $"{name}: linking failed (see History).");
                }
            }

            Save();

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<string?> LinkProblemAsync(CopyToLink link, string library, string download)
    {
        var libraryInfo = new FileInfo(library);
        var downloadInfo = new FileInfo(download);

        if (!libraryInfo.Exists || !downloadInfo.Exists)
        {
            return "one of the two files is gone - check again first.";
        }

        if (libraryInfo.Length != link.Library.SizeBytes || downloadInfo.Length != link.Download.SizeBytes ||
            !SameTime(libraryInfo.LastWriteTimeUtc, link.Library.ModifiedUtc) || !SameTime(downloadInfo.LastWriteTimeUtc, link.Download.ModifiedUtc))
        {
            return "it changed since the check - check again first.";
        }

        if (NativeHardlinks.GetIdentity(library) is { LinkCount: > 1 })
        {
            return "the library file has another name too, so linking would free nothing.";
        }

        // The library copy goes to Quarantine, so this is a close look
        // rather than reading every byte: many parts of both files.
        var libraryPrint = await FingerprintAsync(library, 16, CancellationToken.None);

        return libraryPrint is null || libraryPrint != await FingerprintAsync(download, 16, CancellationToken.None)
            ? "the two files aren't the same inside."
            : null;
    }

    // ------------------------------------------------------------- delete

    public async Task<CopiesActionResult> DeleteAsync(IReadOnlyCollection<DownloadLeftover> downloads)
    {
        var result = new CopiesActionResult();

        await _gate.WaitAsync();

        try
        {
            foreach (var leftover in downloads)
            {
                string? problem = null;

                foreach (var media in leftover.Media.Where(media => media.MatchKind == DownloadMatchKind.IdenticalCopy))
                {
                    if (!await FileContentComparer.AreIdenticalAsync(media.DownloadPath, media.LibraryPath))
                    {
                        problem = $"{media.FileName} isn't byte for byte the same as the library file, so the folder was left alone.";
                        break;
                    }
                }

                if (problem is not null)
                {
                    Skip(result, $"{leftover.Name}: {problem}");
                    continue;
                }

                var removed = await _downloads.RemoveLeftoversAsync([leftover]);

                if (removed.Error is { } error)
                {
                    result.Error = error;
                    break;
                }

                removed.SkipReasons.ForEach(reason => Skip(result, reason));

                if (removed.Removed > 0)
                {
                    result.Done++;
                    result.FreedBytes += leftover.FreedBytes;
                    Forget(last => last.ToDelete.RemoveAll(row => string.Equals(row.Path, leftover.Path, StringComparison.OrdinalIgnoreCase)));
                }
            }

            Save();

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------ versions

    public async Task<CopiesActionResult> RemoveVersionAsync(CopyVersionGroup group, CopyFile version)
    {
        var result = new CopiesActionResult();

        await _gate.WaitAsync();

        try
        {
            if (await VersionProblemAsync(group, version) is { } problem)
            {
                Skip(result, $"{version.FileName}: {problem}");
                return result;
            }

            var keeper =
                group.Versions
                    .Where(other => !ReferenceEquals(other, version) && other.Names.Any(File.Exists))
                    .OrderByDescending(other => other.InLibrary)
                    .First();

            var inRecycleBin = NativeHardlinks.OtherNamesOnlyInRecycleBin(version.Path, version.Names) ?? [];

            if (!version.InLibrary && version.TorrentFolder.Length > 0)
            {
                // Alone in its torrent folder: the folder and its leftovers go too.
                var removed =
                    await _downloads.RemoveLeftoversAsync(
                    [
                        new DownloadLeftover
                        {
                            Path = version.TorrentFolder,
                            IsFolder = version.TorrentFolderIsFolder,
                            DriveProfileName = version.DriveName,
                            Kind = DownloadLeftoverKind.OtherRelease,
                            Media =
                            [
                                new DownloadCleanupCandidate
                                {
                                    DownloadPath = version.DownloadNames[0],
                                    FileName = version.FileName,
                                    SizeBytes = version.SizeBytes,
                                    LibraryPath = keeper.Path,
                                    LibrarySizeBytes = keeper.SizeBytes,
                                    MatchKind = DownloadMatchKind.OtherRelease,
                                    DriveProfileName = version.DriveName,
                                    DownloadModifiedUtc = version.ModifiedUtc,
                                    LibraryModifiedUtc = File.GetLastWriteTimeUtc(keeper.Path),
                                    DownloadQuality = version.Quality,
                                    LibraryQuality = keeper.Quality
                                }
                            ],
                            Junk = version.TorrentFolderJunk,
                            SizeBytes = version.SizeBytes
                        }
                    ]);

                if (removed.Error is not null || removed.Removed == 0)
                {
                    result.Error = removed.Error;
                    removed.SkipReasons.ForEach(reason => Skip(result, reason));
                    return result;
                }
            }
            else if (await QuarantineVersionAsync(group, version) is { } failed)
            {
                Skip(result, $"{version.FileName}: {failed}");
                return result;
            }

            result.Done = 1;
            result.FreedBytes = version.SizeBytes;

            if (inRecycleBin.Count > 0)
            {
                result.Note =
                    "It also has a name in the Recycle Bin (left by an earlier swap or delete), so the space comes back once both Quarantine and the Recycle Bin are emptied.";
            }

            Forget(last =>
            {
                var stored = last.Versions.FirstOrDefault(candidate => candidate.Identity == group.Identity);

                stored?.Versions.RemoveAll(candidate => string.Equals(candidate.Path, version.Path, StringComparison.OrdinalIgnoreCase));
                last.Versions.RemoveAll(candidate => candidate.Versions.Count < 2 || !candidate.Versions.Any(other => other.InLibrary));
            });

            group.Versions.Remove(version);
            Save();

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> VersionProblemAsync(CopyVersionGroup group, CopyFile version)
    {
        if (_quarantine is null)
        {
            return "Quarantine isn't available.";
        }

        if (!group.Versions.Any(other => !ReferenceEquals(other, version) && other.Names.Any(File.Exists)))
        {
            return "it's the only version left, so it stays.";
        }

        var path = version.Path;
        var info = new FileInfo(path);

        if (!info.Exists)
        {
            return "it's already gone.";
        }

        if (info.Length != version.SizeBytes || (version.ModifiedUtc != default && !SameTime(info.LastWriteTimeUtc, version.ModifiedUtc)))
        {
            return "it changed since the check - check again first.";
        }

        var torrents = await _client.TryGetTorrentsAsync();
        var lookup = new TorrentLookup(torrents ?? []);
        var names = version.Names.Where(File.Exists).ToList();

        if (names.Select(lookup.Find).FirstOrDefault(found => found is not null) is { } torrent)
        {
            return $"{_client.Name} is seeding it (\"{torrent}\"). Remove the torrent there first.";
        }

        if (NativeHardlinks.GetIdentity(path) is { LinkCount: > 1 } identity && identity.LinkCount > names.Count)
        {
            if (torrents is null)
            {
                return $"it has another name somewhere and {_client.Name} couldn't be asked whether it's seeding.";
            }

            var check = SeedingCheck.From(torrents.Select(record => (record.ContentPath, record.Name))).Check(path);

            if (check.State == SeedState.Seeding)
            {
                return $"{_client.Name} is seeding it (\"{check.Torrent}\"). Remove the torrent there first.";
            }

            // A name left in the Recycle Bin (by a swap or a delete) uses
            // nothing; it only holds the space until the bin is emptied.
            if (NativeHardlinks.OtherNamesOnlyInRecycleBin(path, names) is null)
            {
                var elsewhere = check.OtherNames.FirstOrDefault(name => !names.Contains(name, StringComparer.OrdinalIgnoreCase) && !NativeHardlinks.IsInRecycleBin(name));

                return $"it has another name ({elsewhere ?? "one Windows won't list"}), so removing this one would free nothing.";
            }
        }

        if (version.InDownloads && torrents is null)
        {
            return $"{_client.Name} couldn't be asked whether it's seeding.";
        }

        return null;
    }

    // The first name goes to Quarantine; any other names of the same file
    // (a hardlink in downloads) are deleted, since Quarantine has the bytes.
    private async Task<string?> QuarantineVersionAsync(CopyVersionGroup group, CopyFile version)
    {
        var names = version.Names.Where(File.Exists).ToList();
        var primary = names[0];
        var identity = NativeHardlinks.GetIdentity(primary);
        var others = names.Skip(1).Where(name => NativeHardlinks.GetIdentity(name) is { } other && identity is { } first &&
                                                 other.VolumeSerialNumber == first.VolumeSerialNumber && other.FileIndex == first.FileIndex).ToList();

        var quarantined = await _quarantine!.QuarantineFileAsync(primary);

        await _history.AddRecordAsync(
            new HistoryRecord
            {
                ActionType = version.InLibrary ? HistoryActionType.DuplicateQuarantined : HistoryActionType.DownloadCopyRemoved,
                FileName = Path.GetFileName(primary),
                LibraryPath = version.InLibrary ? primary : string.Empty,
                DownloadPath = version.InLibrary ? string.Empty : primary,
                QuarantinePath = quarantined ?? string.Empty,
                SizeBytes = version.SizeBytes,
                Success = quarantined is not null,
                Message =
                    quarantined is not null
                        ? $"Another version of {group.DisplayTitle} moved to Quarantine" + (version.Quality.Length > 0 ? $" ({version.Quality})." : ".")
                        : "Couldn't move this version to Quarantine."
            });

        if (quarantined is null)
        {
            return "it couldn't be moved to Quarantine (open in another program?).";
        }

        foreach (var name in others)
        {
            try
            {
                File.SetAttributes(name, File.GetAttributes(name) & ~FileAttributes.ReadOnly);
                File.Delete(name);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"Copies: couldn't delete the other name {name}: {exception.Message}");
            }
        }

        return null;
    }

    public void Ignore(CopyVersionGroup group, CopyFile version)
    {
        _ignored?.Add(
            new IgnoredItem
            {
                SourcePath = version.Path,
                Title = group.Title,
                Category =
                    group.MediaType switch
                    {
                        MediaType.TvEpisode or MediaType.TvSeason => IgnoredCategory.Tv,
                        MediaType.Book => IgnoredCategory.Book,
                        MediaType.Audiobook => IgnoredCategory.Audiobook,
                        MediaType.Music => IgnoredCategory.Music,
                        MediaType.Comic => IgnoredCategory.Comic,
                        _ => IgnoredCategory.Movie
                    },
                Author = group.Author,
                Year = group.Year,
                Season = group.Season,
                Episode = group.Episode,
                DriveProfileName = version.DriveName,
                IgnoredUtc = DateTime.UtcNow,
                Reason = "Marked not another version"
            });

        group.Versions.Remove(version);

        Forget(last =>
        {
            var stored = last.Versions.FirstOrDefault(candidate => candidate.Identity == group.Identity);

            stored?.Versions.RemoveAll(candidate => string.Equals(candidate.Path, version.Path, StringComparison.OrdinalIgnoreCase));
            last.Versions.RemoveAll(candidate => candidate.Versions.Count < 2 || !candidate.Versions.Any(other => other.InLibrary));
        });

        Save();
    }

    // -------------------------------------------------------------- helpers

    private static void Skip(CopiesActionResult result, string reason)
    {
        result.Skipped.Add(reason);

        AppLog.Info($"Copies: left alone - {reason}");
    }

    private void Forget(Action<CopiesScanResult> change)
    {
        if (_last is not null)
        {
            change(_last);
        }
    }

    private void Save()
    {
        if (_last is not null)
        {
            JsonFileStore.Save(_cachePath, _last);
        }
    }

    private static bool SameTime(DateTime actual, DateTime recorded) =>
        (actual - recorded).Duration() <= ModifiedTolerance;

    /// <summary>
    /// A quick look inside a file: <paramref name="samples"/> pieces spread
    /// from start to end, hashed. Two files of the same size with the same
    /// fingerprint are almost certainly the same; deleting still reads every byte.
    /// </summary>
    internal static async Task<string?> FingerprintAsync(string path, int samples, CancellationToken cancellationToken)
    {
        const int SampleSize = 64 * 1024;

        try
        {
            await using var stream =
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, SampleSize, FileOptions.Asynchronous);

            var length = stream.Length;
            var buffer = new byte[SampleSize];

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            for (var index = 0; index < samples; index++)
            {
                var offset = samples == 1 || length <= SampleSize ? 0 : (length - SampleSize) * index / (samples - 1);

                stream.Position = offset;

                var read = await stream.ReadAtLeastAsync(buffer, Math.Min(SampleSize, (int)Math.Min(length, SampleSize)), throwOnEndOfStream: false, cancellationToken);

                hash.AppendData(buffer, 0, read);
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The torrent using a path, whatever state it's in.</summary>
    private sealed class TorrentLookup
    {
        private readonly Dictionary<string, string> _roots = new(StringComparer.OrdinalIgnoreCase);

        public TorrentLookup(IEnumerable<TorrentRecord> torrents)
        {
            foreach (var torrent in torrents)
            {
                var path =
                    !string.IsNullOrWhiteSpace(torrent.ContentPath) ? torrent.ContentPath
                    : !string.IsNullOrWhiteSpace(torrent.SavePath) && !string.IsNullOrWhiteSpace(torrent.Name) ? Path.Combine(torrent.SavePath, torrent.Name)
                    : string.Empty;

                try
                {
                    if (path.Length > 0)
                    {
                        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

                        _roots.TryAdd(root, torrent.Name.Length > 0 ? torrent.Name : Path.GetFileName(root));
                    }
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // Not a path any of our files can be under.
                }
            }
        }

        // The path itself or any folder above it: a few lookups per file
        // rather than one comparison per torrent.
        public string? Find(string path)
        {
            var current = Path.TrimEndingDirectorySeparator(path);

            while (!string.IsNullOrEmpty(current))
            {
                if (_roots.TryGetValue(current, out var name))
                {
                    return name;
                }

                var parent = Path.GetDirectoryName(current);

                if (parent is null || parent.Length >= current.Length)
                {
                    break;
                }

                current = Path.TrimEndingDirectorySeparator(parent);
            }

            return null;
        }
    }
}
