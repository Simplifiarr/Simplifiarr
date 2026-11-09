using System.Security.Cryptography;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Verifies a candidate file against a parsed torrent's declared piece
/// hashes (BEP 3) - the exact mechanism a torrent client uses for its own
/// "force recheck", reproduced here so a match can be proven byte-for-byte
/// regardless of the candidate file's current name/location. Size is
/// checked first as a cheap, correct pre-filter before any hashing.
/// </summary>
public class TorrentPieceVerificationService
    : ITorrentPieceVerificationService
{
    public async Task<PieceVerificationResult> VerifyAsync(
        TorrentMetadata torrent,
        string candidateFilePath)
    {
        if (!File.Exists(candidateFilePath))
        {
            return new PieceVerificationResult
            {
                IsFullMatch = false,
                TotalPieceCount = torrent.Pieces.Count,
                FailureReason = "Candidate file does not exist."
            };
        }

        var candidateLength =
            new FileInfo(candidateFilePath).Length;

        if (candidateLength != torrent.TotalLengthBytes)
        {
            return new PieceVerificationResult
            {
                IsFullMatch = false,
                TotalPieceCount = torrent.Pieces.Count,
                FailureReason =
                    $"Size mismatch: candidate is {candidateLength} bytes, torrent expects {torrent.TotalLengthBytes} bytes."
            };
        }

        var buffer = new byte[torrent.PieceLengthBytes];

        await using var stream =
            new FileStream(
                candidateFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

        var verifiedCount = 0;
        string? failureReason = null;

        for (var pieceIndex = 0; pieceIndex < torrent.Pieces.Count; pieceIndex++)
        {
            var pieceStart = pieceIndex * torrent.PieceLengthBytes;

            var pieceLength =
                (int)Math.Min(
                    torrent.PieceLengthBytes,
                    torrent.TotalLengthBytes - pieceStart);

            var bytesRead =
                await ReadExactAsync(stream, buffer, pieceLength);

            if (bytesRead != pieceLength)
            {
                failureReason ??=
                    $"Could not read piece {pieceIndex} - file ended early.";

                continue;
            }

            var actualHash =
                SHA1.HashData(buffer.AsSpan(0, pieceLength));

            if (actualHash.AsSpan().SequenceEqual(torrent.Pieces[pieceIndex]))
            {
                verifiedCount++;
            }
            else
            {
                failureReason ??=
                    $"Piece {pieceIndex} hash mismatch.";
            }
        }

        var isFullMatch =
            verifiedCount == torrent.Pieces.Count;

        return new PieceVerificationResult
        {
            IsFullMatch = isFullMatch,
            VerifiedPieceCount = verifiedCount,
            TotalPieceCount = torrent.Pieces.Count,
            FailureReason = isFullMatch ? null : failureReason
        };
    }

    public async Task<PieceVerificationResult> VerifyFileEntryAsync(
        TorrentMetadata torrent,
        TorrentFileEntry fileEntry,
        string candidateFilePath)
    {
        if (fileEntry.LengthBytes == 0)
        {
            return new PieceVerificationResult
            {
                IsFullMatch = false,
                FailureReason = "File entry is zero-length; nothing to verify."
            };
        }

        if (!File.Exists(candidateFilePath))
        {
            return new PieceVerificationResult
            {
                IsFullMatch = false,
                FailureReason = "Candidate file does not exist."
            };
        }

        var candidateLength =
            new FileInfo(candidateFilePath).Length;

        if (candidateLength != fileEntry.LengthBytes)
        {
            return new PieceVerificationResult
            {
                IsFullMatch = false,
                FailureReason =
                    $"Size mismatch: candidate is {candidateLength} bytes, torrent entry expects {fileEntry.LengthBytes} bytes."
            };
        }

        var entryStart = fileEntry.StreamOffsetBytes;
        var entryEnd = entryStart + fileEntry.LengthBytes;

        var firstPieceIndex = (int)(entryStart / torrent.PieceLengthBytes);
        var lastPieceIndex = (int)((entryEnd - 1) / torrent.PieceLengthBytes);

        var buffer = new byte[torrent.PieceLengthBytes];

        await using var stream =
            new FileStream(
                candidateFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

        var verifiedCount = 0;
        var boundaryCount = 0;
        var mismatchCount = 0;
        string? failureReason = null;

        for (var pieceIndex = firstPieceIndex; pieceIndex <= lastPieceIndex; pieceIndex++)
        {
            var pieceStart = (long)pieceIndex * torrent.PieceLengthBytes;

            var pieceEnd =
                Math.Min(pieceStart + torrent.PieceLengthBytes, torrent.TotalLengthBytes);

            var isClean = pieceStart >= entryStart && pieceEnd <= entryEnd;

            if (!isClean)
            {
                boundaryCount++;
                continue;
            }

            var pieceLength = (int)(pieceEnd - pieceStart);
            var fileRelativeOffset = pieceStart - entryStart;

            stream.Position = fileRelativeOffset;

            var bytesRead =
                await ReadExactAsync(stream, buffer, pieceLength);

            if (bytesRead != pieceLength)
            {
                mismatchCount++;

                failureReason ??=
                    $"Could not read piece {pieceIndex} - file ended early.";

                continue;
            }

            var actualHash =
                SHA1.HashData(buffer.AsSpan(0, pieceLength));

            if (actualHash.AsSpan().SequenceEqual(torrent.Pieces[pieceIndex]))
            {
                verifiedCount++;
            }
            else
            {
                mismatchCount++;

                failureReason ??=
                    $"Piece {pieceIndex} hash mismatch.";
            }
        }

        var totalPieceCount = lastPieceIndex - firstPieceIndex + 1;
        var isFullMatch = mismatchCount == 0 && verifiedCount > 0;

        return new PieceVerificationResult
        {
            IsFullMatch = isFullMatch,
            VerifiedPieceCount = verifiedCount,
            BoundaryPieceCount = boundaryCount,
            TotalPieceCount = totalPieceCount,
            FailureReason = isFullMatch ? null : failureReason
        };
    }

    public async Task<PieceVerificationResult> VerifyWholeTorrentAsync(
        TorrentMetadata torrent,
        IReadOnlyList<FileEntryMatch> entryMatches)
    {
        var ordered =
            new List<(long Length, string Path)>();

        foreach (var entry in torrent.Files.Where(entry => entry.LengthBytes > 0))
        {
            var match =
                entryMatches.FirstOrDefault(candidate =>
                    ReferenceEquals(candidate.FileEntry, entry));

            if (match is null)
            {
                return new PieceVerificationResult
                {
                    IsFullMatch = false,
                    TotalPieceCount = torrent.Pieces.Count,
                    FailureReason =
                        $"No matched file for \"{entry.RelativePath}\"."
                };
            }

            ordered.Add((entry.LengthBytes, match.ResolvedPath));
        }

        var buffer = new byte[torrent.PieceLengthBytes];

        var verifiedCount = 0;
        string? failureReason = null;

        var fileIndex = 0;
        FileStream? stream = null;
        long remainingInFile = 0;

        try
        {
            for (var pieceIndex = 0; pieceIndex < torrent.Pieces.Count; pieceIndex++)
            {
                var pieceStart = (long)pieceIndex * torrent.PieceLengthBytes;

                var pieceLength =
                    (int)Math.Min(
                        torrent.PieceLengthBytes,
                        torrent.TotalLengthBytes - pieceStart);

                var filled = 0;

                while (filled < pieceLength)
                {
                    if (stream is null)
                    {
                        if (fileIndex >= ordered.Count)
                        {
                            break;
                        }

                        stream =
                            new FileStream(
                                ordered[fileIndex].Path,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.Read);

                        remainingInFile = ordered[fileIndex].Length;
                    }

                    var wanted =
                        (int)Math.Min(pieceLength - filled, remainingInFile);

                    var read =
                        wanted > 0
                            ? await stream.ReadAsync(buffer.AsMemory(filled, wanted))
                            : 0;

                    if (read == 0)
                    {
                        // Finished this file (or it came up short) - move on
                        // so a short file shows up as a hash mismatch rather
                        // than hanging.
                        await stream.DisposeAsync();
                        stream = null;
                        fileIndex++;
                        continue;
                    }

                    filled += read;
                    remainingInFile -= read;
                }

                if (filled != pieceLength)
                {
                    failureReason ??=
                        $"Could not read piece {pieceIndex} - a file ended early.";

                    continue;
                }

                var actualHash =
                    SHA1.HashData(buffer.AsSpan(0, pieceLength));

                if (actualHash.AsSpan().SequenceEqual(torrent.Pieces[pieceIndex]))
                {
                    verifiedCount++;
                }
                else
                {
                    failureReason ??=
                        $"Piece {pieceIndex} hash mismatch.";
                }
            }
        }
        catch (IOException ex)
        {
            failureReason ??= $"Could not read a matched file: {ex.Message}";
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync();
            }
        }

        var isFullMatch =
            verifiedCount == torrent.Pieces.Count &&
            torrent.Pieces.Count > 0;

        return new PieceVerificationResult
        {
            IsFullMatch = isFullMatch,
            VerifiedPieceCount = verifiedCount,
            TotalPieceCount = torrent.Pieces.Count,
            FailureReason = isFullMatch ? null : failureReason
        };
    }

    private static async Task<int> ReadExactAsync(
        Stream stream,
        byte[] buffer,
        int count)
    {
        var totalRead = 0;

        while (totalRead < count)
        {
            var read =
                await stream.ReadAsync(
                    buffer.AsMemory(totalRead, count - totalRead));

            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }
}
