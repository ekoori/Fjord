namespace Fjord.Core.FileSystem;

public enum SortKey
{
    Name,
    Size,
    Modified,
    Extension,
}

/// <summary>
/// How a directory listing is ordered.
/// </summary>
/// <param name="DirectoriesFirst">
/// Grouping is applied before the key, so descending order does not push directories to
/// the bottom.
/// </param>
/// <param name="Natural">
/// Whether to use <see cref="NaturalComparer"/> for names. Off gives plain ordinal
/// order, which is what a user comparing against <c>ls</c> output wants.
/// </param>
public sealed record SortSpec(
    SortKey Key = SortKey.Name,
    bool Descending = false,
    bool DirectoriesFirst = true,
    bool Natural = true)
{
    public static readonly SortSpec Default = new();

    /// <summary>
    /// Whether ordering needs <see cref="FileEntry.Size"/>.
    /// </summary>
    /// <remarks>
    /// This drives <see cref="ListingOptions.NeedsSize"/>. On Unix, reading a size costs
    /// a <c>stat</c> per entry, so sorting by name must not pay for sizes nobody asked
    /// for. On a network mount that is the difference between instant and unusable.
    /// </remarks>
    public bool NeedsSize => Key == SortKey.Size;

    /// <summary>Whether ordering needs <see cref="FileEntry.ModifiedUtcTicks"/>.</summary>
    public bool NeedsTimes => Key == SortKey.Modified;

    /// <summary>Builds the comparer this spec describes.</summary>
    public IComparer<FileEntry> CreateComparer() => new EntryComparer(this);

    private sealed class EntryComparer(SortSpec spec) : IComparer<FileEntry>
    {
        public int Compare(FileEntry x, FileEntry y)
        {
            if (spec.DirectoriesFirst)
            {
                // Part of the comparator rather than a second pass, so a single sort
                // produces the final order.
                var byKind = y.IsDirectory.CompareTo(x.IsDirectory);
                if (byKind != 0)
                {
                    return byKind;
                }
            }

            var result = CompareByKey(x, y);
            if (spec.Descending)
            {
                result = -result;
            }

            // Every key except Name can tie. Falling back to the name keeps the order
            // total and consistent with equality, which Array.Sort requires.
            return result != 0 ? result : CompareNames(x.Name, y.Name);
        }

        private int CompareByKey(FileEntry x, FileEntry y) => spec.Key switch
        {
            SortKey.Name => CompareNames(x.Name, y.Name),
            SortKey.Size => x.Size.CompareTo(y.Size),
            SortKey.Modified => x.ModifiedUtcTicks.CompareTo(y.ModifiedUtcTicks),
            SortKey.Extension => CompareExtensions(x, y),
            _ => CompareNames(x.Name, y.Name),
        };

        private static int CompareExtensions(FileEntry x, FileEntry y)
        {
            var byExtension = MemoryExtensions.CompareTo(
                x.Extension, y.Extension, StringComparison.OrdinalIgnoreCase);

            return byExtension != 0
                ? byExtension
                : NaturalComparer.CompareNatural(x.BaseName, y.BaseName);
        }

        private int CompareNames(string x, string y) => spec.Natural
            ? NaturalComparer.Instance.Compare(x, y)
            : string.CompareOrdinal(x, y);
    }
}
