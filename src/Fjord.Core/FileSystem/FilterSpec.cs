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
/// A case-insensitive substring the name must contain. This is the <c>/</c> filter, so
/// it is applied to the already-enumerated snapshot rather than re-reading the directory.
/// </param>
/// <param name="DirectoriesOnly">Hide files, e.g. while picking a destination folder.</param>
public sealed record FilterSpec(
    bool ShowHidden = false,
    string? NameContains = null,
    bool DirectoriesOnly = false)
{
    public static readonly FilterSpec Default = new();

    /// <summary>True when every entry passes, so the view can skip the filter pass.</summary>
    public bool IsEmpty => ShowHidden && NameContains is null && !DirectoriesOnly;

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

        if (NameContains is { Length: > 0 } needle &&
            !entry.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}
