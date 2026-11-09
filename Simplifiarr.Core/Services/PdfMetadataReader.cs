using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Best-effort, dependency-free PDF metadata reader. Reads the /Info
/// dictionary (Title/Author) from the classic trailer or an XRef stream
/// dictionary, resolving the object directly or by brute-force scanning
/// compressed object streams (/ObjStm) when it isn't stored inline.
/// PDFs it can't confidently parse (encrypted, unusual structure) return
/// null so callers can fall back to filename-based identification.
/// </summary>
public static class PdfMetadataReader
{
    public static BookMetadata? TryExtract(
        string filePath)
    {
        try
        {
            var bytes =
                File.ReadAllBytes(filePath);

            var text =
                Encoding.Latin1.GetString(bytes);

            if (Regex.IsMatch(text, @"/Encrypt\b"))
            {
                return null;
            }

            var infoRef =
                FindInfoReference(text);

            if (infoRef is null)
            {
                return null;
            }

            var dictText =
                ResolveObjectDictionary(
                    text,
                    infoRef.Value.ObjectNumber);

            if (dictText is null)
            {
                return null;
            }

            var title =
                ExtractStringValue(dictText, "Title");

            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var author =
                ExtractStringValue(dictText, "Author");

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

    private static (int ObjectNumber, int Generation)? FindInfoReference(
        string text)
    {
        var trailerIndex =
            text.LastIndexOf("trailer", StringComparison.Ordinal);

        if (trailerIndex >= 0)
        {
            var window =
                text.Substring(
                    trailerIndex,
                    Math.Min(4000, text.Length - trailerIndex));

            var match =
                Regex.Match(window, @"/Info\s+(\d+)\s+(\d+)\s+R");

            if (match.Success)
            {
                return (
                    int.Parse(match.Groups[1].Value),
                    int.Parse(match.Groups[2].Value));
            }
        }

        var xrefStreamIndex =
            text.LastIndexOf("/Type/XRef", StringComparison.Ordinal);

        if (xrefStreamIndex < 0)
        {
            xrefStreamIndex =
                text.LastIndexOf("/Type /XRef", StringComparison.Ordinal);
        }

        if (xrefStreamIndex >= 0)
        {
            var streamKeywordIndex =
                text.IndexOf("stream", xrefStreamIndex, StringComparison.Ordinal);

            var windowLength =
                streamKeywordIndex > xrefStreamIndex
                    ? streamKeywordIndex - xrefStreamIndex
                    : Math.Min(2000, text.Length - xrefStreamIndex);

            var window =
                text.Substring(xrefStreamIndex, windowLength);

            var match =
                Regex.Match(window, @"/Info\s+(\d+)\s+(\d+)\s+R");

            if (match.Success)
            {
                return (
                    int.Parse(match.Groups[1].Value),
                    int.Parse(match.Groups[2].Value));
            }
        }

        return null;
    }

    private static string? ResolveObjectDictionary(
        string text,
        int objectNumber)
    {
        var directMatch =
            Regex.Match(
                text,
                $@"(?<![0-9]){objectNumber}\s+\d+\s+obj\b");

        if (directMatch.Success)
        {
            var dict =
                ExtractDictionary(
                    text,
                    directMatch.Index + directMatch.Length);

            if (dict is not null)
            {
                return dict;
            }
        }

        return FindObjectInObjectStreams(
            text,
            objectNumber);
    }

    private static string? FindObjectInObjectStreams(
        string text,
        int objectNumber)
    {
        foreach (Match objStmMatch in Regex.Matches(text, @"\d+\s+\d+\s+obj\b"))
        {
            var dictStart =
                objStmMatch.Index + objStmMatch.Length;

            var dict =
                ExtractDictionary(text, dictStart);

            if (dict is null ||
                !dict.Contains("/ObjStm", StringComparison.Ordinal))
            {
                continue;
            }

            var nMatch =
                Regex.Match(dict, @"/N\s+(\d+)");

            var firstMatch =
                Regex.Match(dict, @"/First\s+(\d+)");

            if (!nMatch.Success || !firstMatch.Success)
            {
                continue;
            }

            var count =
                int.Parse(nMatch.Groups[1].Value);

            var first =
                int.Parse(firstMatch.Groups[1].Value);

            var streamStart =
                text.IndexOf("stream", dictStart, StringComparison.Ordinal);

            if (streamStart < 0)
            {
                continue;
            }

            streamStart +=
                "stream".Length;

            if (streamStart < text.Length && text[streamStart] == '\r')
            {
                streamStart++;
            }

            if (streamStart < text.Length && text[streamStart] == '\n')
            {
                streamStart++;
            }

            var streamEnd =
                text.IndexOf("endstream", streamStart, StringComparison.Ordinal);

            if (streamEnd < 0)
            {
                continue;
            }

            var rawStream =
                Encoding.Latin1.GetBytes(
                    text.Substring(streamStart, streamEnd - streamStart));

            var decompressed =
                dict.Contains("FlateDecode", StringComparison.Ordinal)
                    ? Inflate(rawStream)
                    : rawStream;

            if (decompressed is null)
            {
                continue;
            }

            var decompressedText =
                Encoding.Latin1.GetString(decompressed);

            var header =
                decompressedText.Length >= first
                    ? decompressedText[..first]
                    : decompressedText;

            var pairs =
                Regex.Matches(header, @"(\d+)\s+(\d+)")
                    .Select(match => (
                        ObjectNumber: int.Parse(match.Groups[1].Value),
                        Offset: int.Parse(match.Groups[2].Value)))
                    .Take(count)
                    .ToList();

            var target =
                pairs.FirstOrDefault(
                    pair => pair.ObjectNumber == objectNumber);

            if (target == default)
            {
                continue;
            }

            var start =
                first + target.Offset;

            if (start < 0 || start >= decompressedText.Length)
            {
                continue;
            }

            var nextOffset =
                pairs
                    .Where(pair => pair.Offset > target.Offset)
                    .Select(pair => (int?)pair.Offset)
                    .DefaultIfEmpty(null)
                    .Min();

            var end =
                nextOffset.HasValue
                    ? Math.Min(decompressedText.Length, first + nextOffset.Value)
                    : decompressedText.Length;

            if (end <= start)
            {
                continue;
            }

            return decompressedText.Substring(start, end - start);
        }

        return null;
    }

    private static string? ExtractDictionary(
        string text,
        int searchFrom)
    {
        var openIndex =
            text.IndexOf("<<", searchFrom, StringComparison.Ordinal);

        if (openIndex < 0)
        {
            return null;
        }

        var depth = 0;
        var index = openIndex;

        while (index < text.Length - 1)
        {
            if (text[index] == '<' && text[index + 1] == '<')
            {
                depth++;
                index += 2;
                continue;
            }

            if (text[index] == '>' && text[index + 1] == '>')
            {
                depth--;
                index += 2;

                if (depth == 0)
                {
                    return text.Substring(
                        openIndex,
                        index - openIndex);
                }

                continue;
            }

            index++;
        }

        return null;
    }

    private static byte[]? Inflate(
        byte[] zlibData)
    {
        try
        {
            using var input =
                new MemoryStream(zlibData);

            using var zlib =
                new ZLibStream(input, CompressionMode.Decompress);

            using var output =
                new MemoryStream();

            zlib.CopyTo(output);

            return output.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractStringValue(
        string dictText,
        string key)
    {
        var keyIndex =
            dictText.IndexOf($"/{key}", StringComparison.Ordinal);

        if (keyIndex < 0)
        {
            return null;
        }

        var cursor =
            keyIndex + key.Length + 1;

        while (cursor < dictText.Length &&
               char.IsWhiteSpace(dictText[cursor]))
        {
            cursor++;
        }

        if (cursor >= dictText.Length)
        {
            return null;
        }

        if (dictText[cursor] == '(')
        {
            return ExtractLiteralString(dictText, cursor);
        }

        if (dictText[cursor] == '<')
        {
            return ExtractHexString(dictText, cursor);
        }

        return null;
    }

    private static string? ExtractLiteralString(
        string text,
        int openIndex)
    {
        var bytes = new List<byte>();
        var depth = 0;
        var index = openIndex;

        for (; index < text.Length; index++)
        {
            var ch = text[index];

            if (ch == '\\' && index + 1 < text.Length)
            {
                var next = text[index + 1];

                switch (next)
                {
                    case 'n': bytes.Add((byte)'\n'); index++; continue;
                    case 'r': bytes.Add((byte)'\r'); index++; continue;
                    case 't': bytes.Add((byte)'\t'); index++; continue;
                    case 'b': bytes.Add((byte)'\b'); index++; continue;
                    case 'f': bytes.Add((byte)'\f'); index++; continue;
                    case '(': bytes.Add((byte)'('); index++; continue;
                    case ')': bytes.Add((byte)')'); index++; continue;
                    case '\\': bytes.Add((byte)'\\'); index++; continue;
                    case '\r':
                        index++;
                        if (index + 1 < text.Length && text[index + 1] == '\n')
                        {
                            index++;
                        }
                        continue;
                    case '\n':
                        index++;
                        continue;
                }

                if (char.IsDigit(next))
                {
                    var octal = string.Empty;
                    var digitCursor = index + 1;

                    while (digitCursor < text.Length &&
                           octal.Length < 3 &&
                           char.IsDigit(text[digitCursor]))
                    {
                        octal += text[digitCursor];
                        digitCursor++;
                    }

                    bytes.Add(
                        (byte)Convert.ToInt32(octal, 8));

                    index = digitCursor - 1;
                    continue;
                }

                continue;
            }

            if (ch == '(')
            {
                depth++;

                if (depth == 1)
                {
                    continue;
                }

                bytes.Add((byte)'(');
                continue;
            }

            if (ch == ')')
            {
                depth--;

                if (depth == 0)
                {
                    break;
                }

                bytes.Add((byte)')');
                continue;
            }

            bytes.Add((byte)ch);
        }

        return DecodeBytes(bytes.ToArray());
    }

    private static string? ExtractHexString(
        string text,
        int openIndex)
    {
        var closeIndex =
            text.IndexOf('>', openIndex + 1);

        if (closeIndex < 0)
        {
            return null;
        }

        var hex =
            Regex.Replace(
                text.Substring(openIndex + 1, closeIndex - openIndex - 1),
                @"\s+",
                string.Empty);

        if (hex.Length % 2 != 0)
        {
            hex += "0";
        }

        var bytes = new byte[hex.Length / 2];

        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] =
                Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        return DecodeBytes(bytes);
    }

    private static string DecodeBytes(
        byte[] bytes)
    {
        if (bytes.Length >= 2 &&
            bytes[0] == 0xFE &&
            bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(
                bytes,
                2,
                bytes.Length - 2);
        }

        return Encoding.Latin1.GetString(bytes);
    }
}
