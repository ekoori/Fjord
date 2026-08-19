using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Fjord.Core.FileSystem;
using Fjord.Core.Panes;

namespace Fjord.App.Views;

/// <summary>
/// Dragging entries with the mouse, while the mode is on.
/// </summary>
/// <remarks>
/// Off by default, and that is the whole reason this is a mode. Fjord has no title bar, so
/// the listing is most of the window's surface and it is what the user grabs to move the
/// window — pressing on a row and moving the mouse cannot mean both "move the window" and
/// "move these files". With the mode off nothing here runs and the press falls through to
/// the window exactly as it always did; with it on, the listing takes the press and the
/// window is still draggable by the bars above and below it.
/// <para>
/// The drop is performed by whichever side receives it. A drop inside Fjord is queued as an
/// ordinary copy or move, so it shows progress and undoes with <c>u</c> like anything else;
/// a drop into another application is that application's business, and the result of the
/// drag is deliberately ignored rather than acted on twice.
/// </para>
/// </remarks>
internal sealed class FileDragMode
{
    /// <summary>How far the pointer must travel before a press becomes a drag.</summary>
    /// <remarks>
    /// The same reasoning as the tab strip's: without it an unsteady click starts a file
    /// drag, and a file drag that was meant as a click can move files.
    /// </remarks>
    private const double Threshold = 4;

    private readonly FileListView _list;
    private readonly Func<IPane?> _pane;
    private readonly Action<IReadOnlyList<string>, string, bool> _drop;
    private readonly Action _open;

    private PointerPressedEventArgs? _pressed;
    private Point _origin;
    private bool _dragging;
    private bool _enabled;

    /// <param name="drop">Sources, destination directory, and whether to copy rather than move.</param>
    /// <param name="open">What a double click means: the same as Enter.</param>
    public FileDragMode(
        FileListView list,
        Func<IPane?> pane,
        Action<IReadOnlyList<string>, string, bool> drop,
        Action open)
    {
        _list = list;
        _pane = pane;
        _drop = drop;
        _open = open;

        _list.PointerPressed += OnPressed;
        _list.PointerMoved += OnMoved;
        _list.PointerReleased += OnReleased;

        DragDrop.AddDragOverHandler(_list, OnDragOver);
        DragDrop.AddDropHandler(_list, OnDrop);
        DragDrop.AddDragLeaveHandler(_list, (_, _) => _list.DropTargetRow = -1);
    }

    /// <summary>Whether the mouse moves entries rather than the window.</summary>
    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            _enabled = value;

            // Only while the mode is on: a window that accepts drops it will not act on is
            // worse than one that visibly refuses them.
            DragDrop.SetAllowDrop(_list, value);

