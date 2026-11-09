using System.Text.RegularExpressions;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class BookMetadataExtractor
    : IBookMetadataExtractor
{
    public BookMetadata Extract(
        string filePath)
    {
        var extension =
            Path.GetExtension(filePath)
                .ToLowerInvariant();

        var embedded =
            extension switch
            {
                ".epub" => EpubMetadataReader.TryExtract(filePath),
                ".pdf" => PdfMetadataReader.TryExtract(filePath),
                ".mobi" or ".azw3" => MobiMetadataReader.TryExtract(filePath),
                _ => null
            };

        return embedded ?? ExtractFromFilename(filePath);
    }

    private static BookMetadata ExtractFromFilename(
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
                return new BookMetadata
                {
                    Title = title,
                    Author =
                        string.IsNullOrWhiteSpace(author)
                            ? null
                            : author,
                    Source = BookMetadataSource.Filename
                };
            }
        }

        return new BookMetadata
        {
            Title = cleaned,
            Author = null,
            Source = BookMetadataSource.Filename
        };
    }
}
