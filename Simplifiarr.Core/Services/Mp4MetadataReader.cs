using System.Buffers.Binary;
using System.Text;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Reads tags from an M4A/M4B file's iTunes-style metadata atoms:
/// moov/udta/meta/ilst, holding one child atom per tag ("&#169;nam" =
/// title, "&#169;ART" = artist/author, "&#169;alb" = album, "trkn" = track
/// number), each (except "trkn") wrapping a nested "data" atom whose
/// payload (after a 4-byte type indicator + 4-byte locale) is the UTF-8
/// string. Shared by the Audiobook and Music metadata extractors, since
/// both just want different atoms out of the same ilst. The "meta" atom
/// uniquely carries a 4-byte version+flags header before its children -
/// skipping it wrong desyncs every subsequent read by 4 bytes. Box sizes
/// are 32-bit big-endian, except size==1 (an 8-byte "largesize" follows)
/// and size==0 ("runs to end of container").
///
/// Box "type" bytes are decoded via Latin-1, not ASCII/UTF-8: the
/// "&#169;" lead byte (0xA9) is outside the ASCII range, and
/// Encoding.ASCII would mangle it into a replacement character, breaking
/// the type comparison. Latin-1 maps every byte 0-255 losslessly.
/// </summary>
public static class Mp4MetadataReader
{
    private const string CopyrightPrefix = "©";

    public static AudiobookMetadata? TryExtract(
        string filePath)
    {
        var ilst =
            TryFindIlst(filePath, out var bytes);

        if (ilst is null)
        {
            return null;
        }

        var title =
            ReadTagText(bytes!, ilst.Value.Start, ilst.Value.End, $"{CopyrightPrefix}nam");

        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var author =
            ReadTagText(bytes!, ilst.Value.Start, ilst.Value.End, $"{CopyrightPrefix}ART");

        var narrator =
            ReadNarratorFromFreeform(bytes!, ilst.Value.Start, ilst.Value.End);

        return new AudiobookMetadata
        {
            Title = title.Trim(),
            Author =
                string.IsNullOrWhiteSpace(author)
                    ? null
                    : author.Trim(),
            Narrator =
                string.IsNullOrWhiteSpace(narrator)
                    ? null
                    : narrator.Trim(),
            Source = AudiobookMetadataSource.Embedded
        };
    }

