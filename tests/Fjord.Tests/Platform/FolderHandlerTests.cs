using Fjord.Commands;
using Fjord.Core.Diagnostics;
using Fjord.Core.Platform;
using Fjord.Platform;
using Fjord.Tests.Commands;
using Fjord.Tests.FileSystem;

namespace Fjord.Tests.Platform;

/// <summary>A folder handler that records what it was asked, and can refuse.</summary>
internal sealed class FakeFolderHandler : IFolderHandler
{
    public bool IsSupported { get; set; } = true;

    public bool Registered { get; set; }

    public string? RegisteredWith { get; private set; }

    public int Unregistrations { get; private set; }

    /// <summary>Set to make the first registration come back needing elevation.</summary>
    public bool Refuse { get; set; }

    public bool IsDefault() => Registered;

    public HandlerResult Register(string executable)
    {
        if (!IsSupported)
        {
            return new HandlerResult(HandlerOutcome.Unsupported, "Not on this system.");
        }

        if (Refuse)
        {
            return new HandlerResult(HandlerOutcome.NeedsElevation, "The registry refused the change.");
        }

        RegisteredWith = executable;
        Registered = true;
        return new HandlerResult(HandlerOutcome.Done, "Fjord now opens folders.");
    }

    public HandlerResult Unregister()
    {
        Unregistrations++;
        Registered = false;
        return new HandlerResult(HandlerOutcome.Done, "Folders open with the system default again.");
    }
}

public class DefaultHandlerCommandTests
{
    private static (CommandParser Parser, FakeFolderHandler Handler, FakeModeHost Host, RecordingMessageSink Messages)
        Setup(Func<bool>? elevate = null)
    {
        var handler = new FakeFolderHandler();
        var host = new FakeModeHost();
        var registry = new CommandRegistry();

        registry.Register(new DefaultHandlerCommands(handler, host, () => "/opt/fjord/Fjord", elevate));

        return (new CommandParser(registry), handler, host, new RecordingMessageSink());
    }

    private static void Run(CommandParser parser, string line, RecordingMessageSink messages)
    {
        foreach (var command in parser.Parse(line, new CommandContext { Messages = messages }))
        {
            command.Invoke();
        }
    }

    [Fact]
    public void TheCommandTurnsItOnAndOffAgain()
    {
        // One command rather than a register/unregister pair: the state is a single
        // yes-or-no, and a pair would mean knowing the answer before pressing anything.
        var (parser, handler, _, messages) = Setup();

        Run(parser, "default-file-manager", messages);
        Assert.True(handler.Registered);
        Assert.Equal("/opt/fjord/Fjord", handler.RegisteredWith);

        Run(parser, "default-file-manager", messages);
        Assert.False(handler.Registered);
        Assert.Equal(1, handler.Unregistrations);
    }

    [Fact]
    public void ARefusalAsksBeforeElevating()
    {
        // Restarting someone's file manager with administrator rights is not something to
        // do to them silently on the way past.
        var elevated = false;
        var (parser, handler, host, messages) = Setup(() => elevated = true);
        handler.Refuse = true;
        host.AnswerYes = false;

        Run(parser, "default-file-manager", messages);

        Assert.False(elevated);
        Assert.Contains(
            host.Actions,
            a => a.StartsWith("ask:", StringComparison.Ordinal)
                && a.Contains("administrator", StringComparison.OrdinalIgnoreCase));

        host.AnswerYes = true;
        Run(parser, "default-file-manager", messages);
        Assert.True(elevated);
    }

