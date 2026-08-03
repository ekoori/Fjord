// Vendored from QuteSharp (src/QuteSharp.Core/Keys/KeyInfo.cs). See VENDORED.md.
using System.Text;

namespace Fjord.Core.Keys;

/// <summary>Thrown when a keystring cannot be parsed.</summary>
public sealed class KeyParseException : Exception
{
    public KeyParseException(string? keystr, string error)
        : base(keystr is null
            ? $"Could not parse keystring: {error}"
            : $"Could not parse '{keystr}': {error}")
    {
        KeyString = keystr;
    }

    public string? KeyString { get; }
}

/// <summary>
/// A single key with optional modifiers.
/// </summary>
/// <remarks>
/// <see cref="ToString"/> is the canonical serialisation used in config files and in
/// the status bar: bare characters for plain keys (<c>a</c>, <c>A</c>) and
/// angle-bracket syntax otherwise (<c>&lt;Ctrl+a&gt;</c>).
/// </remarks>
public readonly record struct KeyInfo(Key Key, KeyModifiers Modifiers = KeyModifiers.None)
    : IComparable<KeyInfo>
{
    /// <summary>Maps a modifier key pressed on its own to the modifier bit it sets.</summary>
    private static readonly Dictionary<Key, KeyModifiers> ModifierMap = new()
    {
        [Key.Shift] = KeyModifiers.Shift,
        [Key.Control] = KeyModifiers.Control,
        [Key.Alt] = KeyModifiers.Alt,
        [Key.Meta] = KeyModifiers.Meta,
        [Key.AltGr] = KeyModifiers.GroupSwitch,
        [Key.ModeSwitch] = KeyModifiers.GroupSwitch,
    };

    /// <summary>
    /// Keys Qt's <c>QKeySequence::toString</c> renders oddly, plus the modifier keys
    /// themselves. Dead/combining keys map to their non-combining form because that is
    /// what users can actually type into a config.
    /// </summary>
    private static readonly Dictionary<Key, string> SpecialNames = new()
    {
        [Key.SuperL] = "Super L",
        [Key.SuperR] = "Super R",
        [Key.HyperL] = "Hyper L",
        [Key.HyperR] = "Hyper R",
        [Key.DirectionL] = "Direction L",
        [Key.DirectionR] = "Direction R",
        [Key.Shift] = "Shift",
        [Key.Control] = "Control",
        [Key.Meta] = "Meta",
        [Key.Alt] = "Alt",
        [Key.AltGr] = "AltGr",
        [Key.MultiKey] = "Multi key",
        [Key.SingleCandidate] = "Single Candidate",
        [Key.ModeSwitch] = "Mode switch",
    };

    /// <summary>Names for non-printable keys.</summary>
    private static readonly Dictionary<Key, string> KeyNames = new()
    {
        [Key.Space] = "Space",
        [Key.Escape] = "Escape",
        [Key.Tab] = "Tab",
        [Key.Backtab] = "Backtab",
        [Key.Backspace] = "Backspace",
        [Key.Return] = "Return",
        [Key.Enter] = "Enter",
        [Key.Insert] = "Ins",
        [Key.Delete] = "Del",
        [Key.Pause] = "Pause",
        [Key.Print] = "Print",
        [Key.SysReq] = "SysReq",
        [Key.Clear] = "Clear",
        [Key.Home] = "Home",
        [Key.End] = "End",
        [Key.Left] = "Left",
        [Key.Up] = "Up",
        [Key.Right] = "Right",
        [Key.Down] = "Down",
        [Key.PageUp] = "PgUp",
        [Key.PageDown] = "PgDown",
        [Key.CapsLock] = "CapsLock",
        [Key.NumLock] = "NumLock",
        [Key.ScrollLock] = "ScrollLock",
        [Key.Menu] = "Menu",
        [Key.Help] = "Help",

        [Key.Back] = "Back",
        [Key.Forward] = "Forward",
        [Key.Stop] = "Stop",
        [Key.Refresh] = "Refresh",

        [Key.F1] = "F1",
        [Key.F2] = "F2",
        [Key.F3] = "F3",
        [Key.F4] = "F4",
        [Key.F5] = "F5",
        [Key.F6] = "F6",
        [Key.F7] = "F7",
        [Key.F8] = "F8",
        [Key.F9] = "F9",
        [Key.F10] = "F10",
        [Key.F11] = "F11",
        [Key.F12] = "F12",
    };

    /// <summary>Reverse of <see cref="KeyNames"/>/<see cref="SpecialNames"/>, case-insensitive.</summary>
    private static readonly Dictionary<string, Key> NamesToKey = BuildNameLookup();

    private static Dictionary<string, Key> BuildNameLookup()
    {
        var lookup = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, name) in KeyNames)
        {
            lookup[name] = key;
        }

        foreach (var (key, name) in SpecialNames)
        {
            lookup.TryAdd(name, key);
        }

        // Aliases users are likely to type that Qt renders differently.
        lookup["Escape"] = Key.Escape;
        lookup["Esc"] = Key.Escape;
        lookup["Insert"] = Key.Insert;
        lookup["Delete"] = Key.Delete;
        lookup["PgUp"] = Key.PageUp;
        lookup["PgDown"] = Key.PageDown;
        lookup["PageUp"] = Key.PageUp;
        lookup["PageDown"] = Key.PageDown;
        lookup["Ctrl"] = Key.Control;
        lookup["Super"] = Key.SuperL;
        lookup["Hyper"] = Key.HyperL;
        return lookup;
    }

    /// <summary>A printable key is one that renders as a single character; Space is excluded.</summary>
    internal static bool IsPrintable(Key key) =>
        (int)key <= 0xff && key != Key.Space && key != Key.Nil;

    /// <summary>True when this must be written with angle-bracket syntax.</summary>
    public bool IsSpecial => !IsPrintable(Key) || Modifiers != KeyModifiers.None;

    /// <summary>True when this is a modifier key pressed on its own.</summary>
    public bool IsModifierKey => ModifierMap.ContainsKey(Key);

    /// <summary>Removes the given modifier bits.</summary>
    public KeyInfo WithStrippedModifiers(KeyModifiers modifiers) =>
        this with { Modifiers = Modifiers & ~modifiers };

    /// <summary>
    /// Canonical string form. Plain printable keys render bare (<c>a</c>, <c>A</c>);
    /// everything else uses <c>&lt;...&gt;</c>.
    /// </summary>
    public override string ToString()
    {
        var keyString = KeyToString(Key);
        var modifiers = Modifiers;

        if (ModifierMap.TryGetValue(Key, out var ownBit))
        {
            // Don't render e.g. <Shift+Shift>.
            modifiers &= ~ownBit;
        }
        else if (IsPrintable(Key))
        {
            if (modifiers == KeyModifiers.Shift)
            {
                return keyString.ToUpperInvariant();
            }

            if (modifiers == KeyModifiers.None)
            {
                return keyString.ToLowerInvariant();
            }

            // Special syntax, but <Ctrl+a> rather than <Ctrl+A>.
            keyString = keyString.ToLowerInvariant();
        }

        return $"<{ModifiersToString(modifiers)}{keyString}>";
    }

    internal static string KeyToString(Key key)
    {
        if (SpecialNames.TryGetValue(key, out var special))
        {
            return special;
        }

        if (KeyNames.TryGetValue(key, out var named))
        {
            return named;
        }

        if (IsPrintable(key))
        {
            // Printable keys render as their uppercase character.
            return char.ToUpperInvariant((char)key).ToString();
        }

        return $"<{(int)key:x}>";
    }

    /// <summary>
    /// Modifier order matches Qt's <c>QKeySequence::encodeString</c>: Meta, Ctrl, Alt,
    /// Shift, Num — with AltGr prefixed, since Qt doesn't treat GroupSwitch as a modifier.
    /// </summary>
    internal static string ModifiersToString(KeyModifiers modifiers)
    {
        var sb = new StringBuilder();

        if (modifiers.HasFlag(KeyModifiers.GroupSwitch))
        {
            sb.Append("AltGr+");
            modifiers &= ~KeyModifiers.GroupSwitch;
        }

        if (modifiers.HasFlag(KeyModifiers.Meta))
        {
            sb.Append("Meta+");
        }

        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            sb.Append("Ctrl+");
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            sb.Append("Alt+");
        }

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            sb.Append("Shift+");
        }

        if (modifiers.HasFlag(KeyModifiers.Keypad))
        {
            sb.Append("Num+");
        }

        return sb.ToString();
    }

    /// <summary>Resolves a key name such as <c>Escape</c> or <c>a</c> to a <see cref="Key"/>.</summary>
    internal static bool TryParseKeyName(string name, out Key key)
    {
        if (NamesToKey.TryGetValue(name, out key))
        {
            return true;
        }

        if (name.Length == 1)
        {
            key = (Key)char.ToUpperInvariant(name[0]);
            return IsPrintable(key) || key == Key.Space;
        }

        key = Key.Nil;
        return false;
    }

    /// <summary>The text that would be inserted by pressing this key.</summary>
    public string Text() => Key switch
    {
        Key.Space => " ",
        Key.Tab => "\t",
        Key.Backspace => "\b",
        Key.Return or Key.Enter => "\r",
        Key.Escape => "\x1b",
        _ when !IsPrintable(Key) => string.Empty,
        _ => Modifiers.HasFlag(KeyModifiers.Shift)
            ? char.ToUpperInvariant((char)Key).ToString()
            : char.ToLowerInvariant((char)Key).ToString(),
    };

    public int CompareTo(KeyInfo other)
    {
        var byKey = ((int)Key).CompareTo((int)other.Key);
        return byKey != 0 ? byKey : ((int)Modifiers).CompareTo((int)other.Modifiers);
    }

    public static bool operator <(KeyInfo left, KeyInfo right) => left.CompareTo(right) < 0;
    public static bool operator >(KeyInfo left, KeyInfo right) => left.CompareTo(right) > 0;
    public static bool operator <=(KeyInfo left, KeyInfo right) => left.CompareTo(right) <= 0;
    public static bool operator >=(KeyInfo left, KeyInfo right) => left.CompareTo(right) >= 0;
}
