// Vendored from QuteSharp (src/QuteSharp.KeyInput/ModeManager.cs). See VENDORED.md.
// Changed: per-mode policy comes from KeyModeRegistry rather than a switch over an enum.
using System.Text;
using Fjord.Core.Keys;

namespace Fjord.Core.Input;

/// <summary>What the caller should do with a key after the mode manager saw it.</summary>
public enum KeyDisposition
{
    /// <summary>Consumed; do not pass it on.</summary>
    Consumed,

    /// <summary>Not bound here; the focused control should receive it.</summary>
    Forward,
}

/// <summary>The result of feeding one key to <see cref="ModeManager"/>.</summary>
/// <param name="Command">
/// The command to run. When <paramref name="Operator"/> is also set this is the
/// <i>motion</i> that gives the operator its range.
/// </param>
/// <param name="Operator">
/// An operator awaiting a range. With <paramref name="Command"/> set, the range comes from
/// running that motion; with it null, the range is the current visual selection.
/// </param>
public readonly record struct KeyResult(
    KeyDisposition Disposition,
    string? Command = null,
    int? Count = null,
    string? Operator = null,
    char? Register = null)
{
    public bool HasCommand => Command != null;

    /// <summary>True when an operator is ready to run against a range.</summary>
    public bool HasOperator => Operator != null;
}

public sealed class ModeChangedEventArgs : EventArgs
{
    public required KeyMode From { get; init; }
    public required KeyMode To { get; init; }
}

/// <summary>
/// Tracks the current input mode and turns keystrokes into commands.
/// </summary>
/// <remarks>
/// The interesting behaviour is all in the buffering: a keystroke may complete a
/// binding, may be a prefix of a longer one and must be held, or may be neither and has
/// to be discarded or forwarded depending on the mode.
/// </remarks>
public sealed class ModeManager
{
    private readonly KeyBindings _bindings;
    private readonly KeyModeRegistry _modes;
    private readonly Func<string, bool> _isOperator;
    private readonly List<KeyInfo> _buffer = [];
    private readonly StringBuilder _count = new();

    // The single keystroke that resolved the binding being dispatched, or null when it took
    // more than one. Only meaningful for the duration of one Resolve call.
    private KeyInfo? _armedBy;

    private string? _pendingOperator;
    private int? _pendingOperatorCount;
    private char? _pendingRegister;

    // The key that armed the operator, so pressing it again can mean "this line".
    private KeyInfo? _pendingOperatorKey;

    /// <param name="isOperator">
    /// Whether a command name waits for a motion. A delegate rather than a reference to
    /// the command registry, so Core does not depend on the command layer.
    /// </param>
    public ModeManager(KeyBindings bindings, KeyModeRegistry modes, Func<string, bool>? isOperator = null)
    {
        _bindings = bindings;
        _modes = modes;
        _isOperator = isOperator ?? (_ => false);
    }

    public KeyMode Mode { get; private set; } = KeyMode.Normal;

    /// <summary>
    /// Keys typed so far that have not yet resolved, shown in the status bar so the
    /// user can see a partially-typed binding.
    /// </summary>
    public string PendingKeys =>
        (_pendingRegister is { } r ? "\"" + r : "")
        + (_pendingOperator is not null ? _pendingOperatorCount?.ToString() + "…" : "")
        + _count
        + string.Concat(_buffer.Select(k => k.ToString()));

    /// <summary>The operator waiting for a motion, or null. Shown in the status bar.</summary>
    public string? PendingOperator => _pendingOperator;

    /// <summary>
    /// The keys held so far, as a sequence rather than as display text.
    /// </summary>
    /// <remarks>
    /// What the window asks the bindings about to show the user their options mid-sequence.
    /// <see cref="PendingKeys"/> is for reading and includes the count and register; this
    /// is the part that can still be completed.
    /// </remarks>
    public KeySequence Buffered => new([.. _buffer]);

    /// <summary>
    /// Names the register the next command should use, as <c>"a</c> does.
    /// </summary>
    /// <remarks>
    /// Set by the handler for <see cref="KeyMode.Register"/>, which reads one raw keystroke
    /// rather than matching it against bindings.
    /// </remarks>
    public void SetRegister(char register) => _pendingRegister = register;

    public event EventHandler<ModeChangedEventArgs>? ModeChanged;

