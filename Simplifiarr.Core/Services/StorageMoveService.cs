using System.Security.Cryptography;
using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

/// <summary>One title waiting to move, or moving.</summary>
public sealed record StorageMoveRequest(
    StorageTitle Title,
    string ToLibraryPath);

public sealed record StorageMoveResult(
    StorageMoveRequest Request,
    bool Succeeded,
    string Message,
    DateTime FinishedUtc);

public sealed record StorageMoveProgress(
    StorageMoveRequest Request,
    string Step,
    long BytesDone,
    long BytesTotal);

/// <summary>
/// Moves title folders to another drive one at a time, in the background,
/// and tells Radarr or Sonarr where each went. A title is only removed
/// from its old drive once every file has arrived intact and Radarr or
/// Sonarr has accepted the new folder.
/// </summary>
public interface IStorageMoveService
{
    event Action? Changed;

    StorageMoveProgress? Current { get; }

    IReadOnlyList<StorageMoveRequest> Queue { get; }

    IReadOnlyList<StorageMoveResult> Results { get; }

    bool IsPaused { get; }

    /// <summary>Adds titles to the end of the queue and starts it if idle.</summary>
    void Enqueue(IEnumerable<StorageMoveRequest> requests);

    /// <summary>Takes a title that hasn't started yet off the queue.</summary>
    void Remove(StorageMoveRequest request);

    /// <summary>Finishes the title in progress, then waits.</summary>
    void Pause();

    void Resume();

    /// <summary>Empties the queue. The title in progress finishes.</summary>
    void ClearQueue();

    /// <summary>Completes when the queue is empty and nothing is moving.</summary>
    Task WhenIdleAsync();

    /// <summary>
    /// Removes a title from Radarr or Sonarr (and Seerr, so it can be asked
    /// for again) and sends its folder to the Recycle Bin.
    /// </summary>
    Task<StorageMoveResult> DeleteAsync(StorageTitle title, int seerrMediaId);
}

public class StorageMoveService : IStorageMoveService
{
    // Kept free on the destination on top of the title itself.
    private const long SpareBytes = 2L * 1024 * 1024 * 1024;

    private const int BufferBytes = 4 * 1024 * 1024;

    private readonly IArrPathService _arr;
    private readonly IStorageService _storage;
    private readonly IHistoryService _history;
    private readonly IScanCoordinator _scans;
    private readonly ISeerrClient? _seerr;

    private readonly object _sync = new();
    private readonly List<StorageMoveRequest> _queue = [];
    private readonly List<StorageMoveResult> _results = [];

    private Task _worker = Task.CompletedTask;
    private bool _paused;

    public StorageMoveService(
        IArrPathService arr,
        IStorageService storage,
        IHistoryService history,
        IScanCoordinator scans,
        ISeerrClient? seerr = null)
    {
        _seerr = seerr;
        _arr = arr;
        _storage = storage;
        _history = history;
        _scans = scans;
    }

