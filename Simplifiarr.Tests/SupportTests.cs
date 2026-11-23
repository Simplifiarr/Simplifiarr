using System.IO.Compression;
using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Tests;

[TestClass]
public class LogRedactorTests
{
    private const string RadarrKey = "0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void Known_keys_and_passwords_never_survive()
    {
        var redactor = new LogRedactor([RadarrKey, "hunter2-password"]);

        var text = redactor.Clean($"Radarr key {RadarrKey}, qBittorrent password hunter2-password");

        Assert.DoesNotContain(RadarrKey, text);
        Assert.DoesNotContain("hunter2", text);
    }

    [TestMethod]
    public void Anything_that_looks_like_a_key_is_taken_out()
    {
        var redactor = new LogRedactor([]);

        var text =
            redactor.Clean(
                "GET http://x/api?apikey=abc123def456 X-Api-Key: zzz999yyy888 " +
                "Authorization: MediaBrowser Token=\"tok3n-value\" \"password\": \"s3cret!\" " +
                "webhook /api/hooks/sonarr/fedcba9876543210fedcba9876543210");

        foreach (var secret in new[] { "abc123def456", "zzz999yyy888", "tok3n-value", "s3cret!", "fedcba9876543210fedcba9876543210" })
        {
            Assert.DoesNotContain(secret, text, secret);
        }
    }

    [TestMethod]
    public void IP_addresses_emails_and_the_windows_user_are_taken_out()
    {
        var redactor = new LogRedactor([], userName: "jordan");

        var text =
            redactor.Clean(@"Connecting to 192.168.1.20:8096 for jordan@example.com from C:\Users\jordan\Downloads, listening on 0.0.0.0:6481 and 127.0.0.1");

        Assert.DoesNotContain("192.168.1.20", text);
        Assert.DoesNotContain("example.com", text);
        Assert.DoesNotContain("jordan", text, StringComparison.OrdinalIgnoreCase);
        StringAssert.Contains(text, "0.0.0.0:6481");
        StringAssert.Contains(text, "127.0.0.1");
    }

    [TestMethod]
    public void Hiding_names_takes_out_titles_people_and_file_names()
    {
        var redactor = new LogRedactor([], titles: ["Night Shift (2021)"], people: ["Robin"]);

        var text =
            redactor.Clean(
                @"Moved Night Shift (2021) to O:\Movies\Night Shift (2021); Night.Shift.2021.1080p.mkv; watched by Robin; Night Shift S01E02.srt");

        Assert.DoesNotContain("Night", text);
        Assert.DoesNotContain("Robin", text);
        StringAssert.Contains(text, @"O:\Movies\[title]");
        StringAssert.Contains(text, "[file].mkv");
    }

    [TestMethod]
    public void A_short_title_is_only_replaced_as_a_whole_word()
    {
        var text = new LogRedactor([], titles: ["Rent", "Music"]).Clean(@"Torrent client added Rent to C:\Media\Music\Musicals");

        Assert.AreEqual(@"Torrent client added [title] to C:\Media\[title]\Musicals", text);
    }

    [TestMethod]
    public void Without_hiding_names_titles_stay()
    {
        var text = new LogRedactor([]).Clean("Moved Night Shift (2021) to O:\\Movies");

        StringAssert.Contains(text, "Night Shift (2021)");
    }

    [TestMethod]
    public void Calls_are_logged_with_query_names_but_not_values()
    {
        var described = LogRedactor.Address(new Uri("http://localhost:7878/api/v3/movie?apikey=secret123&id=5"));

        Assert.AreEqual("http://localhost:7878/api/v3/movie?apikey=…&id=…", described);
    }
}

[TestClass]
public class SupportBundleTests : TempFolderTest
{
    private sealed class FakeStorage(List<StorageDrive> drives) : IStorageService
    {
        public Task ScanAsync(string driveName) => Task.CompletedTask;

        public DateTime? GetLastScanUtc(string driveName) => null;

