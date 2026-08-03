namespace Fjord.Config;

/// <summary>
/// Watches the config file and reports changes, debounced, on the caller's chosen thread.
/// </summary>
/// <remarks>
/// Adapted from noPDF's <c>ConfigWatcher</c> with one change: it hard-codes
/// <c>Dispatcher.UIThread.Post</c>, which drags Avalonia into the config layer. Taking the
/// post callback as a parameter keeps this project UI-free and testable.
/// </remarks>
public sealed class ConfigWatcher : IDisposable
{
    /// <summary>
    /// Editors save in bursts — write, truncate, rename — and each raises an event.
    /// </summary>
    private const int DebounceMilliseconds = 200;

    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounce;

    /// <param name="post">
    /// Marshals the callback to the right thread. The App layer passes
    /// <c>Dispatcher.UIThread.Post</c>; tests pass a direct invoke.
    /// </param>
    public ConfigWatcher(string path, Action onChanged, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        ArgumentNullException.ThrowIfNull(post);

        _debounce = new Timer(_ => post(onChanged), null, Timeout.Infinite, Timeout.Infinite);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException($"Config path has no directory: {path}", nameof(path));
        }

        Directory.CreateDirectory(directory);

        _watcher = new FileSystemWatcher(directory, Path.GetFileName(path))
        {
            // FileName matters as much as LastWrite: an editor configured to write-rename
            // (vim with backupcopy=no is the common one) deletes and recreates the file,
            // and a watcher bound only to Changed stops firing after the first save.
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };

        _watcher.Changed += OnEvent;
        _watcher.Created += OnEvent;
        _watcher.Renamed += OnEvent;
    }

    private void OnEvent(object sender, FileSystemEventArgs e) =>
        _debounce.Change(DebounceMilliseconds, Timeout.Infinite);

    public void Dispose()
    {
        _watcher.Dispose();
        _debounce.Dispose();
    }
}
