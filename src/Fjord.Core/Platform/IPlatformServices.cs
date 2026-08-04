namespace Fjord.Core.Platform;

public enum RootKind
{
    Drive,
    Home,
    Mount,
    Network,
    Removable,
}

/// <summary>A place the user can navigate to from nowhere: a drive, a mount, home.</summary>
public readonly record struct PlatformRoot(string Path, string Label, RootKind Kind);

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

    /// <summary>Shows a path in the system file manager, selected where that is supported.</summary>
    Task RevealAsync(string path, CancellationToken ct = default);

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
