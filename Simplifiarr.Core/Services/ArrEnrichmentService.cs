using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class ArrEnrichmentService
    : IArrEnrichmentService
{
    private readonly IRadarrClient _radarrClient;
    private readonly ISonarrClient _sonarrClient;
    private readonly IMediaCatalogService _catalogService;

    public ArrEnrichmentService(
        IRadarrClient radarrClient,
        ISonarrClient sonarrClient,
        IMediaCatalogService catalogService)
    {
        _radarrClient = radarrClient;
        _sonarrClient = sonarrClient;
        _catalogService = catalogService;
    }

    public async Task<ArrEnrichmentResult> EnrichFromRadarrAsync()
    {
        var movies =
            await _radarrClient.GetMoviesAsync();

        var result =
            new ArrEnrichmentResult
            {
                TotalFetched = movies.Count
            };

        var assets =
            ByPath(_catalogService.GetAssets());

        foreach (var movie in movies)
        {
            if (string.IsNullOrWhiteSpace(movie.FilePath))
            {
                result.Unmatched++;
                continue;
            }

            assets.TryGetValue(movie.FilePath, out var asset);

            if (asset is null)
            {
                result.Unmatched++;
                continue;
            }

            // Radarr's title/year are authoritative - prefer them over
            // whatever Simplifiarr's own filename parser guessed, since the
            // whole point of Arr enrichment is more accurate metadata.
            if (!string.IsNullOrWhiteSpace(movie.Title))
            {
                asset.Title = movie.Title;
            }

            if (movie.Year.HasValue)
            {
                asset.Year = movie.Year;
            }

            // Radarr matched this exact file as one of its movies, so it's
            // authoritative on MediaType too - corrects cases where the
            // filename parser guessed wrong at catalog-scan time (e.g. a
            // non-standard release name it misread as a TV episode).
            asset.MediaType = MediaType.Movie;

            asset.TmdbId = movie.TmdbId;
            asset.Genres = movie.Genres;
            asset.QualityProfileId = movie.QualityProfileId;
            asset.Monitored = movie.Monitored;

            result.Matched++;
            result.MatchedAssets.Add(asset);
        }

        if (result.Matched > 0)
        {
            _catalogService.SaveToDisk();
        }

        return result;
    }

    public async Task<ArrEnrichmentResult> EnrichFromSonarrAsync()
    {
        var episodes =
            await _sonarrClient.GetEpisodesAsync();

        var result =
            new ArrEnrichmentResult
            {
                TotalFetched = episodes.Count
            };

        var assets =
            ByPath(_catalogService.GetAssets());

        foreach (var episode in episodes)
        {
            if (string.IsNullOrWhiteSpace(episode.FilePath))
            {
                result.Unmatched++;
                continue;
            }

            assets.TryGetValue(episode.FilePath, out var asset);

            if (asset is null)
            {
                result.Unmatched++;
                continue;
            }

            // Sonarr's series title/season are authoritative - some shows
            // (absolute-numbered anime especially) don't have an "S00E00"
            // marker Simplifiarr's own parser can find, so the catalog's own
            // Title/Season can be wrong or missing per-episode. Sonarr always
            // knows the real series title and season, so prefer it.
            if (!string.IsNullOrWhiteSpace(episode.SeriesTitle))
            {
                asset.Title = episode.SeriesTitle;
            }

            if (episode.SeasonNumber.HasValue)
            {
                asset.Season = episode.SeasonNumber;
            }

            if (episode.EpisodeNumber.HasValue)
            {
                asset.Episode = episode.EpisodeNumber;
            }

            if (!string.IsNullOrWhiteSpace(episode.EpisodeTitle))
            {
                asset.EpisodeTitle = episode.EpisodeTitle;
            }

            // Sonarr matched this exact file as one of its episodes, so it's
            // authoritative on MediaType too - corrects cases where the
            // filename parser guessed wrong at catalog-scan time (e.g. "1x01"
            // separator style or absolute-numbered anime with no S00E00
            // marker at all, which no filename regex can reliably catch).
            asset.MediaType = MediaType.TvEpisode;

            asset.TvdbId = episode.TvdbId;
            asset.Genres = episode.Genres;
            asset.QualityProfileId = episode.QualityProfileId;
            asset.Monitored = episode.Monitored;

            result.Matched++;
            result.MatchedAssets.Add(asset);
        }

        if (result.Matched > 0)
        {
            _catalogService.SaveToDisk();
        }

        return result;
    }

    private static Dictionary<string, MediaAsset> ByPath(
        IEnumerable<MediaAsset> assets) =>
        assets
            .Where(asset => !string.IsNullOrWhiteSpace(asset.CanonicalPath))
            .GroupBy(asset => asset.CanonicalPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
}
