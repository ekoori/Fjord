using Fjord.Core.Input;
using Fjord.Core.Keys;

namespace Fjord.Tests.Input;

/// <summary>
/// The <c>[count1] operator [count2] motion</c> grammar. QuteSharp has no equivalent, so
/// none of this is covered by the ported suite.
/// </summary>
public class OperatorPendingTests
{
    private static readonly HashSet<string> Operators = new(StringComparer.Ordinal) { "delete", "yank" };

    private static ModeManager Setup()
    {
        var bindings = new KeyBindings();
        var modes = new KeyModeRegistry();

        foreach (var mode in new[] { KeyMode.Normal, KeyMode.Visual, KeyMode.Operator })
        {
            bindings.AddDefault(mode, "j", "move-down");
            bindings.AddDefault(mode, "k", "move-up");
            bindings.AddDefault(mode, "gg", "move-first");
        }

        bindings.AddDefault(KeyMode.Normal, "d", "delete");
        bindings.AddDefault(KeyMode.Normal, "y", "yank");
        bindings.AddDefault(KeyMode.Normal, "x", "delete-char");
        bindings.AddDefault(KeyMode.Visual, "d", "delete");
        bindings.AddDefault(KeyMode.Visual, "y", "yank");

        return new ModeManager(bindings, modes, Operators.Contains);
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
    public void AnOperatorAloneDoesNotDispatch()
    {
        var manager = Setup();

        var result = Feed(manager, "d");

        Assert.False(result.HasCommand);
        Assert.False(result.HasOperator);
        Assert.Equal(KeyMode.Operator, manager.Mode);
        Assert.Equal("delete", manager.PendingOperator);
    }

    [Fact]
    public void OperatorPlusMotionDispatchesBoth()
    {
        var manager = Setup();

        var result = Feed(manager, "dj");

        Assert.Equal("move-down", result.Command);
        Assert.Equal("delete", result.Operator);
        Assert.Equal(KeyMode.Normal, manager.Mode);
    }

    [Fact]
    public void CountBeforeTheOperatorApplies()
    {
        var manager = Setup();

        Assert.Equal(3, Feed(manager, "3dj").Count);
    }

    [Fact]
    public void CountAfterTheOperatorApplies()
    {
        var manager = Setup();

        Assert.Equal(2, Feed(manager, "d2j").Count);
    }

    [Fact]
    public void CountsOnBothSidesMultiply()
    {
        // vim's rule: 3d2j covers six rows, not five and not two.
        var manager = Setup();

        Assert.Equal(6, Feed(manager, "3d2j").Count);
    }

    [Fact]
    public void NoCountAnywhereStaysNull()
    {
        // Null is not 1: a command with no count must fall back to its own default.
        var manager = Setup();

        Assert.Null(Feed(manager, "dj").Count);
    }

    [Fact]
    public void MultiKeyMotionsCompleteAnOperator()
    {
        var manager = Setup();

        var result = Feed(manager, "dgg");

        Assert.Equal("move-first", result.Command);
        Assert.Equal("delete", result.Operator);
    }

    [Fact]
    public void EscapeCancelsAPendingOperator()
    {
        // The operator must not stay armed to fire on the next motion typed in normal mode.
        var manager = Setup();
        Feed(manager, "d");

        manager.Reset();

        Assert.Equal(KeyMode.Normal, manager.Mode);
        Assert.Null(manager.PendingOperator);

        var result = Feed(manager, "j");
        Assert.Equal("move-down", result.Command);
        Assert.Null(result.Operator);
    }

    [Fact]
    public void LeavingOperatorPendingByModeChangeAlsoCancels()
    {
        var manager = Setup();
        Feed(manager, "d");

        manager.EnterMode(KeyMode.Normal);

        Assert.Null(manager.PendingOperator);
    }

    [Fact]
    public void AnOperatorInVisualModeRunsImmediately()
    {
        // The range already exists, so there is nothing to wait for. This is why an
        // operator key in visual mode must not enter operator-pending.
        var manager = Setup();
        manager.EnterMode(KeyMode.Visual);

        var result = Feed(manager, "y");

        Assert.Equal("yank", result.Operator);
        Assert.Null(result.Command);
        Assert.Equal(KeyMode.Visual, manager.Mode);
    }

    [Fact]
    public void NonOperatorCommandsAreUnaffected()
    {
        var manager = Setup();

        var result = Feed(manager, "x");

        Assert.Equal("delete-char", result.Command);
        Assert.Null(result.Operator);
        Assert.Equal(KeyMode.Normal, manager.Mode);
    }

    [Fact]
    public void MotionsInVisualModeStayOrdinaryCommands()
    {
        var manager = Setup();
        manager.EnterMode(KeyMode.Visual);

        var result = Feed(manager, "3j");

        Assert.Equal("move-down", result.Command);
        Assert.Null(result.Operator);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void RegisterIsCarriedToTheOperator()
    {
        var manager = Setup();
        manager.SetRegister('a');

        var result = Feed(manager, "dj");

        Assert.Equal('a', result.Register);
        Assert.Equal("delete", result.Operator);
    }

    [Fact]
    public void RegisterIsCarriedToAPlainCommand()
    {
        var manager = Setup();
        manager.SetRegister('b');

        Assert.Equal('b', Feed(manager, "x").Register);
    }

    [Fact]
    public void RegisterIsConsumedByOneCommandOnly()
    {
        var manager = Setup();
        manager.SetRegister('a');

        Feed(manager, "x");

        Assert.Null(Feed(manager, "x").Register);
    }

    [Fact]
    public void PendingOperatorIsVisibleForTheStatusBar()
    {
        var manager = Setup();

        Feed(manager, "3d2");

        Assert.Equal("delete", manager.PendingOperator);
        Assert.Contains("2", manager.PendingKeys, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedMotionInOperatorPendingDropsTheOperator()
    {
        // "dq" — q is bound to nothing, so there is no range. The operator must be
        // abandoned, not left armed: otherwise the next "j" typed silently deletes.
        var manager = Setup();
        Feed(manager, "d");

        var result = Feed(manager, "q");

        Assert.False(result.HasCommand);
        Assert.False(result.HasOperator);
        Assert.Null(manager.PendingOperator);
        Assert.Equal(KeyMode.Normal, manager.Mode);

        var next = Feed(manager, "j");
        Assert.Equal("move-down", next.Command);
        Assert.Null(next.Operator);
    }

    [Fact]
    public void ADoubledOperatorActsOnTheCurrentRow()
    {
        // vim's dd. Without it there is no way to trash the entry under the cursor with an
        // operator at all — d waits for a motion, and "dj" already means two rows.
        var manager = Setup();

        var result = Feed(manager, "dd");

        Assert.Equal("delete", result.Operator);
        Assert.Equal("move-down", result.Command);
        Assert.Equal(0, result.Count);
        Assert.Equal(KeyMode.Normal, manager.Mode);
        Assert.Null(manager.PendingOperator);
    }

    [Fact]
    public void ADoubledYankIsTheSameShape()
    {
        var result = Feed(Setup(), "yy");

        Assert.Equal("yank", result.Operator);
        Assert.Equal("move-down", result.Command);
        Assert.Equal(0, result.Count);
    }

    [Theory]
    [InlineData("3dd", 2)]
    [InlineData("d3d", 2)]
    [InlineData("2d3d", 5)]
    public void ACountOnADoubledOperatorCoversThatManyRows(string keys, int expectedMotion)
    {
        // Expressed as move-down by one less, so the dispatcher's diff of the cursor gives
        // exactly the rows covered. 3dd is three rows, which is two moves from where you are.
        var result = Feed(Setup(), keys);

        Assert.Equal("delete", result.Operator);
        Assert.Equal(expectedMotion, result.Count);
    }

    [Fact]
    public void ADifferentOperatorKeyDoesNotDouble()
    {
        // "dy" is not a doubling — y is not bound in operator-pending, so this is the
        // failed-motion path and the operator has to be abandoned.
        var manager = Setup();

        var result = Feed(manager, "dy");

        Assert.False(result.HasOperator);
        Assert.Null(manager.PendingOperator);
        Assert.Equal(KeyMode.Normal, manager.Mode);
    }

    [Fact]
    public void ADoubledOperatorHonoursTheRegister()
    {
        var manager = Setup();
        manager.SetRegister('a');

        var result = Feed(manager, "yy");

        Assert.Equal('a', result.Register);
    }

    [Fact]
    public void TheDoublingKeyIsForgottenAfterwards()
    {
        // The armed key has to be cleared with the operator, or a later bare "d" in normal
        // mode would look like the second half of a doubling that is no longer pending.
        var manager = Setup();
        Feed(manager, "dd");

        var result = Feed(manager, "d");

        Assert.False(result.HasOperator);
        Assert.Equal(KeyMode.Operator, manager.Mode);
        Assert.Equal("delete", manager.PendingOperator);
    }
}

public class ModeFallbackTests
{
    private static ModeManager Setup()
    {
        var bindings = new KeyBindings();
        var modes = new KeyModeRegistry();

        bindings.AddDefault(KeyMode.Normal, "j", "move-down");
        bindings.AddDefault(KeyMode.Normal, "d", "trash");
        bindings.AddDefault(KeyMode.Normal, "gs", "scrap-open");

        // Only what differs, which is the point of the fallback.
        bindings.AddDefault(KeyMode.Scrapbook, "dd", "scrap-remove");

        var manager = new ModeManager(bindings, modes, name => name == "trash");
        manager.EnterMode(KeyMode.Scrapbook);
        return manager;
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
    public void AKeyTheModeDoesNotDefineFallsThrough()
    {
        // Without this a scrapbook would have to restate every motion to avoid taking it
        // away, and the duplicate would drift the first time a default changed.
        Assert.Equal("move-down", Feed(Setup(), "j").Command);
    }

    [Fact]
    public void ASequenceTheModeDefinesWinsOverTheFallback()
    {
        // dd is the whole reason for the mode: d alone is an operator in normal mode, and
        // firing it would make the specialised binding unreachable.
        var result = Feed(Setup(), "dd");

        Assert.Equal("scrap-remove", result.Command);
        Assert.Null(result.Operator);
    }

    [Fact]
    public void APartialInTheModeBeatsAnExactInTheFallback()
    {
        // The first d is a prefix here and a whole binding in normal mode. It has to wait.
        var manager = Setup();

        var result = Feed(manager, "d");

        Assert.False(result.HasCommand);
        Assert.False(result.HasOperator);
        Assert.Equal(KeyMode.Scrapbook, manager.Mode);
    }

    [Fact]
    public void AMultiKeyFallbackStillResolves()
    {
        Assert.Equal("scrap-open", Feed(Setup(), "gs").Command);
    }

    [Fact]
    public void TheFallbackAppliesInNormalModeToo()
    {
        // Normal falls back to nothing, so d is the operator it always was.
        var bindings = new KeyBindings();
        var modes = new KeyModeRegistry();
        bindings.AddDefault(KeyMode.Normal, "d", "trash");

        var manager = new ModeManager(bindings, modes, name => name == "trash");

        Feed(manager, "d");

        Assert.Equal("trash", manager.PendingOperator);
    }
}
