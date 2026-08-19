using Fjord.Core.Diagnostics;
using Fjord.Core.Navigation;
using Fjord.Core.Panes;

namespace Fjord.Commands;

/// <summary>
/// Commands that change input mode or affect the application itself.
/// </summary>
/// <remarks>
/// The work happens in <see cref="IModeHost"/>, implemented by the App layer. These stay
/// commands rather than special cases in the key handler so they remain bindable, appear
/// in <c>:help</c>, and are covered by the default-binding resolution test.
/// </remarks>
public sealed class ModeCommands
{
    private readonly IModeHost _host;

    public ModeCommands(IModeHost host) => _host = host;

    [Command("visual-mode", Aliases = ["visual"], Group = "Mode",
        Description = "Enter or leave visual selection mode")]
    public void VisualMode([CommandArgument(Value = CommandValue.Pane)] IPane pane) =>
        _host.ToggleVisual(pane);

    [Command("visual-swap", Group = "Mode",
        Description = "Move the cursor to the other end of the visual range")]
    public void VisualSwap([CommandArgument(Value = CommandValue.Pane)] IPane pane)
    {
        // vim's "o". Lets a selection be extended from the end it was not started at,
        // without leaving visual mode and beginning again.
        if (pane.VisualAnchor is { } anchor)
        {
            var cursor = pane.CursorIndex;
            pane.VisualAnchor = cursor;
            pane.MoveCursorTo(anchor);
        }
    }

    [Command("mode-leave", Group = "Mode", Description = "Return to normal mode")]
    public void ModeLeave([CommandArgument(Value = CommandValue.Pane)] IPane pane) =>
        _host.LeaveMode(pane);

    [Command("command-mode", Group = "Mode", MaxSplit = 0,
        Description = "Open the command line")]
    public void CommandMode(string initial = ":") => _host.EnterCommandLine(initial);

    [Command("register-select", Group = "Mode",
        Description = "Name the register the next command uses")]
    public void RegisterSelect() => _host.ReadName(NameTarget.Register);

    /// <remarks>
    /// Bound to <c>m</c> as <c>:mark-set </c>, so the key opens the command line and the
    /// name is typed as a word. Defaulting the name to the directory's own last segment
    /// makes the common case a single extra keystroke: <c>m</c> then Enter.
    /// </remarks>
    [Command("mark-set", Aliases = ["mark"], Group = "Navigation",
        Description = "Remember this directory under a name you can :cd to")]
    public void MarkSet(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages,
        string name = "")
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(name);

        if (pane.Directory.Length == 0)
        {
            messages.Error("Nothing to mark.");
            return;
        }

        if (name.Length == 0)
        {
            name = SuggestName(pane.Directory);
        }

        if (!MarkFile.IsValidName(name))
        {
            messages.Error($"'{name}' is not a quickmark name: one word of letters, digits, - or _.");
            return;
        }

        _host.Marks.Set(name, pane.Directory, pane.CursorName);
        messages.Info($"{name} → {pane.Directory}");
    }

    [Command("marks", Group = "Navigation", Description = "List the quickmarks that are set")]
    public void Marks([CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var names = _host.Marks.Names;

        if (names.Count == 0)
        {
            messages.Info("No quickmarks. Press m to set one here.");
            return;
        }

        messages.Info(string.Join(
            "   ", names.Select(name => $"{name} {_host.Marks.Get(name)!.Value.Directory}")));
    }

    [Command("mark-clear", Group = "Navigation",
        Description = "Forget one quickmark, or every one when none is named")]
    public void MarkClear(
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages,
        [CommandArgument(Completion = CompletionKind.Quickmark)] string name = "")
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(name);

        if (name.Length == 0)
        {
            _host.Marks.Clear();
            messages.Info("All quickmarks forgotten.");
            return;
        }

        if (_host.Marks.Get(name) is null)
        {
            messages.Error($"No quickmark called '{name}'.");
            return;
        }

        _host.Marks.Clear(name);
        messages.Info($"Quickmark {name} forgotten.");
    }

    /// <summary>
    /// A name for the directory being marked, when the user gave none.
    /// </summary>
    /// <remarks>
    /// The last segment, with anything a quickmark name cannot hold folded to <c>-</c>.
    /// A root has no segment, so it falls back to something typeable rather than nothing.
    /// </remarks>
    private static string SuggestName(string directory)
    {
        var leaf = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var cleaned = new string([.. leaf.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')]);

        return MarkFile.IsValidName(cleaned) ? cleaned : "root";
    }

    [Command("frame-stats", Group = "Diagnostics",
        Description = "Show or hide per-frame render times")]
    public void FrameStats([CommandArgument(Value = CommandValue.Messages)] IMessageSink messages) =>
        messages.Info(_host.ToggleFrameStats() ? "Frame stats on." : "Frame stats off.");

    /// <remarks>
    /// Reported rather than silent: it changes what a press on the listing means, and a
    /// mode you cannot see is a trap. The status line shows it for as long as it is on.
    /// </remarks>
    [Command("drag-mode", Aliases = ["mouse-mode"], Group = "Listing",
        Description = "Toggle dragging entries with the mouse")]
    public void DragMode([CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        messages.Info(_host.ToggleDragMode()
            ? "Drag mode on: drag entries to move them, with Ctrl to copy. The window no longer drags from the list."
            : "Drag mode off.");
    }

    [Command("quit", Aliases = ["q"], Group = "Application", Description = "Close Fjord")]
    public void Quit() => _host.Quit();
}
