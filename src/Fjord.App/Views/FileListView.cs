using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Fjord.Core.FileSystem;
using Fjord.Core.Selection;

namespace Fjord.App.Views;

/// <summary>
/// The directory listing, drawn directly rather than built from per-row controls.
/// </summary>
/// <remarks>
/// <b>Why immediate mode.</b> A virtualizing panel realizes a container per visible row
/// and recycles them while scrolling. That is fine for a list box and wasteful here: the
/// list has to stay smooth at 100,000 entries, every row is the same shape, and the vim
/// cursor and visual-range painting want direct control anyway. Drawing straight into a
/// <see cref="DrawingContext"/> means the cost of a frame is set by the size of the
/// window, not the size of the directory — 100,000 entries costs exactly what 10 does.
/// <para>
/// <b>Accepted debt.</b> No automation peer and no text selection. Both would come free
/// with a ListBox. Both are hand-built work before Fjord can claim accessibility, and
/// that is recorded in docs/architecture.md rather than discovered later.
/// </para>
/// </remarks>
public sealed class FileListView : Control, ILogicalScrollable
{
    /// <summary>
    /// Cached row visuals, indexed by <c>row % Length</c>.
    /// </summary>
    /// <remarks>
    /// Sized comfortably past any realistic viewport. Building a <see cref="FormattedText"/>
    /// runs bidi analysis, itemisation, font fallback and shaping — tens of microseconds
    /// each, and three per row. Rebuilding all of them every frame would spend a third of
    /// the frame budget redrawing text identical to last frame's.
    /// </remarks>
    private readonly RowVisual?[] _visuals = new RowVisual?[256];

    private readonly ListTheme _theme;

    private DirectorySnapshot? _snapshot;
    private SnapshotView _view;
    private int _cursor;
    private int _generation = -1;
    private int _redrawQueued;
    private Size _viewport;
    private Vector _offset;
    private double _lastScaling = 1;

    // Rolling window of Render durations, for the frame-stats overlay. A ring rather than
    // a list so measuring costs no allocation in the paint loop.
    private readonly double[] _frameTimes = new double[120];
    private int _frameIndex;
    private int _frameCount;

    public FileListView(ListTheme theme)
    {
        _theme = theme;
        Focusable = true;
        ClipToBounds = true;
    }

    public event EventHandler? ScrollInvalidated;

    /// <summary>Rows currently painted, as a half-open range. Read by the metadata worker.</summary>
    public (int First, int Last) VisibleRange { get; private set; }

    /// <summary>
    /// Whether to measure and draw per-frame render times.
    /// </summary>
    /// <remarks>
    /// Self-measurement beats an external profiler for a paint loop this small, and it
    /// stays available to a user reporting that scrolling feels slow.
    /// </remarks>
    public bool ShowFrameStats { get; set; }

    /// <summary>Mean and 99th-percentile Render time over the last frames, in milliseconds.</summary>
    public (double Mean, double P99) FrameStats()
    {
        if (_frameCount == 0)
        {
            return (0, 0);
        }

        var sample = _frameTimes.Take(_frameCount).OrderBy(x => x).ToArray();
        return (sample.Average(), sample[Math.Min(sample.Length - 1, (int)(sample.Length * 0.99))]);
    }

    /// <summary>
    /// The selection of whichever pane is showing.
    /// </summary>
    /// <remarks>
    /// Replaced wholesale on a tab switch rather than copied, because one
    /// <see cref="FileListView"/> serves every tab — twenty tabs each with their own
    /// 128-slot text cache would be pure waste, so the view is re-pointed instead.
    /// </remarks>
    public SelectionSet Selection { get; private set; } = new();

    /// <summary>Vertical scroll position, saved and restored across tab switches.</summary>
    public double ScrollOffset
    {
        get => _offset.Y;
        set => Offset = _offset.WithY(value);
    }

    /// <summary>
    /// Points the view at a different pane's state.
    /// </summary>
    /// <remarks>
    /// Everything the user can see is swapped in one go, and the row cache is dropped
    /// because every row now shows something else. Cheap: no enumeration is started, no
    /// background work is touched, and the pane being left keeps loading into its own
    /// snapshot.
    /// </remarks>
    public void Adopt(DirectorySnapshot? snapshot, int cursor, int? visualAnchor, SelectionSet selection,
        double scrollOffset)
    {
        _snapshot = snapshot;
        Selection = selection;
        VisualAnchor = visualAnchor;

        InvalidateRowCache();
        RefreshView();

        _cursor = Math.Clamp(cursor, 0, Math.Max(0, _view.Count - 1));
        ScrollOffset = scrollOffset;
        EnsureCursorVisible();
        InvalidateVisual();
    }

