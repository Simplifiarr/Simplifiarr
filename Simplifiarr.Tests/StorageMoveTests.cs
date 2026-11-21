using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Tests;

internal sealed class FakeArrPaths : IArrPathService
{
    public bool SetUp { get; set; } = true;

    public string? Refusal { get; set; }

    public List<(string Folder, string Library)> Moves { get; } = [];

    public List<int> Rescans { get; } = [];

    public string? KnownFolder { get; set; }

    public Task<bool> IsSetUpAsync(string kind) => Task.FromResult(SetUp);

    public Task<ArrItem?> FindByFolderAsync(string kind, string folderPath) =>
        Task.FromResult(
            SetUp && KnownFolder is not null &&
            string.Equals(KnownFolder, folderPath, StringComparison.OrdinalIgnoreCase)
                ? new ArrItem("Radarr", 7, "Night Shift", folderPath)
                : null);

    public List<ArrItem> All { get; } = [];

    public Task<IReadOnlyList<string>> GetRootFoldersAsync(string kind) => Task.FromResult<IReadOnlyList<string>>([]);

    public Task<IReadOnlyList<ArrItem>> GetAllAsync(string kind) => Task.FromResult<IReadOnlyList<ArrItem>>(All);

    public Task<string?> SetFolderAsync(string kind, ArrItem item, string newFolderPath, string newLibraryPath)
    {
        if (Refusal is null)
        {
            Moves.Add((newFolderPath, newLibraryPath));
        }

        return Task.FromResult(Refusal);
    }

    public List<int> Removed { get; } = [];

    public Task<string?> RemoveAsync(string kind, ArrItem item)
    {
        Removed.Add(item.Id);

        return Task.FromResult<string?>(null);
    }

    public Task RescanAsync(string kind, ArrItem item)
    {
        Rescans.Add(item.Id);

        return Task.CompletedTask;
    }
}

internal sealed class IdleScans : IScanCoordinator
{
    public ScanProgress Progress { get; } = new();

    public ScanSummary? LastSummary => null;

    public DateTime? GetLastScanUtc(string driveName) => null;

    public bool Start(string? driveName = null) => false;

    public void Cancel() { }

    public event Action? Changed { add { } remove { } }

    public Task WhenIdleAsync() => Task.CompletedTask;
}

[TestClass]
public class StorageMoveTests : TempFolderTest
{
    private string _from = default!;
    private string _to = default!;
    private FakeArrPaths _arr = default!;
    private StorageMoveService _mover = default!;
    private HistoryService _history = default!;

    [TestInitialize]
    public void Build()
    {
        _from = Path.Combine(Folder, "first", "Movies");
        _to = Path.Combine(Folder, "second", "Movies");

        Directory.CreateDirectory(_from);
        Directory.CreateDirectory(_to);

        var settings =
            new DriveSettings(
                new SimplifiarrSettings
                {
                    DriveProfiles =
                    [
                        new DriveProfile { Name = "First", MovieLibraryPath = _from },
                        new DriveProfile { Name = "Second", MovieLibraryPath = _to }
                    ]
                });

        _arr = new FakeArrPaths();
        _history = new HistoryService(settings);

        _mover =
            new StorageMoveService(
                _arr,
                new StorageService(settings, new FileInventoryCacheService(new FileScannerService())),
                _history,
                new IdleScans())
            {
                RequireOtherDrive = false,
                PauseBetweenTitles = TimeSpan.Zero,
                RemoveOldFolder = path =>
                {
                    Directory.Delete(path, recursive: true);
                    return true;
                }
            };
    }

