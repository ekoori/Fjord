namespace Fjord.Core.FileSystem;

/// <summary>
/// A consistent, immutable window onto a <see cref="DirectorySnapshot"/>.
/// </summary>
/// <remarks>
/// The renderer captures one of these per frame with a single volatile read and then
/// indexes it without locking. Both arrays are captured together on purpose: reading the
/// entry array and the view array separately could pair a new view with an older, shorter
/// entry array and index out of range.
/// </remarks>
public readonly struct SnapshotView
{
    private readonly FileEntry[] _entries;
    private readonly int[] _view;

    internal SnapshotView(FileEntry[] entries, int[] view)
    {
        _entries = entries;
        _view = view;
    }

    /// <summary>Number of visible rows.</summary>
    public int Count => _view?.Length ?? 0;

    public bool IsEmpty => Count == 0;

    /// <summary>The entry at a row. Returned by reference to avoid copying 32 bytes per access.</summary>
    public ref readonly FileEntry this[int row] => ref _entries[_view[row]];

    /// <summary>
    /// The arrival-order index behind a row.
    /// </summary>
    /// <remarks>
    /// Background metadata must be keyed by this, never by the row: a resort or a filter
    /// change renumbers every row, and results arriving afterwards would land on the
    /// wrong entries.
    /// </remarks>
    public int SourceIndex(int row) => _view[row];

    /// <summary>
    /// Whether this and <paramref name="other"/> are backed by the same published arrays.
    /// </summary>
    /// <remarks>
    /// The test a renderer needs to know whether cached per-row state is still valid.
    /// Comparing <see cref="Count"/> is not enough: a re-sort or a filter change replaces
    /// every row's content while leaving the count identical, and a cache keyed on count
    /// alone would happily redraw the old order.
    /// </remarks>
    public bool IsSameAs(SnapshotView other) =>
        ReferenceEquals(_view, other._view) && ReferenceEquals(_entries, other._entries);

    /// <summary>Finds the row showing a given name, or -1.</summary>
    /// <remarks>
    /// Navigation history restores the cursor by name rather than index precisely because
    /// indices do not survive a resort, a filter change, or files appearing while the
    /// user was elsewhere.
    /// </remarks>
    public int IndexOfName(string name, StringComparison comparison)
    {
        for (var row = 0; row < Count; row++)
        {
            if (string.Equals(this[row].Name, name, comparison))
            {
                return row;
            }
        }

        return -1;
    }
}

/// <summary>
/// The accumulated contents of one directory, plus the sorted and filtered view of them.
/// </summary>
/// <remarks>
/// <b>Threading contract:</b> <see cref="Append"/>, <see cref="Complete"/> and
/// <see cref="SetOrder"/> are called from exactly one producer thread. Any number of
/// threads may read <see cref="View"/>. Published arrays are never mutated — an update
/// builds new ones and swaps them in — which is what lets the renderer read without a
/// lock.
/// </remarks>
public sealed class DirectorySnapshot
{
    private readonly Lock _publishLock = new();

    private FileEntry[] _entries = [];
    private int _count;
    private SortSpec _sort;
    private FilterSpec _filter;
    private Published _published;

    public DirectorySnapshot(string directory, SortSpec? sort = null, FilterSpec? filter = null)
    {
        Directory = directory;
        _sort = sort ?? SortSpec.Default;
        _filter = filter ?? FilterSpec.Default;
        _published = new Published([], []);
    }

    public string Directory { get; }

    /// <summary>
    /// A note per entry, keyed by name, shown in its own column.
    /// </summary>
    /// <remarks>
    /// A side map rather than a field on <see cref="FileEntry"/>, which is deliberately 32
    /// bytes: a 100,000-entry directory would pay for a column it never uses. Null is the
    /// normal case and costs a reference.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? Annotations { get; init; }

    /// <summary>
    /// Measured sizes for directories, by name, filled in on request.
    /// </summary>
    /// <remarks>
    /// Mutable and shared rather than replaced, because it is written to as a background
    /// walk finishes each directory and read by the paint that follows. A dictionary
    /// swapped wholesale per result would allocate one per directory in the listing.
    /// </remarks>
    public Dictionary<string, long> DirectorySizes { get; } = new(StringComparer.Ordinal);

    public SortSpec Sort => _sort;

    public FilterSpec Filter => _filter;

    /// <summary>True once enumeration finished. False while still loading, and after a failure.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>Set when enumeration ended in an error rather than running out of entries.</summary>
    public Exception? Error { get; private set; }

