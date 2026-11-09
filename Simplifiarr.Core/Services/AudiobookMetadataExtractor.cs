using System.Text.RegularExpressions;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class AudiobookMetadataExtractor
    : IAudiobookMetadataExtractor
{
    public AudiobookMetadata Extract(
        string filePath)
    {
        var extension =
            Path.GetExtension(filePath)
                .ToLowerInvariant();

        var embedded =
            extension switch
            {
                ".mp3" => Id3v2MetadataReader.TryExtract(filePath),
                ".m4a" or ".m4b" => Mp4MetadataReader.TryExtract(filePath),
                _ => null
            };

        return embedded ?? ExtractFromFilename(filePath);
    }

    private static AudiobookMetadata ExtractFromFilename(
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
            var author =
                cleaned[..separatorIndex].Trim();

            var title =
                cleaned[(separatorIndex + 3)..].Trim();

            if (!string.IsNullOrWhiteSpace(title))
            {
                return new AudiobookMetadata
                {
                    Title = title,
                    Author =
                        string.IsNullOrWhiteSpace(author)
                            ? null
                            : author,
                    Source = AudiobookMetadataSource.Filename
                };
            }
        }

        return new AudiobookMetadata
        {
            Title = cleaned,
            Author = null,
            Source = AudiobookMetadataSource.Filename
        };
    }
}
