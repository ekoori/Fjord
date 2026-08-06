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
