using Fjord.Core.FileSystem;
using Fjord.Core.Panes;

namespace Fjord.App;

/// <summary>
/// The window's tabs.
/// </summary>
/// <remarks>
/// Each tab owns its own pane, and each pane its own snapshot and cancellation source.
/// Switching tabs is therefore a pointer swap plus one repaint: it does not touch, cancel
/// or await any background work, and a listing started in a tab you left keeps running
/// into that tab's own snapshot, so it is further along when you come back.
/// <para>
/// There is one <c>FileListView</c> for the whole window, re-pointed on each switch.
/// Twenty tabs each holding their own row-text cache would be pure waste.
/// </para>
/// </remarks>
public sealed class Workspace : IWorkspace
{
    /// <summary>How many closed tabs can be reopened. Deep enough to undo a mistake, not a session.</summary>
    private const int UndoDepth = 20;

    private readonly List<Tab> _tabs = [];
    private readonly Stack<ClosedTab> _closed = new();
    private readonly Func<PaneController> _createPane;
    private readonly Action _quit;

    private int _current;

    public Workspace(Func<PaneController> createPane, Action quit)
    {
        _createPane = createPane;
        _quit = quit;
    }

    /// <summary>Raised when tabs were added, removed, reordered or switched.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<ITab> Tabs => _tabs;

    public ITab Current => _tabs[_current];

    /// <summary>The pane the user is looking at.</summary>
    public PaneController ActivePane => _tabs[_current].Controller;

