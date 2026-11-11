using System.Text.RegularExpressions;

namespace Simplifiarr.Core.Utilities;

/// <summary>
/// Recognises packed releases: the first file of each archive set, which
/// is the one 7-Zip is pointed at. A set split over many files (.rar with
/// .r00, .r01 ..., or .part01.rar, .part02.rar ..., or .001, .002 ...) is
/// reported once, with the size of all its parts.
/// </summary>
public static partial class ArchiveSets
{
    private static readonly string[] SingleFileExtensions =
        [".rar", ".zip", ".7z", ".iso", ".img"];

    [GeneratedRegex(@"\.part0*(\d+)\.rar$", RegexOptions.IgnoreCase)]
    private static partial Regex PartRar();

    [GeneratedRegex(@"\.(r\d{2,3}|\d{3})$", RegexOptions.IgnoreCase)]
    private static partial Regex LaterVolume();

    /// <summary>
    /// True when this file is the one to open for its archive set.
    /// </summary>
    public static bool IsFirstVolume(
        string path)
    {
        var name = Path.GetFileName(path);

        if (PartRar().Match(name) is { Success: true } part)
        {
            return int.Parse(part.Groups[1].Value) == 1;
        }

        if (name.EndsWith(".001", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return SingleFileExtensions.Contains(
            Path.GetExtension(name),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True for any file that belongs to an archive set, first or not.
    /// </summary>
    public static bool IsArchivePart(
        string path) =>
        IsFirstVolume(path) ||
        PartRar().IsMatch(path) ||
        LaterVolume().IsMatch(path);

    /// <summary>
    /// The total size of every part of the set this first volume opens.
    /// </summary>
    public static long SetSize(
        string firstVolume,
        IEnumerable<(string Path, long Size)> filesInFolder)
    {
        var name = Path.GetFileName(firstVolume);

        string stem;

        if (PartRar().Match(name) is { Success: true } part)
        {
            stem = name[..part.Index];
        }
        else if (name.EndsWith(".001", StringComparison.OrdinalIgnoreCase))
        {
            stem = name[..^4];
        }
        else
        {
            stem = Path.GetFileNameWithoutExtension(name);
        }

        var total = 0L;

        foreach (var (path, size) in filesInFolder)
        {
            var other = Path.GetFileName(path);

            if (other.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase) &&
                IsArchivePart(other))
            {
                total += size;
            }
        }

        return Math.Max(total, 1);
    }
}
