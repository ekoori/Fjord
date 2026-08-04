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
        var tab = new Tab(_createPane());
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
        var tab = new Tab(_createPane());
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
        var tab = new Tab(_createPane());
        var index = Math.Clamp(closed.Index, 0, _tabs.Count);

        _tabs.Insert(index, tab);
        _tabs[_current].Controller.Deactivate();
        _current = index;
        tab.Controller.Activate();
        _ = tab.Controller.NavigateAsync(closed.Directory, closed.CursorName);

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
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

        public IPane Active => Controller;

        public IReadOnlyList<IPane> Panes => [Controller];

        /// <summary>The directory's own name, which is what fits on a tab.</summary>
        public string Title
        {
            get
            {
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
