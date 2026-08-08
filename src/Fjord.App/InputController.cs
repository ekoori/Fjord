using Avalonia.Input;
using Fjord.App.Input;
using Fjord.Commands;
using Fjord.Core.Diagnostics;
using Fjord.Core.Input;
using Fjord.Core.Navigation;
using Fjord.Core.Panes;

namespace Fjord.App;

/// <summary>
/// Turns key events into command invocations.
/// </summary>
/// <remarks>
/// The join between the three layers: <see cref="AvaloniaKeys"/> translates the event,
/// <see cref="ModeManager"/> decides what it means, and <see cref="CommandParser"/> runs
/// it. Everything modal lives in Core; this class only marshals.
/// </remarks>
public sealed class InputController : IModeHost
{
    private readonly ModeManager _modes;
    private readonly KeyModeRegistry _modeRegistry;
    private readonly CommandParser _parser;
    private readonly OperatorDispatcher _operators;
    private readonly IMessageSink _messages;
    private readonly Func<IPane?> _activePane;
    private readonly Func<IWorkspace?> _workspace;

    // What the next keystroke names, once a command has asked for one. Null the rest of
    // the time, which is what makes an ordinary key an ordinary key.
    private NameTarget? _awaiting;

    // What a yes answer would do, while a question is on screen.
    private Action? _pendingAnswer;

    public InputController(
        ModeManager modes,
        KeyModeRegistry modeRegistry,
        CommandParser parser,
        OperatorDispatcher operators,
        IMessageSink messages,
        Func<IPane?> activePane,
        Func<IWorkspace?> workspace)
    {
        _modes = modes;
        _modeRegistry = modeRegistry;
        _parser = parser;
        _operators = operators;
        _messages = messages;
        _activePane = activePane;
        _workspace = workspace;
    }

    /// <summary>Raised after a key was handled, so the status bar can refresh.</summary>
    public event EventHandler? Handled;

    public ModeManager Modes => _modes;

    /// <summary>
    /// Handles one key event.
    /// </summary>
    /// <returns>True when the key was consumed and must not reach the focused control.</returns>
    public bool HandleKey(KeyEventArgs e)
    {
        var pane = _activePane();

        // "The next keystroke is data" modes read the raw key rather than matching it
        // against bindings, so they are resolved before the mode manager sees anything.
        if (_awaiting is { } target)
        {
            return TakeName(target, e);
        }

        if (_pendingAnswer is not null)
        {
            return Answer(e);
        }

        if (AvaloniaKeys.Translate(e) is not { } key)
        {
            return false;
        }

        // Escape is not a binding: it has to work from any mode, including ones where an
        // unmatched key would otherwise be forwarded to a text field.
        if (key.Key == Core.Keys.Key.Escape && _modes.Mode != KeyMode.Normal)
        {
            LeaveVisual(pane);
            _modes.Reset();
            Handled?.Invoke(this, EventArgs.Empty);
            return true;
        }

        // In normal mode Escape clears the selection, which is useless in a scrapbook and
        // leaves the user with no way out of one. Getting out wins where both apply.
        if (key.Key == Core.Keys.Key.Escape && pane is { IsVirtual: true })
        {
            Observe(pane.LeaveVirtualAsync());
            Handled?.Invoke(this, EventArgs.Empty);
            return true;
        }

        var result = _modes.HandleKey(key);

        if (result.HasOperator || result.HasCommand)
        {
            Run(result, pane);
        }

        Handled?.Invoke(this, EventArgs.Empty);
        return result.Disposition == KeyDisposition.Consumed;
    }

    /// <summary>
    /// Switches between normal and scrapbook mode as the pane's contents change.
    /// </summary>
    /// <remarks>
    /// Driven by what the pane is showing rather than by a key, because the mode is a fact
    /// about the listing and not a state the user enters. Left alone while the user is in
    /// the middle of something modal — visual, operator-pending, a prompt — since a
    /// navigation completing underneath should not cancel a half-typed operator.
    /// </remarks>
    public void SyncModeToPane(IPane? pane)
    {
        if (_modes.Mode != KeyMode.Normal && _modes.Mode != KeyMode.Scrapbook)
        {
            return;
        }

        var wanted = pane is { IsVirtual: true } ? KeyMode.Scrapbook : KeyMode.Normal;

        if (_modes.Mode != wanted)
        {
            _modes.EnterMode(wanted);
        }
    }

