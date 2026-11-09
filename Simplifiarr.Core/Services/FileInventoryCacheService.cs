using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Shared cache of raw directory listings, keyed by folder path. Multiple
/// features (promotion candidates, waste detection, catalog population)
/// read the same cached listing instead of independently re-walking the
/// same folders on disk. Only a forced refresh triggers an actual scan;
/// otherwise callers get whatever was last scanned (by any feature).
/// </summary>
public class FileInventoryCacheService
    : IFileInventoryService
{
    private readonly IFileScannerService _fileScanner;

    private readonly object _sync = new();

    private readonly Dictionary<string, CacheEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _cachePath;

    public FileInventoryCacheService(
        IFileScannerService fileScanner)
    {
        _fileScanner = fileScanner;

        _cachePath = AppPaths.DataFile("file-inventory-cache.json");

        LoadFromDisk();
    }

    public async Task<IReadOnlyList<FileRecord>> GetInventoryAsync(
        string folderPath,
        bool forceRefresh = false)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return [];
        }

        var key =
            NormalizeKey(folderPath);

        if (!forceRefresh)
        {
            lock (_sync)
            {
                if (_entries.TryGetValue(key, out var cached))
                {
                    return cached.Files.ToList();
                }
            }

            return [];
        }

        var files =
            await _fileScanner.ScanAsync(folderPath);

        lock (_sync)
        {
            _entries[key] = new CacheEntry
            {
                Files = files,
                ScannedUtc = DateTime.UtcNow
            };

            SaveToDisk();
        }

        return files;
    }

    public DateTime? GetLastScanUtc(
        string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return null;
        }

        lock (_sync)
        {
            return _entries.TryGetValue(
                NormalizeKey(folderPath),
                out var entry)
                    ? entry.ScannedUtc
                    : null;
        }
    }

    private static string NormalizeKey(
        string folderPath)
    {
        return Path.GetFullPath(folderPath)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
    }

    private void LoadFromDisk()
    {
        lock (_sync)
        {
            var persistedEntries =
                JsonFileStore.Load<Dictionary<string, CacheEntry>>(_cachePath);

            if (persistedEntries is null)
            {
                return;
            }

            _entries.Clear();

            foreach (var pair in persistedEntries)
            {
                _entries[pair.Key] = pair.Value;
            }
        }
    }

    private void SaveToDisk() =>
        JsonFileStore.Save(_cachePath, _entries);

    private sealed class CacheEntry
    {
        public List<FileRecord> Files { get; set; } =
            [];

        public DateTime ScannedUtc { get; set; }
    }
}
