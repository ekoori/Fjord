using System.Diagnostics;
using System.Runtime.Versioning;
using Fjord.Core.Platform;

namespace Fjord.Platform;

/// <summary>Picks the implementation for the running operating system.</summary>
public static class PlatformServices
{
    /// <remarks>
    /// The <c>OperatingSystem.Is*</c> checks are recognised by the trimmer, so a
    /// RID-specific publish proves the other branches dead and drops them — the size
    /// benefit of separate platform assemblies, without breaking single-file publish.
    /// </remarks>
    public static IPlatformServices Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsPlatformServices();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacPlatformServices();
        }

        return OperatingSystem.IsLinux()
            ? new LinuxPlatformServices()
            : new GenericPlatformServices();
    }
}

/// <summary>Shared behaviour that does not actually differ per platform.</summary>
public abstract class PlatformServicesBase : IPlatformServices
{
    public abstract IReadOnlyList<PlatformRoot> GetRoots();

    public abstract StringComparison PathComparison { get; }

    public string HomeDirectory { get; } = Environment.GetFolderPath(
        Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);

    /// <summary>
    /// Opened with the OS default handler.
    /// </summary>
    /// <remarks>
    /// <c>UseShellExecute</c> is what makes <see cref="Process.Start(ProcessStartInfo)"/>
    /// consult the OS association rather than trying to execute the file. Without it,
    /// opening a .txt fails with "not a valid application".
    /// </remarks>
    public virtual Task OpenAsync(string path, CancellationToken ct = default)
    {
        Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    /// <summary>
    /// No chooser by default.
    /// </summary>
    /// <remarks>
    /// Saying so is the point. An unknown system might have a picker under some name, but
    /// guessing at one and silently opening the file with its default handler instead
    /// would be worse than a message explaining that this system has none.
    /// </remarks>
    public virtual Task<bool> OpenWithAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(false);

    public abstract Task RevealAsync(string path, CancellationToken ct = default);

    public virtual string DisplayPath(string path) => path;

    /// <summary>
    /// Runs a helper process without waiting for it.
    /// </summary>
    /// <remarks>
    /// Nothing waits on the exit code. A file manager or a shell handler can outlive
    /// Fjord, and several of them report failure through their own UI rather than an exit
    /// status — <c>explorer.exe</c> returns 1 even on success.
    /// </remarks>
    protected static void Start(ProcessStartInfo info)
    {
        using var process = Process.Start(info);
    }

    protected static ProcessStartInfo Command(string file, params string[] arguments)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformServices : PlatformServicesBase
{
    public override StringComparison PathComparison => StringComparison.OrdinalIgnoreCase;

    public override IReadOnlyList<PlatformRoot> GetRoots()
    {
        var roots = new List<PlatformRoot>
        {
            new(HomeDirectory, "Home", RootKind.Home),
        };

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady)
            {
                // Touching Name is safe, but VolumeLabel on a drive with no media throws.
                roots.Add(new PlatformRoot(drive.Name, drive.Name, RootKind.Drive));
                continue;
            }

            var label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                ? drive.Name
                : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";

            roots.Add(new PlatformRoot(drive.Name, label, Kind(drive.DriveType), Describe(drive)));
        }

        return roots;
    }

