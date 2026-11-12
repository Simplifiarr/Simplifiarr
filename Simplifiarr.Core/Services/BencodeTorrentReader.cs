using System.Security.Cryptography;
using System.Text;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Hand-rolled bencode decoder for .torrent files (BEP 3) - no third-party
/// package, matching every other binary/archive reader in this app. Bencode
/// has four value types: byte strings ("&lt;length&gt;:&lt;bytes&gt;"),
/// integers ("i&lt;digits&gt;e"), lists ("l...e"), and dictionaries
/// ("d...e", keys are always byte strings). A .torrent file is one
/// top-level dictionary with tracker metadata plus a nested "info"
/// dictionary describing the actual data - only "info" is read here.
///
/// Single-file torrents have a "length" key directly on "info"; multi-file
/// torrents have a "files" key instead - a list of {path, length} entries
/// that BEP 3 concatenates end-to-end, in this exact list order, into one
/// virtual byte stream that "pieces" is computed over (a single piece can
/// therefore straddle two adjacent files - see TorrentPieceVerificationService
/// for how that's handled).
/// </summary>
public static class BencodeTorrentReader
{
    public static TorrentMetadata? TryParse(
        byte[] torrentBytes)
    {
        try
        {
            var offset = 0;

            var root =
                ParseValue(torrentBytes, ref offset) as Dictionary<string, object>;

            if (root is null ||
                !root.TryGetValue("info", out var infoValue) ||
                infoValue is not Dictionary<string, object> info)
            {
                return null;
            }

            TorrentMetadata? metadata;

            if (info.TryGetValue("files", out var filesValue))
            {
                metadata = TryParseMultiFile(info, filesValue);
            }
            else if (!info.TryGetValue("name", out var nameValue) ||
                nameValue is not byte[] nameBytes ||
                !info.TryGetValue("piece length", out var pieceLengthValue) ||
                pieceLengthValue is not long pieceLength ||
                !info.TryGetValue("pieces", out var piecesValue) ||
                piecesValue is not byte[] piecesBytes ||
                !info.TryGetValue("length", out var lengthValue) ||
                lengthValue is not long totalLength)
            {
                return null;
            }
            else if (piecesBytes.Length % 20 != 0)
            {
                return null;
            }
            else
            {
                var pieces = new List<byte[]>(piecesBytes.Length / 20);

                for (var i = 0; i < piecesBytes.Length; i += 20)
                {
                    pieces.Add(piecesBytes[i..(i + 20)]);
                }

                metadata = new TorrentMetadata
                {
                    Name = Encoding.UTF8.GetString(nameBytes),
                    PieceLengthBytes = pieceLength,
                    Pieces = pieces,
                    TotalLengthBytes = totalLength,
                    IsSingleFile = true
                };
            }

            if (metadata is not null)
            {
                metadata.InfoHashHex = ComputeInfoHashHex(torrentBytes);
                metadata.TrackerUrls = ReadTrackerUrls(root);
            }

            return metadata;
        }
        catch
        {
            return null;
        }
    }

    private static List<string> ReadTrackerUrls(
        Dictionary<string, object> root)
    {
        var urls = new List<string>();

        if (root.TryGetValue("announce", out var announce) &&
            announce is byte[] announceBytes)
        {
            urls.Add(Encoding.UTF8.GetString(announceBytes));
        }

        if (root.TryGetValue("announce-list", out var announceList) &&
            announceList is List<object> tiers)
        {
            foreach (var tier in tiers.OfType<List<object>>())
            {
                urls.AddRange(
                    tier.OfType<byte[]>().Select(Encoding.UTF8.GetString));
            }
        }

        return urls.Distinct().ToList();
    }

