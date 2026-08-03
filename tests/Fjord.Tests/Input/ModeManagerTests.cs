using Fjord.Core.Input;
using Fjord.Core.Keys;

namespace Fjord.Tests.Input;

/// <summary>
/// Ported from QuteSharp's <c>KeyInput/ModeManagerTests.cs</c>. Command strings are
/// opaque tags here, so they read in file-manager terms; every assertion is unchanged.
/// </summary>
public class KeyBindingsTests
{
    private static KeyBindings WithNormal(params (string Keys, string Command)[] entries)
    {
        var bindings = new KeyBindings();
        foreach (var (keys, command) in entries)
        {
            bindings.AddDefault(KeyMode.Normal, keys, command);
        }

        return bindings;
    }

    [Fact]
    public void ExactMatchReturnsCommand()
    {
        var match = WithNormal(("j", "move-down")).Match(KeyMode.Normal, KeySequence.Parse("j"));

        Assert.True(match.IsExact);
        Assert.Equal("move-down", match.Command);
    }

    [Fact]
    public void PrefixOfLongerBindingIsPartial()
    {
        // "g" must not resolve while "gg" exists, or the longer binding is unreachable.
        var match = WithNormal(("gg", "move-first")).Match(KeyMode.Normal, KeySequence.Parse("g"));

        Assert.True(match.IsPartial);
        Assert.Null(match.Command);
    }

    [Fact]
    public void UnrelatedKeyDoesNotMatch()
    {
        Assert.Equal(SequenceMatch.NoMatch,
            WithNormal(("j", "move-down")).Match(KeyMode.Normal, KeySequence.Parse("q")).Match);
    }

    [Fact]
    public void BindingsAreScopedToMode()
    {
        var bindings = WithNormal(("j", "move-down"));

        Assert.Equal(SequenceMatch.NoMatch, bindings.Match(KeyMode.Insert, KeySequence.Parse("j")).Match);
    }

    [Fact]
    public void UserBindingOverridesDefault()
    {
        var bindings = WithNormal(("j", "move-down"));
        bindings.Bind(KeyMode.Normal, "j", "move-up");

        Assert.Equal("move-up", bindings.Match(KeyMode.Normal, KeySequence.Parse("j")).Command);
    }

    [Fact]
    public void UnbindMasksDefaultAndBindDefaultRestoresIt()
    {
        // A default must be masked rather than deleted, or :bind-default has nothing
        // to restore.
        var bindings = WithNormal(("j", "move-down"));

        bindings.Unbind(KeyMode.Normal, "j");
        Assert.Equal(SequenceMatch.NoMatch, bindings.Match(KeyMode.Normal, KeySequence.Parse("j")).Match);

        bindings.BindDefault(KeyMode.Normal, "j");
        Assert.Equal("move-down", bindings.Match(KeyMode.Normal, KeySequence.Parse("j")).Command);
    }

    [Fact]
    public void EffectiveBindingsExcludeMaskedDefaults()
    {
        var bindings = WithNormal(("j", "move-down"), ("k", "move-up"));
        bindings.Unbind(KeyMode.Normal, "j");

        Assert.Equal(["k"], bindings.For(KeyMode.Normal).Keys.Select(k => k.ToString()));
    }

    [Fact]
    public void ClearUserBindingsRestoresDefaults()
    {
        // A config reload drops the user layer and re-applies it; a stale merged view
        // would keep serving the old binding.
        var bindings = WithNormal(("j", "move-down"));
        bindings.Bind(KeyMode.Normal, "j", "move-up");
        Assert.Equal("move-up", bindings.Match(KeyMode.Normal, KeySequence.Parse("j")).Command);

        bindings.ClearUserBindings();

        Assert.Equal("move-down", bindings.Match(KeyMode.Normal, KeySequence.Parse("j")).Command);
    }

    [Fact]
    public void BindingAddedAfterAMatchIsVisible()
    {
        // Match caches the merged per-mode view. Every mutation must invalidate it, or
        // bindings added after the first keystroke are silently ignored.
        var bindings = WithNormal(("j", "move-down"));
        Assert.True(bindings.Match(KeyMode.Normal, KeySequence.Parse("j")).IsExact);

        bindings.AddDefault(KeyMode.Normal, "k", "move-up");

        Assert.Equal("move-up", bindings.Match(KeyMode.Normal, KeySequence.Parse("k")).Command);
    }

