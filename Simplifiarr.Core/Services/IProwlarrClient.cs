using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IProwlarrClient
{
    /// <summary>
    /// categoryId, when given, is a Torznab top-level category (2000 =
    /// Movies, 5000 = TV) passed straight to Prowlarr's own search so
    /// non-video results (music, books, etc. that happen to match the
    /// query text) never come back at all - cheaper and more reliable
    /// than filtering client-side after the fact.
    /// </summary>
    Task<List<ProwlarrSearchResult>> SearchAsync(
        string query,
        int? categoryId = null);

    Task<byte[]?> DownloadTorrentAsync(string downloadUrl);

    /// <summary>
    /// What a release's download link gives: a .torrent file, or a magnet
    /// link (some indexers only have those). Null when neither can be had.
    /// </summary>
    Task<ReleaseSource?> GetReleaseAsync(string downloadUrl);

    Task<bool> TestConnectionAsync();
}

/// <summary>A .torrent file or a magnet link; exactly one is set.</summary>
public sealed record ReleaseSource(byte[]? TorrentBytes, string? Magnet);