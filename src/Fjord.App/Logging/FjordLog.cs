using System.Diagnostics;
using Fjord.Config;
using Fjord.Config.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fjord.App.Logging;

/// <summary>
/// Builds the application's logger factory from configuration.
/// </summary>
/// <remarks>
/// Logging is wired at the composition root. The lower assemblies raise events for the
/// things worth recording — the operation queue already reports every state change, the
/// pane controller already sees every batch — so those can be logged from here without a
/// logger being threaded through pure logic that is easier to test without one.
/// </remarks>
public static class FjordLog
{
    /// <summary>The current log file. Older generations sit beside it as <c>fjord.1.log</c> and so on.</summary>
    public static string FileFor(FjordPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Path.Combine(paths.State, "fjord.log");
    }

    /// <summary>Creates the factory, or a no-op one when logging is switched off.</summary>
    public static ILoggerFactory Create(FjordPaths paths, LoggingConfig settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var level = LogLevels.Parse(settings.Level);
        if (level == LogLevel.None)
        {
            return NullLoggerFactory.Instance;
        }

        var factory = new LoggerFactory();
        factory.AddProvider(new RollingFileLoggerProvider(FileFor(paths), level, settings.MaxFiles));
#if DEBUG
        factory.AddProvider(new DebugLoggerProvider(level));
#endif
        return factory;
    }

    /// <summary>
    /// Mirrors records to the debugger in Debug builds.
    /// </summary>
    /// <remarks>
    /// <see cref="Debug"/> rather than the console: a WinExe has no console attached, so
    /// <c>Console.WriteLine</c> would go nowhere while the IDE output window shows this.
    /// </remarks>
    private sealed class DebugLoggerProvider(LogLevel minimum) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new DebugLogger(minimum, categoryName);

        public void Dispose()
        {
            // Nothing to release.
        }

        private sealed class DebugLogger(LogLevel minimum, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) =>
                logLevel >= minimum && logLevel != LogLevel.None;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);

                if (IsEnabled(logLevel))
                {
                    Debug.WriteLine($"{logLevel} {category}: {formatter(state, exception)}");
                }
            }
        }
    }
}
