using Fjord.Core.FileSystem;
using Fjord.Core.Navigation;
using Fjord.Core.Panes;
using Fjord.Core.Selection;

namespace Fjord.Tests.Commands;

/// <summary>
/// An <see cref="IPane"/> backed by a real <see cref="DirectorySnapshot"/> and nothing else.
/// </summary>
/// <remarks>
/// The reason <see cref="IPane"/> lives in Core rather than the App layer: command bodies,
/// the parser's runtime-supplied arguments and the operator dispatcher can all be tested
/// without Avalonia, a window, or a filesystem.
/// </remarks>
public sealed class FakePane : IPane
{
    private DirectorySnapshot _snapshot;
    private int _cursor;

    public FakePane(string directory = "/tmp", params string[] names)
    {
        Directory = directory;
        _snapshot = new DirectorySnapshot(directory);

        if (names.Length > 0)
        {
            _snapshot.Append(names.Select(n => new FileEntry(n, 10, 100, EntryFlags.None)).ToArray());
        }
    }

    public string Directory { get; private set; }

    public DirectorySnapshot? Snapshot => _snapshot;

    public int CursorIndex
    {
        get => _cursor;
        set => _cursor = Math.Clamp(value, 0, Math.Max(0, _snapshot.View.Count - 1));
    }

    public int? VisualAnchor { get; set; }

    public SelectionSet Selection { get; } = new();

    public SortSpec Sort { get; private set; } = SortSpec.Default;

    public FilterSpec Filter { get; private set; } = FilterSpec.Default;

    public NavigationHistory History { get; } = new();

    public int PageSize { get; set; } = 10;

    public EntryRange? VisualRange => VisualAnchor is { } anchor
        ? EntryRange.Between(anchor, _cursor)
        : null;

    public string? CursorName
    {
        get
        {
            var view = _snapshot.View;
            return view.Count > 0 && _cursor < view.Count ? view[_cursor].Name : null;
        }
    }

    /// <summary>Records what commands did, so tests can assert on effects rather than state.</summary>
    public List<string> Log { get; } = [];

    public void MoveCursor(int delta) => CursorIndex += delta;

    public void MoveCursorTo(int row) => CursorIndex = row;

    public bool MoveCursorToName(string name)
    {
        var row = _snapshot.View.IndexOfName(name, StringComparison.Ordinal);
        if (row < 0)
        {
            return false;
        }

        CursorIndex = row;
        return true;
    }

    public string? PathAt(int row)
    {
        var view = _snapshot.View;

        // Rooted the same way the real pane is: a listing whose names are relative
        // resolves them against where they were gathered from, not against its label.
        return row >= 0 && row < view.Count
            ? Path.Combine(_root ?? Directory, view[row].Name)
            : null;
    }

    public Task NavigateAsync(string directory, string? selectName = null)
    {
        Log.Add($"navigate:{directory}");
        History.Push(directory, selectName);
        Directory = directory;
        return Task.CompletedTask;
    }

    public Task ReloadAsync()
    {
        Log.Add("reload");
        return Task.CompletedTask;
    }

    /// <summary>How many directories a test says are here to be measured.</summary>
    public int DirectoriesToMeasure { get; set; }

    public int MeasureDirectorySizes()
    {
        Log.Add("measure-sizes");
        return DirectoriesToMeasure;
    }

    /// <summary>Set by a test to stand in for a scrapbook listing.</summary>
    public bool IsVirtual { get; set; }

    // What a rooted listing's relative names resolve against.
    private string? _root;

    /// <summary>The last made-up listing this pane was asked to show.</summary>
    public (string Label,
            IReadOnlyList<string> Paths,
            IReadOnlyDictionary<string, string>? Notes,
            string? Root)? Shown
    {
        get;
        private set;
    }

