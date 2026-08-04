using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Fjord.Core.Operations;

namespace Fjord.Platform;

/// <summary>Picks the trash implementation for the running operating system.</summary>
public static class TrashServices
{
    public static ITrashService Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsTrashService();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacTrashService();
        }

        return OperatingSystem.IsLinux()
            ? new XdgTrashService()
            : new UnsupportedTrashService();
    }
}

/// <summary>For a platform with no trash. Refuses rather than deleting outright.</summary>
public sealed class UnsupportedTrashService : ITrashService
{
    public bool IsSupported => false;

    public Task<string?> TrashAsync(string path, CancellationToken ct = default) =>
        throw new PlatformNotSupportedException("This platform has no trash.");

    public Task<bool> RestoreAsync(string token, CancellationToken ct = default) => Task.FromResult(false);
}

/// <summary>
/// The Windows Recycle Bin, via <c>SHFileOperationW</c>.
/// </summary>
/// <remarks>
/// <c>FOF_ALLOWUNDO</c> is what makes this the Recycle Bin rather than a delete. Restoring
/// is left to Explorer: putting an item back needs the shell's undo stack, and
/// reimplementing it from the <c>$Recycle.Bin</c> layout would be guesswork about an
/// undocumented format. <see cref="TrashAsync"/> therefore returns null — trashed, not
/// restorable by us — and <c>TrashOperation</c> reports it as not undoable rather than
/// promising something it cannot do.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsTrashService : ITrashService
{
    private const uint FoDelete = 0x0003;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofNoErrorUi = 0x0400;
    private const ushort FofSilent = 0x0004;

    public bool IsSupported => true;

    public Task<string?> TrashAsync(string path, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);

        if (!File.Exists(full) && !Directory.Exists(full))
        {
            throw new FileNotFoundException($"'{path}' no longer exists.", path);
        }

        // The path list is double-null terminated; the API accepts several paths at once,
        // but one at a time keeps per-item error reporting.
        var operation = new ShFileOpStruct
        {
            Func = FoDelete,
            From = full + '\0' + '\0',
            Flags = (ushort)(FofAllowUndo | FofNoConfirmation | FofNoErrorUi | FofSilent),
        };

        var result = SHFileOperation(ref operation);

        if (result != 0)
        {
            throw new IOException(
                $"Could not move '{Path.GetFileName(full)}' to the Recycle Bin (error {result}).");
        }

        if (operation.AnyOperationsAborted)
        {
            throw new OperationCanceledException(ct);
        }

        return Task.FromResult<string?>(null);
    }

    public Task<bool> RestoreAsync(string token, CancellationToken ct = default) => Task.FromResult(false);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr Window;
        public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }

    // DllImport rather than LibraryImport: the source generator cannot marshal a struct
    // containing LPWStr fields, and hand-rolling a marshaller for a call made a handful of
    // times per session would be effort spent in the wrong place.
    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Interoperability", "SYSLIB1054", Justification = "Struct has LPWStr fields; see above.")]
    private static extern int SHFileOperation(ref ShFileOpStruct fileOp);
}

