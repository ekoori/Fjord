using Fjord.Core.Diagnostics;
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
    /// <summary>
    /// Refuses an operation that would change an archive, and says why.
    /// </summary>
    /// <returns>True when the pane is inside one and the caller should stop.</returns>
    /// <remarks>
    /// Archives are browsable but not writable, and the paths inside one do not exist on
    /// disk. Without this a trash or a paste would be queued against a path the filesystem
    /// has never heard of and fail somewhere further down with a message about a directory
    /// that is really a file — which tells the user nothing about what actually happened.
    /// </remarks>
    public static bool RefuseInsideArchive(IPane pane, IMessageSink messages, string verb)
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(messages);

        if (!Core.FileSystem.ArchivePath.IsInside(pane.Directory))
        {
            return false;
        }

        messages.Error($"Cannot {verb} inside an archive. :extract copies entries out.");
        return true;
    }

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
