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

    private ScanSession? _session;

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

        ScanSession? session;

        lock (_sync)
        {
            session = _session;

            if (session is not null)
            {
                var alreadyRead =
                    TryAnswerFromSession(session, key);

                if (alreadyRead is not null)
                {
                    return alreadyRead;
                }
            }
        }

        var filesReadBefore =
            session?.FilesRead ?? 0;

        var files =
            await _fileScanner.ScanAsync(
                folderPath,
                session?.CancellationToken ?? default,
                session is null
                    ? null
                    : count => session.Report(filesReadBefore + count));

        lock (_sync)
        {
            _entries[key] = new CacheEntry
            {
                Files = files,
                ScannedUtc = DateTime.UtcNow
            };

            if (session is not null && ReferenceEquals(session, _session))
            {
                session.ReadKeys.Add(key);
                session.FilesRead = filesReadBefore + files.Count;
            }
            else
            {
                SaveToDisk();
            }
        }

        return files;
    }

    /// <summary>
    /// Inside a session: the folder itself was already read, or a folder
    /// that contains it was - in which case its listing is just the part
    /// of the parent's listing that sits under it. Caller holds the lock.
    /// </summary>
    private List<FileRecord>? TryAnswerFromSession(
        ScanSession session,
        string key)
    {
        if (session.ReadKeys.Contains(key))
        {
            return _entries[key].Files.ToList();
        }

        var prefix = key + Path.DirectorySeparatorChar;

        foreach (var parentKey in session.ReadKeys)
        {
            if (!prefix.StartsWith(
                    parentKey + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parent = _entries[parentKey];

            var files =
                parent.Files
                    .Where(file =>
                        file.FullPath.StartsWith(
                            prefix,
                            StringComparison.OrdinalIgnoreCase))
                    .ToList();

            _entries[key] = new CacheEntry
            {
                Files = files,
                ScannedUtc = parent.ScannedUtc
            };

            session.ReadKeys.Add(key);

            return files.ToList();
        }

        return null;
    }

    public IDisposable BeginScanSession(
        CancellationToken cancellationToken = default,
        Action<long>? onFilesRead = null)
    {
        lock (_sync)
        {
            if (_session is not null)
            {
                throw new InvalidOperationException(
                    "A scan session is already running.");
            }

            _session =
                new ScanSession(this, cancellationToken, onFilesRead);

            return _session;
        }
    }

    private void EndScanSession(
        ScanSession session)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(session, _session))
            {
                return;
            }

            _session = null;

            if (session.ReadKeys.Count > 0)
            {
                SaveToDisk();
            }
        }
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

    private sealed class ScanSession(
        FileInventoryCacheService owner,
        CancellationToken cancellationToken,
        Action<long>? onFilesRead) : IDisposable
    {
        public CancellationToken CancellationToken { get; } = cancellationToken;

        public HashSet<string> ReadKeys { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public long FilesRead { get; set; }

        public void Report(long filesRead) =>
            onFilesRead?.Invoke(filesRead);

        public void Dispose() =>
            owner.EndScanSession(this);
    }
}
