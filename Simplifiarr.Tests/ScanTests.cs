using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;

namespace Simplifiarr.Tests;

/// <summary>
/// Wraps the real file scanner and counts how many times each folder is
/// actually read from disk.
/// </summary>
internal sealed class CountingFileScanner : IFileScannerService
{
    private readonly FileScannerService _inner = new();

    public List<string> Walked { get; } = [];

    public Task<List<FileRecord>> ScanAsync(
        string path,
        CancellationToken cancellationToken = default,
        Action<int>? onProgress = null)
    {
        lock (Walked)
        {
            Walked.Add(path);
        }

        return _inner.ScanAsync(path, cancellationToken, onProgress);
    }
}

internal sealed class FixedSettingsService(params DriveProfile[] profiles)
    : ISettingsService
{
    public Task<SimplifiarrSettings> GetSettingsAsync() =>
        Task.FromResult(new SimplifiarrSettings { DriveProfiles = profiles.ToList() });

    public Task<List<DriveProfile>> GetEnabledDriveProfilesAsync() =>
        Task.FromResult(profiles.Where(profile => profile.Enabled).ToList());

    public Task SaveSettingsAsync(SimplifiarrSettings settings) =>
        Task.CompletedTask;
}

internal sealed class FixedSteps(params ScanStep[] steps) : IScanStepProvider
{
    public IReadOnlyList<ScanStep> GetSteps() => steps;
}

[TestClass]
public class FileScannerServiceTests : TempFolderTest
{
    private readonly FileScannerService _scanner = new();

    [TestMethod]
    public async Task Lists_every_file_in_every_subfolder_with_its_size_and_type()
    {
        WriteFile(@"lib\Movie (2020)\Movie (2020).mkv", "12345");
        WriteFile(@"lib\Movie (2020)\poster.jpg");
        WriteFile(@"lib\Show\Season 01\Show - S01E01.mkv");

        var files = await _scanner.ScanAsync(Path.Combine(Folder, "lib"));

        Assert.HasCount(3, files);

        var movie = files.Single(file => file.FileName == "Movie (2020).mkv");

        Assert.AreEqual(5, movie.SizeBytes);
        Assert.AreEqual(FileType.Video, movie.FileType);
        Assert.AreEqual(
            Path.Combine(Folder, "lib", "Movie (2020)", "Movie (2020).mkv"),
            movie.FullPath);
    }

    [TestMethod]
    public async Task Hidden_files_are_still_listed()
    {
        var hidden = WriteFile(@"lib\hidden.mkv");

        File.SetAttributes(hidden, FileAttributes.Hidden);

        var files = await _scanner.ScanAsync(Path.Combine(Folder, "lib"));

        Assert.HasCount(1, files);
    }

    [TestMethod]
    public async Task A_missing_folder_is_an_empty_listing()
    {
        Assert.IsEmpty(
            await _scanner.ScanAsync(Path.Combine(Folder, "nope")));
    }

    [TestMethod]
    public async Task Reports_how_many_files_it_has_read()
    {
        WriteFile(@"lib\a.mkv");
        WriteFile(@"lib\b.mkv");

        var reported = 0;

        await _scanner.ScanAsync(
            Path.Combine(Folder, "lib"),
            onProgress: count => reported = count);

        Assert.AreEqual(2, reported);
    }

    [TestMethod]
    public async Task Stops_when_cancelled()
    {
        WriteFile(@"lib\a.mkv");

        using var cancellation = new CancellationTokenSource();

        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _scanner.ScanAsync(Path.Combine(Folder, "lib"), cancellation.Token));
    }
}

[TestClass]
public class FileInventorySessionTests : TempFolderTest
{
    [TestMethod]
    public async Task Outside_a_scan_every_refresh_reads_the_disk()
    {
        WriteFile(@"downloads\a.mkv");

        var scanner = new CountingFileScanner();
        var inventory = new FileInventoryCacheService(scanner);
        var downloads = Path.Combine(Folder, "downloads");

        await inventory.GetInventoryAsync(downloads, forceRefresh: true);
        await inventory.GetInventoryAsync(downloads, forceRefresh: true);

        Assert.HasCount(2, scanner.Walked);
    }

