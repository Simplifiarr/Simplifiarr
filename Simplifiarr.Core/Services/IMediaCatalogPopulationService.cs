using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IMediaCatalogPopulationService
{
    Task<int> PopulateAsync(
        IEnumerable<FileRecord> files,
        MediaType? mediaTypeOverride = null);
}
