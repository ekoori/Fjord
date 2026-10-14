using Fjord.Commands;
using Fjord.Core.Diagnostics;
using Fjord.Core.Input;
using Fjord.Core.Keys;

namespace Fjord.Tests.Commands;

public class DefaultBindingsTests
{
    private static CommandRegistry Registry()
    {
        // Both command sets, because the binding table spans them and the point of
        // EveryDefaultBindingResolvesAgainstTheRegistry is that nothing escapes it.
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
        return registry;
    }

    private static (KeyBindings Bindings, KeyModeRegistry Modes) Loaded()
    {
        var bindings = new KeyBindings();
        var modes = new KeyModeRegistry();
        DefaultBindings.Load(bindings, modes);
        return (bindings, modes);
    }

    [Fact]
    public void EveryDefaultBindingResolvesAgainstTheRegistry()
    {
        // The guard that matters: renaming a command without updating its binding would
        // otherwise ship a key that silently does nothing.
        var registry = Registry();
        var parser = new CommandParser(registry);
        var unresolved = new List<string>();

        foreach (var (mode, entries) in DefaultBindings.All)
        {
            foreach (var (keys, command) in entries)
            {
                // A ':' binding is text for the command line, not a command to resolve.
                // The command inside it still has to exist, though, or the key opens a
                // line that can only fail — so the colon is stripped and it is checked.
                var name = (CommandTemplate.IsCommandLine(command) ? command[1..] : command)
                    .Split(' ')[0];

                if (name.Length > 0 && registry.Find(name) is null)
                {
                    unresolved.Add($"{mode}/{keys} -> {command}");
                }
            }
        }

        Assert.Empty(unresolved);
        Assert.NotNull(parser);
    }

    [Fact]
    public void NoModeBindsTheSameKeysTwice()
    {
        // The table is a dictionary literal, so a duplicate is a TypeInitializationException
        // at first key press rather than a compile error. Nearly shipped one by rebinding
        // Ctrl-l to history-forward while search-clear still held it.
        foreach (var (mode, entries) in DefaultBindings.All)
        {
            var duplicates = entries.Keys
                .GroupBy(k => k, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => $"{mode}/{g.Key}");

            Assert.Empty(duplicates);
        }
    }

    [Fact]
    public void EveryDefaultBindingParsesAsAKeySequence()
    {
        foreach (var (mode, entries) in DefaultBindings.All)
        {
            foreach (var keys in entries.Keys)
            {
                var parsed = Record.Exception(() => KeySequence.Parse(keys));
                Assert.True(parsed is null, $"{mode}/{keys}: {parsed?.Message}");
            }
        }
    }

    [Fact]
    public void EveryModeNamedByTheDefaultsIsRegistered()
    {
        var modes = new KeyModeRegistry();

        foreach (var mode in DefaultBindings.All.Keys)
        {
            Assert.True(modes.TryParse(mode, out _), $"mode '{mode}' is not registered");
        }
    }

    [Fact]
    public void OperatorPendingModeOnlyBindsMotions()
    {
        // An operator waiting for its motion must not be able to receive a command that
        // is not one, or the dispatcher has nothing to measure.
        var registry = Registry();

        foreach (var (keys, command) in DefaultBindings.All["operator-pending"])
        {
            var definition = registry.Find(command.Split(' ')[0]);
            Assert.True(definition?.IsMotion, $"operator-pending/{keys} -> {command} is not a motion");
        }
    }

    [Fact]
    public void TheRegisterKeyReadsTheNextKeystroke()
    {
        // " is an ordinary binding rather than a character wired into the key handler,
        // which is what makes it movable. This checks the wiring survived.
        var host = new FakeModeHost();
        var registry = new CommandRegistry();
        registry.Register(new ModeCommands(host));
        var parser = new CommandParser(registry);

        parser.Execute(DefaultBindings.All["normal"]["\""], new CommandContext());

        Assert.Equal(["read-name:Register"], host.Actions);
    }