    /// <summary>Rest between titles, so the drives get a breather.</summary>
    public TimeSpan PauseBetweenTitles { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>For tests: how a folder is removed from its old drive.</summary>
    public Func<string, bool> RemoveOldFolder { get; set; } = RecycleBinHelper.Send;

    /// <summary>For tests, which only have one drive to work on.</summary>
    public bool RequireOtherDrive { get; set; } = true;

    public event Action? Changed;

    public StorageMoveProgress? Current { get; private set; }

    public bool IsPaused
    {
        get { lock (_sync) { return _paused; } }
    }

    public IReadOnlyList<StorageMoveRequest> Queue
    {
        get { lock (_sync) { return _queue.ToList(); } }
    }

    public IReadOnlyList<StorageMoveResult> Results
    {
        get { lock (_sync) { return _results.ToList(); } }
    }

    public void Enqueue(IEnumerable<StorageMoveRequest> requests)
    {
        lock (_sync)
        {
            foreach (var request in requests)
            {
                var alreadyThere =
                    _queue.Any(queued => SamePath(queued.Title.FolderPath, request.Title.FolderPath)) ||
                    (Current is { } current && SamePath(current.Request.Title.FolderPath, request.Title.FolderPath));

                if (!alreadyThere)
                {
                    _queue.Add(request);
                }
            }

            if (_worker.IsCompleted)
            {
                _worker = Task.Run(RunAsync);
            }
        }

        Changed?.Invoke();
    }

    public void Remove(StorageMoveRequest request)
    {
        lock (_sync)
        {
            _queue.Remove(request);
        }

        Changed?.Invoke();
    }

    public void Pause()
    {
        lock (_sync) { _paused = true; }

        Changed?.Invoke();
    }

    public void Resume()
    {
        lock (_sync)
        {
            _paused = false;

            if (_worker.IsCompleted && _queue.Count > 0)
            {
                _worker = Task.Run(RunAsync);
            }
        }

        Changed?.Invoke();
    }

    public void ClearQueue()
    {
        lock (_sync) { _queue.Clear(); }

        Changed?.Invoke();
    }

    public Task WhenIdleAsync()
    {
        lock (_sync) { return _worker; }
    }

    public async Task<StorageMoveResult> DeleteAsync(StorageTitle title, int seerrMediaId)
    {
        var request = new StorageMoveRequest(title, string.Empty);
        var folder = Path.TrimEndingDirectorySeparator(title.FolderPath);

        StorageMoveResult result;

        if (!Directory.Exists(folder))
        {
            result = Fail(request, "Its folder is no longer there. Scan again.");
        }
        else if (Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                 .Any(file => NativeHardlinks.GetIdentity(file) is { LinkCount: > 1 }))
        {
            result = Fail(request, "A file in it is still seeding, so deleting the library copy would free nothing.");
        }
        else
        {
            result = await DeleteFolderAsync(request, folder, seerrMediaId);
        }

        lock (_sync)
        {
            _results.Insert(0, result);
        }

        await _history.AddRecordAsync(
            new HistoryRecord
            {
                ActionType = HistoryActionType.TitleDeleted,
                FileName = title.Name,
                LibraryPath = folder,
                SizeBytes = title.SizeBytes,
                Success = result.Succeeded,
                Message = result.Message
            });

        Changed?.Invoke();

        return result;
    }

    private async Task<StorageMoveResult> DeleteFolderAsync(StorageMoveRequest request, string folder, int seerrMediaId)
    {
        var title = request.Title;
        ArrItem? item = null;

        if (await _arr.IsSetUpAsync(title.Kind))
        {
            try
            {
                item = await _arr.FindByFolderAsync(title.Kind, folder);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                return Fail(request, $"Couldn't reach {AppName(title.Kind)}: {exception.Message}. Nothing was deleted.");
            }

            if (item is not null && await _arr.RemoveAsync(title.Kind, item) is { } refusal)
            {
                return Fail(request, $"{refusal} Nothing was deleted.");
            }
        }

        if (!RemoveOldFolder(folder))
        {
            return Fail(
                request,
                item is null
                    ? "Its folder couldn't be sent to the Recycle Bin."
                    : $"{item.Source} no longer has it, but its folder couldn't be sent to the Recycle Bin; delete {folder} yourself.");
        }

        if (_seerr is not null && seerrMediaId > 0)
        {
            await _seerr.ForgetMediaAsync(seerrMediaId);
        }

        _storage.Forget(title);

        return new StorageMoveResult(
            request,
            true,
            $"Deleted {title.Name}" + (item is null ? string.Empty : $" and removed it from {item.Source}") +
            ". It is in the Recycle Bin until you empty it.",
            DateTime.UtcNow);
    }

    private async Task RunAsync()
    {
        var first = true;

        while (true)
        {
            StorageMoveRequest next;

            lock (_sync)
            {
                if (_paused || _queue.Count == 0)
                {
                    return;
                }

                next = _queue[0];
                _queue.RemoveAt(0);
            }

            if (!first)
            {
                await Task.Delay(PauseBetweenTitles);
            }

            first = false;

            // A scan reads every drive; moving at the same time would make
            // both slow, so wait for it.
            if (_scans.Progress.IsRunning)
            {
                Report(next, "Waiting for the scan to finish", 0, next.Title.SizeBytes);

                await _scans.WhenIdleAsync();
            }

            StorageMoveResult result;

            try
            {
                result = await MoveAsync(next);
            }
            catch (Exception exception)
            {
                AppLog.Warn($"Moving {next.Title.Name} failed.", exception);

                result = Fail(next, $"Something went wrong: {exception.Message}. The title was left where it was.");
            }

            lock (_sync)
            {
                _results.Insert(0, result);
                Current = null;
            }

            await _history.AddRecordAsync(
                new HistoryRecord
                {
                    ActionType = HistoryActionType.TitleMoved,
                    FileName = next.Title.Name,
                    DownloadPath = next.Title.FolderPath,
                    LibraryPath = Path.Combine(next.ToLibraryPath, Path.GetFileName(next.Title.FolderPath)),
                    SizeBytes = next.Title.SizeBytes,
                    Success = result.Succeeded,
                    Message = result.Message
                });

            Changed?.Invoke();
        }
    }

    /// <summary>Moves one title. Never leaves it in two half-places.</summary>
    public async Task<StorageMoveResult> MoveAsync(StorageMoveRequest request)
    {
        var title = request.Title;
        var source = Path.TrimEndingDirectorySeparator(title.FolderPath);
        var target = Path.Combine(request.ToLibraryPath, Path.GetFileName(source));

        Report(request, "Checking", 0, title.SizeBytes);

        if (!Directory.Exists(source))
        {
            return Fail(request, "Its folder is no longer there. Scan again.");
        }

        if (!Directory.Exists(request.ToLibraryPath))
        {
            return Fail(request, $"{request.ToLibraryPath} isn't there.");
        }

        if (RequireOtherDrive &&
            string.Equals(StoragePlanner.RootOf(source), StoragePlanner.RootOf(target), StringComparison.OrdinalIgnoreCase))
        {
            return Fail(request, "It is already on that drive.");
        }

        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            return Fail(request, $"{target} already exists and isn't empty, so nothing was moved.");
        }

        var files =
            Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .ToList();

        var total = files.Sum(file => file.Length);

        // Seeding can have started since the last scan.
        if (files.Any(file => NativeHardlinks.GetIdentity(file.FullName) is { LinkCount: > 1 }))
        {
            return Fail(request, "A file in it is hardlinked elsewhere (most likely still seeding), so moving it would free nothing.");
        }

        var free = new DriveInfo(StoragePlanner.RootOf(target)).AvailableFreeSpace;

        if (free < total + SpareBytes)
        {
            return Fail(request, $"Not enough room: it needs {SizeFormatter.FormatBytes(total)} and {SizeFormatter.FormatBytes(free)} is free.");
        }

        // Ask before copying anything, so a missing or unreachable app
        // stops the move while nothing has changed.
        ArrItem? item = null;

        if (await _arr.IsSetUpAsync(title.Kind))
        {
            try
            {
                item = await _arr.FindByFolderAsync(title.Kind, source);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                return Fail(request, $"Couldn't reach {AppName(title.Kind)}: {exception.Message}. Nothing was moved.");
            }
        }

        var copied = 0L;

        try
        {
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(source, file.FullName);
                var destination = Path.Combine(target, relative);

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                await CopyAndVerifyAsync(file.FullName, destination, done =>
                    Report(request, $"Copying {relative}", copied + done, total));

                copied += file.Length;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            TryDelete(target);

            return Fail(request, $"Copying stopped: {exception.Message}. The copy was removed; the title is still where it was.");
        }

        if (item is not null)
        {
            Report(request, $"Telling {item.Source} the new folder", total, total);

            var refusal = await _arr.SetFolderAsync(title.Kind, item, target, request.ToLibraryPath);

            if (refusal is not null)
            {
                TryDelete(target);

                return Fail(request, $"{refusal} The copy was removed; the title is still where it was.");
            }
        }

        Report(request, "Sending the old folder to the Recycle Bin", total, total);

        var removed = RemoveOldFolder(source);

        if (item is not null)
        {
            await _arr.RescanAsync(title.Kind, item);
        }

        _storage.Forget(title);

        var message =
            $"Moved {title.Name} to {target}" +
            (item is null
                ? await _arr.IsSetUpAsync(title.Kind)
                    ? $" ({AppName(title.Kind)} doesn't have it, so nothing to update)."
                    : "."
                : $" and {item.Source} now uses it.");

        if (!removed)
        {
            message += $" The old folder couldn't be sent to the Recycle Bin; delete {source} yourself.";
        }

        return new StorageMoveResult(request, true, message, DateTime.UtcNow);
    }

