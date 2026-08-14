using Fjord.Commands;
using Fjord.Core.FileSystem;
using Fjord.Core.Panes;
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

            // RestorableDirectory, not Directory: a pane showing a scrapbook has a label
            // where its path would be, and the tab comes back pointed at the real place it
            // was opened from.
            var chose = pane is PaneController { HasOwnOrder: true };

            tabs.Add(new SavedTab(
                Restorable(pane),
                pane.CursorName,
                selection!,
                tab.IsLocked,
                tab.Name,
                chose ? pane.Sort : null,
                chose ? pane.Filter : null));
        }

        return new SavedSession(name, tabs, _workspace.CurrentIndex);
    }

    /// <summary>
    /// A directory a future launch can actually open.
    /// </summary>
    /// <remarks>
    /// Decided from what the pane observed rather than from the shape of the string. A
    /// scrapbook label saved as a directory once — <c>scrapbook:default</c> — and from then
    /// on it was self-repairing in the worst sense: resolved against the working directory
    /// on the next launch, it failed to list, and the failed pane saved the now-absolute
    /// path straight back. Testing the string could not catch that, because by the second
    /// launch it was a perfectly well-formed path to nothing.
    /// <para>
    /// A pane whose listing failed has nothing worth restoring, so home is saved instead.
    /// That does mean a tab on an unplugged drive comes back at home rather than waiting
    /// for the drive, which is the right way round: one is an inconvenience, the other is a
    /// session that never recovers.
    /// </para>
    /// </remarks>
    private static string Restorable(IPane pane)
    {
        var directory = pane.RestorableDirectory;

        var usable = Path.IsPathRooted(directory)
            && pane.Snapshot?.Error is null
            && !ArchivePath.IsInside(directory);

        return usable
            ? directory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
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
    public Task OpenScrapbookAsync(
        string name,
        IReadOnlyList<string> paths,
        IReadOnlyDictionary<string, string>? notes = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        // The label is the scrapbook's own, so a command can tell which one is showing.
        return _workspace.ActivePane.ShowVirtualAsync(
            Fjord.Commands.ScrapbookCommands.ScrapbookLabel(name), paths, notes);
    }

    /// <summary>
    /// Shows the scrapbooks as a listing whose rows are names rather than paths.
    /// </summary>
    /// <remarks>
    /// Enter on a row is handed back to the command that built the listing, because the
    /// name of a scrapbook is not a path and nothing else could know what opening it means.
    /// </remarks>
    public Task OpenScrapbookIndexAsync(
        IReadOnlyList<string> names,
        IReadOnlyDictionary<string, string> sizes,
        Func<string, Task> open)
    {
        ArgumentNullException.ThrowIfNull(names);

        return _workspace.ActivePane.ShowVirtualAsync("scrapbooks", names, sizes, open);
    }

    /// <summary>Builds an entry for a path that may no longer exist.</summary>
    /// <param name="path">Where to look on disk.</param>
    /// <param name="name">
    /// What the listing shows. Defaults to the path itself, which is what a scrapbook
    /// wants; a search result passes the path relative to where the search started, so
    /// the column reads as a location rather than as forty characters of shared prefix.
    /// </param>
    /// <remarks>
    /// A scrapbook outlives what it points at. A missing item is flagged rather than
    /// dropped, so the user can see what went and remove it deliberately.
    /// </remarks>
    internal static FileEntry DescribePath(string path, string? name = null)
    {
        var shown = name ?? path;

        try
        {
            if (Directory.Exists(path))
            {
                var info = new DirectoryInfo(path);
                return new FileEntry(shown, -1, info.LastWriteTimeUtc.Ticks, EntryFlags.Directory);
            }

            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                return new FileEntry(shown, info.Length, info.LastWriteTimeUtc.Ticks, EntryFlags.None);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable is as good as broken for display purposes.
        }

        return new FileEntry(shown, -1, 0, EntryFlags.Broken);
    }
}
