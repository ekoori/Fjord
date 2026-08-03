using Fjord.Commands;

namespace Fjord.Tests.Commands;

public class CommandHistoryTests
{
    private static CommandHistory Filled(params string[] lines)
    {
        var history = new CommandHistory();
        foreach (var line in lines)
        {
            history.Add(line);
        }

        return history;
    }

    [Fact]
    public void WalksBackwardsThenForwards()
    {
        var history = Filled("cd a", "sort", "cd b");

        Assert.Equal("cd b", history.Previous(""));
        Assert.Equal("sort", history.Previous(""));
        Assert.Equal("cd a", history.Previous(""));
        Assert.Null(history.Previous(""));

        Assert.Equal("sort", history.Next());
        Assert.Equal("cd b", history.Next());
    }

    [Fact]
    public void WalkingPastTheNewestRestoresTheHalfTypedLine()
    {
        // Otherwise the user is left staring at someone else's command with no way back.
        var history = Filled("cd alpha");

        Assert.Equal("cd alpha", history.Previous("cd "));
        Assert.Equal("cd ", history.Next());
        Assert.False(history.IsWalking);
    }

    [Fact]
    public void WalkingIsFilteredByThePrefix()
    {
        // What makes history usable once it is more than a handful of entries.
        var history = Filled("cd alpha", "sort", "cd beta", "reload");

        Assert.Equal("cd beta", history.Previous("cd"));
        Assert.Equal("cd alpha", history.Previous("cd"));
        Assert.Null(history.Previous("cd"));
    }

    [Fact]
    public void ThePrefixIsCapturedOnceNotRereadEachStep()
    {
        // The text is being replaced as the walk proceeds, so re-reading it would filter
        // against the previous result and the walk would stall after one step.
        var history = Filled("cd alpha", "cd beta");

        Assert.Equal("cd beta", history.Previous("cd"));
        Assert.Equal("cd alpha", history.Previous("cd beta"));
    }

    [Fact]
    public void BlankLinesAreNotRecorded()
    {
        var history = Filled("   ", "");

        Assert.Empty(history.Entries);
    }

    [Fact]
    public void RepeatingTheNewestEntryDoesNotDuplicateIt()
    {
        var history = Filled("sort", "sort");

        Assert.Single(history.Entries);
    }

    [Fact]
    public void OldestEntriesFallOffAtCapacity()
    {
        var history = new CommandHistory(capacity: 2);
        history.Add("a");
        history.Add("b");
        history.Add("c");

        Assert.Equal(["b", "c"], history.Entries);
    }

    [Fact]
    public void AddingResetsAnInProgressWalk()
    {
        var history = Filled("a", "b");
        history.Previous("");

        history.Add("c");

        Assert.False(history.IsWalking);
        Assert.Equal("c", history.Previous(""));
    }

    [Fact]
    public void NextWithoutAWalkDoesNothing() => Assert.Null(Filled("a").Next());

    [Fact]
    public void CapacityMustBePositive() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new CommandHistory(0));
}

/// <summary>A path source with a fixed tree, so completion is tested without touching disk.</summary>
file sealed class FakePaths : IPathCompletionSource
{
    private readonly Dictionary<string, (string[] Dirs, string[] Files)> _tree = new(StringComparer.Ordinal)
    {
        ["/base"] = (["src", "scripts", "tests"], ["README.md", "Fjord.slnx"]),
        ["/base/src"] = (["Fjord.Core", "Fjord.App"], []),
    };

    public IEnumerable<string> List(string directory, bool directoriesOnly)
    {
        if (!_tree.TryGetValue(directory, out var entry))
        {
            return [];
        }

        var dirs = entry.Dirs.Select(d => d + "/");
        return directoriesOnly ? dirs : dirs.Concat(entry.Files);
    }

    public string Resolve(string path) =>
        path is "." or "" ? "/base" : "/base/" + path.TrimEnd('/', '\\');
}

public class CompletionEngineTests
{
    private static CompletionEngine Engine()
    {
        var registry = new CommandRegistry();
        registry.Register(new NavigationCommands());
        registry.Register(new ModeCommands(new FakeModeHost()));
        return new CompletionEngine(registry, new FakePaths());
    }

    private static List<string> Texts(CompletionResult result) =>
        result.Items.Select(i => i.Text).ToList();

    [Fact]
    public void CompletesCommandNames()
    {
        var result = Engine().Complete(":mo", 3);

        Assert.All(Texts(result), t => Assert.StartsWith("mo", t, StringComparison.Ordinal));
        Assert.Contains("move-down", Texts(result));
        Assert.Contains("move-first", Texts(result));
    }

