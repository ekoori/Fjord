using Fjord.Commands;
using Fjord.Config;
using Fjord.Core.Diagnostics;
using Fjord.Core.FileSystem;
using Fjord.Tests.Commands;

namespace Fjord.Tests.Config;

public class SortCyclingTests
{
    private static (CommandParser Parser, FakeConfigService Config) Setup(params string[] names)
    {
        var config = new FakeConfigService();

        // Distinct views, so cycling can be told apart from doing nothing.
        var keys = new[] { SortKey.Name, SortKey.Size, SortKey.Modified };

        for (var i = 0; i < names.Length; i++)
        {
            config.Listings[names[i]] = (
                new SortSpec(keys[i % keys.Length], i % 2 == 1, true, true),
                new FilterSpec(false));
        }

        var registry = new CommandRegistry();
        registry.Register(new NavigationCommands(null, null, config));

        return (new CommandParser(registry), config);
    }

    private static CommandContext Context(FakePane pane, IMessageSink? messages = null) =>
        new() { Pane = pane, Messages = messages ?? NullMessageSink.Instance };

    [Fact]
    public void ANamedListingIsAppliedWholesale()
    {
        // Wholesale, not as a patch: the same name gives the same view wherever you were,
        // which is what makes the cycle predictable.
        var (parser, config) = Setup("by_size");
        var pane = new FakePane("/tmp", "a", "b");
        config.Listings["by_size"] = (new SortSpec(SortKey.Size, true, false, false), new FilterSpec(true));

        parser.Execute("sort by_size", Context(pane));

        Assert.Equal(SortKey.Size, pane.Sort.Key);
        Assert.True(pane.Sort.Descending);
        Assert.False(pane.Sort.DirectoriesFirst);
        Assert.True(pane.Filter.ShowHidden);
    }

    [Fact]
    public void BareSortStepsToTheNextListing()
    {
        var (parser, config) = Setup("one", "two", "three");
        var pane = new FakePane("/tmp", "a");

        parser.Execute("sort one", Context(pane));
        parser.Execute("sort", Context(pane));

        Assert.Equal(config.Listings["two"].Sort, pane.Sort);
    }

    [Fact]
    public void TheCycleWrapsAtTheEnd()
    {
        var (parser, config) = Setup("one", "two");
        var pane = new FakePane("/tmp", "a");

        parser.Execute("sort two", Context(pane));
        parser.Execute("sort", Context(pane));

        Assert.Equal(config.Listings["one"].Sort, pane.Sort);
    }

    [Fact]
    public void CyclingFromAViewThatMatchesNothingStartsAtTheFirst()
    {
        // Sorting some other way and then pressing the key should continue from what is on
        // screen, and when that is nothing recognisable the front of the cycle is the only
        // answer that does not depend on hidden state.
        var (parser, config) = Setup("one", "two");
        var pane = new FakePane("/tmp", "a");
        pane.SetOrder(new SortSpec(SortKey.Extension, false, true, true), FilterSpec.Default);

        parser.Execute("sort", Context(pane));

        Assert.Equal(config.Listings["one"].Sort, pane.Sort);
    }

    [Fact]
    public void WithNoListingsBareSortStillTogglesDirection()
    {
        // Removing every listing has to leave a usable command rather than a dead key.
        var (parser, _) = Setup();
        var pane = new FakePane("/tmp", "a");

        var before = pane.Sort.Descending;
        parser.Execute("sort", Context(pane));

        Assert.NotEqual(before, pane.Sort.Descending);
    }

    [Fact]
    public void AFlagStillWorksAndDoesNotCycle()
    {
        var (parser, _) = Setup("one", "two");
        var pane = new FakePane("/tmp", "a");

        parser.Execute("sort -k modified", Context(pane));

        Assert.Equal(SortKey.Modified, pane.Sort.Key);
    }

    [Fact]
    public void AnUnknownListingIsReportedRatherThanIgnored()
    {
        var (parser, _) = Setup("one");
        var pane = new FakePane("/tmp", "a");
        var messages = new RecordingMessageSink();

        parser.Execute("sort nope", Context(pane, messages));

        Assert.Equal(MessageLevel.Error, messages.Messages[^1].Level);
        Assert.Contains("nope", messages.Last!, StringComparison.Ordinal);
    }

    [Fact]
    public void SavingAndRemovingGoThroughTheConfig()
    {
        var (parser, config) = Setup("one");
        var pane = new FakePane("/tmp", "a");

        parser.Execute("listing-save mine", Context(pane));
        Assert.Equal(["mine"], config.Saved);

        parser.Execute("listing-remove mine", Context(pane));
        Assert.Equal(["mine"], config.Removed);
    }

    [Fact]
    public void RemovingSomethingThatIsNotThereSaysSo()
    {
        var (parser, _) = Setup("one");
        var messages = new RecordingMessageSink();

        parser.Execute("listing-remove nope", Context(new FakePane("/tmp"), messages));

        Assert.Equal(MessageLevel.Error, messages.Messages[^1].Level);
    }
}

public class ConfigSectionTests
{
    private const string Yaml = """
        listing:
          sort_key: name

        listings:

          dir_asc:
            sort_key: name
            sort_descending: false

          # A note about size
          siz_asc:
            sort_key: size
            sort_descending: false

        bindings:
          normal:
            "j": "move-down"
        """;

