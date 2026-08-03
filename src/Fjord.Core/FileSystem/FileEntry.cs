namespace Fjord.Core.FileSystem;

/// <summary>Attributes of a directory entry, packed to keep <see cref="FileEntry"/> small.</summary>
[Flags]
public enum EntryFlags : ushort
{
    None = 0,
    Directory = 1 << 0,
    Hidden = 1 << 1,
    ReparsePoint = 1 << 2,
    ReadOnly = 1 << 3,
    System = 1 << 4,

    /// <summary>A symlink or junction whose target could not be resolved.</summary>
    Broken = 1 << 5,

    /// <summary>Size and timestamps were skipped by the lister and are not yet known.</summary>
    MetadataPending = 1 << 6,
}

/// <summary>
/// One directory entry.
/// </summary>
/// <remarks>
/// A struct, and deliberately 32 bytes: 100,000 of them is a single 3.2 MB array rather
/// than 100,000 heap objects for the GC to walk.
/// <para>
/// <see cref="Name"/> is the name alone, never a full path. The directory is stored once
/// on the owning <see cref="DirectorySnapshot"/>; a full path is built on demand for the
/// few dozen visible rows and for the selection. At ~80 characters a path, storing them
/// per entry would cost ~16 MB for a 100,000-entry folder to no purpose.
/// </para>
/// </remarks>
public readonly struct FileEntry
{
    /// <summary>The entry name, without any directory component.</summary>
    public readonly string Name;

    /// <summary>Size in bytes, or -1 for a directory or when metadata was skipped.</summary>
    public readonly long Size;

    /// <summary>Last write time in UTC ticks, or 0 when metadata was skipped.</summary>
    public readonly long ModifiedUtcTicks;

    public readonly EntryFlags Flags;

    /// <summary>
    /// Index into <see cref="Name"/> of the '.' that begins the extension, or
    /// <c>Name.Length</c> when there is none. Computed once during enumeration so that
    /// sorting and rendering never rescan the name.
    /// </summary>
    public readonly ushort ExtensionStart;

    public FileEntry(string name, long size, long modifiedUtcTicks, EntryFlags flags)
    {
        Name = name;
        Size = size;
        ModifiedUtcTicks = modifiedUtcTicks;
        Flags = flags;
        ExtensionStart = FindExtensionStart(name);
    }

    public bool IsDirectory => (Flags & EntryFlags.Directory) != 0;

    public bool IsHidden => (Flags & EntryFlags.Hidden) != 0;

    public bool IsBroken => (Flags & EntryFlags.Broken) != 0;

    public bool HasMetadata => (Flags & EntryFlags.MetadataPending) == 0;

    public bool HasExtension => ExtensionStart < Name.Length;

    /// <summary>The extension including its leading '.', or empty.</summary>
    public ReadOnlySpan<char> Extension => Name.AsSpan(ExtensionStart);

    /// <summary>The name with the extension removed.</summary>
    public ReadOnlySpan<char> BaseName => Name.AsSpan(0, ExtensionStart);

    public DateTime ModifiedUtc => new(ModifiedUtcTicks, DateTimeKind.Utc);

    public override string ToString() => Name;

    /// <summary>
    /// Finds the start of the extension.
    /// </summary>
    /// <remarks>
    /// A leading dot is part of the name, not an extension separator: <c>.bashrc</c> is
    /// a dotfile with no extension, which is not what <see cref="Path.GetExtension(string)"/>
    /// reports. Directories are treated as extensionless — <c>my.folder</c> should sort
    /// and display as a whole name.
    /// </remarks>
    private static ushort FindExtensionStart(string name)
    {
        var dot = name.LastIndexOf('.');

        // No dot, a leading dot, or a trailing dot: no extension.
        if (dot <= 0 || dot == name.Length - 1)
        {
            return Clamp(name.Length);
        }

        return Clamp(dot);
    }

    /// <summary>
    /// Filename components cap at 255 on every filesystem Fjord targets, so a ushort is
    /// ample — but a synthetic entry should degrade to "no extension" rather than wrap.
    /// </summary>
    private static ushort Clamp(int value) => value > ushort.MaxValue ? ushort.MaxValue : (ushort)value;
}