    private StorageTitle Title(string name, params (string File, string Content)[] files)
    {
        var folder = Path.Combine(_from, name);

        foreach (var (file, content) in files)
        {
            var path = Path.Combine(folder, file);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        return new StorageTitle
        {
            Name = name,
            Kind = StorageKinds.Movies,
            FolderPath = folder,
            LibraryPath = _from,
            SizeBytes = files.Sum(file => (long)file.Content.Length)
        };
    }

    [TestMethod]
    public async Task A_title_is_copied_checked_handed_to_Radarr_and_the_old_folder_removed()
    {
        var title = Title("Night Shift (2021)", ("Night Shift.mkv", "film"), (Path.Combine("Subs", "en.srt"), "words"));

        _arr.KnownFolder = title.FolderPath;

        var result = await _mover.MoveAsync(new StorageMoveRequest(title, _to));

        Assert.IsTrue(result.Succeeded, result.Message);

        var moved = Path.Combine(_to, "Night Shift (2021)");

        Assert.AreEqual("film", File.ReadAllText(Path.Combine(moved, "Night Shift.mkv")));
        Assert.AreEqual("words", File.ReadAllText(Path.Combine(moved, "Subs", "en.srt")));
        Assert.IsFalse(Directory.Exists(title.FolderPath));
        Assert.IsEmpty(Directory.GetFiles(_to, "*.simplifiarr-partial", SearchOption.AllDirectories));

        Assert.AreEqual((moved, _to), _arr.Moves.Single());
        CollectionAssert.AreEqual(new[] { 7 }, _arr.Rescans);
    }

    [TestMethod]
    public async Task When_Radarr_refuses_the_copy_goes_and_the_title_stays()
    {
        var title = Title("Harbour (2019)", ("Harbour.mkv", "film"));

        _arr.KnownFolder = title.FolderPath;
        _arr.Refusal = "Radarr said no.";

        var result = await _mover.MoveAsync(new StorageMoveRequest(title, _to));

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Message, "Radarr said no.");
        Assert.IsTrue(File.Exists(Path.Combine(title.FolderPath, "Harbour.mkv")));
        Assert.IsFalse(Directory.Exists(Path.Combine(_to, "Harbour (2019)")));
    }

    [TestMethod]
    public async Task A_title_Radarr_doesnt_have_still_moves()
    {
        var title = Title("Home Video", ("clip.mkv", "film"));

        var result = await _mover.MoveAsync(new StorageMoveRequest(title, _to));

        Assert.IsTrue(result.Succeeded, result.Message);
        StringAssert.Contains(result.Message, "doesn't have it");
        Assert.IsEmpty(_arr.Moves);
    }

    [TestMethod]
    public async Task An_existing_folder_at_the_destination_is_never_overwritten()
    {
        var title = Title("Baker's Dozen", ("film.mkv", "new"));

        Directory.CreateDirectory(Path.Combine(_to, "Baker's Dozen"));
        File.WriteAllText(Path.Combine(_to, "Baker's Dozen", "film.mkv"), "old");

        var result = await _mover.MoveAsync(new StorageMoveRequest(title, _to));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(_to, "Baker's Dozen", "film.mkv")));
        Assert.IsTrue(Directory.Exists(title.FolderPath));
    }

    [TestMethod]
    public async Task A_title_that_started_seeding_is_left_alone()
    {
        if (!NativeHardlinks.IsSupported)
        {
            Assert.Inconclusive("Hardlinks are Windows-only here.");
        }

        var title = Title("Seeding Now", ("film.mkv", "film"));
        Assert.IsNull(NativeHardlinks.CreateHardlink(Path.Combine(title.FolderPath, "film.mkv"), Path.Combine(Folder, "seed.mkv")));

        var result = await _mover.MoveAsync(new StorageMoveRequest(title, _to));

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Message, "seeding");
        Assert.IsTrue(Directory.Exists(title.FolderPath));
    }

    [TestMethod]
    public async Task The_queue_moves_one_title_after_another_and_records_history()
    {
        var first = Title("One", ("a.mkv", "1"));
        var second = Title("Two", ("b.mkv", "2"));

        _mover.Enqueue([new StorageMoveRequest(first, _to), new StorageMoveRequest(second, _to)]);

        await _mover.WhenIdleAsync();

        Assert.HasCount(2, _mover.Results);
        Assert.IsTrue(_mover.Results.All(result => result.Succeeded));
        Assert.AreEqual("Two", _mover.Results[0].Request.Title.Name, "Newest first.");
        Assert.IsEmpty(_mover.Queue);
        Assert.IsNull(_mover.Current);

        var history = await _history.GetHistoryAsync();

        Assert.AreEqual(2, history.Count(record => record.ActionType == HistoryActionType.TitleMoved));
    }

    [TestMethod]
    public async Task A_paused_queue_waits_until_resumed()
    {
        var first = Title("Paused", ("a.mkv", "1"));

        _mover.Pause();
        _mover.Enqueue([new StorageMoveRequest(first, _to)]);

        await _mover.WhenIdleAsync();

        Assert.HasCount(1, _mover.Queue);
        Assert.IsTrue(Directory.Exists(first.FolderPath));

        _mover.Resume();
        await _mover.WhenIdleAsync();

        Assert.IsEmpty(_mover.Queue);
        Assert.IsFalse(Directory.Exists(first.FolderPath));
    }
}
