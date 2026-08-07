using Fjord.Core.FileSystem;

namespace Fjord.Config;

/// <summary>One saved view of a directory, under a name.</summary>
/// <remarks>
/// Every field, not a patch. A listing is a complete state, so <c>:sort dir_asc</c> gives
/// exactly the same view whatever it was showing before — which is what makes cycling
/// through them predictable rather than dependent on where you started.
/// </remarks>
public sealed class ListingPreset
{
    public SortKey SortKey { get; set; } = SortKey.Name;

    public bool SortDescending { get; set; }

    public bool DirectoriesFirst { get; set; } = true;

    public bool NaturalSort { get; set; } = true;

    public bool ShowHidden { get; set; }

    public SortSpec ToSortSpec() => new(SortKey, SortDescending, DirectoriesFirst, NaturalSort);

    public FilterSpec ToFilterSpec() => new(ShowHidden);

    /// <summary>Captures what a pane is currently showing.</summary>
    public static ListingPreset From(SortSpec sort, FilterSpec filter)
    {
        ArgumentNullException.ThrowIfNull(sort);
        ArgumentNullException.ThrowIfNull(filter);

        return new ListingPreset
        {
            SortKey = sort.Key,
            SortDescending = sort.Descending,
            DirectoriesFirst = sort.DirectoriesFirst,
            NaturalSort = sort.Natural,
            ShowHidden = filter.ShowHidden,
        };
    }

    /// <summary>Whether this describes the same view as a pane's current one.</summary>
    /// <remarks>
    /// How cycling finds its place: <c>:sort</c> with no name moves to the one after
    /// whichever listing matches what is on screen, so it continues from where the user is
    /// rather than from wherever the cycle last stopped.
    /// </remarks>
    public bool Matches(SortSpec sort, FilterSpec filter)
    {
        ArgumentNullException.ThrowIfNull(sort);
        ArgumentNullException.ThrowIfNull(filter);

        return SortKey == sort.Key
            && SortDescending == sort.Descending
            && DirectoriesFirst == sort.DirectoriesFirst
            && NaturalSort == sort.Natural
            && ShowHidden == filter.ShowHidden;
    }
}

/// <summary>
/// The shipped listings, written into a fresh configuration file.
/// </summary>
/// <remarks>
/// Ascending and descending for each of the three orders anyone actually sorts by. The
/// pairing is the point: <c>:sort</c> with no argument steps through them, so one key
/// alternates a column's direction and then moves on to the next column.
/// </remarks>
public static class DefaultListings
{
    public static IReadOnlyDictionary<string, ListingPreset> All { get; } =
        new Dictionary<string, ListingPreset>(StringComparer.OrdinalIgnoreCase)
        {
            ["dir_asc"] = new() { SortKey = SortKey.Name },
            ["dir_desc"] = new() { SortKey = SortKey.Name, SortDescending = true },
            ["siz_asc"] = new() { SortKey = SortKey.Size },
            ["siz_desc"] = new() { SortKey = SortKey.Size, SortDescending = true },
            ["mod_asc"] = new() { SortKey = SortKey.Modified },
            ["mod_desc"] = new() { SortKey = SortKey.Modified, SortDescending = true },
        };

    /// <summary>
    /// The order <c>:sort</c> cycles in.
    /// </summary>
    /// <remarks>
    /// Insertion order for the shipped set, and name order for anything the user added —
    /// a dictionary from YAML has no meaningful order of its own, and a cycle that changed
    /// with the parser's whim would be unusable.
    /// </remarks>
    public static IReadOnlyList<string> Order(IReadOnlyDictionary<string, ListingPreset> listings)
    {
        ArgumentNullException.ThrowIfNull(listings);

        var shipped = All.Keys.Where(listings.ContainsKey);
        var added = listings.Keys.Where(k => !All.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal);

        return [.. shipped, .. added];
    }
}
