using System.Text.Json;

namespace Simplifiarr.Core.Utilities;

/// <summary>
/// The single way Simplifiarr reads and writes its JSON files. Writes go
/// to a temporary file first and are then swapped in, so a crash or power
/// cut mid-write can never leave a half-written file behind. Failures are
/// logged rather than thrown: a cache that can't be read is treated as
/// empty, and a cache that can't be saved is rebuilt by the next scan.
/// </summary>
public static class JsonFileStore
{
    private static readonly JsonSerializerOptions Compact = new();

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Returns null when the file is missing, empty or unreadable.
    /// </summary>
    public static T? Load<T>(string path)
        where T : class
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream =
                new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);

            if (stream.Length == 0)
            {
                return null;
            }

            return JsonSerializer.Deserialize<T>(stream, Compact);
        }
        catch (Exception exception)
        {
            AppLog.Error($"Could not read '{path}'.", exception);

            return null;
        }
    }

    /// <summary>
    /// Returns false (after logging) when the file could not be written.
    /// Use <paramref name="indented"/> for files people open by hand.
    /// </summary>
    public static bool Save<T>(
        string path,
        T value,
        bool indented = false)
    {
        var temporaryPath = path + ".tmp";

        try
        {
            var directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var stream =
                   new FileStream(
                       temporaryPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None))
            {
                JsonSerializer.Serialize(
                    stream,
                    value,
                    indented ? Indented : Compact);
            }

            File.Move(temporaryPath, path, overwrite: true);

            return true;
        }
        catch (Exception exception)
        {
            AppLog.Error($"Could not save '{path}'.", exception);

            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // Nothing more to do - the original file is still intact.
            }

            return false;
        }
    }
}
