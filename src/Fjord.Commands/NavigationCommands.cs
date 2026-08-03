using Fjord.Core.Diagnostics;
using Fjord.Core.FileSystem;
using Fjord.Core.Panes;
using Fjord.Core.Selection;

namespace Fjord.Commands;

/// <summary>
/// Cursor motions, directory navigation and selection.
/// </summary>
/// <remarks>
/// Everything marked with a <see cref="MotionKind"/> must be a <b>pure cursor move</b>:
/// <see cref="OperatorDispatcher"/> resolves an operator's range by running the motion and
/// diffing the cursor, so a motion with side effects would apply them twice. A reflection
/// test enforces this across the whole registry.
/// </remarks>
public sealed class NavigationCommands
{
    // ---- Motions

    [Command("move-down", Group = "Motion", Motion = MotionKind.Linewise,
        Description = "Move the cursor down")]
    public void MoveDown(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Count)] int count = 1) =>
        pane.MoveCursor(count);

    [Command("move-up", Group = "Motion", Motion = MotionKind.Linewise,
        Description = "Move the cursor up")]
    public void MoveUp(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Count)] int count = 1) =>
        pane.MoveCursor(-count);

    /// <remarks>
    /// Bare <c>gg</c> goes to the top; <c>&lt;count&gt;gg</c> goes to that entry, counted
    /// from one as vim numbers lines.
    /// </remarks>
    [Command("move-first", Group = "Motion", Motion = MotionKind.Linewise,
        Description = "Move to the first entry, or to entry <count>")]
    public void MoveFirst(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Count)] int count = 0) =>
        pane.MoveCursorTo(count > 0 ? count - 1 : 0);

    [Command("move-last", Group = "Motion", Motion = MotionKind.Linewise,
        Description = "Move to the last entry, or to entry <count>")]
    public void MoveLast(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Count)] int count = 0) =>
        pane.MoveCursorTo(count > 0 ? count - 1 : int.MaxValue);

    [Command("move-page-down", Group = "Motion", Motion = MotionKind.Linewise,
        Description = "Move down a screenful")]
    public void MovePageDown(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Count)] int count = 1) =>
        pane.MoveCursor(pane.PageSize * count);

    [Command("move-page-up", Group = "Motion", Motion = MotionKind.Linewise,
        Description = "Move up a screenful")]
    public void MovePageUp(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Count)] int count = 1) =>
        pane.MoveCursor(-pane.PageSize * count);

    [Command("move-half-page-down", Group = "Motion", Motion = MotionKind.Linewise,
        Description = "Move down half a screenful")]
    public void MoveHalfPageDown(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Count)] int count = 1) =>
        pane.MoveCursor(Math.Max(1, pane.PageSize / 2) * count);

    [Command("move-half-page-up", Group = "Motion", Motion = MotionKind.Linewise,
        Description = "Move up half a screenful")]
    public void MoveHalfPageUp(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Count)] int count = 1) =>
        pane.MoveCursor(-Math.Max(1, pane.PageSize / 2) * count);

    // ---- Navigation

    [Command("enter", Aliases = ["open"], Group = "Navigation",
        Description = "Open the entry under the cursor")]
    public Task Enter(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        var view = pane.Snapshot?.View ?? default;
        if (view.Count == 0 || pane.CursorIndex >= view.Count)
        {
            return Task.CompletedTask;
        }

        ref readonly var entry = ref view[pane.CursorIndex];
        if (!entry.IsDirectory)
        {
            // Opening a file needs the platform layer, which arrives at plan step 13.
            messages.Info($"Not a directory: {entry.Name}");
            return Task.CompletedTask;
        }

        return pane.NavigateAsync(Path.Combine(pane.Directory, entry.Name));
    }

    [Command("leave", Aliases = ["up"], Group = "Navigation",
        Description = "Go to the parent directory")]
    public Task Leave([CommandArgument(Value = CommandValue.Pane)] IPane pane)
    {
        var parent = Path.GetDirectoryName(pane.Directory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (string.IsNullOrEmpty(parent))
        {
            return Task.CompletedTask;
        }

        // Select the directory just left, so up-then-down returns to where you were.
        return pane.NavigateAsync(parent, Path.GetFileName(pane.Directory));
    }

    [Command("cd", Group = "Navigation", MaxSplit = 0, Description = "Go to a directory")]
    public Task ChangeDirectory(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Completion = CompletionKind.Directory)] string directory)
    {
        var expanded = directory.StartsWith('~')
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                directory[1..].TrimStart('/', '\\'))
            : directory;

        var target = Path.IsPathRooted(expanded) ? expanded : Path.Combine(pane.Directory, expanded);
        return pane.NavigateAsync(Path.GetFullPath(target));
    }

    [Command("reload", Group = "Navigation", Description = "Re-read the current directory")]
    public Task Reload([CommandArgument(Value = CommandValue.Pane)] IPane pane) => pane.ReloadAsync();

    [Command("back", Group = "Navigation", Description = "Go back in history")]
    public Task Back(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Count)] int count = 1) =>
        pane.BackAsync(count);

    [Command("forward", Group = "Navigation", Description = "Go forward in history")]
    public Task Forward(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Count)] int count = 1) =>
        pane.ForwardAsync(count);

    // ---- Ordering

    [Command("sort", Group = "Listing", Description = "Change the sort order")]
    public void Sort(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Flag = 'k', Choices = ["name", "size", "modified", "extension"])]
        string key = "",
        [CommandArgument(Flag = 'r')] bool reverse = false)
    {
        var sort = pane.Sort;

        if (key.Length > 0)
        {
            sort = sort with { Key = Enum.Parse<SortKey>(key, ignoreCase: true) };
        }

        // With no flags at all, :sort toggles direction — the common interactive case.
        sort = reverse || key.Length == 0
            ? sort with { Descending = !pane.Sort.Descending }
            : sort;

        pane.SetOrder(sort, pane.Filter);
    }

    [Command("toggle-hidden", Group = "Listing", Description = "Show or hide dotfiles")]
    public void ToggleHidden([CommandArgument(Value = CommandValue.Pane)] IPane pane) =>
        pane.SetOrder(pane.Sort, pane.Filter with { ShowHidden = !pane.Filter.ShowHidden });

    // ---- Selection

    [Command("select-toggle", Group = "Selection", Description = "Select or deselect the current entry")]
    public void SelectToggle(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Count)] int count = 1)
    {
        // Toggling then advancing lets Space be held to select a run, as in a mail client.
        for (var i = 0; i < count; i++)
        {
            pane.Selection.Toggle(pane.CursorIndex);
            pane.MoveCursor(1);
        }
    }

    [Command("select-all", Group = "Selection", Description = "Select every visible entry")]
    public void SelectAll([CommandArgument(Value = CommandValue.Pane)] IPane pane)
    {
        var count = pane.Snapshot?.View.Count ?? 0;
        if (count > 0)
        {
            pane.Selection.SetRange(0, count - 1, true);
        }
    }

    [Command("select-none", Group = "Selection", Description = "Clear the selection")]
    public void SelectNone([CommandArgument(Value = CommandValue.Pane)] IPane pane) =>
        pane.Selection.Clear();

    [Command("select-invert", Group = "Selection", Description = "Invert the selection")]
    public void SelectInvert([CommandArgument(Value = CommandValue.Pane)] IPane pane)
    {
        var count = pane.Snapshot?.View.Count ?? 0;
        if (count > 0)
        {
            pane.Selection.ToggleRange(0, count - 1);
        }
    }

    // ---- Operators

    [Command("select-range", Group = "Selection", IsOperator = true,
        Description = "Select the entries a motion covers")]
    public void SelectRange(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Range)] EntryRange range)
    {
        if (range.Clamp(pane.Snapshot?.View.Count ?? 0) is { } clamped)
        {
            pane.Selection.SetRange(clamped.Start, clamped.End, true);
        }
    }

    [Command("yank", Aliases = ["y"], Group = "Selection", IsOperator = true,
        Description = "Copy the paths a motion covers into a register")]
    public void Yank(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Range)] EntryRange range,
        [CommandArgument(Value = CommandValue.Register)] char register,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        if (range.Clamp(pane.Snapshot?.View.Count ?? 0) is not { } clamped)
        {
            return;
        }

        var paths = clamped.Rows()
            .Select(pane.PathAt)
            .Where(p => p is not null)
            .ToList();

        Registers[register] = paths!;
        messages.Info($"Yanked {paths.Count} path{(paths.Count == 1 ? "" : "s")} to register {register}");
    }

    /// <summary>
    /// Yanked paths, keyed by register.
    /// </summary>
    /// <remarks>
    /// M1 only needs registers to round-trip paths. Real file operations, and the undo log
    /// behind them, arrive with the operations queue in M2.
    /// </remarks>
    public Dictionary<char, IReadOnlyList<string>> Registers { get; } = [];
}
