using System.Text;

namespace Simplifiarr.Core.Utilities;

public static class FileNameSanitizer
{
    private static readonly char[] InvalidChars =
        Path.GetInvalidFileNameChars();

    public static string Sanitize(
        string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        var builder =
            new StringBuilder(name.Length);

        foreach (var character in name)
        {
            builder.Append(
                InvalidChars.Contains(character)
                    ? ' '
                    : character);
        }

        var result =
            builder.ToString();

        while (result.Contains("  "))
        {
            result = result.Replace("  ", " ");
        }

        return result.Trim();
    }
}
