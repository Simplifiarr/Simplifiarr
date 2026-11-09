using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IBookMetadataExtractor
{
    BookMetadata Extract(
        string filePath);
}
