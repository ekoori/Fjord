using Fjord.Core.FileSystem;
using Fjord.Core.Navigation;
using Fjord.Core.Selection;

namespace Fjord.Core.Panes;

/// <summary>
/// One directory listing the user can act on.
/// </summary>
/// <remarks>
/// Replaces QuteSharp's <c>IBrowserTab</c> as the seam commands are written against.
/// Deliberately an interface in Core, so command bodies are testable against a fake with
/// no Avalonia anywhere in the picture.
/// </remarks>
public interface IPane
{
    string Directory { get; }

    DirectorySnapshot? Snapshot { get; }

    /// <summary>Row the cursor is on. Setting it clamps to the list and scrolls into view.</summary>
    int CursorIndex { get; set; }

    /// <summary>
    /// Row visual mode was entered on, or null outside visual mode.
    /// </summary>
    /// <remarks>
    /// The highlighted span is derived from this and the cursor at paint time, and is only
    /// written into <see cref="Selection"/> when an operator runs. That is what lets
    /// <c>v3j</c> then Escape leave the committed selection untouched.
    /// </remarks>
    int? VisualAnchor { get; set; }

    SelectionSet Selection { get; }

    SortSpec Sort { get; }

    FilterSpec Filter { get; }

    NavigationHistory History { get; }

    /// <summary>
    /// Rows that fit in the viewport, for <c>Ctrl-d</c> and page motions.
    /// </summary>
    /// <remarks>
    /// The one place view knowledge is allowed into the model. It makes paging a model
    /// operation and therefore unit-testable, which is worth the leak.
    /// </remarks>
    int PageSize { get; }

    /// <summary>The range visual mode currently covers, or null when not in visual mode.</summary>
    EntryRange? VisualRange { get; }

    /// <summary>
    /// Whether the listing is a made-up one — a scrapbook — rather than a directory.
    /// </summary>
    /// <remarks>
    /// A virtual listing has no parent to go up to and nothing to reload, so the keys that
    /// leave a directory have to mean "leave this view" instead. Without it a scrapbook is
    /// a room with no door.
    /// </remarks>
    bool IsVirtual { get; }

    /// <summary>
    /// Returns from a virtual listing to the directory that was showing before it.
    /// </summary>
    /// <returns>False when there was nothing to leave.</returns>
    Task<bool> LeaveVirtualAsync();

    /// <summary>
    /// The directory a session should record for this pane.
    /// </summary>
    /// <remarks>
    /// The same as <see cref="Directory"/> except while a virtual listing is showing, when
    /// that is a label rather than a path. Saving the label produced a tab that could not be
    /// restored, and on Windows its colon made path normalisation throw and took the rest of
    /// the restore with it.
    /// </remarks>
    string RestorableDirectory { get; }

    /// <summary>
    /// Opens the row of a virtual listing whose entries are not paths.
    /// </summary>
    /// <returns>False when this listing's rows are ordinary paths.</returns>
    /// <remarks>
    /// What makes the list of scrapbooks navigable: its rows are names, so Enter has to ask
    /// the thing that built the listing what opening one means rather than treating the
    /// name as a directory.
    /// </remarks>
    Task<bool> OpenVirtualAsync(int row);

    void MoveCursor(int delta);

    void MoveCursorTo(int row);

    /// <summary>Moves the cursor to a named entry. False when it is not in the current view.</summary>
    bool MoveCursorToName(string name);

    /// <summary>The name under the cursor, or null when the list is empty.</summary>
    string? CursorName { get; }

    /// <summary>Full path of a row, or null when out of range.</summary>
    string? PathAt(int row);

    Task NavigateAsync(string directory, string? selectName = null);

    Task ReloadAsync();

    Task BackAsync(int count = 1);

    Task ForwardAsync(int count = 1);

    void SetOrder(SortSpec sort, FilterSpec filter);
}

public enum NewTabPosition
{
    AfterCurrent,
    End,
}

/// <summary>A tab, owning one or more panes once split views arrive.</summary>
public interface ITab
{
    IPane Active { get; }

    IReadOnlyList<IPane> Panes { get; }

    string Title { get; }

    /// <summary>
    /// Whether this tab stays where it is.
    /// </summary>
    /// <remarks>
    /// Navigating out of a locked tab opens a new one at the destination and moves to it,
    /// so the tab keeps showing what it was pinned to and the user keeps browsing without
    /// having to remember to open a tab first. Locking the place you keep coming back to is
    /// the point — it is a bookmark you can already see.
    /// </remarks>
    bool IsLocked { get; set; }
}

/// <summary>The window's set of tabs.</summary>
public interface IWorkspace
{
    IReadOnlyList<ITab> Tabs { get; }

    int CurrentIndex { get; set; }

    ITab Current { get; }

    ITab OpenTab(string directory, NewTabPosition position = NewTabPosition.AfterCurrent, bool background = false);

    void CloseTab(int index);

    /// <summary>Reopens the most recently closed tab. False when there is none.</summary>
    bool UndoClose();

    void MoveTab(int from, int to);

    void Quit();
}
