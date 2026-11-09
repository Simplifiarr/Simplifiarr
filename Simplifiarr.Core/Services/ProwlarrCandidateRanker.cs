using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Re-ranks Prowlarr search results by proximity to a known target size,
/// so a search for cross-seed alternates of a specific torrent/file surfaces
/// plausible matches first instead of every raw title hit. This never
/// changes what counts as a verified match (that's still exact SHA1 piece
/// hashing) - it only narrows/orders which candidates are worth downloading
/// and checking at all.
/// </summary>
public static class ProwlarrCandidateRanker
{
    /// <summary>
    /// How close an indexer result's total size is to the torrent you're
    /// cross-seeding. The same release re-uploaded to another tracker has the
    /// same bytes, so an exact (or nearly exact) size is the strongest cheap
    /// signal that a result can possibly be the same files.
    /// </summary>
    public enum SizeMatch
    {
        Exact,
        Near,
        Different
    }

    public const double NearFraction = 0.01;

    public static SizeMatch ClassifySize(
        long resultSizeBytes,
        long targetSizeBytes)
    {
        if (resultSizeBytes == targetSizeBytes)
        {
            return SizeMatch.Exact;
        }

        var band =
            (long)(targetSizeBytes * NearFraction);

        return Math.Abs(resultSizeBytes - targetSizeBytes) <= band
            ? SizeMatch.Near
            : SizeMatch.Different;
    }

    /// <summary>
    /// Only results whose size is exact or within <see cref="NearFraction"/>
    /// of the target, closest first - no fallback to loosely related
    /// results when nothing is close.
    /// </summary>
    public static List<ProwlarrSearchResult> FilterSameContentCandidates(
        IReadOnlyCollection<ProwlarrSearchResult> results,
        long targetSizeBytes) =>
        results
            .Where(result =>
                ClassifySize(result.SizeBytes, targetSizeBytes) != SizeMatch.Different)
            .OrderBy(result => Math.Abs(result.SizeBytes - targetSizeBytes))
            .ThenByDescending(result => result.Seeders)
            .ToList();

    public static List<ProwlarrSearchResult> RankBySizeProximity(
        IReadOnlyCollection<ProwlarrSearchResult> results,
        long targetSizeBytes,
        double sizeBandFraction = 0.10)
    {
        var band =
            (long)(targetSizeBytes * sizeBandFraction);

        var inBand =
            results
                .Where(result => Math.Abs(result.SizeBytes - targetSizeBytes) <= band)
                .ToList();

        var candidates =
            inBand.Count > 0 ? inBand : results;

        return candidates
            .OrderBy(result => Math.Abs(result.SizeBytes - targetSizeBytes))
            .ThenByDescending(result => result.Seeders)
            .ToList();
    }
}
