using System.Net;
using System.Text.Json;
using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;

namespace Simplifiarr.Tests;

/// <summary>
/// A stand-in qBittorrent Web API that can insist on a login, the way it
/// does when "Bypass authentication for clients on localhost" is off.
/// </summary>
internal sealed class FakeQbittorrentWeb : HttpMessageHandler
{
    public string? RequiredUser { get; set; }

    public string RequiredPassword { get; set; } = string.Empty;

    /// <summary>The session qBittorrent will hand out next.</summary>
    public string NextSession { get; set; } = "session-1";

    /// <summary>The session currently accepted; changing it "expires" older ones.</summary>
    public string? ValidSession { get; set; }

    public List<string> Paths { get; } = [];

    public int Logins { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Paths.Add(request.RequestUri!.AbsolutePath);

        if (request.RequestUri.AbsolutePath.EndsWith("/api/v2/auth/login", StringComparison.Ordinal))
        {
            Logins++;

            var form = await request.Content!.ReadAsStringAsync(cancellationToken);
            var ok = form.Contains($"username={RequiredUser}") && form.Contains($"password={RequiredPassword}");

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ok ? "Ok." : "Fails.") };

            if (ok)
            {
                ValidSession = NextSession;
                response.Headers.Add("Set-Cookie", $"SID={NextSession}; HttpOnly; path=/");
            }

            return response;
        }

        if (RequiredUser is not null)
        {
            var cookie = request.Headers.TryGetValues("Cookie", out var values) ? string.Join(";", values) : string.Empty;

            if (ValidSession is null || !cookie.Contains($"SID={ValidSession}"))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("Forbidden") };
            }
        }

        if (request.RequestUri.AbsolutePath.EndsWith("/api/v2/torrents/info", StringComparison.Ordinal))
        {
            var torrents = new[]
            {
                new { hash = "aaa", name = "One", state = "stalledUP", save_path = @"D:\Torrents", content_path = @"D:\Torrents\One", size = 10L, progress = 1.0 },
                new { hash = "bbb", name = "Two", state = "stoppedDL", save_path = @"D:\Torrents", content_path = @"D:\Torrents\Two", size = 20L, progress = 0.5 }
            };

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(torrents)) };
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("v5.3.0") };
    }
}

[TestClass]
public class TorrentClientTests
{
    private static int _addresses;

    // Each test gets its own address, so sessions remembered by one test
    // never leak into another.
    private readonly string _url = $"http://qbittorrent-{Interlocked.Increment(ref _addresses)}.test:8080";

    private readonly FakeQbittorrentWeb _web = new();

    private QbittorrentClient Client(string? url = null, string username = "", string password = "") =>
        new(
            new HttpClient(_web),
            new DriveSettings(
                new SimplifiarrSettings
                {
                    TorrentClientUrl = url ?? _url,
                    TorrentClientUsername = username,
                    TorrentClientPassword = password
                }));

    [TestMethod]
    public void qBittorrent_states_map_to_Simplifiarrs_words()
    {
        Assert.AreEqual(TorrentStatus.Seeding, QbittorrentClient.MapState("stalledUP"));
        Assert.AreEqual(TorrentStatus.Stopped, QbittorrentClient.MapState("stoppedUP"));
        Assert.AreEqual(TorrentStatus.Stopped, QbittorrentClient.MapState("pausedDL"), "qBittorrent 4's word.");
        Assert.AreEqual(TorrentStatus.Checking, QbittorrentClient.MapState("checkingResumeData"));
        Assert.AreEqual(TorrentStatus.Moving, QbittorrentClient.MapState("moving"));
        Assert.AreEqual(TorrentStatus.Preparing, QbittorrentClient.MapState("metaDL"));
        Assert.AreEqual(TorrentStatus.Error, QbittorrentClient.MapState("missingFiles"));
        Assert.AreEqual(TorrentStatus.Unknown, QbittorrentClient.MapState("somethingNew"));
    }

    [TestMethod]
    public async Task Torrents_are_read_without_a_login_when_none_is_needed()
    {
        var torrents = await Client().TryGetTorrentsAsync();

        Assert.IsNotNull(torrents);
        Assert.HasCount(2, torrents);
        Assert.AreEqual(TorrentStatus.Seeding, torrents[0].Status);
        Assert.AreEqual("stalledUP", torrents[0].State);
        Assert.AreEqual(0.5, torrents[1].Progress);
        Assert.AreEqual(0, _web.Logins);
    }

    [TestMethod]
    public async Task With_a_login_it_signs_in_and_signs_in_again_when_the_session_runs_out()
    {
        _web.RequiredUser = "admin";
        _web.RequiredPassword = "secret";

        var client = Client(username: "admin", password: "secret");

        Assert.IsNotNull(await client.TryGetTorrentsAsync());
        Assert.IsNotNull(await client.TryGetTorrentsAsync());
        Assert.AreEqual(1, _web.Logins, "One login serves later requests.");

        // qBittorrent restarted: the old session is no good any more.
        _web.NextSession = "session-2";
        _web.ValidSession = "something-else";

        Assert.IsNotNull(await client.TryGetTorrentsAsync());
        Assert.AreEqual(2, _web.Logins);
    }

    [TestMethod]
    public async Task A_client_that_needs_a_login_and_has_none_counts_as_unreachable()
    {
        _web.RequiredUser = "admin";

        Assert.IsNull(await Client().TryGetTorrentsAsync(), "Never an empty list: that would look like no torrents at all.");
    }

    [TestMethod]
    public async Task The_connection_test_says_what_is_wrong()
    {
        _web.RequiredUser = "admin";
        _web.RequiredPassword = "secret";

        var client = Client();

        Assert.IsNull(await client.TestConnectionAsync(_url, "admin", "secret"));
        StringAssert.Contains(await client.TestConnectionAsync(_url, "admin", "wrong"), "didn't accept");
        StringAssert.Contains(await client.TestConnectionAsync(_url, "", ""), "asks for a login");
        StringAssert.Contains(await client.TestConnectionAsync("not an address", "", ""), "isn't a valid address");
    }

    [TestMethod]
    public async Task An_address_behind_a_reverse_proxy_keeps_its_folder()
    {
        await Client(url: $"{_url}/qbittorrent/").TryGetTorrentsAsync();

        Assert.AreEqual("/qbittorrent/api/v2/torrents/info", _web.Paths.Single());
    }

    [TestMethod]
    public void An_old_settings_file_keeps_its_qBittorrent_address()
    {
        var settings =
            JsonSerializer.Deserialize<SimplifiarrSettings>("""{ "QbittorrentUrl": "http://localhost:9090" }""")!;

        Assert.AreEqual("http://localhost:9090", settings.TorrentClientUrl);
        Assert.AreEqual(TorrentClientKind.QBittorrent, settings.TorrentClient);

        var saved = JsonSerializer.Serialize(settings);

        Assert.DoesNotContain("QbittorrentUrl", saved, "The old name is never written back.");
        Assert.Contains("\"TorrentClient\":\"QBittorrent\"", saved);
    }
}
