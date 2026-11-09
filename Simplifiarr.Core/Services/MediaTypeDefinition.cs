using System.Globalization;
using System.Text.RegularExpressions;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>
/// One column of a media type's results table.
/// </summary>
public sealed record MediaTypeColumn(
    string Header,
    Func<MediaPromotionCandidate, string> Value);

/// <summary>
/// Everything that is specific to one kind of media: which files it is,
/// which folders it lives in, how to read its details, how its library is
/// laid out, and how to tell whether the library already has it. Everything
/// else - scanning, caching, previewing, promoting, ignoring - is shared.
///
/// Adding a new kind of media means adding one of these.
/// </summary>
public abstract class MediaTypeDefinition
{
    public abstract MediaType MediaType { get; }

    public abstract IgnoredCategory IgnoredCategory { get; }

    /// <summary>"Books", "Music" - for headings.</summary>
    public abstract string PluralName { get; }

    /// <summary>"book", "track" - for sentences.</summary>
    public abstract string SingularName { get; }

    public abstract FileType FileType { get; }

    public abstract string CacheFileName { get; }

    public abstract string GetLibraryPath(DriveProfile profile);

    public abstract string GetDownloadPath(DriveProfile profile);

    public abstract MediaFileMetadata ReadMetadata(string filePath);

    public abstract LibraryTarget ResolveTarget(
        MediaFileMetadata metadata,
        string format,
        string libraryPath,
        string? originalFileName,
        bool preserveOriginalFileName);

    public abstract MediaAsset? FindInCatalog(
        IMediaCatalogService catalog,
        MediaFileMetadata metadata,
        string format);

    /// <summary>
    /// The type-specific columns shown between Title and Status.
    /// </summary>
    public abstract IReadOnlyList<MediaTypeColumn> Columns { get; }

    /// <summary>
    /// Audiobooks and music are the same kinds of file, so the catalog has
    /// to be told which one a library holds. Other types are recognised by
    /// their file extension and return null.
    /// </summary>
    public virtual MediaType? CatalogTypeOverride => null;

    public virtual string DescribeNewItem(MediaFileMetadata metadata) =>
        metadata.FromEmbeddedTags
            ? $"New {SingularName} identified from embedded metadata."
            : $"New {SingularName} identified from filename (no embedded metadata found).";

    protected static MediaTypeColumn FormatColumn { get; } =
        new("Format", candidate => candidate.Format.ToUpperInvariant());

    protected static string OrUnknown(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Unknown" : value;

    protected static string WithDot(string format) =>
        format.StartsWith('.') ? format : $".{format}";

    protected static string SanitizeForPath(string value)
    {
        var invalidChars =
            Path.GetInvalidFileNameChars();

        var sanitized =
            new string(
                value
                    .Select(ch => invalidChars.Contains(ch) ? ' ' : ch)
                    .ToArray());

        sanitized =
            string.Join(
                " ",
                sanitized.Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries));

        return string.IsNullOrWhiteSpace(sanitized)
            ? "Unknown"
            : sanitized;
    }

    protected static LibraryTarget BuildTarget(
        string targetDirectory,
        string defaultFileName,
        string? originalFileName,
        bool preserveOriginalFileName)
    {
        var targetFileName =
            preserveOriginalFileName && !string.IsNullOrWhiteSpace(originalFileName)
                ? originalFileName
                : defaultFileName;

        var targetFilePath =
            Path.Combine(targetDirectory, targetFileName);

        return new LibraryTarget
        {
            TargetDirectory = targetDirectory,
            TargetFilePath = targetFilePath,
            DirectoryExists = Directory.Exists(targetDirectory),
            FileExists = File.Exists(targetFilePath)
        };
    }

    /// <summary>
    /// Turns "some.file_name" into "some file name".
    /// </summary>
    protected static string CleanFileName(string filePath)
    {
        var name =
            Path.GetFileNameWithoutExtension(filePath);

        var cleaned =
            Regex.Replace(name, @"[._]+", " ")
                .Trim();

        return Regex.Replace(cleaned, @"\s+", " ");
    }

