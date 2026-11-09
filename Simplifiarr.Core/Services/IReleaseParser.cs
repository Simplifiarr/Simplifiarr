using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IReleaseParser
{
    ParsedRelease Parse(
        string releaseName);
}
