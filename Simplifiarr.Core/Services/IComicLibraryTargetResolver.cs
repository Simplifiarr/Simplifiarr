using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IComicLibraryTargetResolver
{
    LibraryTarget Resolve(
        ComicMetadata metadata,
        string format,
        string comicLibraryPath,
        string? originalFileName = null,
        bool preserveOriginalFileName = false);
}
