namespace Simplifiarr.Core.Utilities;

public enum AppLogLevel
{
    Debug,
    Information,
    Warning,
    Error
}

/// <summary>
/// Minimal logging hook for the Core project, which deliberately has no
/// package dependencies. The web host points <see cref="Sink"/> at its real
/// logger at startup; until then messages are dropped.
/// </summary>
public static class AppLog
{
    public static Action<AppLogLevel, string, Exception?>? Sink { get; set; }

    /// <summary>
    /// Only written while detailed logging is on (Logs &amp; support page):
    /// the small steps that explain why something happened.
    /// </summary>
    public static void Debug(string message)
    {
        if (LogSwitch.IsDetailed)
        {
            Write(AppLogLevel.Debug, message, null);
        }
    }

    public static void Info(string message) =>
        Write(AppLogLevel.Information, message, null);

    public static void Warn(string message, Exception? exception = null) =>
        Write(AppLogLevel.Warning, message, exception);

    public static void Error(string message, Exception? exception = null) =>
        Write(AppLogLevel.Error, message, exception);

    private static void Write(
        AppLogLevel level,
        string message,
        Exception? exception)
    {
        try
        {
            Sink?.Invoke(level, message, exception);
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