    [Fact]
    public void ARefusalWithNoWayToElevateSaysSo()
    {
        var (parser, handler, host, messages) = Setup();
        handler.Refuse = true;

        Run(parser, "default-file-manager", messages);

        Assert.DoesNotContain(host.Actions, a => a.StartsWith("ask:", StringComparison.Ordinal));
        Assert.Contains("higher privileges", messages.Last!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnsupportedPlatformIsToldRatherThanTried()
    {
        var (parser, handler, _, messages) = Setup();
        handler.IsSupported = false;

        Run(parser, "default-file-manager", messages);

        Assert.False(handler.Registered);
        Assert.Contains("Not on this system", messages.Last!, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusSaysWhichWayRoundItIs()
    {
        var (parser, handler, _, messages) = Setup();

        Run(parser, "default-file-manager?", messages);
        Assert.Contains("system default", messages.Last!, StringComparison.Ordinal);

        handler.Registered = true;
        Run(parser, "default-file-manager?", messages);
        Assert.Contains("Fjord opens folders", messages.Last!, StringComparison.Ordinal);
    }
}

/// <summary>
/// The freedesktop registration, against a temporary XDG layout.
/// </summary>
/// <remarks>
/// Runs on every platform: nothing in it is Linux-specific beyond the file format it
/// writes, and the point is that the format is right. The Windows registration has no
/// equivalent test — the only thing it can write to is the live association for the
/// account running the suite, and a test that changes which program opens your folders is
/// not one to leave lying around.
/// </remarks>
public class FreedesktopHandlerTests
{
    private static (FreedesktopFolderHandler Handler, string Config, string Data) Setup(TempDirectory temp)
    {
        var config = Path.Combine(temp.Path, "config");
        var data = Path.Combine(temp.Path, "data");
        return (new FreedesktopFolderHandler(config, data), config, data);
    }

    [Fact]
    public void RegisteringWritesADesktopFileAndClaimsTheType()
    {
        using var temp = new TempDirectory();
        var (handler, config, data) = Setup(temp);

        Assert.True(handler.Register("/opt/fjord/Fjord").Succeeded);

        var desktop = File.ReadAllText(Path.Combine(data, "applications", "fjord.desktop"));
        Assert.Contains("Exec=/opt/fjord/Fjord %f", desktop, StringComparison.Ordinal);
        Assert.Contains("MimeType=inode/directory;", desktop, StringComparison.Ordinal);

        var mime = File.ReadAllText(Path.Combine(config, "mimeapps.list"));
        Assert.Contains("[Default Applications]", mime, StringComparison.Ordinal);
        Assert.Contains("inode/directory=fjord.desktop", mime, StringComparison.Ordinal);

        Assert.True(handler.IsDefault());
    }

    [Fact]
    public void OtherAssociationsInTheFileAreLeftAlone()
    {
        // mimeapps.list belongs to the desktop environment and holds every association the
        // user has ever made. Rewriting it wholesale would take their PDF viewer with it.
        using var temp = new TempDirectory();
        var (handler, config, _) = Setup(temp);

        Directory.CreateDirectory(config);
        File.WriteAllLines(
            Path.Combine(config, "mimeapps.list"),
            ["[Default Applications]", "application/pdf=okular.desktop", "image/png=eog.desktop"]);

        handler.Register("/opt/fjord/Fjord");

        var mime = File.ReadAllText(Path.Combine(config, "mimeapps.list"));
        Assert.Contains("application/pdf=okular.desktop", mime, StringComparison.Ordinal);
        Assert.Contains("image/png=eog.desktop", mime, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisteringTwiceLeavesOneEntry()
    {
        using var temp = new TempDirectory();
        var (handler, config, _) = Setup(temp);

        handler.Register("/opt/fjord/Fjord");
        handler.Register("/opt/fjord/Fjord");

        var claims = File.ReadAllLines(Path.Combine(config, "mimeapps.list"))
            .Count(l => l.StartsWith("inode/directory=", StringComparison.Ordinal));

        Assert.Equal(1, claims);
    }

    [Fact]
    public void UnregisteringPutsTheSystemBack()
    {
        using var temp = new TempDirectory();
        var (handler, config, data) = Setup(temp);

        handler.Register("/opt/fjord/Fjord");
        Assert.True(handler.Unregister().Succeeded);

        Assert.False(File.Exists(Path.Combine(data, "applications", "fjord.desktop")));
        Assert.DoesNotContain(
            "inode/directory=fjord.desktop",
            File.ReadAllText(Path.Combine(config, "mimeapps.list")),
            StringComparison.Ordinal);
        Assert.False(handler.IsDefault());
    }

    [Fact]
    public void AnExistingClaimByAnotherManagerIsReplaced()
    {
        using var temp = new TempDirectory();
        var (handler, config, _) = Setup(temp);

        Directory.CreateDirectory(config);
        File.WriteAllLines(
            Path.Combine(config, "mimeapps.list"),
            ["[Default Applications]", "inode/directory=org.gnome.Nautilus.desktop"]);

        handler.Register("/opt/fjord/Fjord");

        var lines = File.ReadAllLines(Path.Combine(config, "mimeapps.list"));
        Assert.Single(lines, l => l.StartsWith("inode/directory=", StringComparison.Ordinal));
        Assert.Contains("inode/directory=fjord.desktop", lines);
    }
}