    public static MusicMetadata? TryExtractMusic(
        string filePath)
    {
        var ilst =
            TryFindIlst(filePath, out var bytes);

        if (ilst is null)
        {
            return null;
        }

        var title =
            ReadTagText(bytes!, ilst.Value.Start, ilst.Value.End, $"{CopyrightPrefix}nam");

        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var artist =
            ReadTagText(bytes!, ilst.Value.Start, ilst.Value.End, $"{CopyrightPrefix}ART");

        var album =
            ReadTagText(bytes!, ilst.Value.Start, ilst.Value.End, $"{CopyrightPrefix}alb");

        var trackNumber =
            ReadTrackNumber(bytes!, ilst.Value.Start, ilst.Value.End);

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
            TrackNumber = trackNumber,
            Source = MusicMetadataSource.Embedded
        };
    }

    private static Box? TryFindIlst(
        string filePath,
        out byte[]? bytes)
    {
        bytes = null;

        try
        {
            var fileBytes =
                File.ReadAllBytes(filePath);

            var moov =
                FindChild(fileBytes, 0, fileBytes.Length, "moov");

            if (moov is null)
            {
                return null;
            }

            var udta =
                FindChild(fileBytes, moov.Value.Start, moov.Value.End, "udta");

            if (udta is null)
            {
                return null;
            }

            var meta =
                FindChild(fileBytes, udta.Value.Start, udta.Value.End, "meta");

            if (meta is null)
            {
                return null;
            }

            // "meta" is a full box: 4-byte version+flags before its children.
            var metaChildrenStart =
                meta.Value.Start + 4;

            if (metaChildrenStart > meta.Value.End)
            {
                return null;
            }

            var ilst =
                FindChild(fileBytes, metaChildrenStart, meta.Value.End, "ilst");

            if (ilst is null)
            {
                return null;
            }

            bytes = fileBytes;

            return ilst;
        }
        catch
        {
            return null;
        }
    }

    private readonly record struct Box(
        int Start,
        int End);

    private static Box? FindChild(
        byte[] bytes,
        int containerStart,
        int containerEnd,
        string targetType)
    {
        var offset = containerStart;

        while (offset + 8 <= containerEnd)
        {
            var box =
                ReadBoxHeader(bytes, offset, containerEnd);

            if (box is null)
            {
                return null;
            }

            var (type, payloadStart, boxEnd) = box.Value;

            if (type == targetType)
            {
                return new Box(payloadStart, boxEnd);
            }

            offset = boxEnd;
        }

        return null;
    }

    private static (string Type, int PayloadStart, int End)? ReadBoxHeader(
        byte[] bytes,
        int offset,
        int containerEnd)
    {
        var size =
            (long)BinaryPrimitives.ReadUInt32BigEndian(
                bytes.AsSpan(offset, 4));

        var type =
            Encoding.Latin1.GetString(bytes, offset + 4, 4);

        var headerSize = 8;

        if (size == 1)
        {
            if (offset + 16 > containerEnd)
            {
                return null;
            }

            size =
                (long)BinaryPrimitives.ReadUInt64BigEndian(
                    bytes.AsSpan(offset + 8, 8));

            headerSize = 16;
        }
        else if (size == 0)
        {
            size = containerEnd - offset;
        }

        if (size < headerSize ||
            size > int.MaxValue ||
            offset + size > containerEnd)
        {
            return null;
        }

        var boxEnd = offset + (int)size;
        var payloadStart = offset + headerSize;

        return (type, payloadStart, boxEnd);
    }

    private static string? ReadTagText(
        byte[] bytes,
        int ilstStart,
        int ilstEnd,
        string tagType)
    {
        var tagBox =
            FindChild(bytes, ilstStart, ilstEnd, tagType);

        if (tagBox is null)
        {
            return null;
        }

        var dataBox =
            FindChild(bytes, tagBox.Value.Start, tagBox.Value.End, "data");

        if (dataBox is null)
        {
            return null;
        }

        // data atom: 4-byte type indicator + 4-byte locale, then UTF-8 text.
        var textStart = dataBox.Value.Start + 8;

        if (textStart > dataBox.Value.End)
        {
            return null;
        }

        var text =
            Encoding.UTF8.GetString(
                bytes,
                textStart,
                dataBox.Value.End - textStart);

        return string.IsNullOrWhiteSpace(text)
            ? null
            : text;
    }

    private static double? ReadTrackNumber(
        byte[] bytes,
        int ilstStart,
        int ilstEnd)
    {
        var tagBox =
            FindChild(bytes, ilstStart, ilstEnd, "trkn");

        if (tagBox is null)
        {
            return null;
        }

        var dataBox =
            FindChild(bytes, tagBox.Value.Start, tagBox.Value.End, "data");

        if (dataBox is null)
        {
            return null;
        }

        // trkn's data atom payload is binary, not text: 2 bytes padding,
        // 2-byte big-endian track number, 2-byte total tracks, 2 bytes
        // padding (8 bytes) - not the UTF-8 text every other tag uses.
        var payloadStart = dataBox.Value.Start + 8;

        if (payloadStart + 4 > dataBox.Value.End)
        {
            return null;
        }

        var trackNumber =
            BinaryPrimitives.ReadUInt16BigEndian(
                bytes.AsSpan(payloadStart + 2, 2));

        return trackNumber == 0
            ? null
            : trackNumber;
    }

    private static string? ReadNarratorFromFreeform(
        byte[] bytes,
        int ilstStart,
        int ilstEnd)
    {
        var offset = ilstStart;

        while (offset + 8 <= ilstEnd)
        {
            var box =
                ReadBoxHeader(bytes, offset, ilstEnd);

            if (box is null)
            {
                break;
            }

            var (type, payloadStart, boxEnd) = box.Value;

            if (type == "----")
            {
                var nameBox =
                    FindChild(bytes, payloadStart, boxEnd, "name");

                // "mean"/"name" atoms carry a 4-byte version+flags header
                // before their text payload (unlike "data", which has 8).
                if (nameBox is not null)
                {
                    var nameTextStart = nameBox.Value.Start + 4;

                    if (nameTextStart <= nameBox.Value.End)
                    {
                        var nameText =
                            Encoding.UTF8.GetString(
                                bytes,
                                nameTextStart,
                                nameBox.Value.End - nameTextStart);

                        if (nameText.Contains("narrat", StringComparison.OrdinalIgnoreCase))
                        {
                            var dataBox =
                                FindChild(bytes, payloadStart, boxEnd, "data");

                            if (dataBox is not null)
                            {
                                var textStart = dataBox.Value.Start + 8;

                                if (textStart <= dataBox.Value.End)
                                {
                                    return Encoding.UTF8.GetString(
                                        bytes,
                                        textStart,
                                        dataBox.Value.End - textStart);
                                }
                            }
                        }
                    }
                }
            }

            offset = boxEnd;
        }

        return null;
    }
}
