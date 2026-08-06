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
            return TakeName(target, e, pane);
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
        _awaiting = null;
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

    /// <summary>
    /// Arranges for the next keystroke to be read as a name.
    /// </summary>
    /// <remarks>
    /// Called from a command, so <c>"</c>, <c>m</c> and <c>'</c> are ordinary bindings the
    /// user can move rather than characters wired into the key handler.
    /// </remarks>
    public void ReadName(NameTarget target)
    {
        _awaiting = target;
        _modes.EnterMode(target == NameTarget.Register ? KeyMode.Register : KeyMode.Mark);
    }

    /// <summary>Directories remembered with <c>m</c>.</summary>
    public MarkFile Marks { get; } = new();

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
    /// half-typed <c>m</c> must be cancellable, and binding a mark to Home would be a
    /// mark nobody could type again.
    /// </remarks>
    private bool TakeName(NameTarget target, KeyEventArgs e, IPane? pane)
    {
        _awaiting = null;
        _modes.EnterMode(KeyMode.Normal);

        if (AvaloniaKeys.TextOf(e) is { Length: 1 } text && !char.IsControl(text[0]))
        {
            Apply(target, text[0], pane);
        }

        Handled?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void Apply(NameTarget target, char name, IPane? pane)
    {
        switch (target)
        {
            case NameTarget.Register:
                _modes.SetRegister(name);
                break;

            case NameTarget.MarkToSet when pane is { Directory.Length: > 0 }:
                Marks.Set(name, pane.Directory, pane.CursorName);
                _messages.Info($"Mark {name} set to {pane.Directory}.");
                break;

            case NameTarget.MarkToVisit when pane is not null:
                Visit(name, pane);
                break;

            default:
                break;
        }
    }

    private void Visit(char name, IPane pane)
    {
        if (Marks.Get(name) is not { } mark)
        {
            _messages.Error($"Mark {name} is not set.");
            return;
        }

        Observe(pane.NavigateAsync(mark.Directory, mark.CursorName));
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
