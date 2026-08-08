namespace Fjord.Commands;

/// <summary>
/// The command line's history, walked with Up and Down.
/// </summary>
/// <remarks>
/// Walking is <b>prefix-filtered</b>: typing <c>cd</c> then pressing Up offers only past
/// commands starting with <c>cd</c>. That is what makes history useful once it is more
/// than a few entries long, and it is why the prefix is captured when the walk starts
/// rather than re-read on each step — the text is being replaced as you go.
/// </remarks>
public sealed class CommandHistory
{
    public const int DefaultCapacity = 100;

    private readonly List<string> _entries = [];

    /// <summary>Position in the walk, counted back from the newest. -1 means "not walking".</summary>
    private int _position = -1;
    private string _prefix = "";
    private string? _draft;

    public CommandHistory(int capacity = DefaultCapacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be at least 1.");
        }

        Capacity = capacity;
    }

    public int Capacity { get; }

    public IReadOnlyList<string> Entries => _entries;

    public bool IsWalking => _position >= 0;

    /// <summary>
    /// Where the walk currently sits, as an index into <see cref="Entries"/>.
    /// </summary>
    /// <remarks>
    /// Null when not walking, which the panel shows as "at the end": the newest entries
    /// are on screen and nothing is highlighted, because the user is typing rather than
    /// choosing.
    /// </remarks>
    public int? CurrentIndex => _position < 0 ? null : _entries.Count - 1 - _position;

    /// <summary>
    /// Replaces the whole history, newest last.
    /// </summary>
    /// <remarks>
    /// Used when loading from disk. Capacity is applied here as well as on add, so a file
    /// that grew under an older, larger limit is trimmed on the way in rather than kept
    /// forever.
    /// </remarks>
    public void Load(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        _entries.Clear();
        _entries.AddRange(entries.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()));

        if (_entries.Count > Capacity)
        {
            _entries.RemoveRange(0, _entries.Count - Capacity);
        }

        Reset();
    }

    /// <summary>
    /// Records a command that was run.
    /// </summary>
    /// <remarks>
    /// Blank lines are ignored, and repeating the most recent command does not add a
    /// second copy — pressing Up after running the same thing twice should step past it,
    /// not through it.
    /// </remarks>
    public void Add(string line)
    {
        Reset();

        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        if (_entries.Count > 0 && string.Equals(_entries[^1], trimmed, StringComparison.Ordinal))
        {
            return;
        }

        _entries.Add(trimmed);

        if (_entries.Count > Capacity)
        {
            _entries.RemoveRange(0, _entries.Count - Capacity);
        }
    }

    /// <summary>
    /// Steps back through entries matching the current prefix.
    /// </summary>
    /// <param name="current">
    /// What is in the command line now. On the first step this becomes the prefix to
    /// filter by, and is remembered so <see cref="Next"/> can restore it.
    /// </param>
    /// <returns>The entry to show, or null when there is nothing further back.</returns>
    public string? Previous(string current)
    {
        if (!IsWalking)
        {
            _prefix = current;
            _draft = current;
        }

        for (var i = _position + 1; i < _entries.Count; i++)
        {
            var candidate = _entries[_entries.Count - 1 - i];
            if (candidate.StartsWith(_prefix, StringComparison.Ordinal))
            {
                _position = i;
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Steps forward again, ending with the text that was being typed before the walk.
    /// </summary>
    public string? Next()
    {
        if (!IsWalking)
        {
            return null;
        }

        for (var i = _position - 1; i >= 0; i--)
        {
            var candidate = _entries[_entries.Count - 1 - i];
            if (candidate.StartsWith(_prefix, StringComparison.Ordinal))
            {
                _position = i;
                return candidate;
            }
        }

        // Walked back past the newest match: restore the half-typed line rather than
        // leaving the user staring at someone else's command.
        _position = -1;
        var draft = _draft;
        _draft = null;
        return draft ?? "";
    }

    /// <summary>Abandons a walk, so the next Up starts from the newest entry again.</summary>
    public void Reset()
    {
        _position = -1;
        _prefix = "";
        _draft = null;
    }

    public void Clear()
    {
        _entries.Clear();
        Reset();
    }
}
