using Fjord.Commands;
using Fjord.Config;
using Fjord.Core.Diagnostics;
using Fjord.Tests.Commands;
using Fjord.Tests.FileSystem;
using Fjord.Tests.Platform;

namespace Fjord.Tests.Config;

public class SetCommandTests
{
    private static (CommandParser Parser, FakeConfigService Config, RecordingMessageSink Messages) Setup()
    {
        var registry = new CommandRegistry();
        var config = new FakeConfigService();
        registry.Register(new HelpCommands(
            registry,
            new Fjord.Core.Input.KeyBindings(),
            new Fjord.Core.Input.KeyModeRegistry(),
            new FakeHelpView(),
            config,
            new FakePlatform()));

        return (new CommandParser(registry), config, new RecordingMessageSink());
    }

    [Fact]
    public void SettingAValueWritesIt()
    {
        var (parser, config, messages) = Setup();

        parser.Execute("set appearance.font_size 15", new CommandContext { Messages = messages });

        Assert.Equal([("appearance.font_size", "15")], config.Written);
        Assert.Equal("appearance.font_size = 15", messages.Last);
    }

    [Fact]
    public void AskingWithoutAValueReportsTheCurrentOne()
    {
        // vim's ":set opt?". Without this, checking a value means opening the file.
        var (parser, config, messages) = Setup();

        parser.Execute("set appearance.font_size", new CommandContext { Messages = messages });

        Assert.Empty(config.Written);
        Assert.Contains("= 13", messages.Last, StringComparison.Ordinal);
        Assert.Contains("[number]", messages.Last, StringComparison.Ordinal);
    }