    [Theory]
    [InlineData("<F2>", ":rename ")]
    [InlineData("c", ":cd ")]
    [InlineData("m", ":mark-set ")]
    public void TheCommandLineKeysOpenTheLineRatherThanRun(string keys, string expected)
    {
        // The leading ':' is the whole mechanism, and it is what a user copies to write
        // their own. A default that lost it would run on key press instead.
        var command = DefaultBindings.All["normal"][keys];

        Assert.True(CommandTemplate.IsCommandLine(command));
        Assert.StartsWith(expected, command, StringComparison.Ordinal);
    }

    [Fact]
    public void ContinuationsListWhatCouldFollowAHalfTypedBinding()
    {
        // What the key-hint panel is built from: type g and be told gg and gs exist,
        // rather than having to remember or give up.
        var (bindings, _) = Loaded();

        var next = bindings.Continuations(KeyMode.Normal, KeySequence.Parse("g"));

        Assert.Contains(next, c => c.Keys.ToString() == "gg");
        Assert.Contains(next, c => c.Keys.ToString() == "gs");
        Assert.DoesNotContain(next, c => c.Keys.ToString() == "j");
    }

    [Fact]
    public void AKeyThatCompletesNothingHasNoContinuations()
    {
        var (bindings, _) = Loaded();

        Assert.Empty(bindings.Continuations(KeyMode.Normal, KeySequence.Parse("j")));
    }

    [Fact]
    public void AnEmptySequenceAsksForTheWholeMode()
    {
        // What operator-pending needs: after d, the useful answer is every motion the
        // operator will accept, and nothing has been typed towards one yet.
        var (bindings, _) = Loaded();

        var all = bindings.Continuations(KeyMode.Operator, KeySequence.Parse(""));

        Assert.Equal(DefaultBindings.All["operator-pending"].Count, all.Count);
    }

    [Fact]
    public void MotionsDoNotMutateTheSnapshot()
    {
        // OperatorDispatcher resolves a range by running the motion and diffing the
        // cursor. A motion with side effects would apply them twice — once to measure,
        // once for real. This is the test that keeps that assumption honest.
        var registry = Registry();
        var parser = new CommandParser(registry);

        foreach (var motion in registry.All.Where(c => c.IsMotion))
        {
            var pane = new FakePane("/tmp", "a", "b", "c", "d", "e");
            var before = pane.Snapshot!.View;

            parser.Execute(motion.Name, new CommandContext { Count = 2, Pane = pane });

            Assert.True(
                pane.Snapshot.View.IsSameAs(before),
                $"motion '{motion.Name}' replaced the snapshot view");
            Assert.Empty(pane.Log);
            Assert.Equal(0, pane.Selection.Count);
        }
    }

    [Fact]
    public void LoadInstallsBindingsIntoTheRightModes()
    {
        var (bindings, _) = Loaded();

        Assert.Equal("move-down", bindings.Match(KeyMode.Normal, KeySequence.Parse("j")).Command);
        Assert.Equal("move-down", bindings.Match(KeyMode.Visual, KeySequence.Parse("j")).Command);
        Assert.Equal("move-first", bindings.Match(KeyMode.Normal, KeySequence.Parse("gg")).Command);
        Assert.Equal("enter", bindings.Match(KeyMode.Normal, KeySequence.Parse("<Return>")).Command);
    }

    [Fact]
    public void SpecialKeySyntaxSurvivesTheRoundTrip()
    {
        var (bindings, _) = Loaded();

        Assert.True(bindings.Match(KeyMode.Normal, KeySequence.Parse("<Ctrl+d>")).IsExact);
        Assert.True(bindings.Match(KeyMode.Normal, KeySequence.Parse("<Escape>")).IsExact);
        Assert.True(bindings.Match(KeyMode.Normal, KeySequence.Parse("<Space>")).IsExact);
    }