    /// <summary>
    /// Copies under a temporary name, reads the copy back and compares
    /// checksums, then gives it its real name.
    /// </summary>
    private static async Task CopyAndVerifyAsync(string from, string to, Action<long> onProgress)
    {
        var partial = to + ".simplifiarr-partial";
        var buffer = new byte[BufferBytes];

        byte[] sourceHash;

        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            await using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes, FileOptions.SequentialScan);
            await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, BufferBytes, FileOptions.SequentialScan);

            long done = 0;
            int read;

            while ((read = await input.ReadAsync(buffer)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read));

                done += read;
                onProgress(done);
            }

            sourceHash = hash.GetHashAndReset();
        }

        byte[] copyHash;

        await using (var check = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes, FileOptions.SequentialScan))
        {
            copyHash = await SHA256.HashDataAsync(check);
        }

        if (!sourceHash.AsSpan().SequenceEqual(copyHash))
        {
            File.Delete(partial);

            throw new InvalidDataException($"The copy of {Path.GetFileName(from)} didn't match the original");
        }

        File.Move(partial, to);
        File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(from));
    }

    private DateTime _lastReportUtc;

    // Copying reports every few megabytes; pages only need a few updates a second.
    private void Report(StorageMoveRequest request, string step, long done, long total)
    {
        var sameStep = Current is { } previous && previous.Request == request && previous.Step == step;

        Current = new StorageMoveProgress(request, step, done, total);

        if (sameStep && done < total && DateTime.UtcNow - _lastReportUtc < TimeSpan.FromMilliseconds(400))
        {
            return;
        }

        _lastReportUtc = DateTime.UtcNow;

        Changed?.Invoke();
    }

    private static StorageMoveResult Fail(StorageMoveRequest request, string message) =>
        new(request, false, message, DateTime.UtcNow);

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Couldn't remove the partial copy at {folder}.", exception);
        }
    }

    private static string AppName(string kind) =>
        kind == StorageKinds.Tv ? "Sonarr" : "Radarr";

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(a),
            Path.TrimEndingDirectorySeparator(b),
            StringComparison.OrdinalIgnoreCase);
}
