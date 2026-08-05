namespace Fjord.Core.FileSystem;

/// <summary>
/// Which entries of a listing are visible.
/// </summary>
/// <param name="ShowHidden">
/// Whether dotfiles and entries carrying the Hidden attribute are shown. When false the
/// lister filters them at the OS level via <c>EnumerationOptions.AttributesToSkip</c>,
/// which is cheaper than enumerating and discarding.
/// </param>
/// <param name="NameContains">
/// What the name must match. This is the <c>/</c> filter, applied to the already-enumerated
/// snapshot rather than re-reading the directory, so narrowing is instant.
/// </param>
/// <param name="DirectoriesOnly">Hide files, e.g. while picking a destination folder.</param>
/// <param name="Fuzzy">
/// Whether <paramref name="NameContains"/> is a fuzzy subsequence rather than a substring.
/// Fuzzy is the default for <c>/</c>: typing <c>fjc</c> to reach <c>Fjord.Core.csproj</c>
/// is the whole point, and a substring filter would find nothing.
/// </param>
public sealed record FilterSpec(
    bool ShowHidden = false,
    string? NameContains = null,
    bool DirectoriesOnly = false,
    bool Fuzzy = true)
{
    public static readonly FilterSpec Default = new();

    /// <summary>True when every entry passes, so the view can skip the filter pass.</summary>
    public bool IsEmpty => ShowHidden && NameContains is null && !DirectoriesOnly;

    /// <summary>True when a name filter is active, so the status line can say so.</summary>
    public bool HasNameFilter => NameContains is { Length: > 0 };

    public bool Matches(in FileEntry entry)
    {
        if (!ShowHidden && entry.IsHidden)
        {
            return false;
        }

        if (DirectoriesOnly && !entry.IsDirectory)
        {
            return false;
        }

        if (NameContains is not { Length: > 0 } needle)
        {
            return true;
        }

        return Fuzzy
            ? FuzzyMatcher.IsMatch(needle, entry.Name)
            : entry.Name.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// How well an entry matches, for ordering results.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Matches"/> because sorting only needs a score when a fuzzy
    /// filter is active, and computing one per entry per keystroke otherwise would be
    /// waste. Returns 0 when there is nothing to rank by, leaving the normal sort in charge.
    /// </remarks>
    public int Score(in FileEntry entry) =>
        Fuzzy && NameContains is { Length: > 0 } needle
            ? FuzzyMatcher.Match(needle, entry.Name).Score
            : 0;
}
