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
    private readonly CompletionList _completions;
    private readonly CompletionEngine _engine;
    private readonly CommandHistory _history = new();

    private CompletionResult _result = CompletionResult.Empty;

    /// <summary>
    /// The line as it was before a candidate was applied.
    /// </summary>
    /// <remarks>
    /// Every candidate is applied to this, never to the current text. The span in
    /// <see cref="CompletionResult"/> describes the original line, so applying the second
    /// candidate to the text the first one produced overwrites the wrong characters —
    /// ":cd s" cycled twice gives ":cd src/cripts/".
    /// </remarks>
    private string _origin = "";

    /// <summary>Guards against reacting to text we set ourselves.</summary>
    private bool _settingText;

    public CommandLine(ListTheme theme, CompletionEngine engine)
    {
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

        _completions = new CompletionList(theme);
        _completions.Chosen += (_, item) =>
        {
            Apply(item);
            Refresh();
            _input.Focus();
        };

        Background = theme.Stripe;
        Padding = new Thickness(theme.LeftPadding, theme.RowPadding);
        Child = _input;
        IsVisible = false;

        SuppressFluentTextBoxChrome();

        _input.TextChanged += (_, _) =>
        {
            if (_settingText)
            {
                return;
            }

            _history.Reset();
            Refresh();
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
    public Control CompletionHost => _completions;

    /// <summary>Raised when the user accepted a line with Enter.</summary>
    public event EventHandler<string>? Accepted;

    /// <summary>Raised when the user dismissed the command line.</summary>
    public event EventHandler? Cancelled;

    public bool IsOpen => IsVisible;

    /// <summary>
    /// Opens the command line holding <paramref name="initial"/>.
    /// </summary>
    /// <remarks>
    /// Candidates appear immediately. A binding that opens <c>:cd </c> is asking a
    /// question, and the answers should be on screen rather than behind a Tab the user has
    /// to know to press.
    /// </remarks>
    public void Open(string initial)
    {
        ArgumentNullException.ThrowIfNull(initial);

        SetText(initial, initial.Length);

        IsVisible = true;
        _history.Reset();

        _input.Focus();
        Refresh();
    }

    public void Close()
    {
        IsVisible = false;
        _completions.Hide();
        _result = CompletionResult.Empty;

        SetText(string.Empty, 0);
    }

    public void RefreshTheme() => _completions.RefreshTheme();

    /// <remarks>
    /// Up and Down mean two things depending on what is on screen, and the list decides:
    /// with candidates showing they move the selection, and without them they walk the
    /// command history. That is how qutebrowser resolves the same collision, and it needs
    /// no third key.
    /// </remarks>
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
                MoveSelection(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                break;

            case Key.Up when _completions.IsOpen:
                MoveSelection(-1);
                break;

            case Key.Down when _completions.IsOpen:
                MoveSelection(1);
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

    /// <remarks>
    /// Enter takes the highlighted candidate rather than running the line, when there is
    /// one. Two presses of Enter therefore mean "this one, then go", which is what every
    /// list with a text box above it does — and nothing is selected until the user moves,
    /// so a line typed in full still runs on the first press.
    /// </remarks>
    private void Accept()
    {
        if (_completions.Selected is { } candidate)
        {
            Apply(candidate);
            Refresh();
            return;
        }

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
        Refresh();
    }

    private void MoveSelection(int delta)
    {
        if (!_completions.IsOpen)
        {
            return;
        }

        if (_completions.Move(delta) is { } candidate)
        {
            // Applied as it is highlighted, so the line always shows what Enter would run.
            // The origin is not updated, so moving on to the next candidate replaces this
            // one rather than appending to it.
            var (text, caret) = _result.Apply(_origin, candidate);
            SetText(text, caret);
        }
    }

    /// <summary>
    /// Recomputes the candidates for the line as it stands.
    /// </summary>
    /// <remarks>
    /// On every keystroke, which is what makes the list narrow as you type rather than
    /// being something you summon. The cost is one directory enumeration per character
    /// while completing a path; the same enumeration Tab always did, and the engine is
    /// asked only for the word the caret is in.
    /// </remarks>
    private void Refresh()
    {
        _origin = _input.Text ?? string.Empty;
        _result = _engine.Complete(_origin, _input.CaretIndex);

        if (_result.IsEmpty)
        {
            _completions.Hide();
            return;
        }

        _completions.Show(_result.Items);
    }

    private void Apply(CompletionItem item)
    {
        var (text, caret) = _result.Apply(_origin, item);
        SetText(text, caret);
    }

    private void SetText(string text, int caret)
    {
        // Without the guard, TextChanged would treat this as typing and recompute against
        // text the user did not enter, which for an applied candidate means completing the
        // completion.
        _settingText = true;
        _input.Text = text;
        _input.CaretIndex = Math.Clamp(caret, 0, text.Length);
        _settingText = false;
    }
}
