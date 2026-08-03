// Vendored from QuteSharp (src/QuteSharp.App/Input/AvaloniaKeys.cs). See VENDORED.md.
using Avalonia.Input;
using Fjord.Core.Keys;
using CoreKey = Fjord.Core.Keys.Key;
using CoreModifiers = Fjord.Core.Keys.KeyModifiers;

namespace Fjord.App.Input;

/// <summary>
/// Translates Avalonia key events into <see cref="KeyInfo"/>.
/// </summary>
/// <remarks>
/// The key model is Qt's, because the binding syntax and the whole <c>KeySequence</c>
/// parser came from qutebrowser by way of QuteSharp. Avalonia has its own enum with
/// unrelated numeric values, so the two have to be mapped rather than cast.
/// </remarks>
internal static class AvaloniaKeys
{
    private static readonly Dictionary<Avalonia.Input.Key, CoreKey> Map = new()
    {
        [Avalonia.Input.Key.Escape] = CoreKey.Escape,
        [Avalonia.Input.Key.Tab] = CoreKey.Tab,
        [Avalonia.Input.Key.Back] = CoreKey.Backspace,
        [Avalonia.Input.Key.Return] = CoreKey.Return,
        [Avalonia.Input.Key.Enter] = CoreKey.Return,
        [Avalonia.Input.Key.Insert] = CoreKey.Insert,
        [Avalonia.Input.Key.Delete] = CoreKey.Delete,
        [Avalonia.Input.Key.Pause] = CoreKey.Pause,
        [Avalonia.Input.Key.Home] = CoreKey.Home,
        [Avalonia.Input.Key.End] = CoreKey.End,
        [Avalonia.Input.Key.Left] = CoreKey.Left,
        [Avalonia.Input.Key.Up] = CoreKey.Up,
        [Avalonia.Input.Key.Right] = CoreKey.Right,
        [Avalonia.Input.Key.Down] = CoreKey.Down,
        [Avalonia.Input.Key.PageUp] = CoreKey.PageUp,
        [Avalonia.Input.Key.PageDown] = CoreKey.PageDown,
        [Avalonia.Input.Key.Space] = CoreKey.Space,
        [Avalonia.Input.Key.CapsLock] = CoreKey.CapsLock,
        [Avalonia.Input.Key.NumLock] = CoreKey.NumLock,
        [Avalonia.Input.Key.Scroll] = CoreKey.ScrollLock,

        // Mice with side buttons report these; binding them to navigation history is the
        // one place a file manager wants them.
        [Avalonia.Input.Key.BrowserBack] = CoreKey.Back,
        [Avalonia.Input.Key.BrowserForward] = CoreKey.Forward,
        [Avalonia.Input.Key.BrowserStop] = CoreKey.Stop,
        [Avalonia.Input.Key.BrowserRefresh] = CoreKey.Refresh,

        [Avalonia.Input.Key.F1] = CoreKey.F1,
        [Avalonia.Input.Key.F2] = CoreKey.F2,
        [Avalonia.Input.Key.F3] = CoreKey.F3,
        [Avalonia.Input.Key.F4] = CoreKey.F4,
        [Avalonia.Input.Key.F5] = CoreKey.F5,
        [Avalonia.Input.Key.F6] = CoreKey.F6,
        [Avalonia.Input.Key.F7] = CoreKey.F7,
        [Avalonia.Input.Key.F8] = CoreKey.F8,
        [Avalonia.Input.Key.F9] = CoreKey.F9,
        [Avalonia.Input.Key.F10] = CoreKey.F10,
        [Avalonia.Input.Key.F11] = CoreKey.F11,
        [Avalonia.Input.Key.F12] = CoreKey.F12,
    };

    /// <summary>
    /// Converts a key event, or returns null for a key that carries no meaning on its own.
    /// </summary>
    /// <remarks>
    /// A bare modifier press is filtered out: holding Ctrl before pressing another key
    /// would otherwise register as a keystroke and break every multi-key binding.
    /// </remarks>
    public static KeyInfo? Translate(KeyEventArgs e)
    {
        if (IsModifierKey(e.Key))
        {
            return null;
        }

        var modifiers = Convert(e.KeyModifiers);

        if (Map.TryGetValue(e.Key, out var mapped))
        {
            return new KeyInfo(mapped, modifiers);
        }

        // Letters and digits: Avalonia's A-Z and D0-D9 line up with ASCII, which is what
        // the Qt-style values use for printable keys.
        if (e.Key >= Avalonia.Input.Key.A && e.Key <= Avalonia.Input.Key.Z)
        {
            var letter = (char)('A' + (e.Key - Avalonia.Input.Key.A));

            // Without Shift the binding is the lowercase letter; "a" and "A" are stored as
            // different bindings.
            return new KeyInfo((CoreKey)char.ToUpperInvariant(letter), modifiers);
        }

        if (e.Key >= Avalonia.Input.Key.D0 && e.Key <= Avalonia.Input.Key.D9)
        {
            return new KeyInfo((CoreKey)('0' + (e.Key - Avalonia.Input.Key.D0)), modifiers);
        }

        if (e.Key >= Avalonia.Input.Key.NumPad0 && e.Key <= Avalonia.Input.Key.NumPad9)
        {
            return new KeyInfo(
                (CoreKey)('0' + (e.Key - Avalonia.Input.Key.NumPad0)),
                modifiers | CoreModifiers.Keypad);
        }

        // Punctuation and symbols. Avalonia names these by physical key (OemPeriod,
        // OemSemicolon, ...) which says nothing about the character produced — that
        // depends on layout and modifiers. KeySymbol carries the actual character, which
        // is what bindings are written in terms of.
        //
        // This matters more than it looks: ":" is Shift+OemSemicolon on a US layout, so
        // without this the command line cannot be opened at all.
        var symbol = e.KeySymbol;
        if (!string.IsNullOrEmpty(symbol) && symbol.Length == 1 && !char.IsControl(symbol[0]))
        {
            // Shift is already expressed by the character itself (":" rather than ";"), so
            // carrying the flag too would fail to match the binding.
            return new KeyInfo(
                (CoreKey)char.ToUpperInvariant(symbol[0]),
                modifiers & ~CoreModifiers.Shift);
        }

        return null;
    }

    /// <summary>
    /// The character a key event would insert, for the command line where the text matters
    /// rather than the binding.
    /// </summary>
    public static string? TextOf(KeyEventArgs e) =>
        string.IsNullOrEmpty(e.KeySymbol) ? null : e.KeySymbol;

    private static bool IsModifierKey(Avalonia.Input.Key key) => key
        is Avalonia.Input.Key.LeftCtrl or Avalonia.Input.Key.RightCtrl
        or Avalonia.Input.Key.LeftShift or Avalonia.Input.Key.RightShift
        or Avalonia.Input.Key.LeftAlt or Avalonia.Input.Key.RightAlt
        or Avalonia.Input.Key.LWin or Avalonia.Input.Key.RWin;

    private static CoreModifiers Convert(Avalonia.Input.KeyModifiers modifiers)
    {
        var result = CoreModifiers.None;

        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift))
        {
            result |= CoreModifiers.Shift;
        }

        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))
        {
            result |= CoreModifiers.Control;
        }

        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt))
        {
            result |= CoreModifiers.Alt;
        }

        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta))
        {
            result |= CoreModifiers.Meta;
        }

        return result;
    }
}