    /// <summary>
    /// What a ready drive reports about itself.
    /// </summary>
    /// <remarks>
    /// Every one of these throws on a drive that stops being ready between the readiness
    /// check and the read — a card pulled out, a share dropped — so the whole thing is
    /// guarded rather than each property. A root with no figures still belongs in the list.
    /// </remarks>
    private static VolumeInfo Describe(DriveInfo drive)
    {
        try
        {
            return new VolumeInfo(drive.TotalSize, drive.AvailableFreeSpace, drive.DriveFormat);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    /// <remarks>
    /// The comma with no space after <c>/select</c> is required; <c>explorer /select, path</c>
    /// silently opens the parent instead. And explorer returns exit code 1 on success, so
    /// nothing may check it.
    /// </remarks>
    /// <summary>
    /// The shell's own "How do you want to open this file?" picker.
    /// </summary>
    /// <remarks>
    /// <c>OpenAs_RunDLL</c> is the documented entry point for <c>SHOpenWithDialog</c>,
    /// which on Windows 10 and later is the full picker — the one with "More apps" and
    /// "Always use this app", rather than the short list on the shell's context menu.
    /// <para>
    /// The path is passed unquoted and as a raw command line, because the entry point
    /// takes everything after the comma verbatim; <c>ArgumentList</c> would wrap it in
    /// quotes that arrive as part of the filename. Spaces are fine, and a quotation mark
    /// cannot appear here because Windows does not allow one in a filename.
    /// </para>
    /// </remarks>
    public override Task<bool> OpenWithAsync(string path, CancellationToken ct = default)
    {
        Start(new ProcessStartInfo("rundll32.exe")
        {
            Arguments = $"shell32.dll,OpenAs_RunDLL {Path.GetFullPath(path)}",
            UseShellExecute = false,
        });

        return Task.FromResult(true);
    }

    public override Task RevealAsync(string path, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);

        Start(new ProcessStartInfo("explorer.exe")
        {
            // ArgumentList would quote this into /select,"..." as a single argument, which
            // explorer does not parse. The raw string is deliberate.
            Arguments = $"/select,\"{full}\"",
            UseShellExecute = true,
        });

        return Task.CompletedTask;
    }

    private static RootKind Kind(DriveType type) => type switch
    {
        DriveType.Network => RootKind.Network,
        DriveType.Removable or DriveType.CDRom => RootKind.Removable,
        _ => RootKind.Drive,
    };
}

[SupportedOSPlatform("macos")]
public sealed class MacPlatformServices : PlatformServicesBase
{
    /// <remarks>
    /// APFS and HFS+ are case-insensitive by default, though case-sensitive volumes exist.
    /// </remarks>
    public override StringComparison PathComparison => StringComparison.OrdinalIgnoreCase;

    public override IReadOnlyList<PlatformRoot> GetRoots()
    {
        var roots = new List<PlatformRoot>
        {
            new(HomeDirectory, "Home", RootKind.Home),
            new("/", "Macintosh HD", RootKind.Drive, UnixMounts.Volume("/")),
        };

        roots.AddRange(UnixMounts.Under("/Volumes"));
        return roots;
    }

    /// <summary>
    /// The Standard Additions application chooser, then <c>open -a</c> with what it picked.
    /// </summary>
    /// <remarks>
    /// Finder's own "Open With" is a context submenu with no scripting entry point, so
    /// this is as close as macOS comes to a system-provided picker: the same list of
    /// applications, in a real system dialog. It cannot change the default handler for the
    /// file type the way the Windows picker can — that lives in Get Info.
    /// <para>
    /// Cancelling the dialog makes osascript exit non-zero, which nothing here reads: the
    /// user cancelling is not a failure to report back to them.
    /// </para>
    /// </remarks>
    public override Task<bool> OpenWithAsync(string path, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);

        Start(Command(
            "osascript",
            "-e", "set chosen to choose application as alias",
            "-e", "set app to POSIX path of chosen",
            "-e", $"do shell script \"open -a \" & quoted form of app & \" \" & quoted form of {Quote(full)}"));

        return Task.FromResult(true);
    }

    /// <summary>Wraps a path as an AppleScript string literal.</summary>
    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    public override Task RevealAsync(string path, CancellationToken ct = default)
    {
        Start(Command("open", "-R", Path.GetFullPath(path)));
        return Task.CompletedTask;
    }

    public override string DisplayPath(string path) => UnixMounts.Abbreviate(path, HomeDirectory);
}

[SupportedOSPlatform("linux")]
public sealed class LinuxPlatformServices : PlatformServicesBase
{
    public override StringComparison PathComparison => StringComparison.Ordinal;

    public override IReadOnlyList<PlatformRoot> GetRoots()
    {
        var roots = new List<PlatformRoot>
        {
            new(HomeDirectory, "Home", RootKind.Home),
            new("/", "Filesystem", RootKind.Drive, UnixMounts.Volume("/")),
        };

        // Where udisks2 and most desktops mount removable media.
        roots.AddRange(UnixMounts.Under("/media"));
        roots.AddRange(UnixMounts.Under($"/run/media/{Environment.UserName}"));
        roots.AddRange(UnixMounts.Under("/mnt"));
        return roots;
    }

