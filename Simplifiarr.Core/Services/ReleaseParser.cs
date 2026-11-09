using System.Text.RegularExpressions;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class ReleaseParser
    : IReleaseParser
{
    private static readonly string[] NoiseTokens =
    [
        "2160P",
        "1080P",
        "720P",
        "480P",
        "4K",

        "WEB",
        "WEBDL",
        "WEB-DL",
        "WEBRIP",

        "BLURAY",
        "BLU-RAY",
        "REMUX",

        "HDR",
        "DV",

        "X264",
        "X265",
        "H264",
        "H265",
        "HEVC",
        "AV1",
        "VP9",

        "MKV",
        "MP4",
        "AVI",
        "MOV",

        "AAC",
        "AAC5",
        "DD",
        "DDP",
        "DTS",
        "ATMOS",

"PLAY",
"DL",

"DD2.0",
"DD2",
"0",
"DD5",
"DDP5",
"DDP51",

"SASUKEDUCK",
"WOU",
"LAWLESS",
"YTS",
"RARBG",
"ETHEL",
"KONTRAST"
    ];

    public ParsedRelease Parse(
        string releaseName)
    {
        var result =
            new ParsedRelease
            {
                OriginalName = releaseName
            };

        if (string.IsNullOrWhiteSpace(releaseName))
        {
            return result;
        }

        var normalized =
            releaseName
                .Replace('.', ' ')
                .Replace('_', ' ')
                .Replace('-', ' ');



        var resolutionMatch =
            Regex.Match(
                normalized,
                @"\b(2160p|1080p|720p|480p)\b",
                RegexOptions.IgnoreCase);

        if (resolutionMatch.Success)
        {
            result.Resolution =
                resolutionMatch.Value.ToUpperInvariant();
        }

        var codecMatch =
            Regex.Match(
                normalized,
                @"\b(HEVC|H265|X265|H264|X264|AV1|VP9)\b",
                RegexOptions.IgnoreCase);

        if (codecMatch.Success)
        {
            result.Codec =
                codecMatch.Value.ToUpperInvariant();
        }
        var episodeMatch =
            Regex.Match(
                normalized,
                @"S(?<season>\d{1,4})E(?<episode>\d{1,4})",
                RegexOptions.IgnoreCase);

        if (episodeMatch.Success)
        {
            result.IsTv = true;

            result.Season =
                int.Parse(
                    episodeMatch.Groups["season"].Value);

            result.Episode =
                int.Parse(
                    episodeMatch.Groups["episode"].Value);

            normalized =
                normalized.Substring(
                    0,
                    episodeMatch.Index);
        }
        else
        {
            // Some shows (older releases, some anime) use "1x01" instead of
            // "S01E01" - capped at 1-2 season digits / 1-3 episode digits so
            // this doesn't false-positive on resolution tokens like
            // "1920x1080" or "720x480", whose components run 3-4 digits.
            var separatorEpisodeMatch =
                Regex.Match(
                    normalized,
                    @"\b(?<season>\d{1,2})x(?<episode>\d{1,3})\b",
                    RegexOptions.IgnoreCase);

            if (separatorEpisodeMatch.Success)
            {
                result.IsTv = true;

                result.Season =
                    int.Parse(
                        separatorEpisodeMatch.Groups["season"].Value);

                result.Episode =
                    int.Parse(
                        separatorEpisodeMatch.Groups["episode"].Value);

                normalized =
                    normalized.Substring(
                        0,
                        separatorEpisodeMatch.Index);
            }
            else
            {
                var seasonMatch =
                    Regex.Match(
                        normalized,
                        @"S(?<season>\d{1,4})\b",
                        RegexOptions.IgnoreCase);

                if (seasonMatch.Success)
                {
                    result.IsTv = true;

                    result.Season =
                        int.Parse(
                            seasonMatch.Groups["season"].Value);

                    normalized =
                        normalized.Substring(
                            0,
                            seasonMatch.Index);
                }
            }
        }


        var yearMatch =
            Regex.Match(
                normalized,
                @"\b(19\d{2}|20\d{2})\b");

        if (yearMatch.Success)
        {
            result.Year =
                int.Parse(yearMatch.Value);

            normalized =
                normalized.Substring(
                    0,
                    yearMatch.Index);
        }

        foreach (var token in NoiseTokens)
        {
            normalized =
                Regex.Replace(
                    normalized,
                    $@"\b{Regex.Escape(token)}\b",
                    string.Empty,
                    RegexOptions.IgnoreCase);
        }


        normalized =
            Regex.Replace(
                normalized,
                @"\s+",
                " ")
            .Trim()
            .Trim('(', '[', '-')
            .Trim();

        result.NormalizedTitle =
            normalized;

        return result;
    }
}













