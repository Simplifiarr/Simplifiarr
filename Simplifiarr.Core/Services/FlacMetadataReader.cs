using System.Buffers.Binary;
using System.Text;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Reads tags from a FLAC file's VORBIS_COMMENT metadata block. After the
/// 4-byte "fLaC" magic, the file is a sequence of metadata blocks: a
/// 1-byte header (top bit = last-block flag, low 7 bits = block type) +
/// 3-byte big-endian block length, then the block data. Block type 4 is
/// VORBIS_COMMENT, which - unlike every other format this project parses -
/// is little-endian: 4-byte vendor length + vendor string, 4-byte comment
/// count, then per comment a 4-byte length + "KEY=VALUE" UTF-8 string.
/// </summary>
public static class FlacMetadataReader
{
    private const int VorbisCommentBlockType = 4;

    public static MusicMetadata? TryExtractMusic(
        string filePath)
    {
        var comments =
            TryReadVorbisComments(filePath);

        if (comments is null ||
            !comments.TryGetValue("TITLE", out var title) ||
            string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        comments.TryGetValue("ARTIST", out var artist);
        comments.TryGetValue("ALBUM", out var album);
        comments.TryGetValue("TRACKNUMBER", out var trackRaw);

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

        // TRACKNUMBER is occasionally "N/total" (e.g. "5/12").
        var slice =
            raw.Split('/')[0].Trim();

        return int.TryParse(slice, out var trackNumber)
            ? trackNumber
            : null;
    }

    private static Dictionary<string, string>? TryReadVorbisComments(
        string filePath)
    {
        try
        {
            var bytes =
                File.ReadAllBytes(filePath);

            if (bytes.Length < 4 ||
                bytes[0] != (byte)'f' ||
                bytes[1] != (byte)'L' ||
                bytes[2] != (byte)'a' ||
                bytes[3] != (byte)'C')
            {
                return null;
            }

            var cursor = 4;

            while (cursor + 4 <= bytes.Length)
            {
                var header = bytes[cursor];
                var isLast = (header & 0x80) != 0;
                var blockType = header & 0x7F;

                var blockLength =
                    (bytes[cursor + 1] << 16) |
                    (bytes[cursor + 2] << 8) |
                    bytes[cursor + 3];

                var blockStart = cursor + 4;

                if (blockStart + blockLength > bytes.Length)
                {
                    return null;
                }

                if (blockType == VorbisCommentBlockType)
                {
                    return ParseVorbisCommentBlock(
                        bytes.AsSpan(blockStart, blockLength));
                }

                if (isLast)
                {
                    break;
                }

                cursor = blockStart + blockLength;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string>? ParseVorbisCommentBlock(
        ReadOnlySpan<byte> block)
    {
        if (block.Length < 8)
        {
            return null;
        }

        var offset = 0;

        var vendorLength =
            (int)BinaryPrimitives.ReadUInt32LittleEndian(block[offset..]);

        offset += 4;

        if (offset + vendorLength + 4 > block.Length)
        {
            return null;
        }

        offset += vendorLength;

        var commentCount =
            (int)BinaryPrimitives.ReadUInt32LittleEndian(block[offset..]);

        offset += 4;

        var comments =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < commentCount; i++)
        {
            if (offset + 4 > block.Length)
            {
                break;
            }

            var commentLength =
                (int)BinaryPrimitives.ReadUInt32LittleEndian(block[offset..]);

            offset += 4;

            if (offset + commentLength > block.Length)
            {
                break;
            }

            var comment =
                Encoding.UTF8.GetString(block.Slice(offset, commentLength));

            offset += commentLength;

            var separatorIndex = comment.IndexOf('=');

            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = comment[..separatorIndex];
            var value = comment[(separatorIndex + 1)..];

            if (!string.IsNullOrWhiteSpace(value))
            {
                comments.TryAdd(key, value);
            }
        }

        return comments;
    }
}
