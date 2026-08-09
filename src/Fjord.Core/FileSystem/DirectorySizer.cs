using System.IO.Enumeration;

namespace Fjord.Core.FileSystem;

/// <summary>One directory's measured size.</summary>
/// <param name="Bytes">Total of every file beneath it.</param>
/// <param name="Files">How many files that was, for the message.</param>
public readonly record struct DirectorySize(long Bytes, int Files);

/// <summary>
/// Adds up what is inside a directory, recursively.
/// </summary>
/// <remarks>
/// A listing shows a directory with no size because the filesystem does not keep one —
/// finding out means walking the whole tree, which is far too slow to do for every row of
/// every listing. So it is a thing the user asks for, one listing at a time, and it reports
/// per directory as it finishes each so the column fills in rather than appearing at the
/// end.
/// <para>
/// Symlinks and junctions are not followed. A reparse point can point at its own ancestor,
/// and a size that never finishes is worse than one that is missing.
/// </para>
/// </remarks>
public static class DirectorySizer
{
    /// <summary>
    /// Measures each directory, reporting as it goes.
    /// </summary>
    /// <param name="report">
    /// Called on a background thread as each directory finishes. The caller marshals.
    /// </param>
    public static Task MeasureAsync(
        string parent,
        IReadOnlyList<string> names,
        Action<string, DirectorySize> report,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(report);

        return Task.Run(
            () =>
            {
                foreach (var name in names)
                {
                    ct.ThrowIfCancellationRequested();

                    var size = Measure(Path.Combine(parent, name), ct);
                    report(name, size);
                }
            },
            ct);
    }

    /// <summary>
    /// Walks one tree and totals it.
    /// </summary>
    /// <remarks>
    /// <see cref="FileSystemEnumerable{TResult}"/> with recursion left to the enumerator,
    /// which is the same machinery the listing uses and knows about long paths and reparse
    /// points. Inaccessible subtrees are skipped rather than aborting the total: a folder
    /// you cannot read part of still has a useful size for the part you can.
    /// </remarks>
    public static DirectorySize Measure(string directory, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(directory);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,

            // Following one would let a junction pointing at an ancestor spin forever.
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        var total = 0L;
        var files = 0;

        try
        {
            var lengths = new FileSystemEnumerable<long>(
                directory,
                static (ref FileSystemEntry entry) => entry.IsDirectory ? -1 : entry.Length,
                options);

            foreach (var length in lengths)
            {
                ct.ThrowIfCancellationRequested();

                if (length >= 0)
                {
                    total += length;
                    files++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The directory itself went away or cannot be opened. What was counted before
            // that is still worth reporting.
        }

        return new DirectorySize(total, files);
    }
}
