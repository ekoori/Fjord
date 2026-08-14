using Fjord.Core.FileSystem;

namespace Fjord.Tests.FileSystem;

public class RecursiveSearchTests
{
    private static TempDirectory Tree()
    {
        var temp = new TempDirectory();
        temp.CreateFile("readme.md");
        temp.CreateFile(Path.Combine("src", "Program.cs"));
        temp.CreateFile(Path.Combine("src", "core", "Entry.cs"));
        temp.CreateFile(Path.Combine("src", "core", "deep", "Nested.cs"));
        temp.CreateFile(Path.Combine("docs", "notes.md"));
        return temp;
    }

    [Fact]
    public void HitsComeBackRelativeToWhereTheSearchStarted()
    {
        // The absolute path would be the same forty characters on every row, with the part
        // worth reading — which folder it is in — pushed off the end of the column.
        using var temp = Tree();

        var results = RecursiveSearch.Find(temp.Path, "Nested");

        Assert.Equal([Path.Combine("src", "core", "deep", "Nested.cs")], results.Paths);
    }

    [Fact]
    public void AnEntryDirectlyInsideIsJustItsName()
    {
        using var temp = Tree();

        var results = RecursiveSearch.Find(temp.Path, "readme");

        Assert.Equal(["readme.md"], results.Paths);
    }

    [Fact]
    public void MatchingIsFuzzyOnTheNameAlone()
    {
        // Not on the relative path: a pattern typed to find a file would otherwise score
        // for the letters of every folder above it, and anything deep in a tree would
        // match almost everything.
        using var temp = Tree();

        var results = RecursiveSearch.Find(temp.Path, "core");

        Assert.DoesNotContain(Path.Combine("src", "core", "Entry.cs"), results.Paths);
    }

    [Fact]
    public void SubsequenceMatchingStillApplies()
    {
        using var temp = Tree();

        var results = RecursiveSearch.Find(temp.Path, "prg");

        Assert.Contains(Path.Combine("src", "Program.cs"), results.Paths);
    }

    [Fact]
    public void EveryMatchAnywhereUnderneathIsFound()
    {
        using var temp = Tree();

        var results = RecursiveSearch.Find(temp.Path, ".cs");

        Assert.Equal(3, results.Paths.Count);
    }

    [Fact]
    public void DirectoriesAreMatchedToo()
    {
        // Looking for a folder is as ordinary as looking for a file, and Enter on the row
        // goes into it. It does mean a loose pattern picks up folders the eye did not
        // think of — "cs" is a subsequence of "docs" — which is the fuzzy bargain.
        using var temp = Tree();

        var results = RecursiveSearch.Find(temp.Path, "docs");

        Assert.Equal(["docs"], results.Paths);
    }

    [Fact]
    public void HiddenEntriesAreLeftOutUnlessAskedFor()
    {
        // Dotted, because that is what "hidden" means on Unix. Named secret.cs it is an
        // ordinary file there, and the test passes on Windows and fails in CI.
        using var temp = new TempDirectory();
        temp.CreateHiddenFile(".secret.cs");
        temp.CreateFile("plain.cs");

        Assert.Equal(["plain.cs"], RecursiveSearch.Find(temp.Path, "cs").Paths);
        Assert.Equal(2, RecursiveSearch.Find(temp.Path, "cs", includeHidden: true).Paths.Count);
    }

    [Fact]
    public void AnEmptyPatternFindsNothingRatherThanEverything()
    {
        // A fuzzy match against "" succeeds for every candidate, so without this the
        // command would answer "here is your entire disk".
        using var temp = Tree();

        Assert.Empty(RecursiveSearch.Find(temp.Path, string.Empty).Paths);
    }

    [Fact]
    public void ADirectoryThatIsNotThereReportsNothingRatherThanThrowing()
    {
        using var temp = new TempDirectory();

        var results = RecursiveSearch.Find(Path.Combine(temp.Path, "gone"), "x");

        Assert.Empty(results.Paths);
    }

    [Fact]
    public void CancellationStopsTheWalk()
    {
        using var temp = Tree();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => RecursiveSearch.Find(temp.Path, "cs", ct: cancelled.Token));
    }

    [Fact]
    public void TheBestMatchComesFirst()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("a-b-c.txt");
        temp.CreateFile("abc.txt");

        var results = RecursiveSearch.Find(temp.Path, "abc");

        Assert.Equal("abc.txt", results.Paths[0]);
    }
}
