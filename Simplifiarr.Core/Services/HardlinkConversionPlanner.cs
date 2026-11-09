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

        var canConvert =
            libraryExists &&
            downloadExists &&
            sameVolume &&
            !candidate.IsAlreadyHardlinked;

        var reason =
            !libraryExists
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

