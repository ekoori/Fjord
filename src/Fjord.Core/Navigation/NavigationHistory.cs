namespace Fjord.Core.Navigation;

/// <summary>
/// One visited directory.
/// </summary>
/// <param name="CursorName">
/// The name the cursor was on, not its row. Rows do not survive a resort, a filter
/// change, or files appearing and disappearing while the user was elsewhere; a name
/// does. Null means "no particular entry".
/// </param>
public readonly record struct HistoryEntry(string Directory, string? CursorName, DateTime VisitedUtc);

/// <summary>
/// Per-pane back/forward history, driving <c>Ctrl-o</c> and <c>Ctrl-i</c>.
/// </summary>
/// <remarks>
/// A browser-style linear history with a cursor: pushing after going back discards the
/// forward tail. A separate global recents list, which does keep everything, arrives with
/// the SQLite work in a later milestone.
/// </remarks>
public sealed class NavigationHistory
{
    public const int DefaultCapacity = 200;

    private readonly List<HistoryEntry> _entries = [];
    private int _index = -1;

    public NavigationHistory(int capacity = DefaultCapacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be at least 1.");
        }

        Capacity = capacity;
    }

    public int Capacity { get; }

    public int Count => _entries.Count;

    public IReadOnlyList<HistoryEntry> Entries => _entries;

    /// <summary>Position in <see cref="Entries"/>, or -1 when empty.</summary>
    public int Position => _index;

    public bool CanGoBack => _index > 0;

    public bool CanGoForward => _index >= 0 && _index < _entries.Count - 1;

    public HistoryEntry? Current => _index >= 0 ? _entries[_index] : null;

    /// <summary>
    /// Records a newly visited directory.
    /// </summary>
    /// <remarks>
    /// Navigating somewhere new after going back discards what was ahead, exactly as a
    /// browser does. Re-entering the directory already at the cursor only refreshes its
    /// remembered cursor name, so pressing Enter on <c>..</c> and coming back does not
    /// fill the history with duplicates.
    /// </remarks>
    public void Push(string directory, string? cursorName = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(directory);

        var now = (time ?? TimeProvider.System).GetUtcNow().UtcDateTime;

        if (_index >= 0 && string.Equals(_entries[_index].Directory, directory, StringComparison.Ordinal))
        {
            _entries[_index] = _entries[_index] with { CursorName = cursorName, VisitedUtc = now };
            return;
        }

        if (CanGoForward)
        {
            _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);
        }

        _entries.Add(new HistoryEntry(directory, cursorName, now));
        _index = _entries.Count - 1;

        Trim();
    }

    /// <summary>
    /// Records where the cursor is, so returning here restores it.
    /// </summary>
    /// <remarks>
    /// Called just before leaving a directory. Without it, back-navigation lands on row 0
    /// rather than where the user actually was.
    /// </remarks>
    public void UpdateCursor(string? cursorName)
    {
        if (_index >= 0)
        {
            _entries[_index] = _entries[_index] with { CursorName = cursorName };
        }
    }

    /// <summary>Moves back up to <paramref name="count"/> entries. Null when already at the oldest.</summary>
    public HistoryEntry? Back(int count = 1)
    {
        if (count < 1 || !CanGoBack)
        {
            return null;
        }

        _index = Math.Max(0, _index - count);
        return _entries[_index];
    }

    /// <summary>Moves forward up to <paramref name="count"/> entries. Null when already at the newest.</summary>
    public HistoryEntry? Forward(int count = 1)
    {
        if (count < 1 || !CanGoForward)
        {
            return null;
        }

        _index = Math.Min(_entries.Count - 1, _index + count);
        return _entries[_index];
    }

    public void Clear()
    {
        _entries.Clear();
        _index = -1;
    }

    private void Trim()
    {
        var excess = _entries.Count - Capacity;
        if (excess <= 0)
        {
            return;
        }

        _entries.RemoveRange(0, excess);
        _index -= excess;
    }
}
