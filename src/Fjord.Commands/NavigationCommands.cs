using Fjord.Core.Diagnostics;
using Fjord.Core.FileSystem;
using Fjord.Core.Navigation;
using Fjord.Core.Panes;
using Fjord.Core.Platform;
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
    private readonly IPlatformServices? _platform;
    private readonly MarkFile? _marks;
    private readonly Core.Config.IConfigService? _config;

    /// <param name="platform">
    /// Used to open a file with its default application. Optional so command parsing and
    /// motion behaviour can be tested without an OS dependency.
    /// </param>
    /// <param name="marks">
    /// Consulted by <c>:cd</c> before the filesystem. Optional for the same reason.
    /// </param>
    /// <param name="config">
    /// Supplies the named listings <c>:sort</c> cycles through. Optional for the same
    /// reason as the others: the motions are tested without any of it.
    /// </param>
    public NavigationCommands(
        IPlatformServices? platform = null,
        MarkFile? marks = null,
        Core.Config.IConfigService? config = null)
    {
        _platform = platform;
        _marks = marks;
        _config = config;
    }

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

        // A listing whose rows are names rather than paths — the list of scrapbooks — knows
        // what opening one of its rows means, and it is not "navigate to this directory".
        if (pane.IsVirtual)
        {
            return EnterVirtualAsync(pane, messages);
        }

        ref readonly var entry = ref view[pane.CursorIndex];
        var path = Path.Combine(pane.Directory, entry.Name);

        if (entry.IsDirectory)
        {
            return pane.NavigateAsync(path);
        }

        if (_platform is null)
        {
            messages.Info($"Not a directory: {entry.Name}");
            return Task.CompletedTask;
        }

        // Enter on a file hands it to the OS, which is what every file manager does and
        // what makes the key mean one thing rather than two.
        return OpenFileAsync(path, messages);
    }

    /// <summary>Tries the listing's own idea of opening a row, then falls back to a path.</summary>
    private async Task EnterVirtualAsync(IPane pane, IMessageSink messages)
    {
        if (await pane.OpenVirtualAsync(pane.CursorIndex).ConfigureAwait(true))
        {
            return;
        }

        // An ordinary virtual listing — a scrapbook — holds real paths as its names, so
        // opening a row means the same thing it does anywhere else.
        var view = pane.Snapshot?.View ?? default;
        if (view.Count == 0 || pane.CursorIndex >= view.Count)
        {
            return;
        }

        var path = Path.Combine(pane.Directory, view[pane.CursorIndex].Name);

        if (System.IO.Directory.Exists(path))
        {
            await pane.NavigateAsync(path).ConfigureAwait(true);
        }
        else if (_platform is not null)
        {
            await OpenFileAsync(path, messages).ConfigureAwait(true);
        }
    }

    private async Task OpenFileAsync(string path, IMessageSink messages)
    {
        try
        {
            await _platform!.OpenAsync(path).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                       or InvalidOperationException
                                       or IOException
                                       or UnauthorizedAccessException
                                       or PlatformNotSupportedException)
        {
            messages.Error($"Could not open {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    [Command("leave", Aliases = ["up"], Group = "Navigation",
        Description = "Go to the parent directory, or leave a scrapbook")]
    public Task Leave([CommandArgument(Value = CommandValue.Pane)] IPane pane)
    {
        // A scrapbook has no parent. Every instinct for getting out of a listing — h,
        // Backspace, Left, Escape — should get you out of one.
        if (pane.IsVirtual)
        {
            return pane.LeaveVirtualAsync();
        }

        var parent = Path.GetDirectoryName(pane.Directory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (string.IsNullOrEmpty(parent))
        {
            return Task.CompletedTask;
        }

        // Select the directory just left, so up-then-down returns to where you were.
        return pane.NavigateAsync(parent, Path.GetFileName(pane.Directory));
    }

    /// <summary>
    /// Goes to a quickmark, or to a directory.
    /// </summary>
    /// <remarks>
    /// Quickmarks are tried first, so <c>:cd work</c> reaches the place you named even when
    /// a folder called <c>work</c> happens to sit beside you. That is the point of naming
    /// one; a subdirectory is always reachable as <c>./work</c>.
    /// <para>
    /// A destination that does not exist is refused rather than navigated to. Fjord will
    /// happily show an empty listing for a path with nothing at it, which looks exactly like
    /// an empty directory and sends the user looking for files that were never there.
    /// </para>
    /// </remarks>
    [Command("cd", Group = "Navigation", MaxSplit = 0, Description = "Go to a quickmark or a directory")]
    public Task ChangeDirectory(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages,
        [CommandArgument(Completion = CompletionKind.Destination)] string directory = "")
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(directory);

        if (directory.Length == 0)
        {
            // Bare :cd goes home, as a shell's does. The picker is what a trailing space
            // gets you, since that is the command line asking rather than the command.
            directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        else if (_marks?.Get(directory) is { } mark)
        {
            return Directory.Exists(mark.Directory)
                ? pane.NavigateAsync(mark.Directory, mark.CursorName)
                : Refuse(messages, $"Quickmark {directory} points at {mark.Directory}, which is gone.");
        }

        var expanded = directory.StartsWith('~')
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                directory[1..].TrimStart('/', '\\'))
            : directory;

        var target = Path.IsPathRooted(expanded) ? expanded : Path.Combine(pane.Directory, expanded);

        string full;
        try
        {
            full = Path.GetFullPath(target);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Refuse(messages, $"{directory} is not a usable path: {ex.Message}");
        }

        if (!Directory.Exists(full))
        {
            return Refuse(
                messages,
                File.Exists(full)
                    ? $"{full} is a file, not a directory."
                    : $"No directory or quickmark called '{directory}'.");
        }

        return pane.NavigateAsync(full);
    }

    private static Task Refuse(IMessageSink messages, string reason)
    {
        messages.Error(reason);
        return Task.CompletedTask;
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

    /// <summary>
    /// Applies a named listing, cycles to the next one, or changes the order directly.
    /// </summary>
    /// <remarks>
    /// Three jobs because they are the same question asked at three levels of detail.
    /// Bare <c>:sort</c> is the interactive one and cycles, which is what makes a single
    /// key alternate a column's direction and then move on to the next column. A listing
    /// replaces the view wholesale rather than patching it, so the same name always gives
    /// the same result and the cycle is predictable from anywhere.
    /// </remarks>
    [Command("sort", Group = "Listing", MaxSplit = 1,
        Description = "Apply a named listing, or cycle through them")]
    public void Sort(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages,
        [CommandArgument(Completion = CompletionKind.Listing)] string listing = "",
        [CommandArgument(Flag = 'k', Choices = ["name", "size", "modified", "extension"])]
        string key = "",
        [CommandArgument(Flag = 'r')] bool reverse = false)
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(key);

        if (listing.Length > 0)
        {
            Apply(pane, messages, listing);
            return;
        }

        if (key.Length == 0 && !reverse && NextListing(pane) is { } next)
        {
            Apply(pane, messages, next);
            return;
        }

        var sort = pane.Sort;

        if (key.Length > 0)
        {
            sort = sort with { Key = Enum.Parse<SortKey>(key, ignoreCase: true) };
        }

        // No listings configured and no key given: fall back to what :sort always did,
        // so removing every listing leaves a usable command rather than a dead key.
        sort = reverse || key.Length == 0
            ? sort with { Descending = !pane.Sort.Descending }
            : sort;

        pane.SetOrder(sort, pane.Filter);
    }

    /// <summary>The listing after whichever one matches the pane, or the first.</summary>
    /// <remarks>
    /// Found by matching the current view rather than by remembering where the cycle last
    /// stopped. Changing the order some other way — a flag, a config reload — then pressing
    /// the key should continue from what is on screen, not from a position nothing else
    /// knows about.
    /// </remarks>
    private string? NextListing(IPane pane)
    {
        var names = _config?.ListingNames ?? [];

        if (names.Count == 0)
        {
            return null;
        }

        for (var i = 0; i < names.Count; i++)
        {
            if (_config!.Listing(names[i]) is { } view
                && view.Sort == pane.Sort
                && view.Filter == pane.Filter)
            {
                return names[(i + 1) % names.Count];
            }
        }

        return names[0];
    }

    private void Apply(IPane pane, IMessageSink messages, string name)
    {
        if (_config?.Listing(name) is not { } view)
        {
            messages.Error($"No listing called '{name}'.");
            return;
        }

        pane.SetOrder(view.Sort, view.Filter);
        messages.Info($"Listing: {name}");
    }

    [Command("listing-save", Group = "Listing",
        Description = "Save the current sort and filter under a name")]
    public void ListingSave(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages,
        string name)
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(name);

        if (_config is null)
        {
            messages.Error("No configuration file to save to.");
            return;
        }

        if (_config.SaveListing(name, pane.Sort, pane.Filter) is { } problem)
        {
            messages.Error(problem);
            return;
        }

        messages.Info($"Saved listing '{name}'.");
    }

    [Command("listing-remove", Group = "Listing", Description = "Remove a named listing")]
    public void ListingRemove(
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages,
        [CommandArgument(Completion = CompletionKind.Listing)] string name)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(name);

        if (_config is null)
        {
            messages.Error("No configuration file to edit.");
            return;
        }

        if (_config.RemoveListing(name) is { } problem)
        {
            messages.Error(problem);
            return;
        }

        messages.Info($"Removed listing '{name}'.");
    }

    [Command("listings", Group = "Listing", Description = "List the named listings")]
    public void Listings([CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var names = _config?.ListingNames ?? [];

        messages.Info(names.Count == 0
            ? "No listings. :listing-save <name> keeps the current one."
            : string.Join("  ", names));
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
}