    [Fact]
    public void DrivesTheModeManagerEndToEnd()
    {
        var (bindings, modes) = Loaded();
        var manager = new ModeManager(bindings, modes);

        var result = default(KeyResult);
        foreach (var key in KeySequence.Parse("3j"))
        {
            result = manager.HandleKey(key);
        }

        Assert.Equal("move-down", result.Command);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void UserBindingsLayerOverTheDefaults()
    {
        var (bindings, modes) = Loaded();

        var problems = DefaultBindings.Apply(bindings, modes, new Dictionary<string, Dictionary<string, string>>
        {
            ["normal"] = new() { ["j"] = "move-up" },
        });

        Assert.Empty(problems);
        Assert.Equal("move-up", bindings.Match(KeyMode.Normal, KeySequence.Parse("j")).Command);
    }

    [Fact]
    public void AnEmptyCommandUnbinds()
    {
        var (bindings, modes) = Loaded();

        DefaultBindings.Apply(bindings, modes, new Dictionary<string, Dictionary<string, string>>
        {
            ["normal"] = new() { ["j"] = "" },
        });

        Assert.Equal(SequenceMatch.NoMatch, bindings.Match(KeyMode.Normal, KeySequence.Parse("j")).Match);
    }

    [Fact]
    public void OneBadBindingDoesNotCostTheUserTheRest()
    {
        // A typo should report itself, not silently discard every other binding in the
        // file.
        var (bindings, modes) = Loaded();

        var problems = DefaultBindings.Apply(bindings, modes, new Dictionary<string, Dictionary<string, string>>
        {
            ["nonsense-mode"] = new() { ["j"] = "move-up" },
            ["normal"] = new() { ["<Nope>"] = "move-up", ["q"] = "reload" },
        });

        Assert.Equal(2, problems.Count);
        Assert.Equal("reload", bindings.Match(KeyMode.Normal, KeySequence.Parse("q")).Command);
    }
}

public class NavigationCommandTests
{
    private static (CommandParser Parser, NavigationCommands Commands) Setup()
    {
        var commands = new NavigationCommands();
        var registry = new CommandRegistry();
        registry.Register(commands);
        return (new CommandParser(registry), commands);
    }

    private static FakePane Pane() => new("/tmp", "a", "b", "c", "d", "e", "f", "g", "h");

    [Fact]
    public void CountedMotionsMoveByThatMuch()
    {
        var (parser, _) = Setup();
        var pane = Pane();

        parser.Execute("move-down", new CommandContext { Count = 3, Pane = pane });

        Assert.Equal(3, pane.CursorIndex);
    }

    [Fact]
    public void MotionsClampAtTheEnds()
    {
        var (parser, _) = Setup();
        var pane = Pane();

        parser.Execute("move-up", new CommandContext { Count = 99, Pane = pane });
        Assert.Equal(0, pane.CursorIndex);

        parser.Execute("move-down", new CommandContext { Count = 99, Pane = pane });
        Assert.Equal(7, pane.CursorIndex);
    }

    [Fact]
    public void MoveFirstWithACountGoesToThatEntryOneBased()
    {
        // vim numbers lines from one, and <count>gg follows that.
        var (parser, _) = Setup();
        var pane = Pane();

        parser.Execute("move-first", new CommandContext { Count = 3, Pane = pane });

        Assert.Equal(2, pane.CursorIndex);
    }

    [Fact]
    public void MoveLastWithoutACountGoesToTheEnd()
    {
        var (parser, _) = Setup();
        var pane = Pane();

        parser.Execute("move-last", new CommandContext { Pane = pane });

        Assert.Equal(7, pane.CursorIndex);
    }

    [Fact]
    public void SortWithNoFlagsTogglesDirection()
    {
        var (parser, _) = Setup();
        var pane = Pane();

        parser.Execute("sort", new CommandContext { Pane = pane });
        Assert.True(pane.Sort.Descending);

        parser.Execute("sort", new CommandContext { Pane = pane });
        Assert.False(pane.Sort.Descending);
    }

    [Fact]
    public void SortAcceptsAKey()
    {
        var (parser, _) = Setup();
        var pane = Pane();

        parser.Execute("sort -k size", new CommandContext { Pane = pane });

        Assert.Equal(Core.FileSystem.SortKey.Size, pane.Sort.Key);
    }

    [Fact]
    public void ToggleHiddenFlipsTheFilter()
    {
        var (parser, _) = Setup();
        var pane = Pane();

        parser.Execute("toggle-hidden", new CommandContext { Pane = pane });

        Assert.True(pane.Filter.ShowHidden);
    }

    [Fact]
    public void SelectToggleStaysOnWhatItMarked()
    {
        // Space marks the entry under the cursor and leaves the cursor on it. Advancing
        // moves off the thing just picked, so checking it costs a keystroke back and
        // mis-aiming marks the row below.
        var (parser, _) = Setup();
        var pane = Pane();

        parser.Execute("select-toggle", new CommandContext { Pane = pane });

        Assert.Equal([0], pane.Selection.Indices());
        Assert.Equal(0, pane.CursorIndex);
    }

