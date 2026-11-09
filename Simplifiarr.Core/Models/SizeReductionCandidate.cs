namespace Simplifiarr.Core.Models;

/// <summary>
/// A smaller alternate release of a library file found via Prowlarr -
/// report-only. Never verified byte-for-byte (these are genuinely different
/// encodes, unlike Cross-Seed's matches) and the candidate is never
/// downloaded - everything here comes from the asset's own already-known
/// metadata and the candidate's cheap Prowlarr search-result metadata
/// (declared size, release-title-parsed resolution/codec).
/// </summary>
public class SizeReductionCandidate
{
    // Asset side - what you already own.
    public Guid MediaAssetId { get; set; }

    public string AssetTitle { get; set; } =
        string.Empty;

    public int? AssetYear { get; set; }

    public int? Season { get; set; }

    public int? Episode { get; set; }

    public string CanonicalPath { get; set; } =
        string.Empty;

    public long AssetSizeBytes { get; set; }

    public string AssetResolution { get; set; } =
        string.Empty;

    public string AssetVideoCodec { get; set; } =
        string.Empty;

    public string DriveProfileName { get; set; } =
        string.Empty;

    // Candidate side - Prowlarr search-result metadata only, never downloaded.
    public ProwlarrSearchResult Candidate { get; set; } =
        new();

    public string CandidateResolution { get; set; } =
        string.Empty;

    public string? CandidateCodec { get; set; }

    public bool IsSameResolution { get; set; }

    /// <summary>
    /// True when the candidate is a strictly higher resolution than the
    /// asset (e.g. a 2160p candidate for a 1080p file) despite being
    /// smaller - the best possible outcome, so these are sorted ahead of
    /// same-resolution matches, not lumped in with resolution downgrades.
    /// </summary>
    public bool IsUpgrade { get; set; }

    public long SavingsBytes { get; set; }

    public double SavingsPercent { get; set; }

    public DateTime DiscoveredUtc { get; set; }
}
