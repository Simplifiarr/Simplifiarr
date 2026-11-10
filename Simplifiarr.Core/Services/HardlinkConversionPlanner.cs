using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public class HardlinkConversionPlanner : IHardlinkConversionPlanner
{
    public Task<HardlinkConversionPlan?> CreatePlanAsync(
        DuplicateCandidate candidate)
    {
        var libraryExists =
            File.Exists(candidate.LibraryPath);

        var downloadExists =
            File.Exists(candidate.DownloadPath);

        var libraryRoot =
            Path.GetPathRoot(candidate.LibraryPath) ?? string.Empty;

        var downloadRoot =
            Path.GetPathRoot(candidate.DownloadPath) ?? string.Empty;

        var sameVolume =
            libraryRoot.Equals(
                downloadRoot,
                StringComparison.OrdinalIgnoreCase);

        // Converting means moving the library file aside and linking the
        // download in its place. If both names are the same file there is
        // nothing to gain and the move would take the only copy away.
        var sameFile =
            string.Equals(
                Path.GetFullPath(candidate.LibraryPath),
                Path.GetFullPath(candidate.DownloadPath),
                StringComparison.OrdinalIgnoreCase);

        var canConvert =
            !sameFile &&
            libraryExists &&
            downloadExists &&
            sameVolume &&
            !candidate.IsAlreadyHardlinked;

        var reason =
            sameFile
                ? "The library file and the download are the same file - check this drive's folders in Settings."
            : !libraryExists
                ? "Library file does not exist."
            : !downloadExists
                ? "Download file does not exist."
            : !sameVolume
                ? $"Files are on different volumes ({libraryRoot} vs {downloadRoot})."
            : candidate.IsAlreadyHardlinked
                ? "File is already hardlinked."
            : "Ready for conversion.";

        var plan = new HardlinkConversionPlan
        {
            LibraryPath = candidate.LibraryPath,
            DownloadPath = candidate.DownloadPath,

            FileName = candidate.LibraryFileName,

            SizeBytes = candidate.SizeBytes,
            SizeFormatted =
                SizeFormatter.FormatBytes(candidate.SizeBytes),

            LibraryFileExists = libraryExists,
            DownloadFileExists = downloadExists,

            LibraryRoot = libraryRoot,
            DownloadRoot = downloadRoot,

            SameVolume = sameVolume,

            CanConvert = canConvert,

            Reason = reason
        };

        return Task.FromResult<HardlinkConversionPlan?>(plan);
    }
}

