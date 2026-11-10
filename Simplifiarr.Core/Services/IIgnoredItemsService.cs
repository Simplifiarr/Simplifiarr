using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IIgnoredItemsService
{
    IReadOnlyList<IgnoredItem> GetAll();

    bool IsIgnored(
        string sourcePath);

    void Add(
        IgnoredItem item);

    /// <summary>
    /// Adds many items with a single save - used when ignoring a whole
    /// season or show at once.
    /// </summary>
    void AddRange(
        IEnumerable<IgnoredItem> items);

    void Remove(
        string sourcePath);

    /// <summary>
    /// Un-ignores several items with one save. Returns how many were removed.
    /// </summary>
    int RemoveRange(
        IEnumerable<string> sourcePaths);
}
