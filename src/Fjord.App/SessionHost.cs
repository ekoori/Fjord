using Fjord.Commands;
using Fjord.Core.FileSystem;
using Fjord.Core.Storage;

namespace Fjord.App;

/// <summary>
/// Reads and rewrites the window's tabs for the session commands.
/// </summary>
/// <remarks>
/// A session records directories, cursors and selection — never the listings. Restoring
/// re-reads, because the disk has almost certainly changed since it was saved and a
/// restored stale listing would be a lie the user acts on.
/// </remarks>
public sealed class SessionHost : ISessionHost, IScrapbookHost
{
    private readonly Workspace _workspace;

    public SessionHost(Workspace workspace) => _workspace = workspace;

    public SavedSession Capture(string name)
    {
        var tabs = new List<SavedTab>(_workspace.Tabs.Count);

        foreach (var tab in _workspace.Tabs)
        {
            var pane = tab.Active;

            // Selection is stored by path, not by row: rows do not survive the directory
            // changing while the session was closed.
            var selection = pane.Selection.Indices()
                .Select(pane.PathAt)
                .Where(p => p is not null)
                .ToList();

            tabs.Add(new SavedTab(pane.Directory, pane.CursorName, selection!));
        }

        return new SavedSession(name, tabs, _workspace.CurrentIndex);
    }

    public async Task RestoreAsync(SavedSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.Tabs.Count == 0)
        {
            return;
        }

        await _workspace.RestoreAsync(session).ConfigureAwait(true);
    }

    /// <summary>
    /// Shows a scrapbook's paths as a listing.
    /// </summary>
    /// <remarks>
    /// The entries come from many directories, so each one's name <i>is</i> its full path.
    /// That is what makes the rest of the app keep working unchanged:
    /// <see cref="Path.Combine(string, string)"/> returns its second argument when that is
    /// rooted, so <c>PathAt</c> and <c>enter</c> resolve correctly against the synthetic
    /// directory label without either knowing this is a scrapbook.
    /// </remarks>
    public Task OpenScrapbookAsync(string name, IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return _workspace.ActivePane.ShowVirtualAsync($"scrapbook:{name}", paths);
    }

    /// <summary>Builds an entry for a path that may no longer exist.</summary>
    /// <remarks>
    /// A scrapbook outlives what it points at. A missing item is flagged rather than
    /// dropped, so the user can see what went and remove it deliberately.
    /// </remarks>
    internal static FileEntry DescribePath(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var info = new DirectoryInfo(path);
                return new FileEntry(path, -1, info.LastWriteTimeUtc.Ticks, EntryFlags.Directory);
            }

            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                return new FileEntry(path, info.Length, info.LastWriteTimeUtc.Ticks, EntryFlags.None);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable is as good as broken for display purposes.
        }

        return new FileEntry(path, -1, 0, EntryFlags.Broken);
    }
}