    /// <summary>
    /// The fallback when a file has no readable tags: treat
    /// "Author - Title" as exactly that, and anything else as just a title.
    /// </summary>
    protected static MediaFileMetadata FromAuthorDashTitle(string filePath)
    {
        var cleaned = CleanFileName(filePath);

        var separatorIndex =
            cleaned.IndexOf(" - ", StringComparison.Ordinal);

        if (separatorIndex > 0)
        {
            var author = cleaned[..separatorIndex].Trim();
            var title = cleaned[(separatorIndex + 3)..].Trim();

            if (!string.IsNullOrWhiteSpace(title))
            {
                return new MediaFileMetadata
                {
                    Title = title,
                    Author = string.IsNullOrWhiteSpace(author) ? null : author
                };
            }
        }

        return new MediaFileMetadata { Title = cleaned };
    }
}

/// <summary>
/// The media types handled by the shared pipeline. Movies and TV are not
/// here: they are matched by release name and torrent rather than by tags
/// inside the file, and have their own service.
/// </summary>
public static class MediaTypeDefinitions
{
    public static BookDefinition Book { get; } = new();

    public static AudiobookDefinition Audiobook { get; } = new();

    public static MusicDefinition Music { get; } = new();

    public static ComicDefinition Comic { get; } = new();

    public static IReadOnlyList<MediaTypeDefinition> All { get; } =
        [Book, Audiobook, Music, Comic];

    public static MediaTypeDefinition Get(MediaType mediaType) =>
        All.FirstOrDefault(definition => definition.MediaType == mediaType)
        ?? throw new ArgumentException(
            $"{mediaType} is not handled by the shared media pipeline.",
            nameof(mediaType));

    /// <summary>
    /// Which definition, if any, a file in a library belongs to.
    /// </summary>
    public static MediaTypeDefinition? ForFile(
        FileType fileType,
        MediaType? mediaTypeOverride)
    {
        return fileType switch
        {
            FileType.Book => Book,
            FileType.Comic => Comic,
            FileType.Audio when mediaTypeOverride == MediaType.Music => Music,
            FileType.Audio => Audiobook,
            _ => null
        };
    }
}

public sealed class BookDefinition : MediaTypeDefinition
{
    public override MediaType MediaType => MediaType.Book;

    public override IgnoredCategory IgnoredCategory => IgnoredCategory.Book;

    public override string PluralName => "Books";

    public override string SingularName => "book";

    public override FileType FileType => FileType.Book;

    public override string CacheFileName => "book-promotion-cache.json";

    public override string GetLibraryPath(DriveProfile profile) =>
        profile.BookLibraryPath;

    // Books have their own file extensions, so they can safely be picked
    // out of the shared download folder.
    public override string GetDownloadPath(DriveProfile profile) =>
        profile.DownloadPath;

    public override IReadOnlyList<MediaTypeColumn> Columns { get; } =
    [
        new("Author", candidate => OrUnknown(candidate.Author)),
        FormatColumn
    ];

    public override MediaFileMetadata ReadMetadata(string filePath)
    {
        var embedded =
            Path.GetExtension(filePath).ToLowerInvariant() switch
            {
                ".epub" => EpubMetadataReader.TryExtract(filePath),
                ".pdf" => PdfMetadataReader.TryExtract(filePath),
                ".mobi" or ".azw3" => MobiMetadataReader.TryExtract(filePath),
                _ => null
            };

        return embedded is null
            ? FromAuthorDashTitle(filePath)
            : new MediaFileMetadata
            {
                Title = embedded.Title,
                Author = embedded.Author,
                FromEmbeddedTags = embedded.Source == BookMetadataSource.Embedded
            };
    }