    [Fact]
    public void MatchIgnoresBindingsWithADifferentFirstKey()
    {
        // Match narrows the scan by first key; a binding sharing a later key must not
        // leak into an unrelated prefix.
        var bindings = WithNormal(("gg", "move-first"), ("dg", "delete-to-first"));

        Assert.True(bindings.Match(KeyMode.Normal, KeySequence.Parse("g")).IsPartial);
        Assert.True(bindings.Match(KeyMode.Normal, KeySequence.Parse("d")).IsPartial);
        Assert.Equal("move-first", bindings.Match(KeyMode.Normal, KeySequence.Parse("gg")).Command);
        Assert.Equal("delete-to-first", bindings.Match(KeyMode.Normal, KeySequence.Parse("dg")).Command);
    }
}

public class ModeManagerTests
{
    private static ModeManager Setup(params (string Keys, string Command)[] entries)
    {
        var bindings = new KeyBindings();
        foreach (var (keys, command) in entries)
        {
            bindings.AddDefault(KeyMode.Normal, keys, command);
        }

        bindings.AddDefault(KeyMode.Insert, "<Escape>", "mode-leave");
        return new ModeManager(bindings, new KeyModeRegistry());
    }

    private static KeyResult Feed(ModeManager manager, string keys)
    {
        var result = default(KeyResult);
        foreach (var key in KeySequence.Parse(keys))
        {
            result = manager.HandleKey(key);
        }

        return result;
    }

    [Fact]
    public void SingleKeyRunsCommand()
    {
        var result = Feed(Setup(("j", "move-down")), "j");

        Assert.Equal("move-down", result.Command);
        Assert.Equal(KeyDisposition.Consumed, result.Disposition);
    }

    [Fact]
    public void MultiKeyBindingBuffersUntilComplete()
    {
        var manager = Setup(("gg", "move-first"));

        var first = manager.HandleKey(KeySequence.Parse("g")[0]);
        Assert.False(first.HasCommand);
        Assert.Equal("g", manager.PendingKeys);

        var second = manager.HandleKey(KeySequence.Parse("g")[0]);
        Assert.Equal("move-first", second.Command);
        Assert.Equal("", manager.PendingKeys);
    }

