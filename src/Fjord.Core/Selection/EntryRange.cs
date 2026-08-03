namespace Fjord.Core.Selection;

/// <summary>
/// A contiguous span of rows, inclusive at both ends.
/// </summary>
/// <remarks>
/// What an operator acts on, whether it came from a motion (<c>d3j</c>) or from a visual
/// selection. Rows, not source indices: an operator acts on what the user can see, and the
/// range is resolved to entries immediately, before anything can reorder.
/// </remarks>
/// <param name="Linewise">
/// Whole rows rather than a partial span. Always true today — a file list has no
/// sub-row positions — but operators are written against it so character-wise motions in
/// a rename field can reuse the same shape.
/// </param>
public readonly record struct EntryRange(int Start, int End, bool Linewise = true)
{
    /// <summary>Builds a range from two rows in either order.</summary>
    public static EntryRange Between(int a, int b, bool linewise = true) =>
        a <= b ? new EntryRange(a, b, linewise) : new EntryRange(b, a, linewise);

    public static EntryRange Single(int row) => new(row, row);

    public int Count => End - Start + 1;

    public bool Contains(int row) => row >= Start && row <= End;

    /// <summary>Clamps to a list of <paramref name="rowCount"/> rows, or null when disjoint from it.</summary>
    public EntryRange? Clamp(int rowCount)
    {
        if (rowCount <= 0 || Start >= rowCount || End < 0)
        {
            return null;
        }

        return new EntryRange(Math.Max(0, Start), Math.Min(rowCount - 1, End), Linewise);
    }

    public IEnumerable<int> Rows()
    {
        for (var row = Start; row <= End; row++)
        {
            yield return row;
        }
    }
}