    // Library layout: Title\Title.ext
    public override LibraryTarget ResolveTarget(
        MediaFileMetadata metadata,
        string format,
        string libraryPath,
        string? originalFileName,
        bool preserveOriginalFileName)
    {
        if (string.IsNullOrWhiteSpace(libraryPath))
        {
            return new LibraryTarget();
        }

        var safeTitle = SanitizeForPath(metadata.Title);

        return BuildTarget(
            Path.Combine(libraryPath, safeTitle),
            $"{safeTitle}{WithDot(format)}",
            originalFileName,
            preserveOriginalFileName);
    }

    public override MediaAsset? FindInCatalog(
        IMediaCatalogService catalog,
        MediaFileMetadata metadata,
        string format) =>
        catalog.FindBookAsset(metadata.Title, metadata.Author, format);
}

public sealed class AudiobookDefinition : MediaTypeDefinition
{
    public override MediaType MediaType => MediaType.Audiobook;

    public override IgnoredCategory IgnoredCategory => IgnoredCategory.Audiobook;

    public override string PluralName => "Audiobooks";

    public override string SingularName => "audiobook";

    public override FileType FileType => FileType.Audio;

    public override string CacheFileName => "audiobook-promotion-cache.json";

    public override MediaType? CatalogTypeOverride => MediaType.Audiobook;

    public override string GetLibraryPath(DriveProfile profile) =>
        profile.AudiobookLibraryPath;

    // Audiobooks and music are the same kinds of file and can't be told
    // apart, so each has its own download folder.
    public override string GetDownloadPath(DriveProfile profile) =>
        profile.AudiobookDownloadPath;

    public override IReadOnlyList<MediaTypeColumn> Columns { get; } =
    [
        new("Author", candidate => OrUnknown(candidate.Author)),
        new("Narrator", candidate => OrUnknown(candidate.Narrator)),
        FormatColumn
    ];

    public override MediaFileMetadata ReadMetadata(string filePath)
    {
        var embedded =
            Path.GetExtension(filePath).ToLowerInvariant() switch
            {
                ".mp3" => Id3v2MetadataReader.TryExtract(filePath),
                ".m4a" or ".m4b" => Mp4MetadataReader.TryExtract(filePath),
                _ => null
            };

        return embedded is null
            ? FromAuthorDashTitle(filePath)
            : new MediaFileMetadata
            {
                Title = embedded.Title,
                Author = embedded.Author,
                Narrator = embedded.Narrator,
                FromEmbeddedTags = embedded.Source == AudiobookMetadataSource.Embedded
            };
    }

    // Library layout: Title\Title.ext
    public override LibraryTarget ResolveTarget(
        MediaFileMetadata metadata,
        string format,
        string libraryPath,
        string? originalFileName,
        bool preserveOriginalFileName)
    {
        if (string.IsNullOrWhiteSpace(libraryPath))
        {
            return new LibraryTarget();
        }

        var safeTitle = SanitizeForPath(metadata.Title);

        return BuildTarget(
            Path.Combine(libraryPath, safeTitle),
            $"{safeTitle}{WithDot(format)}",
            originalFileName,
            preserveOriginalFileName);
    }

    public override MediaAsset? FindInCatalog(
        IMediaCatalogService catalog,
        MediaFileMetadata metadata,
        string format) =>
        catalog.FindAudiobookAsset(metadata.Title, metadata.Author, format);
}

public sealed class MusicDefinition : MediaTypeDefinition
{
    public override MediaType MediaType => MediaType.Music;

    public override IgnoredCategory IgnoredCategory => IgnoredCategory.Music;

    public override string PluralName => "Music";

    public override string SingularName => "track";

    public override FileType FileType => FileType.Audio;

    public override string CacheFileName => "music-promotion-cache.json";

    public override MediaType? CatalogTypeOverride => MediaType.Music;

    public override string GetLibraryPath(DriveProfile profile) =>
        profile.MusicLibraryPath;

    public override string GetDownloadPath(DriveProfile profile) =>
        profile.MusicDownloadPath;

