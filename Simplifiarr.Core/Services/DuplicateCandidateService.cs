using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public class DuplicateCandidateService : IDuplicateCandidateService
{
    private readonly IFileInventoryService _fileInventory;
    private readonly IHardlinkService _hardlinkService;

    public DuplicateCandidateService(
        IFileInventoryService fileInventory,
        IHardlinkService hardlinkService)
    {
        _fileInventory = fileInventory;
        _hardlinkService = hardlinkService;
    }

    public async Task<List<DuplicateCandidate>> FindVideoCandidatesAsync(
        string libraryPath,
        string downloadPath,
        bool forceRefresh = false)
    {
        var libraryFiles = await _fileInventory.GetInventoryAsync(libraryPath, forceRefresh);
        var downloadFiles = await _fileInventory.GetInventoryAsync(downloadPath, forceRefresh);

        // If the library folder contains the download folder (say the
        // library is set to a whole drive), the downloads show up in the
        // library listing too. Those are downloads, not library copies -
        // counting them would pair every download with itself and report
        // it as a wasted duplicate.
        var libraryVideos = libraryFiles
            .Where(file =>
                file.FileType == FileType.Video &&
                !PathContainment.IsPathUnderRoot(file.FullPath, downloadPath))
            .ToList();

        var downloadVideosBySize = downloadFiles
            .Where(file => file.FileType == FileType.Video)
            .GroupBy(file => file.SizeBytes)
            .ToDictionary(group => group.Key, group => group.ToList());

        var candidates = new List<DuplicateCandidate>();

        foreach (var libraryFile in libraryVideos)
        {
            if (!downloadVideosBySize.TryGetValue(libraryFile.SizeBytes, out var matchingDownloads))
            {
                continue;
            }

            var libraryHardlinkCount = await _hardlinkService.GetHardlinkCountAsync(libraryFile.FullPath);

            foreach (var downloadFile in matchingDownloads)
            {
                // A file is never a copy of itself.
                if (string.Equals(
                        libraryFile.FullPath,
                        downloadFile.FullPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                candidates.Add(new DuplicateCandidate
                {
                    LibraryPath = libraryFile.FullPath,
                    DownloadPath = downloadFile.FullPath,
                    LibraryFileName = libraryFile.FileName,
                    DownloadFileName = downloadFile.FileName,
                    SizeBytes = libraryFile.SizeBytes,
                    LibraryHardlinkCount = libraryHardlinkCount
                });
            }
        }

        return candidates;
    }
}