    [Fact]
    public void CountPrefixIsCollectedAndReported()
    {
        var result = Feed(Setup(("j", "move-down")), "3j");

        Assert.Equal("move-down", result.Command);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void MultiDigitCount()
    {
        Assert.Equal(42, Feed(Setup(("j", "move-down")), "42j").Count);
    }

    [Fact]
    public void NoCountMeansNull()
    {
        // Commands must be able to tell "no count given" from "count of 1".
        Assert.Null(Feed(Setup(("j", "move-down")), "j").Count);
    }

    [Fact]
    public void LeadingZeroIsABindingNotACount()
    {
        // "0" is itself bound, so it cannot start a count.
        var result = Feed(Setup(("0", "move-first-column")), "0");

        Assert.Equal("move-first-column", result.Command);
    }

    [Fact]
    public void ZeroAfterADigitIsPartOfTheCount()
    {
        Assert.Equal(10, Feed(Setup(("j", "move-down")), "10j").Count);
    }

    [Fact]
    public void CountIsClearedAfterUse()
    {
        var manager = Setup(("j", "move-down"));

        Feed(manager, "3j");
        Assert.Null(Feed(manager, "j").Count);
    }

    [Fact]
    public void UnmatchedKeyIsSwallowedInNormalMode()
    {
        // Swallowing is what makes single-letter commands safe to type.
        var result = Feed(Setup(("j", "move-down")), "q");

        Assert.Equal(KeyDisposition.Consumed, result.Disposition);
        Assert.False(result.HasCommand);
    }

    [Fact]
    public void UnmatchedKeyIsForwardedInInsertMode()
    {
        // The opposite rule: in a text-entry mode an unbound key must reach the focused
        // control or the user cannot type.
        var manager = Setup(("j", "move-down"));
        manager.EnterMode(KeyMode.Insert);

        Assert.Equal(KeyDisposition.Forward, Feed(manager, "q").Disposition);
    }

    [Fact]
    public void FailedPrefixDiscardsBufferedKeys()
    {
        // After "g" then "q", the "g" was only held on the promise of "gg"; it must
        // not later combine with an unrelated key.
        var manager = Setup(("gg", "move-first"), ("j", "move-down"));

        manager.HandleKey(KeySequence.Parse("g")[0]);
        manager.HandleKey(KeySequence.Parse("q")[0]);

        Assert.Equal("", manager.PendingKeys);
        Assert.Equal("move-down", Feed(manager, "j").Command);
    }

    [Fact]
    public void PendingKeysAreVisibleForTheStatusBar()
    {
        var manager = Setup(("gg", "move-first"));

        manager.HandleKey(KeySequence.Parse("3")[0]);
        manager.HandleKey(KeySequence.Parse("g")[0]);

        Assert.Equal("3g", manager.PendingKeys);
    }

    [Fact]
    public void ChangingModeClearsPendingKeys()
    {
        var manager = Setup(("gg", "move-first"));
        manager.HandleKey(KeySequence.Parse("g")[0]);

        manager.EnterMode(KeyMode.Insert);

        Assert.Equal("", manager.PendingKeys);
    }

    [Fact]
    public void ModeChangeIsAnnounced()
    {
        var manager = Setup();
        ModeChangedEventArgs? seen = null;
        manager.ModeChanged += (_, e) => seen = e;

        manager.EnterMode(KeyMode.Insert);

        Assert.Equal(KeyMode.Normal, seen!.From);
        Assert.Equal(KeyMode.Insert, seen.To);
    }

    [Fact]
    public void ReenteringTheSameModeIsANoOp()
    {
        var manager = Setup();
        var count = 0;
        manager.ModeChanged += (_, _) => count++;

        manager.EnterMode(KeyMode.Normal);

        Assert.Equal(0, count);
    }

    [Fact]
    public void CountIsIgnoredInModesThatDoNotSupportIt()
    {
        // Typing "3" in a text-entry mode must insert a 3, not start a count.
        var manager = Setup();
        manager.EnterMode(KeyMode.Insert);

        Assert.Equal(KeyDisposition.Forward, Feed(manager, "3").Disposition);
    }

    [Fact]
    public void VisualModeSupportsCounts()
    {
        // Visual mode is new in Fjord; "3j" must extend the selection by three, so the
        // count has to survive.
        var bindings = new KeyBindings();
        bindings.AddDefault(KeyMode.Visual, "j", "move-down");
        var manager = new ModeManager(bindings, new KeyModeRegistry());
        manager.EnterMode(KeyMode.Visual);

        Assert.Equal(3, Feed(manager, "3j").Count);
    }

    [Fact]
    public void EnteringAnUnregisteredModeThrows()
    {
        // A typo in a mode name should fail where it happens, not silently swallow
        // every subsequent key.
        var manager = Setup();

        Assert.Throws<ArgumentException>(() => manager.EnterMode(new KeyMode("vsiual")));
    }
}

public class KeyModeTests
{
    [Fact]
    public void DefaultValueReadsAsNormal()
    {
        // KeyMode is a dictionary key throughout KeyBindings; a default-constructed one
        // carrying a null name would throw deep inside a lookup.
        Assert.Equal(KeyMode.Normal, default(KeyMode));
        Assert.Equal("normal", default(KeyMode).Name);
    }

    [Fact]
    public void ComparisonIsOrdinal()
    {
        Assert.Equal(KeyMode.Visual, new KeyMode("visual"));
        Assert.NotEqual(KeyMode.Visual, new KeyMode("Visual"));
    }

    [Fact]
    public void NullNameIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new KeyMode(null!));
    }

    [Theory]
    [InlineData("normal", false, true, false)]
    [InlineData("visual", false, true, true)]
    [InlineData("operator-pending", false, true, false)]
    [InlineData("command", true, false, false)]
    [InlineData("insert", true, false, false)]
    [InlineData("register", false, false, false)]
    public void BuiltInModesHaveExpectedTraits(
        string name, bool forwards, bool counts, bool selection)
    {
        var registry = new KeyModeRegistry();
        Assert.True(registry.TryParse(name, out var mode));

        Assert.Equal(forwards, registry.ForwardsUnmatchedKeys(mode));
        Assert.Equal(counts, registry.SupportsCount(mode));
        Assert.Equal(selection, registry.IsSelectionMode(mode));
    }

    [Fact]
    public void UnknownModeNameDoesNotResolve()
    {
        Assert.False(new KeyModeRegistry().TryParse("nonsense", out _));
    }

    [Fact]
    public void PluginModesCanBeRegistered()
    {
        // The reason KeyMode stopped being an enum.
        var registry = new KeyModeRegistry();
        var mode = registry.Register("preview", new KeyModeTraits(SupportsCount: true));

        Assert.True(registry.IsRegistered(mode));
        Assert.True(registry.SupportsCount(mode));
        Assert.True(registry.TryParse("preview", out _));
    }
}
