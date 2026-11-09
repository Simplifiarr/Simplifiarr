namespace Simplifiarr.Core.Services;

public class JellyfinClient : IJellyfinClient
{
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

            request.Headers.TryAddWithoutValidation(
                "Authorization",
                $"MediaBrowser Token=\"{apiKey}\"");

            using var response =
                await _httpClient.SendAsync(request);

            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
