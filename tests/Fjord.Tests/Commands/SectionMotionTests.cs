using Fjord.Commands;
using Fjord.Core.FileSystem;
using Fjord.Tests.Commands;

namespace Fjord.Tests.Motions;

/// <summary>
/// <c>gg</c> and <c>G</c> in a listing that has directories at the top.
/// </summary>
/// <remarks>
/// With directories first the listing is two halves, and the top of the half you are in is
/// usually nearer what you meant. Pressing again reaches the very top, so the old
/// behaviour is one extra keystroke away rather than gone.
/// </remarks>
public class SectionMotionTests
{
    /// <summary>Two directories then two files, which is what the comparator produces.</summary>
    private static FakePane Mixed()
    {
        var pane = new FakePane("/tmp");
        pane.AddDirectory("alpha");
        pane.AddDirectory("beta");
        pane.AddFile("one.txt");
        pane.AddFile("two.txt");
        return pane;
    }

    private static CommandParser Parser()
    {
        var registry = new CommandRegistry();
        registry.Register(new NavigationCommands());
        return new CommandParser(registry);
    }

    private static void Run(CommandParser parser, string line, FakePane pane) =>
        parser.Execute(line, new CommandContext { Pane = pane });

    [Fact]
    public void FirstGoesToTheTopOfTheFilesThenTheTopOfEverything()
    {
        var parser = Parser();
        var pane = Mixed();
        pane.CursorIndex = 3;

        Run(parser, "move-first", pane);
        Assert.Equal(2, pane.CursorIndex);

        Run(parser, "move-first", pane);
        Assert.Equal(0, pane.CursorIndex);
    }

    [Fact]
    public void LastGoesToTheEndOfTheDirectoriesThenTheEndOfEverything()
    {
        var parser = Parser();
        var pane = Mixed();
        pane.CursorIndex = 0;

        Run(parser, "move-last", pane);
        Assert.Equal(1, pane.CursorIndex);

        Run(parser, "move-last", pane);
        Assert.Equal(3, pane.CursorIndex);
    }

    [Fact]
    public void FromTheTopOfTheFilesTheNextPressReachesTheTop()
    {
        var parser = Parser();
        var pane = Mixed();
        pane.CursorIndex = 2;

        Run(parser, "move-first", pane);
        Assert.Equal(0, pane.CursorIndex);
    }

    [Fact]
    public void WithDirectoriesFirstOffThereIsOneSection()
    {
        // No halves to stop at, so the motions go the whole way the first time.
        var parser = Parser();
        var pane = Mixed();
        pane.SetOrder(SortSpec.Default with { DirectoriesFirst = false }, FilterSpec.Default);
        pane.CursorIndex = 3;

        Run(parser, "move-first", pane);
        Assert.Equal(0, pane.CursorIndex);
    }

    [Fact]
    public void AListingOfFilesAloneHasOneSection()
    {
        var parser = Parser();
        var pane = new FakePane("/tmp", "a.txt", "b.txt", "c.txt");
        pane.CursorIndex = 2;

        Run(parser, "move-first", pane);
        Assert.Equal(0, pane.CursorIndex);
    }

    [Fact]
    public void AListingOfFoldersAloneHasOneSection()
    {
        var parser = Parser();
        var pane = new FakePane("/tmp");
        pane.AddDirectory("a");
        pane.AddDirectory("b");
        pane.CursorIndex = 0;

        Run(parser, "move-last", pane);
        Assert.Equal(1, pane.CursorIndex);
    }

    [Fact]
    public void ACountStillNamesTheRow()
    {
        var parser = Parser();
        var pane = Mixed();

        parser.Execute("move-first", new CommandContext { Pane = pane, Count = 3 });
        Assert.Equal(2, pane.CursorIndex);
    }
}