    public Task ShowListingAsync(
        string label,
        IReadOnlyList<string> paths,
        IReadOnlyDictionary<string, string>? notes = null,
        string? root = null)
    {
        Shown = (label, paths, notes, root);
        _root = root;

        // A real listing, not just a record of the call: the commands under test go on to
        // move the cursor over it and ask for the path of a row.
        _snapshot = new DirectorySnapshot(label);
        _snapshot.Append(paths.Select(Describe).ToArray());
        _snapshot.Complete();
        _cursor = 0;
        IsVirtual = true;
        Directory = label;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds an entry the way the app does, so IsDirectory is right for the row.
    /// </summary>
    /// <remarks>
    /// Enter and goto-folder both read that flag rather than stat the path again, so a
    /// fake that left it false would make every result look like a file.
    /// </remarks>
    private FileEntry Describe(string path)
    {
        var full = _root is null ? path : Path.Combine(_root, path);

        return System.IO.Directory.Exists(full)
            ? new FileEntry(path, -1, 0, EntryFlags.Directory)
            : new FileEntry(path, 0, 0, EntryFlags.None);
    }

    /// <summary>Relabels the pane without navigating, as showing a scrapbook does.</summary>
    public void SetDirectory(string directory) => Directory = directory;

    /// <summary>Where a virtual listing was opened from, when a test sets one.</summary>
    public string? Origin { get; set; }

    public string RestorableDirectory => Origin ?? Directory;

    /// <summary>What Enter on a row of a virtual index does, when a test sets one.</summary>
    public Func<string, Task>? VirtualOpen { get; set; }

    public async Task<bool> OpenVirtualAsync(int row)
    {
        var view = _snapshot.View;

        if (VirtualOpen is not { } open || row < 0 || row >= view.Count)
        {
            return false;
        }

        await open(view[row].Name);
        return true;
    }

    public Task<bool> LeaveVirtualAsync()
    {
        if (!IsVirtual)
        {
            return Task.FromResult(false);
        }

        Log.Add("leave-virtual");
        IsVirtual = false;
        return Task.FromResult(true);
    }

    public Task BackAsync(int count = 1)
    {
        Log.Add($"back:{count}");
        return Task.CompletedTask;
    }

    public Task ForwardAsync(int count = 1)
    {
        Log.Add($"forward:{count}");
        return Task.CompletedTask;
    }

    /// <summary>Adds a directory entry, for tests that need one to navigate into.</summary>
    public void AddDirectory(string name) =>
        _snapshot.Append([new FileEntry(name, -1, 100, EntryFlags.Directory)]);

    public void SetOrder(SortSpec sort, FilterSpec filter)
    {
        Sort = sort;
        Filter = filter;
        _snapshot.SetOrder(sort, filter);
        Log.Add($"order:{sort.Key}:{(sort.Descending ? "desc" : "asc")}");
    }
}

/// <summary>Records mode changes so command bodies can be tested without a window.</summary>
public sealed class FakeModeHost : IModeHost
{
    public List<string> Actions { get; } = [];

    public void ToggleVisual(IPane pane)
    {
        pane.VisualAnchor = pane.VisualAnchor is null ? pane.CursorIndex : null;
        Actions.Add("toggle-visual");
    }

    public void LeaveMode(IPane pane)
    {
        pane.VisualAnchor = null;
        Actions.Add("leave-mode");
    }

    public void EnterCommandLine(string initial) => Actions.Add($"command-line:{initial}");

    public void ReadName(NameTarget target) => Actions.Add($"read-name:{target}");

    /// <summary>Whether a question is answered yes. Tests that care set it explicitly.</summary>
    public bool AnswerYes { get; set; } = true;

    public void Ask(string question, Action confirmed)
    {
        Actions.Add($"ask:{question}");

        if (AnswerYes)
        {
            confirmed();
        }
    }

    public Fjord.Core.Navigation.MarkFile Marks { get; } = new();

    /// <summary>Whether drag mode is on, flipped by the command under test.</summary>
    public bool DragMode { get; private set; }

    public bool ToggleDragMode()
    {
        DragMode = !DragMode;
        Actions.Add($"drag-mode:{(DragMode ? "on" : "off")}");
        return DragMode;
    }

    public bool ToggleFrameStats()
    {
        Actions.Add("toggle-frame-stats");
        return true;
    }

    public void Quit() => Actions.Add("quit");
}
