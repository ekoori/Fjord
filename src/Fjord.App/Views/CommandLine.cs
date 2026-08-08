using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
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

    /// <summary>
    /// History rows shown at once.
    /// </summary>
    /// <remarks>
    /// Ten, and the rest reachable by holding Up. A panel tall enough for a hundred
    /// commands is a panel that covers the listing you are running them against.
    /// </remarks>
    private const int HistoryRows = 10;

    private CompletionResult _result = CompletionResult.Empty;

    // Whether the panel is showing candidates rather than history, which is what decides
    // whether Up and Down select or walk.
    private bool _showingCandidates;
    private bool _historyLoaded;

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

    /// <summary>The panel above the line: history, or candidates when there are any.</summary>
    public Control CompletionHost => _completions;

    /// <summary>Raised when the user accepted a line with Enter.</summary>
    public event EventHandler<string>? Accepted;

    /// <summary>Raised when the user dismissed the command line.</summary>
    public event EventHandler? Cancelled;

    /// <summary>Raised as the line changes, with what the command being typed does.</summary>
    /// <remarks>
    /// The hint goes to the status bar rather than into a panel of its own. A line of text
    /// where messages already appear is somewhere the eye is; a third floating panel was
    /// one more thing to dismiss.
    /// </remarks>
    public event EventHandler<string>? UsageChanged;

    /// <summary>Asked what a command does, for the usage hint. Supplied by the window.</summary>
    public Func<string, string?>? Usage { get; set; }

    /// <summary>Loads and stores the history across runs. Supplied by the window.</summary>
    public Func<IReadOnlyList<string>>? LoadHistory { get; set; }

    /// <summary>Persists the history. Supplied by the window.</summary>
    public Action<IReadOnlyList<string>>? SaveHistory { get; set; }

    public bool IsOpen => IsVisible;

    /// <summary>
    /// Opens the command line holding <paramref name="initial"/>.
    /// </summary>
    /// <remarks>
    /// The panel is populated straight away — with candidates when the text asks a question
    /// and with history otherwise. Opening onto a blank bar and a blank panel would make
    /// the history a feature you had to already know about.
    /// </remarks>
    public void Open(string initial)
    {
        ArgumentNullException.ThrowIfNull(initial);

        if (!_historyLoaded)
        {
            _historyLoaded = true;
            _history.Load(LoadHistory?.Invoke() ?? []);
        }

        SetText(initial, initial.Length);

        IsVisible = true;
        _history.Reset();
        Refresh();

        FocusInput();
    }

    /// <summary>Whether the text box actually has the keyboard.</summary>
    /// <remarks>
    /// The window asks before handing keys over. Open-but-not-focused is a state that
    /// swallows every key: the window defers to the command line, and the command line
    /// never sees anything.
    /// </remarks>
    public bool HasKeyboard => _input.IsFocused;

    /// <summary>
    /// Puts the keyboard in the text box, retrying after layout.
    /// </summary>
    /// <remarks>
    /// Focus fails on a control that has only just become visible, because it is not yet
    /// realised. The first attempt covers the common case, and the posted one covers the
    /// open that follows a layout change — showing the candidate panel is one. Without the
    /// retry the bar could open with nothing focused and eat every key until the process
    /// was killed.
    /// </remarks>
    public void FocusInput()
    {
        if (!IsVisible)
        {
            return;
        }

        _input.Focus();

        if (!_input.IsFocused)
        {
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (IsVisible)
                    {
                        _input.Focus();
                    }
                },
                DispatcherPriority.Input);
        }
    }

    public void Close()
    {
        IsVisible = false;
        _completions.Hide();
        _result = CompletionResult.Empty;

        SetText(string.Empty, 0);
        UsageChanged?.Invoke(this, string.Empty);
    }

    public void RefreshTheme() => _completions.RefreshTheme();

    /// <remarks>
    /// Up and Down mean two things and the panel decides which: with candidates showing
    /// they move the selection, and otherwise they walk the history. That is how
    /// qutebrowser and noPDF both resolve the collision, and it needs no third key.
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

            case Key.Up when _showingCandidates:
                MoveSelection(-1);
                break;

            case Key.Down when _showingCandidates:
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
    /// one. Nothing is selected until the user moves, so a line typed in full still runs on
    /// the first press.
    /// </remarks>
    private void Accept()
    {
        if (_showingCandidates && _completions.Selected is { } candidate)
        {
            Apply(candidate);
            Refresh();
            return;
        }

        var line = _input.Text ?? string.Empty;
        _history.Add(line);
        SaveHistory?.Invoke(_history.Entries);

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
        ShowHistory();
        Hint();
    }

    private void MoveSelection(int delta)
    {
        if (!_showingCandidates)
        {
            return;
        }

        if (_completions.Move(delta) is { } candidate)
        {
            // Applied as it is highlighted, so the line always shows what Enter would run.
            var (text, caret) = _result.Apply(_origin, candidate);
            SetText(text, caret);
        }
    }

    /// <summary>
    /// Recomputes what the panel shows for the line as it stands.
    /// </summary>
    /// <remarks>
    /// Candidates when the word under the caret has any, history otherwise. One panel doing
    /// both rather than two competing for the same space: they answer the same question —
    /// "what could this line be" — from different sources.
    /// </remarks>
    private void Refresh()
    {
        _origin = _input.Text ?? string.Empty;
        _result = _engine.Complete(_origin, _input.CaretIndex);

        // An empty line matches every command, and sixty of them is a wall of text rather
        // than a suggestion. Until something has been typed the useful answer is what you
        // ran last, so history holds the panel and candidates take over on the first
        // keystroke.
        if (!_result.IsEmpty && !IsBare(_origin))
        {
            _showingCandidates = true;
            _completions.Show(_result.Items);
        }
        else
        {
            ShowHistory();
        }

        Hint();
    }

    /// <summary>Whether the line is still just its prompt, with nothing typed after it.</summary>
    private static bool IsBare(string line) => line.TrimStart().TrimStart(':', '/').Trim().Length == 0;

    /// <summary>
    /// Shows a window onto the history, centred on wherever the walk has reached.
    /// </summary>
    /// <remarks>
    /// A window rather than the lot: the panel is a dozen rows, and a hundred entries would
    /// bury the command line. Centring on the current entry is what makes Up keep moving
    /// through history that is longer than the panel.
    /// </remarks>
    private void ShowHistory()
    {
        _showingCandidates = false;

        var entries = _history.Entries;

        if (entries.Count == 0)
        {
            _completions.Hide();
            return;
        }

        var current = _history.CurrentIndex;
        var count = Math.Min(HistoryRows, entries.Count);

        var start = current is { } at
            ? Math.Clamp(at - (count / 2), 0, Math.Max(0, entries.Count - count))
            : Math.Max(0, entries.Count - count);

        var window = new List<CompletionItem>(count);

        for (var i = start; i < start + count && i < entries.Count; i++)
        {
            window.Add(new CompletionItem(entries[i], string.Empty, "history"));
        }

        _completions.Show(window);

        if (current is { } selected && selected >= start)
        {
            _completions.Select(selected - start);
        }
    }

    /// <summary>Tells the status bar what the command being typed does.</summary>
    private void Hint()
    {
        if (Usage is not { } describe)
        {
            return;
        }

        var line = (_input.Text ?? string.Empty).TrimStart().TrimStart(':');
        var space = line.IndexOf(' ', StringComparison.Ordinal);
        var name = space < 0 ? line : line[..space];

        UsageChanged?.Invoke(this, name.Length == 0 ? string.Empty : describe(name) ?? string.Empty);
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
