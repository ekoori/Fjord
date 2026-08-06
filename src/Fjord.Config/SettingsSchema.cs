using System.Globalization;
using System.Text;

namespace Fjord.Config;

/// <summary>
/// One setting: where it lives, what it accepts, its default and what it does.
/// </summary>
/// <param name="Name">Dotted path matching the YAML structure, e.g. <c>appearance.font_size</c>.</param>
/// <param name="Type">Human-readable type, shown in the generated file and by <c>:help</c>.</param>
/// <param name="Default">The default already rendered as a YAML scalar.</param>
public sealed record SettingDefinition(string Name, string Type, string Default, string Doc);

/// <summary>
/// The catalogue of settings, and the source of the shipped <c>config.yaml</c>.
/// </summary>
/// <remarks>
/// noPDF keeps a hand-written doc table parallel to its implementation, with nothing
/// enforcing that the two agree. Here the file is <i>generated</i> from this table, and a
/// test asserts every public property on <see cref="FjordConfig"/> appears in it — so a
/// setting cannot ship without documentation, and documentation cannot describe a setting
/// that no longer exists.
/// </remarks>
public static class SettingsSchema
{
    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        new("appearance.font_family", "string", "\"Inter, $Default\"",
            "Font used for the list and chrome. $Default falls back to the system UI font."),
        new("appearance.font_size", "number", "13",
            "Font size in DIPs. Row height is derived from this and the font's own metrics."),
        new("appearance.row_padding", "number", "4",
            "Padding above and below a row's text, in DIPs."),

        new("appearance.colors.background", "#RRGGBB", "\"#1B1D21\"", "List background."),
        new("appearance.colors.stripe", "#RRGGBB", "\"#1F2226\"", "Background of alternating rows."),
        new("appearance.colors.selected", "#RRGGBB", "\"#2C3A4E\"", "Background of committed selection."),
        new("appearance.colors.visual", "#RRGGBB", "\"#3A334E\"",
            "Background of the pending range in visual mode, before an operator commits it."),
        new("appearance.colors.cursor", "#RRGGBB", "\"#2A2F37\"", "Background of the cursor row."),
        new("appearance.colors.cursor_bar", "#RRGGBB", "\"#6E9ED8\"", "Accent bar marking the cursor row."),
        new("appearance.colors.file", "#RRGGBB", "\"#D4D7DD\"", "File names."),
        new("appearance.colors.directory", "#RRGGBB", "\"#8AB4F8\"", "Directory names."),
        new("appearance.colors.broken", "#RRGGBB", "\"#E07B7B\"", "Symlinks whose target cannot be resolved."),
        new("appearance.colors.muted", "#RRGGBB", "\"#7E8692\"", "Size and date columns."),

        new("listing.sort_key", "name | size | modified | extension", "name",
            "What to order by. Sorting by size or modified costs one stat per entry on Unix; "
            + "sorting by name does not."),
        new("listing.sort_descending", "true | false", "false", "Reverse the sort order."),
        new("listing.directories_first", "true | false", "true",
            "Group directories above files. Applied before the sort key, so descending order "
            + "does not push directories to the bottom."),
        new("listing.natural_sort", "true | false", "true",
            "Compare digit runs as numbers, so file2 sorts before file10."),
        new("listing.show_hidden", "true | false", "false",
            "Show dotfiles and entries carrying the hidden attribute."),

        new("logging.level", "off | error | warning | information | debug | trace", "information",
            "How much is written to the log file. Read at startup only, and off writes no "
            + "file at all. Run :config-path to find the directory."),
        new("logging.max_files", "number", "3",
            "Log generations kept, including the current file. Each rolls at 1 MB."),