        public Task<List<StorageDrive>> GetDrivesAsync() => Task.FromResult(drives);

        public Task<List<StorageMoveSuggestion>> GetSuggestionsAsync(Func<StorageTitle, WatchRecord?>? watch = null) => Task.FromResult(new List<StorageMoveSuggestion>());

        public void Forget(StorageTitle title)
        {
        }
    }

    private sealed class FakeWatch(List<WatchRecord> records) : IWatchHistoryService
    {
        public DateTime? RefreshedUtc => DateTime.UtcNow;

        public Task RefreshAsync() => Task.CompletedTask;

        public WatchRecord? Find(StorageTitle title) => null;

        public IReadOnlyList<WatchRecord> Records => records;
    }

    private const string SonarrKey = "abcdefabcdefabcdefabcdefabcdef12";

    private SupportBundleService Service(out string logs)
    {
        logs = Path.Combine(Folder, "Logs");
        Directory.CreateDirectory(logs);

        File.WriteAllText(
            Path.Combine(logs, $"simplifiarr-{DateTime.Now:yyyy-MM-dd}.log"),
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [Information] Simplifiarr: Moved Night Shift (2021) for Robin using {SonarrKey}\n" +
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [Warning] Simplifiarr: GET http://192.168.1.20:8989/api answered 401\n" +
            "   at Some.Stack.Trace()\n");

        var settings = new SimplifiarrSettings { SonarrUrl = "http://192.168.1.20:8989", SonarrApiKey = SonarrKey, TorrentClientPassword = "hunter2-password" };

        var drive = new StorageDrive
        {
            Root = @"O:\",
            TotalBytes = 1000,
            FreeBytes = 500,
            Titles = [new StorageTitle { Name = "Night Shift (2021)", Kind = StorageKinds.Movies }]
        };

        var history = new HistoryService(new DriveSettings(settings));

        return new SupportBundleService(
            new DriveSettings(settings),
            new FakeStorage([drive]),
            new FakeWatch([new WatchRecord { StartedBy = ["Robin"] }]),
            history,
            "9.9.9")
        {
            LogsDirectory = logs
        };
    }

    private static Dictionary<string, string> Read(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip));

        return archive.Entries.ToDictionary(
            entry => entry.FullName,
            entry => new StreamReader(entry.Open()).ReadToEnd());
    }

    [TestMethod]
    public async Task The_zip_has_the_logs_and_setup_but_no_secrets()
    {
        var files = Read(await Service(out _).CreateAsync(hideNames: true));

        CollectionAssert.IsSubsetOf(
            new[] { "README.txt", "about.txt", "settings.json", "problems.txt" },
            files.Keys.ToArray());
        Assert.IsTrue(files.Keys.Any(name => name.StartsWith("logs/")));

        var everything = string.Join("\n", files.Values);

        Assert.DoesNotContain(SonarrKey, everything);
        Assert.DoesNotContain("hunter2", everything);
        Assert.DoesNotContain("192.168.1.20", everything);
        Assert.DoesNotContain("Night Shift", everything);
        Assert.DoesNotContain("Robin", everything);
        StringAssert.Contains(files["about.txt"], "Simplifiarr 9.9.9");
        StringAssert.Contains(files["about.txt"], "Sonarr: yes");
    }

    [TestMethod]
    public async Task Names_can_be_left_in_for_email()
    {
        var files = Read(await Service(out _).CreateAsync(hideNames: false));
        var everything = string.Join("\n", files.Values);

        StringAssert.Contains(everything, "Night Shift (2021)");
        Assert.DoesNotContain(SonarrKey, everything);
    }

    [TestMethod]
    public void Recent_problems_keep_their_stack_trace()
    {
        var lines = Service(out _).RecentLines(50, problemsOnly: true);

        Assert.HasCount(1, lines);
        StringAssert.Contains(lines[0], "answered 401");
        StringAssert.Contains(lines[0], "Some.Stack.Trace");
    }
}
