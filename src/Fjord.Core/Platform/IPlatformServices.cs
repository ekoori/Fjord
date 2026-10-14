namespace Fjord.Core.Platform;

/// <summary>What a volume reports about itself, when it will say.</summary>
/// <param name="TotalBytes">Capacity, or null when the volume did not answer.</param>
/// <param name="FreeBytes">Space available to this user, or null.</param>
/// <param name="FileSystem">NTFS, ext4, APFS — or empty when unknown.</param>
/// <remarks>
/// Nullable throughout because a root is not always a mounted volume. A network share that
/// is offline, a card reader with no card and an unreadable mount all appear in the list
/// and answer nothing; showing them with blank figures is more honest than hiding them.
/// </remarks>
public readonly record struct VolumeInfo(long? TotalBytes, long? FreeBytes, string FileSystem = "");

public enum RootKind
{
    Drive,
    Home,
    Mount,
    Network,
    Removable,
}

/// <summary>A place the user can navigate to from nowhere: a drive, a mount, home.</summary>
/// <param name="Volume">
/// Capacity, free space and filesystem, where the volume reports them. Gathered with the
/// root rather than on demand: they come from the same call that finds it.
/// </param>
public readonly record struct PlatformRoot(
    string Path,
    string Label,
    RootKind Kind,
    VolumeInfo Volume = default);

/// <summary>
/// The operations that differ per operating system.
/// </summary>
/// <remarks>
/// Deliberately small. Trash, icon extraction, file associations, filesystem watching and
/// elevation are each a platform assembly's worth of native code and belong with the
/// milestone that needs them, not here.
/// <para>
/// There is one implementation project rather than three. With plain project references
/// all three would be referenced on every OS anyway, and dynamic loading breaks
/// single-file publish and trimming. <c>OperatingSystem.IsWindows()</c> is special-cased
/// by the linker, so a RID-specific publish drops the other platforms' branches for free.
/// </para>
/// </remarks>
public interface IPlatformServices
{
    /// <summary>Drives on Windows; <c>/</c>, home and mounted volumes on Unix.</summary>
    IReadOnlyList<PlatformRoot> GetRoots();

    /// <summary>Opens a path with whatever the OS considers its default handler.</summary>
    Task OpenAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Asks the OS to show its own "open with" chooser for a path.
    /// </summary>
    /// <returns>False when the running system offers no such dialog.</returns>
    /// <remarks>
    /// The dialog belongs to the OS rather than to Fjord on purpose: it is the one place
    /// the user can also change which application owns the file type from then on, and a
    /// list Fjord drew itself could offer the choice but not make it stick.
    /// </remarks>
    Task<bool> OpenWithAsync(string path, CancellationToken ct = default);

    /// <summary>Shows a path in the system file manager, selected where that is supported.</summary>
    Task RevealAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Opens a folder in the system file manager, as a window of its own.
    /// </summary>
    /// <returns>False when this platform has no file manager to send it to.</returns>
    /// <remarks>
    /// Not <see cref="OpenAsync"/>, which goes to the registered handler for folders — and
    /// once <c>:default-file-manager</c> has been used that handler is Fjord, so asking the
    /// system to open a folder would come straight back here. This names the system's own
    /// file manager instead, which is the point of asking for it.
    /// <para>
    /// Distinct from <see cref="RevealAsync"/>: reveal shows a path <i>selected inside its
    /// parent</i>, which for a folder means looking at it from outside rather than opening
    /// it.
    /// </para>
    /// </remarks>
    Task<bool> OpenInFileManagerAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Runs a program, handing it paths as its arguments.
    /// </summary>
    /// <returns>False when this platform cannot launch it.</returns>
    /// <remarks>
    /// What dropping files onto an executable has meant since the shell learned to do it:
    /// the paths arrive as <c>argv</c>, and nothing more. There is no registry entry or
    /// manifest describing flags a program would like — a drop target carries no
    /// convention beyond the arguments themselves — so Fjord passes the paths and nothing
    /// else rather than inventing switches the program never asked for.
    /// </remarks>
    Task<bool> RunWithArgumentsAsync(
        string executable, IReadOnlyList<string> arguments, CancellationToken ct = default);

    string HomeDirectory { get; }

    /// <summary>Shortens a path for display: <c>~/x</c> on Unix.</summary>
    string DisplayPath(string path);

    /// <summary>
    /// How paths compare on this platform.
    /// </summary>
    /// <remarks>
    /// Windows and macOS are case-insensitive by default, Linux is not — and it varies per
    /// volume on all three. This is the platform's usual answer, good enough for sorting
    /// and for spotting duplicates; anything that must be exact has to probe the volume.
    /// </remarks>
    StringComparison PathComparison { get; }
}