    [TestMethod]
    public async Task During_a_scan_a_folder_is_read_from_disk_only_once()
    {
        WriteFile(@"downloads\a.mkv");

        var scanner = new CountingFileScanner();
        var inventory = new FileInventoryCacheService(scanner);
        var downloads = Path.Combine(Folder, "downloads");

        using (inventory.BeginScanSession())
        {
            // Three different features all asking for a fresh listing.
            var first = await inventory.GetInventoryAsync(downloads, forceRefresh: true);
            var second = await inventory.GetInventoryAsync(downloads, forceRefresh: true);
            var third = await inventory.GetInventoryAsync(downloads + @"\", forceRefresh: true);

            Assert.HasCount(1, first);
            Assert.HasCount(1, second);
            Assert.HasCount(1, third);
        }

        Assert.HasCount(1, scanner.Walked);
    }

    [TestMethod]
    public async Task A_folder_inside_one_already_read_is_not_read_again()
    {
        WriteFile(@"downloads\movie.mkv");
        WriteFile(@"downloads\audiobooks\book.m4b");
        WriteFile(@"downloads\audiobooks-other\other.m4b");

        var scanner = new CountingFileScanner();
        var inventory = new FileInventoryCacheService(scanner);
        var downloads = Path.Combine(Folder, "downloads");
        var audiobooks = Path.Combine(downloads, "audiobooks");

        using (inventory.BeginScanSession())
        {
            await inventory.GetInventoryAsync(downloads, forceRefresh: true);

            var inside = await inventory.GetInventoryAsync(audiobooks, forceRefresh: true);

            // Only what is really under "audiobooks" - not the sibling
            // folder whose name merely starts the same way.
            Assert.HasCount(1, inside);
            Assert.AreEqual("book.m4b", inside[0].FileName);
        }

        Assert.HasCount(1, scanner.Walked);

        // And the listing is remembered afterwards, like any other.
        Assert.HasCount(1, await inventory.GetInventoryAsync(audiobooks));
    }

    [TestMethod]
    public async Task What_a_scan_read_is_still_there_after_a_restart()
    {
        WriteFile(@"downloads\a.mkv");

        var downloads = Path.Combine(Folder, "downloads");
        var first = new FileInventoryCacheService(new CountingFileScanner());

        using (first.BeginScanSession())
        {
            await first.GetInventoryAsync(downloads, forceRefresh: true);
        }

        var second = new FileInventoryCacheService(new CountingFileScanner());

        Assert.HasCount(1, await second.GetInventoryAsync(downloads));
        Assert.IsNotNull(second.GetLastScanUtc(downloads));
    }

    [TestMethod]
    public async Task A_scan_reports_the_running_total_of_files_read()
    {
        WriteFile(@"movies\a.mkv");
        WriteFile(@"tv\b.mkv");
        WriteFile(@"tv\c.mkv");

        var inventory = new FileInventoryCacheService(new CountingFileScanner());
        long total = 0;

        using (inventory.BeginScanSession(onFilesRead: count => total = count))
        {
            await inventory.GetInventoryAsync(Path.Combine(Folder, "movies"), forceRefresh: true);
            await inventory.GetInventoryAsync(Path.Combine(Folder, "tv"), forceRefresh: true);
        }

        Assert.AreEqual(3, total);
    }

    [TestMethod]
    public void Only_one_scan_can_run_at_a_time()
    {
        var inventory = new FileInventoryCacheService(new CountingFileScanner());

        using var session = inventory.BeginScanSession();

        Assert.ThrowsExactly<InvalidOperationException>(
            () => inventory.BeginScanSession());
    }
}

[TestClass]
public class ScanCoordinatorTests : TempFolderTest
{
    private DriveProfile Drive(string name)
    {
        WriteFile($@"{name}\downloads\movie.mkv");
        WriteFile($@"{name}\downloads\audiobooks\book.m4b");
        WriteFile($@"{name}\movies\Movie (2020)\Movie (2020).mkv");

        return new DriveProfile
        {
            Name = name,
            DownloadPath = Path.Combine(Folder, name, "downloads"),
            AudiobookDownloadPath = Path.Combine(Folder, name, "downloads", "audiobooks"),
            MovieLibraryPath = Path.Combine(Folder, name, "movies"),
            TvLibraryPath = Path.Combine(Folder, name, "tv-does-not-exist")
        };
    }

