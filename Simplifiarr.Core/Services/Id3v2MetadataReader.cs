using System.Buffers.Binary;
using System.Text;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Reads tag frames from an ID3v2 tag (v2.2/v2.3/v2.4) at the start of an
/// MP3 file - shared by the Audiobook and Music metadata extractors, since
/// both just want different frames out of the same underlying tag. Frame
/// ID/size shape differs by version: v2.2 uses 3-char frame IDs with
/// plain (non-synchsafe) 3-byte sizes and no flags; v2.3 uses 4-char IDs
/// with plain 4-byte sizes; v2.4 uses 4-char IDs with synchsafe 4-byte
/// sizes. The tag's own header size (and, in v2.4 only, the extended
/// header size) is always synchsafe regardless of frame-size rules - a
/// common source of bugs. Text frames carry a leading encoding byte
/// (0=Latin-1, 1=UTF-16 w/BOM, 2=UTF-16BE w/o BOM, 3=UTF-8) before the
/// string data.
/// </summary>
public static class Id3v2MetadataReader
{
    public static AudiobookMetadata? TryExtract(
        string filePath)
    {
        var frames =
            TryReadFrames(filePath, out var narrator);

        if (frames is null ||
            !frames.TryGetValue("TIT2", out var title) ||
            string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        frames.TryGetValue("TPE1", out var author);

        // TPE2 (album artist) is a common repurposed slot for narrator
        // when no TXXX "narrator" frame is present.
        var resolvedNarrator =
            narrator ?? frames.GetValueOrDefault("TPE2");

        return new AudiobookMetadata
        {
            Title = title.Trim(),
            Author =
                string.IsNullOrWhiteSpace(author)
                    ? null
                    : author.Trim(),
            Narrator =
                string.IsNullOrWhiteSpace(resolvedNarrator)
                    ? null
                    : resolvedNarrator.Trim(),
            Source = AudiobookMetadataSource.Embedded
        };
    }

    public static MusicMetadata? TryExtractMusic(
        string filePath)
    {
        var frames =
            TryReadFrames(filePath, out _);

        if (frames is null ||
            !frames.TryGetValue("TIT2", out var title) ||
            string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        frames.TryGetValue("TPE1", out var artist);
        frames.TryGetValue("TALB", out var album);
        frames.TryGetValue("TRCK", out var trackRaw);

        return new MusicMetadata
        {
            Title = title.Trim(),
            Artist =
                string.IsNullOrWhiteSpace(artist)
                    ? null
                    : artist.Trim(),
            Album =
                string.IsNullOrWhiteSpace(album)
                    ? null
                    : album.Trim(),
            TrackNumber = ParseTrackNumber(trackRaw),
            Source = MusicMetadataSource.Embedded
        };
    }

    private static double? ParseTrackNumber(
        string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // TRCK is often "N" or "N/total" (e.g. "5/12").
        var slice =
            raw.Split('/')[0].Trim();

        return int.TryParse(slice, out var trackNumber)
            ? trackNumber
            : null;
    }

    private static Dictionary<string, string>? TryReadFrames(
        string filePath,
        out string? narrator)
    {
        narrator = null;

        try
        {
            var bytes =
                File.ReadAllBytes(filePath);

            if (bytes.Length < 10 ||
                bytes[0] != (byte)'I' ||
                bytes[1] != (byte)'D' ||
                bytes[2] != (byte)'3')
            {
                return null;
            }

            var majorVersion =
                bytes[3];

            var flags =
                bytes[5];

            var tagSize =
                ReadSynchsafeInt(
                    bytes.AsSpan(6, 4));

            if (tagSize <= 0 || 10 + tagSize > bytes.Length)
            {
                return null;
            }

            var cursor = 10;
            var tagEnd = 10 + tagSize;

            if (majorVersion >= 3 &&
                (flags & 0x40) != 0 &&
                cursor + 4 <= tagEnd)
            {
                var declaredSize =
                    majorVersion == 4
                        ? ReadSynchsafeInt(bytes.AsSpan(cursor, 4))
                        : (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(cursor, 4));

                // v2.4's extended-header size includes the 4-byte size
                // field itself; v2.3's does not.
                cursor +=
                    majorVersion == 4
                        ? declaredSize
                        : declaredSize + 4;

                if (cursor > tagEnd || cursor < 10)
                {
                    return null;
                }
            }

            var frames =
                new Dictionary<string, string>(StringComparer.Ordinal);

            string? foundNarrator = null;

            while (cursor < tagEnd)
            {
                if (majorVersion == 2)
                {
                    if (cursor + 6 > tagEnd)
                    {
                        break;
                    }

                    var frameId =
                        Encoding.ASCII.GetString(bytes, cursor, 3);

                    if (frameId[0] == '\0')
                    {
                        break;
                    }

                    var frameSize =
                        (bytes[cursor + 3] << 16) |
                        (bytes[cursor + 4] << 8) |
                        bytes[cursor + 5];

                    var dataStart = cursor + 6;

                    if (frameSize < 0 || dataStart + frameSize > tagEnd)
                    {
                        break;
                    }

                    ApplyFrame(
                        MapShortFrameId(frameId),
                        bytes.AsSpan(dataStart, frameSize),
                        frames,
                        ref foundNarrator);

                    cursor = dataStart + frameSize;
                }
                else
                {
                    if (cursor + 10 > tagEnd)
                    {
                        break;
                    }

                    var frameId =
                        Encoding.ASCII.GetString(bytes, cursor, 4);

                    if (frameId[0] == '\0')
                    {
                        break;
                    }

                    var frameSize =
                        majorVersion == 4
                            ? ReadSynchsafeInt(bytes.AsSpan(cursor + 4, 4))
                            : (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(cursor + 4, 4));

                    var dataStart = cursor + 10;

                    if (frameSize < 0 || dataStart + frameSize > tagEnd)
                    {
                        break;
                    }

                    ApplyFrame(
                        frameId,
                        bytes.AsSpan(dataStart, frameSize),
                        frames,
                        ref foundNarrator);

                    cursor = dataStart + frameSize;
                }
            }

            narrator = foundNarrator;

            return frames;
        }
        catch
        {
            return null;
        }
    }

    private static string MapShortFrameId(
        string shortId)
    {
        return shortId switch
        {
            "TT2" => "TIT2",
            "TP1" => "TPE1",
            "TP2" => "TPE2",
            "TAL" => "TALB",
            "TRK" => "TRCK",
            "TXX" => "TXXX",
            _ => shortId
        };
    }

    private static void ApplyFrame(
        string frameId,
        ReadOnlySpan<byte> data,
        Dictionary<string, string> frames,
        ref string? narrator)
    {
        if (data.Length == 0)
        {
            return;
        }

        var decoded =
            DecodeEncodedString(data);

        if (decoded is null)
        {
            return;
        }

        if (frameId == "TXXX")
        {
            var parts = decoded.Split('\0', 2);

            if (parts.Length == 2 &&
                parts[0].Contains("narrat", StringComparison.OrdinalIgnoreCase))
            {
                var value = FirstSegment(parts[1]);

                if (value is not null)
                {
                    narrator = value;
                }
            }

            return;
        }

        var segment =
            FirstSegment(decoded);

        if (segment is not null)
        {
            frames.TryAdd(frameId, segment);
        }
    }

    private static string? DecodeEncodedString(
        ReadOnlySpan<byte> data)
    {
        var encodingByte = data[0];
        var payload = data[1..];

        return encodingByte switch
        {
            0 => Encoding.Latin1.GetString(payload),
            1 => DecodeUtf16WithBom(payload),
            2 => Encoding.BigEndianUnicode.GetString(payload),
            3 => Encoding.UTF8.GetString(payload),
            _ => Encoding.Latin1.GetString(payload)
        };
    }

    private static string DecodeUtf16WithBom(
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length >= 2 &&
            payload[0] == 0xFF &&
            payload[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(payload[2..]);
        }

        if (payload.Length >= 2 &&
            payload[0] == 0xFE &&
            payload[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(payload[2..]);
        }

        return Encoding.Unicode.GetString(payload);
    }

    private static string? FirstSegment(
        string value)
    {
        var segment =
            value.Split('\0')[0].Trim();

        return string.IsNullOrWhiteSpace(segment)
            ? null
            : segment;
    }

    private static int ReadSynchsafeInt(
        ReadOnlySpan<byte> bytes)
    {
        return (bytes[0] << 21) |
               (bytes[1] << 14) |
               (bytes[2] << 7) |
               bytes[3];
    }
}
