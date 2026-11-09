using Simplifiarr.Core.Models;

namespace Simplifiarr.Web.Support;

/// <summary>
/// Groups a flat list of TV episode assets into Show -&gt; Season -&gt;
/// Episodes, for the nested browsing UI shared by ArrIntegration and
/// Collections (previously duplicated verbatim in both pages).
/// </summary>
public sealed class ShowGroup
{
    public string Title { get; set; } =
        string.Empty;

    public int EpisodeCount { get; set; }

    public List<SeasonGroup> Seasons { get; set; } =
        [];

    public static List<ShowGroup> GroupByShow(
        IEnumerable<MediaAsset> episodes)
    {
        return episodes
            .GroupBy(asset => asset.Title)
            .OrderBy(group => group.Key)
            .Select(group => new ShowGroup
            {
                Title = group.Key,
                EpisodeCount = group.Count(),
                Seasons = group
                    .GroupBy(asset => asset.Season)
                    .OrderBy(seasonGroup => seasonGroup.Key)
                    .Select(seasonGroup => new SeasonGroup
                    {
                        Season = seasonGroup.Key,
                        Episodes = seasonGroup
                            .OrderBy(asset => asset.Episode)
                            .ToList()
                    })
                    .ToList()
            })
            .ToList();
    }
}

public sealed class SeasonGroup
{
    public int? Season { get; set; }

    public List<MediaAsset> Episodes { get; set; } =
        [];
}
