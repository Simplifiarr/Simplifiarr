using System.Net;
using System.Text;
using System.Text.Json;
using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;

namespace Simplifiarr.Tests;

/// <summary>
/// Stands in for everything the poster code talks to over the web:
/// Jellyfin, fanart.tv, and any site a picture is fetched from.
/// </summary>
internal sealed class FakePosterWeb : HttpMessageHandler
{
    public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];
    public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9, 9];

    /// <summary>Jellyfin item id -> its current poster.</summary>
    public Dictionary<string, byte[]> Posters { get; } = [];

    /// <summary>Jellyfin item id -> what its own image search finds.</summary>
    public Dictionary<string, List<object>> RemoteImages { get; } = [];

    /// <summary>Web address -> (body, content type) for anything else.</summary>
    public Dictionary<string, (byte[] Body, string Type)> Pages { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What was sent as the content type of each poster upload.</summary>
    public List<string> UploadTypes { get; } = [];

    public List<string> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;

        Requests.Add($"{request.Method} {uri}");

        if (uri.Host != "jellyfin.test")
        {
            return Pages.TryGetValue(uri.ToString(), out var page)
                ? Bytes(page.Body, page.Type)
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var parts = uri.AbsolutePath.Trim('/').Split('/');

        if (parts is ["Items", var id, "RemoteImages"])
        {
            return Json(new { Images = RemoteImages.TryGetValue(id, out var images) ? images : [] });
        }

        if (parts is ["Items", var itemId, "Images", "Primary"])
        {
            if (request.Method == HttpMethod.Get)
            {
                return Posters.TryGetValue(itemId, out var poster)
                    ? Bytes(poster, "image/jpeg")
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            // Like the real thing: the picture arrives as base64 text.
            var text = await request.Content!.ReadAsStringAsync(cancellationToken);

            Posters[itemId] = Convert.FromBase64String(text);
            UploadTypes.Add(request.Content.Headers.ContentType!.MediaType!);

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Bytes(byte[] body, string type)
    {
        var content = new ByteArrayContent(body);

        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage Json(object value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value))
        };
}

[TestClass]
public class PosterTests : TempFolderTest
{
    private const string Film = "11111111111111111111111111111111";
    private const string Collection = "22222222222222222222222222222222";

    private FakePosterWeb _web = default!;
    private JellyfinSettings _settings = default!;
    private PosterService _posters = default!;

    [TestInitialize]
    public void Build()
    {
        _web = new FakePosterWeb();
        _settings = new JellyfinSettings();

        var http = new HttpClient(_web);

        _posters =
            new PosterService(
                http,
                new JellyfinClient(http, _settings),
                _settings);
    }

    private static JellyfinPosterItem FilmItem =>
        new(Film, "Alien", "Movie", 1979, true, TmdbId: "348");

    [TestMethod]
    public async Task Posters_come_from_Jellyfins_own_search_and_from_fanart_when_a_key_is_set()
    {
        _web.RemoteImages[Film] =
        [
            new { Url = "https://image.tmdb.org/t/p/original/a.jpg", ProviderName = "TheMovieDb", Language = "en", Width = 2000, Height = 3000 }
        ];

        _web.Pages["https://webservice.fanart.tv/v3/movies/348?api_key=my-key"] =
            (Encoding.UTF8.GetBytes(
                """{"movieposter":[{"url":"https://assets.fanart.tv/fanart/movies/348/movieposter/b.jpg","lang":"00"}]}"""),
             "application/json");

        Assert.HasCount(1, (await _posters.GetOptionsAsync(FilmItem)).Options, "No key: Jellyfin's only.");

        _settings.Settings.FanartApiKey = "my-key";

        var found = await _posters.GetOptionsAsync(FilmItem);

        Assert.IsEmpty(found.Notes);
        Assert.HasCount(2, found.Options);

        var tmdb = found.Options.Single(option => option.Source == "TheMovieDb");

        Assert.AreEqual("https://image.tmdb.org/t/p/w342/a.jpg", tmdb.ThumbnailUrl);
        Assert.AreEqual(2000, tmdb.Width);

        var fanart = found.Options.Single(option => option.Source == "fanart.tv");

        Assert.AreEqual("https://assets.fanart.tv/preview/movies/348/movieposter/b.jpg", fanart.ThumbnailUrl);
        Assert.AreEqual(string.Empty, fanart.Language);
    }

    [TestMethod]
    public async Task When_fanart_cannot_be_asked_Jellyfins_posters_are_still_offered_with_a_note()
    {
        _settings.Settings.FanartApiKey = "wrong-key";

        _web.RemoteImages[Film] =
        [
            new { Url = "https://image.tmdb.org/t/p/original/a.jpg", ProviderName = "TheMovieDb" }
        ];

        _web.Pages["https://webservice.fanart.tv/v3/movies/348?api_key=wrong-key"] =
            (Encoding.UTF8.GetBytes("not json"), "text/plain");

        var found = await _posters.GetOptionsAsync(FilmItem);

        Assert.HasCount(1, found.Options);
        Assert.HasCount(1, found.Notes);
    }

    [TestMethod]
    public async Task Choosing_a_poster_fetches_it_and_sets_it_in_Jellyfin_and_can_be_undone()
    {
        var old = new byte[] { 0xFF, 0xD8, 0xFF, 7, 7, 7 };

        _web.Posters[Film] = old;
        _web.Pages["https://posters.test/new.png"] = (FakePosterWeb.Png, "image/png");

        var previous = await _posters.SetFromUrlAsync(Film, "https://posters.test/new.png");

        CollectionAssert.AreEqual(FakePosterWeb.Png, _web.Posters[Film]);
        Assert.AreEqual("image/png", _web.UploadTypes.Single());
        CollectionAssert.AreEqual(old, previous!.Bytes);

        await _posters.SetFromImageAsync(Film, previous.Bytes);

        CollectionAssert.AreEqual(old, _web.Posters[Film]);
    }

    [TestMethod]
    public async Task The_type_sent_to_Jellyfin_is_what_the_file_is_not_what_the_site_called_it()
    {
        _web.Pages["https://posters.test/poster"] = (FakePosterWeb.Jpeg, "application/octet-stream");

        await _posters.SetFromUrlAsync(Film, "https://posters.test/poster");

        Assert.AreEqual("image/jpeg", _web.UploadTypes.Single());
    }

    [TestMethod]
    public async Task A_link_to_a_web_page_rather_than_a_picture_changes_nothing()
    {
        _web.Posters[Film] = FakePosterWeb.Jpeg;
        _web.Pages["https://posters.test/page"] = (Encoding.UTF8.GetBytes("<html>poster</html>"), "image/jpeg");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => _posters.SetFromUrlAsync(Film, "https://posters.test/page"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => _posters.SetFromUrlAsync(Film, "https://posters.test/missing.jpg"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => _posters.SetFromUrlAsync(Film, @"file:///C:/secret.jpg"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => _posters.SetFromImageAsync(Film, [1, 2, 3]));

        CollectionAssert.AreEqual(FakePosterWeb.Jpeg, _web.Posters[Film]);
        Assert.IsEmpty(_web.UploadTypes);
        Assert.IsFalse(_web.Requests.Any(request => request.Contains("secret")));
    }

    [TestMethod]
    public async Task A_collection_can_take_the_poster_of_a_film_inside_it()
    {
        _web.Posters[Film] = FakePosterWeb.Jpeg;

        var previous = await _posters.SetFromItemAsync(Collection, Film);

        Assert.IsNull(previous, "The collection had no poster before.");
        CollectionAssert.AreEqual(FakePosterWeb.Jpeg, _web.Posters[Collection]);
    }
}