    [TestMethod]
    public async Task A_scan_reads_each_folder_once_then_runs_every_step_for_every_drive()
    {
        var scanner = new CountingFileScanner();
        var inventory = new FileInventoryCacheService(scanner);
        var ran = new List<string>();

        // Each step asks for fresh listings, exactly as the real features do.
        ScanStep Step(string name) =>
            new(name, async profile =>
            {
                await inventory.GetInventoryAsync(profile.DownloadPath, forceRefresh: true);
                await inventory.GetInventoryAsync(profile.MovieLibraryPath, forceRefresh: true);
                await inventory.GetInventoryAsync(profile.AudiobookDownloadPath, forceRefresh: true);

                ran.Add($"{profile.Name}:{name}");
            });

        var coordinator = new ScanCoordinator(
            new FixedSettingsService(Drive("C"), Drive("D")),
            inventory,
            new FixedSteps(Step("link"), Step("copies"), Step("leftovers")));

        Assert.IsTrue(coordinator.Start());

        await coordinator.WhenIdleAsync();

        CollectionAssert.AreEqual(
            new[]
            {
                "C:link", "C:copies", "C:leftovers",
                "D:link", "D:copies", "D:leftovers"
            },
            ran);

        // Two real folders per drive (downloads, movies). The audiobook
        // folder sits inside downloads, and the TV folder doesn't exist.
        Assert.HasCount(4, scanner.Walked);

        var summary = coordinator.LastSummary!;

        Assert.IsFalse(summary.Cancelled);
        Assert.IsEmpty(summary.Problems);
        Assert.AreEqual(6, summary.FilesRead);
        CollectionAssert.AreEqual(new[] { "C", "D" }, summary.Drives);
        Assert.IsNotNull(coordinator.GetLastScanUtc("C"));
        Assert.IsFalse(coordinator.Progress.IsRunning);
    }

    [TestMethod]
    public async Task Scanning_one_drive_leaves_the_others_alone()
    {
        var ran = new List<string>();

        var coordinator = new ScanCoordinator(
            new FixedSettingsService(Drive("C"), Drive("D")),
            new FileInventoryCacheService(new CountingFileScanner()),
            new FixedSteps(new ScanStep("link", profile =>
            {
                ran.Add(profile.Name);
                return Task.CompletedTask;
            })));

        coordinator.Start("d");

        await coordinator.WhenIdleAsync();

        CollectionAssert.AreEqual(new[] { "D" }, ran);
        Assert.IsNull(coordinator.GetLastScanUtc("C"));
    }

    [TestMethod]
    public async Task A_disabled_drive_is_not_scanned()
    {
        var disabled = Drive("C");

        disabled.Enabled = false;

        var ran = new List<string>();

        var coordinator = new ScanCoordinator(
            new FixedSettingsService(disabled, Drive("D")),
            new FileInventoryCacheService(new CountingFileScanner()),
            new FixedSteps(new ScanStep("link", profile =>
            {
                ran.Add(profile.Name);
                return Task.CompletedTask;
            })));

        coordinator.Start();

        await coordinator.WhenIdleAsync();

        CollectionAssert.AreEqual(new[] { "D" }, ran);
    }

    [TestMethod]
    public async Task One_failing_step_does_not_stop_the_rest()
    {
        var ran = new List<string>();

        var coordinator = new ScanCoordinator(
            new FixedSettingsService(Drive("C")),
            new FileInventoryCacheService(new CountingFileScanner()),
            new FixedSteps(
                new ScanStep("link", _ => throw new InvalidOperationException("qBittorrent said no")),
                new ScanStep("copies", _ =>
                {
                    ran.Add("copies");
                    return Task.CompletedTask;
                })));

        coordinator.Start();

        await coordinator.WhenIdleAsync();

        CollectionAssert.AreEqual(new[] { "copies" }, ran);
        Assert.HasCount(1, coordinator.LastSummary!.Problems);
        StringAssert.Contains(coordinator.LastSummary.Problems[0], "qBittorrent said no");
        StringAssert.Contains(coordinator.LastSummary.Problems[0], "link");
    }

