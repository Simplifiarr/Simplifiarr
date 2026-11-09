using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Web.Support;

/// <summary>
/// Writes log lines to one text file per day under the data folder's Logs
/// directory. Simplifiarr normally runs as a Windows service with no
/// console, so without this a failure leaves no trace anywhere a person
/// would look. Keeps the most recent two weeks.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int DaysToKeep = 14;

    private readonly object _sync = new();
    private DateOnly _lastCleanup;

    public ILogger CreateLogger(string categoryName) =>
        new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string line)
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);

            lock (_sync)
            {
                Directory.CreateDirectory(AppPaths.LogsDirectory);

                File.AppendAllText(
                    Path.Combine(
                        AppPaths.LogsDirectory,
                        $"simplifiarr-{today:yyyy-MM-dd}.log"),
                    line + Environment.NewLine);

                if (_lastCleanup != today)
                {
                    _lastCleanup = today;
                    DeleteOldLogs();
                }
            }
        }
        catch
        {
            // A logger that throws would turn a small problem into a crash.
        }
    }

    private static void DeleteOldLogs()
    {
        var cutoff = DateTime.Now.AddDays(-DaysToKeep);

        foreach (var file in Directory.EnumerateFiles(
                     AppPaths.LogsDirectory,
                     "simplifiarr-*.log"))
        {
            if (File.GetLastWriteTime(file) < cutoff)
            {
                File.Delete(file);
            }
        }
    }

    private sealed class FileLogger(
        FileLoggerProvider provider,
        string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        // Every outgoing web request (qBittorrent, Prowlarr, Radarr...) logs
        // four information lines of its own, which buries everything else
        // in the file. Keep those only when something went wrong.
        private readonly LogLevel _minimum =
            category.StartsWith("System.Net.Http", StringComparison.Ordinal)
                ? LogLevel.Warning
                : LogLevel.Information;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= _minimum;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var line =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{logLevel}] {category}: {formatter(state, exception)}";

            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            provider.Write(line);
        }
    }
}
