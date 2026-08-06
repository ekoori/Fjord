using Fjord.Commands;
using Fjord.Core.Config;
using Fjord.Core.Diagnostics;
using Fjord.Core.Input;
using Fjord.Tests.Platform;

namespace Fjord.Tests.Commands;

/// <summary>Records what was shown, so the help commands are testable without a window.</summary>
internal sealed class FakeHelpView : IHelpView
{
    public bool IsVisible { get; private set; }

    public string? Title { get; private set; }

    public string? Content { get; private set; }

    public void Show(string title, string content)
    {
        Title = title;
        Content = content;
        IsVisible = true;
    }

    public void Hide() => IsVisible = false;
}

internal sealed class FakeConfigService : IConfigService
{
    public string ConfigFilePath { get; set; } = "/home/u/.config/fjord/config.yaml";

    public bool Existed { get; set; } = true;

    public string? ReloadError { get; set; }

    public int EnsureCalls { get; private set; }

    public int ReloadCalls { get; private set; }

    public bool EnsureExists()
    {
        EnsureCalls++;
        return !Existed;
    }

    public string? Reload()
    {
        ReloadCalls++;
        return ReloadError;
    }

    /// <summary>Two settings, enough to tell "unknown" from "wrong value" apart.</summary>
    public IReadOnlyList<SettingInfo> Settings { get; set; } =
    [
        new("appearance.font_size", "number", "Font size in DIPs.", "13"),
        new("listing.show_hidden", "true | false", "Show dotfiles.", null),
    ];

    /// <summary>What Set will refuse with, or null to accept.</summary>
    public string? SetError { get; set; }

    public List<(string Name, string Value)> Written { get; } = [];

    public string? Set(string name, string value)
    {
        if (SetError is not null)
        {
            return SetError;
        }

        Written.Add((name, value));
        return null;
    }
}

public class HelpTextTests
{
    private static (CommandRegistry Registry, KeyBindings Bindings, KeyModeRegistry Modes) Setup()
    {
        var registry = new CommandRegistry();
        registry.Register(new NavigationCommands());

        var bindings = new KeyBindings();
        var modes = new KeyModeRegistry();
        DefaultBindings.Load(bindings, modes);

        return (registry, bindings, modes);
    }