    [TestMethod]
    public async Task Cancelling_stops_before_the_next_step_and_a_second_scan_cannot_start_meanwhile()
    {
        var firstStepStarted = new TaskCompletionSource();
        var letFirstStepFinish = new TaskCompletionSource();
        var ran = new List<string>();

        var coordinator = new ScanCoordinator(
            new FixedSettingsService(Drive("C"), Drive("D")),
            new FileInventoryCacheService(new CountingFileScanner()),
            new FixedSteps(
                new ScanStep("link", async _ =>
                {
                    ran.Add("link");
                    firstStepStarted.TrySetResult();
                    await letFirstStepFinish.Task;
                }),
                new ScanStep("copies", _ =>
                {
                    ran.Add("copies");
                    return Task.CompletedTask;
                })));

        coordinator.Start();

        await firstStepStarted.Task;

        Assert.IsTrue(coordinator.Progress.IsRunning);
        Assert.AreEqual("C", coordinator.Progress.DriveName);
        Assert.AreEqual("link", coordinator.Progress.StepName);
        Assert.IsFalse(coordinator.Start(), "a second scan must not start while one is running");

        coordinator.Cancel();

        Assert.IsTrue(coordinator.Progress.CancelRequested);

        letFirstStepFinish.SetResult();

        await coordinator.WhenIdleAsync();

        CollectionAssert.AreEqual(new[] { "link" }, ran);
        Assert.IsTrue(coordinator.LastSummary!.Cancelled);
        Assert.IsFalse(coordinator.Progress.IsRunning);

        // A cancelled drive is not marked as scanned.
        Assert.IsNull(coordinator.GetLastScanUtc("C"));

        // And the next scan can start normally.
        Assert.IsTrue(coordinator.Start());

        await coordinator.WhenIdleAsync();
    }

    [TestMethod]
    public async Task Pages_are_told_when_a_scan_starts_and_ends()
    {
        var coordinator = new ScanCoordinator(
            new FixedSettingsService(Drive("C")),
            new FileInventoryCacheService(new CountingFileScanner()),
            new FixedSteps());

        var sawRunning = false;
        var notifications = 0;

        coordinator.Changed += () =>
        {
            Interlocked.Increment(ref notifications);

            if (coordinator.Progress.IsRunning)
            {
                sawRunning = true;
            }
        };

        coordinator.Start();

        await coordinator.WhenIdleAsync();

        Assert.IsTrue(sawRunning);
        Assert.IsGreaterThanOrEqualTo(2, notifications);
    }

    [TestMethod]
    public async Task The_last_scan_is_remembered_after_a_restart()
    {
        var settings = new FixedSettingsService(Drive("C"));

        var first = new ScanCoordinator(
            settings,
            new FileInventoryCacheService(new CountingFileScanner()),
            new FixedSteps());

        first.Start();

        await first.WhenIdleAsync();

        var second = new ScanCoordinator(
            settings,
            new FileInventoryCacheService(new CountingFileScanner()),
            new FixedSteps());

        Assert.IsNotNull(second.GetLastScanUtc("C"));
        Assert.IsNotNull(second.LastSummary);
    }
}

[TestClass]
public class IgnoredItemsServiceTests : TempFolderTest
{
    [TestMethod]
    public void Ignoring_and_un_ignoring_a_file()
    {
        var service = new IgnoredItemsService();

        Assert.IsFalse(service.IsIgnored(@"D:\dl\a.mkv"));

        service.Add(new IgnoredItem { SourcePath = @"D:\dl\a.mkv" });

        Assert.IsTrue(service.IsIgnored(@"d:\DL\A.MKV"));
        Assert.IsFalse(service.IsIgnored(@"D:\dl\b.mkv"));

        service.Remove(@"D:\dl\a.mkv");

        Assert.IsFalse(service.IsIgnored(@"D:\dl\a.mkv"));
    }

    [TestMethod]
    public void Ignored_files_are_remembered_after_a_restart()
    {
        new IgnoredItemsService().AddRange(
        [
            new IgnoredItem { SourcePath = @"D:\dl\a.mkv" },
            new IgnoredItem { SourcePath = @"D:\dl\b.mkv" }
        ]);

        var restarted = new IgnoredItemsService();

        Assert.IsTrue(restarted.IsIgnored(@"D:\dl\a.mkv"));
        Assert.IsTrue(restarted.IsIgnored(@"D:\dl\b.mkv"));
        Assert.HasCount(2, restarted.GetAll());
    }
}