    [Fact]
    public void ACountMarksARunAndStopsOnTheLastOfThem()
    {
        var (parser, _) = Setup();
        var pane = Pane();

        parser.Execute("select-toggle", new CommandContext { Count = 3, Pane = pane });

        Assert.Equal([0, 1, 2], pane.Selection.Indices());
        Assert.Equal(2, pane.CursorIndex);
    }

    [Fact]
    public void TheAdvancingVariantIsStillThere()
    {
        // For anyone who wants Space to sweep, as a mail client does.
        var (parser, _) = Setup();
        var pane = Pane();

        parser.Execute("select-toggle-advance", new CommandContext { Count = 3, Pane = pane });

        Assert.Equal([0, 1, 2], pane.Selection.Indices());
        Assert.Equal(3, pane.CursorIndex);
    }

    [Fact]
    public void SelectAllAndNone()
    {
        var (parser, _) = Setup();
        var pane = Pane();

        parser.Execute("select-all", new CommandContext { Pane = pane });
        Assert.Equal(8, pane.Selection.Count);

        parser.Execute("select-none", new CommandContext { Pane = pane });
        Assert.Equal(0, pane.Selection.Count);
    }



    [Fact]
    public void EnterOnAFileReportsRatherThanNavigating()
    {
        var (parser, _) = Setup();
        var pane = Pane();
        var messages = new RecordingMessageSink();

        parser.Execute("enter", new CommandContext { Pane = pane, Messages = messages });

        Assert.Empty(pane.Log);
        Assert.Contains("Not a directory", messages.Last, StringComparison.Ordinal);
    }

    [Fact]
    public void CdResolvesRelativePaths()
    {
        // Against real directories, because :cd now refuses one that is not there.
        using var temp = new Fjord.Tests.FileSystem.TempDirectory();
        var nested = Path.Combine(temp.Path, "sub", "dir");
        System.IO.Directory.CreateDirectory(nested);

        var (parser, _) = Setup();
        var pane = new FakePane(temp.Path);

        parser.Execute("cd sub/dir", Ctx(pane));

        Assert.Equal([$"navigate:{Path.GetFullPath(nested)}"], pane.Log);
    }

    [Fact]
    public void CdKeepsSpacesInPathsWithoutQuoting()
    {
        // MaxSplit = 0, so the remainder is free text; a user typing a folder name with
        // spaces should not have to quote it.
        using var temp = new Fjord.Tests.FileSystem.TempDirectory();
        var spaced = Path.Combine(temp.Path, "My Documents");
        System.IO.Directory.CreateDirectory(spaced);

        var (parser, _) = Setup();
        var pane = new FakePane(temp.Path);

        parser.Execute("cd My Documents", Ctx(pane));

        Assert.Equal([$"navigate:{Path.GetFullPath(spaced)}"], pane.Log);
    }

    [Fact]
    public void CdRefusesADirectoryThatIsNotThere()
    {
        // Navigating anyway produced an empty listing, which is indistinguishable from an
        // empty directory and sends the user looking for files that were never there.
        using var temp = new Fjord.Tests.FileSystem.TempDirectory();
        var (parser, _) = Setup();
        var pane = new FakePane(temp.Path);
        var messages = new RecordingMessageSink();

        parser.Execute("cd no-such-folder", Ctx(pane, messages));

        Assert.Empty(pane.Log);
        Assert.Equal(MessageLevel.Error, messages.Messages[^1].Level);
        Assert.Contains("no-such-folder", messages.Last, StringComparison.Ordinal);
    }

    [Fact]
    public void CdRefusesAFile()
    {
        using var temp = new Fjord.Tests.FileSystem.TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "a.txt"), string.Empty);

        var (parser, _) = Setup();
        var pane = new FakePane(temp.Path);
        var messages = new RecordingMessageSink();

        parser.Execute("cd a.txt", Ctx(pane, messages));