    public override IReadOnlyList<MediaTypeColumn> Columns { get; } =
    [
        new("Artist", candidate => OrUnknown(candidate.Author)),
        new("Album", candidate => OrUnknown(candidate.Album)),
        new("Track", candidate =>
            candidate.TrackNumber?.ToString("0.#", CultureInfo.InvariantCulture) ?? "-"),
        FormatColumn
    ];

    public override MediaFileMetadata ReadMetadata(string filePath)
    {
        var embedded =
            Path.GetExtension(filePath).ToLowerInvariant() switch
            {
                ".mp3" => Id3v2MetadataReader.TryExtractMusic(filePath),
                ".m4a" or ".m4b" => Mp4MetadataReader.TryExtractMusic(filePath),
                ".flac" => FlacMetadataReader.TryExtractMusic(filePath),
                _ => null
            };

        return embedded is null
            ? FromAuthorDashTitle(filePath)
            : new MediaFileMetadata
            {
                Title = embedded.Title,
                Author = embedded.Artist,
                Album = embedded.Album,
                TrackNumber = embedded.TrackNumber,
                FromEmbeddedTags = embedded.Source == MusicMetadataSource.Embedded
            };
    }

    // Library layout: Artist\Album\NN - Title.ext (skipping whichever of
    // artist and album isn't known).
    public override LibraryTarget ResolveTarget(
        MediaFileMetadata metadata,
        string format,
        string libraryPath,
        string? originalFileName,
        bool preserveOriginalFileName)
    {
        if (string.IsNullOrWhiteSpace(libraryPath))
        {
            return new LibraryTarget();
        }

        var targetDirectory = libraryPath;

        if (!string.IsNullOrWhiteSpace(metadata.Author))
        {
            targetDirectory =
                Path.Combine(targetDirectory, SanitizeForPath(metadata.Author));
        }

        if (!string.IsNullOrWhiteSpace(metadata.Album))
        {
            targetDirectory =
                Path.Combine(targetDirectory, SanitizeForPath(metadata.Album));
        }

        var trackPrefix =
            metadata.TrackNumber is > 0
                ? $"{metadata.TrackNumber:00} - "
                : string.Empty;

        return BuildTarget(
            targetDirectory,
            $"{trackPrefix}{SanitizeForPath(metadata.Title)}{WithDot(format)}",
            originalFileName,
            preserveOriginalFileName);
    }

    public override MediaAsset? FindInCatalog(
        IMediaCatalogService catalog,
        MediaFileMetadata metadata,
        string format) =>
        catalog.FindMusicAsset(metadata.Title, metadata.Author, metadata.Album, format);
}

public sealed class ComicDefinition : MediaTypeDefinition
{
    public override MediaType MediaType => MediaType.Comic;

    public override IgnoredCategory IgnoredCategory => IgnoredCategory.Comic;

    public override string PluralName => "Comics";

    public override string SingularName => "comic";

    public override FileType FileType => FileType.Comic;

    public override string CacheFileName => "comic-promotion-cache.json";

    public override string GetLibraryPath(DriveProfile profile) =>
        profile.ComicLibraryPath;

    public override string GetDownloadPath(DriveProfile profile) =>
        profile.ComicDownloadPath;

    public override IReadOnlyList<MediaTypeColumn> Columns { get; } =
    [
        new("Series", candidate => OrUnknown(candidate.Album)),
        new("Issue", candidate =>
            candidate.TrackNumber?.ToString("0.#", CultureInfo.InvariantCulture) ?? "-"),
        FormatColumn
    ];

    public override string DescribeNewItem(MediaFileMetadata metadata) =>
        metadata.FromEmbeddedTags
            ? "New comic identified from embedded ComicInfo.xml."
            : "New comic identified from filename (no embedded metadata found).";

