using System.Diagnostics;
using Avalonia.Threading;
using Fjord.App.Views;
using Fjord.Core.FileSystem;
using Fjord.Core.Navigation;
using Fjord.Core.Panes;
using Fjord.Core.Selection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly ILogger _log;

    private CancellationTokenSource? _navigation;

    // Where a virtual listing was opened from, and null the rest of the time — which is
    // also how the pane knows it is showing one.
    private (string Directory, string? CursorName)? _leftBehind;

    // What opening a row means, when the rows are names rather than paths.
    private Func<string, Task>? _openVirtual;

    // Virtual listings this pane has shown, by label. Small and bounded by how many
    // scrapbooks the user opens in one tab, and cleared with the pane.
    private readonly Dictionary<string, VirtualListing> _virtualListings = new(StringComparer.Ordinal);

    /// <summary>
    /// Given first refusal on every navigation to a different directory.
    /// </summary>
    /// <remarks>
    /// How a locked tab works. The pane knows nothing about tabs, so the workspace supplies
    /// this and answers true when it handled the navigation somewhere else. Putting it here
    /// rather than in each navigation command means every route out — Enter, h, :cd, a
    /// quickmark, history — goes through the same door.
    /// </remarks>
    public Func<string, string?, bool>? Redirect { get; set; }

    // State held while this pane is not the one on screen. One FileListView serves every
    // tab, so an inactive pane cannot leave its cursor and selection there; it keeps them
    // here and hands them back on activation. That is what makes switching tabs a pointer
    // swap that restores position exactly.
    private int _detachedCursor;
    private int? _detachedVisualAnchor;
    private SelectionSet _detachedSelection = new();
    private double _detachedScrollOffset;
    private bool _isActive;

    public PaneController(
        IDirectoryLister lister,
        FileListView list,
        SortSpec? sort = null,
        FilterSpec? filter = null,
        ILogger? log = null)
    {
        _lister = lister;
        _list = list;
        _log = log ?? NullLogger.Instance;
        Sort = sort ?? SortSpec.Default;
        Filter = filter ?? FilterSpec.Default;
    }

    public DirectorySnapshot? Snapshot { get; private set; }

    public NavigationHistory History { get; } = new();

    public SortSpec Sort { get; private set; }

    public FilterSpec Filter { get; private set; }

    /// <summary>The directory being shown. Empty before the first navigation.</summary>
    public string Directory => Snapshot?.Directory ?? string.Empty;

    /// <summary>Whether this pane is the one the shared view is currently showing.</summary>
    public bool IsActive => _isActive;

    public int CursorIndex
    {
        get => _isActive ? _list.CursorIndex : _detachedCursor;
        set
        {
            if (_isActive)
            {
                _list.CursorIndex = value;
            }
            else
            {
                _detachedCursor = Math.Max(0, value);
            }
        }
    }

    public int? VisualAnchor
    {
        get => _isActive ? _list.VisualAnchor : _detachedVisualAnchor;
        set
        {
            if (_isActive)
            {
                _list.VisualAnchor = value;
                _list.RequestRedraw();
            }
            else
            {
                _detachedVisualAnchor = value;
            }
        }
    }

    public SelectionSet Selection => _isActive ? _list.Selection : _detachedSelection;

    /// <summary>
    /// Rows that fit the viewport.
    /// </summary>
    /// <remarks>
    /// An inactive pane has no viewport of its own, so it borrows the live one: a page
    /// motion run against a background tab should still move by a screenful.
    /// </remarks>
    public int PageSize => _list.PageSize;

    public EntryRange? VisualRange
    {
        get
        {
            if (_isActive)
            {
                return _list.VisualRange is { } range ? EntryRange.Between(range.From, range.To) : null;
            }

            return _detachedVisualAnchor is { } anchor
                ? EntryRange.Between(anchor, _detachedCursor)
                : null;
        }
    }

    /// <summary>Points the shared view at this pane.</summary>
    public void Activate()
    {
        if (_isActive)
        {
            return;
        }

        _isActive = true;
        _list.Adopt(Snapshot, _detachedCursor, _detachedVisualAnchor, _detachedSelection, _detachedScrollOffset);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Takes this pane's state off the shared view before another pane adopts it.</summary>
    public void Deactivate()
    {
        if (!_isActive)
        {
            return;
        }

        _detachedCursor = _list.CursorIndex;
        _detachedVisualAnchor = _list.VisualAnchor;
        _detachedSelection = _list.Selection;
        _detachedScrollOffset = _list.ScrollOffset;
        _isActive = false;
    }

    public string? CursorName => CurrentCursorName();

    public void MoveCursor(int delta)
    {
        // int.MaxValue is how "go to the end" is spelled; adding to it would wrap.
        CursorIndex = delta > 0 && CursorIndex > int.MaxValue - delta
            ? int.MaxValue
            : CursorIndex + delta;
    }

    public void MoveCursorTo(int row) => CursorIndex = row;

    public bool MoveCursorToName(string name)
    {
        if (_isActive)
        {
            // Synchronously, because the caller is usually placing the cursor on an entry
            // that arrived in the batch it just appended, and the view the list searches is
            // refreshed lazily on the next paint. Without this the lookup runs against the
            // previous directory's rows and always misses — which is why going back never
            // restored the cursor to the folder you came out of.
            _list.SyncView();
            return _list.MoveCursorToName(name, StringComparison.Ordinal);
        }

        var row = (Snapshot?.View ?? default).IndexOfName(name, StringComparison.Ordinal);
        if (row < 0)
        {
            return false;
        }

        _detachedCursor = row;
        return true;
    }

    public string? PathAt(int row)
    {
        var view = Snapshot?.View ?? default;

        if (row < 0 || row >= view.Count)
        {
            return null;
        }

        var path = Path.Combine(Directory, view[row].Name);

        if (!PathSafety.IsAddressable(path))
        {
            _log.LogWarning(
                "{Name} in {Directory} cannot be addressed on this platform and was skipped",
                view[row].Name,
                Directory);
            return null;
        }

        return path;
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
        // A label this pane has shown before is a listing to replay, not a path to resolve.
        // This is what makes back and forward work through a scrapbook.
        if (_virtualListings.TryGetValue(directory, out var remembered))
        {
            await ShowVirtualAsync(
                directory, remembered.Paths, remembered.Annotations, remembered.Open)
                .ConfigureAwait(true);

            if (selectName is not null)
            {
                MoveCursorToName(selectName);
            }

            return;
        }

        string full;

        try
        {
            // Normalising an archive path would fold the part inside the archive against
            // the filesystem's rules, which are not the archive's. Only the file itself is
            // a real path.
            full = ArchivePath.Parse(directory) is { } inside
                ? (inside with { Archive = Path.GetFullPath(inside.Archive) }).ToString()
                : Path.GetFullPath(directory);
        }
        catch (Exception ex) when (ex is ArgumentException
                                       or NotSupportedException
                                       or PathTooLongException)
        {
            // Nothing above here can be trusted to have handed us a path: a restored
            // session, a scrapbook label, a hand-edited database. Refusing one navigation
            // is survivable; throwing out of here is not, because the caller is often a
            // fire-and-forget restore that takes every other tab down with it.
            _log.LogWarning(ex, "cannot navigate to {Directory}", directory);
            return;
        }

        // Asked before anything is torn down, and only for a real move: a reload navigates
        // to the directory already showing, and spawning a tab for that would make r
        // unusable.
        if (!string.Equals(full, Directory, StringComparison.Ordinal)
            && Redirect is { } redirect
            && redirect(full, selectName))
        {
            return;
        }

        // Navigating anywhere real ends the virtual listing, so entering a scrapbook item's
        // folder does not leave the pane thinking it is still in the scrapbook.
        _leftBehind = null;
        _openVirtual = null;

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
        if (_isActive)
        {
            _list.Snapshot = snapshot;
        }
        else
        {
            _detachedCursor = 0;
            _detachedVisualAnchor = null;
            _detachedSelection = new SelectionSet();
            _detachedScrollOffset = 0;
        }

        Changed?.Invoke(this, EventArgs.Empty);

        await LoadAsync(snapshot, selectName, cts.Token).ConfigureAwait(true);
    }

    /// <summary>
    /// Shows an arbitrary set of paths as a listing, instead of a directory.
    /// </summary>
    /// <remarks>
    /// Used by the scrapbook, whose contents come from many directories at once. Each
    /// entry's name is its full path, which is what lets the rest of the app treat it as an
    /// ordinary listing: Path.Combine returns its second argument when that is rooted, so
    /// PathAt and enter resolve correctly against the synthetic label.
    /// <para>
    /// No enumeration is started, so there is nothing to cancel — but the previous
    /// navigation is still cancelled, or its batches would arrive into the pane afterwards
    /// and overwrite this.
    /// </para>
    /// </remarks>
    /// <param name="annotations">
    /// A note per entry, shown in its own column. Keyed by the entry name, which for a
    /// virtual listing is the whole path.
    /// </param>
    /// <param name="open">
    /// What Enter on a row means, when the rows are names rather than paths. Null leaves
    /// them behaving like ordinary entries.
    /// </param>
    public async Task ShowVirtualAsync(
        string label,
        IReadOnlyList<string> paths,
        IReadOnlyDictionary<string, string>? annotations = null,
        Func<string, Task>? open = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        // Where Escape and h go back to. Kept separately from history because those keys
        // mean "leave this view", which is not the same as "one step back" once you have
        // been somewhere else since.
        _leftBehind ??= (Directory, CurrentCursorName());

        // Remembered so history can replay it. A scrapbook is not on disk, so going back to
        // one cannot be a matter of listing a directory — without this, entering a folder
        // from a scrapbook and pressing back landed on whatever the tab showed before the
        // scrapbook was ever opened.
        _virtualListings[label] = new VirtualListing(paths, annotations, open);

        var reshowing = string.Equals(Directory, label, StringComparison.Ordinal);

        if (!reshowing)
        {
            History.UpdateCursor(CurrentCursorName());
            History.Push(label);
        }

        var previous = _navigation;
        _navigation = null;

        if (previous is not null)
        {
            await previous.CancelAsync().ConfigureAwait(true);
            previous.Dispose();
        }

        // Refreshing the listing you are already looking at should not throw away where you
        // were in it, which is what adding a note to the third entry does.
        var cursor = reshowing ? CurrentCursorName() : null;

        var snapshot = new DirectorySnapshot(label, Sort, Filter with { ShowHidden = true })
        {
            Annotations = annotations,
        };

        snapshot.Append(paths.Select(SessionHost.DescribePath).ToArray());
        snapshot.Complete();

        Snapshot = snapshot;
        _openVirtual = open;

        if (_isActive)
        {
            _list.Snapshot = snapshot;
            _list.SyncView();
        }

        if (cursor is not null)
        {
            MoveCursorToName(cursor);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A listing that is not a directory, kept so history can show it again.</summary>
    private sealed record VirtualListing(
        IReadOnlyList<string> Paths,
        IReadOnlyDictionary<string, string>? Annotations,
        Func<string, Task>? Open);

    public bool IsVirtual => _leftBehind is not null;

    /// <summary>
    /// The directory a session should record for this pane.
    /// </summary>
    /// <remarks>
    /// A virtual listing's <see cref="Directory"/> is a label like <c>scrapbook:default</c>,
    /// not a path. Saving that put a tab in the session that could never be restored — and
    /// on Windows the colon made <see cref="Path.GetFullPath(string)"/> throw a
    /// <see cref="NotSupportedException"/> on the way back in, which took the whole restore
    /// with it and left an empty window with nothing to explain why.
    /// </remarks>
    public string RestorableDirectory => _leftBehind?.Directory ?? Directory;

    public Task<bool> OpenVirtualAsync(int row)
    {
        var view = Snapshot?.View ?? default;

        if (_openVirtual is not { } open || row < 0 || row >= view.Count)
        {
            return Task.FromResult(false);
        }

        return OpenAndReport(open, view[row].Name);
    }

    private static async Task<bool> OpenAndReport(Func<string, Task> open, string name)
    {
        await open(name).ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// Returns to the directory a virtual listing was opened from.
    /// </summary>
    /// <remarks>
    /// Bound to Escape as well as to the keys that go up, because a scrapbook looks like a
    /// listing and every instinct for getting out of one should work.
    /// </remarks>
    public async Task<bool> LeaveVirtualAsync()
    {
        if (_leftBehind is not { } origin)
        {
            return false;
        }

        _leftBehind = null;
        await NavigateAsync(origin.Directory, origin.CursorName, recordHistory: false).ConfigureAwait(true);
        return true;
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
        if (_isActive)
        {
            _list.SyncView();
        }

        if (name is not null)
        {
            MoveCursorToName(name);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string? CurrentCursorName()
    {
        var view = Snapshot?.View ?? default;
        var cursor = CursorIndex;
        return view.Count > 0 && cursor < view.Count ? view[cursor].Name : null;
    }

    private Task GoAsync(HistoryEntry? entry) =>
        entry is { } target
            ? NavigateAsync(target.Directory, target.CursorName, recordHistory: false)
            : Task.CompletedTask;

    private async Task LoadAsync(DirectorySnapshot snapshot, string? selectName, CancellationToken ct)
    {
        var options = ListingOptions.For(Sort, Filter, sizeColumnVisible: true, modifiedColumnVisible: true);
        var cursorPlaced = selectName is null;

        // Wall clock rather than a count of entries: the number that matters is how long
        // the user waited, and on a network mount the two are unrelated.
        var clock = Stopwatch.StartNew();
        var batches = 0;
        var entries = 0;

        try
        {
            await foreach (var batch in _lister.ListAsync(snapshot.Directory, options, ct).ConfigureAwait(true))
            {
                snapshot.Append(batch.Span);

                batches++;
                entries += batch.Length;

                // Per batch, never per entry. A LogTrace in the transform would make a
                // 100,000-entry directory take the better part of a minute.
                _log.LogDebug(
                    "batch {Batch} of {Directory}: {Count} entries, {Elapsed} ms in",
                    batches,
                    snapshot.Directory,
                    batch.Length,
                    clock.ElapsedMilliseconds);

                // One coalesced repaint per batch, and none at all for a background tab:
                // it keeps loading into its own snapshot and is simply not on screen.
                if (_isActive)
                {
                    _list.RequestRedraw();
                }

                if (!cursorPlaced && MoveCursorToName(selectName!))
                {
                    cursorPlaced = true;
                }
            }

            snapshot.Complete();

            _log.LogInformation(
                "listed {Directory}: {Entries} entries in {Elapsed} ms",
                snapshot.Directory,
                entries,
                clock.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            // Navigated away. The snapshot is discarded with the pane's old state.
            _log.LogDebug(
                "abandoned {Directory} after {Entries} entries, {Elapsed} ms",
                snapshot.Directory,
                entries,
                clock.ElapsedMilliseconds);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            snapshot.Complete(ex);
            _log.LogWarning(ex, "could not list {Directory}", snapshot.Directory);
        }

        if (!ReferenceEquals(Snapshot, snapshot))
        {
            // A newer navigation won the race while we were finishing.
            return;
        }

        if (!cursorPlaced && selectName is not null)
        {
            MoveCursorToName(selectName);
        }

        if (_isActive)
        {
            _list.RequestRedraw();
        }
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
