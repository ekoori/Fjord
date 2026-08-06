using Fjord.Commands;
using Fjord.Core.Navigation;
using Fjord.Core.Storage;

namespace Fjord.App;

/// <summary>
/// Answers <c>:cd</c> with the places the user already knows about.
/// </summary>
/// <remarks>
/// Quickmarks come from memory and recents from the session database, and both are read on
/// demand rather than cached: a mark set thirty seconds ago has to appear in the next
/// <c>:cd</c>, and a cache invalidated on every navigation would be a cache for nothing.
/// </remarks>
public sealed class DestinationSource : IDestinationSource
{
    private readonly MarkFile _marks;
    private readonly IStorage _storage;

    /// <summary>How many recent directories are offered.</summary>
    /// <remarks>
    /// The panel is a list you scan, not one you scroll. Past a dozen or so the thing you
    /// wanted is faster to type than to find.
    /// </remarks>
    private const int RecentLimit = 12;

    public DestinationSource(MarkFile marks, IStorage storage)
    {
        _marks = marks;
        _storage = storage;
    }

    public IEnumerable<Destination> Quickmarks() =>
        _marks.Names.Select(name => new Destination(name, _marks.Get(name)!.Value.Directory, "mark"));

    /// <remarks>
    /// Offered under its last segment rather than its full path, so it matches the same way
    /// a quickmark does — typing <c>proj</c> should find <c>~/src/projects</c> without
    /// needing the part of the path you were not thinking about.
    /// </remarks>
    public IEnumerable<Destination> Recents()
    {
        foreach (var directory in _storage.RecentDirectories(RecentLimit))
        {
            var leaf = Path.GetFileName(directory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            yield return new Destination(
                leaf.Length > 0 ? leaf : directory, directory, "recent");
        }
    }
}