    public override MediaFileMetadata ReadMetadata(string filePath)
    {
        // Only CBZ (a zip) is first-party readable - CBR/CB7/CBT/CBA have
        // no built-in archive support and reading them would require a
        // third-party package, so they go straight to filename parsing.
        var embedded =
            Path.GetExtension(filePath).Equals(".cbz", StringComparison.OrdinalIgnoreCase)
                ? CbzComicInfoReader.TryExtract(filePath)
                : null;

        return embedded is null
            ? FromComicFileName(filePath)
            : new MediaFileMetadata
            {
                Title = embedded.Title,
                Author = embedded.Author,
                Album = embedded.Series,
                TrackNumber = embedded.Issue,
                Year = embedded.Year,
                FromEmbeddedTags = embedded.Source == ComicMetadataSource.Embedded
            };
    }

    // Library layout: Series\Series #001.ext, or just Title.ext when the
    // series isn't known.
    public override LibraryTarget ResolveTarget(
        MediaFileMetadata metadata,
        string format,
        string libraryPath,
        string? originalFileName,
        bool preserveOriginalFileName)
    {
        if (string.IsNullOrWhiteSpace(libraryPath))
        {
            return new LibraryTarget();
        }

        var hasSeries = !string.IsNullOrWhiteSpace(metadata.Album);
        var safeSeries = hasSeries ? SanitizeForPath(metadata.Album!) : null;
        var extension = WithDot(format);

        return BuildTarget(
            hasSeries
                ? Path.Combine(libraryPath, safeSeries!)
                : libraryPath,
            hasSeries && metadata.TrackNumber.HasValue
                ? $"{safeSeries} #{FormatIssue(metadata.TrackNumber.Value)}{extension}"
                : $"{SanitizeForPath(metadata.Title)}{extension}",
            originalFileName,
            preserveOriginalFileName);
    }

    public override MediaAsset? FindInCatalog(
        IMediaCatalogService catalog,
        MediaFileMetadata metadata,
        string format) =>
        catalog.FindComicAsset(metadata.Title, metadata.Album, metadata.TrackNumber, format);

    /// <summary>
    /// Zero-pads a comic issue number to 3 digits, keeping fractional
    /// annuals (e.g. "#0.5") readable - "1" -> "001", "0.5" -> "000.5",
    /// "12.1" -> "012.1". Whole-number issues never show a decimal point.
    /// </summary>
    private static string FormatIssue(double issue)
    {
        return issue == Math.Floor(issue)
            ? issue.ToString("000", CultureInfo.InvariantCulture)
            : issue.ToString("000.0#", CultureInfo.InvariantCulture);
    }

    private static MediaFileMetadata FromComicFileName(string filePath)
    {
        var cleaned = CleanFileName(filePath);

        var yearMatch =
            Regex.Match(cleaned, @"\((19\d{2}|20\d{2})\)");

        int? year =
            yearMatch.Success
                ? int.Parse(yearMatch.Groups[1].Value, CultureInfo.InvariantCulture)
                : null;

        var issueMatch =
            Regex.Match(cleaned, @"#\s*(?<issue>\d+(?:\.\d+)?)");

        var cutMatch =
            issueMatch.Success
                ? issueMatch
                : Regex.Match(
                    cleaned,
                    @"\bVol(?:ume)?\.?\s*(?<issue>\d+)\b",
                    RegexOptions.IgnoreCase);

        if (!cutMatch.Success)
        {
            return new MediaFileMetadata
            {
                Title = cleaned,
                Year = year
            };
        }

        var series =
            cleaned[..cutMatch.Index]
                .TrimEnd('-', '#', ' ')
                .Trim();

        double? issue =
            double.TryParse(
                cutMatch.Groups["issue"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsedIssue)
                    ? parsedIssue
                    : null;

        return new MediaFileMetadata
        {
            Title = string.IsNullOrWhiteSpace(series) ? cleaned : series,
            Album = string.IsNullOrWhiteSpace(series) ? null : series,
            TrackNumber = issue,
            Year = year
        };
    }
}
