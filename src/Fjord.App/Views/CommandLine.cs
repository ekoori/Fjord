using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Fjord.Commands;

namespace Fjord.App.Views;

/// <summary>
/// The <c>:</c> command line, with history and completion.
/// </summary>
/// <remarks>
/// Owns its own <see cref="TextBox"/> and the completion popup below it. Key handling is
/// deliberately local rather than routed through the mode manager: once the box has focus
/// the user is typing text, and only a handful of keys (Enter, Tab, Up, Down, Escape) mean
/// anything else.
/// </remarks>
public sealed class CommandLine : Border
{
    private readonly TextBox _input;
    private readonly StackPanel _completions;
    private readonly Border _completionHost;
    private readonly CompletionEngine _engine;
    private readonly CommandHistory _history = new();
    private readonly ListTheme _theme;

    private CompletionResult _result = CompletionResult.Empty;
    private int _selected = -1;

    /// <summary>
    /// The line as it was when cycling began.
    /// </summary>
    /// <remarks>
    /// Every candidate is applied to this, never to the current text. The span in
    /// <see cref="CompletionResult"/> describes the original line, so applying the second
    /// candidate to the text the first one produced overwrites the wrong characters —
    /// ":cd s" cycled twice gives ":cd src/cripts/".
    /// </remarks>
    private string _cycleOrigin = "";

    /// <summary>Guards against reacting to text we set ourselves.</summary>
    private bool _settingText;

    public CommandLine(ListTheme theme, CompletionEngine engine)
    {
        _theme = theme;
        _engine = engine;

        _input = new TextBox
        {
            FontFamily = theme.Typeface.FontFamily,
            FontSize = theme.FontSize,
            Foreground = theme.FileText,
            CaretBrush = theme.CursorBar,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MinHeight = 0,
            VerticalContentAlignment = VerticalAlignment.Center,

            // Fluent draws a focus adorner over the border even at zero thickness, which
            // reads as a stray coloured box around the command line.
            FocusAdorner = null,
        };

        // A StackPanel with real children rather than an ItemsControl: the list is at most
        // a few dozen entries, and this leaves no doubt about how items are realised.
        _completions = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };

        _completionHost = new Border
        {
            Background = theme.Stripe,
            Padding = new Thickness(theme.LeftPadding, theme.RowPadding),
            Child = _completions,
            IsVisible = false,
        };

        Background = theme.Stripe;
        Padding = new Thickness(theme.LeftPadding, theme.RowPadding);
        Child = _input;
        IsVisible = false;

        SuppressFluentTextBoxChrome();

        _input.TextChanged += (_, _) =>
        {
            if (!_settingText)
            {
                _history.Reset();
                HideCompletions();
            }
        };

        _input.AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Stops Fluent painting its own background and accent border over the command line.
    /// </summary>
    /// <remarks>
    /// The theme's TextBox template has a <c>PART_BorderElement</c> that paints regardless
    /// of the control's own Background and BorderThickness, so a focused command line gets
    /// a bright accent box around it. Restyling that part is the only way to defeat it —
    /// QuteSharp needed the same override.
    /// </remarks>
    private void SuppressFluentTextBoxChrome()
    {
        foreach (var selector in new Func<Selector?, Selector>[]
                 {
                     x => x.OfType<TextBox>().Template().OfType<Border>().Name("PART_BorderElement"),
                     x => x.OfType<TextBox>().Class(":focus").Template()
                           .OfType<Border>().Name("PART_BorderElement"),
                     x => x.OfType<TextBox>().Class(":pointerover").Template()
                           .OfType<Border>().Name("PART_BorderElement"),
                 })
        {
            Styles.Add(new Style(selector)
            {
                Setters =
                {
                    new Setter(Border.BackgroundProperty, Brushes.Transparent),
                    new Setter(Border.BorderBrushProperty, Brushes.Transparent),
                    new Setter(Border.BorderThicknessProperty, new Thickness(0)),
                },
            });
        }
    }