    /// <summary>
    /// Where visual mode was entered, or null outside visual mode.
    /// </summary>
    /// <remarks>
    /// The highlighted range is derived from this and the cursor at paint time and is only
    /// written into <see cref="Selection"/> when an operator runs. That is what makes
    /// <c>v3j</c> followed by Escape leave the committed selection untouched, and it means
    /// motions need no visual-mode-specific code at all.
    /// </remarks>
    public int? VisualAnchor { get; set; }

    public DirectorySnapshot? Snapshot
    {
        get => _snapshot;
        set
        {
            _snapshot = value;
            _cursor = 0;
            VisualAnchor = null;
            Selection.Clear();
            InvalidateRowCache();
            RefreshView();
        }
    }

    public int CursorIndex
    {
        get => _cursor;
        set
        {
            var clamped = Math.Clamp(value, 0, Math.Max(0, _view.Count - 1));
            if (clamped == _cursor)
            {
                return;
            }

            _cursor = clamped;
            EnsureCursorVisible();
            RequestRedraw();
        }
    }

    /// <summary>Rows that fit in the viewport, for <c>Ctrl-d</c> and page motions.</summary>
    public int PageSize => Math.Max(1, (int)(_viewport.Height / _theme.RowHeight));

    // ---- ILogicalScrollable. Implementing this rather than hand-rolling a scrollbar is
    // what buys mouse wheel, trackpad inertia and Fluent-matching chrome from ScrollViewer.

    public Size Extent { get; private set; }

    public Vector Offset
    {
        get => _offset;
        set
        {
            var clamped = new Vector(
                Math.Max(0, value.X),
                Math.Clamp(value.Y, 0, Math.Max(0, Extent.Height - _viewport.Height)));

            if (clamped == _offset)
            {
                return;
            }

            _offset = clamped;
            InvalidateVisual();
        }
    }

    public Size Viewport => _viewport;

    public bool CanHorizontallyScroll { get; set; }

    public bool CanVerticallyScroll { get; set; } = true;

    /// <summary>
    /// The single most important line in this control.
    /// </summary>
    /// <remarks>
    /// False makes <c>ScrollContentPresenter</c> size the child to <see cref="Extent"/>.
    /// For 100,000 rows at ~22 DIP that requests a 2.2-million-DIP-tall visual, well past
    /// where Skia's single-precision coordinates stop being exact.
    /// </remarks>
    public bool IsLogicalScrollEnabled => true;

    /// <summary>Wheel and arrow steps, in rows rather than DIPs, so rows never land clipped.</summary>
    public Size ScrollSize => new(16, _theme.RowHeight);

    /// <summary>PageUp/PageDown, keeping one row of overlap for continuity.</summary>
    public Size PageScrollSize =>
        new(_viewport.Width, Math.Max(_theme.RowHeight, _viewport.Height - _theme.RowHeight));

    public bool BringIntoView(Control target, Rect targetRect) => false;

    public Control? GetControlInDirection(NavigationDirection direction, Control? from) => null;

    public void RaiseScrollInvalidated(EventArgs e) => ScrollInvalidated?.Invoke(this, e);

