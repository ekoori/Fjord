using Fjord.Core.FileSystem;

namespace Fjord.Config;

/// <summary>
/// The user's settings, as deserialized from <c>config.yaml</c>.
/// </summary>
/// <remarks>
/// Plain mutable classes rather than records: YamlDotNet binds settable properties, and
/// every property needs a default so a partial config file still produces a usable object.
/// <para>
/// Every public property here must appear in <see cref="SettingsSchema"/>; a test enforces
/// it, so a new setting cannot ship undocumented.
/// </para>
/// </remarks>
public sealed class FjordConfig
{
    public AppearanceConfig Appearance { get; set; } = new();

    public ListingConfig Listing { get; set; } = new();

    public LoggingConfig Logging { get; set; } = new();

    /// <summary>
    /// Named views of a directory, cycled through by <c>:sort</c>.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Listing"/>, which is the one in effect at startup. These
    /// are the ones you can return to by name.
    /// </remarks>
    public Dictionary<string, ListingPreset> Listings { get; set; } = [];

    public BehaviourConfig Behaviour { get; set; } = new();

    /// <summary>
    /// Key bindings, as <c>mode -&gt; keys -&gt; command</c>.
    /// </summary>
    /// <remarks>
    /// The nested type is <c>string</c> on purpose. YAML 1.1 reads bare <c>n</c>, <c>y</c>,
    /// <c>on</c> and <c>off</c> as booleans, and in a vim-keyed file manager <c>n</c> and
    /// <c>y</c> are core bindings. Because the key type is <c>string</c>, YamlDotNet takes
    /// each key's scalar text and never applies that inference — loosening this to an
    /// untyped map would silently turn <c>n:</c> into <c>false</c> and lose the binding.
    /// <para>
    /// Generated files still quote every key (see <c>SettingsSchema.QuoteKey</c>): keys
    /// like <c>:</c>, <c>#</c> and <c>&lt;Ctrl+a&gt;</c> need it to be valid YAML at all,
    /// and other tools reading the file do apply boolean inference.
    /// </para>
    /// </remarks>
    public Dictionary<string, Dictionary<string, string>> Bindings { get; set; } = [];

    /// <summary>Derives the sort the listing config describes.</summary>
    public SortSpec ToSortSpec() => new(
        Listing.SortKey,
        Listing.SortDescending,
        Listing.DirectoriesFirst,
        Listing.NaturalSort);

    public FilterSpec ToFilterSpec() => new(Listing.ShowHidden);
}

public sealed class AppearanceConfig
{
    public string FontFamily { get; set; } = "Inter, $Default";

    public double FontSize { get; set; } = 13;

    /// <summary>Vertical padding above and below a row's text, in DIPs.</summary>
    public double RowPadding { get; set; } = 4;

    public ColorsConfig Colors { get; set; } = new();
}

/// <summary>
/// Colours as <c>#RRGGBB</c> strings.
/// </summary>
/// <remarks>
/// Strings rather than a parsed colour type so <c>Fjord.Config</c> stays free of any UI
/// dependency; the App layer parses them.
/// </remarks>
public sealed class ColorsConfig
{
    public string Background { get; set; } = "#1B1D21";

    public string Stripe { get; set; } = "#1F2226";

    public string Selected { get; set; } = "#2C3A4E";

    public string Visual { get; set; } = "#3A334E";

    public string Cursor { get; set; } = "#2A2F37";

    public string CursorBar { get; set; } = "#6E9ED8";

    public string File { get; set; } = "#D4D7DD";

    public string Directory { get; set; } = "#8AB4F8";

    public string Broken { get; set; } = "#E07B7B";

    public string Muted { get; set; } = "#7E8692";
}

/// <summary>How Fjord acts, as opposed to how it looks.</summary>
public sealed class BehaviourConfig
{
    /// <summary>Whether trashing asks first.</summary>
    public bool ConfirmTrash { get; set; } = true;
}

/// <summary>
/// What gets written to the log file, and how much of it is kept.
/// </summary>
/// <remarks>
/// Read once at startup. Changing the level is one of the few settings a reload cannot
/// apply, because the provider owns an open file handle.
/// </remarks>
public sealed class LoggingConfig
{
    /// <summary>Minimum level recorded. <c>off</c> writes no file at all.</summary>
    public string Level { get; set; } = "information";

    /// <summary>Generations kept, including the current file.</summary>
    public int MaxFiles { get; set; } = 3;
}

public sealed class ListingConfig
{
    public SortKey SortKey { get; set; } = SortKey.Name;

    public bool SortDescending { get; set; }

    public bool DirectoriesFirst { get; set; } = true;

    /// <summary>Whether digit runs in names compare as numbers, so <c>file2</c> precedes <c>file10</c>.</summary>
    public bool NaturalSort { get; set; } = true;

    public bool ShowHidden { get; set; }
}
