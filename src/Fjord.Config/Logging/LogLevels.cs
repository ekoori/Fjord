using Microsoft.Extensions.Logging;

namespace Fjord.Config.Logging;

/// <summary>Reads the configured log level.</summary>
public static class LogLevels
{
    /// <summary>
    /// Maps a configured level name onto <see cref="LogLevel"/>.
    /// </summary>
    /// <remarks>
    /// Both the short and long spellings are accepted, and an unrecognised value falls back
    /// to Information rather than failing: a typo in the log level must not be the reason
    /// the app will not start, and it must not silently switch logging off either.
    /// </remarks>
    public static LogLevel Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "off" or "none" => LogLevel.None,
        "critical" or "crit" => LogLevel.Critical,
        "error" or "err" => LogLevel.Error,
        "warning" or "warn" => LogLevel.Warning,
        "debug" => LogLevel.Debug,
        "trace" => LogLevel.Trace,
        _ => LogLevel.Information,
    };
}
