using Fjord.Core.Diagnostics;
using Fjord.Core.FileSystem;
using Fjord.Core.Panes;

namespace Fjord.Commands;

/// <summary>
/// Narrowing the current listing by name.
/// </summary>
/// <remarks>
/// This is a <b>filter</b>, not a jump-to-next-match. A file manager's list is already the
/// result set, so hiding what does not match is more useful than moving the cursor through
/// a haystack — and it composes with everything else, since operators and selection then
/// act on exactly what is visible.
/// </remarks>
public sealed class SearchCommands
{
    private readonly IModeHost _host;

    public SearchCommands(IModeHost host) => _host = host;

    [Command("search", Aliases = ["filter"], Group = "Search", MaxSplit = 0,
        Description = "Narrow the listing to names matching a pattern")]
    public void Search(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages,
        [CommandArgument(Description = "Fuzzy pattern; empty clears the filter")] string pattern = "")
    {
        var trimmed = pattern.Trim();

        pane.SetOrder(pane.Sort, pane.Filter with
        {
            NameContains = trimmed.Length == 0 ? null : trimmed,
            Fuzzy = true,
        });

        if (trimmed.Length == 0)
        {
            messages.Info("Filter cleared.");
            return;
        }

        var shown = pane.Snapshot?.View.Count ?? 0;
        var total = pane.Snapshot?.TotalCount ?? 0;

        messages.Message(
            shown == 0 ? MessageLevel.Warning : MessageLevel.Info,
            shown == 0
                ? $"No match for '{trimmed}'."
                : $"{shown} of {total} match '{trimmed}'.");
    }

    /// <summary>
    /// Searches everything beneath the current directory and shows what it found.
    /// </summary>
    /// <remarks>
    /// A separate command rather than a flag on <c>:search</c>, because it is a different
    /// kind of thing: <c>:search</c> narrows what is already on screen and reacts instantly,
    /// this walks the disk and can take a while over a large tree. Giving them one name
    /// would mean one of the two behaved surprisingly.
    /// <para>
    /// Results arrive as a listing rooted at the directory searched, so Enter, the cursor
    /// and every operator work on them exactly as they would on real entries — the rows
    /// just happen to read as paths.
    /// </para>
    /// </remarks>
    [Command("find", Aliases = ["search-recursive"], Group = "Search", MaxSplit = 0,
        Description = "Search this directory and everything under it")]
    public async Task Find(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages,
        [CommandArgument(Description = "Fuzzy pattern matched against each name")] string pattern = "")
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(pattern);

        var trimmed = pattern.Trim();

        if (trimmed.Length == 0)
        {
            messages.Error("Nothing to find. Give :find a pattern.");
            return;
        }

        var root = pane.Directory;

        if (pane.IsVirtual || ArchivePath.IsInside(root) || !Directory.Exists(root))
        {
            messages.Error("Can only search a real directory.");
            return;
        }

        messages.Info($"Searching {root} for '{trimmed}'…");

        // Off the UI thread: the walk is synchronous and can run for seconds over a deep
        // tree, and a file manager that stops repainting while it looks for something is
        // indistinguishable from one that has hung.
        var results = await Task.Run(
            () => RecursiveSearch.Find(root, trimmed, pane.Filter.ShowHidden))
            .ConfigureAwait(true);

        if (results.Paths.Count == 0)
        {
            messages.Message(MessageLevel.Warning, $"Nothing under {root} matches '{trimmed}'.");
            return;
        }

        await pane.ShowListingAsync($"find:{trimmed}", results.Paths, notes: null, root)
            .ConfigureAwait(true);

        messages.Message(
            results.Truncated ? MessageLevel.Warning : MessageLevel.Info,
            results.Truncated
                ? $"Showing the first {results.Paths.Count} matches for '{trimmed}'; narrow the pattern to see the rest."
                : $"{results.Paths.Count} of {results.Examined} match '{trimmed}'.");
    }

    /// <remarks>
    /// Opens the command line pre-filled, so <c>/</c> feels like vim's search even though
    /// what it runs is an ordinary command. Keeping it a command means it is bindable,
    /// scriptable and appears in <c>:help</c> like everything else.
    /// </remarks>
    [Command("search-open", Group = "Search", Description = "Open the command line to search")]
    public void SearchOpen() => _host.EnterCommandLine(":search ");

    [Command("search-clear", Group = "Search", Description = "Clear the name filter")]
    public void SearchClear(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        if (!pane.Filter.HasNameFilter)
        {
            return;
        }

        // Keep the cursor on whatever it was on; the entry is usually still visible once
        // the rest of the directory comes back.
        var name = pane.CursorName;
        pane.SetOrder(pane.Sort, pane.Filter with { NameContains = null });

        if (name is not null)
        {
            pane.MoveCursorToName(name);
        }

        messages.Info("Filter cleared.");
    }
}
