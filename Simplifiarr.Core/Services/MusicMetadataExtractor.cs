using System.Text.RegularExpressions;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class MusicMetadataExtractor
    : IMusicMetadataExtractor
{
    public MusicMetadata Extract(
        string filePath)
    {
        var extension =
            Path.GetExtension(filePath)
                .ToLowerInvariant();

        var embedded =
            extension switch
            {
                ".mp3" => Id3v2MetadataReader.TryExtractMusic(filePath),
                ".m4a" or ".m4b" => Mp4MetadataReader.TryExtractMusic(filePath),
                ".flac" => FlacMetadataReader.TryExtractMusic(filePath),
                _ => null
            };

        return embedded ?? ExtractFromFilename(filePath);
    }

    private static MusicMetadata ExtractFromFilename(
        string filePath)
    {
        var name =
            Path.GetFileNameWithoutExtension(filePath);

        var cleaned =
            Regex.Replace(name, @"[._]+", " ")
                .Trim();

        cleaned =
            Regex.Replace(cleaned, @"\s+", " ");

        var separatorIndex =
            cleaned.IndexOf(" - ", StringComparison.Ordinal);

        if (separatorIndex > 0)
        {
            var artist =
                cleaned[..separatorIndex].Trim();

            var title =
                cleaned[(separatorIndex + 3)..].Trim();

            if (!string.IsNullOrWhiteSpace(title))
            {
                return new MusicMetadata
                {
                    Title = title,
                    Artist =
                        string.IsNullOrWhiteSpace(artist)
                            ? null
                            : artist,
                    Source = MusicMetadataSource.Filename
                };
            }
        }

        return new MusicMetadata
        {
            Title = cleaned,
            Artist = null,
            Source = MusicMetadataSource.Filename
        };
    }
}
