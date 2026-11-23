using System.Text.Json;
using Simplifiarr.Core.Services;

namespace Simplifiarr.Tests;

[TestClass]
public class UpdateServiceTests
{
    [TestMethod]
    public void Versions_compare_by_number_not_text()
    {
        Assert.IsTrue(UpdateService.IsNewer("v1.10.0", "1.9.3"));
        Assert.IsTrue(UpdateService.IsNewer("1.0.1", "1.0.0"));
        Assert.IsTrue(UpdateService.IsNewer("v2", "1.9.9"));
        Assert.IsFalse(UpdateService.IsNewer("1.0.0", "1.0.0"));
        Assert.IsFalse(UpdateService.IsNewer("v1.0.0", "1.2.0"));
        Assert.IsFalse(UpdateService.IsNewer("nonsense", "1.0.0"));
    }

    [TestMethod]
    public void A_full_release_is_newer_than_its_own_beta()
    {
        Assert.IsTrue(UpdateService.IsNewer("1.2.0", "1.2.0-beta.1"));
        Assert.IsFalse(UpdateService.IsNewer("1.2.0-beta.1", "1.2.0"));
        Assert.AreEqual("1.2.0-beta.1", UpdateService.NormalizeVersion("v1.2-beta.1"));
    }

    [TestMethod]
    public void A_GitHub_release_is_read_with_its_installer_and_checksums()
    {
        const string json = """
            {
              "tag_name": "v1.2.0",
              "name": "Simplifiarr 1.2.0",
              "body": "  Rolling Movies\n- Faster scans  ",
              "html_url": "https://github.com/Simplifiarr/Simplifiarr/releases/tag/v1.2.0",
              "published_at": "2026-11-20T10:00:00Z",
              "assets": [
                { "name": "Simplifiarr-1.2.0-win-x64.zip", "size": 1, "browser_download_url": "https://example.test/zip" },
                { "name": "Simplifiarr-Setup-1.2.0.exe", "size": 74000000, "browser_download_url": "https://example.test/setup" },
                { "name": "SHA256SUMS.txt", "size": 2, "browser_download_url": "https://example.test/sums" }
              ]
            }
            """;

        using var document = JsonDocument.Parse(json);
        var release = UpdateService.ParseRelease(document.RootElement);

        Assert.AreEqual("1.2.0", release.Version);
        Assert.AreEqual("Simplifiarr 1.2.0", release.Name);
        Assert.AreEqual("Rolling Movies\n- Faster scans", release.Notes);
        Assert.AreEqual("https://example.test/setup", release.SetupUrl);
        Assert.AreEqual("Simplifiarr-Setup-1.2.0.exe", release.SetupName);
        Assert.AreEqual(74000000, release.SetupBytes);
        Assert.AreEqual("https://example.test/sums", release.ChecksumsUrl);
        Assert.AreEqual(new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc), release.PublishedUtc);
    }

    [TestMethod]
    public void A_release_without_an_installer_has_nothing_to_install()
    {
        using var document = JsonDocument.Parse("""{ "tag_name": "v1.3.0", "assets": [] }""");
        var release = UpdateService.ParseRelease(document.RootElement);

        Assert.AreEqual("v1.3.0", release.Name);
        Assert.IsNull(release.SetupUrl);
        Assert.IsNull(release.ChecksumsUrl);
    }

    [TestMethod]
    public void The_checksum_is_found_for_the_right_file_only()
    {
        var hashA = new string('a', 64);
        var hashB = new string('b', 64);
        var sums = $"{hashA}  Simplifiarr-1.2.0-win-x64.zip\r\n{hashB} *Simplifiarr-Setup-1.2.0.exe\n";

        Assert.AreEqual(hashB, UpdateService.ChecksumFor(sums, "Simplifiarr-Setup-1.2.0.exe"));
        Assert.AreEqual(hashA, UpdateService.ChecksumFor(sums, "simplifiarr-1.2.0-win-x64.zip"));
        Assert.IsNull(UpdateService.ChecksumFor(sums, "Simplifiarr-Setup-9.9.9.exe"));
        Assert.IsNull(UpdateService.ChecksumFor("short  Simplifiarr-Setup-1.2.0.exe", "Simplifiarr-Setup-1.2.0.exe"));
    }

    [TestMethod]
    public void The_program_is_read_from_a_service_command_line()
    {
        Assert.AreEqual(@"C:\Program Files\Simplifiarr\Simplifiarr.Web.exe",
            UpdateService.ProgramOf("\"C:\\Program Files\\Simplifiarr\\Simplifiarr.Web.exe\" --Kestrel:Endpoints:Http:Url=http://0.0.0.0:6481"));

        Assert.AreEqual(@"C:\Services\Simplifiarr\Simplifiarr.Web.exe",
            UpdateService.ProgramOf(@"C:\Services\Simplifiarr\Simplifiarr.Web.exe --Simplifiarr:DataPath=D:\Data"));
    }
}
