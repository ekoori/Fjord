using Fjord.Commands;
using Fjord.Core.Diagnostics;
using Fjord.Core.Selection;
using Fjord.Core.Storage;
using Fjord.Storage;
using Fjord.Tests.Commands;

namespace Fjord.Tests.Storage;

/// <summary>Records what the commands asked the window to do.</summary>
internal sealed class FakeSessionHost : ISessionHost, IScrapbookHost
{
    public SavedSession Captured { get; set; } =
        new("x", [new SavedTab("/a", null, [])], 0);

    public SavedSession? Restored { get; private set; }

    public (string Name, IReadOnlyList<string> Paths)? Opened { get; private set; }

    public SavedSession Capture(string name) => Captured with { Name = name };

    public Task RestoreAsync(SavedSession session)
    {
        Restored = session;
        return Task.CompletedTask;
    }

    public Task OpenScrapbookAsync(
        string name,
        IReadOnlyList<string> paths,
        IReadOnlyDictionary<string, string>? notes = null)
    {
        Opened = (name, paths);
        Notes = notes;
        return Task.CompletedTask;
    }

    /// <summary>Notes the last opened scrapbook carried, keyed by path.</summary>
    public IReadOnlyDictionary<string, string>? Notes { get; private set; }

    /// <summary>The scrapbook names the last index listed, and what opening one does.</summary>
    public (IReadOnlyList<string> Names, IReadOnlyDictionary<string, string> Sizes, Func<string, Task> Open)? Index
    {
        get;
        private set;
    }

    public Task OpenScrapbookIndexAsync(
        IReadOnlyList<string> names,
        IReadOnlyDictionary<string, string> sizes,
        Func<string, Task> open)
    {
        Index = (names, sizes, open);
        return Task.CompletedTask;
    }
}

public class SessionCommandTests
{
    private static (CommandParser Parser, SqliteStorage Storage, FakeSessionHost Host) Setup()
    {
        var storage = SqliteStorage.InMemory();
        var host = new FakeSessionHost();
        var registry = new CommandRegistry();
        registry.Register(new SessionCommands(storage, host));
        return (new CommandParser(registry), storage, host);
    }

    private static async Task Run(CommandParser parser, string line, IMessageSink messages)
    {
        foreach (var command in parser.Parse(line, new CommandContext { Messages = messages }))
        {
            if (command.Invoke() is Task task)
            {
                await task;
            }
        }
    }

    [Fact]
    public async Task SavingAndLoadingANamedSessionRoundTrips()
    {
        var (parser, storage, host) = Setup();
        var messages = new RecordingMessageSink();
        host.Captured = new SavedSession("ignored", [new SavedTab("/work", "a.cs", [])], 0);

        await Run(parser, "session-save work", messages);
        Assert.Contains("'work' saved", messages.Last!, StringComparison.Ordinal);

        await Run(parser, "session-load work", messages);
        Assert.Equal("/work", host.Restored!.Tabs[0].Directory);

        storage.Dispose();
    }

    [Fact]
    public async Task AnUnnamedSaveUsesTheAutomaticSession()
    {
        var (parser, storage, _) = Setup();

        await Run(parser, "session-save", new RecordingMessageSink());

        Assert.Contains(SavedSession.DefaultName, storage.SessionNames());
        storage.Dispose();
    }

    [Fact]
    public async Task LoadingAMissingSessionWarnsRatherThanFailing()
    {
        var (parser, storage, host) = Setup();
        var messages = new RecordingMessageSink();

        await Run(parser, "session-load nope", messages);

        Assert.Null(host.Restored);
        Assert.Equal(MessageLevel.Warning, messages.Messages[^1].Level);
        storage.Dispose();
    }

    [Fact]
    public async Task TheAutomaticSessionIsNotListedAsANamedOne()
    {
        // It is an implementation detail, not something to load by name.
        var (parser, storage, _) = Setup();
        var messages = new RecordingMessageSink();

        await Run(parser, "session-save", messages);
        await Run(parser, "session-save mine", messages);
        await Run(parser, "session-list", messages);

        Assert.Contains("mine", messages.Last!, StringComparison.Ordinal);
        Assert.DoesNotContain(SavedSession.DefaultName, messages.Last!, StringComparison.Ordinal);
        storage.Dispose();
    }