    [Fact]
    public void SectionNamesAreReadInFileOrder()
    {
        // File order is the cycle order, so it has to survive the round trip exactly.
        Assert.Equal(["dir_asc", "siz_asc"], ConfigSections.SectionNames(Yaml, "listings"));
    }

    [Fact]
    public void AddingASectionLeavesTheOthersAlone()
    {
        var updated = ConfigSections.SetSection(
            Yaml, "listings", "mod_asc", ["sort_key: modified", "sort_descending: false"]);

        Assert.Equal(["dir_asc", "siz_asc", "mod_asc"], ConfigSections.SectionNames(updated, "listings"));
        Assert.Contains("# A note about size", updated, StringComparison.Ordinal);
        Assert.Contains("\"j\": \"move-down\"", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacingASectionKeepsItsPlace()
    {
        var updated = ConfigSections.SetSection(
            Yaml, "listings", "dir_asc", ["sort_key: extension"]);

        Assert.Equal(["dir_asc", "siz_asc"], ConfigSections.SectionNames(updated, "listings"));
        Assert.Contains("sort_key: extension", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("sort_descending: false\n\n  # A note", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovingASectionTakesItsCommentWithIt()
    {
        // A block's documentation belongs to the block. Leaving it behind would orphan it
        // above whatever happened to follow, describing something that is no longer there.
        var updated = ConfigSections.RemoveSection(Yaml, "listings", "siz_asc");

        Assert.Equal(["dir_asc"], ConfigSections.SectionNames(updated, "listings"));
        Assert.DoesNotContain("# A note about size", updated, StringComparison.Ordinal);
        Assert.Contains("sort_key: name", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovingSomethingAbsentChangesNothing() =>
        Assert.Equal(Yaml, ConfigSections.RemoveSection(Yaml, "listings", "never_there"));

    [Fact]
    public void AParentThatDoesNotExistIsCreated()
    {
        // :listing-save has to work on a file that has never mentioned listings.
        const string bare = "listing:\n  sort_key: name\n";

        var updated = ConfigSections.SetSection(bare, "listings", "mine", ["sort_key: size"]);

        Assert.Equal(["mine"], ConfigSections.SectionNames(updated, "listings"));
    }

    [Fact]
    public void AnEmptyMapParentBecomesARealOne()
    {
        // "listings: {}" is a parent in name only; appending under it would leave a block
        // YAML still reads as empty.
        const string empty = "listings: {}\n\nbindings: {}\n";

        var updated = ConfigSections.SetSection(empty, "listings", "mine", ["sort_key: size"]);

        Assert.DoesNotContain("listings: {}", updated, StringComparison.Ordinal);
        Assert.Equal(["mine"], ConfigSections.SectionNames(updated, "listings"));
    }

    [Fact]
    public void WhatIsWrittenParsesBackAsTheListingItDescribes()
    {
        // The end-to-end one: the writer and the reader have to agree, or :listing-save
        // produces a name that :sort cannot find.
        var updated = ConfigSections.SetSection(Yaml, "listings", "mine",
        [
            "sort_key: modified",
            "sort_descending: true",
            "directories_first: false",
            "natural_sort: false",
            "show_hidden: true",
        ]);

        var path = Path.Combine(Path.GetTempPath(), "fjord-tests", Guid.NewGuid().ToString("n") + ".yaml");
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, updated);

        var result = ConfigFile.Load(path);

        Assert.True(result.Succeeded, result.Error);
        var preset = result.Config.Listings["mine"];

        Assert.Equal(SortKey.Modified, preset.SortKey);
        Assert.True(preset.SortDescending);
        Assert.False(preset.DirectoriesFirst);
        Assert.False(preset.NaturalSort);
        Assert.True(preset.ShowHidden);
    }
}

public class DefaultListingTests
{
    [Fact]
    public void TheShippedSetPairsEachOrderWithItsReverse()
    {
        // The pairing is what makes one key alternate a column's direction and then move on
        // to the next column.
        foreach (var stem in new[] { "dir", "siz", "mod" })
        {
            Assert.True(DefaultListings.All.ContainsKey($"{stem}_asc"), $"{stem}_asc missing");
            Assert.True(DefaultListings.All.ContainsKey($"{stem}_desc"), $"{stem}_desc missing");
            Assert.False(DefaultListings.All[$"{stem}_asc"].SortDescending);
            Assert.True(DefaultListings.All[$"{stem}_desc"].SortDescending);
        }
    }

    [Fact]
    public void TheGeneratedFileShipsThemAsLiveEntries()
    {
        var rendered = SettingsSchema.Render();

        foreach (var name in DefaultListings.All.Keys)
        {
            Assert.Contains($"  {name}:", rendered, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("listings: {}", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGeneratedListingsParseBackToWhatTheyDescribe()
    {
        var path = Path.Combine(Path.GetTempPath(), "fjord-tests", Guid.NewGuid().ToString("n") + ".yaml");
        ConfigFile.WriteDefault(path, DefaultBindings.All);

        var result = ConfigFile.Load(path);
        Assert.True(result.Succeeded, result.Error);

        foreach (var (name, expected) in DefaultListings.All)
        {
            Assert.True(result.Config.Listings.ContainsKey(name), $"{name} was lost");
            Assert.Equal(expected.SortKey, result.Config.Listings[name].SortKey);
            Assert.Equal(expected.SortDescending, result.Config.Listings[name].SortDescending);
        }
    }
}
