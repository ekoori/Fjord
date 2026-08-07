namespace Fjord.Core.FileSystem;

/// <summary>
/// A location inside an archive, written as the archive's path with a suffix.
/// </summary>
/// <param name="Archive">The archive file on disk.</param>
/// <param name="Entry">
/// The directory inside it, with forward slashes and no leading or trailing one. Empty for
/// the archive's own root.
/// </param>
/// <remarks>
/// Spelled <c>C:\x\src.zip!\lib</c>, with the separator the platform's own so the eye reads
/// the whole thing as a path. The bang is what makes it unambiguous: a real directory
/// cannot contain one on Windows, and on Unix the archive part is matched as an existing
/// file first, so a directory genuinely called <c>src.zip!</c> is still reachable.
/// </remarks>
public readonly record struct ArchivePath(string Archive, string Entry)
{
    /// <summary>Marks the boundary between the archive and the path inside it.</summary>
    public const string Separator = "!";

    /// <summary>Extensions Fjord will open as a directory.</summary>
    /// <remarks>
    /// Zip and tar only, and deliberately: both are in the base class library, so browsing
    /// an archive costs no dependency and no native code. 7z and rar would need a
    /// third-party library and a licence to record.
    /// </remarks>
    public static IReadOnlyList<string> Extensions { get; } =
        [".zip", ".tar", ".tar.gz", ".tgz", ".jar", ".nupkg", ".vsix"];

    /// <summary>Whether a filename is one Fjord can browse into.</summary>
    public static bool IsArchive(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        // Longest first, so ".tar.gz" is not read as ".gz" and refused.
        return Extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether a path points inside an archive.</summary>
    public static bool IsInside(string path) =>
        !string.IsNullOrEmpty(path) && Parse(path) is not null;

    /// <summary>
    /// Reads a path that may point inside an archive.
    /// </summary>
    /// <returns>Null when it is an ordinary path.</returns>
    public static ArchivePath? Parse(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var mark = path.IndexOf(Separator, StringComparison.Ordinal);

        if (mark <= 0)
        {
            return null;
        }

        var archive = path[..mark];
        var entry = path[(mark + Separator.Length)..]
            .Replace('\\', '/')
            .Trim('/');

        return IsArchive(archive) ? new ArchivePath(archive, entry) : null;
    }

    /// <summary>The path as the rest of the app writes it.</summary>
    public override string ToString() =>
        Entry.Length == 0
            ? Archive + Separator + Path.DirectorySeparatorChar
            : Archive + Separator + Path.DirectorySeparatorChar + Entry.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>The location one level up, or null at the archive's root.</summary>
    /// <remarks>
    /// Null rather than the archive file itself: leaving an archive is going to the
    /// directory that holds it, which the caller expresses as an ordinary path.
    /// </remarks>
    public ArchivePath? Parent()
    {
        if (Entry.Length == 0)
        {
            return null;
        }

        var slash = Entry.LastIndexOf('/');
        return this with { Entry = slash < 0 ? string.Empty : Entry[..slash] };
    }

    /// <summary>The location of a child directory or file.</summary>
    public ArchivePath Child(string name) =>
        this with { Entry = Entry.Length == 0 ? name : Entry + "/" + name };
}
