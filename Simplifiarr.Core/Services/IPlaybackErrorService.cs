using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>
/// What happened when a fix was applied to one file.
/// </summary>
public sealed record PlaybackFixResult(
    bool Succeeded,
    string Message);

/// <summary>
/// Finds library files that may not play, and fixes the ones that can be
/// fixed without changing what is in the file: Windows' "downloaded" mark,
/// permissions, and unpacking packed releases next to the archive.
/// </summary>
public interface IPlaybackErrorService
{
    /// <summary>
    /// Checks every library folder on one drive. Part of the app-wide Scan.
    /// </summary>
    Task<List<PlaybackError>> ScanAsync(
        string driveName);

    /// <summary>What the last scan found on the enabled drives.</summary>
    Task<List<PlaybackError>> GetAllAsync();

    IReadOnlyList<PlaybackError> GetForDrive(
        string driveName);

    DateTime? GetLastScanUtc(
        string driveName);

    /// <summary>
    /// The account whose access is checked, as a readable name, or null
    /// when none is set and no media server service was found.
    /// </summary>
    Task<string?> GetMediaServerAccountAsync();

    /// <summary>Where 7-Zip was found, or null.</summary>
    Task<string?> FindSevenZipAsync();

    /// <summary>
    /// Fixes one problem: removes the mark, resets permissions, or unpacks
    /// the archive. Does nothing for problems that can't be fixed here.
    /// </summary>
    Task<PlaybackFixResult> FixAsync(
        PlaybackError error,
        CancellationToken cancellationToken = default);

    /// <summary>Whether <see cref="FixAsync"/> can do anything for this kind.</summary>
    bool CanFix(
        PlaybackErrorKind kind);

    /// <summary>
    /// Puts back the permissions a file had before the last permission fix.
    /// </summary>
    Task<PlaybackFixResult> UndoPermissionsAsync(
        string path);

    /// <summary>Files with saved permissions that can be put back.</summary>
    IReadOnlyCollection<string> GetUndoablePaths();
}
