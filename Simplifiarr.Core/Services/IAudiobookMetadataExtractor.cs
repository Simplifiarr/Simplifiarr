using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IAudiobookMetadataExtractor
{
    AudiobookMetadata Extract(
        string filePath);
}
