using Fjord.Core.FileSystem;
using Fjord.Core.Navigation;
using Fjord.Core.Storage;
using Fjord.Storage;
using Fjord.Tests.FileSystem;

namespace Fjord.Tests.Storage;

/// <summary>
/// Runs against real SQLite, in memory.
/// </summary>
/// <remarks>
/// Not mocked: the value of this class is the SQL — the upsert, the cascade, the ordering —
/// and a fake would test none of it. In-memory keeps it fast and self-contained.
/// </remarks>
public class SqliteStorageTests : IDisposable
{
    private readonly SqliteStorage _storage = SqliteStorage.InMemory();

    public void Dispose()
    {
        _storage.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- Sessions

    [Fact]
    public void ASavedSessionRoundTrips()
    {
        var session = new SavedSession(
            "work",
            [
                new SavedTab("/home/u/src", "Program.cs", ["/home/u/src/a.cs", "/home/u/src/b.cs"]),
                new SavedTab("/home/u/docs", null, []),
            ],
            ActiveTab: 1);

        _storage.SaveSession(session);
        var loaded = _storage.LoadSession("work");

        Assert.NotNull(loaded);
        Assert.Equal(1, loaded!.ActiveTab);
        Assert.Equal(2, loaded.Tabs.Count);
        Assert.Equal("/home/u/src", loaded.Tabs[0].Directory);
        Assert.Equal("Program.cs", loaded.Tabs[0].CursorName);
        Assert.Equal(2, loaded.Tabs[0].Selection.Count);
        Assert.Null(loaded.Tabs[1].CursorName);
        Assert.Empty(loaded.Tabs[1].Selection);
    }

    [Fact]
    public void TabOrderIsPreserved()
    {
        // Tabs are positional; restoring them shuffled would move every gt.
        _storage.SaveSession(new SavedSession(
            "s",
            [new SavedTab("/a", null, []), new SavedTab("/b", null, []), new SavedTab("/c", null, [])],
            0));

        Assert.Equal(["/a", "/b", "/c"], _storage.LoadSession("s")!.Tabs.Select(t => t.Directory));
    }

    [Fact]
    public void SavingAgainReplacesRatherThanAccumulates()
    {
        _storage.SaveSession(new SavedSession("s", [new SavedTab("/a", null, []), new SavedTab("/b", null, [])], 0));
        _storage.SaveSession(new SavedSession("s", [new SavedTab("/c", null, [])], 0));

        Assert.Equal(["/c"], _storage.LoadSession("s")!.Tabs.Select(t => t.Directory));
    }

    [Fact]
    public void AnUnknownSessionIsNull() => Assert.Null(_storage.LoadSession("never-saved"));

    [Fact]
    public void SessionsAreListedAndDeletable()
    {
        _storage.SaveSession(new SavedSession("b", [new SavedTab("/x", null, [])], 0));
        _storage.SaveSession(new SavedSession("a", [new SavedTab("/y", null, [])], 0));

        Assert.Equal(["a", "b"], _storage.SessionNames());

        Assert.True(_storage.DeleteSession("a"));
        Assert.False(_storage.DeleteSession("a"));
        Assert.Equal(["b"], _storage.SessionNames());
    }

    [Fact]
    public void DeletingASessionRemovesItsTabs()
    {
        // The foreign key cascade; without it the tabs linger and the next session of the
        // same name inherits them.
        _storage.SaveSession(new SavedSession("s", [new SavedTab("/a", null, [])], 0));
        _storage.DeleteSession("s");

        Assert.Null(_storage.LoadSession("s"));

        _storage.SaveSession(new SavedSession("s", [new SavedTab("/b", null, [])], 0));
        Assert.Equal(["/b"], _storage.LoadSession("s")!.Tabs.Select(t => t.Directory));
    }

    [Fact]
    public void PathsWithNewlinesInSelectionAreNotSupportedButDoNotCorrupt()
    {
        // Selection is stored newline-joined. A newline in a filename is legal on Unix, so
        // this records the limitation rather than pretending it cannot happen: the entry
        // splits, but nothing else breaks and the session still loads.
        _storage.SaveSession(new SavedSession(
            "s", [new SavedTab("/a", null, ["/a/we\nird"])], 0));

        var loaded = _storage.LoadSession("s");

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Tabs[0].Selection.Count);
    }

    // ---- Scrapbook

    [Fact]
    public void ItemsAddedToAScrapbookComeBack()
    {
        _storage.AddToScrapbook("default", ["/a/one.txt", "/b/two.txt"]);

        Assert.Equal(["/a/one.txt", "/b/two.txt"], _storage.Scrapbook("default").Select(i => i.Path));
    }

