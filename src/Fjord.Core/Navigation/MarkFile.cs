namespace Fjord.Core.Navigation;

/// <summary>A remembered place: a directory, and the entry the cursor was on.</summary>
/// <param name="CursorName">
/// Stored by name, not by row. A row is invalidated by a resort, a filter change, or a
/// file arriving while you were away; a name survives all three.
/// </param>
public readonly record struct Mark(string Directory, string? CursorName);

/// <summary>
/// Directories remembered under a name, set with <c>:mark-set</c> and returned to with
/// <c>:cd</c>.
/// </summary>
/// <remarks>
/// Named rather than keyed to a single character. A single letter is the vim spelling and
/// it suits a mark you set, jump from and forget within a minute — but these are the three
/// or four places a piece of work keeps returning to, kept for weeks, and <c>work</c> is
/// worth typing where <c>w</c> has to be remembered.
/// <para>
/// It also lets them share a namespace with directories: <c>:cd work</c> tries the
/// quickmarks first and a real path second, so one command covers both and neither needs
/// its own key.
/// </para>
/// <para>
/// These are session state rather than settings. They live in the session database next to
/// the open tabs, because they record where you have been rather than how you want the app
/// to behave.
/// </para>
/// </remarks>
public sealed class MarkFile
{
    private readonly Dictionary<string, Mark> _marks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Marks set, in name order.</summary>
    public IReadOnlyList<string> Names => [.. _marks.Keys.OrderBy(name => name, StringComparer.Ordinal)];

    /// <summary>Reads a mark, or null when nothing is stored under that name.</summary>
    public Mark? Get(string name) =>
        !string.IsNullOrEmpty(name) && _marks.TryGetValue(name, out var mark) ? mark : null;

    /// <summary>
    /// Whether a name is usable as a quickmark.
    /// </summary>
    /// <remarks>
    /// One word, no separators. A name containing a slash or a dot would be ambiguous with
    /// the path <c>:cd</c> falls back to, and a quickmark that shadowed <c>..</c> would be
    /// a trap the user set for themselves without meaning to.
    /// </remarks>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_')
        && !name.All(char.IsDigit);

    /// <summary>
    /// Sets a mark, replacing whatever that name held.
    /// </summary>
    /// <remarks>
    /// Silently replacing is the vim behaviour and the right one here too: re-marking a
    /// place you have moved is the common case, and a confirmation prompt on every one
    /// would make marks not worth setting.
    /// </remarks>
    public void Set(string name, string directory, string? cursorName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);

        if (!IsValidName(name))
        {
            throw new ArgumentException(
                "A quickmark name is one word of letters, digits, - or _.", nameof(name));
        }

        _marks[name] = new Mark(directory, cursorName);
    }

    /// <summary>Forgets one mark, or every mark when no name is given.</summary>
    public void Clear(string? name = null)
    {
        if (name is { Length: > 0 })
        {
            _marks.Remove(name);
        }
        else
        {
            _marks.Clear();
        }
    }

    /// <summary>Replaces the whole set, as restoring a session does.</summary>
    public void Load(IEnumerable<KeyValuePair<string, Mark>> marks)
    {
        ArgumentNullException.ThrowIfNull(marks);

        _marks.Clear();

        foreach (var (name, mark) in marks)
        {
            _marks[name] = mark;
        }
    }

    /// <summary>Every mark, for saving.</summary>
    public IReadOnlyDictionary<string, Mark> All => _marks;
}
