using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IMusicLibraryTargetResolver
{
    LibraryTarget Resolve(
        MusicMetadata metadata,
        string format,
        string musicLibraryPath,
        string? originalFileName = null,
        bool preserveOriginalFileName = false);
}
