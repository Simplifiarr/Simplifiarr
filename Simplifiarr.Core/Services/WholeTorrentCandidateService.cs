using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Composes the existing per-file verification/reconstruction primitives
/// into a "does this whole candidate torrent match?" operation - the thing
/// a bulk scan actually needs. ITorrentReconstructionService/
/// ITorrentPieceVerificationService deliberately stay scoped to "one
/// candidate file/entry, already handed to me"; this service sits one level
/// above, searching a source set per entry and requiring every entry to
/// verify before anything is hardlinked. No duplicated hashing/hardlink
/// logic - every real check still happens inside the composed services.
/// </summary>
public class WholeTorrentCandidateService : IWholeTorrentCandidateService
{
    private readonly ITorrentPieceVerificationService _verificationService;
    private readonly ITorrentReconstructionService _reconstructionService;

    public WholeTorrentCandidateService(
        ITorrentPieceVerificationService verificationService,
        ITorrentReconstructionService reconstructionService)
    {
        _verificationService = verificationService;
        _reconstructionService = reconstructionService;
    }

    public async Task<WholeTorrentMatchResult> EvaluateAsync(
        TorrentMetadata candidateTorrent,
        IReadOnlyCollection<string> primarySourceFilePaths,
        IReadOnlyCollection<MediaAsset> fallbackCatalogAssets)
    {
        if (candidateTorrent.IsSingleFile)
        {
            var wholeMatch =
                await ResolveWholeFileAsync(
                    candidateTorrent,
                    primarySourceFilePaths,
                    fallbackCatalogAssets);

            if (wholeMatch is null)
            {
                return new WholeTorrentMatchResult
                {
                    IsFullMatch = false,
                    FailureReason = "No verified byte-identical file found for this torrent."
                };
            }

            return new WholeTorrentMatchResult
            {
                IsFullMatch = true,
                EntryMatches = [wholeMatch],
                WholeTorrentVerification = wholeMatch.VerificationResult
            };
        }

        var matches = new List<FileEntryMatch>();

        foreach (var entry in candidateTorrent.Files.Where(entry => entry.LengthBytes > 0))
        {
            var resolved =
                await ResolveFileEntryAsync(
                    candidateTorrent,
                    entry,
                    primarySourceFilePaths,
                    fallbackCatalogAssets);

            if (resolved is null)
            {
                return new WholeTorrentMatchResult
                {
                    IsFullMatch = false,
                    EntryMatches = matches,
                    FailureReason = $"No verified byte-identical file found for \"{entry.RelativePath}\"."
                };
            }

            matches.Add(resolved);
        }

        if (matches.Count == 0)
        {
            return new WholeTorrentMatchResult
            {
                IsFullMatch = false,
                FailureReason = "This torrent has no non-empty file entries to verify."
            };
        }

        // Each file matched on its own clean pieces, but the pieces that
        // straddle two files were never checked. Re-hash the whole stream so
        // a match means every byte of the torrent is proven, not "almost".
        var wholeVerification =
            await _verificationService.VerifyWholeTorrentAsync(
                candidateTorrent,
                matches);

        if (!wholeVerification.IsFullMatch)
        {
            return new WholeTorrentMatchResult
            {
                IsFullMatch = false,
                EntryMatches = matches,
                FailureReason =
                    $"Every file matched individually, but whole-torrent verification failed: {wholeVerification.FailureReason}"
            };
        }

        return new WholeTorrentMatchResult
        {
            IsFullMatch = true,
            EntryMatches = matches,
            WholeTorrentVerification = wholeVerification
        };
    }

