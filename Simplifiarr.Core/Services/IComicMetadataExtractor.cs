using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IComicMetadataExtractor
{
    ComicMetadata Extract(
        string filePath);
}
