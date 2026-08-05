using Fjord.Core.Diagnostics;
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
