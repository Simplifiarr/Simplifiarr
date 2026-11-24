using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Utilities;

/// <summary>
/// Reads a track's tags. FLAC and MP3 are read directly and only as far as
/// the tags go, so a library of thousands of tracks reads quickly; other
/// formats (M4A, Ogg, Opus, WMA...) are asked of ffprobe.
/// </summary>
public static class MusicTagReader
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    // Smaller than this can't hold a second of music.
    private const long SmallestTrackBytes = 16 * 1024;

    public static MusicTags Read(string path)
    {
        try
        {
            var size = new FileInfo(path).Length;

            if (size == 0)
            {
                return new MusicTags { Problem = "The file is empty" };
            }

            if (size < SmallestTrackBytes)
            {
                return new MusicTags { Problem = $"Only {size:N0} bytes, too small to be a track" };
            }

            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".flac" => ReadFlac(path),
                ".mp3" => ReadMp3(path),
                _ => ReadWithFfprobe(path)
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new MusicTags { Problem = $"Couldn't be read: {exception.Message}" };
        }
    }

    // ------------------------------------------------------------------ FLAC

    private static MusicTags ReadFlac(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);

        var header = new byte[4];

        if (stream.Read(header, 0, 4) < 4 || Encoding.ASCII.GetString(header) != "fLaC")
        {
            return new MusicTags { Problem = "Doesn't start like a FLAC file" };
        }

        // Metadata blocks follow one another; pictures are skipped, not read.
        while (true)
        {
            var block = new byte[4];

            if (stream.Read(block, 0, 4) < 4)
            {
                return new MusicTags();
            }

            var isLast = (block[0] & 0x80) != 0;
            var type = block[0] & 0x7F;
            var length = (block[1] << 16) | (block[2] << 8) | block[3];

            if (type == 4)
            {
                var data = new byte[length];

                if (stream.Read(data, 0, length) < length)
                {
                    return new MusicTags { Problem = "The tags are cut short" };
                }

                return FromVorbis(ParseVorbis(data));
            }

            if (isLast || stream.Position + length > stream.Length)
            {
                return new MusicTags();
            }

            stream.Seek(length, SeekOrigin.Current);
        }
    }

    private static Dictionary<string, string> ParseVorbis(byte[] data)
    {
        var comments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var cursor = 0;
            var vendor = BitConverter.ToInt32(data, cursor);
            cursor += 4 + vendor;

            var count = BitConverter.ToInt32(data, cursor);
            cursor += 4;

            for (var i = 0; i < count && cursor + 4 <= data.Length; i++)
            {
                var length = BitConverter.ToInt32(data, cursor);
                cursor += 4;

                if (length < 0 || cursor + length > data.Length)
                {
                    break;
                }

                var entry = Encoding.UTF8.GetString(data, cursor, length);
                cursor += length;

                var equals = entry.IndexOf('=');

                if (equals > 0)
                {
                    comments.TryAdd(entry[..equals], entry[(equals + 1)..]);
                }
            }
        }
        catch (ArgumentException)
        {
            // A damaged block: keep what was read.
        }

        return comments;
    }

    private static MusicTags FromVorbis(Dictionary<string, string> tags) =>
        new()
        {
            Title = Clean(tags.GetValueOrDefault("TITLE")),
            Artist = Clean(tags.GetValueOrDefault("ARTIST")),
            AlbumArtist = Clean(tags.GetValueOrDefault("ALBUMARTIST") ?? tags.GetValueOrDefault("ALBUM ARTIST")),
            Album = Clean(tags.GetValueOrDefault("ALBUM")),
            Track = Number(tags.GetValueOrDefault("TRACKNUMBER")),
            Disc = Number(tags.GetValueOrDefault("DISCNUMBER")),
            Year = Year(tags.GetValueOrDefault("DATE") ?? tags.GetValueOrDefault("YEAR"))
        };

    // ------------------------------------------------------------------- MP3

    private static MusicTags ReadMp3(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);

        var header = new byte[10];

        if (stream.Read(header, 0, 10) < 10)
        {
            return new MusicTags { Problem = "Too short to be an MP3" };
        }

        if (header[0] != 'I' || header[1] != 'D' || header[2] != '3')
        {
            // No ID3v2 tag: fine if it at least starts with MPEG audio.
            return header[0] == 0xFF && (header[1] & 0xE0) == 0xE0
                ? new MusicTags()
                : new MusicTags { Problem = "Doesn't start like an MP3 file" };
        }

        var version = header[3];
        var size = Synchsafe(header, 6);

        if (size <= 0 || size > 64 * 1024 * 1024)
        {
            return new MusicTags { Problem = "The tags are damaged" };
        }

        var data = new byte[size];
        var read = stream.Read(data, 0, size);

        var frames = new Dictionary<string, string>(StringComparer.Ordinal);
        var cursor = 0;
        var idLength = version == 2 ? 3 : 4;
        var headerLength = version == 2 ? 6 : 10;

        // An extended header comes first when flagged.
        if (version >= 3 && (header[5] & 0x40) != 0 && read >= 4)
        {
            cursor += version == 4 ? Synchsafe(data, 0) : BigEndian(data, 0, 4) + 4;
        }

        while (cursor + headerLength <= read)
        {
            var id = Encoding.ASCII.GetString(data, cursor, idLength);

            if (id[0] == '\0')
            {
                break;
            }

            var length =
                version switch
                {
                    2 => BigEndian(data, cursor + 3, 3),
                    4 => Synchsafe(data, cursor + 4),
                    _ => BigEndian(data, cursor + 4, 4)
                };

            var start = cursor + headerLength;

            if (length <= 0 || start + length > read)
            {
                break;
            }

            if (id[0] == 'T')
            {
                frames.TryAdd(Id3v22To23(id), Text(data, start, length));
            }

            cursor = start + length;
        }

        return new MusicTags
        {
            Title = Clean(frames.GetValueOrDefault("TIT2")),
            Artist = Clean(frames.GetValueOrDefault("TPE1")),
            AlbumArtist = Clean(frames.GetValueOrDefault("TPE2")),
            Album = Clean(frames.GetValueOrDefault("TALB")),
            Track = Number(frames.GetValueOrDefault("TRCK")),
            Disc = Number(frames.GetValueOrDefault("TPOS")),
            Year = Year(frames.GetValueOrDefault("TDRC") ?? frames.GetValueOrDefault("TYER"))
        };
    }

    private static string Id3v22To23(string id) =>
        id switch
        {
            "TT2" => "TIT2",
            "TP1" => "TPE1",
            "TP2" => "TPE2",
            "TAL" => "TALB",
            "TRK" => "TRCK",
            "TPA" => "TPOS",
            "TYE" => "TYER",
            _ => id
        };

    private static string Text(byte[] data, int start, int length)
    {
        var body = data.AsSpan(start + 1, length - 1);
        string text;

        switch (data[start])
        {
            case 1:
                var withoutBom = StripBom(body, out var bigEndian);
                text = bigEndian ? Encoding.BigEndianUnicode.GetString(withoutBom) : Encoding.Unicode.GetString(withoutBom);
                break;
            case 2:
                text = Encoding.BigEndianUnicode.GetString(body);
                break;
            case 3:
                text = Encoding.UTF8.GetString(body);
                break;
            default:
                text = Encoding.Latin1.GetString(body);
                break;
        }

        // Several values are separated by nulls; the first is enough here.
        var end = text.IndexOf('\0');

        return end >= 0 ? text[..end] : text;
    }

    private static ReadOnlySpan<byte> StripBom(ReadOnlySpan<byte> body, out bool bigEndian)
    {
        bigEndian = body.Length >= 2 && body[0] == 0xFE && body[1] == 0xFF;

        return body.Length >= 2 && (bigEndian || (body[0] == 0xFF && body[1] == 0xFE)) ? body[2..] : body;
    }

    private static int Synchsafe(byte[] data, int at) =>
        (data[at] << 21) | (data[at + 1] << 14) | (data[at + 2] << 7) | data[at + 3];

    private static int BigEndian(byte[] data, int at, int count)
    {
        var value = 0;

        for (var i = 0; i < count; i++)
        {
            value = (value << 8) | data[at + i];
        }

        return value;
    }

    // ----------------------------------------------------------- Everything else

    private static MusicTags ReadWithFfprobe(string path)
    {
        if (FfprobeLocator.Find() is not { } ffprobe)
        {
            return new MusicTags();
        }

        try
        {
            using var process =
                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName = ffprobe,
                        ArgumentList = { "-v", "error", "-show_entries", "format_tags:stream_tags", "-of", "json", path },
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });

            if (process is null)
            {
                return new MusicTags();
            }

            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(ProbeTimeout))
            {
                process.Kill(entireProcessTree: true);

                return new MusicTags();
            }

            if (process.ExitCode != 0)
            {
                var error = errors.Result.Trim();

                return new MusicTags { Problem = error.Length > 0 ? $"ffprobe can't read it: {FirstLine(error)}" : "ffprobe can't read it" };
            }

            using var json = JsonDocument.Parse(output.Result);

            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            void Collect(JsonElement element)
            {
                if (element.TryGetProperty("tags", out var found) && found.ValueKind == JsonValueKind.Object)
                {
                    foreach (var tag in found.EnumerateObject())
                    {
                        tags.TryAdd(tag.Name, tag.Value.ToString());
                    }
                }
            }

            if (json.RootElement.TryGetProperty("format", out var format))
            {
                Collect(format);
            }

            // Ogg and Opus keep their tags on the audio stream.
            if (json.RootElement.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    Collect(stream);
                }
            }

            return new MusicTags
            {
                Title = Clean(tags.GetValueOrDefault("title")),
                Artist = Clean(tags.GetValueOrDefault("artist")),
                AlbumArtist = Clean(tags.GetValueOrDefault("album_artist") ?? tags.GetValueOrDefault("albumartist")),
                Album = Clean(tags.GetValueOrDefault("album")),
                Track = Number(tags.GetValueOrDefault("track")),
                Disc = Number(tags.GetValueOrDefault("disc")),
                Year = Year(tags.GetValueOrDefault("date") ?? tags.GetValueOrDefault("year"))
            };
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or JsonException)
        {
            return new MusicTags();
        }
    }

    private static string FirstLine(string text) =>
        text.Split('\n')[0].Trim();

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // "5", "05", "5/12"
    private static int? Number(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return int.TryParse(value.Split('/')[0].Trim(), out var number) && number > 0 ? number : null;
    }

    // "2019", "2019-03-01"
    private static string? Year(string? value) =>
        value is { Length: >= 4 } && int.TryParse(value[..4], out var year) && year is > 1800 and < 2200
            ? value[..4]
            : null;
}
