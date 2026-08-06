using Fjord.Core.Diagnostics;
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

    [Command("mark-set", Group = "Navigation",
        Description = "Remember this directory under the next key pressed")]
    public void MarkSet() => _host.ReadName(NameTarget.MarkToSet);

    [Command("mark-visit", Group = "Navigation",
        Description = "Go to the mark named by the next key pressed")]
    public void MarkVisit() => _host.ReadName(NameTarget.MarkToVisit);

    [Command("marks", Group = "Navigation", Description = "List the marks that are set")]
    public void Marks([CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var names = _host.Marks.Names;

        if (names.Count == 0)
        {
            messages.Info("No marks set. Press m then a key to set one.");
            return;
        }

        messages.Info(string.Join(
            "  ", names.Select(name => $"{name} {_host.Marks.Get(name)!.Value.Directory}")));
    }

    [Command("mark-clear", Group = "Navigation",
        Description = "Forget one mark, or every mark when none is named")]
    public void MarkClear(
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages,
        string name = "")
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(name);

        if (name.Length == 0)
        {
            _host.Marks.Clear();
            messages.Info("All marks forgotten.");
            return;
        }

        _host.Marks.Clear(name[0]);
        messages.Info($"Mark {name[0]} forgotten.");
    }

    [Command("frame-stats", Group = "Diagnostics",
        Description = "Show or hide per-frame render times")]
    public void FrameStats([CommandArgument(Value = CommandValue.Messages)] IMessageSink messages) =>
        messages.Info(_host.ToggleFrameStats() ? "Frame stats on." : "Frame stats off.");

    [Command("quit", Aliases = ["q"], Group = "Application", Description = "Close Fjord")]
    public void Quit() => _host.Quit();
}