    [Fact]
    public void AScrapbookIsASet()
    {
        // Collecting the same file from two searches is normal; a duplicate row is not.
        Assert.Equal(2, _storage.AddToScrapbook("d", ["/a", "/b"]));
        Assert.Equal(0, _storage.AddToScrapbook("d", ["/a", "/b"]));
        Assert.Equal(1, _storage.AddToScrapbook("d", ["/a", "/c"]));

        Assert.Equal(3, _storage.Scrapbook("d").Count);
    }

    [Fact]
    public void ScrapbooksAreIndependent()
    {
        _storage.AddToScrapbook("one", ["/a"]);
        _storage.AddToScrapbook("two", ["/b"]);

        Assert.Equal(["/a"], _storage.Scrapbook("one").Select(i => i.Path));
        Assert.Equal(["one", "two"], _storage.ScrapbookNames());
    }

    [Fact]
    public void ItemsCanBeRemovedAndCleared()
    {
        _storage.AddToScrapbook("d", ["/a", "/b", "/c"]);

        Assert.Equal(1, _storage.RemoveFromScrapbook("d", ["/b"]));
        Assert.Equal(0, _storage.RemoveFromScrapbook("d", ["/not-there"]));
        Assert.Equal(2, _storage.Scrapbook("d").Count);

        Assert.True(_storage.ClearScrapbook("d"));
        Assert.Empty(_storage.Scrapbook("d"));
        Assert.False(_storage.ClearScrapbook("d"));
    }

    [Fact]
    public void AnEmptyScrapbookIsNotAnError() => Assert.Empty(_storage.Scrapbook("never-used"));

    // ---- Recents

    [Fact]
    public void VisitsAreRecordedNewestFirst()
    {
        _storage.RecordVisit("/a");
        Thread.Sleep(2);
        _storage.RecordVisit("/b");

        Assert.Equal(["/b", "/a"], _storage.RecentDirectories());
    }

    [Fact]
    public void RevisitingBumpsRatherThanDuplicates()
    {
        // The upsert. Without it, recents fill with the same directory.
        _storage.RecordVisit("/a");
        Thread.Sleep(2);
        _storage.RecordVisit("/b");
        Thread.Sleep(2);
        _storage.RecordVisit("/a");

        Assert.Equal(["/a", "/b"], _storage.RecentDirectories());
    }

    [Fact]
    public void RecentsRespectTheLimit()
    {
        for (var i = 0; i < 10; i++)
        {
            _storage.RecordVisit($"/dir{i}");
            Thread.Sleep(1);
        }

        Assert.Equal(3, _storage.RecentDirectories(3).Count);
        Assert.Equal("/dir9", _storage.RecentDirectories(3)[0]);
    }

    [Fact]
    public void ALockedAndNamedTabComesBackThatWay()
    {
        // Both are decisions the user made about that tab. Losing them on restart means
        // making them again every launch, which is most of the value gone.
        _storage.SaveSession(new SavedSession(
            "s",
            [
                new SavedTab("/work", null, [], IsLocked: true, Name: "work"),
                new SavedTab("/other", null, []),
            ],
            0));

        var loaded = _storage.LoadSession("s")!;

        Assert.True(loaded.Tabs[0].IsLocked);
        Assert.Equal("work", loaded.Tabs[0].Name);
        Assert.False(loaded.Tabs[1].IsLocked);
        Assert.Null(loaded.Tabs[1].Name);
    }

    [Fact]
    public void ATabsOwnSortOrderComesBack()
    {
        // Per tab, not per app: sorting one tab by size is a statement about that tab, and
        // having to make it again every launch is most of the value gone.
        _storage.SaveSession(new SavedSession(
            "s",
            [
                new SavedTab(
                    "/work", null, [],
                    Sort: new SortSpec(SortKey.Size, Descending: true, DirectoriesFirst: false, Natural: false),
                    Filter: new FilterSpec(ShowHidden: true)),
            ],
            0));

        var tab = _storage.LoadSession("s")!.Tabs[0];

        Assert.NotNull(tab.Sort);
        Assert.Equal(SortKey.Size, tab.Sort!.Key);
        Assert.True(tab.Sort.Descending);
        Assert.False(tab.Sort.DirectoriesFirst);
        Assert.False(tab.Sort.Natural);
        Assert.True(tab.Filter!.ShowHidden);
    }

    [Fact]
    public void ATabThatNeverChoseComesBackWithoutOne()
    {
        // Null rather than a copy of today's default. A tab that inherited should keep
        // inheriting, or changing the configured sort would stop reaching it.
        _storage.SaveSession(new SavedSession("s", [new SavedTab("/work", null, [])], 0));

        var tab = _storage.LoadSession("s")!.Tabs[0];

        Assert.Null(tab.Sort);
        Assert.Null(tab.Filter);
    }

