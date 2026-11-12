using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Tests;

internal sealed class DriveSettings(SimplifiarrSettings settings) : ISettingsService
{
    public Task<SimplifiarrSettings> GetSettingsAsync() =>
        Task.FromResult(settings);

    public Task<List<DriveProfile>> GetEnabledDriveProfilesAsync() =>
        Task.FromResult(settings.DriveProfiles.Where(profile => profile.Enabled).ToList());

    public Task SaveSettingsAsync(SimplifiarrSettings value) =>
        Task.CompletedTask;
}

[TestClass]
[SupportedOSPlatform("windows")]
public class PlaybackErrorTests : TempFolderTest
{
    private const string Drive = "Test Drive";

    // The account Jellyfin's Windows service uses by default. Temp folders
    // don't give it access, which is exactly the situation to test.
    private const string MediaServer = @"NT AUTHORITY\NETWORK SERVICE";

    private SimplifiarrSettings _settings = default!;
    private PlaybackErrorService _service = default!;
    private string _movies = default!;
    private string _downloads = default!;

    [TestInitialize]
    public void Build()
    {
        _movies = Path.Combine(Folder, "Movies");
        _downloads = Path.Combine(Folder, "Torrents");

        Directory.CreateDirectory(_movies);
        Directory.CreateDirectory(_downloads);

        _settings =
            new SimplifiarrSettings
            {
                MediaServerAccount = MediaServer,
                DriveProfiles =
                [
                    new DriveProfile
                    {
                        Name = Drive,
                        DriveRoot = Path.GetPathRoot(Folder)!,
                        MovieLibraryPath = _movies,
                        DownloadPath = _downloads
                    }
                ]
            };

        var settings = new DriveSettings(_settings);

        _service =
            new PlaybackErrorService(
                settings,
                new FileInventoryCacheService(new FileScannerService()),
                new HistoryService(settings));

        // The library folder lets the media server read what's in it, as a
        // real library does.
        GrantFolderRead(_movies);
    }

    private string Film(string name, int bytes = 1000)
    {
        var path = Path.Combine(_movies, Path.GetFileNameWithoutExtension(name), name);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);