    /// <summary>Enters or leaves visual mode, anchoring the selection at the cursor.</summary>
    public void ToggleVisual(IPane pane)
    {
        ArgumentNullException.ThrowIfNull(pane);

        if (_modeRegistry.IsSelectionMode(_modes.Mode))
        {
            LeaveVisual(pane);
            _modes.EnterMode(KeyMode.Normal);
        }
        else
        {
            pane.VisualAnchor = pane.CursorIndex;
            _modes.EnterMode(KeyMode.Visual);
        }
    }

    /// <summary>Returns to normal mode, abandoning any pending operator or register.</summary>
    public void LeaveMode(IPane pane)
    {
        _awaiting = null;
        _pendingAnswer = null;
        LeaveVisual(pane);
        _modes.Reset();

        // Reset lands in normal mode, which is wrong when the pane is still a scrapbook.
        SyncModeToPane(pane);
    }

    /// <summary>Opens the command line. The window supplies the handler.</summary>
    public void EnterCommandLine(string initial) => CommandLineRequested?.Invoke(this, initial);

    /// <summary>Raised when a command asked for the command line to open.</summary>
    public event EventHandler<string>? CommandLineRequested;

    /// <summary>
    /// Returns to whatever mode the pane implies, after the command line closes.
    /// </summary>
    /// <remarks>
    /// The command line is a mode of its own while it has focus, which is what makes the
    /// status bar say so and what stops a key meaning two things at once. Leaving it has to
    /// land in scrapbook mode rather than normal when that is what is showing.
    /// </remarks>
    public void LeaveCommandLine(IPane? pane)
    {
        if (_modes.Mode == KeyMode.Command)
        {
            _modes.EnterMode(KeyMode.Normal);
        }

        SyncModeToPane(pane);
    }

