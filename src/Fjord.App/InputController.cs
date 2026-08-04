using Avalonia.Input;
using Fjord.App.Input;
using Fjord.Commands;
using Fjord.Core.Diagnostics;
using Fjord.Core.Input;
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
        if (_modes.Mode == KeyMode.Register)
        {
            return TakeRegister(e);
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

        // Entering a register name is spelled the same way vim spells it.
        if (_modes.Mode == KeyMode.Normal && key is { Key: Core.Keys.Key.QuoteDbl })
        {
            _modes.EnterMode(KeyMode.Register);
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
        LeaveVisual(pane);
        _modes.Reset();
    }

    /// <summary>Opens the command line. The window supplies the handler.</summary>
    public void EnterCommandLine(string initial) => CommandLineRequested?.Invoke(this, initial);

    /// <summary>Raised when a command asked for the command line to open.</summary>
    public event EventHandler<string>? CommandLineRequested;

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

    private bool TakeRegister(KeyEventArgs e)
    {
        var text = AvaloniaKeys.TextOf(e);

        if (text is { Length: 1 } && !char.IsControl(text[0]))
        {
            _modes.SetRegister(text[0]);
        }

        _modes.EnterMode(KeyMode.Normal);
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
