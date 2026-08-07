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

    /// <remarks>
    /// Not bound to a key by default. It is a deliberate act on one tab, not something you
    /// reach for often, and every unshifted letter is already spoken for.
    /// </remarks>
    [Command("tab-lock", Aliases = ["lock"], Group = "Tabs",
        Description = "Pin this tab to its directory; navigating out opens a new tab")]
    public void TabLock(
        [CommandArgument(Value = CommandValue.Workspace)] IWorkspace workspace,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(messages);

        var tab = workspace.Current;
        tab.IsLocked = !tab.IsLocked;

        messages.Info(tab.IsLocked
            ? $"Locked to {tab.Title}. Navigating out opens a new tab."
            : $"Unlocked {tab.Title}.");
    }

    /// <remarks>
    /// Takes a position as an argument as well as a count, because they come from
    /// different places: the count is the vim-style prefix typed before a key, and
    /// <c>:tab-move 3</c> from the command line is an argument. Only supporting the count
    /// meant the command silently did nothing when typed, which is how it was found.
    /// </remarks>
    [Command("tab-move", Group = "Tabs",
        Description = "Move the current tab to position <n>, counting from one")]
    public void TabMove(
        [CommandArgument(Value = CommandValue.Workspace)] IWorkspace workspace,
        [CommandArgument(Value = CommandValue.Count)] int count = 0,
        int position = 0)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var requested = position > 0 ? position : count;
        var target = requested > 0 ? Math.Min(requested, workspace.Tabs.Count) - 1 : 0;

        workspace.MoveTab(workspace.CurrentIndex, target);
    }

    /// <remarks>
    /// With no argument it clears the name and the tab goes back to showing its directory,
    /// which is the only way back and needs no second command.
    /// </remarks>
    [Command("tab-rename", Aliases = ["tabrename"], Group = "Tabs", MaxSplit = 0,
        Description = "Name this tab. With no name, go back to the directory's own.")]
    public void TabRename(
        [CommandArgument(Value = CommandValue.Workspace)] IWorkspace workspace,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages,
        string name = "")
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(name);

        var tab = workspace.Current;
        var trimmed = name.Trim();

        tab.Name = trimmed.Length == 0 ? null : trimmed;
        messages.Info(tab.Name is null ? $"Tab shows {tab.Title} again." : $"Tab named {tab.Name}.");
    }

    [Command("tab-move-left", Aliases = ["tabmoveleft"], Group = "Tabs",
        Description = "Swap this tab with the one before it")]
    public void TabMoveLeft(
        [CommandArgument(Value = CommandValue.Workspace)] IWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        // Wraps, like tab-next does. Moving left off the front and landing at the back is
        // more useful than a key that stops working at position one.
        var from = workspace.CurrentIndex;
        var to = from == 0 ? workspace.Tabs.Count - 1 : from - 1;
        workspace.MoveTab(from, to);
    }

    [Command("tab-move-right", Aliases = ["tabmoveright"], Group = "Tabs",
        Description = "Swap this tab with the one after it")]
    public void TabMoveRight(
        [CommandArgument(Value = CommandValue.Workspace)] IWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var from = workspace.CurrentIndex;
        var to = from == workspace.Tabs.Count - 1 ? 0 : from + 1;
        workspace.MoveTab(from, to);
    }
}