/// <summary>
/// The freedesktop.org trash specification, used by every Linux desktop.
/// </summary>
/// <remarks>
/// An item goes to <c>$XDG_DATA_HOME/Trash/files/</c> with a matching
/// <c>.trashinfo</c> in <c>info/</c> recording where it came from — which is what makes
/// "Restore" work in the desktop's own file manager, not just in Fjord.
/// <para>
/// Trash must live on the same filesystem as the item, because trashing has to be a rename
/// and not a copy. For anything outside the home volume the spec defines a
/// <c>.Trash-$uid</c> directory at that volume's mount point, which is what
/// <see cref="TrashDirectoryFor"/> looks for.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class XdgTrashService : ITrashService
{
    public bool IsSupported => true;

    public async Task<string?> TrashAsync(string path, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);

        if (!File.Exists(full) && !Directory.Exists(full))
        {
            throw new FileNotFoundException($"'{path}' no longer exists.", path);
        }

        var trash = TrashDirectoryFor(full);
        var files = Path.Combine(trash, "files");
        var info = Path.Combine(trash, "info");
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(info);

        var name = UniqueTrashName(files, info, Path.GetFileName(full));
        var target = Path.Combine(files, name);

        // The info file is written first: an entry in files/ with no info/ is an orphan
        // the desktop cannot restore, whereas the reverse is merely a stale record.
        var record = new StringBuilder()
            .AppendLine("[Trash Info]")
            .Append("Path=").AppendLine(Uri.EscapeDataString(full).Replace("%2F", "/", StringComparison.Ordinal))
            .Append("DeletionDate=")
            .AppendLine(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture))
            .ToString();

        await File.WriteAllTextAsync(Path.Combine(info, name + ".trashinfo"), record, ct)
            .ConfigureAwait(false);

        try
        {
            if (File.Exists(full))
            {
                File.Move(full, target);
            }
            else
            {
                Directory.Move(full, target);
            }
        }
        catch
        {
            // Do not leave an info file describing something that is still in place.
            File.Delete(Path.Combine(info, name + ".trashinfo"));
            throw;
        }

        return target;
    }

    public Task<bool> RestoreAsync(string token, CancellationToken ct = default)
    {
        if (!File.Exists(token) && !Directory.Exists(token))
        {
            return Task.FromResult(false);
        }

        var files = Path.GetDirectoryName(token)!;
        var trash = Path.GetDirectoryName(files)!;
        var infoFile = Path.Combine(trash, "info", Path.GetFileName(token) + ".trashinfo");

        if (!File.Exists(infoFile))
        {
            return Task.FromResult(false);
        }

        var original = File.ReadLines(infoFile)
            .FirstOrDefault(l => l.StartsWith("Path=", StringComparison.Ordinal))?[5..];

        if (string.IsNullOrEmpty(original))
        {
            return Task.FromResult(false);
        }

        var destination = Uri.UnescapeDataString(original);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        if (File.Exists(token))
        {
            File.Move(token, destination, overwrite: false);
        }
        else
        {
            Directory.Move(token, destination);
        }

        File.Delete(infoFile);
        return Task.FromResult(true);
    }

    /// <summary>Finds the trash that lives on the same filesystem as <paramref name="path"/>.</summary>
    private static string TrashDirectoryFor(string path)
    {
        var home = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);

        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(home, ".local", "share");

        var homeTrash = Path.Combine(dataHome, "Trash");

        try
        {
            var itemRoot = new DriveInfo(Path.GetPathRoot(path) ?? "/").Name;
            var homeRoot = new DriveInfo(Path.GetPathRoot(homeTrash) ?? "/").Name;

            if (string.Equals(itemRoot, homeRoot, StringComparison.Ordinal))
            {
                return homeTrash;
            }

            // Different volume: the spec's per-mount trash, so the move stays a rename.
            var mount = MountPointOf(path);
            return Path.Combine(mount, $".Trash-{Environment.GetEnvironmentVariable("UID") ?? "1000"}");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return homeTrash;
        }
    }

    private static string MountPointOf(string path)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "/");

        // Walks up until the parent is on a different device, which is the mount point.
        while (directory.Parent is { } parent)
        {
            try
            {
                if (new DriveInfo(directory.FullName).Name != new DriveInfo(parent.FullName).Name)
                {
                    return directory.FullName;
                }
            }
            catch (ArgumentException)
            {
                break;
            }

            directory = parent;
        }

        return directory.FullName;
    }

    /// <summary>
    /// Picks a name free in both <c>files/</c> and <c>info/</c>.
    /// </summary>
    /// <remarks>
    /// Both, because the two must stay paired. A name free in one but taken in the other
    /// would either orphan an entry or overwrite someone else's record.
    /// </remarks>
    private static string UniqueTrashName(string files, string info, string name)
    {
        if (!Occupied(files, info, name))
        {
            return name;
        }

        var dot = name.LastIndexOf('.');
        var stem = dot <= 0 ? name : name[..dot];
        var extension = dot <= 0 ? string.Empty : name[dot..];

        for (var counter = 2; counter < 10_000; counter++)
        {
            var candidate = $"{stem}.{counter}{extension}";
            if (!Occupied(files, info, candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Could not find a free name in the trash.");
    }

    private static bool Occupied(string files, string info, string name) =>
        File.Exists(Path.Combine(files, name))
        || Directory.Exists(Path.Combine(files, name))
        || File.Exists(Path.Combine(info, name + ".trashinfo"));
}

/// <summary>
/// The macOS trash.
/// </summary>
/// <remarks>
/// <b>Untested — no macOS machine was available.</b> This moves items to
/// <c>~/.Trash</c>, which is where the Finder keeps them and is enough for them to appear
/// there and be emptied normally.
/// <para>
/// It is an approximation in one respect: the Finder records the original location in its
/// own database, so items trashed by Fjord will have no "Put Back". Fjord's own undo works,
/// because it remembers where the item came from itself. Doing better needs
/// <c>NSFileManager.trashItemAtURL</c> through Objective-C interop.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class MacTrashService : ITrashService
{
    public bool IsSupported => true;

    public Task<string?> TrashAsync(string path, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);

        if (!File.Exists(full) && !Directory.Exists(full))
        {
            throw new FileNotFoundException($"'{path}' no longer exists.", path);
        }

        var trash = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify),
            ".Trash");

        Directory.CreateDirectory(trash);

        var target = UniquePathIn(trash, Path.GetFileName(full));

        if (File.Exists(full))
        {
            File.Move(full, target);
        }
        else
        {
            Directory.Move(full, target);
        }

        return Task.FromResult<string?>(target);
    }

    /// <remarks>
    /// Restoring needs the original path, which the trash directory does not record — so
    /// the token carries the trashed location and the caller supplies the destination.
    /// <c>TrashOperation</c> knows where each item came from, which is why Fjord's undo
    /// works even though the Finder's "Put Back" will not.
    /// </remarks>
    public Task<bool> RestoreAsync(string token, CancellationToken ct = default) =>
        Task.FromResult(File.Exists(token) || Directory.Exists(token));

    private static string UniquePathIn(string directory, string name)
    {
        var candidate = Path.Combine(directory, name);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            return candidate;
        }

        var dot = name.LastIndexOf('.');
        var stem = dot <= 0 ? name : name[..dot];
        var extension = dot <= 0 ? string.Empty : name[dot..];

        for (var counter = 2; counter < 10_000; counter++)
        {
            candidate = Path.Combine(directory, $"{stem} {counter}{extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Could not find a free name in the trash.");
    }
}
