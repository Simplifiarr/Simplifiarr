using System.Text.Json;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public class JellyfinClient : IJellyfinClient
{
    // Item ids travel in the web address, which has a length limit.
    private const int IdsPerRequest = 40;

    private const int PageSize = 2000;

    private const string LibraryItemTypes =
        "Movie,Episode,Audio,AudioBook,Book";

    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;

    public JellyfinClient(
        HttpClient httpClient,
        ISettingsService settingsService)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
    }

    public async Task<bool> TestConnectionAsync()
    {
        var settings =
            await _settingsService.GetSettingsAsync();

        return await TestConnectionAsync(
            settings.JellyfinUrl,
            settings.JellyfinApiKey);
    }

    public async Task<bool> TestConnectionAsync(
        string url,
        string apiKey)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            string.IsNullOrWhiteSpace(apiKey))
        {
            return false;
        }

        if (!Uri.TryCreate(
                url.TrimEnd('/') + "/System/Info",
                UriKind.Absolute,
                out var uri))
        {
            return false;
        }

        try
        {
            using var request =
                new HttpRequestMessage(HttpMethod.Get, uri);

            AddKey(request, apiKey);

            using var response =
                await _httpClient.SendAsync(request);

            return response.IsSuccessStatusCode;
        }
        catch (Exception exception)
        {
            AppLog.Warn("Jellyfin connection test failed.", exception);

            return false;
        }
    }

    public async Task<IReadOnlyList<JellyfinItem>> GetLibraryItemsAsync()
    {
        var items = new List<JellyfinItem>();

        for (var start = 0; ; start += PageSize)
        {
            // CollapseBoxSetItems=false matters: by default Jellyfin leaves
            // out every film that is in a collection and lists the
            // collection instead. Those films then look as if they had left
            // Jellyfin, get taken out of the collection, reappear, and get
            // added back - on alternate sends.
            var page =
                await GetItemsAsync(
                    $"/Items?Recursive=true&IncludeItemTypes={LibraryItemTypes}&CollapseBoxSetItems=false" +
                    $"&Fields=Path&EnableTotalRecordCount=false&StartIndex={start}&Limit={PageSize}");

            items.AddRange(
                page.Where(item => !string.IsNullOrWhiteSpace(item.Path)));

            if (page.Count < PageSize)
            {
                return items;
            }
        }
    }

    public async Task<IReadOnlyList<JellyfinItem>> GetCollectionsAsync() =>
        await GetItemsAsync(
            "/Items?Recursive=true&IncludeItemTypes=BoxSet&EnableTotalRecordCount=false");

    public async Task<IReadOnlyList<string>> GetCollectionItemIdsAsync(
        string collectionId)
    {
        // Jellyfin only lists what is directly in a collection when asked
        // on behalf of a user; without one it answers with nothing.
        // (Asking "recursively" instead would also list every episode
        // inside a show that is in the collection.)
        var userId = await GetAdministratorIdAsync();

        var children =
            await GetItemsAsync(
                $"/Items?ParentId={Uri.EscapeDataString(collectionId)}" +
                $"&UserId={Uri.EscapeDataString(userId)}&EnableTotalRecordCount=false");

        return children
            .Select(item => item.Id)
            .ToList();
    }

    private async Task<string> GetAdministratorIdAsync()
    {
        using var document =
            await SendAsync(HttpMethod.Get, "/Users");

        if (document is not null &&
            document.RootElement.ValueKind == JsonValueKind.Array)
        {
            string? first = null;

            foreach (var user in document.RootElement.EnumerateArray())
            {
                var id = Text(user, "Id");

                if (id.Length == 0)
                {
                    continue;
                }

                first ??= id;

                // An administrator sees everything; another user may have
                // libraries hidden from them.
                if (user.TryGetProperty("Policy", out var policy) &&
                    policy.TryGetProperty("IsAdministrator", out var isAdmin) &&
                    isAdmin.ValueKind == JsonValueKind.True)
                {
                    return id;
                }
            }

            if (first is not null)
            {
                return first;
            }
        }

        throw new InvalidOperationException(
            "Jellyfin didn't list any users, so the collection's contents couldn't be read.");
    }

    public async Task<int> CountCollectionLibrariesAsync()
    {
        using var document =
            await SendAsync(HttpMethod.Get, "/Library/VirtualFolders");

        if (document is null ||
            document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        return document.RootElement
            .EnumerateArray()
            .Count(folder =>
                folder.TryGetProperty("CollectionType", out var type) &&
                string.Equals(
                    type.GetString(),
                    "boxsets",
                    StringComparison.OrdinalIgnoreCase));
    }

    public async Task<string> CreateCollectionAsync(
        string name,
        IReadOnlyCollection<string> itemIds)
    {
        // Created with its first few items in the same call; the rest are
        // added afterwards so the address never gets too long.
        var first = itemIds.Take(IdsPerRequest).ToList();

        using var document =
            await SendAsync(
                HttpMethod.Post,
                $"/Collections?name={Uri.EscapeDataString(name)}&ids={string.Join(',', first)}");

        var id =
            document is not null &&
            document.RootElement.TryGetProperty("Id", out var idProperty)
                ? idProperty.GetString()
                : null;

        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException(
                "Jellyfin accepted the request but didn't say which collection it made.");
        }

        await AddToCollectionAsync(
            id,
            itemIds.Skip(IdsPerRequest).ToList());

        return id;
    }

    public async Task AddToCollectionAsync(
        string collectionId,
        IReadOnlyCollection<string> itemIds)
    {
        foreach (var batch in itemIds.Chunk(IdsPerRequest))
        {
            using var _ =
                await SendAsync(
                    HttpMethod.Post,
                    $"/Collections/{Uri.EscapeDataString(collectionId)}/Items?ids={string.Join(',', batch)}");
        }
    }

    public async Task RemoveFromCollectionAsync(
        string collectionId,
        IReadOnlyCollection<string> itemIds)
    {
        foreach (var batch in itemIds.Chunk(IdsPerRequest))
        {
            using var _ =
                await SendAsync(
                    HttpMethod.Delete,
                    $"/Collections/{Uri.EscapeDataString(collectionId)}/Items?ids={string.Join(',', batch)}");
        }
    }

    private async Task<List<JellyfinItem>> GetItemsAsync(
        string pathAndQuery)
    {
        using var document =
            await SendAsync(HttpMethod.Get, pathAndQuery);

        var items = new List<JellyfinItem>();

        if (document is null ||
            !document.RootElement.TryGetProperty("Items", out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (var element in array.EnumerateArray())
        {
            var id = Text(element, "Id");

            if (id.Length == 0)
            {
                continue;
            }

            items.Add(
                new JellyfinItem(
                    id,
                    Text(element, "Name"),
                    Text(element, "Type"),
                    Text(element, "Path"),
                    Text(element, "SeriesId"),
                    Text(element, "SeasonId")));
        }

        return items;
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>
    /// Sends one request to the Jellyfin in Settings. Returns the reply as
    /// JSON, or null when the reply had no body. Throws with a plain
    /// sentence when Jellyfin can't be reached or says no.
    /// </summary>
    private async Task<JsonDocument?> SendAsync(
        HttpMethod method,
        string pathAndQuery)
    {
        var settings =
            await _settingsService.GetSettingsAsync();

        if (string.IsNullOrWhiteSpace(settings.JellyfinUrl) ||
            string.IsNullOrWhiteSpace(settings.JellyfinApiKey))
        {
            throw new InvalidOperationException(
                "Jellyfin isn't set up yet. Add its address and API key in Settings, under Connections.");
        }

        if (!Uri.TryCreate(
                settings.JellyfinUrl.TrimEnd('/') + pathAndQuery,
                UriKind.Absolute,
                out var uri))
        {
            throw new InvalidOperationException(
                "The Jellyfin address in Settings isn't a valid web address.");
        }

        using var request =
            new HttpRequestMessage(method, uri);

        AddKey(request, settings.JellyfinApiKey);

        HttpResponseMessage response;

        try
        {
            response = await _httpClient.SendAsync(request);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException(
                $"Couldn't reach Jellyfin at {settings.JellyfinUrl}. Is it running?",
                exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    (int)response.StatusCode is 401 or 403
                        ? "Jellyfin refused the request. Check the API key in Settings."
                        : $"Jellyfin answered with an error ({(int)response.StatusCode} {response.ReasonPhrase}).");
            }

            var body =
                await response.Content.ReadAsStringAsync();

            return string.IsNullOrWhiteSpace(body)
                ? null
                : JsonDocument.Parse(body);
        }
    }

    private static void AddKey(
        HttpRequestMessage request,
        string apiKey) =>
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            $"MediaBrowser Token=\"{apiKey}\"");
}
