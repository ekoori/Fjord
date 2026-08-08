using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Fjord.App.Views;

/// <summary>
/// What could follow a half-typed key sequence, as a row of chips.
/// </summary>
/// <remarks>
/// Chips in a wrapping panel, not a list. The first version of this was a full-height
/// vertical list and it took a third of the window to say what four keys did — which made
/// it feel like an error rather than a hint. noPDF's which-key is a wrap panel of small
/// chips and takes one or two lines; that is the right shape for something that appears
/// while you are mid-keystroke.
/// </remarks>
public sealed class KeyHints : Border
{
    /// <summary>
    /// Chips shown before the list is cut short.
    /// </summary>
    /// <remarks>
    /// A mode with sixty bindings would otherwise cover the window at exactly the moment
    /// the user is trying to see it. Past this many, the answer is <c>:help</c>.
    /// </remarks>
    private const int MaxChips = 24;

    private readonly WrapPanel _chips;
    private readonly ListTheme _theme;

    public KeyHints(ListTheme theme)
    {
        _theme = theme;
        _chips = new WrapPanel { Orientation = Orientation.Horizontal };

        Background = theme.Stripe;
        Padding = new Thickness(theme.LeftPadding, theme.RowPadding);
        Child = _chips;
        IsVisible = false;
    }

    /// <summary>Shows the continuations, or hides when there are none.</summary>
    public void Show(IReadOnlyList<(string Keys, string Command)> continuations)
    {
        ArgumentNullException.ThrowIfNull(continuations);

        _chips.Children.Clear();

        foreach (var (keys, command) in continuations.Take(MaxChips))
        {
            _chips.Children.Add(Chip(keys, command));
        }

        if (continuations.Count > MaxChips)
        {
            _chips.Children.Add(Chip("…", $"{continuations.Count - MaxChips} more"));
        }

        IsVisible = continuations.Count > 0;
    }

    public void Hide()
    {
        IsVisible = false;
        _chips.Children.Clear();
    }

    public void RefreshTheme()
    {
        Background = _theme.Stripe;
        Padding = new Thickness(_theme.LeftPadding, _theme.RowPadding);
    }

    /// <remarks>
    /// The key is the part being scanned for, so it takes the readable colour and the
    /// command sits beside it muted. Reversed, the eye has to read every chip in full.
    /// </remarks>
    private Control Chip(string keys, string command)
    {
        var text = new StackPanel { Orientation = Orientation.Horizontal };

        text.Children.Add(new TextBlock
        {
            Text = keys,
            FontFamily = _theme.Typeface.FontFamily,
            FontSize = _theme.FontSize,
            Foreground = _theme.CursorBar,
        });

        text.Children.Add(new TextBlock
        {
            Text = "  " + command,
            FontFamily = _theme.Typeface.FontFamily,
            FontSize = _theme.FontSize,
            Foreground = _theme.MutedText,
        });

        return new Border
        {
            Background = _theme.CursorBackground,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(7, 2),
            Margin = new Thickness(0, 0, 6, 3),
            Child = text,
        };
    }
}