    [Fact]
    public void TheOverviewListsCommandsGrouped()
    {
        var (registry, bindings, modes) = Setup();

        var text = HelpText.Overview(registry, bindings, modes);

        Assert.Contains("── Motion ──", text, StringComparison.Ordinal);
        Assert.Contains("── Navigation ──", text, StringComparison.Ordinal);
        Assert.Contains(":move-down", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOverviewShowsTheKeyThatRunsEachCommand()
    {
        // The whole point: the question a user asks is "what key does this", and the
        // bindings are stored the other way round.
        var (registry, bindings, modes) = Setup();

        var text = HelpText.Overview(registry, bindings, modes);
        var line = text.Split('\n').First(l => l.Contains(":move-down", StringComparison.Ordinal));

        Assert.Contains("j", line, StringComparison.Ordinal);
    }

    [Fact]
    public void KeysFromOtherModesAreLabelled()
    {
        // "j" in visual mode and "j" in normal mode are different bindings; showing them
        // undifferentiated would mislead.
        var (registry, bindings, modes) = Setup();

        var text = HelpText.ForCommand(registry.Find("move-down")!, bindings, modes);

        Assert.Contains("(v)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryDescriptionReachesTheOverview()
    {
        // The registry is the catalogue; help that omitted a command would let one ship
        // undiscoverable.
        var (registry, bindings, modes) = Setup();

        var text = HelpText.Overview(registry, bindings, modes);

        foreach (var command in registry.Commands)
        {
            Assert.Contains(":" + command.Name, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void HiddenCommandsAreNotListed()
    {
        var registry = new CommandRegistry();
        registry.Register(new HelpCommands(
            registry, new KeyBindings(), new KeyModeRegistry(),
            new FakeHelpView(), new FakeConfigService(), new FakePlatform()));

        var text = HelpText.Overview(registry, new KeyBindings(), new KeyModeRegistry());

        Assert.DoesNotContain(":help-close", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandDetailShowsUsageAliasesAndKeys()
    {
        var (registry, bindings, modes) = Setup();

        var text = HelpText.ForCommand(registry.Find("enter")!, bindings, modes);

        Assert.Contains(":enter", text, StringComparison.Ordinal);
        Assert.Contains("Usage", text, StringComparison.Ordinal);
        Assert.Contains(":open", text, StringComparison.Ordinal);
        Assert.Contains("Keys", text, StringComparison.Ordinal);
    }

    [Fact]
    public void OperatorsAndMotionsSayWhatThatMeans()
    {
        // "Operator" is meaningless to anyone who has not used vim, and it changes how the
        // key behaves entirely.
        var (registry, bindings, modes) = Setup();

        Assert.Contains(
            "Operator.",
            HelpText.ForCommand(registry.Find("select-range")!, bindings, modes),
            StringComparison.Ordinal);

        Assert.Contains(
            "Motion (linewise)",
            HelpText.ForCommand(registry.Find("move-down")!, bindings, modes),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ChoicesAreListedForArgumentsThatHaveThem()
    {
        var (registry, bindings, modes) = Setup();

        var text = HelpText.ForCommand(registry.Find("sort")!, bindings, modes);

        Assert.Contains("name | size | modified | extension", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ACommandWithNoBindingSaysSo()
    {
        var registry = new CommandRegistry();
        registry.Register(new NavigationCommands());

        var text = HelpText.ForCommand(registry.Find("cd")!, new KeyBindings(), new KeyModeRegistry());

        Assert.Contains("(none — run it from the command line)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownNameSuggestsNearMatches()
    {
        // The usual reason for asking about a command that does not exist is a
        // half-remembered name.
        var (registry, _, _) = Setup();

        var text = HelpText.UnknownCommand("move", registry);

        Assert.Contains("Did you mean", text, StringComparison.Ordinal);
        Assert.Contains(":move-down", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownNameWithNoMatchesPointsAtTheFullList()
    {
        var (registry, _, _) = Setup();

        Assert.Contains("Try :help", HelpText.UnknownCommand("zzzz", registry), StringComparison.Ordinal);
    }
}

public class HelpCommandTests
{
    private static (CommandParser Parser, FakeHelpView View, FakeConfigService Config, FakePlatform Platform)
        Setup()
    {
        var registry = new CommandRegistry();
        registry.Register(new NavigationCommands());

        var view = new FakeHelpView();
        var config = new FakeConfigService();
        var platform = new FakePlatform();
        var bindings = new KeyBindings();
        var modes = new KeyModeRegistry();
        DefaultBindings.Load(bindings, modes);

        registry.Register(new HelpCommands(registry, bindings, modes, view, config, platform));
        return (new CommandParser(registry), view, config, platform);
    }

    private static CommandContext Context(IMessageSink? messages = null) =>
        new() { Messages = messages ?? NullMessageSink.Instance };

    [Fact]
    public void BareHelpShowsTheOverview()
    {
        var (parser, view, _, _) = Setup();

        parser.Execute("help", Context());

        Assert.True(view.IsVisible);
        Assert.Equal("Help", view.Title);
        Assert.Contains("── Motion ──", view.Content!, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpForACommandShowsItsDetail()
    {
        var (parser, view, _, _) = Setup();

        parser.Execute("help move-down", Context());

        Assert.Equal("Help — :move-down", view.Title);
        Assert.Contains("Motion (linewise)", view.Content!, StringComparison.Ordinal);
    }

    [Fact]
    public void ALeadingColonOnTheArgumentIsAccepted()
    {
        // ":help :cd" is what a user types after reading ":cd" somewhere.
        var (parser, view, _, _) = Setup();

        parser.Execute("help :cd", Context());

        Assert.Equal("Help — :cd", view.Title);
    }

    [Fact]
    public void HelpForAnUnknownCommandExplainsRatherThanFailing()
    {
        var (parser, view, _, _) = Setup();

        parser.Execute("help nonsense", Context());

        Assert.True(view.IsVisible);
        Assert.Contains("No command 'nonsense'", view.Content!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigOpensTheFileInTheDefaultApplication()
    {
        var (parser, _, config, platform) = Setup();
        var messages = new RecordingMessageSink();

        await AwaitAll(parser, "config", messages);

        Assert.Equal([config.ConfigFilePath], platform.Opened);
        Assert.Contains("saving applies changes immediately", messages.Last!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigCreatesTheFileOnAFreshInstall()
    {
        // Opening a path that does not exist just makes the editor complain.
        var (parser, _, config, _) = Setup();
        config.Existed = false;
        var messages = new RecordingMessageSink();

        await AwaitAll(parser, "config", messages);

        Assert.Equal(1, config.EnsureCalls);
        Assert.Contains("Created and opened", messages.Last!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailureToOpenIsReportedRatherThanThrown()
    {
        var (parser, _, _, platform) = Setup();
        platform.FailWith = new System.ComponentModel.Win32Exception("no handler");
        var messages = new RecordingMessageSink();

        await AwaitAll(parser, "config", messages);

        Assert.Equal(MessageLevel.Error, messages.Messages[^1].Level);
        Assert.Contains("Could not open", messages.Last!, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigPathReportsWhereTheFileLives()
    {
        var (parser, _, config, _) = Setup();
        var messages = new RecordingMessageSink();

        parser.Execute("config-path", Context(messages));

        Assert.Equal(config.ConfigFilePath, messages.Last);
    }

    [Fact]
    public void ConfigReloadReportsSuccessAndFailure()
    {
        var (parser, _, config, _) = Setup();
        var messages = new RecordingMessageSink();

        parser.Execute("config-reload", Context(messages));
        Assert.Equal("Configuration reloaded.", messages.Last);

        config.ReloadError = "config.yaml(3,1): bad indent";
        parser.Execute("config-reload", Context(messages));
        Assert.Equal(MessageLevel.Error, messages.Messages[^1].Level);
        Assert.Contains("bad indent", messages.Last!, StringComparison.Ordinal);
    }

    private static async Task AwaitAll(CommandParser parser, string line, IMessageSink messages)
    {
        foreach (var command in parser.Parse(line, new CommandContext { Messages = messages }))
        {
            if (command.Invoke() is Task task)
            {
                await task;
            }
        }
    }
}
