using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public class QuarantineManagementService : IQuarantineManagementService
{
    private readonly IHistoryService _historyService;

    public QuarantineManagementService(
        IHistoryService historyService)
    {
        _historyService = historyService;
    }

    public async Task<bool> DeleteAsync(string fullPath)
    {
        try
        {
            if (!File.Exists(fullPath))
            {
                return false;
            }

            var fileInfo =
                new FileInfo(fullPath);

            var fileDirectory =
                Path.GetDirectoryName(fullPath);

            if (!RecycleBinHelper.Send(fullPath))
            {
                return false;
            }

            await _historyService.AddRecordAsync(
                new HistoryRecord
                {
                    ActionType = HistoryActionType.QuarantineDeleted,
                    FileName = fileInfo.Name,
                    QuarantinePath = fullPath,
                    SizeBytes = fileInfo.Length,
                    Success = true,
                    Message = "Quarantined file sent to the Recycle Bin."
                });

            CleanupEmptyQuarantineFolders(fileDirectory);

            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> RestoreAsync(
        string quarantinePath,
        string originalLibraryPath)
    {
        try
        {
            if (!File.Exists(quarantinePath) ||
                string.IsNullOrWhiteSpace(originalLibraryPath))
            {
                return false;
            }

            var fileInfo =
                new FileInfo(quarantinePath);

            var fileName =
                fileInfo.Name;

            var sizeBytes =
                fileInfo.Length;

            var quarantineDirectory =
                Path.GetDirectoryName(quarantinePath);

            var targetDirectory =
                Path.GetDirectoryName(originalLibraryPath);

            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            if (File.Exists(originalLibraryPath))
            {
                File.Delete(originalLibraryPath);
            }

            File.Move(quarantinePath, originalLibraryPath);

            await _historyService.AddRecordAsync(
                new HistoryRecord
                {
                    ActionType = HistoryActionType.QuarantineRestored,
                    FileName = fileName,
                    LibraryPath = originalLibraryPath,
                    QuarantinePath = quarantinePath,
                    SizeBytes = sizeBytes,
                    Success = true,
                    Message = "Restored from Quarantine to its original library path."
                });

            CleanupEmptyQuarantineFolders(quarantineDirectory);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void CleanupEmptyQuarantineFolders(
        string? fileDirectory)
    {
        if (string.IsNullOrWhiteSpace(fileDirectory))
        {
            return;
        }

        DeleteDirectoryIfEmpty(fileDirectory);

        var dateDirectory =
            Directory.GetParent(fileDirectory)?.FullName;

        DeleteDirectoryIfEmpty(dateDirectory);
    }

    private static void DeleteDirectoryIfEmpty(
        string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return;
        }

        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        if (Directory.EnumerateFileSystemEntries(directoryPath).Any())
        {
            return;
        }

        Directory.Delete(directoryPath);
    }
}