    public async Task<WholeTorrentReconstructionResult> CommitAsync(
        TorrentMetadata candidateTorrent,
        WholeTorrentMatchResult match,
        byte[] candidateTorrentBytes,
        string outputRoot)
    {
        if (!match.IsFullMatch)
        {
            return new WholeTorrentReconstructionResult
            {
                Success = false,
                FailureReason = "Cannot commit - this candidate was not a full verified match."
            };
        }

        var firstPath =
            match.EntryMatches[0].ResolvedPath;

        if (match.EntryMatches.Any(entryMatch =>
                !CrossSeedOutputPath.IsSameVolume(entryMatch.ResolvedPath, firstPath)))
        {
            return new WholeTorrentReconstructionResult
            {
                Success = false,
                FailureReason =
                    "This torrent's matched files are spread across more than one drive - hardlinks can't cross drives, so they can't all be linked into one folder."
            };
        }

        // Pin one output root for the whole torrent, on the matched files'
        // own drive (hardlinks can't cross volumes).
        outputRoot =
            CrossSeedOutputPath.ResolveRoot(outputRoot, firstPath);

        var hardlinkedPaths = new List<string>();

        foreach (var entryMatch in match.EntryMatches)
        {
            var reconstructResult =
                entryMatch.FileEntry is null
                    ? await _reconstructionService.ReconstructAsync(
                        candidateTorrent,
                        entryMatch.ResolvedPath,
                        outputRoot)
                    : await _reconstructionService.ReconstructFileEntryAsync(
                        candidateTorrent,
                        entryMatch.FileEntry,
                        entryMatch.ResolvedPath,
                        outputRoot);

            if (!reconstructResult.Success)
            {
                return new WholeTorrentReconstructionResult
                {
                    Success = false,
                    HardlinkedPaths = hardlinkedPaths,
                    FailureReason =
                        reconstructResult.FailureReason ??
                        "Hardlinking failed for one of the matched files."
                };
            }

            hardlinkedPaths.Add(reconstructResult.TargetPath!);
        }

        string torrentFilePath;

        try
        {
            // A single-file torrent's data file *is* outputRoot\<name>, so
            // its .torrent can't also live in a folder of that name.
            var torrentDir =
                candidateTorrent.IsSingleFile
                    ? outputRoot
                    : Path.Combine(outputRoot, candidateTorrent.Name);

            Directory.CreateDirectory(torrentDir);

            torrentFilePath =
                Path.Combine(torrentDir, $"{candidateTorrent.Name}.torrent");

            await File.WriteAllBytesAsync(torrentFilePath, candidateTorrentBytes);
        }
        catch (Exception ex)
        {
            return new WholeTorrentReconstructionResult
            {
                Success = false,
                HardlinkedPaths = hardlinkedPaths,
                FailureReason =
                    $"Hardlinked every file successfully, but could not save the .torrent file: {ex.Message}"
            };
        }

        return new WholeTorrentReconstructionResult
        {
            Success = true,
            HardlinkedPaths = hardlinkedPaths,
            TorrentFilePath = torrentFilePath,
            OutputRoot = outputRoot
        };
    }

    private async Task<FileEntryMatch?> ResolveFileEntryAsync(
        TorrentMetadata torrent,
        TorrentFileEntry entry,
        IReadOnlyCollection<string> primarySourceFilePaths,
        IReadOnlyCollection<MediaAsset> fallbackCatalogAssets)
    {
        foreach (var path in primarySourceFilePaths)
        {
            if (!TryGetFileSize(path, out var size) || size != entry.LengthBytes)
            {
                continue;
            }

            var verification =
                await _verificationService.VerifyFileEntryAsync(torrent, entry, path);

            if (verification.IsFullMatch)
            {
                return new FileEntryMatch
                {
                    FileEntry = entry,
                    ResolvedPath = path,
                    VerificationResult = verification
                };
            }
        }

        foreach (var asset in CrossSeedCandidateFilter.SameSizeCandidates(
                     fallbackCatalogAssets,
                     entry.LengthBytes,
                     entry.PathSegments.LastOrDefault() ?? string.Empty,
                     torrent.Name))
        {
            var verification =
                await _verificationService.VerifyFileEntryAsync(torrent, entry, asset.CanonicalPath);

            if (verification.IsFullMatch)
            {
                return new FileEntryMatch
                {
                    FileEntry = entry,
                    ResolvedPath = asset.CanonicalPath,
                    VerificationResult = verification
                };
            }
        }

        return null;
    }

    private async Task<FileEntryMatch?> ResolveWholeFileAsync(
        TorrentMetadata torrent,
        IReadOnlyCollection<string> primarySourceFilePaths,
        IReadOnlyCollection<MediaAsset> fallbackCatalogAssets)
    {
        foreach (var path in primarySourceFilePaths)
        {
            if (!TryGetFileSize(path, out var size) || size != torrent.TotalLengthBytes)
            {
                continue;
            }

            var verification =
                await _verificationService.VerifyAsync(torrent, path);

            if (verification.IsFullMatch)
            {
                return new FileEntryMatch
                {
                    FileEntry = null,
                    ResolvedPath = path,
                    VerificationResult = verification
                };
            }
        }

        foreach (var asset in CrossSeedCandidateFilter.SameSizeCandidates(
                     fallbackCatalogAssets,
                     torrent.TotalLengthBytes,
                     torrent.Name))
        {
            var verification =
                await _verificationService.VerifyAsync(torrent, asset.CanonicalPath);

            if (verification.IsFullMatch)
            {
                return new FileEntryMatch
                {
                    FileEntry = null,
                    ResolvedPath = asset.CanonicalPath,
                    VerificationResult = verification
                };
            }
        }

        return null;
    }

    private static bool TryGetFileSize(
        string path,
        out long size)
    {
        try
        {
            if (!File.Exists(path))
            {
                size = 0;
                return false;
            }

            size = new FileInfo(path).Length;
            return true;
        }
        catch
        {
            size = 0;
            return false;
        }
    }
}
