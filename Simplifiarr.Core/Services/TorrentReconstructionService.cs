using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Reconstructs a single-file torrent's expected layout from a candidate
/// file that may have been renamed/relocated - refuses to proceed unless
/// piece verification proves the candidate is byte-identical to what the
/// torrent describes, then hardlinks it into place under the torrent's own
/// name. Never copies, never touches the candidate file itself.
/// </summary>
public class TorrentReconstructionService
    : ITorrentReconstructionService
{
    private readonly ITorrentPieceVerificationService _verificationService;
    private readonly IHardlinkService _hardlinkService;

    public TorrentReconstructionService(
        ITorrentPieceVerificationService verificationService,
        IHardlinkService hardlinkService)
    {
        _verificationService = verificationService;
        _hardlinkService = hardlinkService;
    }

    public async Task<TorrentReconstructionResult> ReconstructAsync(
        TorrentMetadata torrent,
        string candidateFilePath,
        string outputRoot)
    {
        var verification =
            await _verificationService.VerifyAsync(
                torrent,
                candidateFilePath);

        if (!verification.IsFullMatch)
        {
            return new TorrentReconstructionResult
            {
                Success = false,
                VerificationResult = verification,
                FailureReason =
                    verification.FailureReason ??
                    "Candidate file does not match this torrent's piece hashes."
            };
        }

        return await LinkIntoPlaceAsync(
            torrent,
            candidateFilePath,
            outputRoot,
            verification);
    }

    public async Task<TorrentReconstructionResult> ReconstructFromCatalogAsync(
        TorrentMetadata torrent,
        IReadOnlyCollection<MediaAsset> catalogAssets,
        string outputRoot)
    {
        var sizeCandidates =
            CrossSeedCandidateFilter.SameSizeCandidates(
                catalogAssets,
                torrent.TotalLengthBytes,
                torrent.Name);

        if (sizeCandidates.Count == 0)
        {
            return new TorrentReconstructionResult
            {
                Success = false,
                FailureReason =
                    "No cataloged file has a matching size and file type - nothing to verify against."
            };
        }

        PieceVerificationResult? lastVerification = null;

        foreach (var candidate in sizeCandidates)
        {
            var verification =
                await _verificationService.VerifyAsync(
                    torrent,
                    candidate.CanonicalPath);

            lastVerification = verification;

            if (!verification.IsFullMatch)
            {
                continue;
            }

            var result =
                await LinkIntoPlaceAsync(
                    torrent,
                    candidate.CanonicalPath,
                    outputRoot,
                    verification);

            result.MatchedAsset = candidate;

            return result;
        }

        return new TorrentReconstructionResult
        {
            Success = false,
            VerificationResult = lastVerification,
            FailureReason =
                $"Tried {sizeCandidates.Count} same-size candidate(s) from your catalog - none matched byte-for-byte."
        };
    }

    public async Task<TorrentReconstructionResult> ReconstructFileEntryAsync(
        TorrentMetadata torrent,
        TorrentFileEntry fileEntry,
        string candidateFilePath,
        string outputRoot)
    {
        var verification =
            await _verificationService.VerifyFileEntryAsync(
                torrent,
                fileEntry,
                candidateFilePath);

        if (!verification.IsFullMatch)
        {
            return new TorrentReconstructionResult
            {
                Success = false,
                VerificationResult = verification,
                FailureReason =
                    verification.FailureReason ??
                    "Candidate file does not match this torrent entry's piece hashes."
            };
        }

        return await LinkFileEntryIntoPlaceAsync(
            torrent,
            fileEntry,
            candidateFilePath,
            outputRoot,
            verification);
    }

    public async Task<TorrentReconstructionResult> ReconstructFileEntryFromCatalogAsync(
        TorrentMetadata torrent,
        TorrentFileEntry fileEntry,
        IReadOnlyCollection<MediaAsset> catalogAssets,
        string outputRoot)
    {
        var sizeCandidates =
            CrossSeedCandidateFilter.SameSizeCandidates(
                catalogAssets,
                fileEntry.LengthBytes,
                fileEntry.PathSegments.LastOrDefault() ?? string.Empty,
                torrent.Name);

        if (sizeCandidates.Count == 0)
        {
            return new TorrentReconstructionResult
            {
                Success = false,
                FailureReason =
                    "No cataloged file has a matching size and file type - nothing to verify against."
            };
        }

        PieceVerificationResult? lastVerification = null;

        foreach (var candidate in sizeCandidates)
        {
            var verification =
                await _verificationService.VerifyFileEntryAsync(
                    torrent,
                    fileEntry,
                    candidate.CanonicalPath);

            lastVerification = verification;

            if (!verification.IsFullMatch)
            {
                continue;
            }

            var result =
                await LinkFileEntryIntoPlaceAsync(
                    torrent,
                    fileEntry,
                    candidate.CanonicalPath,
                    outputRoot,
                    verification);

            result.MatchedAsset = candidate;

            return result;
        }

        return new TorrentReconstructionResult
        {
            Success = false,
            VerificationResult = lastVerification,
            FailureReason =
                $"Tried {sizeCandidates.Count} same-size candidate(s) from your catalog - none matched byte-for-byte."
        };
    }

    private async Task<TorrentReconstructionResult> LinkFileEntryIntoPlaceAsync(
        TorrentMetadata torrent,
        TorrentFileEntry fileEntry,
        string candidateFilePath,
        string outputRoot,
        PieceVerificationResult verification)
    {
        var effectiveRoot =
            CrossSeedOutputPath.ResolveRoot(outputRoot, candidateFilePath);

        var targetPath =
            Path.Combine(
                effectiveRoot,
                torrent.Name,
                Path.Combine(fileEntry.PathSegments.ToArray()));

        var hardlinkResult =
            await _hardlinkService.CreateHardlinkAsync(
                candidateFilePath,
                targetPath);

        return new TorrentReconstructionResult
        {
            Success = hardlinkResult.Success,
            TargetPath = hardlinkResult.Success ? targetPath : null,
            OutputRoot = effectiveRoot,
            VerificationResult = verification,
            FailureReason =
                hardlinkResult.Success ? null : hardlinkResult.ErrorMessage
        };
    }

    private async Task<TorrentReconstructionResult> LinkIntoPlaceAsync(
        TorrentMetadata torrent,
        string candidateFilePath,
        string outputRoot,
        PieceVerificationResult verification)
    {
        var effectiveRoot =
            CrossSeedOutputPath.ResolveRoot(outputRoot, candidateFilePath);

        var targetPath =
            Path.Combine(effectiveRoot, torrent.Name);

        var hardlinkResult =
            await _hardlinkService.CreateHardlinkAsync(
                candidateFilePath,
                targetPath);

        return new TorrentReconstructionResult
        {
            Success = hardlinkResult.Success,
            TargetPath = hardlinkResult.Success ? targetPath : null,
            OutputRoot = effectiveRoot,
            VerificationResult = verification,
            FailureReason =
                hardlinkResult.Success ? null : hardlinkResult.ErrorMessage
        };
    }
}
