using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

/// <summary>
/// What the Setup page would point out about your apps, from the last scan,
/// so Home can say so without asking every app on each visit.
/// </summary>
public interface IDownloadsDriveCheck
{
    /// <summary>What the last check found, in plain words. Empty when all is well.</summary>
    IReadOnlyList<string> Problems { get; }

    /// <summary>Asks the apps again.</summary>
    Task CheckAsync();
}

public sealed class DownloadsDriveCheck : IDownloadsDriveCheck
{
    private readonly IStackSetupService _setup;
    private readonly string _path;

    private List<string> _problems;

    public DownloadsDriveCheck(IStackSetupService setup)
    {
        _setup = setup;
        _path = AppPaths.DataFile("downloads-drive-check.json");
        _problems = JsonFileStore.Load<List<string>>(_path) ?? [];
    }

    public IReadOnlyList<string> Problems => _problems;

    public async Task CheckAsync()
    {
        _problems =
            (await _setup.CheckAsync())
                .Where(check => check.Status is SetupStatus.CanFix or SetupStatus.Manual)
                .Select(check => $"{check.App}: {check.Title.ToLowerInvariant()}? Not yet. {check.Detail}")
                .ToList();

        JsonFileStore.Save(_path, _problems);
    }
}