    public override Task OpenAsync(string path, CancellationToken ct = default)
    {
        // xdg-open rather than UseShellExecute: .NET's shell-execute path on Linux also
        // ends at xdg-open, but only after probing several fallbacks.
        Start(Command("xdg-open", Path.GetFullPath(path)));
        return Task.CompletedTask;
    }

    /// <summary>
    /// The desktop portal's chooser, asked for explicitly with <c>ask</c>.
    /// </summary>
    /// <remarks>
    /// <c>xdg-open</c> has no equivalent — it goes straight to the default handler, which
    /// is the thing being avoided. <c>org.freedesktop.portal.OpenURI</c> with
    /// <c>ask: true</c> is the portal's documented way to demand the picker, and every
    /// desktop that ships xdg-desktop-portal answers it with its own native dialog.
    /// <para>
    /// A system with no portal — a bare window manager, a container — has no such dialog
    /// at all, and that is reported rather than papered over with the default handler.
    /// </para>
    /// </remarks>
    public override Task<bool> OpenWithAsync(string path, CancellationToken ct = default)
    {
        var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri;

        try
        {
            Start(Command(
                "gdbus",
                "call",
                "--session",
                "--dest", "org.freedesktop.portal.Desktop",
                "--object-path", "/org/freedesktop/portal/desktop",
                "--method", "org.freedesktop.portal.OpenURI.OpenURI",
                string.Empty,
                uri,
                "{'ask': <true>}"));

            return Task.FromResult(true);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Task.FromResult(false);
        }
    }

    /// <remarks>
    /// The freedesktop interface is the only way to get the file <i>selected</i> rather
    /// than merely opening its folder, and every major Linux file manager implements it.
    /// Falling back to opening the parent is a real loss of behaviour, but better than
    /// nothing when no D-Bus session is available.
    /// </remarks>
    public override Task RevealAsync(string path, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        var uri = new Uri(full).AbsoluteUri;

        try
        {
            Start(Command(
                "dbus-send",
                "--session",
                "--print-reply",
                "--dest=org.freedesktop.FileManager1",
                "/org/freedesktop/FileManager1",
                "org.freedesktop.FileManager1.ShowItems",
                $"array:string:{uri}",
                "string:"));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent))
            {
                Start(Command("xdg-open", parent));
            }
        }

        return Task.CompletedTask;
    }

    public override string DisplayPath(string path) => UnixMounts.Abbreviate(path, HomeDirectory);
}

/// <summary>Fallback for an operating system Fjord does not know about.</summary>
public sealed class GenericPlatformServices : PlatformServicesBase
{
    public override StringComparison PathComparison => StringComparison.Ordinal;

    public override IReadOnlyList<PlatformRoot> GetRoots() =>
    [
        new(HomeDirectory, "Home", RootKind.Home),
        new("/", "Filesystem", RootKind.Drive, UnixMounts.Volume("/")),
    ];

    public override Task RevealAsync(string path, CancellationToken ct = default)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        return string.IsNullOrEmpty(parent) ? Task.CompletedTask : OpenAsync(parent, ct);
    }
}

internal static class UnixMounts
{
    /// <summary>Mounted volumes directly under a directory, if it exists.</summary>
    public static IEnumerable<PlatformRoot> Under(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            return Directory
                .EnumerateDirectories(directory)
                .Select(path => new PlatformRoot(
                    path, Path.GetFileName(path), RootKind.Mount, Volume(path)))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Replaces the home prefix with <c>~</c>, but only on a path boundary.</summary>
    /// <summary>Capacity and free space for a mount point, where it will say.</summary>
    /// <remarks>
    /// DriveInfo works on any path on Unix, not just a drive letter, so the same class
    /// answers for a mount under /media as for C: on Windows.
    /// </remarks>
    public static VolumeInfo Volume(string path)
    {
        try
        {
            var drive = new DriveInfo(path);

            return drive.IsReady
                ? new VolumeInfo(drive.TotalSize, drive.AvailableFreeSpace, drive.DriveFormat)
                : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return default;
        }
    }

    public static string Abbreviate(string path, string home)
    {
        if (home.Length == 0 || !path.StartsWith(home, StringComparison.Ordinal))
        {
            return path;
        }

        // Guards against "/home/igor2" becoming "~2" for a user whose home is /home/igor.
        if (path.Length == home.Length)
        {
            return "~";
        }

        return path[home.Length] == '/' ? "~" + path[home.Length..] : path;
    }
}
