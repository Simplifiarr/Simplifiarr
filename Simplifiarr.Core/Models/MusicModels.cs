namespace Simplifiarr.Core.Models;

/// <summary>The tags a track carries that Music cleanup cares about.</summary>
public sealed class MusicTags
{
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Album { get; set; }
    public int? Track { get; set; }
    public int? Disc { get; set; }
    public string? Year { get; set; }

    /// <summary>Why the file couldn't be read as what its extension says; null when fine.</summary>
    public string? Problem { get; set; }
}

/// <summary>One audio file in a music library.</summary>
public sealed class MusicTrack
{
    public string Path { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public MusicTags Tags { get; set; } = new();

    /// <summary>The music library folder it is in.</summary>
    public string LibraryRoot { get; set; } = string.Empty;

    public bool IsLossless => MusicFormats.IsLossless(Extension);
}

public enum MusicIssueKind
{
    Folder,
    Duplicate,
    Tags,
    Junk,
    Broken
}

/// <summary>A file that moves to tidy a folder.</summary>
public sealed record MusicMove(string From, string To);

/// <summary>One copy of an album that exists more than once.</summary>
public sealed record MusicCopy(
    string Folder,
    string Formats,
    int Tracks,
    long Bytes,
    bool Suggested,
    IReadOnlyList<string> Paths);

/// <summary>Something Music cleanup found, and what it would do about it.</summary>
public sealed class MusicIssue
{
    public MusicIssueKind Kind { get; init; }

    /// <summary>Stays the same between scans, for ignoring and keeping a selection.</summary>
    public string Key { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string Folder { get; init; } = string.Empty;

    /// <summary>The files it is about (the ones Quarantine would take, for junk and broken files).</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    public long Bytes { get; init; }

    /// <summary>For folder problems: where each file would go.</summary>
    public IReadOnlyList<MusicMove> Moves { get; init; } = [];

    /// <summary>For an album found more than once: each copy.</summary>
    public IReadOnlyList<MusicCopy> Copies { get; init; } = [];

    public bool CanTidy => Moves.Count > 0;
    public bool CanQuarantine => Kind is MusicIssueKind.Junk or MusicIssueKind.Broken && Paths.Count > 0;
}

public sealed class MusicScanResult
{
    public DateTime ScannedUtc { get; set; }
    public int Tracks { get; set; }
    public int Albums { get; set; }
    public long Bytes { get; set; }
    public Dictionary<string, int> Formats { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<MusicIssue> Issues { get; set; } = [];
    public List<MusicTrack> AllTracks { get; set; } = [];
    public List<string> Problems { get; set; } = [];
}

public enum MusicConvertFormat
{
    Mp3Cbr320,
    Mp3V0,
    Aac256,
    Opus160,
    Flac
}

public enum MusicConvertMode
{
    /// <summary>The converted file takes the original's place; the original goes to Quarantine.</summary>
    Replace,

    /// <summary>The converted file is written next to the original, which stays.</summary>
    NextToOriginal,

    /// <summary>The converted file is written under another folder, in the same Artist\Album layout.</summary>
    OtherFolder
}

public sealed record MusicConvertRequest(
    string SourcePath,
    string LibraryRoot,
    MusicConvertFormat Format,
    MusicConvertMode Mode,
    string? OutputRoot);

public sealed record MusicConvertResult(
    MusicConvertRequest Request,
    bool Succeeded,
    string Message,
    long SavedBytes);

public static class MusicFormats
{
    public static readonly HashSet<string> Audio =
        new([".flac", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".aiff", ".aif", ".ape", ".wv", ".wma", ".alac", ".dsf", ".dff", ".mka"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> Lossless =
        new([".flac", ".wav", ".aiff", ".aif", ".ape", ".wv", ".alac", ".dsf", ".dff"], StringComparer.OrdinalIgnoreCase);

    public static bool IsAudio(string extension) => Audio.Contains(extension);

    public static bool IsLossless(string extension) => Lossless.Contains(extension);

    public static string Extension(MusicConvertFormat format) =>
        format switch
        {
            MusicConvertFormat.Mp3Cbr320 or MusicConvertFormat.Mp3V0 => ".mp3",
            MusicConvertFormat.Aac256 => ".m4a",
            MusicConvertFormat.Opus160 => ".opus",
            _ => ".flac"
        };

    public static string Label(MusicConvertFormat format) =>
        format switch
        {
            MusicConvertFormat.Mp3Cbr320 => "MP3 320 kbps",
            MusicConvertFormat.Mp3V0 => "MP3 V0 (about 245 kbps)",
            MusicConvertFormat.Aac256 => "AAC 256 kbps (.m4a)",
            MusicConvertFormat.Opus160 => "Opus 160 kbps",
            _ => "FLAC (lossless)"
        };

    public static string Label(MusicConvertMode mode) =>
        mode switch
        {
            MusicConvertMode.Replace => "Replace the original (it goes to Quarantine)",
            MusicConvertMode.NextToOriginal => "Keep the original, save the new file next to it",
            _ => "Keep the original, save the new file in another folder"
        };

    /// <summary>
    /// Whether a file can be converted to that format. Lossy files are never
    /// made lossless (it would only make them bigger), and a file isn't
    /// converted to the format it already is.
    /// </summary>
    public static bool CanConvert(string extension, MusicConvertFormat format) =>
        IsAudio(extension) &&
        !string.Equals(extension, Extension(format), StringComparison.OrdinalIgnoreCase) &&
        (format != MusicConvertFormat.Flac || IsLossless(extension));
}
