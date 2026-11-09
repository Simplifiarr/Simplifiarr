using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IAudiobookLibraryTargetResolver
{
    LibraryTarget Resolve(
        AudiobookMetadata metadata,
        string format,
        string audiobookLibraryPath,
        string? originalFileName = null,
        bool preserveOriginalFileName = false);
}
