using System.Buffers.Binary;
using System.Text;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Reads title/author from a MOBI (or AZW3, same container family) file.
/// MOBI is a PalmDB container: a 78-byte database header, a record offset
/// list, then record 0 holds the PalmDOC header + MOBI header (with the
/// book's full title) and, if present, an EXTH header (with the author,
/// record type 100). All multi-byte fields are big-endian.
/// </summary>
public static class MobiMetadataReader
{
    public static BookMetadata? TryExtract(
        string filePath)
    {
        try
        {
            var bytes =
                File.ReadAllBytes(filePath);

            if (bytes.Length < 78)
            {
                return null;
            }

            var recordCount =
                BinaryPrimitives.ReadUInt16BigEndian(
                    bytes.AsSpan(76, 2));

            if (recordCount == 0 || bytes.Length < 78 + 8)
            {
                return null;
            }

            var record0Offset =
                (int)BinaryPrimitives.ReadUInt32BigEndian(
                    bytes.AsSpan(78, 4));

            if (record0Offset < 0 || record0Offset + 16 + 232 > bytes.Length)
            {
                return null;
            }

            var mobiHeaderOffset =
                record0Offset + 16;

            var mobiIdentifier =
                Encoding.ASCII.GetString(bytes, mobiHeaderOffset, 4);

            if (mobiIdentifier != "MOBI")
            {
                return null;
            }

            var headerLength =
                (int)BinaryPrimitives.ReadUInt32BigEndian(
                    bytes.AsSpan(mobiHeaderOffset + 4, 4));

            var textEncoding =
                BinaryPrimitives.ReadUInt32BigEndian(
                    bytes.AsSpan(mobiHeaderOffset + 12, 4));

            var fullNameOffset =
                (int)BinaryPrimitives.ReadUInt32BigEndian(
                    bytes.AsSpan(mobiHeaderOffset + 84, 4));

            var fullNameLength =
                (int)BinaryPrimitives.ReadUInt32BigEndian(
                    bytes.AsSpan(mobiHeaderOffset + 88, 4));

            var titleStart =
                record0Offset + fullNameOffset;

            if (titleStart < 0 ||
                fullNameLength <= 0 ||
                titleStart + fullNameLength > bytes.Length)
            {
                return null;
            }

            var title =
                DecodeText(
                    bytes.AsSpan(titleStart, fullNameLength).ToArray(),
                    textEncoding);

            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            string? author = null;

            var exthFlags =
                mobiHeaderOffset + 128 <= bytes.Length - 4
                    ? BinaryPrimitives.ReadUInt32BigEndian(
                        bytes.AsSpan(mobiHeaderOffset + 128, 4))
                    : 0;

            if ((exthFlags & 0x40) != 0)
            {
                author =
                    TryReadExthAuthor(
                        bytes,
                        mobiHeaderOffset + headerLength,
                        textEncoding);
            }

            return new BookMetadata
            {
                Title = title.Trim(),
                Author =
                    string.IsNullOrWhiteSpace(author)
                        ? null
                        : author.Trim(),
                Source = BookMetadataSource.Embedded
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadExthAuthor(
        byte[] bytes,
        int exthOffset,
        uint textEncoding)
    {
        if (exthOffset < 0 || exthOffset + 12 > bytes.Length)
        {
            return null;
        }

        var identifier =
            Encoding.ASCII.GetString(bytes, exthOffset, 4);

        if (identifier != "EXTH")
        {
            return null;
        }

        var recordCount =
            (int)BinaryPrimitives.ReadUInt32BigEndian(
                bytes.AsSpan(exthOffset + 8, 4));

        var cursor =
            exthOffset + 12;

        for (var i = 0; i < recordCount; i++)
        {
            if (cursor + 8 > bytes.Length)
            {
                break;
            }

            var recordType =
                BinaryPrimitives.ReadUInt32BigEndian(
                    bytes.AsSpan(cursor, 4));

            var recordLength =
                (int)BinaryPrimitives.ReadUInt32BigEndian(
                    bytes.AsSpan(cursor + 4, 4));

            if (recordLength < 8 || cursor + recordLength > bytes.Length)
            {
                break;
            }

            if (recordType == 100)
            {
                var dataLength =
                    recordLength - 8;

                return DecodeText(
                    bytes.AsSpan(cursor + 8, dataLength).ToArray(),
                    textEncoding);
            }

            cursor += recordLength;
        }

        return null;
    }

    private static string DecodeText(
        byte[] bytes,
        uint textEncoding)
    {
        return textEncoding == 65001
            ? Encoding.UTF8.GetString(bytes)
            : Encoding.Latin1.GetString(bytes);
    }
}
