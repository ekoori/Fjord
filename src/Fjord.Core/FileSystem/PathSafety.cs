namespace Fjord.Core.FileSystem;

/// <summary>Whether a path means what it says on this operating system.</summary>
public static class PathSafety
{
    /// <summary>
    /// Whether the operating system resolves this path to the entry it names.
    /// </summary>
    /// <remarks>
    /// Windows strips trailing dots and spaces while resolving, so a file actually named
    /// <c>...</c> resolves to the directory holding it, and one named <c>report.</c>
    /// resolves to <c>report</c>. Neither can be created through Win32, but both arrive
    /// over SMB and from other operating systems routinely.
    /// <para>
    /// Left alone that is not a display glitch: trashing the entry named <c>...</c> would
    /// trash the directory it is sitting in. Anything that turns an entry into a path an
    /// operation will act on has to ask this first.
    /// </para>
    /// <para>
    /// On Linux and macOS the same names resolve to themselves, so this is true for
    /// everything the filesystem can hold — which is the point of asking the platform
    /// rather than maintaining a list of characters to fear.
    /// </para>
    /// </remarks>
    public static bool IsAddressable(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        try
        {
            // Compared against the given path's own last segment rather than against an
            // entry name, so a rooted name — which is how the scrapbook labels its
            // entries — still compares like for like.
            return string.Equals(
                Path.GetFileName(path),
                Path.GetFileName(Path.GetFullPath(path)),
                StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }
    }
}
