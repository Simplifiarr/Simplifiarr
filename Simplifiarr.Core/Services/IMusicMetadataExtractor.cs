using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IMusicMetadataExtractor
{
    MusicMetadata Extract(
        string filePath);
}
