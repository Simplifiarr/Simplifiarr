using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Utilities;

/// <summary>
/// What the log writer needs to know from Settings, kept where the logger
/// can reach it without asking for the settings file on every line:
/// whether detailed logging is on, and the secrets that must never be
/// written down.
/// </summary>
public static class LogSwitch
{
    private static LogRedactor _redactor = new([]);

    /// <summary>Detailed logging is on until then; null when it's off.</summary>
    public static DateTime? DetailedUntilUtc { get; private set; }

    public static bool IsDetailed =>
        DetailedUntilUtc is { } until && until > DateTime.UtcNow;

    /// <summary>Takes API keys, passwords and the like out of a log line.</summary>
    public static string Clean(string line) =>
        _redactor.Clean(line);

    /// <summary>Called whenever settings are read or saved.</summary>
    public static void Apply(SimplifiarrSettings settings)
    {
        DetailedUntilUtc = settings.DetailedLoggingUntilUtc;
        _redactor = new LogRedactor(Secrets(settings), Environment.UserName);
    }

    /// <summary>Every password, API key and token in Settings.</summary>
    public static IEnumerable<string?> Secrets(SimplifiarrSettings settings) =>
    [
        settings.TorrentClientPassword,
        settings.RadarrApiKey,
        settings.SonarrApiKey,
        settings.ProwlarrApiKey,
        settings.SeerrApiKey,
        settings.JellyfinApiKey,
        settings.FanartApiKey
    ];
}