    public void EnterMode(KeyMode mode)
    {
        if (!_modes.IsRegistered(mode))
        {
            throw new ArgumentException(
                $"Mode '{mode}' is not registered. Register it with KeyModeRegistry first.",
                nameof(mode));
        }

        if (mode == Mode)
        {
            return;
        }

        var previous = Mode;

        // Leaving operator-pending any way other than completing a motion abandons the
        // operator. Escape has to actually cancel a half-typed "d", not leave it armed to
        // fire on the next motion typed in normal mode.
        if (previous == KeyMode.Operator && mode != KeyMode.Operator)
        {
            ClearOperator();
        }

        Mode = mode;

        // Any half-typed binding belongs to the mode being left.
        ClearPending();

        ModeChanged?.Invoke(this, new ModeChangedEventArgs { From = previous, To = mode });
    }

    /// <summary>Returns to normal mode, as Escape does from most modes.</summary>
    public void LeaveMode() => EnterMode(KeyMode.Normal);

    /// <summary>Discards any partially-typed binding and count.</summary>
    public void ClearPending()
    {
        _buffer.Clear();
        _count.Clear();
    }

    /// <summary>Abandons a half-typed binding, a pending operator and a named register.</summary>
    public void Reset()
    {
        ClearPending();
        ClearOperator();

        if (Mode != KeyMode.Normal)
        {
            EnterMode(KeyMode.Normal);
        }
    }

    /// <summary>Feeds one key press to the current mode.</summary>
    public KeyResult HandleKey(KeyInfo key)
    {
        // A digit before any binding keys is a repeat count: "3j" moves down three rows.
        // "0" is excluded when the count is empty because it is itself bound, to
        // first-column / start-of-line.
        if (_modes.SupportsCount(Mode) && _buffer.Count == 0 && IsCountDigit(key))
        {
            _count.Append(key.ToString());
            return new KeyResult(KeyDisposition.Consumed);
        }

        // vim's doubling: an operator repeated acts on the current line, so dd trashes the
        // entry under the cursor and 3yy yanks three. Checked before the buffer is touched,
        // because in operator-pending mode the second d is not a binding to look up — d is
        // not bound there at all, and without this it would fall through to "no match" and
        // silently abandon the operator.
        if (Mode == KeyMode.Operator && _buffer.Count == 0 && _pendingOperatorKey == key)
        {
            return ResolveDoubled();
        }

        _buffer.Add(key);
        var typed = new KeySequence(_buffer.ToArray());
        var match = MatchWithFallback(typed);

        if (match.IsExact)
        {
            var count = _count.Length > 0 && int.TryParse(_count.ToString(), out var parsed)
                ? parsed
                : (int?)null;

            // Remembered for the doubling check: Resolve is where an operator is armed,
            // and by then the buffer that produced it has been cleared.
            _armedBy = _buffer.Count == 1 ? _buffer[0] : null;

            ClearPending();
            return Resolve(match.Command!, count);
        }

        if (match.IsPartial)
        {
            // Hold the keys: more may complete a longer binding, e.g. "g" before "gg".
            return new KeyResult(KeyDisposition.Consumed);
        }

        // Nothing matched. Anything buffered was only held on the promise of a longer
        // binding that did not arrive, so it is dropped along with this key.
        var hadPending = _buffer.Count > 1 || _count.Length > 0;
        ClearPending();

        // An operator waiting for a motion that never came must be abandoned too.
        // Leaving it armed means the next motion the user types silently runs it — "dq"
        // then "j" would delete a row when the user expected the "d" to have been
        // discarded along with the "q".
        if (Mode == KeyMode.Operator)
        {
            EnterMode(KeyMode.Normal);
            return new KeyResult(KeyDisposition.Consumed);
        }

        // In text-entry modes an unbound key must reach the focused control or the user
        // cannot type. In normal mode it is swallowed, which is what makes single-letter
        // commands safe.
        if (_modes.ForwardsUnmatchedKeys(Mode) && !hadPending)
        {
            return new KeyResult(KeyDisposition.Forward);
        }

        return new KeyResult(KeyDisposition.Consumed);
    }

