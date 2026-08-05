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
                var name = command.Split(' ')[0];
                if (registry.Find(name) is null)
                {
                    unresolved.Add($"{mode}/{keys} -> {command}");
                }
            }
        }

        Assert.Empty(unresolved);
        Assert.NotNull(parser);
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
    public void SelectToggleSelectsAndAdvances()
    {
        // Holding Space should select a run, as it does in a mail client.
        var (parser, _) = Setup();
        var pane = Pane();

        parser.Execute("select-toggle", new CommandContext { Count = 3, Pane = pane });

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
        var (parser, _) = Setup();
        var pane = new FakePane("/tmp");

        parser.Execute("cd sub/dir", new CommandContext { Pane = pane });

        Assert.Equal([$"navigate:{Path.GetFullPath(Path.Combine("/tmp", "sub/dir"))}"], pane.Log);
    }

    [Fact]
    public void CdKeepsSpacesInPathsWithoutQuoting()
    {
        // MaxSplit = 0, so the remainder is free text; a user typing a folder name with
        // spaces should not have to quote it.
        var (parser, _) = Setup();
        var pane = new FakePane("/tmp");

        parser.Execute("cd My Documents", new CommandContext { Pane = pane });

        Assert.Equal([$"navigate:{Path.GetFullPath(Path.Combine("/tmp", "My Documents"))}"], pane.Log);
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
