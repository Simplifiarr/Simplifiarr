using System.Text.RegularExpressions;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Utilities;

public static partial class SampleFileDetector
{
    // A real movie/episode can have "sample" in its title ("Sample Size"),
    // so the looser name match below only applies to small files; real
    // samples are a minute or two of video. Exact-pattern matches and
    // files inside a Sample folder have no size limit.
    private const long LooseNameMatchMaxBytes = 300L * 1024 * 1024;

    [GeneratedRegex(@"(^|[\s._\-\[\(])samples?([\s._\-\]\)]|\d|$)", RegexOptions.IgnoreCase)]
    private static partial Regex SampleToken();

    public static bool IsSample(
        FileRecord file)
    {
        var fileName =
            Path.GetFileNameWithoutExtension(
                file.FileName);

        if (string.Equals(
                fileName,
                "sample",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (fileName.EndsWith(
                "-sample",
                StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(
                ".sample",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IsInSampleFolder(file))
        {
            return true;
        }

        return file.SizeBytes < LooseNameMatchMaxBytes &&
               SampleToken().IsMatch(fileName);
    }

    /// <summary>
    /// True when any folder above the file is named "Sample" or "Samples"
    /// - the scene convention for a release's sample clip and its leftovers.
    /// </summary>
    public static bool IsInSampleFolder(
        FileRecord file)
    {
        var directory =
            Path.GetDirectoryName(file.FullPath);

        return directory?
            .Split(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
            .Any(part =>
                string.Equals(part, "sample", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, "samples", StringComparison.OrdinalIgnoreCase))
            == true;
    }
}
