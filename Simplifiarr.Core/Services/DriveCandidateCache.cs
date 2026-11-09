using System.Diagnostics;
using System.Text.Json;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public sealed class CandidateCacheEntry<TCandidate>
{
    public List<TCandidate> Candidates { get; set; } =
        [];

    public DateTime LastScanUtc { get; set; }

    public DateTime LastScanUtcMovies { get; set; }

    public DateTime LastScanUtcTv { get; set; }

    // Only WasteCandidateCacheService populates these. They live here
    // rather than in a second file so waste-cache.json's on-disk shape
    // doesn't change - splitting them out would silently reset the
    // dashboard's healthy-hardlink counter to zero for existing users.
    public int HealthyHardlinkCountMovies { get; set; }

    public int HealthyHardlinkCountTv { get; set; }
}

/// <summary>
/// Shared per-drive scan-result cache backing the Promotion, Book
/// Promotion, Waste, and Junk candidate lists - JSON-persisted under
/// PromotionCache\, one file per consumer, pruning entries whose backing
/// file no longer exists on load.
/// </summary>
internal sealed class DriveCandidateCache<TCandidate>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _sync = new();

    private readonly Dictionary<string, CandidateCacheEntry<TCandidate>> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _cachePath;
    private readonly Func<TCandidate, string> _keySelector;
    private readonly Func<TCandidate, bool> _stillExists;

    public DriveCandidateCache(
        string cacheFileName,
        Func<TCandidate, string> keySelector,
        Func<TCandidate, bool> stillExists)
    {
        _keySelector = keySelector;
        _stillExists = stillExists;

        _cachePath = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData),
            "Simplifiarr",
            "PromotionCache",
            cacheFileName);

        LoadFromDisk();
    }

    public IReadOnlyList<TCandidate> GetCandidates(
        string driveName)
    {
        lock (_sync)
        {
            return _entries.TryGetValue(driveName, out var entry)
                ? entry.Candidates.ToList()
                : [];
        }
    }

    public DateTime? GetLastScanUtc(
        string driveName)
    {
        lock (_sync)
        {
            return _entries.TryGetValue(driveName, out var entry)
                ? entry.LastScanUtc
                : null;
        }
    }

    public DateTime? GetLastScanUtc(
        string driveName,
        MediaKind kind)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(driveName, out var entry))
            {
                return null;
            }

            var value =
                kind == MediaKind.Tv
                    ? entry.LastScanUtcTv
                    : entry.LastScanUtcMovies;

            return value == default ? null : value;
        }
    }

    public void SetLastScanUtc(
        string driveName,
        MediaKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driveName);

        lock (_sync)
        {
            var entry = GetOrCreateEntry(driveName);
            var now = DateTime.UtcNow;

            if (kind != MediaKind.Tv)
            {
                entry.LastScanUtcMovies = now;
            }

            if (kind != MediaKind.Movies)
            {
                entry.LastScanUtcTv = now;
            }

            SaveToDisk();
        }
    }

    public void SetCandidates(
        string driveName,
        IEnumerable<TCandidate> candidates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driveName);
        ArgumentNullException.ThrowIfNull(candidates);

        lock (_sync)
        {
            _entries.TryGetValue(driveName, out var existing);

            _entries[driveName] = new CandidateCacheEntry<TCandidate>
            {
                Candidates = candidates.ToList(),
                LastScanUtc = DateTime.UtcNow,
                LastScanUtcMovies = existing?.LastScanUtcMovies ?? default,
                LastScanUtcTv = existing?.LastScanUtcTv ?? default,
                HealthyHardlinkCountMovies = existing?.HealthyHardlinkCountMovies ?? 0,
                HealthyHardlinkCountTv = existing?.HealthyHardlinkCountTv ?? 0
            };

            SaveToDisk();
        }
    }

    public int GetHealthyHardlinkCount(
        string driveName)
    {
        lock (_sync)
        {
            return _entries.TryGetValue(driveName, out var entry)
                ? entry.HealthyHardlinkCountMovies + entry.HealthyHardlinkCountTv
                : 0;
        }
    }

    public void SetHealthyHardlinkCount(
        string driveName,
        MediaKind kind,
        int count)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driveName);

        lock (_sync)
        {
            var entry = GetOrCreateEntry(driveName);

            if (kind == MediaKind.Tv)
            {
                entry.HealthyHardlinkCountTv = count;
            }
            else
            {
                entry.HealthyHardlinkCountMovies = count;
            }

            SaveToDisk();
        }
    }

    public void IncrementHealthyHardlinkCount(
        string driveName,
        MediaKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driveName);

        lock (_sync)
        {
            var entry = GetOrCreateEntry(driveName);

            if (kind == MediaKind.Tv)
            {
                entry.HealthyHardlinkCountTv++;
            }
            else
            {
                entry.HealthyHardlinkCountMovies++;
            }

            SaveToDisk();
        }
    }

    public void RemoveCandidate(
        string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_sync)
        {
            var changed = false;

            foreach (var entry in _entries.Values)
            {
                changed |= entry.Candidates.RemoveAll(candidate =>
                    string.Equals(
                        _keySelector(candidate),
                        key,
                        StringComparison.OrdinalIgnoreCase)) > 0;
            }

            if (changed)
            {
                SaveToDisk();
            }
        }
    }

    public void Clear(
        string? driveName = null)
    {
        lock (_sync)
        {
            var changed = string.IsNullOrWhiteSpace(driveName)
                ? _entries.Count > 0
                : _entries.Remove(driveName);

            if (string.IsNullOrWhiteSpace(driveName))
            {
                _entries.Clear();
            }

            if (changed)
            {
                SaveToDisk();
            }
        }
    }

    private CandidateCacheEntry<TCandidate> GetOrCreateEntry(
        string driveName)
    {
        if (!_entries.TryGetValue(driveName, out var entry))
        {
            entry = new CandidateCacheEntry<TCandidate>();
            _entries[driveName] = entry;
        }

        return entry;
    }

    private void LoadFromDisk()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_cachePath))
                {
                    return;
                }

                var json =
                    File.ReadAllText(_cachePath);

                var persistedEntries =
                    JsonSerializer.Deserialize<
                        Dictionary<string, CandidateCacheEntry<TCandidate>>>(
                        json,
                        JsonOptions);

                if (persistedEntries is null)
                {
                    return;
                }

                _entries.Clear();

                foreach (var pair in persistedEntries)
                {
                    _entries[pair.Key] = pair.Value;
                }

                var removedStaleCandidates = false;

                foreach (var entry in _entries.Values)
                {
                    removedStaleCandidates |=
                        entry.Candidates.RemoveAll(candidate => !_stillExists(candidate)) > 0;
                }

                if (removedStaleCandidates)
                {
                    SaveToDisk();
                }
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    $"Unable to load candidate cache '{_cachePath}': {exception}");
            }
        }
    }

    private void SaveToDisk()
    {
        try
        {
            var directory =
                Path.GetDirectoryName(_cachePath)!;

            Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(
                _entries,
                JsonOptions);

            var temporaryPath = _cachePath + ".tmp";

            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _cachePath, true);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                $"Unable to save candidate cache '{_cachePath}': {exception}");
        }
    }
}
