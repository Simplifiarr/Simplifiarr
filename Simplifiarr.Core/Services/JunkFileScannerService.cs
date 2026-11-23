using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public class JunkFileScannerService
    : IJunkFileScannerService
{
    // .nfo is deliberately absent: Jellyfin/Plex/Kodi read it for real
    // metadata (tvshow.nfo, movie.nfo, ...), so it's never treated as junk.
    private static readonly string[] JunkMetadataExtensions =
    [
        ".sfv", ".md5", ".torrent", ".url", ".txt", ".json", ".xml"
    ];

    private static readonly string[] ImageExtensions =
    [
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".ico", ".tbn"
    ];

    private static readonly string[] ArtworkNames =
    [
        "folder", "poster", "backdrop", "landscape", "banner", "logo",
        "clearlogo", "disc", "thumb", "background", "fanart", "cover"
    ];

    private readonly DriveCandidateCache<JunkFileCandidate> _cache =
        new(
            "junk-file-cache.json",
            candidate => candidate.FilePath,
            candidate =>
                !string.IsNullOrWhiteSpace(candidate.FilePath) &&
                File.Exists(candidate.FilePath));

    private readonly ISettingsService _settingsService;
    private readonly IFileInventoryService _fileInventory;

    public JunkFileScannerService(
        ISettingsService settingsService,
        IFileInventoryService fileInventory)
    {
        _settingsService = settingsService;
        _fileInventory = fileInventory;

        _ = LoadRulesAsync();
    }

    private async Task LoadRulesAsync()
    {
        try
        {
            _rules ??= (await _settingsService.GetSettingsAsync()).Junk ?? new JunkRules();
        }
        catch (Exception exception)
        {
            AppLog.Warn("Couldn't read the junk rules; showing the last scan's results as they were.", exception);
        }
    }

    // The rules the last scan used, or that were just saved. Results are
    // checked against them again, so turning a rule off hides its files
    // straight away (turning one on needs a scan to find new ones).
    private JunkRules? _rules;

    public IReadOnlyList<JunkFileCandidate> GetCandidates(
        string driveName)
    {
        var rules = _rules;

        return _cache.GetCandidates(driveName)
            .Select(candidate =>
                rules is null
                    ? candidate
                    : Classify(RecordFor(candidate), isBookLibrary: false, rules) is { } reason
                        ? WithReason(candidate, reason)
                        : null)
            .OfType<JunkFileCandidate>()
            .ToList();
    }

    public void UseRules(
        JunkRules rules) =>
        _rules = rules;

    public DateTime? GetLastScanUtc(
        string driveName) =>
        _cache.GetLastScanUtc(driveName);

    public async Task<List<JunkFileCandidate>> ScanAsync(
        string driveName)
    {
        var settings =
            await _settingsService.GetSettingsAsync();

        var rules =
            settings.Junk ?? new JunkRules();

        _rules = rules;

        var profile =
            settings.DriveProfiles.FirstOrDefault(profile =>
                profile.Enabled &&
                string.Equals(
                    profile.Name,
                    driveName,
                    StringComparison.OrdinalIgnoreCase));

        if (profile is null)
        {
            return [];
        }

        var libraryPaths =
            new[]
            {
                (Path: profile.MovieLibraryPath, IsBookLibrary: false),
                (Path: profile.TvLibraryPath, IsBookLibrary: false),
                (Path: profile.BookLibraryPath, IsBookLibrary: true),
                (Path: profile.AudiobookLibraryPath, IsBookLibrary: true),
                (Path: profile.MusicLibraryPath, IsBookLibrary: false),
                (Path: profile.ComicLibraryPath, IsBookLibrary: true)
            }
            .Where(entry =>
                !string.IsNullOrWhiteSpace(entry.Path) &&
                Directory.Exists(entry.Path))
            .DistinctBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase);

        var candidates =
            new List<JunkFileCandidate>();

        // The promise of this feature is that downloads are never touched,
        // because a torrent may still be seeding them. If a library folder
        // is set so wide that it contains a downloads folder, that promise
        // still has to hold - so anything inside one is skipped.
        var downloadFolders =
            new[]
            {
                profile.SeedOnlyPath
            }
            .Concat(profile.AllDownloadPaths())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();

        var seenPaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (libraryPath, isBookLibrary) in libraryPaths)
        {
            var files =
                await _fileInventory.GetInventoryAsync(
                    libraryPath,
                    forceRefresh: true);

            foreach (var file in files)
            {
                if (downloadFolders.Any(folder =>
                        PathContainment.IsPathUnderRoot(file.FullPath, folder)))
                {
                    continue;
                }

                // One library folder inside another would list a file twice.
                if (!seenPaths.Add(file.FullPath))
                {
                    continue;
                }

                var reason =
                    Classify(file, isBookLibrary, rules);

                if (reason is null)
                {
                    continue;
                }

                candidates.Add(
                    new JunkFileCandidate
                    {
                        FilePath = file.FullPath,
                        FileName = file.FileName,
                        SizeBytes = file.SizeBytes,
                        Reason = reason,
                        DriveProfileName = profile.Name
                    });
            }
        }

        _cache.SetCandidates(profile.Name, candidates);

        return candidates;
    }

    public void RemoveCandidate(
        string filePath) =>
        _cache.RemoveCandidate(filePath);

    private static FileRecord RecordFor(
        JunkFileCandidate candidate) =>
        new()
        {
            FullPath = candidate.FilePath,
            FileName = candidate.FileName,
            Extension = Path.GetExtension(candidate.FileName),
            FileType = FileScannerService.FileTypeOf(Path.GetExtension(candidate.FileName)),
            SizeBytes = candidate.SizeBytes
        };

    private static JunkFileCandidate WithReason(
        JunkFileCandidate candidate,
        string reason)
    {
        candidate.Reason = reason;
        return candidate;
    }

    /// <summary>Why <paramref name="file"/> is junk under <paramref name="rules"/>, or null when it isn't.</summary>
    public static string? Classify(
        FileRecord file,
        bool isBookLibrary,
        JunkRules rules)
    {
        // Media is never junk, whatever a rule says, except a sample clip.
        var isMedia = file.FileType is FileType.Video or FileType.Audio or FileType.Book or FileType.Comic or FileType.Subtitle;

        if (rules.KeepNames.Any(pattern => NameMatches(file.FileName, pattern)))
        {
            return null;
        }

        if (rules.SampleVideos &&
            file.FileType == FileType.Video &&
            SampleFileDetector.IsSample(file))
        {
            return "Sample video file";
        }

        // Thumbnails, .nfo and other files left behind in a release's
        // Sample folder describe the sample clip, not the real media - even
        // ones that would otherwise look like artwork (sample-thumb.jpg) or
        // be skipped (.nfo, .nfo-orig).
        if (rules.SampleFolderLeftovers &&
            SampleFileDetector.IsInSampleFolder(file))
        {
            return "Leftover file in a Sample folder";
        }

        if (isMedia)
        {
            return null;
        }

        var extension =
            Path.GetExtension(file.FileName).ToLowerInvariant();

        if (extension == ".metathumb")
        {
            return null;
        }

        if (rules.JunkNames.Any(pattern => NameMatches(file.FileName, pattern)))
        {
            return "Name you marked as junk";
        }

        // Book libraries (Calibre, Audiobookshelf, etc.) use .json/.xml
        // for their own library metadata (e.g. metadata_db_prefs_backup.json),
        // not scene-release junk.
        if (rules.Extensions.Any(ext => string.Equals(JunkRules.NormalizeExtension(ext), extension, StringComparison.Ordinal)) &&
            !(isBookLibrary && extension is ".json" or ".xml"))
        {
            return $"Junk file type ({extension})";
        }

        if (rules.UnrecognizedImages && ImageExtensions.Contains(extension))
        {
            var nameWithoutExtension =
                Path.GetFileNameWithoutExtension(file.FileName);

            var isKnownArtwork =
                ArtworkNames.Any(name =>
                    string.Equals(
                        name,
                        nameWithoutExtension,
                        StringComparison.OrdinalIgnoreCase) ||
                    nameWithoutExtension.EndsWith(
                        $"-{name}",
                        StringComparison.OrdinalIgnoreCase));

            return isKnownArtwork
                ? null
                : "Unrecognized image (not standard artwork naming)";
        }

        return null;
    }

    /// <summary>A file name against a pattern where * is any text and ? any one character.</summary>
    public static bool NameMatches(
        string fileName,
        string pattern)
    {
        pattern = pattern.Trim();

        if (pattern.Length == 0)
        {
            return false;
        }

        var regex =
            "^" +
            System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace(@"\*", ".*")
                .Replace(@"\?", ".") +
            "$";

        return System.Text.RegularExpressions.Regex.IsMatch(
            fileName,
            regex,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
    }
}