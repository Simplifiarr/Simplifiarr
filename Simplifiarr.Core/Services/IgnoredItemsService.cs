using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public class IgnoredItemsService
    : IIgnoredItemsService
{
    private readonly object _sync = new();

    private readonly List<IgnoredItem> _items = [];

    private readonly HashSet<string> _ignoredPaths =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _cachePath;

    public IgnoredItemsService()
    {
        _cachePath = AppPaths.DataFile("ignored-items.json");

        LoadFromDisk();
    }

    public IReadOnlyList<IgnoredItem> GetAll()
    {
        lock (_sync)
        {
            return _items
                .OrderByDescending(item => item.IgnoredUtc)
                .ToList();
        }
    }

    public bool IsIgnored(
        string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return false;
        }

        lock (_sync)
        {
            return _ignoredPaths.Contains(sourcePath);
        }
    }

    // IsIgnored is asked once per file on every scan, so it answers from
    // this set rather than searching the whole list each time. Rebuilt
    // whenever the list changes. Caller holds the lock.
    private void RebuildIndex()
    {
        _ignoredPaths.Clear();

        foreach (var item in _items)
        {
            if (!string.IsNullOrWhiteSpace(item.SourcePath))
            {
                _ignoredPaths.Add(item.SourcePath);
            }
        }
    }

    public void Add(
        IgnoredItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.SourcePath);

        lock (_sync)
        {
            _items.RemoveAll(existing =>
                string.Equals(
                    existing.SourcePath,
                    item.SourcePath,
                    StringComparison.OrdinalIgnoreCase));

            _items.Add(item);

            RebuildIndex();
            SaveToDisk();
        }
    }

    public void AddRange(
        IEnumerable<IgnoredItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var list =
            items.ToList();

        if (list.Count == 0)
        {
            return;
        }

        lock (_sync)
        {
            var paths =
                list
                    .Select(item => item.SourcePath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _items.RemoveAll(existing =>
                paths.Contains(existing.SourcePath));

            _items.AddRange(list);

            RebuildIndex();
            SaveToDisk();
        }
    }

    public void Remove(
        string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        lock (_sync)
        {
            var removed =
                _items.RemoveAll(item =>
                    string.Equals(
                        item.SourcePath,
                        sourcePath,
                        StringComparison.OrdinalIgnoreCase)) > 0;

            if (removed)
            {
                RebuildIndex();
                SaveToDisk();
            }
        }
    }

    private void LoadFromDisk()
    {
        lock (_sync)
        {
            var persisted =
                JsonFileStore.Load<List<IgnoredItem>>(_cachePath);

            if (persisted is null)
            {
                return;
            }

            _items.Clear();
            _items.AddRange(persisted);

            RebuildIndex();
        }
    }

    private void SaveToDisk() =>
        JsonFileStore.Save(_cachePath, _items);
}
