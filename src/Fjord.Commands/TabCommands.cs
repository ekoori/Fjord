using Fjord.Core.Diagnostics;
using Fjord.Core.Panes;

namespace Fjord.Commands;

/// <summary>Opening, closing and switching tabs.</summary>
public sealed class TabCommands
{
    [Command("tab-next", Group = "Tabs", Description = "Switch to the next tab, or to tab <count>")]
    public void TabNext(
        [CommandArgument(Value = CommandValue.Workspace)] IWorkspace workspace,
        [CommandArgument(Value = CommandValue.Count)] int count = 0)
    {
        // <count>gt goes to that tab, counted from one, as vim numbers them. Bare gt
        // steps forward and wraps.
        workspace.CurrentIndex = count > 0
            ? Math.Min(count, workspace.Tabs.Count) - 1
            : (workspace.CurrentIndex + 1) % workspace.Tabs.Count;
    }

    [Command("tab-prev", Group = "Tabs", Description = "Switch to the previous tab")]
    public void TabPrev(
        [CommandArgument(Value = CommandValue.Workspace)] IWorkspace workspace,
        [CommandArgument(Value = CommandValue.Count)] int count = 1)
    {
        var total = workspace.Tabs.Count;
        var step = ((count % total) + total) % total;
        workspace.CurrentIndex = (workspace.CurrentIndex - step + total) % total;
    }

    [Command("tab-new", Aliases = ["tabnew"], Group = "Tabs", MaxSplit = 0,
        Description = "Open a new tab, in this directory unless one is given")]
    public void TabNew(
        [CommandArgument(Value = CommandValue.Workspace)] IWorkspace workspace,
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Completion = CompletionKind.Directory)] string directory = "",
        [CommandArgument(Flag = 'b')] bool background = false)
    {
        var target = directory.Length == 0
            ? pane.Directory
            : Path.GetFullPath(Path.IsPathRooted(directory)
                ? directory
                : Path.Combine(pane.Directory, directory));

        workspace.OpenTab(target, NewTabPosition.AfterCurrent, background);
    }

    [Command("tab-close", Aliases = ["tabclose"], Group = "Tabs",
        Description = "Close the current tab")]
    public void TabClose(
        [CommandArgument(Value = CommandValue.Workspace)] IWorkspace workspace,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        if (workspace.Tabs.Count == 1)
        {
            // Closing the last tab would leave a window with nothing in it. Quitting is a
            // separate, explicit decision.
            messages.Info("Last tab; use :quit to close Fjord.");
            return;
        }

        workspace.CloseTab(workspace.CurrentIndex);
    }

    [Command("tab-undo-close", Aliases = ["tabundo"], Group = "Tabs",
        Description = "Reopen the most recently closed tab")]
    public void TabUndoClose(
        [CommandArgument(Value = CommandValue.Workspace)] IWorkspace workspace,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        if (!workspace.UndoClose())
        {
            messages.Info("No recently closed tabs.");
        }
    }

    [Command("tab-move", Group = "Tabs", Description = "Move the current tab to position <count>")]
    public void TabMove(
        [CommandArgument(Value = CommandValue.Workspace)] IWorkspace workspace,
        [CommandArgument(Value = CommandValue.Count)] int count = 0)
    {
        var target = count > 0 ? Math.Min(count, workspace.Tabs.Count) - 1 : 0;
        workspace.MoveTab(workspace.CurrentIndex, target);
    }
}