    /// <summary>
    /// Runs a line typed into the command line.
    /// </summary>
    /// <remarks>
    /// Separate from key handling: there is no mode manager involved, the text is the
    /// command. Failures report rather than throw, since the user is mid-conversation with
    /// the app.
    /// </remarks>
    public void Execute(string line)
    {
        var pane = _activePane();

        try
        {
            var context = new CommandContext
            {
                Pane = pane,
                Workspace = _workspace(),
                Messages = _messages,
            };

            foreach (var command in _parser.Parse(line, context))
            {
                Observe(command.Invoke());
            }
        }
        catch (CommandException ex)
        {
            _messages.Error(ex.Message);
        }

        Handled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Arranges for the next keystroke to be read as a name.
    /// </summary>
    /// <remarks>
    /// Called from a command, so <c>"</c> is an ordinary binding the user can move rather
    /// than a character wired into the key handler.
    /// </remarks>
    public void ReadName(NameTarget target)
    {
        _awaiting = target;
        _modes.EnterMode(KeyMode.Register);
    }

    /// <summary>
    /// Puts a question in the status bar and waits for one keystroke.
    /// </summary>
    /// <remarks>
    /// A second question replaces the first rather than queueing behind it. Two prompts
    /// deep is a state nobody can reason about from a single status line.
    /// </remarks>
    public void Ask(string question, Action confirmed)
    {
        ArgumentNullException.ThrowIfNull(confirmed);

        _pendingAnswer = confirmed;
        _modes.EnterMode(KeyMode.YesNo);
        _messages.Info($"{question} [y/N]");
        Handled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Resolves an outstanding question.
    /// </summary>
    /// <remarks>
    /// Only y and Enter agree. Everything else declines, including keys that would
    /// otherwise be bindings — a question on screen owns the keyboard until it is answered,
    /// or a mistyped j would leave one hanging with no sign of it.
    /// </remarks>
    private bool Answer(KeyEventArgs e)
    {
        var answer = _pendingAnswer!;
        _pendingAnswer = null;
        _modes.EnterMode(KeyMode.Normal);

        var key = AvaloniaKeys.Translate(e);
        var yes = key is { Key: Core.Keys.Key.Y or Core.Keys.Key.Return or Core.Keys.Key.Enter };

        if (yes)
        {
            answer();
        }
        else
        {
            _messages.Info("Cancelled.");
        }

        Handled?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Directories remembered by name with <c>:mark-set</c>.</summary>
    /// <remarks>Supplied by the window, which shares the same instance with <c>:cd</c>.</remarks>
    public MarkFile Marks { get; init; } = new();

    /// <summary>Toggles the render-time overlay. The window supplies the handler.</summary>
    public bool ToggleFrameStats()
    {
        FrameStatsToggled?.Invoke(this, EventArgs.Empty);
        return FrameStatsEnabled?.Invoke() ?? false;
    }

    /// <summary>Raised when a command asked to toggle the frame-stats overlay.</summary>
    public event EventHandler? FrameStatsToggled;

    /// <summary>Reports whether the overlay is currently on.</summary>
    public Func<bool>? FrameStatsEnabled { get; set; }

    public void Quit()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    /// <summary>
    /// Consumes the keystroke a command asked to read as a name.
    /// </summary>
    /// <remarks>
    /// Escape and any non-character key abandon the wait rather than naming something: a
    /// half-typed <c>"</c> must be cancellable, and a register bound to Home would be one
    /// nobody could name again.
    /// </remarks>
    private bool TakeName(NameTarget target, KeyEventArgs e)
    {
        _awaiting = null;
        _modes.EnterMode(KeyMode.Normal);

        if (target == NameTarget.Register
            && AvaloniaKeys.TextOf(e) is { Length: 1 } text
            && !char.IsControl(text[0]))
        {
            _modes.SetRegister(text[0]);
        }

        Handled?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void Run(KeyResult result, IPane? pane)
    {
        if (pane is null)
        {
            return;
        }

        try
        {
            if (result.Operator is { } op)
            {
                RunOperator(op, result, pane);
                return;
            }

            // A binding starting with ':' is a starting point, not an action: it opens the
            // command line holding that text for the user to finish or confirm.
            if (CommandTemplate.IsCommandLine(result.Command!))
            {
                // Whether the placeholders are quoted depends on the command they are being
                // written into, and only the registry knows which take free text.
                var name = result.Command![1..].Split(' ')[0];
                var quoting = _parser.Registry.Find(name)?.MaxSplit < 0;

                EnterCommandLine(CommandTemplate.Expand(result.Command!, pane, quoting));
                return;
            }

            var parsed = _parser.Parse(result.Command!, new CommandContext
            {
                Count = result.Count,
                Pane = pane,
                Workspace = _workspace(),
                Register = result.Register ?? '"',
                Messages = _messages,
            });

            foreach (var command in parsed)
            {
                Observe(command.Invoke());
            }
        }
        catch (CommandException ex)
        {
            _messages.Error(ex.Message);
        }
    }

    private void RunOperator(string op, KeyResult result, IPane pane)
    {
        var register = result.Register ?? '"';

        // A null Command means the range already exists: the operator was typed in visual
        // mode rather than followed by a motion.
        if (result.Command is null)
        {
            if (pane.VisualRange is { } range)
            {
                _operators.ExecuteOverRange(op, range, register, pane, _messages);
                LeaveVisual(pane);
                _modes.EnterMode(KeyMode.Normal);
            }

            return;
        }

        _operators.Execute(op, result.Command, result.Count, register, pane, _messages);
    }

    private static void LeaveVisual(IPane? pane)
    {
        if (pane is not null)
        {
            pane.VisualAnchor = null;
        }
    }

    /// <summary>
    /// Surfaces a failure from a command that returned a task.
    /// </summary>
    /// <remarks>
    /// Navigation commands are async. Dropping the task would swallow a permission error
    /// silently and leave the pane looking merely empty.
    /// </remarks>
    private void Observe(object? result)
    {
        if (result is not Task task)
        {
            return;
        }

        _ = task.ContinueWith(
            t => _messages.Error(t.Exception!.GetBaseException().Message),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.FromCurrentSynchronizationContext());
    }
}
