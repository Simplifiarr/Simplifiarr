using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IMediaMetadataExtractor
{
    Task<MediaMetadata?> ExtractAsync(
        string filePath);
}
