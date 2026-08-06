using Fjord.Core.Panes;

namespace Fjord.Commands;

/// <summary>What a command with no motion of its own should act on.</summary>
/// <remarks>
/// Three sources, in the order a user means them. A visual range wins because being in
/// visual mode is a statement about what you are pointing at right now — it is what
/// <c>:</c> from visual mode is for, and vim resolves the same way. A committed Space
/// selection comes next, and the entry under the cursor last so a key always does
/// something.
/// <para>
/// Shared rather than reimplemented per command class, because the three had drifted:
/// <c>:scrap-remove</c> could not see a visual range at all, which is what made it look
/// broken from visual mode.
/// </para>
/// </remarks>
public static class PaneTargets
{
    public static List<string> Of(IPane pane)
    {
        ArgumentNullException.ThrowIfNull(pane);

        var count = pane.Snapshot?.View.Count ?? 0;

        if (pane.VisualRange is { } visual && visual.Clamp(count) is { } clamped)
        {
            return clamped.Rows().Select(pane.PathAt).Where(p => p is not null).ToList()!;
        }

        if (pane.Selection.Count > 0)
        {
            return pane.Selection.Indices().Select(pane.PathAt).Where(p => p is not null).ToList()!;
        }

        return pane.PathAt(pane.CursorIndex) is { } path ? [path] : [];
    }
}
