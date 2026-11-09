namespace Simplifiarr.Core.Utilities;

public static class PathContainment
{
    public static bool IsPathUnderRoot(
        string path,
        string root)
    {
        if (string.IsNullOrWhiteSpace(root) ||
            string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalizedPath =
            Path.GetFullPath(path)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);

        var normalizedRoot =
            Path.GetFullPath(root)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);

        return normalizedPath.Equals(
                   normalizedRoot,
                   StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(
                   normalizedRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }
}
