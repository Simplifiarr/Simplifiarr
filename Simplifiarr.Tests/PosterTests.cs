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

    /// <summary>"item id/picture type" -> its pictures, first one first.</summary>
    public Dictionary<string, List<byte[]>> Pictures { get; } = [];

    /// <summary>"item id/picture type" -> what Jellyfin's own image search finds.</summary>
    public Dictionary<string, List<object>> RemoteImages { get; } = [];

    /// <summary>Web address -> (body, content type) for anything else.</summary>
    public Dictionary<string, (byte[] Body, string Type)> Pages { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What was sent as the content type of each picture upload.</summary>
    public List<string> UploadTypes { get; } = [];

    public List<string> Requests { get; } = [];

    public List<byte[]> Of(string itemId, string type = "Primary") =>
        Pictures.TryGetValue($"{itemId}/{type}", out var pictures) ? pictures : [];

    public void Give(string itemId, string type, params byte[][] pictures) =>
        Pictures[$"{itemId}/{type}"] = [.. pictures];

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
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);

        if (parts is ["Items"] && query["Ids"] is { } ids)
        {
            return Json(new { Items = new[] { Item(ids) }, TotalRecordCount = 1 });
        }

        if (parts is ["Items", var id, "RemoteImages"])
        {
            return Json(new { Images = RemoteImages.TryGetValue($"{id}/{query["type"]}", out var images) ? images : [] });
        }

        if (parts is ["Items", var itemId, "Images", var type, ..])
        {
            var pictures = Of(itemId, type);
            var index = parts.Length > 4 ? int.Parse(parts[4]) : 0;

            if (request.Method == HttpMethod.Get)
            {
                return index < pictures.Count
                    ? Bytes(pictures[index], "image/jpeg")
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (request.Method == HttpMethod.Delete)
            {
                if (index >= pictures.Count)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                pictures.RemoveAt(index);

                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (parts is [.., "Index"])
            {
                var other = int.Parse(query["newIndex"]!);

                (pictures[index], pictures[other]) = (pictures[other], pictures[index]);

                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            // Like the real thing: the picture arrives as base64 text, and
            // a backdrop is always added to the end.
            var text = await request.Content!.ReadAsStringAsync(cancellationToken);
            var bytes = Convert.FromBase64String(text);

            if (type == "Backdrop")
            {
                Pictures[$"{itemId}/{type}"] = [.. pictures, bytes];
            }
            else
            {
                Pictures[$"{itemId}/{type}"] = [bytes];
            }

            UploadTypes.Add(request.Content.Headers.ContentType!.MediaType!);

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private object Item(string id) =>
        new
        {
            Id = id,
            Name = "Alien",
            Type = "Movie",
            ImageTags =
                Pictures
                    .Where(entry => entry.Key.StartsWith(id + "/") && !entry.Key.EndsWith("/Backdrop") && entry.Value.Count > 0)
                    .ToDictionary(entry => entry.Key.Split('/')[1], _ => "tag"),
            BackdropImageTags = Of(id, "Backdrop").Select((_, index) => $"tag{index}").ToArray()
        };

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

    private static readonly byte[] Old = [0xFF, 0xD8, 0xFF, 7, 7, 7];
    private static readonly byte[] Second = [0xFF, 0xD8, 0xFF, 8, 8, 8];

    private FakePosterWeb _web = default!;
    private JellyfinSettings _settings = default!;
    private JellyfinClient _jellyfin = default!;
    private PosterService _posters = default!;

    [TestInitialize]
    public void Build()
    {
        _web = new FakePosterWeb();
        _settings = new JellyfinSettings();

        var http = new HttpClient(_web);

        _jellyfin = new JellyfinClient(http, _settings);

        _posters =
            new PosterService(
                http,
                _jellyfin,
                _settings);
    }

    private static JellyfinPosterItem FilmItem =>
        new(Film, "Alien", "Movie", 1979, true, TmdbId: "348");

    private void FanartHas(string kind, string id, string json, string key = "my-key")
    {
        _settings.Settings.FanartApiKey = key;
        _web.Pages[$"https://webservice.fanart.tv/v3/{kind}/{id}?api_key={key}"] =
            (Encoding.UTF8.GetBytes(json), "application/json");
    }

    [TestMethod]
    public async Task Posters_come_from_Jellyfins_own_search_and_from_fanart_when_a_key_is_set()
    {
        _web.RemoteImages[$"{Film}/Primary"] =
        [
            new { Url = "https://image.tmdb.org/t/p/original/a.jpg", ProviderName = "TheMovieDb", Language = "en", Width = 2000, Height = 3000 }
        ];

        Assert.HasCount(1, (await _posters.GetOptionsAsync(FilmItem)).Options, "No key: Jellyfin's only.");

        FanartHas("movies", "348", """{"movieposter":[{"url":"https://assets.fanart.tv/fanart/movies/348/movieposter/b.jpg","lang":"00"}]}""");

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
    public async Task Each_kind_of_picture_is_searched_for_by_its_own_name()
    {
        _web.RemoteImages[$"{Film}/Logo"] =
        [
            new { Url = "https://image.tmdb.org/t/p/original/logo.png", ProviderName = "TheMovieDb" }
        ];

        FanartHas(
            "movies",
            "348",
            """
            {"movieposter":[{"url":"https://assets.fanart.tv/fanart/p.jpg"}],
             "hdmovielogo":[{"url":"https://assets.fanart.tv/fanart/hd.png","lang":"en"}],
             "movielogo":[{"url":"https://assets.fanart.tv/fanart/sd.png","lang":"en"}],
             "moviebackground":[{"url":"https://assets.fanart.tv/fanart/bg.jpg"}]}
            """);

        var logos = await _posters.GetOptionsAsync(FilmItem, "Logo");

        CollectionAssert.AreEqual(
            new[] { "https://image.tmdb.org/t/p/original/logo.png", "https://assets.fanart.tv/fanart/hd.png", "https://assets.fanart.tv/fanart/sd.png" },
            logos.Options.Select(option => option.Url).ToArray());
        Assert.AreEqual("https://image.tmdb.org/t/p/w780/logo.png", logos.Options[0].ThumbnailUrl, "Wide pictures get a bigger preview.");

        var backdrops = await _posters.GetOptionsAsync(FilmItem, "Backdrop");

        Assert.AreEqual("https://assets.fanart.tv/fanart/bg.jpg", backdrops.Options.Single().Url);
        Assert.AreEqual(1, _web.Requests.Count(request => request.Contains("fanart.tv/v3")), "fanart.tv is asked once per title.");
    }

    [TestMethod]
    public async Task A_seasons_pictures_are_its_own_and_those_for_every_season()
    {
        FanartHas(
            "tv",
            "81189",
            """
            {"seasonposter":[
                {"url":"https://assets.fanart.tv/fanart/s1.jpg","season":"1"},
                {"url":"https://assets.fanart.tv/fanart/s2.jpg","season":"2"},
                {"url":"https://assets.fanart.tv/fanart/all.jpg","season":"all"}],
             "tvposter":[{"url":"https://assets.fanart.tv/fanart/show.jpg"}]}
            """);

        var season =
            new JellyfinPosterItem(Film, "Season 2", "Season", null, false)
            {
                SeasonNumber = 2,
                SeriesTvdbId = "81189"
            };

        var found = await _posters.GetOptionsAsync(season);

        CollectionAssert.AreEquivalent(
            new[] { "https://assets.fanart.tv/fanart/s2.jpg", "https://assets.fanart.tv/fanart/all.jpg" },
            found.Options.Select(option => option.Url).ToArray());
    }

    [TestMethod]
    public async Task When_fanart_cannot_be_asked_Jellyfins_posters_are_still_offered_with_a_note()
    {
        _web.RemoteImages[$"{Film}/Primary"] =
        [
            new { Url = "https://image.tmdb.org/t/p/original/a.jpg", ProviderName = "TheMovieDb" }
        ];

        FanartHas("movies", "348", "not json", key: "wrong-key");

        var found = await _posters.GetOptionsAsync(FilmItem);

        Assert.HasCount(1, found.Options);
        Assert.HasCount(1, found.Notes);
    }

    [TestMethod]
    public async Task Choosing_a_poster_fetches_it_and_sets_it_in_Jellyfin_and_can_be_undone()
    {
        _web.Give(Film, "Primary", Old);
        _web.Pages["https://posters.test/new.png"] = (FakePosterWeb.Png, "image/png");

        var change = await _posters.SetFromUrlAsync(Film, "https://posters.test/new.png");

        CollectionAssert.AreEqual(FakePosterWeb.Png, _web.Of(Film).Single());
        Assert.AreEqual("image/png", _web.UploadTypes.Single());
        CollectionAssert.AreEqual(Old, change.Previous!.Bytes);

        await _posters.UndoAsync(change);

        CollectionAssert.AreEqual(Old, _web.Of(Film).Single());
    }

    [TestMethod]
    public async Task A_logo_is_set_as_a_logo_and_undoing_one_that_was_not_there_removes_it()
    {
        _web.Give(Film, "Primary", Old);

        var change = await _posters.SetFromImageAsync(Film, FakePosterWeb.Png, "Logo");

        CollectionAssert.AreEqual(FakePosterWeb.Png, _web.Of(Film, "Logo").Single());
        CollectionAssert.AreEqual(Old, _web.Of(Film).Single(), "The poster is left alone.");
        Assert.IsNull(change.Previous);

        await _posters.UndoAsync(change);

        Assert.IsEmpty(_web.Of(Film, "Logo"));
    }

    [TestMethod]
    public async Task Changing_the_main_backdrop_keeps_the_others_in_place()
    {
        _web.Give(Film, "Backdrop", Old, Second);

        var change = await _posters.SetFromImageAsync(Film, FakePosterWeb.Jpeg, "Backdrop", BackdropChoice.ReplaceMain);

        var backdrops = _web.Of(Film, "Backdrop");

        Assert.HasCount(2, backdrops);
        CollectionAssert.AreEqual(FakePosterWeb.Jpeg, backdrops[0], "The new one is first.");
        CollectionAssert.AreEqual(Second, backdrops[1], "The second is untouched.");
        CollectionAssert.AreEqual(Old, change.Previous!.Bytes);
        Assert.IsFalse(change.Added);

        await _posters.UndoAsync(change);

        backdrops = _web.Of(Film, "Backdrop");

        CollectionAssert.AreEqual(Old, backdrops[0]);
        CollectionAssert.AreEqual(Second, backdrops[1]);
    }

    [TestMethod]
    public async Task Adding_a_backdrop_puts_it_after_the_others_and_undo_takes_just_that_one_away()
    {
        _web.Give(Film, "Backdrop", Old);

        var change = await _posters.SetFromImageAsync(Film, FakePosterWeb.Jpeg, "Backdrop", BackdropChoice.Add);

        Assert.IsTrue(change.Added);
        Assert.AreEqual(2, change.Backdrops);
        CollectionAssert.AreEqual(FakePosterWeb.Jpeg, _web.Of(Film, "Backdrop")[1]);

        await _posters.UndoAsync(change);

        CollectionAssert.AreEqual(Old, _web.Of(Film, "Backdrop").Single());
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
        _web.Give(Film, "Primary", FakePosterWeb.Jpeg);
        _web.Pages["https://posters.test/page"] = (Encoding.UTF8.GetBytes("<html>poster</html>"), "image/jpeg");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => _posters.SetFromUrlAsync(Film, "https://posters.test/page"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => _posters.SetFromUrlAsync(Film, "https://posters.test/missing.jpg"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => _posters.SetFromUrlAsync(Film, @"file:///C:/secret.jpg"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => _posters.SetFromImageAsync(Film, [1, 2, 3]));

        CollectionAssert.AreEqual(FakePosterWeb.Jpeg, _web.Of(Film).Single());
        Assert.IsEmpty(_web.UploadTypes);
        Assert.IsFalse(_web.Requests.Any(request => request.Contains("secret")));
    }

    [TestMethod]
    public async Task A_collection_can_take_the_poster_of_a_film_inside_it()
    {
        _web.Give(Film, "Primary", FakePosterWeb.Jpeg);

        var change = await _posters.SetFromItemAsync(Collection, Film);

        Assert.IsNull(change.Previous, "The collection had no poster before.");
        CollectionAssert.AreEqual(FakePosterWeb.Jpeg, _web.Of(Collection).Single());
    }

    [TestMethod]
    public async Task Jellyfin_says_which_pictures_an_item_has()
    {
        _web.Give(Film, "Primary", Old);
        _web.Give(Film, "Logo", FakePosterWeb.Png);
        _web.Give(Film, "Backdrop", Old, Second);

        var item = (await _jellyfin.GetPosterItemAsync(Film))!;

        Assert.IsTrue(item.HasPoster);
        Assert.IsTrue(item.Has("Logo"));
        Assert.IsTrue(item.Has("Backdrop"));
        Assert.IsFalse(item.Has("Thumb"));
        Assert.AreEqual(2, item.Backdrops);
    }

    [TestMethod]
    public void The_picture_types_offered_depend_on_what_was_chosen_and_the_kind_of_item()
    {
        CollectionAssert.AreEqual(
            new[] { "Primary", "Backdrop", "Logo", "Thumb" },
            PictureTypes.For("Movie", null).Select(picture => picture.Type).ToArray(),
            "The usual four until something is chosen.");

        CollectionAssert.AreEqual(
            new[] { "Primary", "Disc" },
            PictureTypes.For("Movie", ["Disc"]).Select(picture => picture.Type).ToArray(),
            "The poster is always there.");

        CollectionAssert.AreEqual(
            new[] { "Primary", "Thumb" },
            PictureTypes.For("Season", ["Logo", "Thumb", "Disc"]).Select(picture => picture.Type).ToArray(),
            "Seasons have no logo or disc.");
    }
}
