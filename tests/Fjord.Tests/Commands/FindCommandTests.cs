using Fjord.Commands;
using Fjord.Core.Diagnostics;
using Fjord.Tests.FileSystem;

namespace Fjord.Tests.Commands;

/// <summary>
/// <c>:find</c>, which walks the disk rather than filtering what is on screen.
/// </summary>
/// <remarks>
/// Against a real temporary tree, because the whole point of the command is what it does
/// to a filesystem — a fake one would only prove the arguments were passed along.
/// </remarks>
public class FindCommandTests
{
    private static (CommandParser Parser, RecordingMessageSink Messages) Setup()
    {
        var registry = new CommandRegistry();
        registry.Register(new SearchCommands(new FakeModeHost()));
        return (new CommandParser(registry), new RecordingMessageSink());
    }

    private static async Task RunAsync(
        CommandParser parser, string line, FakePane pane, IMessageSink messages)
    {
        var context = new CommandContext { Pane = pane, Messages = messages };

        foreach (var command in parser.Parse(line, context))
        {
            if (command.Invoke() is Task task)
            {
                await task;
            }
        }
    }

    private static TempDirectory Tree()
    {
        var temp = new TempDirectory();
        temp.CreateFile(Path.Combine("src", "core", "Entry.cs"));
        temp.CreateFile("readme.md");
        return temp;
    }

    [Fact]
    public async Task ResultsOpenAsAListingRootedWhereTheSearchStarted()
    {
        using var temp = Tree();
        var (parser, messages) = Setup();
        var pane = new FakePane(temp.Path);

        await RunAsync(parser, "find Entry", pane, messages);

        Assert.NotNull(pane.Shown);

        // The root is what makes the relative names resolve back to real files, so the
        // cursor, Enter and every operator work on the results like any other listing.
        Assert.Equal(temp.Path, pane.Shown!.Value.Root);
        Assert.Equal([Path.Combine("src", "core", "Entry.cs")], pane.Shown.Value.Paths);
    }

    [Fact]
    public async Task NothingFoundLeavesTheListingAlone()
    {
        // Replacing the directory with an empty listing would lose the user's place to
        // tell them something a message says better.
        using var temp = Tree();
        var (parser, messages) = Setup();
        var pane = new FakePane(temp.Path);

        await RunAsync(parser, "find zzzznope", pane, messages);

        Assert.Null(pane.Shown);
        Assert.Contains("matches", messages.Last!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyPatternIsRefused()
    {
        // A fuzzy match against "" succeeds for every candidate, so running it would
        // answer with the entire tree.
        using var temp = Tree();
        var (parser, messages) = Setup();
        var pane = new FakePane(temp.Path);

        await RunAsync(parser, "find", pane, messages);

        Assert.Null(pane.Shown);
        Assert.Contains("pattern", messages.Last!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchingAScrapbookIsRefusedRatherThanReturningNothing()
    {
        // A virtual listing's Directory is a label, not a path. Walking it would find
        // nothing, which reads as "no matches" instead of "that is not a place".
        var (parser, messages) = Setup();
        var pane = new FakePane("/tmp") { IsVirtual = true };

        await RunAsync(parser, "find x", pane, messages);

        Assert.Null(pane.Shown);
        Assert.Contains("real directory", messages.Last!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchingInsideAnArchiveIsRefused()
    {
        var (parser, messages) = Setup();
        var pane = new FakePane(Path.Combine("/tmp", "src.zip!") + Path.DirectorySeparatorChar);

        await RunAsync(parser, "find x", pane, messages);

        Assert.Null(pane.Shown);
        Assert.Contains("real directory", messages.Last!, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyAScrapbookCountsAsAScrapbook()
    {
        // Search results and the drive list are virtual listings too. Treating them as
        // scrapbooks put the pane in a mode where dd means scrap-remove — so the key that
        // trashes a file everywhere else quietly did something else on a search result.
        var scrapbook = new FakePane("/tmp");
        scrapbook.SetDirectory(ScrapbookCommands.ScrapbookLabel("default"));
        scrapbook.IsVirtual = true;

        var results = new FakePane("/tmp");
        results.SetDirectory("find:Entry");
        results.IsVirtual = true;

        Assert.True(ScrapbookCommands.IsScrapbook(scrapbook));
        Assert.False(ScrapbookCommands.IsScrapbook(results));
        Assert.False(ScrapbookCommands.IsScrapbook(new FakePane("/tmp")));
    }

    [Fact]
    public async Task EnterOnAResultOpensTheRealFile()
    {
        // The names in a search result are relative, so joining them to the pane's
        // directory — a label like "find:Entry" — produced a path to nothing, and Enter
        // did nothing at all.
        using var temp = Tree();
        var (parser, messages) = Setup();
        var pane = new FakePane(temp.Path);

        await RunAsync(parser, "find Entry", pane, messages);

        Assert.Equal(
            Path.Combine(temp.Path, "src", "core", "Entry.cs"),
            pane.PathAt(pane.CursorIndex));
    }

    [Fact]
    public async Task GoingToTheFolderLandsOnTheFileItHolds()
    {
        // The usual next thing after finding something: what is around it.
        using var temp = Tree();
        var (parser, messages) = Setup();
        var pane = new FakePane(temp.Path);

        await RunAsync(parser, "find Entry", pane, messages);

        var navigation = new CommandRegistry();
        navigation.Register(new NavigationCommands());
        await RunAsync(new CommandParser(navigation), "goto-folder", pane, messages);

        Assert.Contains($"navigate:{Path.Combine(temp.Path, "src", "core")}", pane.Log);
    }
}
