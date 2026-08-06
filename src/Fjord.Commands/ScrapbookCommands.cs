using Fjord.Core.Diagnostics;
using Fjord.Core.Panes;
using Fjord.Core.Selection;
using Fjord.Core.Storage;

namespace Fjord.Commands;

/// <summary>
/// A virtual collection of entries gathered from anywhere.
/// </summary>
/// <remarks>
/// Items stay where they are — a scrapbook holds paths, not copies. That is the whole point
/// of it: gather results from several searches and directories, then act on them together
/// without first moving anything.
/// <para>
/// Opening one navigates to a virtual directory rather than filtering the current listing,
/// because its contents come from many places at once and no single directory could show
/// them.
/// </para>
/// </remarks>
public sealed class ScrapbookCommands
{
    /// <summary>Where items go when no scrapbook is named.</summary>
    public const string DefaultScrapbook = "default";

    private readonly IStorage _storage;
    private readonly IScrapbookHost _host;

    public ScrapbookCommands(IStorage storage, IScrapbookHost host)
    {
        _storage = storage;
        _host = host;
    }

    [Command("scrap-add", Aliases = ["scrap"], Group = "Scrapbook", MaxSplit = 0,
        Description = "Add the selection, or the entry under the cursor, to a scrapbook")]
    public void ScrapAdd(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages,
        string scrapbook = DefaultScrapbook)
    {
        var paths = PaneTargets.Of(pane);

        if (paths.Count == 0)
        {
            return;
        }

        var name = Named(scrapbook);
        var added = _storage.AddToScrapbook(name, paths);
        var total = _storage.Scrapbook(name).Count;

        // Distinguishing added from already-there matters: adding a whole directory twice
        // should say so rather than silently reporting the same number again.
        messages.Info(added == paths.Count
            ? $"Added {Count(added)} to '{name}' ({total} total)."
            : $"Added {Count(added)} to '{name}'; {paths.Count - added} already there ({total} total).");
    }

    [Command("scrap-add-range", Group = "Scrapbook", IsOperator = true,
        Description = "Add the entries a motion covers to the default scrapbook")]
    public void ScrapAddRange(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Range)] EntryRange range,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        if (range.Clamp(pane.Snapshot?.View.Count ?? 0) is not { } clamped)
        {
            return;
        }

        var paths = clamped.Rows().Select(pane.PathAt).Where(p => p is not null).ToList()!;
        var added = _storage.AddToScrapbook(DefaultScrapbook, paths!);

        messages.Info($"Added {Count(added)} to '{DefaultScrapbook}'.");
    }

    [Command("scrap-open", Group = "Scrapbook", MaxSplit = 0, Description = "Show a scrapbook's contents")]
    public async Task ScrapOpen(
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages,
        string scrapbook = DefaultScrapbook)
    {
        var name = Named(scrapbook);
        var items = _storage.Scrapbook(name);

        if (items.Count == 0)
        {
            messages.Warning($"Scrapbook '{name}' is empty.");
            return;
        }

        await _host.OpenScrapbookAsync(
            name,
            items.Select(i => i.Path).ToList(),
            items.Where(i => !string.IsNullOrEmpty(i.Comment))
                 .ToDictionary(i => i.Path, i => i.Comment!, StringComparer.Ordinal))
            .ConfigureAwait(true);
    }

    /// <remarks>
    /// A scrapbook outlives the reason for collecting something. Forty paths with no note
    /// against them is a list you have to re-derive, so the note is worth a command of its
    /// own rather than living in the name.
    /// </remarks>
    [Command("scrap-note", Aliases = ["note"], Group = "Scrapbook", MaxSplit = 0,
        Description = "Note why the selection is in a scrapbook. An empty note clears it.")]
    public void ScrapNote(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages,

        // The note is free text and takes the rest of the line, so the scrapbook has to be
        // a flag — as a second positional it would eat every word after the first.
        [CommandArgument(Flag = 'b')] string book = DefaultScrapbook,
        string comment = "")
    {
        ArgumentNullException.ThrowIfNull(messages);

        var paths = PaneTargets.Of(pane);

        if (paths.Count == 0)
        {
            return;
        }

        var name = Named(book);
        var annotated = _storage.CommentOnScrapbook(name, paths, comment);

        if (annotated == 0)
        {
            messages.Error($"Nothing under the cursor is in '{name}'.");
            return;
        }

        messages.Info(comment.Length == 0
            ? $"Cleared the note on {Count(annotated)}."
            : $"Noted {Count(annotated)} in '{name}'.");
    }

    [Command("scrap-remove", Group = "Scrapbook", MaxSplit = 0,
        Description = "Remove the selection from a scrapbook")]
    public void ScrapRemove(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages,
        string scrapbook = DefaultScrapbook)
    {
        var paths = PaneTargets.Of(pane);

        if (paths.Count == 0)
        {
            return;
        }

        var name = Named(scrapbook);
        messages.Info($"Removed {Count(_storage.RemoveFromScrapbook(name, paths))} from '{name}'.");
    }

    [Command("scrap-clear", Group = "Scrapbook", MaxSplit = 0, Description = "Empty a scrapbook")]
    public void ScrapClear(
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages,
        string scrapbook = DefaultScrapbook)
    {
        var name = Named(scrapbook);

        messages.Info(_storage.ClearScrapbook(name)
            ? $"Cleared '{name}'."
            : $"Scrapbook '{name}' was already empty.");
    }

    /// <remarks>
    /// A listing rather than a line of text, because a list of scrapbooks is something you
    /// pick from: Enter on a row opens it, so the two commands compose into browsing rather
    /// than into reading a name and typing it back.
    /// </remarks>
    [Command("scrap-list", Aliases = ["scraps"], Group = "Scrapbook",
        Description = "Show the scrapbooks as a list you can open")]
    public async Task ScrapList([CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var names = _storage.ScrapbookNames();

        if (names.Count == 0)
        {
            messages.Info("No scrapbooks. Press S to collect something.");
            return;
        }

        var sizes = names.ToDictionary(
            n => n,
            n => $"{Count(_storage.Scrapbook(n).Count)}",
            StringComparer.Ordinal);

        await _host.OpenScrapbookIndexAsync(names, sizes, name => ScrapOpen(messages, name))
            .ConfigureAwait(true);
    }

    private static string Named(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length == 0 ? DefaultScrapbook : trimmed;
    }

    private static string Count(int n) => $"{n} item{(n == 1 ? "" : "s")}";
}

/// <summary>Showing a scrapbook's contents as a listing.</summary>
public interface IScrapbookHost
{
    /// <param name="notes">Path to note, for the column beside each entry.</param>
    Task OpenScrapbookAsync(
        string name,
        IReadOnlyList<string> paths,
        IReadOnlyDictionary<string, string>? notes = null);

    /// <summary>
    /// Shows the scrapbooks themselves as a listing.
    /// </summary>
    /// <param name="sizes">How many items each holds, for the column beside the name.</param>
    /// <param name="open">What Enter on a row does.</param>
    Task OpenScrapbookIndexAsync(
        IReadOnlyList<string> names,
        IReadOnlyDictionary<string, string> sizes,
        Func<string, Task> open);
}
