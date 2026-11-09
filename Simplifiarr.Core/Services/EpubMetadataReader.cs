using System.IO.Compression;
using System.Xml.Linq;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Reads title/author from an EPUB's OPF package document. An EPUB is a
/// zip archive containing META-INF/container.xml, which points at the
/// package (.opf) file holding Dublin Core metadata (dc:title, dc:creator).
/// </summary>
public static class EpubMetadataReader
{
    private static readonly XNamespace ContainerNamespace =
        "urn:oasis:names:tc:opendocument:xmlns:container";

    private static readonly XNamespace DublinCoreNamespace =
        "http://purl.org/dc/elements/1.1/";

    public static BookMetadata? TryExtract(
        string filePath)
    {
        try
        {
            using var archive =
                ZipFile.OpenRead(filePath);

            var containerEntry =
                archive.GetEntry("META-INF/container.xml");

            if (containerEntry is null)
            {
                return null;
            }

            using var containerStream =
                containerEntry.Open();

            var containerXml =
                XDocument.Load(containerStream);

            var opfPath =
                containerXml
                    .Descendants(ContainerNamespace + "rootfile")
                    .Select(element =>
                        (string?)element.Attribute("full-path"))
                    .FirstOrDefault(path =>
                        !string.IsNullOrWhiteSpace(path));

            if (string.IsNullOrWhiteSpace(opfPath))
            {
                return null;
            }

            var opfEntry =
                archive.GetEntry(opfPath);

            if (opfEntry is null)
            {
                return null;
            }

            using var opfStream =
                opfEntry.Open();

            var opfXml =
                XDocument.Load(opfStream);

            var title =
                opfXml
                    .Descendants(DublinCoreNamespace + "title")
                    .Select(element => element.Value)
                    .FirstOrDefault(value =>
                        !string.IsNullOrWhiteSpace(value));

            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var author =
                opfXml
                    .Descendants(DublinCoreNamespace + "creator")
                    .Select(element => element.Value)
                    .FirstOrDefault(value =>
                        !string.IsNullOrWhiteSpace(value));

            return new BookMetadata
            {
                Title = title.Trim(),
                Author = author?.Trim(),
                Source = BookMetadataSource.Embedded
            };
        }
        catch
        {
            return null;
        }
    }
}
