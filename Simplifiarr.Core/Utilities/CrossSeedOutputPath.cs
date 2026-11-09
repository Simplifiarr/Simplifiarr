namespace Simplifiarr.Core.Utilities;

/// <summary>
/// Hardlinks can't cross volumes, so a single configured cross-seed storage
/// path only works for files on that same drive. For a file on any other
/// drive, the storage folder (same leaf name) is created at that drive's
/// root instead.
/// </summary>
public static class CrossSeedOutputPath
{
    public static bool IsSameVolume(
        string pathA,
        string pathB)
    {
        var rootA = Path.GetPathRoot(pathA);
        var rootB = Path.GetPathRoot(pathB);

        return !string.IsNullOrWhiteSpace(rootA) &&
               string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolveRoot(
        string configuredRoot,
        string sourceFilePath)
    {
        if (IsSameVolume(configuredRoot, sourceFilePath))
        {
            return configuredRoot;
        }

        var sourceRoot =
            Path.GetPathRoot(sourceFilePath);

        if (string.IsNullOrWhiteSpace(sourceRoot))
        {
            return configuredRoot;
        }

        var leaf =
            Path.GetFileName(
                configuredRoot.TrimEnd('\\', '/'));

        return Path.Combine(
            sourceRoot,
            string.IsNullOrWhiteSpace(leaf) ? "SimplifiarrCrossSeed" : leaf);
    }
}