    [Fact]
    public void LeadingColonIsOptional()
    {
        Assert.Contains("reload", Texts(Engine().Complete("rel", 3)));
    }

    [Fact]
    public void EmptyLineOffersEverything()
    {
        Assert.Contains("sort", Texts(Engine().Complete(":", 1)));
    }

    [Fact]
    public void HiddenCommandsAreNotOffered()
    {
        Assert.DoesNotContain(Texts(Engine().Complete(":", 1)), t => t.StartsWith("hidden", StringComparison.Ordinal));
    }

    [Fact]
    public void CompletesDeclaredChoices()
    {
        var result = Engine().Complete(":sort -k s", 10);

        Assert.Equal(["size"], Texts(result));
    }

    [Fact]
    public void CompletesFlags()
    {
        var result = Engine().Complete(":sort --", 8);

        Assert.Contains("--key", Texts(result));
        Assert.Contains("--reverse", Texts(result));
    }

    [Fact]
    public void CompletesDirectoriesOnlyForCd()
    {
        var result = Engine().Complete(":cd s", 5);

        Assert.Equal(["scripts/", "src/"], Texts(result));
        Assert.DoesNotContain("README.md", Texts(result));
    }

    [Fact]
    public void CompletingKeepsTheTypedDirectoryPrefix()
    {
        // Completing "src/Fj" must stay relative, not jump to an absolute path.
        var result = Engine().Complete(":cd src/Fj", 10);

        Assert.Equal(["src/Fjord.App/", "src/Fjord.Core/"], Texts(result));
    }

    [Fact]
    public void ApplyReplacesOnlyTheWordUnderTheCaret()
    {
        var engine = Engine();
        const string line = ":cd s";
        var result = engine.Complete(line, line.Length);

        var (text, caret) = result.Apply(line, result.Items[0]);

        Assert.Equal(":cd scripts/", text);
        Assert.Equal(text.Length, caret);
    }

    [Fact]
    public void CyclingAppliesEachCandidateToTheOriginalLine()
    {
        // The span describes the line the result was computed from, so cycling must
        // re-apply to that original. Applying the second candidate to what the first
        // produced overwrites the wrong characters and yields ":cd src/cripts/".
        var engine = Engine();
        const string line = ":cd s";
        var result = engine.Complete(line, line.Length);

        Assert.Equal(":cd scripts/", result.Apply(line, result.Items[0]).Text);
        Assert.Equal(":cd src/", result.Apply(line, result.Items[1]).Text);
    }

    [Fact]
    public void NoHistoryEntryMatchingThePrefixMeansNoWalk()
    {
        // Pressing Up with text that matches nothing should do nothing, not start an
        // empty walk that Down then has to unwind.
        var history = new CommandHistory();
        history.Add("sort");

        Assert.Null(history.Previous("cd "));
        Assert.False(history.IsWalking);
        Assert.Null(history.Next());
    }

    [Fact]
    public void CandidatesWithSpacesAreQuotedOnInsertion()
    {
        var item = new CompletionItem("My Documents/");

        Assert.Equal("'My Documents/'", item.Insertion);
        Assert.Equal(["My Documents/"], CommandLexer.Split(item.Insertion));
    }

    [Fact]
    public void UnknownCommandOffersNothing()
    {
        Assert.True(Engine().Complete(":nonsense arg", 13).IsEmpty);
    }

    [Fact]
    public void CompletingMidLineIgnoresWhatFollowsTheCaret()
    {
        const string line = ":cd s extra";
        var result = Engine().Complete(line, 5);

        Assert.Equal(["scripts/", "src/"], Texts(result));

        var (text, _) = result.Apply(line, result.Items[1]);
        Assert.Equal(":cd src/ extra", text);
    }

    [Fact]
    public void FlagValuesAreNotCountedAsPositionalArguments()
    {
        // Otherwise the caret would be treated as sitting in the second positional
        // argument when it is still in the first.
        var registry = new CommandRegistry();
        registry.Register(new NavigationCommands());
        var engine = new CompletionEngine(registry, new FakePaths());

        var result = engine.Complete(":sort -k size ", 14);

        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void AnEngineWithoutAPathSourceOffersNoPaths()
    {
        var registry = new CommandRegistry();
        registry.Register(new NavigationCommands());

        Assert.True(new CompletionEngine(registry).Complete(":cd s", 5).IsEmpty);
    }
}
