using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IBookLibraryTargetResolver
{
    LibraryTarget Resolve(
        BookMetadata metadata,
        string format,
        string bookLibraryPath,
        string? originalFileName = null,
        bool preserveOriginalFileName = false);
}
