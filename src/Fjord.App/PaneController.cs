using Avalonia.Threading;
using Fjord.App.Views;
using Fjord.Core.FileSystem;
using Fjord.Core.Navigation;
using Fjord.Core.Panes;
using Fjord.Core.Selection;

namespace Fjord.App;

/// <summary>
/// Drives navigation for one pane: starts a listing, streams it into a snapshot, and
/// keeps the view pointed at the right one.
/// </summary>
/// <remarks>
/// Each pane owns its own snapshot and its own cancellation source. That is what makes
/// tab switching a pointer swap that never waits on background work, and what makes an
/// abandoned navigation stop costing anything within a single batch.
/// </remarks>
public sealed class PaneController : IPane
{
    private readonly IDirectoryLister _lister;
    private readonly FileListView _list;

    private CancellationTokenSource? _navigation;

    public PaneController(
        IDirectoryLister lister, FileListView list, SortSpec? sort = null, FilterSpec? filter = null)
    {
        _lister = lister;
        _list = list;
        Sort = sort ?? SortSpec.Default;
        Filter = filter ?? FilterSpec.Default;
    }

    public DirectorySnapshot? Snapshot { get; private set; }

    public NavigationHistory History { get; } = new();

    public SortSpec Sort { get; private set; }

    public FilterSpec Filter { get; private set; }

    /// <summary>The directory being shown. Empty before the first navigation.</summary>
    public string Directory => Snapshot?.Directory ?? string.Empty;

    public int CursorIndex
    {
        get => _list.CursorIndex;
        set => _list.CursorIndex = value;
    }

    public int? VisualAnchor
    {
        get => _list.VisualAnchor;
        set
        {
            _list.VisualAnchor = value;
            _list.RequestRedraw();
        }
    }

    public SelectionSet Selection => _list.Selection;

    public int PageSize => _list.PageSize;

    public EntryRange? VisualRange => _list.VisualRange is { } range
        ? EntryRange.Between(range.From, range.To)
        : null;

    public string? CursorName => CurrentCursorName();

    public void MoveCursor(int delta)
    {
        // int.MaxValue is how "go to the end" is spelled; adding to it would wrap.
        CursorIndex = delta > 0 && CursorIndex > int.MaxValue - delta
            ? int.MaxValue
            : CursorIndex + delta;
    }

    public void MoveCursorTo(int row) => CursorIndex = row;

    public bool MoveCursorToName(string name) => _list.MoveCursorToName(name, StringComparison.Ordinal);

    public string? PathAt(int row)
    {
        var view = Snapshot?.View ?? default;
        return row >= 0 && row < view.Count ? Path.Combine(Directory, view[row].Name) : null;
    }

    Task IPane.NavigateAsync(string directory, string? selectName) => NavigateAsync(directory, selectName);

    /// <summary>Raised whenever the pane's state changed enough for a status line to care.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Lists <paramref name="directory"/>, replacing whatever the pane was showing.
    /// </summary>
    /// <param name="selectName">
    /// Name to put the cursor on once it appears. Used by back-navigation and by "go up,
    /// select the folder we came from".
    /// </param>
    public async Task NavigateAsync(string directory, string? selectName = null, bool recordHistory = true)
    {
        var full = Path.GetFullPath(directory);

        if (recordHistory)
        {
            History.UpdateCursor(CurrentCursorName());
            History.Push(full, selectName);
        }

        // Replacing the source cancels the previous listing; the old producer stops
        // within one batch and releases its directory handle.
        var previous = _navigation;
        var cts = new CancellationTokenSource();
        _navigation = cts;

        if (previous is not null)
        {
            await previous.CancelAsync().ConfigureAwait(true);
            previous.Dispose();
        }

        var snapshot = new DirectorySnapshot(full, Sort, Filter);
        Snapshot = snapshot;

        // Published before a single entry has arrived, so the window shows the new
        // directory and an empty list immediately instead of holding the old contents.
        _list.Snapshot = snapshot;
        Changed?.Invoke(this, EventArgs.Empty);

        await LoadAsync(snapshot, selectName, cts.Token).ConfigureAwait(true);
    }

    /// <summary>Re-lists the current directory, keeping the cursor where it was.</summary>
    public Task ReloadAsync() =>
        Directory.Length > 0
            ? NavigateAsync(Directory, CurrentCursorName(), recordHistory: false)
            : Task.CompletedTask;

    public Task BackAsync(int count = 1) => GoAsync(History.Back(count));

    public Task ForwardAsync(int count = 1) => GoAsync(History.Forward(count));

    /// <summary>Changes sort or filter, re-ordering what is already loaded without re-reading.</summary>
    public void SetOrder(SortSpec sort, FilterSpec filter)
    {
        Sort = sort;
        Filter = filter;

        if (Snapshot is not { } snapshot)
        {
            return;
        }

        // Keep the cursor on the same entry across the reorder; its row is about to move.
        var name = CurrentCursorName();
        snapshot.SetOrder(sort, filter);

        // Synchronously, not RequestRedraw: the cursor lookup below has to search the new
        // order, and a deferred refresh would leave it searching the old one.
        _list.SyncView();

        if (name is not null)
        {
            _list.MoveCursorToName(name, StringComparison.Ordinal);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string? CurrentCursorName()
    {
        var view = _list.Snapshot?.View ?? default;
        return view.Count > 0 && _list.CursorIndex < view.Count ? view[_list.CursorIndex].Name : null;
    }

    private Task GoAsync(HistoryEntry? entry) =>
        entry is { } target
            ? NavigateAsync(target.Directory, target.CursorName, recordHistory: false)
            : Task.CompletedTask;

    private async Task LoadAsync(DirectorySnapshot snapshot, string? selectName, CancellationToken ct)
    {
        var options = ListingOptions.For(Sort, Filter, sizeColumnVisible: true, modifiedColumnVisible: true);
        var cursorPlaced = selectName is null;

        try
        {
            await foreach (var batch in _lister.ListAsync(snapshot.Directory, options, ct).ConfigureAwait(true))
            {
                snapshot.Append(batch.Span);

                // One coalesced repaint per batch. Never per entry.
                _list.RequestRedraw();

                if (!cursorPlaced && _list.MoveCursorToName(selectName!, StringComparison.Ordinal))
                {
                    cursorPlaced = true;
                }
            }

            snapshot.Complete();
        }
        catch (OperationCanceledException)
        {
            // Navigated away. The snapshot is discarded with the pane's old state.
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            snapshot.Complete(ex);
        }

        if (!ReferenceEquals(Snapshot, snapshot))
        {
            // A newer navigation won the race while we were finishing.
            return;
        }

        if (!cursorPlaced && selectName is not null)
        {
            _list.MoveCursorToName(selectName, StringComparison.Ordinal);
        }

        _list.RequestRedraw();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Posts to the UI thread if needed. Used by background metadata work later.</summary>
    internal static void OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
