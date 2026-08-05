namespace Fjord.Core.Storage;

/// <summary>One pane's state within a saved session.</summary>
/// <param name="CursorName">
/// The entry the cursor was on, by name. Rows do not survive a resort or files changing
/// while the session was closed; a name does.
/// </param>
public sealed record SavedTab(string Directory, string? CursorName, IReadOnlyList<string> Selection);

/// <summary>A session: what the window looked like.</summary>
public sealed record SavedSession(string Name, IReadOnlyList<SavedTab> Tabs, int ActiveTab)
{
    /// <summary>The session restored on launch, as distinct from a named one.</summary>
    public const string DefaultName = "__last__";
}

/// <summary>An entry collected into a scrapbook.</summary>
public sealed record ScrapItem(string Path, DateTime AddedUtc);

/// <summary>
/// Durable state: sessions, scrapbooks, recent locations.
/// </summary>
/// <remarks>
/// An interface in Core so commands are testable against an in-memory fake and never touch
/// SQLite, and so the App can decide where the database lives.
/// <para>
/// Every method is synchronous. The data is small — tens of rows, not thousands — and a
/// local SQLite write is measured in microseconds; making it async would add ceremony to
/// every call site to hide nothing.
/// </para>
/// </remarks>
public interface IStorage
{
    // ---- Sessions

    void SaveSession(SavedSession session);

    SavedSession? LoadSession(string name);

    IReadOnlyList<string> SessionNames();

    bool DeleteSession(string name);

    // ---- Scrapbook

    /// <summary>
    /// Adds paths to a scrapbook, ignoring ones already there.
    /// </summary>
    /// <returns>How many were actually new.</returns>
    int AddToScrapbook(string scrapbook, IReadOnlyList<string> paths);

    IReadOnlyList<ScrapItem> Scrapbook(string name);

    IReadOnlyList<string> ScrapbookNames();

    int RemoveFromScrapbook(string scrapbook, IReadOnlyList<string> paths);

    bool ClearScrapbook(string name);

    // ---- Recents

    /// <summary>Records a visit, bumping the timestamp if the directory is already known.</summary>
    void RecordVisit(string directory);

    /// <summary>Most recently visited directories, newest first.</summary>
    IReadOnlyList<string> RecentDirectories(int limit = 50);
}
