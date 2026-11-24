using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;

namespace Simplifiarr.Tests;

[TestClass]
public class QuarantinePlacementTests : TempFolderTest
{
    [TestMethod]
    public void Something_on_another_drive_is_quarantined_on_that_drive()
    {
        Assert.AreEqual(@"Y:\Simplifiarr Quarantine", QuarantineService.RootFor(@"X:\Data\Quarantine", @"Y:\Movies\Night Pier (2021)"));
        Assert.AreEqual(@"X:\Data\Quarantine", QuarantineService.RootFor(@"X:\Data\Quarantine", @"X:\Library\Movies\Night Pier (2021)"));
    }

    [TestMethod]
    public async Task A_whole_folder_moves_into_Quarantine_and_is_listed()
    {
        var settings = new DriveSettings(new SimplifiarrSettings { QuarantinePath = Path.Combine(Folder, "Quarantine") });
        var movie = Path.Combine(Folder, "Movies", "Night Pier (2021)");
        Directory.CreateDirectory(Path.Combine(movie, "Subs"));
        File.WriteAllText(Path.Combine(movie, "Night Pier (2021).mkv"), "the film");
        File.WriteAllText(Path.Combine(movie, "Subs", "en.srt"), "words");

        var quarantined = await QuarantineService.MoveIntoAsync(settings, movie);

        Assert.IsNotNull(quarantined);
        Assert.IsFalse(Directory.Exists(movie));
        Assert.AreEqual("the film", File.ReadAllText(Path.Combine(quarantined, "Night Pier (2021).mkv")));

        var items = await new QuarantineInventoryService(settings).GetItemsAsync();

        Assert.HasCount(2, items);
    }

    [TestMethod]
    public async Task Scans_never_read_a_drive_Quarantine_folder()
    {
        var library = Path.Combine(Folder, "Movies");
        Directory.CreateDirectory(Path.Combine(library, QuarantineService.DriveFolderName, "20261124", "abc"));
        File.WriteAllText(Path.Combine(library, QuarantineService.DriveFolderName, "20261124", "abc", "old.mkv"), "taken out");
        Directory.CreateDirectory(Path.Combine(library, "Night Pier (2021)"));
        File.WriteAllText(Path.Combine(library, "Night Pier (2021)", "Night Pier (2021).mkv"), "the film");

        var files = await new FileScannerService().ScanAsync(library);

        Assert.AreEqual("Night Pier (2021).mkv", files.Single().FileName);
    }

    [TestMethod]
    public void Restoring_a_file_of_a_quarantined_folder_takes_its_saving_back_off()
    {
        var folder = @"H:\Simplifiarr Quarantine\20261124\abc\Night Pier (2021)";

        var savings =
            SpaceSavings.Tally(
            [
                new HistoryRecord { ActionType = HistoryActionType.TitleDeleted, Success = true, SizeBytes = 500, LibraryPath = @"H:\Movies\Night Pier (2021)", QuarantinePath = folder, TimestampUtc = new DateTime(2026, 11, 24) },
                new HistoryRecord { ActionType = HistoryActionType.QuarantineRestored, Success = true, SizeBytes = 400, QuarantinePath = folder + @"\Night Pier (2021).mkv", TimestampUtc = new DateTime(2026, 11, 25) }
            ]);

        Assert.IsEmpty(savings);
    }

    [TestMethod]
    public async Task Storage_Delete_puts_the_title_in_Quarantine()
    {
        var movies = Path.Combine(Folder, "Movies");
        var movie = Path.Combine(movies, "Night Pier (2021)");
        Directory.CreateDirectory(movie);
        File.WriteAllText(Path.Combine(movie, "Night Pier (2021).mkv"), "the film");

        var settings =
            new DriveSettings(
                new SimplifiarrSettings
                {
                    QuarantinePath = Path.Combine(Folder, "Quarantine"),
                    DriveProfiles = [new DriveProfile { Name = "D", MovieLibraryPath = movies }]
                });

        var mover =
            new StorageMoveService(
                new FakeArrPaths { SetUp = false },
                new StorageService(settings, new FileInventoryCacheService(new FileScannerService())),
                new HistoryService(settings),
                new IdleScans(),
                settings: settings);

        var result = await mover.DeleteAsync(new StorageTitle { Name = "Night Pier (2021)", Kind = StorageKinds.Movies, FolderPath = movie, LibraryPath = movies }, 0);

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.IsFalse(Directory.Exists(movie));
        StringAssert.Contains(result.Message, "Quarantine");

        var record = (await new HistoryService(settings).GetHistoryAsync()).Single(entry => entry.ActionType == HistoryActionType.TitleDeleted);
        StringAssert.StartsWith(record.QuarantinePath, Path.Combine(Folder, "Quarantine"));
    }
}
