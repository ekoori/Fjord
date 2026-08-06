using System.Reflection;
using Fjord.Config;
using Fjord.Core.FileSystem;

namespace Fjord.Tests.Config;

public class ConfigFileTests
{
    private static string WriteTemp(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), "fjord-tests", Guid.NewGuid().ToString("n") + ".yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, yaml);
        return path;
    }

    [Fact]
    public void MissingFileYieldsDefaults()
    {
        var result = ConfigFile.Load(Path.Combine(Path.GetTempPath(), "fjord-tests", "definitely-absent.yaml"));

        Assert.True(result.Succeeded);
        Assert.False(result.Existed);
        Assert.Equal(13, result.Config.Appearance.FontSize);
    }

    [Fact]
    public void ReadsSettingsWithUnderscoredNames()
    {
        var path = WriteTemp("""
            appearance:
              font_size: 15
              row_padding: 2
            listing:
              sort_key: size
              show_hidden: true
            """);

        var config = ConfigFile.Load(path).Config;

        Assert.Equal(15, config.Appearance.FontSize);
        Assert.Equal(2, config.Appearance.RowPadding);
        Assert.Equal(SortKey.Size, config.Listing.SortKey);
        Assert.True(config.Listing.ShowHidden);
    }

    [Fact]
    public void PartialFileKeepsDefaultsForEverythingElse()
    {
        var config = ConfigFile.Load(WriteTemp("appearance:\n  font_size: 20\n")).Config;

        Assert.Equal(20, config.Appearance.FontSize);
        Assert.Equal("#1B1D21", config.Appearance.Colors.Background);
        Assert.True(config.Listing.DirectoriesFirst);
    }

    [Fact]
    public void UnknownSettingsAreIgnored()
    {
        // A setting removed in a later version must not stop an older config loading.
        var result = ConfigFile.Load(WriteTemp("appearance:\n  font_size: 14\n  no_such_setting: 3\n"));

        Assert.True(result.Succeeded);
        Assert.Equal(14, result.Config.Appearance.FontSize);
    }

    [Fact]
    public void BrokenYamlReportsAnErrorAndStillReturnsAConfig()
    {
        // Never throws: a stray tab must not leave the user with an app that will not start.
        var result = ConfigFile.Load(WriteTemp("appearance:\n\tfont_size: 14\n"));

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
        Assert.Equal(13, result.Config.Appearance.FontSize);
    }

    [Fact]
    public void ConfigMapsOntoSortAndFilterSpecs()
    {
        var config = ConfigFile.Load(WriteTemp("""
            listing:
              sort_key: modified
              sort_descending: true
              directories_first: false
              natural_sort: false
              show_hidden: true
            """)).Config;

        var sort = config.ToSortSpec();
        Assert.Equal(SortKey.Modified, sort.Key);
        Assert.True(sort.Descending);
        Assert.False(sort.DirectoriesFirst);
        Assert.False(sort.Natural);
        Assert.True(sort.NeedsTimes);
        Assert.False(sort.NeedsSize);

        Assert.True(config.ToFilterSpec().ShowHidden);
    }

    [Fact]
    public void SetScalarPreservesCommentsAndOrder()
    {
        // A YamlDotNet round trip would discard every comment and reorder every key, which
        // is fatal for a file whose comments are its documentation.
        const string original = """
            # header comment
            appearance:
              # font size doc
              font_size: 13
              # padding doc
              row_padding: 4
            """;

        var updated = ConfigFile.SetScalar(original, "appearance.font_size", "17");

        Assert.Contains("# header comment", updated, StringComparison.Ordinal);
        Assert.Contains("# font size doc", updated, StringComparison.Ordinal);
        Assert.Contains("# padding doc", updated, StringComparison.Ordinal);
        Assert.Contains("font_size: 17", updated, StringComparison.Ordinal);
        Assert.Contains("row_padding: 4", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("font_size: 13", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void SetScalarLeavesTextAloneWhenTheKeyIsAbsent()
    {
        const string original = "appearance:\n  font_size: 13\n";

        Assert.Equal(original, ConfigFile.SetScalar(original, "appearance.no_such_key", "1"));
    }

    [Fact]
    public void WriteDefaultIfMissingDoesNotClobberAnExistingFile()
    {
        var path = WriteTemp("appearance:\n  font_size: 99\n");

        Assert.False(ConfigFile.WriteDefaultIfMissing(path));
        Assert.Equal(99, ConfigFile.Load(path).Config.Appearance.FontSize);
    }
}

public class SettingsSchemaTests
{
    /// <summary>Derives the dotted names the config object model actually exposes.</summary>
    private static IEnumerable<string> ExpectedNames(Type type, string prefix = "")
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var name = prefix + Underscored(property.Name);
            var propertyType = property.PropertyType;

            // Nested config sections recurse; anything else is a leaf setting.
            if (propertyType.IsClass && propertyType.Namespace == typeof(FjordConfig).Namespace
                && !propertyType.IsGenericType && propertyType != typeof(string))
            {
                foreach (var nested in ExpectedNames(propertyType, name + "."))
                {
                    yield return nested;
                }
            }
            else
            {
                yield return name;
            }
        }
    }

    private static string Underscored(string pascal) =>
        string.Concat(pascal.Select((c, i) =>
            char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    [Fact]
    public void EverySettingIsDocumented()
    {
        // The guard that stops a setting shipping without documentation, and stops
        // documentation describing a setting that no longer exists.
        var expected = ExpectedNames(typeof(FjordConfig)).ToHashSet(StringComparer.Ordinal);
        var documented = SettingsSchema.All.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(expected.Except(documented));
        Assert.Empty(documented.Except(expected));
    }

    [Fact]
    public void GeneratedDefaultsParseBackToAnEquivalentConfig()
    {
        // The shipped file and the object model must agree, or a user editing the file
        // they were given gets something other than what it says.
        var path = Path.Combine(Path.GetTempPath(), "fjord-tests", Guid.NewGuid().ToString("n") + ".yaml");
        ConfigFile.WriteDefault(path);

        var result = ConfigFile.Load(path);
        var fresh = new FjordConfig();

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(fresh.Appearance.FontSize, result.Config.Appearance.FontSize);
        Assert.Equal(fresh.Appearance.FontFamily, result.Config.Appearance.FontFamily);
        Assert.Equal(fresh.Appearance.RowPadding, result.Config.Appearance.RowPadding);
        Assert.Equal(fresh.Appearance.Colors.Background, result.Config.Appearance.Colors.Background);
        Assert.Equal(fresh.Appearance.Colors.Directory, result.Config.Appearance.Colors.Directory);
        Assert.Equal(fresh.Listing.SortKey, result.Config.Listing.SortKey);
        Assert.Equal(fresh.Listing.DirectoriesFirst, result.Config.Listing.DirectoriesFirst);
        Assert.Equal(fresh.Listing.ShowHidden, result.Config.Listing.ShowHidden);
    }

    [Fact]
    public void GeneratedFileKeepsEachSettingsDocumentation()
    {
        var rendered = SettingsSchema.Render();

        foreach (var setting in SettingsSchema.All)
        {
            Assert.Contains(setting.Doc.Split('.')[0], rendered, StringComparison.Ordinal);
        }
    }

    [Theory]
    // The reason every binding key is quoted. YAML 1.1 reads these as booleans, and n and
    // y are core vim bindings — bare, the key becomes `false` and the binding vanishes
    // with no error anywhere.
    [InlineData("n")]
    [InlineData("y")]
    [InlineData("on")]
    [InlineData("off")]
    [InlineData("yes")]
    [InlineData("no")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("Y")]
    [InlineData("N")]
    public void BooleanLookingBindingKeysSurviveARoundTrip(string keys)
    {
        var yaml = $"bindings:\n  normal:\n    {SettingsSchema.QuoteKey(keys)}: \"some-command\"\n";
        var path = Path.Combine(Path.GetTempPath(), "fjord-tests", Guid.NewGuid().ToString("n") + ".yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, yaml);

        var config = ConfigFile.Load(path).Config;

        Assert.True(config.Bindings.TryGetValue("normal", out var mode), "normal mode missing");
        Assert.True(mode.ContainsKey(keys), $"binding '{keys}' was lost");
        Assert.Equal("some-command", mode[keys]);
    }

    [Theory]
    [InlineData("n")]
    [InlineData("y")]
    [InlineData("off")]
    public void UnquotedBooleanLookingKeysAlsoSurvive(string keys)
    {
        // Pins the reason the quoting rule is a convention rather than a correctness
        // requirement for reading. YAML 1.1 does read bare n, y, on and off as booleans,
        // but the binding map is typed Dictionary<string, Dictionary<string, string>>, so
        // YamlDotNet takes each key's scalar text and never applies type inference.
        //
        // If this test starts failing, the binding map has been loosened to an untyped
        // shape (object values for plugin settings, say) and unquoted keys have become
        // genuinely lossy. Quoting is still emitted — see QuotesKeysOtherToolsWouldMisread.
        var path = Path.Combine(Path.GetTempPath(), "fjord-tests", Guid.NewGuid().ToString("n") + ".yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"bindings:\n  normal:\n    {keys}: \"some-command\"\n");

        var mode = ConfigFile.Load(path).Config.Bindings["normal"];

        Assert.True(mode.ContainsKey(keys), $"binding '{keys}' was lost");
    }

    [Theory]
    // These genuinely need quoting: bare, they are not valid YAML keys at all, or they
    // parse as something else entirely. This is what makes QuoteKey load-bearing.
    [InlineData(":")]
    [InlineData("#")]
    [InlineData("- ")]
    [InlineData("{")]
    [InlineData("[")]
    [InlineData("&a")]
    [InlineData("*a")]
    [InlineData("<Ctrl+a>")]
    [InlineData("\"")]
    [InlineData("\\")]
    public void KeysNeedingQuotingRoundTrip(string keys)
    {
        var path = Path.Combine(Path.GetTempPath(), "fjord-tests", Guid.NewGuid().ToString("n") + ".yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"bindings:\n  normal:\n    {SettingsSchema.QuoteKey(keys)}: \"some-command\"\n");

        var result = ConfigFile.Load(path);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("some-command", result.Config.Bindings["normal"][keys]);
    }

    [Fact]
    public void QuotesKeysOtherToolsWouldMisread()
    {
        // Fjord's own reader is safe, but the file is also read by yq, editor YAML modes
        // and anything else the user points at it. Bare `n:` shows up as a boolean there.
        Assert.Equal("\"n\"", SettingsSchema.QuoteKey("n"));
        Assert.Equal("\"<Ctrl+a>\"", SettingsSchema.QuoteKey("<Ctrl+a>"));
        Assert.Equal("\"\\\"\"", SettingsSchema.QuoteKey("\""));
        Assert.Equal("\"\\\\\"", SettingsSchema.QuoteKey("\\"));
    }

    [Fact]
    public void DefaultBindingsAreWrittenAsLiveEntries()
    {
        // Live, not commented out: a commented block reads as documentation you have to
        // retype, and editing in place is the point of opening the file.
        var defaults = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["normal"] = new Dictionary<string, string> { ["j"] = "move-down", ["n"] = "next-match" },
        };

        var rendered = SettingsSchema.Render(defaults);

        Assert.Contains("    \"j\": \"move-down\"", rendered, StringComparison.Ordinal);
        Assert.Contains("    \"n\": \"next-match\"", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("bindings: {}", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void EachBindingCarriesItsCommandDescription()
    {
        var defaults = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["normal"] = new Dictionary<string, string> { ["j"] = "move-down" },
        };

        var descriptions = new Dictionary<string, string> { ["move-down"] = "Move the cursor down" };

        var rendered = SettingsSchema.Render(defaults, descriptions);

        Assert.Contains("    # Move the cursor down", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ABindingWithNoKnownDescriptionIsStillWritten()
    {
        var defaults = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["normal"] = new Dictionary<string, string> { ["j"] = "mystery-command" },
        };

        Assert.Contains(
            "\"j\": \"mystery-command\"",
            SettingsSchema.Render(defaults, new Dictionary<string, string>()),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheGeneratedFileParsesBackWithEveryShippedBinding()
    {
        // The real risk of writing bindings live: one bad quote and the whole file stops
        // loading. Every shipped key goes through the round trip.
        var path = Path.Combine(Path.GetTempPath(), "fjord-tests", Guid.NewGuid().ToString("n") + ".yaml");
        ConfigFile.WriteDefault(path, Fjord.Commands.DefaultBindings.All);

        var result = ConfigFile.Load(path);
        Assert.True(result.Succeeded, result.Error);

        foreach (var (mode, expected) in Fjord.Commands.DefaultBindings.All)
        {
            Assert.True(result.Config.Bindings.TryGetValue(mode, out var actual), $"mode {mode} missing");

            foreach (var (keys, command) in expected)
            {
                Assert.True(actual!.ContainsKey(keys), $"{mode}/{keys} was lost");
                Assert.Equal(command, actual[keys]);
            }
        }
    }

    [Fact]
    public void EveryShippedBindingHasAnExplanationToWriteAboveIt()
    {
        // The pairing the app actually writes with. A binding whose command the catalogue
        // does not know still lands in the file, but silently without its comment — so the
        // file quietly stops explaining itself. Catch that here rather than by reading it.
        var descriptions = Fjord.Commands.CommandCatalogue.Descriptions;

        var undescribed = Fjord.Commands.DefaultBindings.All
            .SelectMany(mode => mode.Value.Values)
            .Select(command => command.TrimStart(':').Split(' ')[0])
            .Where(name => string.IsNullOrEmpty(descriptions.GetValueOrDefault(name)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(undescribed);
    }

    [Fact]
    public void TheCatalogueAgreesWithTheRegistryItStandsInFor()
    {
        // The catalogue reads attributes; the registry reads built instances. They describe
        // the same commands, and the generated file is only trustworthy while they match.
        var registry = new Fjord.Commands.CommandRegistry();
        registry.Register(new Fjord.Commands.NavigationCommands());

        foreach (var command in registry.All)
        {
            Assert.Equal(
                command.Description,
                Fjord.Commands.CommandCatalogue.Descriptions.GetValueOrDefault(command.Name));
        }
    }

    [Fact]
    public void AModeWithNoBindingsDoesNotCrashTheLoader()
    {
        // "normal:" with nothing under it is valid YAML that deserializes to null, and is
        // easy to produce while editing a file full of live entries.
        var path = Path.Combine(Path.GetTempPath(), "fjord-tests", Guid.NewGuid().ToString("n") + ".yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "bindings:\n  normal:\n");

        var config = ConfigFile.Load(path).Config;
        var bindings = new Fjord.Core.Input.KeyBindings();
        var modes = new Fjord.Core.Input.KeyModeRegistry();

        var problems = Fjord.Commands.DefaultBindings.Apply(bindings, modes, config.Bindings);

        Assert.Single(problems);
        Assert.Contains("no bindings", problems[0], StringComparison.Ordinal);
    }
}
