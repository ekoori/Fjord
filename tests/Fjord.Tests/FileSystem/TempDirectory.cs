namespace Fjord.Tests.FileSystem;

/// <summary>
/// A real directory under the system temp path, removed on dispose.
/// </summary>
/// <remarks>
/// The filesystem is deliberately not mocked in these tests. The entire value of
/// <c>DirectoryLister</c> is how it behaves against a real OS — attribute mapping,
/// inaccessible entries, broken links, entries vanishing mid-enumeration — and none of
/// that is exercised by a fake.
/// </remarks>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "fjord-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string CreateFile(string name, long size = 0)
    {
        var full = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);

        using var stream = File.Create(full);
        if (size > 0)
        {
            stream.SetLength(size);
        }

        return full;
    }

    public string CreateDir(string name)
    {
        var full = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>
    /// Creates a file that both Unix and Windows agree is hidden.
    /// </summary>
    /// <param name="name">
    /// Must begin with a dot. Unix has no hidden attribute — the name is the whole
    /// mechanism — so a name without one produces an ordinary file there while still
    /// being hidden on Windows, and the test passes on the machine it was written on and
    /// fails in CI.
    /// </param>
    /// <remarks>
    /// A leading dot is enough on Unix, and .NET reports it as
    /// <see cref="FileAttributes.Hidden"/>. Windows needs the attribute set explicitly.
    /// </remarks>
    public string CreateHiddenFile(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!name.StartsWith('.'))
        {
            throw new ArgumentException(
                $"'{name}' would not be hidden on Unix; give it a leading dot.", nameof(name));
        }

        var full = CreateFile(name);

        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(full, File.GetAttributes(full) | FileAttributes.Hidden);
        }

        return full;
    }

    public void CreateMany(int count, string prefix = "file")
    {
        for (var i = 0; i < count; i++)
        {
            File.Create(System.IO.Path.Combine(Path, $"{prefix}{i}.txt")).Dispose();
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A test that deliberately holds a handle open should not fail the run on
            // cleanup; the temp directory is disposable either way.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: a read-only fixture entry is the test's business, not cleanup's.
        }
    }
}
