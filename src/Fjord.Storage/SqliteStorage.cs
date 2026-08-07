using Fjord.Core.Navigation;
using Fjord.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Fjord.Storage;

/// <summary>
/// Sessions, scrapbooks and recents in a SQLite database.
/// </summary>
/// <remarks>
/// One connection, held open for the life of the app. SQLite's own advice for a desktop
/// application: opening per operation costs a file open and a schema check each time, and
/// the alternative — pooling — buys nothing for a single-writer local database.
/// <para>
/// The database lives under <c>FjordPaths.Data</c>, not the config directory, so it stays
/// out of dotfile backups and configuration sync. It is regenerable state, not settings.
/// </para>
/// </remarks>
public sealed class SqliteStorage : IStorage, IDisposable
{
    /// <summary>
    /// Bumped when the schema changes; <see cref="Migrate"/> applies everything newer.
    /// </summary>
    private const int SchemaVersion = 4;

    private readonly SqliteConnection _connection;

    public SqliteStorage(string databasePath)
    {
        ArgumentNullException.ThrowIfNull(databasePath);

        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        _connection.Open();

        // WAL survives a crash without corrupting, and lets a reader run during a write.
        // NORMAL rather than FULL: losing the last session on a power cut is acceptable,
        // an fsync per commit on every navigation is not.
        Execute("PRAGMA journal_mode = WAL;");
        Execute("PRAGMA synchronous = NORMAL;");
        Execute("PRAGMA foreign_keys = ON;");

        Migrate();
    }

    /// <summary>Opens an in-memory database, for tests.</summary>
    public static SqliteStorage InMemory() => new(":memory:");

    public void Dispose()
    {
        _connection.Dispose();

        // Microsoft.Data.Sqlite pools connections by connection string; without this an
        // in-memory database outlives its owner and leaks between tests.
        SqliteConnection.ClearPool(_connection);
    }

    // ---- Sessions

    public void SaveSession(SavedSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        // One transaction: a session half-written is worse than one not written, because
        // it restores into a window missing tabs with no sign anything went wrong.
        using var transaction = _connection.BeginTransaction();

        Execute("DELETE FROM sessions WHERE name = $name;", transaction, ("$name", session.Name));

        Execute(
            "INSERT INTO sessions (name, active_tab, saved_utc) VALUES ($name, $active, $saved);",
            transaction,
            ("$name", session.Name),
            ("$active", session.ActiveTab),
            ("$saved", DateTime.UtcNow.Ticks));

        for (var i = 0; i < session.Tabs.Count; i++)
        {
            var tab = session.Tabs[i];

            Execute(
                """
                INSERT INTO session_tabs
                    (session_name, position, directory, cursor_name, selection, locked, tab_name)
                VALUES ($name, $position, $directory, $cursor, $selection, $locked, $tabName);
                """,
                transaction,
                ("$name", session.Name),
                ("$position", i),
                ("$directory", tab.Directory),
                ("$cursor", (object?)tab.CursorName ?? DBNull.Value),
                ("$selection", string.Join('\n', tab.Selection)),
                ("$locked", tab.IsLocked ? 1 : 0),
                ("$tabName", (object?)tab.Name ?? DBNull.Value));
        }

        transaction.Commit();
    }

    public SavedSession? LoadSession(string name)
    {
        using var header = Command(
            "SELECT active_tab FROM sessions WHERE name = $name;", null, ("$name", name));

        var active = header.ExecuteScalar();
        if (active is null or DBNull)
        {
            return null;
        }

        var tabs = new List<SavedTab>();

        using var command = Command(
            """
            SELECT directory, cursor_name, selection, locked, tab_name FROM session_tabs
            WHERE session_name = $name ORDER BY position;
            """,
            null,
            ("$name", name));

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var selection = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);

