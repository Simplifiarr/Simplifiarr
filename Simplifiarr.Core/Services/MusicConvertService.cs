using System.Diagnostics;
using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public interface IMusicConvertService
{
    event Action? Changed;

    IReadOnlyList<MusicConvertRequest> Queue { get; }

    MusicConvertRequest? Current { get; }

    IReadOnlyList<MusicConvertResult> Results { get; }

    bool IsPaused { get; }

    void Enqueue(IEnumerable<MusicConvertRequest> requests);

    void Pause();

    void Resume();

    void ClearQueue();

    Task<MusicConvertResult> ConvertAsync(MusicConvertRequest request);
}

/// <summary>
/// Converts tracks with ffmpeg, one at a time in the background, keeping
/// their tags (and cover art, except in Opus). The new file is written
/// under a temporary name and only takes its real name once ffmpeg has
/// finished without an error.
/// </summary>
public sealed class MusicConvertService : IMusicConvertService
{
    private readonly IQuarantineService _quarantine;
    private readonly IHistoryService _history;
    private readonly ITorrentClient? _torrents;

    private SeedingCheck? _seeding;
    private DateTime _seedingAtUtc;

    private readonly object _sync = new();
    private readonly List<MusicConvertRequest> _queue = [];
    private readonly List<MusicConvertResult> _results = [];
    private Task _worker = Task.CompletedTask;
    private bool _paused;

    public MusicConvertService(IQuarantineService quarantine, IHistoryService history, ITorrentClient? torrents = null)
    {
        _quarantine = quarantine;
        _history = history;
        _torrents = torrents;
    }

    // The client's torrents, asked at most every two minutes during a batch.
    private async Task<SeedingCheck> SeedingAsync()
    {
        if (_seeding is null || DateTime.UtcNow - _seedingAtUtc > TimeSpan.FromMinutes(2))
        {
            _seeding = await SeedingCheck.CreateAsync(_torrents);
            _seedingAtUtc = DateTime.UtcNow;
        }

        return _seeding;
    }

    public event Action? Changed;

    /// <summary>For tests: where ffmpeg is.</summary>
    public Func<string?> Ffmpeg { get; set; } = FfprobeLocator.FindFfmpeg;

    public IReadOnlyList<MusicConvertRequest> Queue
    {
        get
        {
            lock (_sync)
            {
                return _queue.ToList();
            }
        }
    }

    public MusicConvertRequest? Current { get; private set; }

    public IReadOnlyList<MusicConvertResult> Results
    {
        get
        {
            lock (_sync)
            {
                return _results.AsEnumerable().Reverse().ToList();
            }
        }
    }

    public bool IsPaused => _paused;

