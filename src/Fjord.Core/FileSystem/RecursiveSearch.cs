using System.IO.Enumeration;

namespace Fjord.Core.FileSystem;

/// <summary>What a recursive search found, and whether it found all of it.</summary>
/// <param name="Paths">Hits, relative to the directory searched, best match first.</param>
/// <param name="Examined">How many entries were looked at.</param>
/// <param name="Truncated">True when the walk stopped at <see cref="RecursiveSearch.Limit"/>.</param>
public readonly record struct SearchResults(
    IReadOnlyList<string> Paths, int Examined, bool Truncated);

/// <summary>
/// Finds names matching a pattern anywhere beneath a directory.
/// </summary>
/// <remarks>
/// The counterpart to <c>:search</c>, which filters the listing already on screen. That one
/// is instant because the entries are in memory; this one has to walk the disk, so it is a
/// command that runs and returns a result rather than a filter that reacts to typing.
/// <para>
/// Results are paths relative to where the search started. An absolute path per row would
/// be mostly a shared prefix repeated down the column, and the thing the eye is looking for
/// — which folder it is in — would be the part pushed off the end.
/// </para>
/// </remarks>
public static class RecursiveSearch
{
    /// <summary>
    /// The most hits collected before the walk gives up.
    /// </summary>
    /// <remarks>
    /// A short pattern against a large tree matches almost everything — fuzzy matching is
    /// generous by design — and a listing of half a million rows is not an answer to
    /// anything. The count is reported so a truncated search says so rather than quietly
    /// looking complete.
    /// </remarks>
    public const int Limit = 5000;

    /// <summary>
    /// Walks <paramref name="directory"/> and everything under it.
    /// </summary>
    /// <param name="pattern">Matched fuzzily against each entry's own name, not its path.</param>
    /// <param name="includeHidden">Whether to descend into and return hidden entries.</param>
    /// <remarks>
    /// Matched against the name rather than the relative path on purpose: a pattern
    /// typed to find a file would otherwise also match on the letters of the folders above
    /// it, and every hit deep in a tree would score for its ancestors.
    /// <para>
    /// Reparse points are skipped, for the same reason directory sizes skip them: a
    /// junction pointing at its own ancestor makes a walk that never finishes.
    /// </para>
    /// </remarks>
    public static SearchResults Find(
        string directory,
        string pattern,
        bool includeHidden = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(pattern);

        if (pattern.Length == 0)
        {
            return new SearchResults([], 0, false);
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = includeHidden
                ? FileAttributes.ReparsePoint
                : FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
        };

        var hits = new List<(string Path, int Score)>();
        var examined = 0;
        var truncated = false;

        try
        {
            // The transform is static and returns only what is needed, so a tree with a
            // hundred thousand entries does not allocate a hundred thousand strings for
            // the ones that do not match.
            var found = new FileSystemEnumerable<(string Path, int Score)>(
                directory,
                (ref FileSystemEntry entry) =>
                {
                    var name = entry.FileName.ToString();
                    var match = FuzzyMatcher.Match(pattern, name);

                    return match.IsMatch
                        ? (Relative(directory, ref entry, name), match.Score)
                        : (string.Empty, 0);
                },
                options);

            foreach (var hit in found)
            {
                ct.ThrowIfCancellationRequested();
                examined++;

                if (hit.Path.Length == 0)
                {
                    continue;
                }

                hits.Add(hit);

                if (hits.Count >= Limit)
                {
                    truncated = true;
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The starting directory went away or cannot be read. Whatever was found
            // before that is still worth showing.
        }

        // Best first, then by path, so equal scores come out in a stable order rather than
        // in whatever order the filesystem happened to hand them over.
        hits.Sort((a, b) => b.Score != a.Score
            ? b.Score.CompareTo(a.Score)
            : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));

        return new SearchResults([.. hits.Select(h => h.Path)], examined, truncated);
    }

    /// <summary>The entry's path with the searched directory's prefix removed.</summary>
    private static string Relative(string root, ref FileSystemEntry entry, string name)
    {
        var directory = entry.Directory;

        // The entry sits directly in the root, so its name is already the whole answer.
        if (directory.Length <= root.Length)
        {
            return name;
        }

        var under = directory[root.Length..]
            .TrimStart(Path.DirectorySeparatorChar)
            .TrimStart(Path.AltDirectorySeparatorChar);

        return under.IsEmpty ? name : Path.Join(under, name);
    }
}
