using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Fjord.Config.Logging;

/// <summary>
/// Writes log records to a rolling file under <see cref="FjordPaths.State"/>.
/// </summary>
/// <remarks>
/// Hand-written rather than <c>Microsoft.Extensions.Logging.Console</c>, which drags in
/// Configuration, Options and a formatter stack for roughly 300 KB to do less than this —
/// and which writes to a console a WinExe does not have.
/// <para>
/// The file is opened with <see cref="FileShare.ReadWrite"/> so it can be tailed while
/// Fjord is running. On Windows the usual sharing mode would make the log unreadable until
/// the app exits, which is exactly when you least need it.
/// </para>
/// </remarks>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly Writer _writer;
    private readonly LogLevel _minimum;

    /// <param name="path">The current log file. Older generations sit beside it.</param>
    /// <param name="minimum">Records below this level are dropped without formatting.</param>
    /// <param name="maxFiles">Generations kept, including the current file.</param>
    /// <param name="maxBytes">Size at which the current file rolls.</param>
    public RollingFileLoggerProvider(
        string path, LogLevel minimum, int maxFiles = 3, long maxBytes = 1024 * 1024)
    {
        _writer = new Writer(path, Math.Max(1, maxFiles), Math.Max(4096, maxBytes));
        _minimum = minimum;
    }

    public ILogger CreateLogger(string categoryName) =>
        new FileLogger(_writer, _minimum, Shorten(categoryName));

    public void Dispose() => _writer.Dispose();

    /// <summary>
    /// Trims the assembly prefix off a category.
    /// </summary>
    /// <remarks>
    /// Every category in this app starts with <c>Fjord.</c>, so keeping it costs six
    /// columns on every line and distinguishes nothing.
    /// </remarks>
    private static string Shorten(string category) =>
        category.StartsWith("Fjord.", StringComparison.Ordinal) ? category[6..] : category;

    private sealed class FileLogger(Writer writer, LogLevel minimum, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            if (!IsEnabled(logLevel))
            {
                return;
            }

            var line = new StringBuilder(128)
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(Abbreviate(logLevel))
                .Append(' ')
                .Append(category)
                .Append(": ")
                .Append(formatter(state, exception));

            if (exception is not null)
            {
                line.Append(Environment.NewLine).Append(exception);
            }

            writer.Write(line.ToString());
        }

        private static string Abbreviate(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };
    }

    /// <summary>
    /// The shared file handle. One per provider, guarded by a lock.
    /// </summary>
    /// <remarks>
    /// A lock rather than a background writer queue: log volume here is per navigation and
    /// per batch, not per entry, so contention is not a concern and a queue would only add
    /// a way to lose the last records of a crash.
    /// </remarks>
    private sealed class Writer(string path, int maxFiles, long maxBytes) : IDisposable
    {
        private readonly Lock _gate = new();
        private StreamWriter? _file;
        private long _written;

        // Logging must never be the thing that takes the app down, and it must never turn
        // a full disk into a stall: after one failure this provider goes quiet for the rest
        // of the session rather than retrying on every record.
        private bool _disabled;

        public void Write(string line)
        {
            lock (_gate)
            {
                if (_disabled)
                {
                    return;
                }

                try
                {
                    if (_file is not null && _written >= maxBytes)
                    {
                        Roll();
                    }

                    if (_file is null)
                    {
                        FjordPaths.EnsureDirectoryFor(path);
                        _file = Open();
                        _written = _file.BaseStream.Length;
                    }

                    _file.WriteLine(line);
                    _file.Flush();
                    _written += line.Length + Environment.NewLine.Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Debug.WriteLine($"Fjord: logging disabled, {ex.Message}");
                    _disabled = true;
                    Close();
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                Close();
            }
        }

        private StreamWriter Open() => new(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        /// <summary>Shifts each generation up one and drops the oldest.</summary>
        private void Roll()
        {
            Close();

            var directory = Path.GetDirectoryName(path) ?? ".";
            var stem = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);

            string Generation(int n) =>
                Path.Combine(directory, n == 0 ? stem + extension : $"{stem}.{n}{extension}");

            File.Delete(Generation(maxFiles - 1));

            for (var n = maxFiles - 2; n >= 0; n--)
            {
                if (File.Exists(Generation(n)))
                {
                    File.Move(Generation(n), Generation(n + 1), overwrite: true);
                }
            }

            _written = 0;
        }

        private void Close()
        {
            _file?.Dispose();
            _file = null;
        }
    }
}
