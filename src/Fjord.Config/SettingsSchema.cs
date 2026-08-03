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

        new("bindings", "mode -> keys -> command", "{}",
            "Key bindings, layered over the defaults. Every key MUST be quoted: YAML reads "
            + "bare n, y, on, off, yes and no as booleans."),
    ];

    public static SettingDefinition? Find(string name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// Renders the documented default configuration file.
    /// </summary>
    /// <param name="defaultBindings">
    /// Shipped bindings, written commented-out so the file documents them without the user
    /// layer duplicating every default.
    /// </param>
    public static string Render(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? defaultBindings = null)
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

        EmitBindings(output, defaultBindings);
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

    private static void EmitBindings(
        StringBuilder output, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? defaults)
    {
        var setting = Find("bindings")!;

        output.Append("# ").AppendLine(setting.Doc);
        output.AppendLine("#");
        output.AppendLine("# Only overrides go here; the defaults below are shown for reference.");
        output.AppendLine("# Set a command to an empty string to unbind a default.");
        output.AppendLine("bindings: {}");

        if (defaults is null || defaults.Count == 0)
        {
            return;
        }

        output.AppendLine();
        output.AppendLine("# --- Defaults, for reference ---");

        foreach (var (mode, bindings) in defaults.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            output.Append("#   ").Append(mode).AppendLine(":");

            foreach (var (keys, command) in bindings.OrderBy(b => b.Key, StringComparer.Ordinal))
            {
                output.Append("#     ").Append(QuoteKey(keys)).Append(": ").AppendLine(QuoteKey(command));
            }
        }
    }
}