    /// <summary>The popup listing candidates. Hosted by the window above the command line.</summary>
    public Control CompletionHost => _completionHost;

    /// <summary>Raised when the user accepted a line with Enter.</summary>
    public event EventHandler<string>? Accepted;

    /// <summary>Raised when the user dismissed the command line.</summary>
    public event EventHandler? Cancelled;

    public bool IsOpen => IsVisible;

    public void Open(string initial)
    {
        _settingText = true;
        _input.Text = initial;
        _settingText = false;

        IsVisible = true;
        _history.Reset();
        HideCompletions();

        _input.Focus();
        _input.CaretIndex = initial.Length;
    }

    public void Close()
    {
        IsVisible = false;
        HideCompletions();

        _settingText = true;
        _input.Text = string.Empty;
        _settingText = false;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                Cancelled?.Invoke(this, EventArgs.Empty);
                break;

            case Key.Enter:
                Accept();
                break;

            case Key.Tab:
                CycleCompletion(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                break;

            case Key.Up:
                Walk(back: true);
                break;

            case Key.Down:
                Walk(back: false);
                break;

            default:
                return;
        }

        // Tab would otherwise move focus, and Up/Down would move the caret.
        e.Handled = true;
    }

    private void Accept()
    {
        var line = _input.Text ?? string.Empty;
        _history.Add(line);
        Close();
        Accepted?.Invoke(this, line);
    }

    private void Walk(bool back)
    {
        var entry = back ? _history.Previous(_input.Text ?? string.Empty) : _history.Next();
        if (entry is null)
        {
            return;
        }

        SetText(entry, entry.Length);
    }

    /// <summary>
    /// Moves through the candidates, applying each as it is highlighted.
    /// </summary>
    /// <remarks>
    /// The result is captured once when cycling starts and reused, because applying a
    /// candidate rewrites the line — recomputing would complete against the text just
    /// inserted and the list would collapse to one entry after the first Tab.
    /// </remarks>
    private void CycleCompletion(int direction)
    {
        if (_selected < 0)
        {
            _cycleOrigin = _input.Text ?? string.Empty;
            _result = _engine.Complete(_cycleOrigin, _input.CaretIndex);
            if (_result.IsEmpty)
            {
                return;
            }

            ShowCompletions();
            _selected = direction > 0 ? 0 : _result.Items.Count - 1;
        }
        else
        {
            _selected = (_selected + direction + _result.Items.Count) % _result.Items.Count;
        }

        Highlight();

        var (text, caret) = _result.Apply(_cycleOrigin, _result.Items[_selected]);
        SetText(text, caret);
    }

    private void SetText(string text, int caret)
    {
        // Without the guard, TextChanged would treat this as typing and drop the very
        // completion state that produced it.
        _settingText = true;
        _input.Text = text;
        _input.CaretIndex = Math.Clamp(caret, 0, text.Length);
        _settingText = false;
    }

    private void ShowCompletions()
    {
        _completions.Children.Clear();

        foreach (var item in _result.Items)
        {
            _completions.Children.Add(new TextBlock
            {
                Text = item.Description.Length > 0 ? $"{item.Text}    {item.Description}" : item.Text,
                FontFamily = _theme.Typeface.FontFamily,
                FontSize = _theme.FontSize,
                Foreground = _theme.MutedText,
                Padding = new Thickness(2, 1),
            });
        }

        _completionHost.IsVisible = true;
    }

    private void Highlight()
    {
        for (var i = 0; i < _completions.Children.Count; i++)
        {
            if (_completions.Children[i] is not TextBlock block)
            {
                continue;
            }

            block.Foreground = i == _selected ? _theme.FileText : _theme.MutedText;
            block.Background = i == _selected ? _theme.SelectedBackground : Brushes.Transparent;
        }
    }

    private void HideCompletions()
    {
        _completionHost.IsVisible = false;
        _completions.Children.Clear();
        _result = CompletionResult.Empty;
        _selected = -1;
    }
}
