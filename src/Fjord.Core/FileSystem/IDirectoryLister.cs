namespace Fjord.Core.FileSystem;

/// <summary>
/// How a directory is enumerated.
/// </summary>
/// <param name="IncludeHidden">
/// When false, hidden and system entries are skipped by the OS rather than enumerated
/// and discarded. On Unix .NET reports a leading dot as the Hidden attribute, so this
/// works the same everywhere.
/// </param>
/// <param name="NeedsSize">Whether <see cref="FileEntry.Size"/> must be populated.</param>
/// <param name="NeedsTimes">Whether <see cref="FileEntry.ModifiedUtcTicks"/> must be populated.</param>
/// <param name="BatchSize">
/// Entries per batch once the ramp is over. 2,048 entries is roughly 64 KB — large
/// enough that per-batch overhead disappears, small enough that cancellation is prompt.
/// </param>
/// <param name="BufferSize">
/// The OS directory-read buffer. 0 takes the .NET default; a larger buffer measurably
/// reduces round trips on SMB and other network filesystems.
/// </param>
public sealed record ListingOptions(
    bool IncludeHidden = false,
    bool NeedsSize = true,
    bool NeedsTimes = true,
    int BatchSize = 2048,
    int BufferSize = 0)
{
    public static readonly ListingOptions Default = new();

    /// <summary>
    /// Whether the enumerator must touch per-entry metadata at all.
    /// </summary>
    /// <remarks>
    /// Size and timestamps are one question, not two. On Windows both arrive in the
    /// directory-read buffer for free. On Unix both are served by a single lazy
    /// <c>stat</c> that .NET caches per entry, so asking for one and asking for both cost
    /// the same — but asking for neither costs nothing at all.
    /// </remarks>
    public bool NeedsMetadata => NeedsSize || NeedsTimes;

    /// <summary>Derives the options a given sort and filter actually require.</summary>
    public static ListingOptions For(SortSpec sort, FilterSpec filter, bool sizeColumnVisible,
        bool modifiedColumnVisible) =>
        new(
            IncludeHidden: filter.ShowHidden,
            NeedsSize: sort.NeedsSize || sizeColumnVisible,
            NeedsTimes: sort.NeedsTimes || modifiedColumnVisible);
}

/// <summary>Streams the contents of a directory in batches.</summary>
public interface IDirectoryLister
{
    /// <summary>
    /// Enumerates <paramref name="directory"/>, yielding batches as they are read.
    /// </summary>
    /// <remarks>
    /// Batches arrive small first so the first screen paints immediately, then grow.
    /// The returned memory is owned by the caller for the duration of the iteration
    /// only; copy anything that must outlive it.
    /// </remarks>
    /// <exception cref="DirectoryNotFoundException">The directory does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The directory cannot be opened.</exception>
    IAsyncEnumerable<ReadOnlyMemory<FileEntry>> ListAsync(
        string directory, ListingOptions options, CancellationToken ct);
}
