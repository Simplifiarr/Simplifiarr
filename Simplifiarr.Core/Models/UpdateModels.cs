namespace Simplifiarr.Core.Models;

/// <summary>A published Simplifiarr release on GitHub.</summary>
public record ReleaseInfo(
    string Version,
    string Name,
    string Notes,
    string PageUrl,
    DateTime? PublishedUtc,
    string? SetupUrl,
    string? SetupName,
    long SetupBytes,
    string? ChecksumsUrl);

/// <summary>What Updates remembers between restarts (updates.json).</summary>
public class UpdateState
{
    /// <summary>Look on GitHub for a new version twice a day.</summary>
    public bool CheckAutomatically { get; set; } = true;

    /// <summary>Install a new version by itself at <see cref="InstallTime"/>.</summary>
    public bool InstallAutomatically { get; set; }

    /// <summary>Local time, "HH:mm".</summary>
    public string InstallTime { get; set; } = "04:00";

    public DateTime? LastCheckedUtc { get; set; }

    public ReleaseInfo? Latest { get; set; }

    public string? LastError { get; set; }

    /// <summary>The version an update was last started for, and when.</summary>
    public string? InstallingVersion { get; set; }

    public DateTime? InstallStartedUtc { get; set; }

    /// <summary>The version that was running when that update started.</summary>
    public string? InstallFromVersion { get; set; }
}

/// <summary>How Simplifiarr is running, and so whether it can update itself.</summary>
public record InstallInfo(
    bool CanUpdateItself,
    string? WhyNot,
    string? ServiceName,
    string? InstallFolder);