    [Fact]
    public async Task SessionsCanBeDeleted()
    {
        var (parser, storage, _) = Setup();
        var messages = new RecordingMessageSink();

        await Run(parser, "session-save gone", messages);
        await Run(parser, "session-delete gone", messages);

        Assert.Contains("Deleted", messages.Last!, StringComparison.Ordinal);
        Assert.Empty(storage.SessionNames());
        storage.Dispose();
    }

    [Fact]
    public async Task RecentReportsVisitedDirectories()
    {
        var (parser, storage, _) = Setup();
        storage.RecordVisit("/one");
        var messages = new RecordingMessageSink();

        await Run(parser, "recent", messages);

        Assert.Contains("/one", messages.Last!, StringComparison.Ordinal);
        storage.Dispose();
    }
}

public class ScrapbookCommandTests
{
    private static (CommandParser Parser, SqliteStorage Storage, FakeSessionHost Host) Setup()
    {
        var storage = SqliteStorage.InMemory();
        var host = new FakeSessionHost();
        var registry = new CommandRegistry();
        registry.Register(new ScrapbookCommands(storage, host));
        return (new CommandParser(registry), storage, host);
    }

    private static FakePane Pane() => new("/tmp", "a.txt", "b.txt", "c.txt");

    private static async Task Run(
        CommandParser parser, string line, FakePane pane, IMessageSink messages, EntryRange? range = null)
    {
        var context = new CommandContext { Pane = pane, Messages = messages, Range = range };

        foreach (var command in parser.Parse(line, context))
        {
            if (command.Invoke() is Task task)
            {
                await task;
            }
        }
    }

    [Fact]
    public async Task AddingUsesTheSelectionWhenThereIsOne()
    {
        var (parser, storage, _) = Setup();
        var pane = Pane();
        pane.Selection.SetRange(0, 1, true);
        var messages = new RecordingMessageSink();

        await Run(parser, "scrap-add", pane, messages);

        Assert.Equal(2, storage.Scrapbook("default").Count);
        storage.Dispose();
    }

    [Fact]
    public async Task AddingFallsBackToTheCursor()
    {
        var (parser, storage, _) = Setup();
        var pane = Pane();
        pane.CursorIndex = 2;

        await Run(parser, "scrap-add", pane, new RecordingMessageSink());

        Assert.Equal(
            [Path.Combine("/tmp", "c.txt")],
            storage.Scrapbook("default").Select(i => i.Path));

        storage.Dispose();
    }

    [Fact]
    public async Task AddingTheSameItemTwiceIsReportedHonestly()
    {
        // Adding a directory twice should say what happened, not report the same number
        // again as if it worked.
        var (parser, storage, _) = Setup();
        var pane = Pane();
        pane.Selection.SetRange(0, 1, true);
        var messages = new RecordingMessageSink();

        await Run(parser, "scrap-add", pane, messages);
        await Run(parser, "scrap-add", pane, messages);

        Assert.Contains("already there", messages.Last!, StringComparison.Ordinal);
        Assert.Equal(2, storage.Scrapbook("default").Count);
        storage.Dispose();
    }

    [Fact]
    public async Task ScrapbooksCanBeNamed()
    {
        var (parser, storage, _) = Setup();

        await Run(parser, "scrap-add photos", Pane(), new RecordingMessageSink());

        Assert.Single(storage.Scrapbook("photos"));
        Assert.Empty(storage.Scrapbook("default"));
        storage.Dispose();
    }

    [Fact]
    public async Task AddRangeIsAnOperator()
    {
        var (parser, storage, _) = Setup();

        await Run(parser, "scrap-add-range", Pane(), new RecordingMessageSink(), new EntryRange(0, 2));

        Assert.Equal(3, storage.Scrapbook("default").Count);
        storage.Dispose();
    }

    [Fact]
    public async Task OpeningShowsTheCollectedPaths()
    {
        var (parser, storage, host) = Setup();
        storage.AddToScrapbook("default", ["/a/one.txt", "/b/two.txt"]);

        await Run(parser, "scrap-open", Pane(), new RecordingMessageSink());

        Assert.Equal("default", host.Opened!.Value.Name);
        Assert.Equal(["/a/one.txt", "/b/two.txt"], host.Opened.Value.Paths);
        storage.Dispose();
    }

