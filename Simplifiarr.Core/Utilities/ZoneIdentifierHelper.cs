namespace Simplifiarr.Core.Utilities;

/// <summary>
/// Reads/removes the NTFS "Zone.Identifier" alternate data stream Windows
/// attaches to files downloaded from the internet or copied from another
/// computer (the "Mark of the Web") - accessed via the standard
/// "path:StreamName" NTFS syntax, which .NET's own File APIs understand
/// natively, so no PowerShell/process-per-file shell-out is needed.
/// </summary>
public static class ZoneIdentifierHelper
{
    public static bool IsBlocked(
        string path)
    {
        try
        {
            return File.Exists(StreamPath(path));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The Zone.Identifier stream's own HostUrl= line, if present - shows
    /// where Windows thinks the file came from. Null if unblocked, or the
    /// stream doesn't carry a URL (common for files from another PC on the
    /// LAN rather than a real download).
    /// </summary>
    public static string? TryReadSourceUrl(
        string path)
    {
        try
        {
            var streamPath =
                StreamPath(path);

            if (!File.Exists(streamPath))
            {
                return null;
            }

            var content =
                File.ReadAllText(streamPath);

            var hostUrlLine =
                content
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .FirstOrDefault(line =>
                        line.StartsWith("HostUrl=", StringComparison.OrdinalIgnoreCase));

            return hostUrlLine?["HostUrl=".Length..];
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Removes the Zone.Identifier stream (the same effect as
    /// Unblock-File), leaving the file's actual content untouched. Safe to
    /// call on an already-unblocked file - returns true either way.
    /// </summary>
    public static bool Unblock(
        string path)
    {
        try
        {
            var streamPath =
                StreamPath(path);

            if (File.Exists(streamPath))
            {
                File.Delete(streamPath);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string StreamPath(
        string path) =>
        path + ":Zone.Identifier";
}
