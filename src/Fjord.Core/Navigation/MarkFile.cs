namespace Fjord.Core.Navigation;

/// <summary>A remembered place: a directory, and the entry the cursor was on.</summary>
/// <param name="CursorName">
/// Stored by name, not by row. A row is invalidated by a resort, a filter change, or a
/// file arriving while you were away; a name survives all three.
/// </param>
public readonly record struct Mark(string Directory, string? CursorName);

/// <summary>
/// Places remembered under a single character, set with <c>m</c> and returned to with
/// <c>'</c>.
/// </summary>
/// <remarks>
/// The file-manager reading of a vim mark. In an editor a mark is a position in a buffer;
/// here the buffer is the filesystem, so a mark is a directory — which makes it the fast
/// way back to the three or four places a piece of work keeps returning to, without
/// spending a bookmark on something you will not want tomorrow.
/// <para>
/// Marks are session state, not settings. They live in the session database next to the
/// open tabs rather than in <c>config.yaml</c>, because they record where you have been
/// rather than how you want the app to behave.
/// </para>
/// </remarks>
public sealed class MarkFile
{
    private readonly Dictionary<char, Mark> _marks = [];

    /// <summary>Marks set, in name order.</summary>
    public IReadOnlyList<char> Names => [.. _marks.Keys.OrderBy(name => name)];

    /// <summary>Reads a mark, or null when that character holds nothing.</summary>
    public Mark? Get(char name) => _marks.TryGetValue(name, out var mark) ? mark : null;

    /// <summary>
    /// Sets a mark, replacing whatever that character held.
    /// </summary>
    /// <remarks>
    /// Silently replacing is the vim behaviour and the right one: a mark is a scratch
    /// position, and a confirmation prompt on every <c>ma</c> would make them not worth
    /// using.
    /// </remarks>
    public void Set(char name, string directory, string? cursorName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _marks[name] = new Mark(directory, cursorName);
    }

    /// <summary>Forgets one mark, or every mark when no name is given.</summary>
    public void Clear(char? name = null)
    {
        if (name is { } one)
        {
            _marks.Remove(one);
        }
        else
        {
            _marks.Clear();
        }
    }

    /// <summary>Replaces the whole set, as restoring a session does.</summary>
    public void Load(IEnumerable<KeyValuePair<char, Mark>> marks)
    {
        ArgumentNullException.ThrowIfNull(marks);

        _marks.Clear();

        foreach (var (name, mark) in marks)
        {
            _marks[name] = mark;
        }
    }

    /// <summary>Every mark, for saving.</summary>
    public IReadOnlyDictionary<char, Mark> All => _marks;
}