    /// <summary>
    /// BEP 3's info-hash is the SHA1 of the raw bencoded bytes of the "info"
    /// dictionary exactly as they appear in the .torrent file - not a
    /// re-encoding, since key ordering/formatting could differ from the
    /// original and produce a different hash. Walks the top-level dict just
    /// far enough to find "info"'s exact byte span, reusing the existing
    /// parse functions purely to advance offset correctly (their parsed
    /// return values are discarded here). This is the same identifier
    /// torrent clients report (e.g. the client's TorrentHash) - two
    /// torrents sharing this hash are, by definition, the exact same
    /// content, regardless of which tracker each came from.
    /// </summary>
    private static string? ComputeInfoHashHex(
        byte[] torrentBytes)
    {
        try
        {
            var offset = 0;

            if ((char)torrentBytes[offset] != 'd')
            {
                return null;
            }

            // Skip the leading 'd'.
            offset++;

            while (torrentBytes[offset] != 'e')
            {
                var keyBytes = ParseByteString(torrentBytes, ref offset);
                var key = Encoding.UTF8.GetString(keyBytes);

                if (key == "info")
                {
                    var infoStart = offset;

                    // Discard - only advancing offset past this value matters.
                    ParseValue(torrentBytes, ref offset);

                    var infoBytes = torrentBytes[infoStart..offset];

                    return Convert.ToHexString(SHA1.HashData(infoBytes)).ToLowerInvariant();
                }

                // Not the info key - skip its value the same way.
                ParseValue(torrentBytes, ref offset);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static TorrentMetadata? TryParseMultiFile(
        Dictionary<string, object> info,
        object filesValue)
    {
        if (!info.TryGetValue("name", out var nameValue) ||
            nameValue is not byte[] nameBytes ||
            !info.TryGetValue("piece length", out var pieceLengthValue) ||
            pieceLengthValue is not long pieceLength ||
            !info.TryGetValue("pieces", out var piecesValue) ||
            piecesValue is not byte[] piecesBytes ||
            filesValue is not List<object> filesList ||
            filesList.Count == 0)
        {
            return null;
        }

        if (piecesBytes.Length % 20 != 0)
        {
            return null;
        }

        var pieces = new List<byte[]>(piecesBytes.Length / 20);

        for (var i = 0; i < piecesBytes.Length; i += 20)
        {
            pieces.Add(piecesBytes[i..(i + 20)]);
        }

        var fileEntries = new List<TorrentFileEntry>(filesList.Count);
        var runningOffset = 0L;

        foreach (var fileValue in filesList)
        {
            if (fileValue is not Dictionary<string, object> fileDict ||
                !fileDict.TryGetValue("length", out var fileLengthValue) ||
                fileLengthValue is not long fileLength ||
                !fileDict.TryGetValue("path", out var pathValue) ||
                pathValue is not List<object> pathList ||
                pathList.Count == 0)
            {
                return null;
            }

            var pathSegments = new List<string>(pathList.Count);

            foreach (var segment in pathList)
            {
                if (segment is not byte[] segmentBytes)
                {
                    return null;
                }

                pathSegments.Add(Encoding.UTF8.GetString(segmentBytes));
            }

            fileEntries.Add(new TorrentFileEntry
            {
                PathSegments = pathSegments,
                LengthBytes = fileLength,
                StreamOffsetBytes = runningOffset
            });

            runningOffset += fileLength;
        }

        return new TorrentMetadata
        {
            Name = Encoding.UTF8.GetString(nameBytes),
            PieceLengthBytes = pieceLength,
            Pieces = pieces,
            TotalLengthBytes = runningOffset,
            IsSingleFile = false,
            Files = fileEntries
        };
    }

    private static object? ParseValue(
        byte[] data,
        ref int offset)
    {
        var marker = (char)data[offset];

        if (marker == 'i')
        {
            return ParseInteger(data, ref offset);
        }

        if (marker == 'l')
        {
            return ParseList(data, ref offset);
        }

        if (marker == 'd')
        {
            return ParseDictionary(data, ref offset);
        }

        if (marker is >= '0' and <= '9')
        {
            return ParseByteString(data, ref offset);
        }

        throw new FormatException(
            $"Unexpected bencode marker '{marker}' at offset {offset}.");
    }

    private static long ParseInteger(
        byte[] data,
        ref int offset)
    {
        // Skip the leading 'i'.
        offset++;

        var end = Array.IndexOf(data, (byte)'e', offset);

        var digits =
            Encoding.ASCII.GetString(data, offset, end - offset);

        offset = end + 1;

        return long.Parse(digits);
    }

    private static byte[] ParseByteString(
        byte[] data,
        ref int offset)
    {
        var colon = Array.IndexOf(data, (byte)':', offset);

        var lengthDigits =
            Encoding.ASCII.GetString(data, offset, colon - offset);

        var length = int.Parse(lengthDigits);

        var start = colon + 1;

        var value = data[start..(start + length)];

        offset = start + length;

        return value;
    }

    private static List<object> ParseList(
        byte[] data,
        ref int offset)
    {
        // Skip the leading 'l'.
        offset++;

        var items = new List<object>();

        while (data[offset] != 'e')
        {
            var item = ParseValue(data, ref offset);

            if (item is not null)
            {
                items.Add(item);
            }
        }

        // Skip the trailing 'e'.
        offset++;

        return items;
    }

    private static Dictionary<string, object> ParseDictionary(
        byte[] data,
        ref int offset)
    {
        // Skip the leading 'd'.
        offset++;

        var dictionary =
            new Dictionary<string, object>();

        while (data[offset] != 'e')
        {
            var keyBytes = ParseByteString(data, ref offset);
            var key = Encoding.UTF8.GetString(keyBytes);

            var value = ParseValue(data, ref offset);

            if (value is not null)
            {
                dictionary[key] = value;
            }
        }

        // Skip the trailing 'e'.
        offset++;

        return dictionary;
    }
}
