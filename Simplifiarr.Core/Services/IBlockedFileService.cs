using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IBlockedFileService
{
    /// <summary>
    /// Report-only: every cataloged Movie/TV/Book/Audiobook/Music/Comic
    /// asset still carrying a Zone.Identifier "Mark of the Web" stream.
    /// Never unblocks anything on its own.
    /// </summary>
    Task<List<BlockedFile>> GetBlockedFilesAsync(
        string? driveName = null,
        bool forceRefresh = false);

    /// <summary>
    /// Removes the Zone.Identifier stream from one specific file - the
    /// only way anything actually gets unblocked, always an explicit
    /// per-file (or per-selection) action from the UI.
    /// </summary>
    Task<bool> UnblockAsync(
        string canonicalPath);
}