    /// <summary>
    /// Asks for one repaint, coalescing concurrent requests. Safe from any thread.
    /// </summary>
    /// <remarks>
    /// This is the guard that keeps background metadata from wedging the app. Posting an
    /// invalidation per arriving item would put 100,000 work items on the dispatcher
    /// queue, starving key input — the app would feel frozen while technically making
    /// progress. Here, any number of requests between two frames collapse into one post.
    /// </remarks>
    public void RequestRedraw()
    {
        if (Interlocked.Exchange(ref _redrawQueued, 1) == 1)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                Volatile.Write(ref _redrawQueued, 0);
                RefreshView();
            },
            DispatcherPriority.Render);
    }

    /// <summary>
    /// Drops cached row visuals after the theme changed.
    /// </summary>
    /// <remarks>
    /// Called on config reload. Row height, colours and typeface are all baked into the
    /// cached text, so none of it survives.
    /// </remarks>
    public void RefreshTheme()
    {
        InvalidateRowCache();
        UpdateExtent();
        EnsureCursorVisible();
        InvalidateVisual();
    }

    /// <summary>
    /// Re-reads the snapshot now rather than on the next frame.
    /// </summary>
    /// <remarks>
    /// For callers that immediately act on rows — locating the cursor's entry after a
    /// re-sort, say. <see cref="RequestRedraw"/> defers to the next frame, so those
    /// callers would otherwise search the pre-sort order.
    /// </remarks>
    public void SyncView() => RefreshView();

    /// <summary>Scrolls the minimum distance needed to bring the cursor row into view.</summary>
    public void EnsureCursorVisible()
    {
        if (_view.Count == 0 || _viewport.Height <= 0)
        {
            return;
        }

        var rowTop = _cursor * _theme.RowHeight;
        var rowBottom = rowTop + _theme.RowHeight;

        if (rowTop < _offset.Y)
        {
            Offset = _offset.WithY(rowTop);
        }
        else if (rowBottom > _offset.Y + _viewport.Height)
        {
            Offset = _offset.WithY(rowBottom - _viewport.Height);
        }
    }

    /// <summary>Moves the cursor to the row showing a name, if it is present.</summary>
    public bool MoveCursorToName(string name, StringComparison comparison)
    {
        var row = _view.IndexOfName(name, comparison);
        if (row < 0)
        {
            return false;
        }

        CursorIndex = row;
        return true;
    }

    /// <summary>The rows the pending visual range covers, or null when not in visual mode.</summary>
    public (int From, int To)? VisualRange => VisualAnchor is { } anchor
        ? (Math.Min(anchor, _cursor), Math.Max(anchor, _cursor))
        : null;

    protected override Size MeasureOverride(Size availableSize)
    {
        // With logical scrolling the presenter hands us the viewport, but a control can
        // still be measured with infinity (a StackPanel parent, a designer surface).
        // Returning infinity from here would throw during layout.
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_viewport != finalSize)
        {
            _viewport = finalSize;
            UpdateExtent();
        }

        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        var started = ShowFrameStats ? Stopwatch.GetTimestamp() : 0L;
        RenderCore(context);

        if (!ShowFrameStats)
        {
            return;
        }

        _frameTimes[_frameIndex] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _frameIndex = (_frameIndex + 1) % _frameTimes.Length;
        _frameCount = Math.Min(_frameCount + 1, _frameTimes.Length);

        DrawFrameStats(context);
    }

    private void DrawFrameStats(DrawingContext context)
    {
        var (mean, p99) = FrameStats();
        var rows = _view.Count;
        var text = Text(
            $"render {mean:F2} ms mean, {p99:F2} ms p99   {rows:N0} rows",
            _theme.MutedText,
            300,
            _theme,
            TextAlignment.Right);

        var origin = new Point(_viewport.Width - 308, 4);
        context.FillRectangle(_theme.Stripe, new Rect(origin.X - 4, 0, 308, text.Height + 8));
        context.DrawText(text, origin);
    }

    private void RenderCore(DrawingContext context)
    {
        var theme = _theme;

        if (TopLevel.GetTopLevel(this) is { } top && Math.Abs(top.RenderScaling - _lastScaling) > 0.0001)
        {
            _lastScaling = top.RenderScaling;

            if (theme.SetScaling(_lastScaling))
            {
                InvalidateRowCache();

                // Deferred, not called here. UpdateExtent raises ScrollInvalidated, and
                // Avalonia throws "Visual was invalidated during the render pass" for
                // anything that dirties the tree from inside Render. This branch runs on
                // the first paint and on every DPI change — dragging the window to another
                // monitor was a crash waiting for someone to do it.
                Dispatcher.UIThread.Post(UpdateExtent, DispatcherPriority.Render);
            }
        }

        context.FillRectangle(theme.Background, new Rect(_viewport));

        var view = _view;
        if (view.Count == 0)
        {
            return;
        }

        var rowHeight = theme.RowHeight;
        var first = Math.Max(0, (int)(_offset.Y / rowHeight));
        var last = Math.Min(view.Count - 1, (int)((_offset.Y + _viewport.Height) / rowHeight));

        if (last < first)
        {
            return;
        }

        VisibleRange = (first, last);

        // Sub-row scroll remainder, so scrolling is smooth rather than row-by-row.
        var yBase = -(_offset.Y - (first * rowHeight));

        DrawBackgrounds(context, view, first, last, yBase, rowHeight, theme);

        for (var row = first; row <= last; row++)
        {
            DrawRow(context, view, row, yBase + ((row - first) * rowHeight), rowHeight, theme);
        }
    }

    /// <summary>
    /// Paints row backgrounds, merging consecutive rows that share one into a single fill.
    /// </summary>
    /// <remarks>
    /// A visual selection covering the whole viewport becomes one rectangle rather than
    /// forty, and the common case — an unselected list — becomes zero.
    /// </remarks>
    private void DrawBackgrounds(
        DrawingContext context, SnapshotView view, int first, int last,
        double yBase, double rowHeight, ListTheme theme)
    {
        var runStart = first;
        var runBrush = BackgroundFor(first);

        for (var row = first + 1; row <= last + 1; row++)
        {
            var brush = row <= last ? BackgroundFor(row) : null;
            if (ReferenceEquals(brush, runBrush) && row <= last)
            {
                continue;
            }

            if (runBrush is not null)
            {
                var top = yBase + ((runStart - first) * rowHeight);
                context.FillRectangle(runBrush, new Rect(0, top, _viewport.Width, (row - runStart) * rowHeight));
            }

            runStart = row;
            runBrush = brush;
        }

        // A left accent bar rather than a border: borders at fractional scaling shimmer
        // as the row moves under the pixel grid.
        if (_cursor >= first && _cursor <= last)
        {
            var top = yBase + ((_cursor - first) * rowHeight);
            context.FillRectangle(theme.CursorBar, new Rect(0, top, theme.CursorBarWidth, rowHeight));
        }

        _ = view;
    }

    /// <summary>Painter's algorithm, later wins: stripe, then selection, then visual, then cursor.</summary>
    private IBrush? BackgroundFor(int row)
    {
        if (row == _cursor)
        {
            return _theme.CursorBackground;
        }

        if (VisualRange is { } range && row >= range.From && row <= range.To)
        {
            return _theme.VisualBackground;
        }

        if (Selection[row])
        {
            return _theme.SelectedBackground;
        }

        return row % 2 == 1 ? _theme.Stripe : null;
    }

    private void DrawRow(
        DrawingContext context, SnapshotView view, int row, double y, double rowHeight, ListTheme theme)
    {
        var visual = GetOrBuildRow(view, row, theme);
        var textY = y + theme.TextOffsetY;

        context.DrawText(visual.Name, new Point(theme.LeftPadding + theme.CursorBarWidth, textY));

        var modifiedX = _viewport.Width - theme.ModifiedColumnWidth - theme.LeftPadding;
        var sizeX = modifiedX - theme.ColumnGap - theme.SizeColumnWidth;

        // Below a certain width the columns would overlap the name; drop them instead.
        if (sizeX > theme.LeftPadding * 4)
        {
            if (visual.Note is { } note)
            {
                // Between the name and the size, so the numeric columns stay where the eye
                // expects them whether or not this listing has notes.
                context.DrawText(note, new Point(sizeX - theme.ColumnGap - theme.NoteColumnWidth, textY));
            }

            context.DrawText(visual.Size, new Point(sizeX, textY));
            context.DrawText(visual.Modified, new Point(modifiedX, textY));
        }

        _ = rowHeight;
    }

    /// <summary>
    /// Returns the cached visuals for a row, building them if the slot is stale.
    /// </summary>
    /// <remarks>
    /// Scrolling at three rows a frame builds three rows' worth of text, not the whole
    /// viewport's. A slot is valid only when it holds this exact row at the current
    /// generation, so a resort, a theme change or a scaling change invalidate everything
    /// by bumping a counter rather than clearing an array.
    /// </remarks>
    private RowVisual GetOrBuildRow(SnapshotView view, int row, ListTheme theme)
    {
        var slot = row % _visuals.Length;
        if (_visuals[slot] is { } cached && cached.Row == row && cached.Generation == _generation)
        {
            return cached;
        }

        ref readonly var entry = ref view[row];

        var nameBrush = entry.IsBroken ? theme.BrokenText
            : entry.IsDirectory ? theme.DirectoryText
            : theme.FileText;

        var note = Annotation(entry.Name);

        var nameWidth = Math.Max(
            16,
            _viewport.Width
            - theme.ModifiedColumnWidth - theme.SizeColumnWidth
            - (note is null ? 0 : theme.NoteColumnWidth + theme.ColumnGap)
            - (theme.ColumnGap * 2) - (theme.LeftPadding * 2) - theme.CursorBarWidth);

        var built = new RowVisual(
            row,
            _generation,
            Text(entry.Name, nameBrush, nameWidth, theme, TextAlignment.Left),
            Text(SizeText(in entry), theme.MutedText, theme.SizeColumnWidth, theme, TextAlignment.Right),
            Text(FormatModified(in entry), theme.MutedText, theme.ModifiedColumnWidth, theme, TextAlignment.Right),
            note is null
                ? null
                : Text(note, theme.CursorBar, theme.NoteColumnWidth, theme, TextAlignment.Left));

        _visuals[slot] = built;
        return built;
    }

    private static FormattedText Text(
        string text, IBrush brush, double maxWidth, ListTheme theme, TextAlignment alignment) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, theme.Typeface, theme.FontSize, brush)
        {
            // Trimming at construction rather than a PushClip per row: a clip costs a
            // Skia save/restore forty times a frame for the same result.
            MaxTextWidth = maxWidth,

            // Without this, text too wide for its column wraps onto a second line and
            // spills into the row below instead of being trimmed. Easy to miss: it only
            // shows up once a font size or column width makes something overflow.
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = alignment,
        };

    private static string FormatModified(ref readonly FileEntry entry) =>
        entry.HasMetadata && entry.ModifiedUtcTicks > 0
            ? entry.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : string.Empty;

    /// <summary>Re-reads the snapshot and repaints. The snapshot read is a single volatile load.</summary>
    /// <remarks>
    /// Cached row visuals are dropped whenever the published arrays change, not merely
    /// when the row count changes. A re-sort or a filter change replaces the content of
    /// every row while leaving the count identical, and appending a batch merges new
    /// entries in among the existing ones — both shift which entry sits at a given row.
    /// Keying invalidation on count alone leaves the list drawing stale text in the new
    /// order's positions.
    /// </remarks>
    private void RefreshView()
    {
        var view = _snapshot?.View ?? default;
        var replaced = !view.IsSameAs(_view);

        _view = view;

        if (replaced)
        {
            InvalidateRowCache();
            UpdateExtent();
        }

        _cursor = Math.Clamp(_cursor, 0, Math.Max(0, _view.Count - 1));
        InvalidateVisual();
    }

    private void UpdateExtent()
    {
        Extent = new Size(_viewport.Width, _view.Count * _theme.RowHeight);

        // Re-clamp: the list shrinking under a scrolled viewport would otherwise leave
        // the offset past the end and the view blank.
        Offset = _offset;
        RaiseScrollInvalidated(EventArgs.Empty);
    }

    private void InvalidateRowCache() => _generation++;

    /// <summary>
    /// What goes in the size column.
    /// </summary>
    /// <remarks>
    /// A directory has no size of its own until someone walks it, so the column is blank
    /// until it has been measured and then shows the total. Measured sizes are told apart
    /// from file sizes in the listing by nothing at all, deliberately: the number means the
    /// same thing in both cases.
    /// </remarks>
    private string SizeText(in FileEntry entry)
    {
        if (!entry.IsDirectory)
        {
            return SizeFormatter.Format(entry.Size);
        }

        return _snapshot?.DirectorySizes.TryGetValue(entry.Name, out var measured) == true
            ? SizeFormatter.Format(measured)
            : string.Empty;
    }

    /// <summary>The note against an entry, or null when this listing has none.</summary>
    private string? Annotation(string name) =>
        _snapshot?.Annotations is { } notes && notes.TryGetValue(name, out var note) && note.Length > 0
            ? note
            : null;

    /// <summary>Cached text for one row. A class so a slot can be null when never built.</summary>
    /// <param name="Note">
    /// The entry's annotation, or null when the listing has none. It takes its width from
    /// the name rather than from the size and modified columns, which a scrapbook entry is
    /// often exactly what you are looking at it for.
    /// </param>
    private sealed record RowVisual(
        int Row,
        int Generation,
        FormattedText Name,
        FormattedText Size,
        FormattedText Modified,
        FormattedText? Note);
}
