using Fjord.Commands;
using Fjord.Core.Panes;

namespace Fjord.App;

/// <summary>
/// Path completion against the real filesystem, relative to the active pane.
/// </summary>
/// <remarks>
/// Relative paths resolve against the pane's directory rather than the process working
/// directory: typing <c>:cd sub</c> means the <c>sub</c> the user is looking at.
/// </remarks>
public sealed class PathCompletionSource : IPathCompletionSource
{
    /// <summary>
    /// Enough to fill any popup. A directory of 100,000 entries must not be materialised
    /// and sorted to show ten of them.
    /// </summary>
    private const int MaxCandidates = 500;

    private readonly Func<IPane?> _activePane;

    public PathCompletionSource(Func<IPane?> activePane) => _activePane = activePane;

    public IEnumerable<string> List(string directory, bool directoriesOnly)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };

        try
        {
            // The trailing separator lets Tab on a directory lead straight into it rather
            // than stopping and needing the separator typed by hand.
            //
            // Forward slash even on Windows: .NET accepts it everywhere, and a backslash
            // would force CommandLexer.Quote to wrap every completion in quotes, turning
            // ":cd scripts/" into the far uglier ":cd 'scripts\'".
            var directories = Directory
                .EnumerateDirectories(directory, "*", options)
                .Select(path => Path.GetFileName(path) + '/');

            if (directoriesOnly)
            {
                return directories.Take(MaxCandidates).ToList();
            }

            var files = Directory
                .EnumerateFiles(directory, "*", options)
                .Select(path => Path.GetFileName(path) ?? string.Empty);

            return directories.Concat(files).Take(MaxCandidates).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Completing inside a directory that cannot be read is not an error worth
            // reporting; there is simply nothing to offer.
            return [];
        }
    }

    public string Resolve(string path)
    {
        var expanded = path.StartsWith('~')
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[1..].TrimStart('/', '\\'))
            : path;

        var basePath = _activePane()?.Directory is { Length: > 0 } directory
            ? directory
            : Environment.CurrentDirectory;

        return Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(basePath, expanded));
    }
}
