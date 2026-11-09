using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface ILibraryTargetResolver
{
    Task<LibraryTarget> ResolveAsync(
        ParsedRelease release,
        string sourcePath);
}