using System.Diagnostics;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Utilities;

/// <summary>
/// Writes tags with ffmpeg without touching the music: the audio and cover
/// art are copied as they are into a new file with the new tags, the new
/// file is read back to check, and only then takes the old one's place.
/// </summary>
public static class MusicTagWriter
{
    private static readonly Dictionary<string, string> Muxers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".flac"] = "flac",
            [".mp3"] = "mp3",
            [".m4a"] = "ipod",
            [".ogg"] = "ogg",
            [".opus"] = "opus"
        };

    public static bool CanWrite(string extension) => Muxers.ContainsKey(extension);

    /// <summary>Null when the tags were written; otherwise why not (the file is then unchanged).</summary>
    public static async Task<string?> WriteAsync(string path, MusicTags tags, Simplifiarr.Core.Services.SeedingCheck? seeding = null)
    {
        var extension = Path.GetExtension(path);

        if (!Muxers.TryGetValue(extension, out var muxer))
        {
            return $"Simplifiarr can't write tags to {extension} files.";
        }

        // A seeding file must stay byte for byte the same, or the torrent breaks.
        var (state, _, torrent) = (seeding ?? Simplifiarr.Core.Services.SeedingCheck.From(null)).Check(path);

        if (state == Simplifiarr.Core.Services.SeedState.Seeding)
        {
            return $"The torrent \"{torrent}\" is seeding it, and changing it would break the torrent.";
        }

        if (state == Simplifiarr.Core.Services.SeedState.Unknown)
        {
            return "It is hardlinked and the torrent client couldn't be asked whether it's seeding, so it was left alone.";
        }

        if (FfprobeLocator.FindFfmpeg() is not { } ffmpeg)
        {
            return "ffmpeg isn't installed.";
        }

        var partial = path + ".simplifiarr-partial";

        // Ogg and Opus keep tags on the audio stream; the others on the file.
        var target = extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".opus", StringComparison.OrdinalIgnoreCase)
            ? "-metadata:s:a:0"
            : "-metadata";

        var start = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in new[] { "-v", "error", "-nostdin", "-y", "-i", path, "-map", "0", "-c", "copy", "-map_metadata", "0", "-map_metadata:s:a", "0:s:a" })
        {
            start.ArgumentList.Add(argument);
        }

        void Set(string key, string? value)
        {
            if (value is not null)
            {
                start.ArgumentList.Add(target);
                start.ArgumentList.Add($"{key}={value}");
            }
        }

        Set("title", tags.Title);
        Set("artist", tags.Artist);
        Set("album_artist", tags.AlbumArtist);
        Set("album", tags.Album);
        Set("track", tags.Track?.ToString());
        Set("disc", tags.Disc?.ToString());
        Set("date", tags.Year);

        if (muxer == "mp3")
        {
            start.ArgumentList.Add("-id3v2_version");
            start.ArgumentList.Add("3");
        }

        start.ArgumentList.Add("-f");
        start.ArgumentList.Add(muxer);
        start.ArgumentList.Add(partial);

        try
        {
            using var process = Process.Start(start);

            if (process is null)
            {
                return "ffmpeg didn't start.";
            }

            var errors = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await process.WaitForExitAsync(timeout.Token);

            if (process.ExitCode != 0 || !File.Exists(partial) || new FileInfo(partial).Length == 0)
            {
                TryDelete(partial);

                return $"ffmpeg couldn't write it: {(await errors).Split('\n').FirstOrDefault()?.Trim()}";
            }

            // The new file must be about the same size: only the tags changed.
            var before = new FileInfo(path).Length;
            var after = new FileInfo(partial).Length;

            if (Math.Abs(after - before) > Math.Max(512 * 1024, before / 20))
            {
                TryDelete(partial);

                return $"The rewritten file came out {SizeFormatter.FormatBytes(after)} instead of about {SizeFormatter.FormatBytes(before)}, so it wasn't used.";
            }

            var check = MusicTagReader.Read(partial);

            if (check.Problem is not null || (tags.Title is not null && CanReadBack(extension) && check.Title != tags.Title))
            {
                TryDelete(partial);

                return "The rewritten file didn't read back right, so it wasn't used.";
            }

            if (state == Simplifiarr.Core.Services.SeedState.LinkedNotSeeding)
            {
                // Written into the same file, so its other name (a leftover
                // no torrent uses) gets the new tags too and no space is doubled.
                File.Copy(partial, path, overwrite: true);
                File.Delete(partial);
            }
            else
            {
                File.Move(partial, path, overwrite: true);
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            TryDelete(partial);

            return exception.Message;
        }
    }

    // FLAC and MP3 are read directly; the rest go through ffprobe, which a
    // test can't count on, so their titles aren't compared.
    private static bool CanReadBack(string extension) =>
        extension.Equals(".flac", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
