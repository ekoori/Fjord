using Fjord.Core.Diagnostics;
using Fjord.Core.FileSystem;
using Fjord.Core.Operations;
using Fjord.Core.Panes;
using Fjord.Core.Registers;
using Fjord.Core.Selection;

namespace Fjord.Commands;

/// <summary>
/// Commands that change the filesystem.
/// </summary>
/// <remarks>
/// Every one of these goes through <see cref="IOperationHost"/> rather than touching disk,
/// so the work is queued, cancellable and undoable, and so these bodies can be tested
/// against a fake.
/// </remarks>
public sealed class OperationCommands
{
    private readonly IOperationHost _host;
    private readonly IFileOperationFactory _factory;

    public OperationCommands(IOperationHost host, IFileOperationFactory factory)
    {
        _host = host;
        _factory = factory;
    }

    // ---- Clipboard

    [Command("yank-paths", Aliases = ["copy"], Group = "Files", IsOperator = true,
        Description = "Mark the entries a motion covers for copying")]
    public void YankPaths(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Range)] EntryRange range,
        [CommandArgument(Value = CommandValue.Register)] char register,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        var paths = PathsIn(pane, range);
        _host.Registers[register] = new ClipboardContents(paths, Cut: false);
        messages.Info($"Yanked {Count(paths.Count)} for copying{Into(register)}.");
    }

    [Command("cut-paths", Aliases = ["cut"], Group = "Files", IsOperator = true,
        Description = "Mark the entries a motion covers for moving")]
    public void CutPaths(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Range)] EntryRange range,
        [CommandArgument(Value = CommandValue.Register)] char register,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        var paths = PathsIn(pane, range);

        // Cutting means the source goes away, and nothing can be taken out of an archive:
        // Fjord reads them and does not write them. Yanking does what was wanted anyway.
        if (paths.Any(ArchivePath.IsInside))
        {
            messages.Error("Cannot cut out of an archive. Yank instead; pasting extracts.");
            return;
        }

        _host.Registers[register] = new ClipboardContents(paths, Cut: true);
        messages.Info($"Cut {Count(paths.Count)} for moving{Into(register)}.");
    }

    [Command("paste", Group = "Files", Description = "Copy or move a register's contents here")]
    public void Paste(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Register)] char register,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        if (PaneTargets.RefuseInsideArchive(pane, messages, "paste"))
        {
            return;
        }

        var clipboard = _host.Registers[register];

        if (clipboard.IsEmpty)
        {
            messages.Info(register == RegisterFile.UnnamedName
                ? "Nothing to paste."
                : $"Register \"{register} is empty.");
            return;
        }

        // A yank taken inside an archive holds paths the filesystem cannot open, so pasting
        // one extracts rather than copies. Splitting here rather than refusing the whole
        // paste keeps a register that mixes the two — easy to build by yanking twice —
        // from being unusable.
        var members = clipboard.Paths.Where(ArchivePath.IsInside).ToList();
        var onDisk = clipboard.Paths.Where(p => !ArchivePath.IsInside(p)).ToList();

        if (onDisk.Count > 0)
        {
            _host.Enqueue(clipboard.Cut
                ? _factory.Move(onDisk, pane.Directory)
                : _factory.Copy(onDisk, pane.Directory));
        }

        if (members.Count > 0)
        {
            _host.Enqueue(_factory.Extract(members, pane.Directory));
        }

        // A cut is consumed by pasting; a yank is not, so the same files can be copied to
        // several places without re-yanking. This is what vim does with its registers and
        // what every file manager does with cut and copy.
        if (clipboard.Cut)
        {
            _host.Registers.Clear(register);
        }
    }

    /// <remarks>
    /// The system clipboard, not a register: this is for pasting a path into a terminal, a
    /// chat message or a dialog box, which is a different job from marking files for Fjord
    /// itself to copy. Hence YY beside yy rather than a flag on it.
    /// </remarks>
    [Command("yank-text", Aliases = ["yank-paths-as-text"], Group = "Files", IsOperator = true,
        Description = "Copy the full paths a motion covers to the system clipboard as text")]
    public async Task YankText(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Range)] EntryRange range,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var paths = PathsIn(pane, range);

        if (paths.Count == 0)
        {
            return;
        }

        await _host.SetClipboardTextAsync(string.Join(Environment.NewLine, paths)).ConfigureAwait(true);
        messages.Info($"Copied {Count(paths.Count)} to the clipboard as text.");
    }

    [Command("registers", Aliases = ["reg"], Group = "Files",
        Description = "List what each register is holding")]
    public void Registers(
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var names = _host.Registers.Names;

        if (names.Count == 0)
        {
            messages.Info("All registers are empty.");
            return;
        }

        // One line, because the status bar is one line. Registers hold a handful of paths
        // at a time, and the count plus the verb is what the user is checking.
        messages.Info(string.Join("  ", names.Select(name =>
        {
            var contents = _host.Registers[name];
            return $"\"{name} {(contents.Cut ? "cut" : "yank")} {contents.Paths.Count}";
        })));
    }

    /// <summary>Names the register in a message, unless it was the unnamed one.</summary>
    private static string Into(char register) =>
        register == RegisterFile.UnnamedName ? string.Empty : $" into \"{register}";

    // ---- Destructive

    /// <remarks>
    /// The operator form, so it takes a motion: <c>d3j</c> for four rows, <c>dd</c> for the
    /// one under the cursor. <c>:trash-selection</c> is the other half — it acts on what
    /// Space selected, which a motion has no way to describe because that selection can
    /// have holes in it.
    /// </remarks>
    [Command("trash", Group = "Files", IsOperator = true,
        Description = "Move the entries a motion covers to the trash (dd for this one)")]
    public void Trash(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Range)] EntryRange range,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        if (PaneTargets.RefuseInsideArchive(pane, messages, "trash"))
        {
            return;
        }

        TrashPaths(PathsIn(pane, range), messages);
    }

    /// <remarks>
    /// What <c>D</c> is bound to, and the one to reach for after selecting with Space. Falls
    /// back to the entry under the cursor so the key always does something.
    /// </remarks>
    [Command("trash-selection", Group = "Files",
        Description = "Move the Space-selected entries, or the one under the cursor, to the trash")]
    public void TrashSelection(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        if (PaneTargets.RefuseInsideArchive(pane, messages, "trash"))
        {
            return;
        }

        TrashPaths(PaneTargets.Of(pane), messages);
    }

    /// <summary>
    /// Confirms, then queues the trash operation.
    /// </summary>
    /// <remarks>
    /// Trashing is undoable, so the prompt is not protecting the data — it is protecting
    /// against the keystroke. <c>d</c> sits next to <c>s</c> and <c>f</c>, and a mistyped
    /// operator that silently empties a folder into the bin is a bad surprise even when it
    /// is reversible. The host decides whether the question is actually asked.
    /// </remarks>
    private void TrashPaths(List<string> paths, IMessageSink messages)
    {
        if (paths.Count == 0)
        {
            return;
        }

        var what = paths.Count == 1 ? Path.GetFileName(paths[0]) : Count(paths.Count);

        _host.Confirm($"Move {what} to the trash?", () =>
        {
            _host.Enqueue(_factory.Trash(paths));
            messages.Info($"Moving {Count(paths.Count)} to the trash.");
        });
    }

    /// <remarks>
    /// Deliberately not bound to a key and deliberately named with a bang. The spec is
    /// emphatic that destructive actions default to the trash and that permanent deletion
    /// needs an explicit command; this is the only route to one, and it cannot be undone.
    /// </remarks>
    [Command("delete!", Aliases = ["delete-permanently"], Group = "Files",
        Description = "Permanently delete the selection. Cannot be undone.")]
    public void DeletePermanently(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        var paths = PaneTargets.Of(pane);

        if (paths.Count == 0)
        {
            return;
        }

        _host.Enqueue(_factory.Delete(paths));
        messages.Warning($"Permanently deleting {Count(paths.Count)}.");
    }

    // ---- Creating and renaming

    [Command("rename", Group = "Files", MaxSplit = 0,
        Description = "Rename the entry under the cursor")]
    public void Rename(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        string newName,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        if (PaneTargets.RefuseInsideArchive(pane, messages, "rename"))
        {
            return;
        }

        if (pane.PathAt(pane.CursorIndex) is not { } path)
        {
            messages.Info("Nothing to rename.");
            return;
        }

        _host.Enqueue(_factory.Rename(path, newName));
    }

    [Command("mkdir", Group = "Files", MaxSplit = 0, Description = "Create a directory")]
    public void MakeDirectory(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane, string name) =>
        _host.Enqueue(_factory.Create(Path.Combine(pane.Directory, name), directory: true));

    [Command("touch", Aliases = ["new"], Group = "Files", MaxSplit = 0,
        Description = "Create an empty file")]
    public void Touch(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane, string name) =>
        _host.Enqueue(_factory.Create(Path.Combine(pane.Directory, name), directory: false));

    [Command("duplicate", Group = "Files",
        Description = "Copy the selection alongside itself")]
    public void Duplicate(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        var paths = PaneTargets.Of(pane);

        if (paths.Count == 0)
        {
            return;
        }

        // Into the same directory, so the conflict policy is what produces "name (2)".
        _host.Enqueue(_factory.Duplicate(paths, pane.Directory));
    }

    // ---- Undo

    [Command("undo", Group = "Files", Description = "Reverse the last file operation")]
    public async Task Undo([CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        var description = await _host.UndoAsync().ConfigureAwait(true);

        messages.Info(description is null
            ? "Nothing to undo."
            : $"Undid: {description}");
    }

    [Command("redo", Group = "Files", Description = "Repeat the last undone operation")]
    public async Task Redo([CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        var description = await _host.RedoAsync().ConfigureAwait(true);

        messages.Info(description is null
            ? "Nothing to redo."
            : $"Redid: {description}");
    }

    /// <summary>Full paths for the rows a range covers.</summary>
    private static List<string> PathsIn(IPane pane, EntryRange range) =>
        PaneTargets.InRange(pane, range);

    /// <summary>
    /// The committed selection, or the cursor row when nothing is selected.
    /// </summary>
    /// <remarks>
    /// The rule every file manager uses, and the one that makes a bare <c>:trash</c> do
    /// what the user means whether or not they have selected anything.
    /// </remarks>

    private static string Count(int n) => $"{n} item{(n == 1 ? "" : "s")}";
}

/// <summary>
/// Builds operations without <c>Fjord.Commands</c> knowing how they are implemented.
/// </summary>
/// <remarks>
/// The operations live in <c>Fjord.Operations</c>, which depends on Core. Commands could
/// depend on it too, but the factory keeps the command bodies testable with fakes and
/// leaves the door open for an operation that needs something the commands should not know
/// about.
/// </remarks>
public interface IFileOperationFactory
{
    IFileOperation Copy(IReadOnlyList<string> sources, string destinationDirectory);

    IFileOperation Move(IReadOnlyList<string> sources, string destinationDirectory);

    IFileOperation Duplicate(IReadOnlyList<string> sources, string destinationDirectory);

    IFileOperation Rename(string path, string newName);

    IFileOperation Trash(IReadOnlyList<string> paths);

    IFileOperation Delete(IReadOnlyList<string> paths);

    /// <param name="members">Paths inside an archive, as <c>archive.zip!/dir/name</c>.</param>
    IFileOperation Extract(IReadOnlyList<string> members, string destinationDirectory);

    IFileOperation Create(string path, bool directory);
}