            tabs.Add(new SavedTab(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                selection.Length == 0 ? [] : selection.Split('\n'),
                !reader.IsDBNull(3) && reader.GetInt64(3) != 0,
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return new SavedSession(name, tabs, Convert.ToInt32(active, System.Globalization.CultureInfo.InvariantCulture));
    }

    public IReadOnlyList<string> SessionNames() =>
        Query("SELECT name FROM sessions ORDER BY name;");

    public bool DeleteSession(string name) =>
        Execute("DELETE FROM sessions WHERE name = $name;", null, ("$name", name)) > 0;

    // ---- Scrapbook

    public int AddToScrapbook(string scrapbook, IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        using var transaction = _connection.BeginTransaction();
        var added = 0;

        foreach (var path in paths)
        {
            // A scrapbook is a set: collecting the same file twice from two searches is
            // normal and should not produce a duplicate row.
            added += Execute(
                """
                INSERT OR IGNORE INTO scrap_items (scrapbook, path, added_utc)
                VALUES ($scrapbook, $path, $added);
                """,
                transaction,
                ("$scrapbook", scrapbook),
                ("$path", path),
                ("$added", DateTime.UtcNow.Ticks));
        }

        transaction.Commit();
        return added;
    }

    public IReadOnlyList<ScrapItem> Scrapbook(string name)
    {
        var items = new List<ScrapItem>();

        using var command = Command(
            """
            SELECT path, added_utc, comment FROM scrap_items
            WHERE scrapbook = $name ORDER BY added_utc, path;
            """,
            null,
            ("$name", name));

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new ScrapItem(
                reader.GetString(0),
                new DateTime(reader.GetInt64(1), DateTimeKind.Utc),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return items;
    }

    public IReadOnlyList<string> ScrapbookNames() =>
        Query("SELECT DISTINCT scrapbook FROM scrap_items ORDER BY scrapbook;");

    public int RemoveFromScrapbook(string scrapbook, IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        using var transaction = _connection.BeginTransaction();
        var removed = 0;

        foreach (var path in paths)
        {
            removed += Execute(
                "DELETE FROM scrap_items WHERE scrapbook = $scrapbook AND path = $path;",
                transaction,
                ("$scrapbook", scrapbook),
                ("$path", path));
        }

        transaction.Commit();
        return removed;
    }

    public int CommentOnScrapbook(string scrapbook, IReadOnlyList<string> paths, string? comment)
    {
        ArgumentNullException.ThrowIfNull(paths);

        using var transaction = _connection.BeginTransaction();
        var annotated = 0;

        foreach (var path in paths)
        {
            annotated += Execute(
                "UPDATE scrap_items SET comment = $comment WHERE scrapbook = $scrapbook AND path = $path;",
                transaction,
                ("$scrapbook", scrapbook),
                ("$path", path),

                // An empty note is no note, stored as NULL rather than as "", so the
                // display does not have to tell the two apart.
                ("$comment", string.IsNullOrWhiteSpace(comment) ? DBNull.Value : comment.Trim()));
        }

        transaction.Commit();
        return annotated;
    }

    public bool ClearScrapbook(string name) =>
        Execute("DELETE FROM scrap_items WHERE scrapbook = $name;", null, ("$name", name)) > 0;

    // ---- Recents

    public void RecordVisit(string directory) =>
        Execute(
            """
            INSERT INTO recents (directory, visited_utc) VALUES ($directory, $visited)
            ON CONFLICT(directory) DO UPDATE SET visited_utc = $visited;
            """,
            null,
            ("$directory", directory),
            ("$visited", DateTime.UtcNow.Ticks));

    public IReadOnlyList<string> RecentDirectories(int limit = 50) =>
        Query(
            "SELECT directory FROM recents ORDER BY visited_utc DESC LIMIT $limit;",
            ("$limit", Math.Max(1, limit)));

    // ---- Marks

    public void SaveMarks(IReadOnlyDictionary<string, Mark> marks)
    {
        ArgumentNullException.ThrowIfNull(marks);

        using var transaction = _connection.BeginTransaction();

        Execute("DELETE FROM marks;", transaction);

        foreach (var (name, mark) in marks)
        {
            Execute(
                """
                INSERT INTO marks (name, directory, cursor_name)
                VALUES ($name, $directory, $cursor);
                """,
                transaction,
                ("$name", name),
                ("$directory", mark.Directory),
                ("$cursor", (object?)mark.CursorName ?? DBNull.Value));
        }

        transaction.Commit();
    }

    public IReadOnlyDictionary<string, Mark> LoadMarks()
    {
        var marks = new Dictionary<string, Mark>(StringComparer.OrdinalIgnoreCase);

        using var command = Command("SELECT name, directory, cursor_name FROM marks;", null);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var name = reader.GetString(0);

            // Validated on the way back in as well as on the way out: a hand-edited database
            // is a normal thing to have, and a name :cd could never match would be a
            // quickmark the user can see listed and never reach.
            if (MarkFile.IsValidName(name))
            {
                marks[name] = new Mark(
                    reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
            }
        }

        return marks;
    }

    /// <summary>
    /// Brings the schema up to <see cref="SchemaVersion"/>.
    /// </summary>
    /// <remarks>
    /// Uses SQLite's own <c>user_version</c> rather than a table of applied migrations:
    /// there is one writer and one schema, and a whole migrations table would be
    /// bookkeeping for a number that SQLite already stores.
    /// </remarks>
    private void Migrate()
    {
        var current = Convert.ToInt32(
            Scalar("PRAGMA user_version;") ?? 0, System.Globalization.CultureInfo.InvariantCulture);

        if (current >= SchemaVersion)
        {
            return;
        }

        using var transaction = _connection.BeginTransaction();

        if (current < 1)
        {
            Execute(
                """
                CREATE TABLE sessions (
                    name       TEXT PRIMARY KEY,
                    active_tab INTEGER NOT NULL,
                    saved_utc  INTEGER NOT NULL
                );

                CREATE TABLE session_tabs (
                    session_name TEXT    NOT NULL REFERENCES sessions(name) ON DELETE CASCADE,
                    position     INTEGER NOT NULL,
                    directory    TEXT    NOT NULL,
                    cursor_name  TEXT,
                    selection    TEXT    NOT NULL DEFAULT '',
                    PRIMARY KEY (session_name, position)
                );

                CREATE TABLE scrap_items (
                    scrapbook TEXT    NOT NULL,
                    path      TEXT    NOT NULL,
                    added_utc INTEGER NOT NULL,
                    PRIMARY KEY (scrapbook, path)
                );

                CREATE TABLE recents (
                    directory   TEXT PRIMARY KEY,
                    visited_utc INTEGER NOT NULL
                );

                CREATE INDEX idx_recents_visited ON recents(visited_utc DESC);
                """,
                transaction);
        }

        if (current < 2)
        {
            Execute(
                """
                CREATE TABLE marks (
                    name        TEXT PRIMARY KEY,
                    directory   TEXT NOT NULL,
                    cursor_name TEXT
                );
                """,
                transaction);
        }

        if (current < 4)
        {
            // Two more ALTER TABLEs. Existing sessions come back unlocked and unnamed,
            // which is what they were.
            Execute("ALTER TABLE session_tabs ADD COLUMN locked INTEGER NOT NULL DEFAULT 0;", transaction);
            Execute("ALTER TABLE session_tabs ADD COLUMN tab_name TEXT;", transaction);
        }

        if (current < 3)
        {
            // Added rather than recreated: a user upgrading has scrapbooks worth keeping,
            // and ALTER TABLE ADD COLUMN is the one schema change SQLite does cheaply.
            Execute("ALTER TABLE scrap_items ADD COLUMN comment TEXT;", transaction);
        }

        // Interpolated because PRAGMA does not accept a parameter; the value is a private
        // constant, never user input.
        Execute($"PRAGMA user_version = {SchemaVersion};", transaction);
        transaction.Commit();
    }

    private SqliteCommand Command(
        string sql, SqliteTransaction? transaction, params (string Name, object Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }

    private int Execute(
        string sql, SqliteTransaction? transaction = null, params (string Name, object Value)[] parameters)
    {
        using var command = Command(sql, transaction, parameters);
        return command.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var command = Command(sql, null);
        return command.ExecuteScalar();
    }

    private List<string> Query(string sql, params (string Name, object Value)[] parameters)
    {
        var results = new List<string>();

        using var command = Command(sql, null, parameters);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }
}