        new("bindings", "mode -> keys -> command", "{}",
            "Key bindings, layered over the defaults. Every key MUST be quoted: YAML reads "
            + "bare n, y, on, off, yes and no as booleans."),
    ];

    public static SettingDefinition? Find(string name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// Checks a value against a setting's declared type and renders it as a YAML scalar.
    /// </summary>
    /// <param name="rendered">The value ready to write, quoted where YAML needs it.</param>
    /// <returns>Why the value was refused, or null when it is good.</returns>
    /// <remarks>
    /// Validating here rather than after a reload is what makes <c>:set</c> safe to use: a
    /// rejected value never reaches the file, so a typo cannot leave the user with a config
    /// that no longer parses and an app running on defaults.
    /// </remarks>
    public static string? Validate(SettingDefinition setting, string value, out string rendered)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentNullException.ThrowIfNull(value);

        rendered = string.Empty;
        var type = setting.Type;

        // The one setting that is a nested map rather than a scalar. Rewriting it needs a
        // structural edit, which SetScalar deliberately cannot do.
        if (type.Contains("->", StringComparison.Ordinal))
        {
            return $"{setting.Name} is edited in the file, not with :set";
        }

        if (type.Contains('|', StringComparison.Ordinal))
        {
            var allowed = type.Split('|').Select(o => o.Trim()).ToList();

            if (!allowed.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                return $"{setting.Name} takes {type}";
            }

            rendered = value.ToLowerInvariant();
            return null;
        }

        switch (type)
        {
            case "number":
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    return $"{setting.Name} takes a number";
                }

                rendered = number.ToString(CultureInfo.InvariantCulture);
                return null;

            case "#RRGGBB":
                if (!IsHexColor(value))
                {
                    return $"{setting.Name} takes a colour as #RRGGBB";
                }

                rendered = QuoteScalar(value);
                return null;

            default:
                rendered = QuoteScalar(value);
                return null;
        }
    }

    /// <summary>
    /// Wraps a value in double quotes, escaping what YAML requires.
    /// </summary>
    /// <remarks>
    /// Always quoted, even when it would parse bare. An unquoted font family called
    /// <c>y</c> or <c>on</c> would come back as a boolean, which is the same trap the
    /// binding keys have.
    /// </remarks>
    private static string QuoteScalar(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static bool IsHexColor(string value) =>
        value.Length == 7
        && value[0] == '#'
        && value[1..].All(c => char.IsAsciiHexDigit(c));

    /// <summary>
    /// Renders the documented default configuration file.
    /// </summary>
    /// <param name="defaultBindings">
    /// Shipped bindings, written into the file as live entries so they can be edited in
    /// place.
    /// </param>
    /// <param name="descriptions">
    /// Command name to one-line description, so every binding can be annotated. Comes from
    /// the command registry, which is why the file cannot describe a command that does not
    /// exist.
    /// </param>
    public static string Render(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? defaultBindings = null,
        IReadOnlyDictionary<string, string>? descriptions = null)
    {
        var output = new StringBuilder();

        output.AppendLine("# Fjord configuration.");
        output.AppendLine("#");
        output.AppendLine("# Edited while Fjord is running, changes apply on save. A file that fails to");
        output.AppendLine("# parse is reported in the status bar and the previous settings are kept.");
        output.AppendLine();

        var written = new List<string>();

        foreach (var setting in All)
        {
            if (setting.Name == "bindings")
            {
                continue;
            }

            var parts = setting.Name.Split('.');
            EmitParents(output, written, parts);

            var indent = new string(' ', (parts.Length - 1) * 2);
            output.Append(indent).Append("# ").AppendLine(setting.Doc);
            output.Append(indent).Append("# type: ").AppendLine(setting.Type);
            output.Append(indent).Append(parts[^1]).Append(": ").AppendLine(setting.Default);
            output.AppendLine();
        }

        EmitBindings(output, defaultBindings, descriptions);
        return output.ToString();
    }

    /// <summary>
    /// Quotes a binding key for YAML.
    /// </summary>
    /// <remarks>
    /// Unconditionally, not just for punctuation. This is the one rule that must not be
    /// relaxed: <c>n</c> and <c>y</c> are core vim bindings and bare they parse as
    /// booleans, turning the key into <c>false</c> and losing the binding without an error.
    /// </remarks>
    public static string QuoteKey(string keys) =>
        "\"" + keys.Replace("\\", "\\\\", StringComparison.Ordinal)
                   .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static void EmitParents(StringBuilder output, List<string> written, string[] parts)
    {
        for (var depth = 0; depth < parts.Length - 1; depth++)
        {
            var prefix = string.Join('.', parts[..(depth + 1)]);
            if (written.Contains(prefix))
            {
                continue;
            }

            written.Add(prefix);
            output.Append(new string(' ', depth * 2)).Append(parts[depth]).AppendLine(":");
        }
    }

    /// <summary>
    /// Writes the bindings section with every default present and annotated.
    /// </summary>
    /// <remarks>
    /// Written as <b>live entries</b>, not commented-out reference. A commented block reads
    /// as documentation you have to retype; live entries can be edited in place, which is
    /// the point of opening the file at all.
    /// <para>
    /// The cost is that these become the user's own layer, so a default changed in a later
    /// version will not reach an existing config. That is the usual bargain for a generated
    /// keymap, and the file says so rather than leaving it to be deduced.
    /// </para>
    /// </remarks>
    private static void EmitBindings(
        StringBuilder output,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? defaults,
        IReadOnlyDictionary<string, string>? descriptions)
    {
        var setting = Find("bindings")!;

        output.Append("# ").AppendLine(setting.Doc);
        output.AppendLine("#");
        output.AppendLine("# Every default is listed below so it can be changed in place. Set a command to");
        output.AppendLine("# an empty string to unbind the key entirely.");
        output.AppendLine("#");
        output.AppendLine("# Because these are now yours, a default changed in a future version will not");
        output.AppendLine("# reach this file. Delete a mode's block to go back to the shipped defaults.");
        output.AppendLine("#");
        output.AppendLine("# Modes:");
        output.AppendLine("#   normal            the mode Fjord sits in");
        output.AppendLine("#   visual            after v, with a selection anchored");
        output.AppendLine("#   operator-pending  after an operator such as d or y, awaiting a motion");
        output.AppendLine("#");
        output.AppendLine("# Key syntax: a bare character, or <> for anything else - <Ctrl+d>, <Escape>,");
        output.AppendLine("# <Space>, <Return>, <PgDown>, <F1>. Multi-key sequences are written together,");
        output.AppendLine("# as in \"gg\". Run :help for what each command does.");

        if (defaults is null || defaults.Count == 0)
        {
            output.AppendLine("bindings: {}");
            return;
        }

        output.AppendLine("bindings:");

        foreach (var (mode, bindings) in defaults.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            output.AppendLine();
            output.Append("  ").Append(mode).AppendLine(":");

            foreach (var (keys, command) in bindings.OrderBy(b => b.Key, StringComparer.Ordinal))
            {
                if (descriptions?.GetValueOrDefault(FirstWord(command)) is { Length: > 0 } description)
                {
                    output.Append("    # ").AppendLine(description);
                }

                output.Append("    ").Append(QuoteKey(keys)).Append(": ").AppendLine(QuoteKey(command));
            }
        }
    }

    private static string FirstWord(string command)
    {
        var end = command.IndexOf(' ', StringComparison.Ordinal);
        return end < 0 ? command : command[..end];
    }

}
