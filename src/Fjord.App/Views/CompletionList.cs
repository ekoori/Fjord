using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Fjord.Commands;

namespace Fjord.App.Views;

/// <summary>
/// The list of candidates above the command line.
/// </summary>
/// <remarks>
/// Its own control rather than a StackPanel inlined into the command line, because it has
/// real behaviour: a selection that moves, groups with headings, a viewport that scrolls to
/// follow the selection, and rows you can click. The first version was a flat stack of
/// TextBlocks with no selection of its own, which is why picking something meant cycling
/// through every candidate with Tab and watching the line rewrite itself each time.
/// <para>
/// Built from ordinary controls rather than drawn immediately like the file list. There are
/// at most a few dozen rows and they change on a keystroke, not on a scroll, so none of the
/// reasons the file list is drawn by hand apply here.
/// </para>
/// </remarks>
public sealed class CompletionList : Border
{
    /// <summary>
    /// Rows shown before the list scrolls.
    /// </summary>
    /// <remarks>
    /// Twelve is about a third of a screen. Enough to recognise what you want without the
    /// popup covering the listing you are choosing against.
    /// </remarks>
    private const int VisibleRows = 12;

    private readonly StackPanel _rows;
    private readonly ScrollViewer _scroller;
    private readonly ListTheme _theme;
    private readonly List<Control> _itemControls = [];

    private IReadOnlyList<CompletionItem> _items = [];
    private int _selected = -1;

    public CompletionList(ListTheme theme)
    {
        _theme = theme;
        _rows = new StackPanel();

        _scroller = new ScrollViewer
        {
            Content = _rows,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        Background = theme.Stripe;
        Padding = new Thickness(0, theme.RowPadding);
        Child = _scroller;
        IsVisible = false;
    }

    /// <summary>Raised when a row was clicked.</summary>
    public event EventHandler<CompletionItem>? Chosen;

    public bool IsOpen => IsVisible;

    public IReadOnlyList<CompletionItem> Items => _items;

    /// <summary>The highlighted candidate, or null when nothing is selected yet.</summary>
    public CompletionItem? Selected =>
        _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;

    /// <summary>
    /// Shows a set of candidates, selecting nothing.
    /// </summary>
    /// <remarks>
    /// Nothing selected on purpose. A list that pre-selects its first row makes Enter mean
    /// "accept a suggestion I did not ask for", which is the wrong default when the user
    /// may simply be typing a path the list happens to be guessing at.
    /// </remarks>
    public void Show(IReadOnlyList<CompletionItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        _items = items;
        _selected = -1;

        Rebuild();
        IsVisible = items.Count > 0;
    }

    public void Hide()
    {
        IsVisible = false;
        _items = [];
        _selected = -1;
        _rows.Children.Clear();
        _itemControls.Clear();
    }

    /// <summary>
    /// Moves the selection, wrapping.
    /// </summary>
    /// <returns>The newly selected candidate.</returns>
    public CompletionItem? Move(int delta)
    {
        if (_items.Count == 0)
        {
            return null;
        }

        _selected = _selected < 0
            ? (delta > 0 ? 0 : _items.Count - 1)
            : (_selected + delta + _items.Count) % _items.Count;

        Highlight();
        ScrollSelectedIntoView();
        return Selected;
    }

    public void RefreshTheme()
    {
        Background = _theme.Stripe;
        Rebuild();
    }

    private void Rebuild()
    {
        _rows.Children.Clear();
        _itemControls.Clear();

        var group = string.Empty;

        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];

            if (!string.Equals(item.Group, group, StringComparison.Ordinal))
            {
                group = item.Group;

                if (group.Length > 0)
                {
                    _rows.Children.Add(Heading(group));
                }
            }

            var index = i;
            var row = Row(item);
            row.PointerPressed += (_, _) => Chosen?.Invoke(this, _items[index]);

            _itemControls.Add(row);
            _rows.Children.Add(row);
        }

        Highlight();
    }

    private Control Heading(string text) => new TextBlock
    {
        Text = text,
        FontFamily = _theme.Typeface.FontFamily,
        FontSize = _theme.FontSize * 0.85,
        Foreground = _theme.CursorBar,
        Padding = new Thickness(_theme.LeftPadding, 3, 0, 1),
    };

    /// <remarks>
    /// Two columns, not one string. The description is what tells a quickmark from a
    /// directory of the same name, and it has to stay readable when the name beside it is
    /// long — which a single padded string does not manage.
    /// </remarks>
    private Control Row(CompletionItem item)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(_theme.LeftPadding, 0, _theme.LeftPadding, 0),
        };

        var name = new TextBlock
        {
            Text = item.Text,
            FontFamily = _theme.Typeface.FontFamily,
            FontSize = _theme.FontSize,
            Foreground = _theme.FileText,
            Padding = new Thickness(0, 2),
        };

        var description = new TextBlock
        {
            Text = item.Description,
            FontFamily = _theme.Typeface.FontFamily,
            FontSize = _theme.FontSize,
            Foreground = _theme.MutedText,
            Margin = new Thickness(_theme.ColumnGap, 0, 0, 0),
            Padding = new Thickness(0, 2),
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        Grid.SetColumn(description, 1);
        grid.Children.Add(name);
        grid.Children.Add(description);

        return new Border { Child = grid, Background = Brushes.Transparent };
    }

    private void Highlight()
    {
        for (var i = 0; i < _itemControls.Count; i++)
        {
            if (_itemControls[i] is Border row)
            {
                row.Background = i == _selected ? _theme.SelectedBackground : Brushes.Transparent;
            }
        }

        // Sized to the rows it has, up to the cap. Without this the popup is as tall as
        // its content and a directory of two hundred entries covers the whole window.
        _scroller.MaxHeight = _theme.RowHeight * VisibleRows;
    }

    private void ScrollSelectedIntoView()
    {
        if (_selected >= 0 && _selected < _itemControls.Count)
        {
            _itemControls[_selected].BringIntoView();
        }
    }
}