    public int CurrentIndex
    {
        get => _current;
        set
        {
            var target = Math.Clamp(value, 0, _tabs.Count - 1);
            if (target == _current)
            {
                return;
            }

            _tabs[_current].Controller.Deactivate();
            _current = target;
            _tabs[_current].Controller.Activate();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Creates the first tab. Separate from <see cref="OpenTab"/>, which needs one to exist.</summary>
    public PaneController Initialise()
    {
        var tab = NewTab();
        _tabs.Add(tab);
        _current = 0;
        tab.Controller.Activate();
        Changed?.Invoke(this, EventArgs.Empty);
        return tab.Controller;
    }

    public ITab OpenTab(
        string directory,
        NewTabPosition position = NewTabPosition.AfterCurrent,
        bool background = false)
    {
        var tab = NewTab();
        var index = position == NewTabPosition.End ? _tabs.Count : _current + 1;
        _tabs.Insert(index, tab);

        if (background)
        {
            // Loading starts immediately even though the tab is not on screen, so it is
            // ready — or well underway — by the time the user switches to it.
            _ = tab.Controller.NavigateAsync(directory);

            if (index <= _current)
            {
                _current++;
            }
        }
        else
        {
            _tabs[_current].Controller.Deactivate();
            _current = index;
            tab.Controller.Activate();
            _ = tab.Controller.NavigateAsync(directory);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return tab;
    }

    /// <summary>
    /// Builds a tab and wires its pane's escape hatch.
    /// </summary>
    /// <remarks>
    /// The redirect closes over the tab rather than over an index, because tabs move: a
    /// closure holding position 2 would start diverting the wrong tab as soon as one to its
    /// left was closed.
    /// </remarks>
    private Tab NewTab()
    {
        var tab = new Tab(_createPane());

        tab.Controller.Redirect = show =>
        {
            if (!tab.IsLocked)
            {
                return false;
            }

            // The new tab is not itself locked, so browsing on from it behaves normally.
            // Opened beside the tab it came from rather than at the end, which is where
            // the eye already is.
            var opened = NewTab();
            var index = Math.Min(_current + 1, _tabs.Count);

            _tabs.Insert(index, opened);
            _tabs[_current].Controller.Deactivate();
            _current = index;
            opened.Controller.Activate();

            // Fire and forget, like every other navigation the workspace starts: the pane
            // reports its own failures and the caller is a key press that has finished.
            _ = show(opened.Controller);

            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        };

        return tab;
    }

    public void CloseTab(int index)
    {
        if (index < 0 || index >= _tabs.Count || _tabs.Count == 1)
        {
            return;
        }

        var tab = _tabs[index];
        tab.Controller.Deactivate();

        // Only the directory and cursor are remembered, not the pane: reopening re-reads,
        // which is both simpler and more correct than restoring a stale listing.
        _closed.Push(new ClosedTab(tab.Controller.Directory, tab.Controller.CurrentCursorName(), index));
        while (_closed.Count > UndoDepth)
        {
            _ = _closed.Pop();
        }

        _tabs.RemoveAt(index);
        _current = Math.Clamp(index > _current ? _current : _current - 1, 0, _tabs.Count - 1);
        _tabs[_current].Controller.Activate();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool UndoClose()
    {
        if (_closed.Count == 0)
        {
            return false;
        }

        var closed = _closed.Pop();
        var tab = NewTab();
        var index = Math.Clamp(closed.Index, 0, _tabs.Count);

        _tabs.Insert(index, tab);
        _tabs[_current].Controller.Deactivate();
        _current = index;
        tab.Controller.Activate();
        _ = tab.Controller.NavigateAsync(closed.Directory, closed.CursorName);

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Replaces every tab with a saved session's.
    /// </summary>
    /// <remarks>
    /// Navigation for all tabs is started before any is awaited, so restoring a session of
    /// ten directories reads them concurrently rather than one after another. The active
    /// tab is awaited last but shown first, so the window is usable while the rest load.
    /// </remarks>
    public async Task RestoreAsync(Core.Storage.SavedSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.Tabs.Count == 0)
        {
            return;
        }

        foreach (var tab in _tabs)
        {
            tab.Controller.Deactivate();
        }

        _tabs.Clear();
        _closed.Clear();

        var loading = new List<Task>(session.Tabs.Count);

        foreach (var saved in session.Tabs)
        {
            var tab = NewTab();
            tab.Name = saved.Name;

            // Before the listing starts, so the entries arrive in the order the tab was
            // left in rather than being sorted once and then re-sorted.
            if (saved.Sort is { } sort)
            {
                tab.Controller.RestoreOrder(sort, saved.Filter ?? FilterSpec.Default);
            }

            _tabs.Add(tab);

            // The lock goes on AFTER the restore navigation, not before. A locked tab
            // diverts navigation into a new tab, and its own restore is navigation — set
            // first, the tab pushed its contents into a fresh tab and came back empty.
            var restoring = tab.Controller.NavigateAsync(saved.Directory, saved.CursorName);
            var locked = saved.IsLocked;

            loading.Add(restoring.ContinueWith(
                _ => tab.IsLocked = locked,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.FromCurrentSynchronizationContext()));
        }

        _current = Math.Clamp(session.ActiveTab, 0, _tabs.Count - 1);
        _tabs[_current].Controller.Activate();
        Changed?.Invoke(this, EventArgs.Empty);

        await Task.WhenAll(loading).ConfigureAwait(true);

        // Selection is restored by path once the listings exist, since rows mean nothing
        // until then.
        for (var i = 0; i < _tabs.Count && i < session.Tabs.Count; i++)
        {
            RestoreSelection(_tabs[i].Controller, session.Tabs[i].Selection);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void RestoreSelection(PaneController pane, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        var wanted = paths.ToHashSet(StringComparer.Ordinal);
        var view = pane.Snapshot?.View ?? default;

        for (var row = 0; row < view.Count; row++)
        {
            if (pane.PathAt(row) is { } path && wanted.Contains(path))
            {
                pane.Selection[row] = true;
            }
        }
    }

    public void MoveTab(int from, int to)
    {
        if (from < 0 || from >= _tabs.Count || from == to)
        {
            return;
        }

        to = Math.Clamp(to, 0, _tabs.Count - 1);
        var tab = _tabs[from];
        _tabs.RemoveAt(from);
        _tabs.Insert(to, tab);

        // The moved tab is still the one being looked at, wherever it landed.
        _current = to;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Quit() => _quit();

    private sealed record ClosedTab(string Directory, string? CursorName, int Index);

    private sealed class Tab : ITab
    {
        public Tab(PaneController controller) => Controller = controller;

        public PaneController Controller { get; }

        public bool IsLocked { get; set; }

        public string? Name { get; set; }

        public IPane Active => Controller;

        public IReadOnlyList<IPane> Panes => [Controller];

        /// <summary>What the tab bar shows: the user's name for it, or the directory's.</summary>
        public string Title
        {
            get
            {
                if (Name is { Length: > 0 } named)
                {
                    return named;
                }

                var directory = Controller.Directory;
                if (directory.Length == 0)
                {
                    return "(empty)";
                }

                var name = Path.GetFileName(directory.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                // A drive or filesystem root has no file name component.
                return name.Length > 0 ? name : directory;
            }
        }
    }
}
