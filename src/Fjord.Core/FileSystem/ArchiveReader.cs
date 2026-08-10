using System.Formats.Tar;
using System.IO.Compression;

namespace Fjord.Core.FileSystem;

/// <summary>One thing inside an archive.</summary>
/// <param name="Key">Its full path within the archive, with forward slashes.</param>
public readonly record struct ArchiveEntry(string Key, long Length, DateTime ModifiedUtc, bool IsDirectory);

/// <summary>
/// Reads zip and tar archives as if they were directories.
/// </summary>
/// <remarks>
/// Read-only. Browsing into an archive and pulling something out of it is the common need
/// and costs no dependency — both formats are in the base class library. Writing into one
/// is a different job: every operation would have to rewrite the container, and undo would
/// have to understand that.
/// <para>
/// Whole-archive rather than incremental. A tar has no central directory, so listing any
/// part of one means reading it through; and a zip's directory is small enough that the
/// distinction would not pay for the second code path.
/// </para>
/// </remarks>
public static class ArchiveReader
{
    /// <summary>
    /// Lists what sits directly inside <paramref name="location"/>.
    /// </summary>
    /// <remarks>
    /// Directories are synthesised. Archives commonly store only files, leaving the folders
    /// implied by the slashes in their names, so a listing built from entries alone would
    /// show nothing at all at the top level of a deeply nested archive.
    /// </remarks>
    public static IReadOnlyList<FileEntry> List(ArchivePath location)
    {
        var prefix = location.Entry.Length == 0 ? string.Empty : location.Entry + "/";

        var files = new List<FileEntry>();
        var directories = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        foreach (var entry in Read(location.Archive))
        {
            if (!entry.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var relative = entry.Key[prefix.Length..].Trim('/');

            if (relative.Length == 0)
            {
                continue;
            }

            var slash = relative.IndexOf('/');

            if (slash >= 0)
            {
                // Something further down. Its first segment is a directory here, whether or
                // not the archive bothered to record one.
                var name = relative[..slash];
                directories[name] = Newer(directories, name, entry.ModifiedUtc);
                continue;
            }

            if (entry.IsDirectory)
            {
                directories[relative] = Newer(directories, relative, entry.ModifiedUtc);
            }
            else
            {
                files.Add(new FileEntry(relative, entry.Length, entry.ModifiedUtc.Ticks, EntryFlags.None));
            }
        }

        return
        [
            .. directories.Select(d => new FileEntry(d.Key, -1, d.Value.Ticks, EntryFlags.Directory)),
            .. files,
        ];
    }

    /// <summary>
    /// Copies one entry out to a real file.
    /// </summary>
    /// <remarks>
    /// The destination is created and written whole. A partly-written extraction is
    /// deleted rather than left behind: half a file with the right name is worse than none,
    /// because nothing afterwards can tell it is half.
    /// </remarks>
    public static async Task ExtractAsync(
        ArchivePath location, string destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);

        var written = false;

        try
        {
            if (IsTar(location.Archive))
            {
                await ExtractFromTarAsync(location, destination, ct).ConfigureAwait(false);
            }
            else
            {
                await ExtractFromZipAsync(location, destination, ct).ConfigureAwait(false);
            }

            written = true;
        }
        finally
        {
            if (!written)
            {
                TryDelete(destination);
            }
        }
    }

    /// <summary>
    /// Copies an entry out, or a whole directory of them.
    /// </summary>
    /// <param name="destination">
    /// The file to write for a file entry, or the directory to fill for a directory entry.
    /// </param>
    /// <returns>How many files were written. Zero means the entry was not in the archive.</returns>
    /// <remarks>
    /// One pass over the archive for the whole subtree, rather than a call to
    /// <see cref="ExtractAsync"/> per file. A tar has no central directory, so each of
    /// those would read the container through from the start — extracting a folder of a
    /// hundred files would read the archive a hundred times.
    /// <para>
    /// As with a single file, a subtree that does not finish is removed rather than left
    /// behind: half a folder is indistinguishable from a whole one once the operation that
    /// wrote it is gone.
    /// </para>
    /// </remarks>
    public static async Task<int> ExtractTreeAsync(
        ArchivePath location, string destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);

        var written = 0;