    public void Enqueue(IEnumerable<MusicConvertRequest> requests)
    {
        lock (_sync)
        {
            foreach (var request in requests)
            {
                if (!_queue.Any(waiting => string.Equals(waiting.SourcePath, request.SourcePath, StringComparison.OrdinalIgnoreCase)) &&
                    !string.Equals(Current?.SourcePath, request.SourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    _queue.Add(request);
                }
            }

            if (_worker.IsCompleted)
            {
                _worker = Task.Run(WorkAsync);
            }
        }

        Changed?.Invoke();
    }

    public void Pause()
    {
        _paused = true;
        Changed?.Invoke();
    }

    public void Resume()
    {
        _paused = false;

        lock (_sync)
        {
            if (_worker.IsCompleted && _queue.Count > 0)
            {
                _worker = Task.Run(WorkAsync);
            }
        }

        Changed?.Invoke();
    }

    public void ClearQueue()
    {
        lock (_sync)
        {
            _queue.Clear();
        }

        Changed?.Invoke();
    }

    private async Task WorkAsync()
    {
        while (!_paused)
        {
            MusicConvertRequest next;

            lock (_sync)
            {
                if (_queue.Count == 0)
                {
                    break;
                }

                next = _queue[0];
                _queue.RemoveAt(0);
            }

            Current = next;
            Changed?.Invoke();

            MusicConvertResult result;

            try
            {
                result = await ConvertAsync(next);
            }
            catch (Exception exception)
            {
                AppLog.Warn($"Converting {next.SourcePath} failed.", exception);
                result = new MusicConvertResult(next, false, $"{Path.GetFileName(next.SourcePath)}: {exception.Message}", 0);
            }

            lock (_sync)
            {
                _results.Add(result);

                if (_results.Count > 500)
                {
                    _results.RemoveAt(0);
                }
            }

            Current = null;
            Changed?.Invoke();
        }
    }

    /// <summary>Where the converted file goes for this request.</summary>
    public static string TargetPath(MusicConvertRequest request)
    {
        var newName = Path.GetFileNameWithoutExtension(request.SourcePath) + MusicFormats.Extension(request.Format);

        if (request.Mode == MusicConvertMode.OtherFolder && !string.IsNullOrWhiteSpace(request.OutputRoot))
        {
            var relative = Path.GetRelativePath(request.LibraryRoot, Path.GetDirectoryName(request.SourcePath)!);

            return Path.Combine(request.OutputRoot, relative, newName);
        }

        return Path.Combine(Path.GetDirectoryName(request.SourcePath)!, newName);
    }

    public async Task<MusicConvertResult> ConvertAsync(MusicConvertRequest request)
    {
        var source = request.SourcePath;
        var name = Path.GetFileName(source);

        MusicConvertResult Fail(string why) => new(request, false, $"{name}: {why}", 0);

        if (!File.Exists(source))
        {
            return Fail("it isn't there any more.");
        }

        if (!MusicFormats.CanConvert(Path.GetExtension(source), request.Format))
        {
            return Fail($"it can't be made into {MusicFormats.Label(request.Format)}.");
        }

        if (request.Mode == MusicConvertMode.OtherFolder && string.IsNullOrWhiteSpace(request.OutputRoot))
        {
            return Fail("no folder was chosen for the converted files.");
        }

        IReadOnlyList<string> leftovers = [];

        if (request.Mode == MusicConvertMode.Replace)
        {
            var (state, others, torrent) = (await SeedingAsync()).Check(source);

            if (state == SeedState.Seeding)
            {
                return Fail($"the torrent \"{torrent}\" is seeding it, so it was left as it is.");
            }

            if (state == SeedState.Unknown)
            {
                return Fail("it's hardlinked and the torrent client couldn't be asked whether it's seeding, so it was left as it is.");
            }

            // Hardlinked, but no torrent uses the other name: a leftover
            // that has to go too, or replacing frees nothing.
            leftovers = others;
        }

        if (Ffmpeg() is not { } ffmpeg)
        {
            return Fail("ffmpeg isn't installed, so nothing can be converted. Install it with: winget install ffmpeg");
        }

        var target = TargetPath(request);

        if (File.Exists(target))
        {
            return Fail($"{Path.GetFileName(target)} is already there, so nothing was converted.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var partial = target + ".simplifiarr-partial";

        // Cover art first; if the format won't take it, again without.
        var error = await RunAsync(ffmpeg, Arguments(request.Format, source, partial, withArt: request.Format != MusicConvertFormat.Opus160));

        if (error is not null && request.Format != MusicConvertFormat.Opus160)
        {
            TryDelete(partial);
            error = await RunAsync(ffmpeg, Arguments(request.Format, source, partial, withArt: false));
        }

        if (error is not null || !File.Exists(partial) || new FileInfo(partial).Length == 0)
        {
            TryDelete(partial);

            return Fail($"ffmpeg couldn't convert it: {error ?? "it wrote nothing"}. The original is untouched.");
        }

        File.Move(partial, target);

        var before = new FileInfo(source).Length;
        var after = new FileInfo(target).Length;
        long saved = 0;
        string message;
        var success = true;

        if (request.Mode == MusicConvertMode.Replace)
        {
            var quarantined = await _quarantine.QuarantineFileAsync(source);

            if (quarantined is null)
            {
                success = false;
                message = $"{name}: converted to {Path.GetFileName(target)}, but the original couldn't go to Quarantine, so both are there.";
            }
            else
            {
                var stuck = new List<string>();

                foreach (var leftover in leftovers)
                {
                    if (await _quarantine.QuarantineFileAsync(leftover) is null)
                    {
                        stuck.Add(leftover);
                    }
                }

                saved = stuck.Count == 0 ? Math.Max(0, before - after) : 0;
                message =
                    $"{name} replaced by {Path.GetFileName(target)} ({SizeFormatter.FormatBytes(before)} to {SizeFormatter.FormatBytes(after)}); the original is in Quarantine" +
                    (leftovers.Count == 0 ? "."
                        : stuck.Count == 0 ? $", with its leftover hardlink in {Path.GetDirectoryName(leftovers[0])} that no torrent uses."
                        : $". Its leftover hardlink {stuck[0]} couldn't be moved, so the space isn't free yet.");
            }

            await _history.AddRecordAsync(
                new HistoryRecord
                {
                    ActionType = HistoryActionType.MusicConverted,
                    FileName = name,
                    LibraryPath = target,
                    QuarantinePath = quarantined ?? string.Empty,
                    SizeBytes = saved,
                    Success = success,
                    Message = message
                });
        }
        else
        {
            message = $"{name} converted to {target} ({SizeFormatter.FormatBytes(after)}); the original stays.";

            await _history.AddRecordAsync(
                new HistoryRecord
                {
                    ActionType = HistoryActionType.MusicConverted,
                    FileName = name,
                    LibraryPath = target,
                    Success = true,
                    Message = message
                });
        }

        return new MusicConvertResult(request, success, message, saved);
    }

    public static List<string> Arguments(MusicConvertFormat format, string source, string output, bool withArt)
    {
        var arguments = new List<string> { "-v", "error", "-nostdin", "-y", "-i", source, "-map", "0:a:0" };

        if (withArt)
        {
            arguments.AddRange(["-map", "0:v:0?", "-c:v", "copy", "-disposition:v:0", "attached_pic"]);
        }

        arguments.AddRange(["-map_metadata", "0"]);

        arguments.AddRange(
            format switch
            {
                MusicConvertFormat.Mp3Cbr320 => ["-c:a", "libmp3lame", "-b:a", "320k", "-id3v2_version", "3", "-f", "mp3"],
                MusicConvertFormat.Mp3V0 => ["-c:a", "libmp3lame", "-q:a", "0", "-id3v2_version", "3", "-f", "mp3"],
                MusicConvertFormat.Aac256 => ["-c:a", "aac", "-b:a", "256k", "-movflags", "+faststart", "-f", "ipod"],
                MusicConvertFormat.Opus160 => ["-c:a", "libopus", "-b:a", "160k", "-f", "opus"],
                _ => ["-c:a", "flac", "-compression_level", "8", "-f", "flac"]
            });

        arguments.Add(output);

        return arguments;
    }

    // Null when ffmpeg finished cleanly; otherwise its first error.
    private static async Task<string?> RunAsync(string ffmpeg, List<string> arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        arguments.ForEach(start.ArgumentList.Add);

        using var process = Process.Start(start);

        if (process is null)
        {
            return "ffmpeg didn't start";
        }

        var errors = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);

            return "it took over 20 minutes";
        }

        if (process.ExitCode == 0)
        {
            return null;
        }

        var first = (await errors).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

        return first ?? $"ffmpeg stopped with code {process.ExitCode}";
    }

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
