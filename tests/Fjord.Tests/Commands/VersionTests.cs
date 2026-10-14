using Fjord.Commands;
using Fjord.Core.Diagnostics;
using Fjord.Core.Input;
using Fjord.Tests.Platform;

namespace Fjord.Tests.Commands;

/// <summary>
/// <c>:version</c> and the build line the help page carries.
/// </summary>
/// <remarks>
/// The number itself cannot be asserted — under the test host the entry assembly is the
/// runner, not Fjord — so these check the shape and the plumbing: that something is
/// reported, that it is one line, and that the help page carries it.
/// </remarks>
public class VersionTests
{
    [Fact]
    public void TheVersionIsNeverEmpty()
    {
        // A blank version in a bug report is worse than a wrong one: it reads as though
        // the question was never answered.
        Assert.False(string.IsNullOrWhiteSpace(BuildInfo.Version));
        Assert.False(string.IsNullOrWhiteSpace(BuildInfo.FullVersion));
    }

    [Fact]
    public void TheCommitSuffixIsTrimmedFromTheDisplayedVersion()
    {
        // The SDK appends "+<sha>" when it knows the repository. It belongs in the log and
        // not in a status bar.
        Assert.DoesNotContain("+", BuildInfo.Version, StringComparison.Ordinal);
        Assert.StartsWith(BuildInfo.Version, BuildInfo.FullVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSummaryIsOneLineNamingFjordAndTheRuntime()
    {
        // Read out into a bug report rather than studied, so it has to fit on the status
        // bar it is printed to.
        Assert.StartsWith("Fjord ", BuildInfo.Summary, StringComparison.Ordinal);
        Assert.Contains(".NET", BuildInfo.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", BuildInfo.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCommandReportsIt()
    {
        var registry = new CommandRegistry();
        registry.Register(new HelpCommands(
            registry, new KeyBindings(), new KeyModeRegistry(),
            new FakeHelpView(), new FakeConfigService(), new FakePlatform()));
        var messages = new RecordingMessageSink();

        new CommandParser(registry).Execute("version", new CommandContext { Messages = messages });

        Assert.Equal(BuildInfo.Summary, messages.Last);
    }

    [Fact]
    public void TheHelpPageSaysWhichBuildItDescribes()
    {
        var registry = new CommandRegistry();
        var bindings = new KeyBindings();
        var modes = new KeyModeRegistry();
        registry.Register(new HelpCommands(
            registry, bindings, modes,
            new FakeHelpView(), new FakeConfigService(), new FakePlatform()));

        var overview = HelpText.Overview(registry, bindings, modes);

        Assert.Contains($"Fjord {BuildInfo.Version}", overview, StringComparison.Ordinal);
        Assert.Contains(BuildInfo.Runtime, overview, StringComparison.Ordinal);
    }
}
