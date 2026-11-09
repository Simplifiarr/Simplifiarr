using System.Diagnostics;
using System.Text.Json;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class IgnoredItemsService
    : IIgnoredItemsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _sync = new();

    private readonly List<IgnoredItem> _items = [];

    private readonly string _cachePath;

    public IgnoredItemsService()
    {
        _cachePath = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData),
            "Simplifiarr",
            "PromotionCache",
            "ignored-items.json");

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
            try
            {
                if (!File.Exists(_cachePath))
                {
                    return;
                }

                var json =
                    File.ReadAllText(_cachePath);

                var persisted =
                    JsonSerializer.Deserialize<List<IgnoredItem>>(
                        json,
                        JsonOptions);

                if (persisted is null)
                {
                    return;
                }

                _items.Clear();
                _items.AddRange(persisted);
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    $"Unable to load ignored items: {exception}");
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
                _items,
                JsonOptions);

            var temporaryPath = _cachePath + ".tmp";

            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _cachePath, true);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                $"Unable to save ignored items: {exception}");
        }
    }
}