        try
        {
            written = IsTar(location.Archive)
                ? await ExtractTreeFromTarAsync(location, destination, ct).ConfigureAwait(false)
                : await ExtractTreeFromZipAsync(location, destination, ct).ConfigureAwait(false);

            return written;
        }
        finally
        {
            if (written == 0)
            {
                TryDiscard(destination);
            }
        }
    }

    /// <summary>Every entry, files and any directories the archive records.</summary>
    public static IEnumerable<ArchiveEntry> Read(string archive)
    {
        ArgumentNullException.ThrowIfNull(archive);

        return IsTar(archive) ? ReadTar(archive) : ReadZip(archive);
    }

    private static bool IsTar(string archive) =>
        archive.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)
        || archive.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
        || archive.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<ArchiveEntry> ReadZip(string archive)
    {
        using var zip = ZipFile.OpenRead(archive);

        foreach (var entry in zip.Entries)
        {
            // A zip marks a directory by ending its name with a slash and having no
            // content; the name alone is not enough, since a zero-length file is legal.
            var key = entry.FullName.Replace('\\', '/');
            var isDirectory = key.EndsWith('/');

            yield return new ArchiveEntry(
                key.Trim('/'),
                entry.Length,
                entry.LastWriteTime.UtcDateTime,
                isDirectory);
        }
    }

    private static IEnumerable<ArchiveEntry> ReadTar(string archive)
    {
        using var stream = OpenTar(archive);
        using var reader = new TarReader(stream);

        while (reader.GetNextEntry() is { } entry)
        {
            yield return new ArchiveEntry(
                entry.Name.Replace('\\', '/').Trim('/'),
                entry.Length,
                entry.ModificationTime.UtcDateTime,
                entry.EntryType is TarEntryType.Directory);
        }
    }

    /// <remarks>
    /// The gzip layer is decided by the extension rather than by sniffing the magic bytes.
    /// A <c>.tar</c> that is really gzipped is a mislabelled file, and treating the name as
    /// wrong would mean every listing paid for a probe.
    /// </remarks>
    private static Stream OpenTar(string archive)
    {
        var file = File.OpenRead(archive);

        if (archive.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
        {
            return file;
        }

        return new GZipStream(file, CompressionMode.Decompress);
    }

    private static async Task ExtractFromZipAsync(
        ArchivePath location, string destination, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(location.Archive);

        var entry = zip.Entries.FirstOrDefault(
            e => string.Equals(e.FullName.Replace('\\', '/').Trim('/'), location.Entry, StringComparison.Ordinal))
            ?? throw new FileNotFoundException($"'{location.Entry}' is not in {location.Archive}.");

        await using var source = entry.Open();
        await using var target = File.Create(destination);
        await source.CopyToAsync(target, ct).ConfigureAwait(false);
    }

    private static async Task ExtractFromTarAsync(
        ArchivePath location, string destination, CancellationToken ct)
    {
        await using var stream = OpenTar(location.Archive);
        using var reader = new TarReader(stream);

        while (reader.GetNextEntry() is { } entry)
        {
            if (!string.Equals(entry.Name.Replace('\\', '/').Trim('/'), location.Entry, StringComparison.Ordinal))
            {
                continue;
            }

            if (entry.DataStream is not { } data)
            {
                throw new InvalidDataException($"'{location.Entry}' has no content.");
            }

            await using var target = File.Create(destination);
            await data.CopyToAsync(target, ct).ConfigureAwait(false);
            return;
        }

        throw new FileNotFoundException($"'{location.Entry}' is not in {location.Archive}.");
    }

    /// <summary>
    /// Where one archive key lands under a destination, or null when it is not in scope.
    /// </summary>
    /// <remarks>
    /// The guard against <c>..</c> is the zip-slip check. An archive is untrusted input, and
    /// a crafted entry named <c>../../.bashrc</c> would otherwise be written outside the
    /// directory the user chose — the whole point of extracting somewhere specific.
    /// </remarks>
    private static string? Landing(ArchivePath location, string key, string destination)
    {
        if (string.Equals(key, location.Entry, StringComparison.Ordinal))
        {
            return destination;
        }

        var prefix = location.Entry.Length == 0 ? string.Empty : location.Entry + "/";

        if (prefix.Length > 0 && !key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var relative = key[prefix.Length..].Trim('/');

        if (relative.Length == 0)
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(destination);

        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    private static async Task<int> ExtractTreeFromZipAsync(
        ArchivePath location, string destination, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(location.Archive);
        var written = 0;

        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();

            var key = entry.FullName.Replace('\\', '/');

            if (Landing(location, key.Trim('/'), destination) is not { } target)
            {
                continue;
            }

            if (key.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            await using var source = entry.Open();
            await using var file = File.Create(target);
            await source.CopyToAsync(file, ct).ConfigureAwait(false);
            written++;
        }

        return written;
    }

    private static async Task<int> ExtractTreeFromTarAsync(
        ArchivePath location, string destination, CancellationToken ct)
    {
        await using var stream = OpenTar(location.Archive);
        using var reader = new TarReader(stream);
        var written = 0;

        while (reader.GetNextEntry() is { } entry)
        {
            ct.ThrowIfCancellationRequested();

            var key = entry.Name.Replace('\\', '/').Trim('/');

            if (Landing(location, key, destination) is not { } target)
            {
                continue;
            }

            if (entry.EntryType is TarEntryType.Directory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            if (entry.DataStream is not { } data)
            {
                // A link or a device node. Nothing to write, and nothing lost by skipping
                // it: recreating one on extraction is a different decision than copying.
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            await using var file = File.Create(target);
            await data.CopyToAsync(file, ct).ConfigureAwait(false);
            written++;
        }

        return written;
    }

    private static DateTime Newer(Dictionary<string, DateTime> known, string name, DateTime candidate) =>
        known.TryGetValue(name, out var existing) && existing > candidate ? existing : candidate;

    /// <summary>Removes a partial extraction, whether it got as far as a file or a tree.</summary>
    private static void TryDiscard(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing useful to do: the caller is already failing.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing useful to do: the caller is already failing.
        }
    }
}
