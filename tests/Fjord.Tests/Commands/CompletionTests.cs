using Fjord.Commands;
using Fjord.Core.Input;

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
        registry.Register(new TabCommands());
        registry.Register(new SearchCommands(new FakeModeHost()));
        registry.Register(new SessionCommands(
            Fjord.Storage.SqliteStorage.InMemory(), new Fjord.Tests.Storage.FakeSessionHost()));
        registry.Register(new ScrapbookCommands(
            Fjord.Storage.SqliteStorage.InMemory(), new Fjord.Tests.Storage.FakeSessionHost()));
        registry.Register(new PlatformCommands(new Fjord.Tests.Platform.FakePlatform()));
        registry.Register(new OperationCommands(
            new Fjord.Tests.Operations.FakeOperationHost(),
            new Fjord.Tests.Operations.FakeOperationFactory()));
        registry.Register(new HelpCommands(
            registry,
            new KeyBindings(),
            new KeyModeRegistry(),
            new FakeHelpView(),
            new FakeConfigService(),
            new Fjord.Tests.Platform.FakePlatform()));
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

public class CommandHistoryWindowTests
{
    private static CommandHistory Filled(int count)
    {
        var history = new CommandHistory();

        for (var i = 0; i < count; i++)
        {
            history.Add($"cd dir{i}");
        }

        return history;
    }

    [Fact]
    public void NotWalkingMeansNothingIsCurrent()
    {
        // The panel reads this as "at the end": newest entries on screen, nothing
        // highlighted, because the user is typing rather than choosing.
        Assert.Null(Filled(3).CurrentIndex);
    }

    [Fact]
    public void WalkingBackMovesTheCurrentIndexTowardsTheOldest()
    {
        var history = Filled(3);

        history.Previous(string.Empty);
        Assert.Equal(2, history.CurrentIndex);

        history.Previous(string.Empty);
        Assert.Equal(1, history.CurrentIndex);
    }

    [Fact]
    public void WalkingForwardAgainEndsWithNothingCurrent()
    {
        var history = Filled(3);

        history.Previous(string.Empty);
        history.Next();

        Assert.Null(history.CurrentIndex);
    }

    [Fact]
    public void LoadReplacesEverythingAndEndsTheWalk()
    {
        var history = Filled(3);
        history.Previous(string.Empty);

        history.Load(["one", "two"]);

        Assert.Equal(["one", "two"], history.Entries);
        Assert.Null(history.CurrentIndex);
    }

    [Fact]
    public void LoadIgnoresBlankLinesAndTrims()
    {
        // The file is the user's to edit by hand, so it will have blank lines in it.
        var history = new CommandHistory();

        history.Load(["  cd work  ", string.Empty, "   ", "quit"]);

        Assert.Equal(["cd work", "quit"], history.Entries);
    }

    [Fact]
    public void LoadTrimsToCapacity()
    {
        // A file that grew under a larger limit should not stay large forever.
        var history = new CommandHistory(capacity: 2);

        history.Load(["one", "two", "three"]);

        Assert.Equal(["two", "three"], history.Entries);
    }

    [Fact]
    public void TheWalkReachesEveryEntryHoweverLongTheHistoryIs()
    {
        // The panel shows ten rows; the history is longer than that on purpose.
        var history = Filled(30);
        var seen = new List<int>();

        for (var i = 0; i < 30; i++)
        {
            history.Previous(string.Empty);
            seen.Add(history.CurrentIndex!.Value);
        }

        Assert.Equal(29, seen[0]);
        Assert.Equal(0, seen[^1]);
    }
}

public class BarePromptTests
{
    /// <summary>The rule the command line applies before deciding what the panel shows.</summary>
    /// <remarks>
    /// Mirrored here rather than reached through the control, which needs Avalonia. The
    /// behaviour it guards is worth pinning: an empty line matches every command, and
    /// sixty rows of them on opening the bar is what made the panel feel like noise.
    /// </remarks>
    private static bool IsBare(string line) => line.TrimStart().TrimStart(':', '/').Trim().Length == 0;

    [Theory]
    [InlineData(":")]
    [InlineData("/")]
    [InlineData("  :  ")]
    [InlineData("")]
    public void APromptWithNothingTypedIsBare(string line) => Assert.True(IsBare(line));

    [Theory]
    [InlineData(":c")]
    [InlineData(":cd ")]
    [InlineData("/report")]
    public void AnythingTypedIsNot(string line) => Assert.False(IsBare(line));

    [Fact]
    public void AnEmptyLineReallyDoesMatchEverything()
    {
        // Why the rule exists. If this ever stopped being true the rule would be pointless
        // rather than wrong, and it should be revisited.
        var registry = new CommandRegistry();
        registry.Register(new NavigationCommands());

        var result = new CompletionEngine(registry).Complete(":", 1);

        Assert.Equal(registry.Commands.Count(), result.Items.Count);
    }
}