    [Fact]
    public void TabsKeepTheirOwnOrdersSeparately()
    {
        _storage.SaveSession(new SavedSession(
            "s",
            [
                new SavedTab("/a", null, [], Sort: new SortSpec(SortKey.Size), Filter: FilterSpec.Default),
                new SavedTab("/b", null, []),
                new SavedTab("/c", null, [], Sort: new SortSpec(SortKey.Modified), Filter: FilterSpec.Default),
            ],
            0));

        var tabs = _storage.LoadSession("s")!.Tabs;

        Assert.Equal(SortKey.Size, tabs[0].Sort!.Key);
        Assert.Null(tabs[1].Sort);
        Assert.Equal(SortKey.Modified, tabs[2].Sort!.Key);
    }

    [Fact]
    public void ASessionFromBeforeOrdersWereStoredStillLoads()
    {
        // The migration's promise: an existing session has no order columns, and every tab
        // in it should come back inheriting rather than pinned to whatever the config said
        // at upgrade time.
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "fjord.db");

        using (var older = new SqliteStorage(path))
        {
            older.SaveSession(new SavedSession("s", [new SavedTab("/work", null, [])], 0));
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using var upgraded = new SqliteStorage(path);

        Assert.Null(upgraded.LoadSession("s")!.Tabs[0].Sort);
    }

    // ---- Marks

    [Fact]
    public void MarksRoundTrip()
    {
        _storage.SaveMarks(new Dictionary<string, Mark>
        {
            ["projects"] = new("/home/igor/projects", "fjord"),
            ["downloads"] = new("/downloads", null),
        });

        var marks = _storage.LoadMarks();

        Assert.Equal(2, marks.Count);
        Assert.Equal("/home/igor/projects", marks["projects"].Directory);
        Assert.Equal("fjord", marks["projects"].CursorName);
        Assert.Null(marks["downloads"].CursorName);
    }

    [Fact]
    public void SavingMarksReplacesTheWholeSet()
    {
        // Not a merge. Clearing a mark in the app has to actually clear it on disk, or it
        // comes back on the next launch.
        _storage.SaveMarks(new Dictionary<string, Mark> { ["alpha"] = new("/first", null) });
        _storage.SaveMarks(new Dictionary<string, Mark> { ["beta"] = new("/second", null) });

        var marks = _storage.LoadMarks();

        Assert.False(marks.ContainsKey("alpha"));
        Assert.Equal("/second", marks["beta"].Directory);
    }

    [Fact]
    public void AMarkNameTheAppWouldRejectIsNotLoaded()
    {
        // The database is a file on disk and hand-editing it is normal. A name :cd could
        // never match would be a quickmark the user can see listed and never reach.
        _storage.SaveMarks(new Dictionary<string, Mark>
        {
            ["good"] = new("/kept", null),
            ["two words"] = new("/dropped", null),
        });

        var marks = _storage.LoadMarks();

        Assert.True(marks.ContainsKey("good"));
        Assert.False(marks.ContainsKey("two words"));
    }

    [Fact]
    public void NoMarksIsNotAnError() => Assert.Empty(_storage.LoadMarks());

    // ---- Schema

    [Fact]
    public void ADatabaseFromTheVersionBeforeMarksIsMigrated()
    {
        // The migration mechanism's first real use. A user upgrading has a v1 database on
        // disk, and it must gain the marks table rather than throwing on open.
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "fjord.db");

        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE sessions (name TEXT PRIMARY KEY, active_tab INTEGER NOT NULL,
                                       saved_utc INTEGER NOT NULL);
                CREATE TABLE session_tabs (session_name TEXT NOT NULL, position INTEGER NOT NULL,
                                           directory TEXT NOT NULL, cursor_name TEXT,
                                           selection TEXT NOT NULL DEFAULT '',
                                           PRIMARY KEY (session_name, position));
                CREATE TABLE scrap_items (scrapbook TEXT NOT NULL, path TEXT NOT NULL,
                                          added_utc INTEGER NOT NULL, PRIMARY KEY (scrapbook, path));
                CREATE TABLE recents (directory TEXT PRIMARY KEY, visited_utc INTEGER NOT NULL);
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using var upgraded = new SqliteStorage(path);
        upgraded.SaveMarks(new Dictionary<string, Mark> { ["alpha"] = new("/kept", null) });

        Assert.Equal("/kept", upgraded.LoadMarks()["alpha"].Directory);
    }

    [Fact]
    public void AFileBackedDatabasePersistsAcrossOpens()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "nested", "fjord.db");

        using (var first = new SqliteStorage(path))
        {
            first.AddToScrapbook("d", ["/kept"]);
        }

        using var second = new SqliteStorage(path);
        Assert.Equal(["/kept"], second.Scrapbook("d").Select(i => i.Path));
    }

    [Fact]
    public void OpeningAnExistingDatabaseDoesNotReapplyTheSchema()
    {
        // The migration guard. Re-running CREATE TABLE would throw on the second open.
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "fjord.db");

        using (var first = new SqliteStorage(path))
        {
            first.RecordVisit("/a");
        }

        using var second = new SqliteStorage(path);
        Assert.Equal(["/a"], second.RecentDirectories());
    }
}
