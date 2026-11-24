namespace Simplifiarr.Core.Utilities;

/// <summary>
/// Deletes for good, without the Recycle Bin. Used only where the bytes
/// are known to live on somewhere else: a title copied to another drive and
/// checked, or a seed moved and rechecked by the torrent client.
///
/// The Recycle Bin would be no safety net there, and a trap besides: the
/// service runs as Windows' own account, whose Recycle Bin is a hidden
/// folder on the same drive that nobody empties, so the space would never
/// come back.
/// </summary>
public static class FileRemoval
{
    /// <summary>Deletes a file or a whole folder. True when it is gone.</summary>
    public static bool Delete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    ClearReadOnly(file);
                }

                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                ClearReadOnly(path);
                File.Delete(path);
            }

            return !Directory.Exists(path) && !File.Exists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Couldn't delete {path}: {exception.Message}");

            return false;
        }
    }

    /// <summary>
    /// Deletes a name a hardlinked file has in a Recycle Bin, with the small
    /// "$I" file that describes it ("$RABC.mkv" goes with "$IABC.mkv").
    /// </summary>
    public static bool DeleteRecycledName(string path)
    {
        var name = Path.GetFileName(path);

        if (name.StartsWith("$R", StringComparison.OrdinalIgnoreCase))
        {
            var info = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, "$I" + name[2..]);

            if (File.Exists(info))
            {
                Delete(info);
            }
        }

        return Delete(path);
    }

    private static void ClearReadOnly(string file)
    {
        var attributes = File.GetAttributes(file);

        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }
    }
}