            if (!value)
            {
                _list.DropTargetRow = -1;
                _pressed = null;
                _dragging = false;
            }
        }
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_enabled || !e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var row = _list.RowAt(e.GetPosition(_list));

        // Empty space below the last row is not an entry, so a press there is left to the
        // window: even in drag mode there has to be somewhere to grab a window with no
        // title bar, and the space under a short listing is the obvious one.
        if (row < 0)
        {
            return;
        }

        if (e.ClickCount > 1)
        {
            _list.CursorIndex = row;
            _open();
            e.Handled = true;
            return;
        }

        // Pressing outside the marked set moves the cursor and drags that one row; pressing
        // inside it drags the whole set. What every file manager does, and the only reading
        // that lets a marked group be dragged at all.
        if (!_list.Selection[row])
        {
            _list.CursorIndex = row;
        }

        // A press on a row is also a click on the list, and the keyboard should follow it:
        // otherwise clicking a row while the command line is open leaves the next keystroke
        // going somewhere else.
        _list.Focus();

        _pressed = e;
        _origin = e.GetPosition(_list);
        _dragging = false;

        // Marks the press as ours so the window does not also start moving itself.
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_enabled || _pressed is null || _dragging)
        {
            return;
        }

        var position = e.GetPosition(_list);

        if (Math.Abs(position.X - _origin.X) < Threshold && Math.Abs(position.Y - _origin.Y) < Threshold)
        {
            return;
        }

        _dragging = true;
        var pressed = _pressed;
        _pressed = null;

        _ = StartAsync(pressed);
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pressed = null;
        _dragging = false;
    }

    /// <remarks>
    /// The pointer-pressed arguments are held from the press and handed over here, because
    /// that is what <see cref="DragDrop.DoDragDropAsync"/> takes and a press is not yet a
    /// drag: starting one on the press itself would make every click a drag.
    /// </remarks>
    private async Task StartAsync(PointerPressedEventArgs pressed)
    {
        if (Sources() is not { Count: > 0 } paths || TopLevel.GetTopLevel(_list) is not { } top)
        {
            _dragging = false;
            return;
        }

        var transfer = new DataTransfer();

        foreach (var path in paths)
        {
            if (await AsStorageItem(top, path).ConfigureAwait(true) is { } item)
            {
                transfer.Add(DataTransferItem.CreateFile(item));
            }
        }

        if (transfer.Items.Count == 0)
        {
            _dragging = false;
            return;
        }

        try
        {
            // Both effects offered so the other end can choose — Explorer picks move or copy
            // from the modifier held when the button comes up, and performs whichever it
            // chose. The result is ignored on purpose: acting on it here would repeat a move
            // that has already happened.
            await DragDrop.DoDragDropAsync(pressed, transfer, DragDropEffects.Move | DragDropEffects.Copy)
                .ConfigureAwait(true);
        }
        catch (InvalidOperationException)
        {
            // The platform declined to start a drag. Nothing is broken and nothing moved.
        }
        finally
        {
            _dragging = false;
            _list.DropTargetRow = -1;
        }
    }

    /// <summary>What the drag carries: the marked set, or the row under the cursor.</summary>
    private List<string> Sources()
    {
        if (_pane() is not { } pane || pane.Snapshot is null)
        {
            return [];
        }

        // Nothing from inside an archive: those are not paths the filesystem can open, so
        // the other end of the drag would be handed something that does not exist.
        return Targets(pane).Where(p => !ArchivePath.IsInside(p)).ToList();
    }

    private static IEnumerable<string> Targets(IPane pane)
    {
        if (pane.Selection.Count > 0)
        {
            return pane.Selection.Indices().Select(pane.PathAt).Where(p => p is not null)!;
        }

        return pane.PathAt(pane.CursorIndex) is { } path ? [path] : [];
    }

    private static async Task<IStorageItem?> AsStorageItem(TopLevel top, string path)
    {
        try
        {
            var uri = new Uri(path);

            return System.IO.Directory.Exists(path)
                ? await top.StorageProvider.TryGetFolderFromPathAsync(uri).ConfigureAwait(true)
                : await top.StorageProvider.TryGetFileFromPathAsync(uri).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException or IOException
                                       or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (!_enabled || Destination(e) is null)
        {
            e.DragEffects = DragDropEffects.None;
            _list.DropTargetRow = -1;
            return;
        }

        e.DragEffects = IsCopy(e) ? DragDropEffects.Copy : DragDropEffects.Move;
        _list.DropTargetRow = TargetRow(e);
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        _list.DropTargetRow = -1;

        if (!_enabled || Destination(e) is not { } destination)
        {
            return;
        }

        var sources = (e.DataTransfer.TryGetFiles() ?? [])
            .Select(f => f.TryGetLocalPath())
            .Where(p => p is { Length: > 0 })
            .ToList();

        // Something already in the destination has nowhere to go. Dropping onto the listing
        // it came from is the usual way to arrive here, and quietly doing nothing is right:
        // the alternative is an error for a gesture that asked for nothing.
        var moving = sources
            .Where(p => !string.Equals(
                Path.GetDirectoryName(p), destination, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (moving.Count == 0)
        {
            return;
        }

        _drop(moving!, destination, IsCopy(e));
        e.Handled = true;
    }

    /// <summary>Ctrl copies, as it does in every file manager; without it the drop moves.</summary>
    private static bool IsCopy(DragEventArgs e) => e.KeyModifiers.HasFlag(KeyModifiers.Control);

    private int TargetRow(DragEventArgs e)
    {
        var row = _list.RowAt(e.GetPosition(_list));
        var view = _pane()?.Snapshot?.View ?? default;

        // Only a folder is a target in its own right. A file under the pointer means the
        // listing it is in, which is what dropping "here" means.
        return row >= 0 && row < view.Count && view[row].IsDirectory ? row : -1;
    }

    /// <summary>Where a drop would land, or null when this listing cannot take one.</summary>
    private string? Destination(DragEventArgs e)
    {
        // A scrapbook, a search result and the drive list are not places, and an archive is
        // read-only. None of them can be written into.
        if (_pane() is not { } pane || pane.IsVirtual || ArchivePath.IsInside(pane.Directory))
        {
            return null;
        }

        if (!e.DataTransfer.Contains(DataFormat.File))
        {
            return null;
        }

        return TargetRow(e) is var row and >= 0 ? pane.PathAt(row) : pane.Directory;
    }
}
