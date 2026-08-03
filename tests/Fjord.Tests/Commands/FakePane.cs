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
    private readonly DirectorySnapshot _snapshot;
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
        return row >= 0 && row < view.Count ? Path.Combine(Directory, view[row].Name) : null;
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

    public void Quit() => Actions.Add("quit");
}