    [Fact]
    public void ASettingTheFileDoesNotMentionReadsAsUnset()
    {
        var (parser, _, messages) = Setup();

        parser.Execute("set listing.show_hidden", new CommandContext { Messages = messages });

        Assert.Contains("(unset)", messages.Last, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownSettingIsRefusedBeforeAnythingIsWritten()
    {
        var (parser, config, messages) = Setup();

        parser.Execute("set appearance.font_sixe 15", new CommandContext { Messages = messages });

        Assert.Empty(config.Written);
        Assert.Equal(MessageLevel.Error, messages.Messages[^1].Level);
    }

    [Fact]
    public void ARefusalFromTheConfigServiceReachesTheUser()
    {
        var (parser, config, messages) = Setup();
        config.SetError = "appearance.font_size takes a number";

        parser.Execute("set appearance.font_size huge", new CommandContext { Messages = messages });

        Assert.Equal("appearance.font_size takes a number", messages.Last);
        Assert.Equal(MessageLevel.Error, messages.Messages[^1].Level);
    }

    [Fact]
    public void BareSetExplainsItself()
    {
        var (parser, _, messages) = Setup();

        parser.Execute("set", new CommandContext { Messages = messages });

        Assert.Contains("Usage", messages.Last, StringComparison.Ordinal);
    }
}

public class SettingValidationTests
{
    private static SettingDefinition Find(string name) => SettingsSchema.Find(name)!;

    [Fact]
    public void ANumberIsAccepted()
    {
        Assert.Null(SettingsSchema.Validate(Find("appearance.font_size"), "15", out var rendered));
        Assert.Equal("15", rendered);
    }

    [Fact]
    public void SomethingThatIsNotANumberIsRefused()
    {
        Assert.NotNull(SettingsSchema.Validate(Find("appearance.font_size"), "huge", out _));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("TRUE")]
    public void BooleansAreAcceptedInAnyCase(string value)
    {
        Assert.Null(SettingsSchema.Validate(Find("listing.show_hidden"), value, out var rendered));
        Assert.Equal(value.ToLowerInvariant(), rendered);
    }

    [Fact]
    public void AValueOutsideTheChoicesIsRefusedWithTheChoices()
    {
        var problem = SettingsSchema.Validate(Find("listing.sort_key"), "colour", out _);

        Assert.NotNull(problem);
        Assert.Contains("name | size | modified | extension", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("#1B1D21")]
    [InlineData("#ffffff")]
    public void AColourIsAcceptedAndQuoted(string value)
    {
        Assert.Null(SettingsSchema.Validate(Find("appearance.colors.background"), value, out var rendered));
        Assert.Equal($"\"{value}\"", rendered);
    }

    [Theory]
    [InlineData("1B1D21")]
    [InlineData("#1B1D2")]
    [InlineData("#GGGGGG")]
    public void SomethingThatIsNotAColourIsRefused(string value)
    {
        Assert.NotNull(SettingsSchema.Validate(Find("appearance.colors.background"), value, out _));
    }

    [Fact]
    public void AStringIsAlwaysQuoted()
    {
        // An unquoted font family called "y" or "on" would come back from YAML as a
        // boolean, which is the same trap the binding keys have.
        Assert.Null(SettingsSchema.Validate(Find("appearance.font_family"), "on", out var rendered));
        Assert.Equal("\"on\"", rendered);
    }

    [Fact]
    public void QuotesInsideAStringAreEscaped()
    {
        SettingsSchema.Validate(Find("appearance.font_family"), "He said \"hi\"", out var rendered);
        Assert.Equal("\"He said \\\"hi\\\"\"", rendered);
    }

    [Fact]
    public void BindingsCannotBeSetThisWay()
    {
        // A nested map, not a scalar. SetScalar deliberately cannot make that edit, so the
        // refusal has to happen here rather than as a silent no-op.
        var problem = SettingsSchema.Validate(Find("bindings"), "j move-down", out _);

        Assert.NotNull(problem);
        Assert.Contains("in the file", problem, StringComparison.Ordinal);
    }
}

public class GetScalarTests
{
    private const string Yaml = """
        appearance:
          # A comment that must not be read as a value.
          font_size: 13
          colors:
            background: "#1B1D21"

        listing:
          sort_key: name
          show_hidden:
        """;

    [Fact]
    public void ANestedValueIsFound() => Assert.Equal("13", ConfigFile.GetScalar(Yaml, "appearance.font_size"));

    [Fact]
    public void ADeeplyNestedValueIsFound() =>
        Assert.Equal("\"#1B1D21\"", ConfigFile.GetScalar(Yaml, "appearance.colors.background"));

    [Fact]
    public void TheValueIsReturnedExactlyAsWritten() =>
        Assert.Equal("name", ConfigFile.GetScalar(Yaml, "listing.sort_key"));

    [Fact]
    public void AKeyWithNoValueIsNull() => Assert.Null(ConfigFile.GetScalar(Yaml, "listing.show_hidden"));

    [Fact]
    public void AnAbsentKeyIsNull() => Assert.Null(ConfigFile.GetScalar(Yaml, "listing.nonexistent"));

    [Fact]
    public void AnAbsentParentIsNull() => Assert.Null(ConfigFile.GetScalar(Yaml, "nope.font_size"));

    [Fact]
    public void WhatSetScalarWritesIsWhatGetScalarReads()
    {
        // The pair has to agree, or :set would report something other than what it wrote.
        var updated = ConfigFile.SetScalar(Yaml, "appearance.font_size", "17");

        Assert.Equal("17", ConfigFile.GetScalar(updated, "appearance.font_size"));
    }

    [Fact]
    public void ASetValueSurvivesIntoTheParsedConfigWithTheCommentsIntact()
    {
        // The whole :set path bar the twenty lines of glue in ConfigService: validate,
        // rewrite the real generated file, read it back, and confirm both that the setting
        // took effect and that the documentation around it is still there.
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "config.yaml");
        ConfigFile.WriteDefault(path, Fjord.Commands.DefaultBindings.All);

        var setting = SettingsSchema.Find("appearance.font_size")!;
        Assert.Null(SettingsSchema.Validate(setting, "19", out var rendered));

        File.WriteAllText(path, ConfigFile.SetScalar(File.ReadAllText(path), setting.Name, rendered));

        var result = ConfigFile.Load(path);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(19, result.Config.Appearance.FontSize);
        Assert.Contains(
            "# Font size in DIPs.",
            File.ReadAllText(path),
            StringComparison.Ordinal);
    }

    [Fact]
    public void EveryShippedSettingIsReadableFromTheGeneratedFile()
    {
        // The generated file is what a user gets on first run, so :set has to be able to
        // read every documented setting straight out of it. A nesting bug would show up
        // here as a null rather than as a mystery at the keyboard.
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "config.yaml");
        ConfigFile.WriteDefault(path, Fjord.Commands.DefaultBindings.All);

        var text = File.ReadAllText(path);

        // Excluding the map-shaped ones: they are nested blocks rather than scalars, which
        // is exactly why :set refuses them and why they have commands of their own.
        foreach (var setting in SettingsSchema.All.Where(s => !s.Type.Contains("->", StringComparison.Ordinal)))
        {
            Assert.NotNull(ConfigFile.GetScalar(text, setting.Name));
        }
    }
}