        return path;
    }

    private async Task<List<PlaybackError>> ScanAsync() =>
        await _service.ScanAsync(Drive);

    private static SecurityIdentifier MediaServerSid =>
        new(WellKnownSidType.NetworkServiceSid, null);

    private static void GrantFolderRead(string folder)
    {
        var info = new DirectoryInfo(folder);
        var security = info.GetAccessControl();

        security.AddAccessRule(
            new FileSystemAccessRule(
                MediaServerSid,
                FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

        info.SetAccessControl(security);
    }

    /// <summary>What a file looks like after being moved in from a folder the media server can't read.</summary>
    private static void LockToCurrentUser(string path)
    {
        var security = new FileSecurity();

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(
            new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!,
                FileSystemRights.FullControl,
                AccessControlType.Allow));

        new FileInfo(path).SetAccessControl(security);
    }

    private static bool MediaServerCanRead(string path) =>
        FilePermissions.CanRead(
            new FileInfo(path).GetAccessControl(),
            FilePermissions.IdentitiesFor(MediaServer)!);

    [TestMethod]
    public async Task A_healthy_library_has_no_playback_errors()
    {
        Film("Alien (1979).mkv");

        Assert.IsEmpty(await ScanAsync());
    }

    [TestMethod]
    public async Task A_file_marked_as_downloaded_is_found_and_unblocking_removes_only_the_mark()
    {
        var path = Film("Alien (1979).mkv");
        var before = File.ReadAllBytes(path);

        File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.test/alien\r\n");

        var error = (await ScanAsync()).Single();

        Assert.AreEqual(PlaybackErrorKind.Blocked, error.Kind);
        Assert.AreEqual("https://example.test/alien", error.SourceUrl);

        var result = await _service.FixAsync(error);

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.IsFalse(ZoneIdentifierHelper.IsBlocked(path));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
        Assert.IsEmpty(await ScanAsync());
    }

    [TestMethod]
    public async Task A_file_the_media_server_cant_read_is_found_fixed_and_can_be_put_back()
    {
        var path = Film("Alien (1979).mkv");

        LockToCurrentUser(path);

        var lockedSddl = new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);

        var error = (await ScanAsync()).Single();

        Assert.AreEqual(PlaybackErrorKind.MediaServerCantRead, error.Kind);
        StringAssert.Contains(error.Detail, MediaServer);

        var result = await _service.FixAsync(error);

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.IsTrue(MediaServerCanRead(path));
        Assert.IsFalse(new FileInfo(path).GetAccessControl().AreAccessRulesProtected, "It takes its folder's permissions again.");
        Assert.IsEmpty(await ScanAsync());
        CollectionAssert.Contains(_service.GetUndoablePaths().ToList(), path);

        var undo = await _service.UndoPermissionsAsync(path);

        Assert.IsTrue(undo.Succeeded, undo.Message);
        Assert.AreEqual(
            lockedSddl,
            new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        Assert.IsEmpty(_service.GetUndoablePaths());
    }

    [TestMethod]
    public async Task A_file_with_its_own_extra_permissions_is_reported_as_not_matching_its_folder()
    {
        var path = Film("Alien (1979).mkv");
        var info = new FileInfo(path);
        var security = info.GetAccessControl();

        security.AddAccessRule(
            new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null),
                FileSystemRights.Read,
                AccessControlType.Allow));

        info.SetAccessControl(security);

        var error = (await ScanAsync()).Single();

        Assert.AreEqual(PlaybackErrorKind.PermissionsDontMatchFolder, error.Kind);

        Assert.IsTrue((await _service.FixAsync(error)).Succeeded);
        Assert.IsTrue(
            FilePermissions.MatchesFolder(
                new FileInfo(path).GetAccessControl(),
                new DirectoryInfo(Path.GetDirectoryName(path)!).GetAccessControl()));
    }

    [TestMethod]
    public async Task Without_a_media_server_account_reading_isnt_checked()
    {
        _settings.MediaServerAccount = "No Such Account Anywhere";

        var path = Film("Alien (1979).mkv");

        LockToCurrentUser(path);

        var errors = await ScanAsync();

        Assert.IsFalse(errors.Any(error => error.Kind == PlaybackErrorKind.MediaServerCantRead));
        Assert.IsTrue(errors.Any(error => error.Kind == PlaybackErrorKind.PermissionsDontMatchFolder));
    }

    [TestMethod]
    public async Task A_split_rar_release_is_reported_once_with_the_size_of_every_part()
    {
        var folder = Path.Combine(_movies, "Alien.1979.1080p-GROUP");

        Directory.CreateDirectory(folder);

        File.WriteAllBytes(Path.Combine(folder, "alien.rar"), new byte[100]);
        File.WriteAllBytes(Path.Combine(folder, "alien.r00"), new byte[100]);
        File.WriteAllBytes(Path.Combine(folder, "alien.r01"), new byte[50]);
        File.WriteAllBytes(Path.Combine(folder, "other.part01.rar"), new byte[10]);
        File.WriteAllBytes(Path.Combine(folder, "other.part02.rar"), new byte[10]);

        var packed = (await ScanAsync()).Where(error => error.Kind == PlaybackErrorKind.Packed).ToList();

        Assert.HasCount(2, packed);
        Assert.AreEqual(250, packed.Single(error => error.FileName == "alien.rar").SizeBytes);
        Assert.AreEqual(20, packed.Single(error => error.FileName == "other.part01.rar").SizeBytes);
    }

    [TestMethod]
    public async Task Comic_archives_are_the_real_format_and_are_not_reported()
    {
        File.WriteAllBytes(Path.Combine(_movies, "issue.cbz"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_movies, "issue.cbr"), new byte[10]);

        Assert.IsEmpty(await ScanAsync());
    }

    [TestMethod]
    public async Task A_zip_in_a_comic_library_is_a_comic_not_a_packed_release()
    {
        var comics = Path.Combine(Folder, "Comics");

        Directory.CreateDirectory(comics);
        GrantFolderRead(comics);

        _settings.DriveProfiles[0].ComicLibraryPath = comics;

        File.WriteAllBytes(Path.Combine(comics, "Issue 001.zip"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_movies, "Alien.zip"), new byte[10]);

        var packed = (await ScanAsync()).Where(error => error.Kind == PlaybackErrorKind.Packed).ToList();

        Assert.AreEqual("Alien.zip", packed.Single().FileName);
    }

    [TestMethod]
    public async Task Unpacking_puts_the_files_beside_the_archive_and_leaves_the_archive()
    {
        if (await _service.FindSevenZipAsync() is null)
        {
            Assert.Inconclusive("7-Zip isn't installed on this machine.");
        }

        var folder = Path.Combine(_movies, "Scream.1996.1080p-GROUP");

        Directory.CreateDirectory(folder);

        var archive = Path.Combine(folder, "scream.zip");
        var content = new byte[5000];

        new Random(1).NextBytes(content);

        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using var entry = zip.CreateEntry("Scream (1996).mkv").Open();

            entry.Write(content);
        }

        var error = (await ScanAsync()).Single(candidate => candidate.Kind == PlaybackErrorKind.Packed);

        var result = await _service.FixAsync(error);

        Assert.IsTrue(result.Succeeded, result.Message);
        CollectionAssert.AreEqual(content, File.ReadAllBytes(Path.Combine(folder, "Scream (1996).mkv")));
        Assert.IsTrue(File.Exists(archive), "The archive is left for seeding.");

        // A second try finds the file already there and changes nothing.
        var again = await _service.FixAsync(error);

        Assert.IsFalse(again.Succeeded);
        StringAssert.Contains(again.Message, "already in that folder");
    }

    [TestMethod]
    public async Task Empty_unfinished_and_too_long_files_are_reported()
    {
        Film("Empty (2001).mkv", bytes: 0);
        Film("Alien (1979).mkv.!qB");

        var deep = Path.Combine(_movies, new string('a', 120), new string('b', 120));

        Directory.CreateDirectory(deep);
        File.WriteAllBytes(Path.Combine(deep, "Long (2002).mkv"), new byte[10]);

        var kinds = (await ScanAsync()).Select(error => error.Kind).ToList();

        CollectionAssert.Contains(kinds, PlaybackErrorKind.Empty);
        CollectionAssert.Contains(kinds, PlaybackErrorKind.Unfinished);
        CollectionAssert.Contains(kinds, PlaybackErrorKind.PathTooLong);
    }

    [TestMethod]
    public async Task Files_in_a_downloads_folder_inside_a_library_are_never_reported()
    {
        _settings.DriveProfiles[0].MovieLibraryPath = Folder;

        var path = Path.Combine(_downloads, "seeding.mkv");

        File.WriteAllBytes(path, new byte[10]);
        File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");

        Assert.IsFalse((await ScanAsync()).Any(error => error.Path == path));
    }

    [TestMethod]
    public void Names_windows_programs_struggle_with_are_recognised()
    {
        Assert.IsNotNull(PlaybackErrorService.BadNamePart(@"D:\Movies\Alien (1979) \Alien.mkv"));
        Assert.IsNotNull(PlaybackErrorService.BadNamePart(@"D:\Movies\Alien.\Alien.mkv"));
        Assert.IsNotNull(PlaybackErrorService.BadNamePart(@"D:\Movies\CON\film.mkv"));
        Assert.IsNotNull(PlaybackErrorService.BadNamePart(@"D:\Movies\nul.mkv"));
        Assert.IsNull(PlaybackErrorService.BadNamePart(@"D:\Movies\Console (2020)\Console.mkv"));
        Assert.IsNull(PlaybackErrorService.BadNamePart(@"D:\Movies\Alien (1979)\Alien (1979).mkv"));
    }

    [TestMethod]
    public void The_7zip_listing_gives_files_and_skips_folders()
    {
        const string listing =
            "7-Zip 24.08\r\n\r\n--\r\nPath = x.rar\r\nType = Rar\r\n\r\n----------\r\n" +
            "Path = Alien\r\nFolder = +\r\nSize = 0\r\n\r\n" +
            "Path = Alien/Alien (1979).mkv\r\nFolder = -\r\nSize = 123456\r\n\r\n" +
            "Path = Alien/Sample/sample.mkv\r\nSize = 99\r\n\r\n";

        var entries = PlaybackErrorService.ParseListing(listing);

        Assert.HasCount(2, entries);
        Assert.AreEqual((@"Alien\Alien (1979).mkv", 123456L), entries[0]);
    }

    [TestMethod]
    public void Permissions_a_folder_hands_down_in_short_form_still_match_the_file()
    {
        // GENERIC_READ|GENERIC_WRITE|GENERIC_EXECUTE|DELETE on the folder is
        // what Windows expands to Modify on the file.
        // .NET won't build a rule like that itself, so it is written the way
        // Windows stores it.
        var folder = new DirectorySecurity();

        folder.SetSecurityDescriptorSddlForm("D:P(A;OIIO;GRGWGXSD;;;AU)", AccessControlSections.Access);

        var file = new FileSecurity();

        file.SetSecurityDescriptorSddlForm("D:AI(A;ID;0x1301bf;;;AU)", AccessControlSections.Access);

        Assert.IsTrue(FilePermissions.MatchesFolder(file, folder));
    }

    [TestMethod]
    public void Creator_owner_on_the_folder_matches_the_files_owner()
    {
        // A drive root's usual permissions. Windows hands "Creator Owner"
        // down as whoever owns the file, here an ordinary user.
        const string user = "S-1-5-21-1000000000-2000000000-3000000000-1001";

        var folder = new DirectorySecurity();

        folder.SetSecurityDescriptorSddlForm(
            "O:BAG:SYD:AI(A;OICIID;FA;;;WD)(A;ID;FA;;;BA)(A;OICIIOID;FA;;;CO)(A;OICIID;FA;;;SY)(A;OICIIOID;FA;;;BA)(A;OICIID;0x1201bf;;;BU)");

        var file = new FileSecurity();

        file.SetSecurityDescriptorSddlForm(
            $"O:{user}G:{user}D:AI(A;ID;FA;;;WD)(A;ID;FA;;;{user})(A;ID;FA;;;SY)(A;ID;FA;;;BA)(A;ID;0x1201bf;;;BU)");

        Assert.IsTrue(FilePermissions.MatchesFolder(file, folder), "The owner's entry came from Creator Owner.");

        // The same file owned by someone else has an entry the folder didn't give it.
        var moved = new FileSecurity();

        moved.SetSecurityDescriptorSddlForm(
            $"O:BAG:SYD:AI(A;ID;FA;;;WD)(A;ID;FA;;;{user})(A;ID;FA;;;SY)(A;ID;FA;;;BA)(A;ID;0x1201bf;;;BU)");

        Assert.IsFalse(FilePermissions.MatchesFolder(moved, folder));
    }
}
