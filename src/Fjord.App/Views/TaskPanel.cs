using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Fjord.Core.Operations;
using Fjord.Operations;

namespace Fjord.App.Views;

/// <summary>
/// Shows queued and running file operations, with progress and a way to cancel.
/// </summary>
/// <remarks>
/// Hidden when nothing is running, so it costs no space in the normal case. The spec asks
/// for a panel showing stacked background work, and the point of it is that long operations
/// are visible and stoppable rather than mysterious.
/// </remarks>
public sealed class TaskPanel : Border
{
    private readonly StackPanel _rows;
    private readonly ListTheme _theme;

    public TaskPanel(ListTheme theme)
    {
        _theme = theme;
        _rows = new StackPanel();

        Background = theme.Stripe;
        Padding = new Thickness(theme.LeftPadding, theme.RowPadding);
        Child = _rows;
        IsVisible = false;
    }

    /// <summary>Rebuilds from the current queue. Cheap: there are never many operations.</summary>
    public void Update(IReadOnlyList<OperationHandle> active)
    {
        ArgumentNullException.ThrowIfNull(active);

        _rows.Children.Clear();
        IsVisible = active.Count > 0;

        foreach (var handle in active)
        {
            _rows.Children.Add(BuildRow(handle));
        }
    }

    private Control BuildRow(OperationHandle handle)
    {
        var progress = handle.Progress;

        var label = new TextBlock
        {
            Text = Describe(handle),
            FontFamily = _theme.Typeface.FontFamily,
            FontSize = _theme.FontSize,
            Foreground = _theme.FileText,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var cancel = new TextBlock
        {
            Text = "cancel",
            FontFamily = _theme.Typeface.FontFamily,
            FontSize = _theme.FontSize,
            Foreground = _theme.BrokenText,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(_theme.ColumnGap, 0, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        cancel.PointerPressed += (_, _) => handle.Cancel();

        // A bar rather than a percentage: an operation whose total is not yet known still
        // has something honest to show, and the eye reads a bar faster mid-scroll.
        var bar = new Border
        {
            Height = 3,
            Background = _theme.CursorBackground,
            Margin = new Thickness(0, 2, 0, 0),
            Child = new Border
            {
                Background = _theme.CursorBar,
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(2, progress.Fraction * 400),
                Height = 3,
            },
        };

        var row = new DockPanel();
        DockPanel.SetDock(cancel, Dock.Right);
        row.Children.Add(cancel);
        row.Children.Add(label);

        return new StackPanel { Children = { row, bar } };
    }

    private static string Describe(OperationHandle handle)
    {
        var progress = handle.Progress;

        var position = progress.Total > 0
            ? $"  {progress.Current}/{progress.Total}"
            : string.Empty;

        var item = progress.Item is { Length: > 0 } name ? $"  {name}" : string.Empty;
        var state = handle.State == OperationState.Queued ? "  (queued)" : string.Empty;

        return handle.Description + position + item + state;
    }
}
