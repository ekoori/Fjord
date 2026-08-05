using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Fjord.Core.Diagnostics;

namespace Fjord.App.Views;

/// <summary>
/// A scrollable page of text over the file list.
/// </summary>
/// <remarks>
/// A plain <see cref="TextBlock"/> in a <see cref="ScrollViewer"/> rather than the
/// immediate-mode treatment the file list gets: help is a few hundred lines read once,
/// not a hundred thousand rows scrolled at 60 fps, and the standard control brings text
/// selection and keyboard scrolling for free.
/// </remarks>
public sealed class HelpOverlay : Border, IHelpView
{
    private readonly TextBlock _title;
    private readonly TextBlock _body;
    private readonly ScrollViewer _scroller;
    private readonly ListTheme _theme;

    public HelpOverlay(ListTheme theme)
    {
        _theme = theme;

        _title = new TextBlock
        {
            FontFamily = theme.Typeface.FontFamily,
            FontSize = theme.FontSize + 2,
            FontWeight = FontWeight.SemiBold,
            Foreground = theme.CursorBar,
            Margin = new Thickness(0, 0, 0, theme.RowPadding * 2),
        };

        _body = new TextBlock
        {
            FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace"),
            FontSize = theme.FontSize,
            Foreground = theme.FileText,

            // The overview lines its columns up with spaces, so it needs a monospace face
            // and must not be re-wrapped.
            TextWrapping = TextWrapping.NoWrap,
        };

        _scroller = new ScrollViewer
        {
            Content = _body,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        Background = theme.Background;
        Padding = new Thickness(theme.LeftPadding * 2, theme.RowPadding * 2);
        IsVisible = false;

        Child = new DockPanel
        {
            Children = { Docked(_title, Dock.Top), _scroller },
        };
    }

    public new bool IsVisible
    {
        get => base.IsVisible;
        private set => base.IsVisible = value;
    }

    public void Show(string title, string content)
    {
        _title.Text = title;
        _body.Text = content;
        IsVisible = true;

        // Always from the top: reopening help and landing mid-page is disorienting.
        _scroller.Offset = default;
    }

    public void Hide() => IsVisible = false;

    /// <summary>Re-applies colours and fonts after a config reload.</summary>
    public void RefreshTheme()
    {
        Background = _theme.Background;
        _title.FontFamily = _theme.Typeface.FontFamily;
        _title.FontSize = _theme.FontSize + 2;
        _title.Foreground = _theme.CursorBar;
        _body.FontSize = _theme.FontSize;
        _body.Foreground = _theme.FileText;
    }

    private static Control Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }
}
