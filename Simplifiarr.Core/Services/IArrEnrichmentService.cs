using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IArrEnrichmentService
{
    Task<ArrEnrichmentResult> EnrichFromRadarrAsync();

    Task<ArrEnrichmentResult> EnrichFromSonarrAsync();
}
