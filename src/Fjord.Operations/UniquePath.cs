namespace Fjord.Operations;

/// <summary>Finding a destination name that is not already taken.</summary>
public static class UniquePath
{
    /// <summary>
    /// Gives up rather than spinning forever when a directory is pathological.
    /// </summary>
    private const int MaxAttempts = 10_000;

    /// <summary>
    /// Returns <paramref name="path"/> if free, otherwise the same name with a counter.
    /// </summary>
    /// <remarks>
    /// The counter goes before the extension — <c>report (2).pdf</c>, not
    /// <c>report.pdf (2)</c> — so the file still opens in the right application. A
    /// multi-part extension like <c>.tar.gz</c> only keeps its last part, matching what
    /// every desktop file manager does.
    /// </remarks>
    public static string For(string path)
    {
        if (!Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileName(path);

        // A dotfile is a name, not an extension: ".bashrc" must become ".bashrc (2)".
        var dot = name.LastIndexOf('.');
        var stem = dot <= 0 ? name : name[..dot];
        var extension = dot <= 0 ? string.Empty : name[dot..];

        for (var counter = 2; counter < MaxAttempts; counter++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({counter}){extension}");
            if (!Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"Could not find a free name next to '{path}'.");
    }

    /// <summary>
    /// Whether anything occupies this path.
    /// </summary>
    /// <remarks>
    /// Both checks, not just <see cref="File.Exists"/>: a directory named the same as the
    /// file being written would otherwise look free and the write would fail late.
    /// </remarks>
    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// Whether <paramref name="child"/> is inside <paramref name="parent"/>.
    /// </summary>
    /// <remarks>
    /// The guard against copying or moving a directory into itself, which otherwise
    /// recurses until the path length limit and leaves a half-built tree behind.
    /// </remarks>
    public static bool IsInside(string child, string parent, StringComparison comparison)
    {
        var normalisedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var normalisedChild = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));

        if (!normalisedChild.StartsWith(normalisedParent, comparison))
        {
            return false;
        }

        // Equal paths count as inside; a separator boundary stops "/a/bc" matching "/a/b".
        return normalisedChild.Length == normalisedParent.Length
            || normalisedChild[normalisedParent.Length] == Path.DirectorySeparatorChar
            || normalisedChild[normalisedParent.Length] == Path.AltDirectorySeparatorChar;
    }
}
