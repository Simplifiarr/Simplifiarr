using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;

namespace Simplifiarr.Tests;

[TestClass]
public class ArrFolderRepairTests
{
    private static ArrItem Movie(int id, string title, int? year, string path) =>
        new("Radarr", id, title, path, year);

    private static StorageTitle Folder(string path, string kind = StorageKinds.Movies) =>
        new() { Kind = kind, FolderPath = path, Name = Path.GetFileName(path) };

    private static readonly HashSet<string> Existing = new(StringComparer.OrdinalIgnoreCase)
    {
        @"O:\Movies\The Gorge (2025)",
        @"O:\Movies\Harbour (2019)",
        @"F:\Holiday\Night Shift 2021",
        @"O:\Movies\Twin (2020)",
        @"H:\Movies\Twin (2020)"
    };

    private static List<ArrFolderProblem> Find(params ArrItem[] items) =>
        ArrFolderRepair.Find(
            StorageKinds.Movies,
            items,
            Existing.Select(path => Folder(path)).ToList(),
            Existing.Contains);

    [TestMethod]
    public void A_moved_folder_is_found_by_its_name()
    {
        var problem = Find(Movie(1, "The Gorge", 2025, @"C:\Media\Movies\The Gorge (2025)")).Single();

        Assert.AreEqual(@"O:\Movies\The Gorge (2025)", problem.FoundFolder);
    }

    [TestMethod]
    public void A_renamed_folder_is_found_by_title_and_year()
    {
        var problem = Find(Movie(1, "Night Shift", 2021, @"C:\Media\Movies\Night Shift (2021)")).Single();

        Assert.AreEqual(@"F:\Holiday\Night Shift 2021", problem.FoundFolder);
    }

    [TestMethod]
    public void Items_whose_folders_exist_are_fine_and_their_folders_are_never_offered()
    {
        var problems =
            Find(
                Movie(1, "Harbour", 2019, @"O:\Movies\Harbour (2019)"),
                Movie(2, "Harbour", 2019, @"C:\Media\Movies\Harbour (2019)"));

        var problem = problems.Single();

        Assert.AreEqual(2, problem.Item.Id);
        Assert.IsNull(problem.FoundFolder, "Radarr's other entry already uses that folder.");
    }

    [TestMethod]
    public void Two_possible_folders_are_left_for_you()
    {
        var problem = Find(Movie(1, "Twin", 2020, @"C:\Media\Movies\Twin (2020)")).Single();

        Assert.IsFalse(problem.CanFix);
        Assert.AreEqual(2, problem.Matches);
    }

    [TestMethod]
    public void Moving_finds_a_title_Radarr_has_under_a_folder_that_is_gone()
    {
        var items = new[]
        {
            Movie(1, "The Gorge", 2025, @"H:\Movies\The Gorge (2025)"),
            Movie(2, "Harbour", 2019, @"O:\Movies\Harbour (2019)")
        };

        Assert.AreEqual(1, ArrPathService.FindStale(items, @"C:\Media\Movies\The Gorge (2025)", Existing.Contains)?.Id);

        // Harbour's own folder is still there, so it isn't the one being moved.
        Assert.IsNull(ArrPathService.FindStale(items, @"E:\movies\Harbour (2019)", Existing.Contains));
    }
}
