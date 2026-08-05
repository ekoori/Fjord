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
        var paths = SelectedOrCursor(pane);

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

        await _host.OpenScrapbookAsync(name, items.Select(i => i.Path).ToList()).ConfigureAwait(true);
    }

    [Command("scrap-remove", Group = "Scrapbook", MaxSplit = 0,
        Description = "Remove the selection from a scrapbook")]
    public void ScrapRemove(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages,
        string scrapbook = DefaultScrapbook)
    {
        var paths = SelectedOrCursor(pane);

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

    [Command("scrap-list", Group = "Scrapbook", Description = "List scrapbooks and their sizes")]
    public void ScrapList([CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        var names = _storage.ScrapbookNames();

        messages.Info(names.Count == 0
            ? "No scrapbooks."
            : string.Join("  ", names.Select(n => $"{n} ({_storage.Scrapbook(n).Count})")));
    }

    /// <summary>
    /// The committed selection, or the cursor row when nothing is selected.
    /// </summary>
    /// <remarks>
    /// The same rule the file operations use, so <c>:scrap-add</c> means what a user
    /// expects whether or not they have selected anything.
    /// </remarks>
    private static List<string> SelectedOrCursor(IPane pane)
    {
        if (pane.Selection.Count > 0)
        {
            return pane.Selection.Indices().Select(pane.PathAt).Where(p => p is not null).ToList()!;
        }

        return pane.PathAt(pane.CursorIndex) is { } path ? [path] : [];
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
    Task OpenScrapbookAsync(string name, IReadOnlyList<string> paths);
}
