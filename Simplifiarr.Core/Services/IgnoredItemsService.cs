using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public class IgnoredItemsService
    : IIgnoredItemsService
{
    private readonly object _sync = new();

    private readonly List<IgnoredItem> _items = [];

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
            return _items.Any(item =>
                string.Equals(
                    item.SourcePath,
                    sourcePath,
                    StringComparison.OrdinalIgnoreCase));
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
        }
    }

    private void SaveToDisk() =>
        JsonFileStore.Save(_cachePath, _items);
}