    /// <summary>Entries seen so far, before filtering.</summary>
    public int TotalCount => Volatile.Read(ref _count);

    /// <summary>The current consistent view. One volatile read; safe to hold for a frame.</summary>
    public SnapshotView View
    {
        get
        {
            var published = Volatile.Read(ref _published);
            return new SnapshotView(published.Entries, published.View);
        }
    }

    /// <summary>
    /// Adds a batch of newly enumerated entries and republishes the view.
    /// </summary>
    /// <remarks>
    /// The batch is sorted on its own and then merged into the existing view, rather than
    /// binary-search-inserted entry by entry. Insertion would shift the tail of a 100,000
    /// element array 2,048 times per batch; the merge touches each element once.
    /// </remarks>
    public void Append(ReadOnlySpan<FileEntry> batch)
    {
        if (batch.IsEmpty)
        {
            return;
        }

        EnsureCapacity(_count + batch.Length);

        var firstNew = _count;
        batch.CopyTo(_entries.AsSpan(firstNew));
        var newCount = firstNew + batch.Length;

        // Collect the rows this batch contributes, then order them among themselves.
        var added = new List<int>(batch.Length);
        for (var i = firstNew; i < newCount; i++)
        {
            if (_filter.IsEmpty || _filter.Matches(in _entries[i]))
            {
                added.Add(i);
            }
        }

        var entries = _entries;
        var comparer = _sort.CreateComparer(_filter);
        var addedArray = added.ToArray();
        Array.Sort(addedArray, (a, b) => comparer.Compare(entries[a], entries[b]));

        var previous = Volatile.Read(ref _published).View;
        var merged = Merge(previous, addedArray, entries, comparer);

        // _count is published together with the arrays: a reader that sees the new view
        // must also see the entries it points at.
        Volatile.Write(ref _count, newCount);
        Publish(entries, merged);
    }

    /// <summary>Re-sorts and re-filters everything. Used when the user changes sort or filter.</summary>
    public void SetOrder(SortSpec sort, FilterSpec filter)
    {
        _sort = sort;
        _filter = filter;
        Rebuild();
    }

    /// <summary>Marks enumeration as finished.</summary>
    public void Complete(Exception? error = null)
    {
        Error = error;
        IsComplete = true;
    }

    private void Rebuild()
    {
        var entries = _entries;
        var count = _count;
        var comparer = _sort.CreateComparer(_filter);

        var view = new List<int>(count);
        for (var i = 0; i < count; i++)
        {
            if (_filter.IsEmpty || _filter.Matches(in entries[i]))
            {
                view.Add(i);
            }
        }

        var array = view.ToArray();
        Array.Sort(array, (a, b) => comparer.Compare(entries[a], entries[b]));
        Publish(entries, array);
    }

    private void Publish(FileEntry[] entries, int[] view)
    {
        // The lock orders concurrent publishes; the volatile write is what makes the
        // element writes above visible to readers before the arrays are.
        lock (_publishLock)
        {
            Volatile.Write(ref _published, new Published(entries, view));
        }
    }

    /// <summary>
    /// Grows the entry array, never in place past what has already been published.
    /// </summary>
    /// <remarks>
    /// Growing allocates a new array and copies, so a reader still holding the previous
    /// one keeps a valid, if shorter, snapshot. Writing into spare capacity beyond the
    /// published count is safe for the same reason: no published view references it.
    /// </remarks>
    private void EnsureCapacity(int required)
    {
        if (_entries.Length >= required)
        {
            return;
        }

        var capacity = Math.Max(_entries.Length == 0 ? 1024 : _entries.Length * 2, required);
        var grown = new FileEntry[capacity];
        Array.Copy(_entries, grown, _count);
        _entries = grown;
    }

    private static int[] Merge(int[] existing, int[] added, FileEntry[] entries, IComparer<FileEntry> comparer)
    {
        if (added.Length == 0)
        {
            return existing;
        }

        if (existing.Length == 0)
        {
            return added;
        }

        var merged = new int[existing.Length + added.Length];
        int a = 0, b = 0, m = 0;

        while (a < existing.Length && b < added.Length)
        {
            merged[m++] = comparer.Compare(entries[added[b]], entries[existing[a]]) < 0
                ? added[b++]
                : existing[a++];
        }

        while (a < existing.Length)
        {
            merged[m++] = existing[a++];
        }

        while (b < added.Length)
        {
            merged[m++] = added[b++];
        }

        return merged;
    }

    private sealed record Published(FileEntry[] Entries, int[] View);
}
