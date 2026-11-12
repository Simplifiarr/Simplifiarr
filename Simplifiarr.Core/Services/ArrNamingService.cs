using System.Collections.Concurrent;
using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Names downloads the way Sonarr and Radarr do, so a hardlink lands where
/// they would have put the file.
/// </summary>
public interface IArrNamingService
{
    /// <summary>
    /// Asks Sonarr (TV) or Radarr (films) to read the release name. Their
    /// title, year, season and episode replace Simplifiarr's guess, their
    /// folder is used when they already have the show or film, and new
    /// folders are named with their folder formats. Returns the release
    /// unchanged when neither is set up.
    /// </summary>
    Task<ParsedRelease> IdentifyAsync(ParsedRelease release);
}

/// <inheritdoc cref="IArrNamingService"/>
public class ArrNamingService : IArrNamingService
{
    // A scan asks about each download once; a later scan asks again, so a
    // show added to Sonarr in between is picked up.
    private static readonly TimeSpan RememberFor = TimeSpan.FromMinutes(10);

    private readonly ISonarrClient _sonarr;
    private readonly IRadarrClient _radarr;

    private readonly ConcurrentDictionary<string, (ArrIdentity? Identity, DateTime AskedUtc)> _answers =
        new(StringComparer.OrdinalIgnoreCase);

    private (ArrFolderFormats? Formats, DateTime AskedUtc) _sonarrFormats;
    private (ArrFolderFormats? Formats, DateTime AskedUtc) _radarrFormats;

    public ArrNamingService(
        ISonarrClient sonarr,
        IRadarrClient radarr)
    {
        _sonarr = sonarr;
        _radarr = radarr;
    }

    public async Task<ParsedRelease> IdentifyAsync(
        ParsedRelease release)
    {
        if (string.IsNullOrWhiteSpace(release.OriginalName))
        {
            return release;
        }

        var key = $"{(release.IsTv ? "tv" : "movie")}|{release.OriginalName}";

        ArrIdentity? identity;

        if (_answers.TryGetValue(key, out var remembered) && DateTime.UtcNow - remembered.AskedUtc < RememberFor)
        {
            identity = remembered.Identity;
        }
        else
        {
            identity = release.IsTv
                ? await _sonarr.ParseAsync(release.OriginalName)
                : await _radarr.ParseAsync(release.OriginalName);

            _answers[key] = (identity, DateTime.UtcNow);
        }

        var formats = await GetFormatsAsync(release.IsTv);

        // Neither Sonarr nor Radarr is set up: Simplifiarr's own naming.
        if (identity is null && formats is null)
        {
            return release;
        }

        formats ??= new ArrFolderFormats();

        var result = release.Clone();

        if (identity is not null)
        {
            result.IdentifiedBy = identity.Source;
            result.NormalizedTitle = identity.Title;
            result.Year = identity.Year ?? (release.IsTv ? null : release.Year);
            result.Season = identity.Season ?? release.Season;
            result.Episode = identity.Episode ?? release.Episode;
            result.KnownFolder = identity.KnownFolder;
            result.UsesSeasonFolders = identity.UsesSeasonFolders;
        }

        result.FolderName =
            result.IsTv
                ? ArrFolderNamer.Series(formats.SeriesFolderFormat, result.NormalizedTitle, result.Year, identity?.TvdbId, identity?.ImdbId)
                : ArrFolderNamer.Movie(formats.MovieFolderFormat, result.NormalizedTitle, result.Year, identity?.TmdbId, identity?.ImdbId);

        if (result.IsTv)
        {
            var season = result.Season ?? 1;

            result.SeasonFolderName =
                ArrFolderNamer.Season(season == 0 ? formats.SpecialsFolderFormat : formats.SeasonFolderFormat, season);
        }

        return result;
    }

    private async Task<ArrFolderFormats?> GetFormatsAsync(
        bool tv)
    {
        var cached = tv ? _sonarrFormats : _radarrFormats;

        if (cached.AskedUtc != default && DateTime.UtcNow - cached.AskedUtc < RememberFor)
        {
            return cached.Formats;
        }

        var formats = tv
            ? await _sonarr.GetFolderFormatsAsync()
            : await _radarr.GetFolderFormatsAsync();

        if (tv)
        {
            _sonarrFormats = (formats, DateTime.UtcNow);
        }
        else
        {
            _radarrFormats = (formats, DateTime.UtcNow);
        }

        return formats;
    }
}
