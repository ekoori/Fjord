// Vendored from QuteSharp (src/QuteSharp.Core/Keys/Key.cs). See VENDORED.md.
namespace Fjord.Core.Keys;

/// <summary>
/// Key codes, numerically identical to <c>Qt::Key</c>.
/// </summary>
/// <remarks>
/// The values match Qt's because this table came from qutebrowser by way of QuteSharp,
/// and keeping the numbering means the key-parsing tests port over unchanged.
/// <para>
/// Printable ASCII maps to its own codepoint, which is what <see cref="KeyInfo"/>
/// relies on to distinguish "normal" from "special" bindings.
/// </para>
/// </remarks>
public enum Key
{
    Nil = 0x0,

    // --- Printable ASCII: value == codepoint ---
    Space = 0x20,
    Exclam = 0x21,
    QuoteDbl = 0x22,
    NumberSign = 0x23,
    Dollar = 0x24,
    Percent = 0x25,
    Ampersand = 0x26,
    Apostrophe = 0x27,
    ParenLeft = 0x28,
    ParenRight = 0x29,
    Asterisk = 0x2a,
    Plus = 0x2b,
    Comma = 0x2c,
    Minus = 0x2d,
    Period = 0x2e,
    Slash = 0x2f,
    Key0 = 0x30,
    Key1 = 0x31,
    Key2 = 0x32,
    Key3 = 0x33,
    Key4 = 0x34,
    Key5 = 0x35,
    Key6 = 0x36,
    Key7 = 0x37,
    Key8 = 0x38,
    Key9 = 0x39,
    Colon = 0x3a,
    Semicolon = 0x3b,
    Less = 0x3c,
    Equal = 0x3d,
    Greater = 0x3e,
    Question = 0x3f,
    At = 0x40,
    A = 0x41,
    B = 0x42,
    C = 0x43,
    D = 0x44,
    E = 0x45,
    F = 0x46,
    G = 0x47,
    H = 0x48,
    I = 0x49,
    J = 0x4a,
    K = 0x4b,
    L = 0x4c,
    M = 0x4d,
    N = 0x4e,
    O = 0x4f,
    P = 0x50,
    Q = 0x51,
    R = 0x52,
    S = 0x53,
    T = 0x54,
    U = 0x55,
    V = 0x56,
    W = 0x57,
    X = 0x58,
    Y = 0x59,
    Z = 0x5a,
    BracketLeft = 0x5b,
    Backslash = 0x5c,
    BracketRight = 0x5d,
    AsciiCircum = 0x5e,
    Underscore = 0x5f,
    QuoteLeft = 0x60,
    BraceLeft = 0x7b,
    Bar = 0x7c,
    BraceRight = 0x7d,
    AsciiTilde = 0x7e,

    // --- Named keys ---
    Escape = 0x01000000,
    Tab = 0x01000001,
    Backtab = 0x01000002,
    Backspace = 0x01000003,
    Return = 0x01000004,
    Enter = 0x01000005,
    Insert = 0x01000006,
    Delete = 0x01000007,
    Pause = 0x01000008,
    Print = 0x01000009,
    SysReq = 0x0100000a,
    Clear = 0x0100000b,
    Home = 0x01000010,
    End = 0x01000011,
    Left = 0x01000012,
    Up = 0x01000013,
    Right = 0x01000014,
    Down = 0x01000015,
    PageUp = 0x01000016,
    PageDown = 0x01000017,

    // --- Modifier keys (pressed on their own) ---
    Shift = 0x01000020,
    Control = 0x01000021,
    Meta = 0x01000022,
    Alt = 0x01000023,
    CapsLock = 0x01000024,
    NumLock = 0x01000025,
    ScrollLock = 0x01000026,

    F1 = 0x01000030,
    F2 = 0x01000031,
    F3 = 0x01000032,
    F4 = 0x01000033,
    F5 = 0x01000034,
    F6 = 0x01000035,
    F7 = 0x01000036,
    F8 = 0x01000037,
    F9 = 0x01000038,
    F10 = 0x01000039,
    F11 = 0x0100003a,
    F12 = 0x0100003b,

    SuperL = 0x01000053,
    SuperR = 0x01000054,
    Menu = 0x01000055,
    HyperL = 0x01000056,
    HyperR = 0x01000057,
    Help = 0x01000058,
    DirectionL = 0x01000059,
    DirectionR = 0x01000060,

    AltGr = 0x01001103,
    MultiKey = 0x01001120,
    SingleCandidate = 0x0100113c,
    ModeSwitch = 0x0100117e,

    // Mice with side buttons report these. Binding <back>/<forward> to the navigation
    // history is the one place a file manager wants them.
    Back = 0x01000061,
    Forward = 0x01000062,
    Stop = 0x01000063,
    Refresh = 0x01000064,

    Unknown = 0x01ffffff,
}

/// <summary>
/// Keyboard modifiers, numerically identical to <c>Qt::KeyboardModifier</c>.
/// </summary>
[Flags]
public enum KeyModifiers
{
    None = 0x00000000,
    Shift = 0x02000000,
    Control = 0x04000000,
    Alt = 0x08000000,
    Meta = 0x10000000,
    Keypad = 0x20000000,
    GroupSwitch = 0x40000000,

    /// <summary>Mask covering every modifier bit; used to assert keys and modifiers never mix.</summary>
    Mask = unchecked((int)0x7e000000),
}
