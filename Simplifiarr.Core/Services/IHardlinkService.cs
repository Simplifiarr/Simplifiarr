using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IHardlinkService
{
    Task<int?> GetHardlinkCountAsync(string filePath);

    Task<HardlinkCreationResult> CreateHardlinkAsync(
        string existingFilePath,
        string newLinkPath);

    /// <summary>
    /// True if both paths point to the same underlying file data (i.e. one
    /// is a hardlink of the other, or both link to a common source) - not
    /// just the same file name/size/metadata. Different volumes can never
    /// be hardlinked, so this returns false immediately without shelling
    /// out when the two paths aren't on the same drive.
    /// </summary>
    Task<bool> AreHardlinkedAsync(
        string pathA,
        string pathB);
}

