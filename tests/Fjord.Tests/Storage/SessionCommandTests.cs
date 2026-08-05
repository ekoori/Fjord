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

    public Task OpenScrapbookAsync(string name, IReadOnlyList<string> paths)
    {
        Opened = (name, paths);
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
    public async Task ListingShowsScrapbooksAndSizes()
    {
        var (parser, storage, _) = Setup();
        storage.AddToScrapbook("photos", ["/a", "/b"]);
        var messages = new RecordingMessageSink();

        await Run(parser, "scrap-list", Pane(), messages);

        Assert.Contains("photos (2)", messages.Last!, StringComparison.Ordinal);
        storage.Dispose();
    }
}
