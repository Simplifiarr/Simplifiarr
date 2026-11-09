namespace Simplifiarr.Core.Services;

public interface IQuarantineService
{
    Task<string?> QuarantineFileAsync(string path);
}