    /// <summary>
    /// Decides what a resolved binding actually means in the current mode.
    /// </summary>
    /// <remarks>
    /// Three cases. An operator typed in a selection mode runs at once, because the range
    /// already exists — that is why <c>y</c> in visual mode yanks rather than waiting.
    /// An operator typed in normal mode is stashed and operator-pending mode is entered.
    /// Anything else is an ordinary command, or the motion that completes a stashed
    /// operator.
    /// </remarks>
    private KeyResult Resolve(string command, int? count)
    {
        if (_pendingOperator is { } pending)
        {
            var register = _pendingRegister;
            var operatorCount = _pendingOperatorCount;
            ClearOperator();
            EnterMode(KeyMode.Normal);

            // vim multiplies the two counts: 3d2j covers six rows.
            var effective = Multiply(operatorCount, count);
            return new KeyResult(KeyDisposition.Consumed, command, effective, pending, register);
        }

        if (!_isOperator(FirstWord(command)))
        {
            var named = _pendingRegister;
            _pendingRegister = null;
            return new KeyResult(KeyDisposition.Consumed, command, count, Register: named);
        }

        if (_modes.IsSelectionMode(Mode))
        {
            var register = _pendingRegister;
            _pendingRegister = null;
            return new KeyResult(KeyDisposition.Consumed, null, count, command, register);
        }

        _pendingOperator = command;
        _pendingOperatorCount = count;
        _pendingOperatorKey = _armedBy;
        EnterMode(KeyMode.Operator);
        return new KeyResult(KeyDisposition.Consumed);
    }

    /// <summary>
    /// Combines the counts either side of an operator.
    /// </summary>
    /// <remarks>
    /// Null means "not given", which is not the same as 1: a command with no count at all
    /// must still be able to fall back to its own parameter default.
    /// </remarks>
    private static int? Multiply(int? first, int? second) => (first, second) switch
    {
        (null, null) => null,
        (null, { } b) => b,
        ({ } a, null) => a,
        ({ } a, { } b) => a * b,
    };

    /// <summary>
    /// Matches against the current mode, then anything it falls back to.
    /// </summary>
    /// <remarks>
    /// The specific mode wins, and a partial match in it beats an exact one in the
    /// fallback. That is what makes <c>dd</c> in a scrapbook reach scrap-remove: the first
    /// <c>d</c> is a prefix here and a whole binding in normal mode, and firing the normal
    /// one would make the specialised binding unreachable.
    /// </remarks>
    private BindingMatch MatchWithFallback(KeySequence typed)
    {
        var mode = Mode;
        var partial = false;

        // Bounded rather than trusting the chain to terminate: the fallback comes from
        // configuration eventually, and a mode that falls back to itself would hang the
        // key handler rather than fail.
        for (var depth = 0; depth < 8; depth++)
        {
            var match = _bindings.Match(mode, typed);

            // Checked before the exact, not after: a partial seen in a more specific mode
            // means more keys could still arrive, and firing a whole binding from the
            // fallback now would make the specific one unreachable.
            if (match.IsExact && !partial)
            {
                return match;
            }

            partial |= match.IsPartial;

            if (_modes.FallbackFor(mode) is not { } next || next == mode)
            {
                break;
            }

            mode = next;
        }

        return partial
            ? new BindingMatch(SequenceMatch.PartialMatch, null, null)
            : new BindingMatch(SequenceMatch.NoMatch, null, null);
    }

    private static string FirstWord(string command)
    {
        var end = command.IndexOf(' ', StringComparison.Ordinal);
        return end < 0 ? command : command[..end];
    }

    /// <summary>
    /// Runs a doubled operator over the rows the count covers, starting at the cursor.
    /// </summary>
    /// <remarks>
    /// Expressed as the motion <c>move-down</c> by one less than the count, so the whole
    /// range machinery is reused: the dispatcher runs it, diffs the cursor and gets exactly
    /// the rows <c>3dd</c> should cover. A count of one moves nothing and covers one row.
    /// </remarks>
    private KeyResult ResolveDoubled()
    {
        var second = _count.Length > 0 && int.TryParse(_count.ToString(), out var parsed)
            ? parsed
            : (int?)null;

        var op = _pendingOperator!;
        var register = _pendingRegister;
        var rows = Multiply(_pendingOperatorCount, second) ?? 1;

        ClearPending();
        ClearOperator();
        EnterMode(KeyMode.Normal);

        return new KeyResult(
            KeyDisposition.Consumed, "move-down", Math.Max(0, rows - 1), op, register);
    }

    private void ClearOperator()
    {
        _pendingOperator = null;
        _pendingOperatorCount = null;
        _pendingOperatorKey = null;
        _pendingRegister = null;
    }

    /// <summary>
    /// Whether a key is a count digit. A leading zero is not, because <c>0</c> is
    /// itself bound.
    /// </summary>
    private bool IsCountDigit(KeyInfo key)
    {
        var text = key.ToString();
        if (text.Length != 1 || !char.IsDigit(text[0]))
        {
            return false;
        }

        return text[0] != '0' || _count.Length > 0;
    }
}