        Assert.Empty(pane.Log);
        Assert.Contains("is a file", messages.Last, StringComparison.Ordinal);
    }

    [Fact]
    public void CdPrefersAQuickmarkOverASubdirectoryOfTheSameName()
    {
        // The point of naming a place: ":cd work" should reach the one you named even when
        // a folder called "work" happens to be beside you. "./work" still gets the folder.
        using var temp = new Fjord.Tests.FileSystem.TempDirectory();
        var beside = Path.Combine(temp.Path, "work");
        var marked = Path.Combine(temp.Path, "elsewhere");
        System.IO.Directory.CreateDirectory(beside);
        System.IO.Directory.CreateDirectory(marked);

        var marks = new Fjord.Core.Navigation.MarkFile();
        marks.Set("work", marked);

        var pane = new FakePane(temp.Path);
        MarkAwareParser(marks).Execute("cd work", Ctx(pane));

        Assert.Equal([$"navigate:{marked}"], pane.Log);
    }

    [Fact]
    public void AQuickmarkPointingAtSomethingGoneIsRefused()
    {
        // Directories get deleted. Saying so beats an empty listing that looks like an
        // empty folder.
        var marks = new Fjord.Core.Navigation.MarkFile();
        marks.Set("stale", Path.Combine(Path.GetTempPath(), "fjord-was-never-here"));

        var pane = new FakePane("/tmp");
        var messages = new RecordingMessageSink();

        MarkAwareParser(marks).Execute("cd stale", Ctx(pane, messages));

        Assert.Empty(pane.Log);
        Assert.Contains("is gone", messages.Last, StringComparison.Ordinal);
    }

    private static CommandContext Ctx(FakePane pane, IMessageSink? messages = null) =>
        new() { Pane = pane, Messages = messages ?? NullMessageSink.Instance };

    private static CommandParser MarkAwareParser(Fjord.Core.Navigation.MarkFile marks)
    {
        var registry = new CommandRegistry();
        registry.Register(new NavigationCommands(null, marks));
        return new CommandParser(registry);
    }
}

public class OperatorDispatcherTests
{
    private static OperatorDispatcher Setup()
    {
        var registry = new CommandRegistry();
        registry.Register(new NavigationCommands());
        var parser = new CommandParser(registry);
        return new OperatorDispatcher(parser, registry);
    }

    private static FakePane Pane() => new("/tmp", "a", "b", "c", "d", "e", "f", "g", "h");

    [Fact]
    public void OperatorActsOnTheRangeTheMotionCovered()
    {
        // d3j, spelled with the operator M1 actually ships.
        var dispatcher = Setup();
        var pane = Pane();

        dispatcher.Execute("select-range", "move-down", 3, '"', pane, NullMessageSink.Instance);

        Assert.Equal([0, 1, 2, 3], pane.Selection.Indices());
    }

    [Fact]
    public void OperatorLeavesTheCursorWhereItStarted()
    {
        var dispatcher = Setup();
        var pane = Pane();
        pane.CursorIndex = 2;

        dispatcher.Execute("select-range", "move-down", 3, '"', pane, NullMessageSink.Instance);

        Assert.Equal(2, pane.CursorIndex);
    }

    [Fact]
    public void BackwardMotionsProduceTheSameRange()
    {
        var dispatcher = Setup();
        var pane = Pane();
        pane.CursorIndex = 5;

        dispatcher.Execute("select-range", "move-up", 2, '"', pane, NullMessageSink.Instance);

        Assert.Equal([3, 4, 5], pane.Selection.Indices());
    }


    [Fact]
    public void MotionsClampSoAnOverlongCountStillWorks()
    {
        var dispatcher = Setup();
        var pane = Pane();

        dispatcher.Execute("select-range", "move-down", 999, '"', pane, NullMessageSink.Instance);

        Assert.Equal(8, pane.Selection.Count);
    }

    [Fact]
    public void ANonMotionIsRefused()
    {
        // Without this the dispatcher would run an arbitrary command and then measure a
        // cursor that never moved, silently applying the operator to a single row.
        var dispatcher = Setup();

        var ex = Assert.Throws<CommandException>(() =>
            dispatcher.Execute("select-range", "toggle-hidden", 1, '"', Pane(), NullMessageSink.Instance));

        Assert.Contains("not a motion", ex.Message, StringComparison.Ordinal);
    }

}
