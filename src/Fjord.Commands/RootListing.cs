using Fjord.Core.FileSystem;
using Fjord.Core.Panes;
using Fjord.Core.Platform;

namespace Fjord.Commands;

/// <summary>
/// Showing the machine's drives and mounts as a listing.
/// </summary>
/// <remarks>
/// Shared because two commands arrive here from opposite directions: <c>:roots</c>, which
/// was asked for, and <c>:up</c> at the top of a drive, which has nowhere else to go. Both
/// must produce the same rows, or going up would land somewhere subtly unlike the place the
/// explicit command shows.
/// </remarks>
internal static class RootListing
{
    /// <summary>The label a rooted listing is shown under.</summary>
    public const string Label = "roots";

    /// <summary>
    /// Shows the roots, with <paramref name="select"/> under the cursor if it is one.
    /// </summary>
    /// <returns>False when the platform reported no roots at all.</returns>
    public static async Task<bool> ShowAsync(
        IPlatformServices platform, IPane pane, string? select = null)
    {
        var roots = platform.GetRoots();

        if (roots.Count == 0)
        {
            return false;
        }

        // Keyed by path, because that is the entry name a virtual listing carries and what
        // Enter will navigate to.
        var described = roots.ToDictionary(r => r.Path, Describe, StringComparer.Ordinal);

        await pane.ShowListingAsync(Label, [.. roots.Select(r => r.Path)], described)
            .ConfigureAwait(true);

        if (select is { Length: > 0 })
        {
            pane.MoveCursorToName(select);
        }

        return true;
    }

    /// <summary>
    /// A root's label, size, free space and filesystem, as one line.
    /// </summary>
    /// <remarks>
    /// Free before total, because free is the number anyone reading this is looking for.
    /// A volume that reported nothing shows its label alone rather than zeroes, which would
    /// read as a full disk.
    /// <para>
    /// No percentage: "27 GB free of 476 GB" already says it, and the eleven characters it
    /// cost were pushing the filesystem out of the end of the column.
    /// </para>
    /// </remarks>
    public static string Describe(PlatformRoot root)
    {
        if (root.Volume is not { TotalBytes: { } total, FreeBytes: { } free })
        {
            return root.Label;
        }

        var fileSystem = root.Volume.FileSystem is { Length: > 0 } fs ? $"  {fs}" : string.Empty;

        return $"{root.Label}   {SizeFormatter.Format(free)} free of "
            + $"{SizeFormatter.Format(total)}{fileSystem}";
    }
}
