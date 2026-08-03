namespace Fjord.Core.Input;

/// <summary>
/// An input mode, identified by name.
/// </summary>
/// <remarks>
/// QuteSharp models this as a closed enum with <c>switch</c> expressions over it for
/// the per-mode policy questions. Fjord needs Visual and operator-pending immediately
/// and will let plugins add modes later, so the identity is a string and the policy
/// lives in <see cref="KeyModeRegistry"/> as data.
/// <para>
/// Comparison is ordinal. <c>default(KeyMode)</c> reads as <see cref="Normal"/> rather
/// than carrying a null name, so it is safe as a dictionary key.
/// </para>
/// </remarks>
public readonly record struct KeyMode : IComparable<KeyMode>
{
    /// <summary>Motions, operators and commands. The mode the app sits in.</summary>
    public static readonly KeyMode Normal = new("normal");

    /// <summary>Selection anchored at a row; motions extend the highlight.</summary>
    public static readonly KeyMode Visual = new("visual");

    /// <summary>An operator has been typed and is waiting for its motion.</summary>
    public static readonly KeyMode Operator = new("operator-pending");

    /// <summary>The <c>:</c> command line has focus.</summary>
    public static readonly KeyMode Command = new("command");

    /// <summary>The <c>/</c> incremental search line has focus.</summary>
    public static readonly KeyMode Search = new("search");

    /// <summary>Rename-in-place: the row is an editable text field.</summary>
    public static readonly KeyMode Insert = new("insert");

    /// <summary>A modal prompt is showing and owns the keyboard.</summary>
    public static readonly KeyMode Prompt = new("prompt");

    /// <summary>A yes/no confirmation is showing.</summary>
    public static readonly KeyMode YesNo = new("yesno");

    /// <summary>The next keystroke names a register.</summary>
    public static readonly KeyMode Register = new("register");

    /// <summary>The next keystroke names a mark.</summary>
    public static readonly KeyMode Mark = new("mark");

    private readonly string? _name;

    public KeyMode(string name) =>
        _name = name ?? throw new ArgumentNullException(nameof(name));

    /// <summary>Never null. A default-constructed value reads as <see cref="Normal"/>.</summary>
    public string Name => _name ?? "normal";

    public override string ToString() => Name;

    public bool Equals(KeyMode other) => string.Equals(Name, other.Name, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name);

    public int CompareTo(KeyMode other) => string.CompareOrdinal(Name, other.Name);
}

/// <summary>
/// The per-mode policy questions the input pipeline asks.
/// </summary>
/// <param name="ForwardsUnmatchedKeys">
/// Whether a key that matches no binding reaches the focused control. True for modes
/// where the user is typing text — without it a command line cannot be typed into.
/// False in normal mode, which is what makes single-letter commands safe.
/// </param>
/// <param name="SupportsCount">Whether a leading digit run is read as a repeat count.</param>
/// <param name="IsSelectionMode">
/// Whether a range is already selected, so an operator runs immediately instead of
/// waiting for a motion.
/// </param>
public sealed record KeyModeTraits(
    bool ForwardsUnmatchedKeys = false,
    bool SupportsCount = false,
    bool IsSelectionMode = false);

/// <summary>
/// Knows every mode that exists and what each one does with a key.
/// </summary>
public sealed class KeyModeRegistry
{
    private static readonly KeyModeTraits Conservative = new();

    private readonly Dictionary<KeyMode, KeyModeTraits> _traits = [];

    public KeyModeRegistry()
    {
        Register(KeyMode.Normal, new KeyModeTraits(SupportsCount: true));
        Register(KeyMode.Visual, new KeyModeTraits(SupportsCount: true, IsSelectionMode: true));
        Register(KeyMode.Operator, new KeyModeTraits(SupportsCount: true));

        // Text-entry modes: an unbound key is a character the user meant to type.
        Register(KeyMode.Command, new KeyModeTraits(ForwardsUnmatchedKeys: true));
        Register(KeyMode.Search, new KeyModeTraits(ForwardsUnmatchedKeys: true));
        Register(KeyMode.Insert, new KeyModeTraits(ForwardsUnmatchedKeys: true));
        Register(KeyMode.Prompt, new KeyModeTraits(ForwardsUnmatchedKeys: true));

        // "The next keystroke is data" modes. They swallow everything; the handler that
        // put the app into them reads the raw key itself.
        Register(KeyMode.YesNo, Conservative);
        Register(KeyMode.Register, Conservative);
        Register(KeyMode.Mark, Conservative);
    }

    public IReadOnlyCollection<KeyMode> Modes => _traits.Keys;

    /// <summary>Adds a mode, or replaces the traits of one that already exists.</summary>
    public KeyMode Register(KeyMode mode, KeyModeTraits traits)
    {
        _traits[mode] = traits ?? throw new ArgumentNullException(nameof(traits));
        return mode;
    }

    /// <summary>Adds a mode by name.</summary>
    public KeyMode Register(string name, KeyModeTraits traits) => Register(new KeyMode(name), traits);

    public bool IsRegistered(KeyMode mode) => _traits.ContainsKey(mode);

    /// <summary>Resolves a mode name from config, e.g. the keys of the <c>bindings</c> map.</summary>
    public bool TryParse(string name, out KeyMode mode)
    {
        mode = new KeyMode(name);
        return _traits.ContainsKey(mode);
    }

    public bool ForwardsUnmatchedKeys(KeyMode mode) => Traits(mode).ForwardsUnmatchedKeys;

    public bool SupportsCount(KeyMode mode) => Traits(mode).SupportsCount;

    public bool IsSelectionMode(KeyMode mode) => Traits(mode).IsSelectionMode;

    /// <summary>
    /// Unknown modes answer conservatively — swallow the key, no count — rather than
    /// throwing. Entering an unregistered mode is the actual bug, and
    /// <see cref="ModeManager.EnterMode"/> rejects that at the point it happens.
    /// </summary>
    private KeyModeTraits Traits(KeyMode mode) =>
        _traits.TryGetValue(mode, out var traits) ? traits : Conservative;
}
