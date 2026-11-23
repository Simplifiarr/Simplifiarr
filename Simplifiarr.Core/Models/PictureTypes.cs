namespace Simplifiarr.Core.Models;

/// <summary>
/// One kind of picture Jellyfin keeps for a title.
/// </summary>
/// <param name="Type">Jellyfin's name for it ("Primary", "Backdrop"...).</param>
/// <param name="Label">What the page calls it.</param>
/// <param name="FileName">The file Jellyfin saves next to the media.</param>
/// <param name="Shape">How it is drawn: poster, wide, banner, logo or disc.</param>
/// <param name="Many">Jellyfin can hold several of these for one title.</param>
/// <param name="About">One line on where it shows up.</param>
/// <param name="ItemTypes">The kinds of item that have this picture.</param>
public sealed record PictureType(
    string Type,
    string Label,
    string FileName,
    string Shape,
    bool Many,
    string About,
    IReadOnlyList<string> ItemTypes);

public static class PictureTypes
{
    public const string Poster = "Primary";
    public const string Backdrop = "Backdrop";

    private static readonly string[] Titles = ["Movie", "Series", "BoxSet"];
    private static readonly string[] TitlesAndSeasons = ["Movie", "Series", "BoxSet", "Season"];

    public static readonly IReadOnlyList<PictureType> All =
    [
        new(Poster, "Poster", "folder.jpg", "poster", false,
            "The main picture everywhere.", TitlesAndSeasons),
        new(Backdrop, "Backdrop", "backdrop.jpg", "wide", true,
            "The big picture behind the details page. Some apps rotate through several.", Titles),
        new("Logo", "Logo", "logo.png", "logo", false,
            "The title as see-through artwork, shown over the backdrop.", Titles),
        new("Thumb", "Landscape", "landscape.jpg", "wide", false,
            "A wide picture with the title on it, used for rows and Continue Watching.", TitlesAndSeasons),
        new("Banner", "Banner", "banner.jpg", "banner", false,
            "A long strip, used by some themes and apps.", TitlesAndSeasons),
        new("Art", "Clear art", "clearart.png", "logo", false,
            "See-through character art, shown by a few apps.", Titles),
        new("Disc", "Disc", "disc.png", "disc", false,
            "A round disc picture, shown by a few apps.", Titles)
    ];

    /// <summary>What the Posters page shows until someone chooses.</summary>
    public static readonly IReadOnlyList<string> Defaults =
        [Poster, Backdrop, "Logo", "Thumb"];

    public static PictureType Find(string type) =>
        All.FirstOrDefault(picture => string.Equals(picture.Type, type, StringComparison.OrdinalIgnoreCase))
        ?? All[0];

    /// <summary>
    /// The chosen picture types this kind of item has, in the usual order.
    /// The poster is always there.
    /// </summary>
    public static IReadOnlyList<PictureType> For(
        string itemType,
        IEnumerable<string>? chosen)
    {
        var wanted =
            new HashSet<string>(chosen ?? Defaults, StringComparer.OrdinalIgnoreCase) { Poster };

        return All
            .Where(picture => wanted.Contains(picture.Type) && picture.ItemTypes.Contains(itemType))
            .ToList();
    }
}
