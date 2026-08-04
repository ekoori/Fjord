using Fjord.Commands;
using Fjord.Core.Diagnostics;
using Fjord.Core.Platform;
using Fjord.Tests.Commands;

namespace Fjord.Tests.Platform;

/// <summary>Records what was asked of the OS, without asking it.</summary>
internal sealed class FakePlatform : IPlatformServices
{
    public List<string> Opened { get; } = [];

    public List<string> Revealed { get; } = [];

    public Exception? FailWith { get; set; }

    public IReadOnlyList<PlatformRoot> Roots { get; set; } =
        [new("/", "Filesystem", RootKind.Drive)];

    public string HomeDirectory { get; set; } = "/home/u";

    public StringComparison PathComparison => StringComparison.Ordinal;

    public IReadOnlyList<PlatformRoot> GetRoots() => Roots;

    public Task OpenAsync(string path, CancellationToken ct = default)
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }

        Opened.Add(path);
        return Task.CompletedTask;
    }

    public Task RevealAsync(string path, CancellationToken ct = default)
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }

        Revealed.Add(path);
        return Task.CompletedTask;
    }

    public string DisplayPath(string path) => path;
}

public class PlatformCommandTests
{
    private static (CommandParser Parser, FakePlatform Platform) Setup()
    {
        var platform = new FakePlatform();
        var registry = new CommandRegistry();
        registry.Register(new PlatformCommands(platform));
        registry.Register(new NavigationCommands(platform));
        return (new CommandParser(registry), platform);
    }

    [Fact]
    public async Task EnterOnAFileHandsItToTheOperatingSystem()
    {
        var (parser, platform) = Setup();
        var pane = new FakePane("/tmp", "notes.txt");

        await AwaitAll(parser, "enter", pane);

        Assert.Equal([Path.Combine("/tmp", "notes.txt")], platform.Opened);
        Assert.Empty(pane.Log);
    }

    [Fact]
    public async Task EnterOnADirectoryStillNavigates()
    {
        var (parser, platform) = Setup();
        var pane = new FakePane("/tmp");
        pane.AddDirectory("sub");

        await AwaitAll(parser, "enter", pane);

        Assert.Empty(platform.Opened);
        Assert.Equal([$"navigate:{Path.Combine("/tmp", "sub")}"], pane.Log);
    }

    [Fact]
    public async Task AFailedLaunchIsReportedRatherThanThrown()
    {
        // No handler registered for the type is an everyday situation, not a crash.
        var (parser, platform) = Setup();
        platform.FailWith = new System.ComponentModel.Win32Exception("No application is associated");
        var messages = new RecordingMessageSink();

        await AwaitAll(parser, "enter", new FakePane("/tmp", "notes.txt"), messages);

        Assert.Contains("Could not open notes.txt", messages.Last, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevealPassesTheEntryUnderTheCursor()
    {
        var (parser, platform) = Setup();

        await AwaitAll(parser, "reveal", new FakePane("/tmp", "a.txt", "b.txt"));

        Assert.Equal([Path.Combine("/tmp", "a.txt")], platform.Revealed);
    }

    [Fact]
    public async Task RevealFallsBackToTheDirectoryWhenTheListIsEmpty()
    {
        var (parser, platform) = Setup();

        await AwaitAll(parser, "reveal", new FakePane("/tmp"));

        Assert.Equal(["/tmp"], platform.Revealed);
    }

    [Fact]
    public async Task GoHomeNavigatesToTheHomeDirectory()
    {
        var (parser, platform) = Setup();
        var pane = new FakePane("/tmp");

        await AwaitAll(parser, "go-home", pane);

        Assert.Equal([$"navigate:{platform.HomeDirectory}"], pane.Log);
    }

    [Fact]
    public async Task RootsAreReported()
    {
        var (parser, platform) = Setup();
        platform.Roots = [new PlatformRoot("/", "Filesystem", RootKind.Drive),
                          new PlatformRoot("/home/u", "Home", RootKind.Home)];
        var messages = new RecordingMessageSink();

        await AwaitAll(parser, "roots", new FakePane("/tmp"), messages);

        Assert.Contains("Filesystem", messages.Last, StringComparison.Ordinal);
        Assert.Contains("Home", messages.Last, StringComparison.Ordinal);
    }

    private static async Task AwaitAll(
        CommandParser parser, string line, FakePane pane, IMessageSink? messages = null)
    {
        var context = new CommandContext
        {
            Pane = pane,
            Messages = messages ?? NullMessageSink.Instance,
        };

        foreach (var command in parser.Parse(line, context))
        {
            if (command.Invoke() is Task task)
            {
                await task;
            }
        }
    }
}

public class RealPlatformServicesTests
{
    [Fact]
    public void CreateReturnsSomethingForThisOperatingSystem() =>
        Assert.NotNull(Fjord.Platform.PlatformServices.Create());

    [Fact]
    public void RootsAlwaysIncludeHome()
    {
        var roots = Fjord.Platform.PlatformServices.Create().GetRoots();

        Assert.Contains(roots, r => r.Kind == RootKind.Home);
    }

    [Fact]
    public void RootsAreRealDirectories()
    {
        // A root that cannot be navigated to is worse than no root at all. Drives with no
        // media are the exception: they are listed so the user can see they exist.
        var roots = Fjord.Platform.PlatformServices.Create().GetRoots();

        Assert.All(
            roots.Where(r => r.Kind is RootKind.Home or RootKind.Mount),
            r => Assert.True(Directory.Exists(r.Path), $"{r.Label} -> {r.Path}"));
    }

    [Fact]
    public void PathComparisonMatchesThePlatformConvention()
    {
        var comparison = Fjord.Platform.PlatformServices.Create().PathComparison;

        Assert.Equal(
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase,
            comparison);
    }

    [Theory]
    [InlineData("/home/u", "/home/u", "~")]
    [InlineData("/home/u/docs", "/home/u", "~/docs")]
    // The guard that stops "/home/igor2" collapsing to "~2" for a user in /home/igor.
    [InlineData("/home/u2/docs", "/home/u", "/home/u2/docs")]
    [InlineData("/etc/hosts", "/home/u", "/etc/hosts")]
    public void UnixDisplayPathsAbbreviateOnlyOnABoundary(string path, string home, string expected)
    {
        var method = typeof(Fjord.Platform.PlatformServices).Assembly
            .GetType("Fjord.Platform.UnixMounts")!
            .GetMethod("Abbreviate")!;

        Assert.Equal(expected, method.Invoke(null, [path, home]));
    }
}
