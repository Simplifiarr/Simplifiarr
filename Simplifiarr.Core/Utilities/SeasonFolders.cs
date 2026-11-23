using System.Text.RegularExpressions;

namespace Simplifiarr.Core.Utilities;

/// <summary>Reading season numbers from a show's folder names.</summary>
public static class SeasonFolders
{
    private static readonly Regex SeasonName =
        new(@"^(?:season|series|s)[\s._-]*(\d{1,4})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The season a folder holds: "Season 6" and "Season 06" are 6, "S06"
    /// is 6, "Specials" is 0. Padded is true for a leading zero
    /// ("Season 06"). Null when the name isn't a season folder.
    /// </summary>
    public static (int Season, bool Padded)? NumberOf(string folderName)
    {
        var name = folderName.Trim();

        if (string.Equals(name, "Specials", StringComparison.OrdinalIgnoreCase))
        {
            return (0, false);
        }

        var match = SeasonName.Match(name);

        if (!match.Success)
        {
            return null;
        }

        var digits = match.Groups[1].Value;

        return (int.Parse(digits), digits.Length > 1 && digits[0] == '0');
    }
}