    [Fact]
    public async Task OpeningAnEmptyScrapbookWarnsInsteadOfShowingNothing()
    {
        var (parser, storage, host) = Setup();
        var messages = new RecordingMessageSink();

        await Run(parser, "scrap-open", Pane(), messages);

        Assert.Null(host.Opened);
        Assert.Equal(MessageLevel.Warning, messages.Messages[^1].Level);
        storage.Dispose();
    }

    [Fact]
    public async Task ItemsCanBeRemovedAndTheBookCleared()
    {
        var (parser, storage, _) = Setup();
        var pane = Pane();
        pane.Selection.SetRange(0, 2, true);
        var messages = new RecordingMessageSink();

        await Run(parser, "scrap-add", pane, messages);
        Assert.Equal(3, storage.Scrapbook("default").Count);

        pane.Selection.Clear();
        pane.Selection[0] = true;
        await Run(parser, "scrap-remove", pane, messages);
        Assert.Equal(2, storage.Scrapbook("default").Count);

        await Run(parser, "scrap-clear", pane, messages);
        Assert.Empty(storage.Scrapbook("default"));
        storage.Dispose();
    }

    [Fact]
    public async Task ListingOpensTheScrapbooksAsAListing()
    {
        // A line of text was something you read a name from and typed back. A listing is
        // something you pick from, which is what a list of collections is for.
        var (parser, storage, host) = Setup();
        storage.AddToScrapbook("photos", ["/a", "/b"]);
        storage.AddToScrapbook("todo", ["/c"]);

        await Run(parser, "scrap-list", Pane(), new RecordingMessageSink());

        Assert.NotNull(host.Index);
        Assert.Equal(["photos", "todo"], host.Index!.Value.Names);
        Assert.Equal("2 items", host.Index.Value.Sizes["photos"]);
        Assert.Equal("1 item", host.Index.Value.Sizes["todo"]);
        storage.Dispose();
    }

    [Fact]
    public async Task EnterOnAScrapbookRowOpensThatScrapbook()
    {
        // The half that makes it a listing rather than a picture of one.
        var (parser, storage, host) = Setup();
        storage.AddToScrapbook("photos", ["/a", "/b"]);

        await Run(parser, "scrap-list", Pane(), new RecordingMessageSink());
        await host.Index!.Value.Open("photos");

        Assert.Equal("photos", host.Opened?.Name);
        Assert.Equal(["/a", "/b"], host.Opened?.Paths);
        storage.Dispose();
    }

    [Fact]
    public async Task NoScrapbooksSaysSoRatherThanOpeningAnEmptyListing()
    {
        var (parser, storage, host) = Setup();
        var messages = new RecordingMessageSink();

        await Run(parser, "scrap-list", Pane(), messages);

        Assert.Null(host.Index);
        Assert.Contains("No scrapbooks", messages.Last!, StringComparison.Ordinal);
        storage.Dispose();
    }

    [Fact]
    public async Task ANoteIsStoredAgainstTheEntryAndTravelsWithIt()
    {
        var (parser, storage, host) = Setup();
        var pane = Pane();
        storage.AddToScrapbook("default", [pane.PathAt(0)!]);

        await Run(parser, "scrap-note keep for the report", pane, new RecordingMessageSink());
        await Run(parser, "scrap-open", pane, new RecordingMessageSink());

        Assert.Equal("keep for the report", host.Notes?[pane.PathAt(0)!]);
        storage.Dispose();
    }

    [Fact]
    public async Task AnEmptyNoteClearsIt()
    {
        var (parser, storage, host) = Setup();
        var pane = Pane();
        storage.AddToScrapbook("default", [pane.PathAt(0)!]);

        await Run(parser, "scrap-note something", pane, new RecordingMessageSink());
        await Run(parser, "scrap-note", pane, new RecordingMessageSink());
        await Run(parser, "scrap-open", pane, new RecordingMessageSink());

        Assert.Empty(host.Notes!);
        storage.Dispose();
    }

    [Fact]
    public async Task NotingSomethingThatIsNotCollectedSaysSo()
    {
        // Silence here would look exactly like success, and the note would be gone.
        var (parser, storage, _) = Setup();
        var messages = new RecordingMessageSink();

        await Run(parser, "scrap-note never collected", Pane(), messages);

        Assert.Contains("is in", messages.Last!, StringComparison.Ordinal);
        Assert.Equal(MessageLevel.Error, messages.Messages[^1].Level);
        storage.Dispose();
    }
}
