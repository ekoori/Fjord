using Fjord.Core.Input;

namespace Fjord.Commands;

/// <summary>
/// The bindings Fjord ships with.
/// </summary>
/// <remarks>
/// A table in code rather than in the config schema, because it is the one thing that must
/// stay in step with the command registry: a test resolves every command string here
/// against the registry, so renaming a command without updating its binding fails the
/// build rather than producing a key that silently does nothing.
/// <para>
/// QuteSharp reads its defaults from qutebrowser's <c>configdata</c>, which Fjord has no
/// equivalent of.
/// </para>
/// </remarks>
public static class DefaultBindings
{
    /// <summary>Bindings per mode name, as <c>keys -&gt; command</c>.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> All { get; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["normal"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // Motions
                ["j"] = "move-down",
                ["k"] = "move-up",
                ["<Down>"] = "move-down",
                ["<Up>"] = "move-up",
                ["gg"] = "move-first",
                ["G"] = "move-last",
                ["<Home>"] = "move-first",
                ["<End>"] = "move-last",
                ["<PgDown>"] = "move-page-down",
                ["<PgUp>"] = "move-page-up",
                ["<Ctrl+f>"] = "move-page-down",
                ["<Ctrl+b>"] = "move-page-up",
                ["<Ctrl+d>"] = "move-half-page-down",
                ["<Ctrl+u>"] = "move-half-page-up",

                // Navigation. h/l follow the directory tree rather than moving a cursor
                // sideways, which is the file-manager reading of them.
                ["l"] = "enter",
                ["<Return>"] = "enter",
                ["<Right>"] = "enter",
                ["h"] = "leave",
                ["<Backspace>"] = "leave",
                ["<Left>"] = "leave",
                ["r"] = "reload",
                ["~"] = "go-home",
                ["o"] = "open-with-default",
                ["R"] = "reveal",
                ["<Ctrl+o>"] = "back",
                ["<Ctrl+i>"] = "forward",
                ["<Back>"] = "back",
                ["<Forward>"] = "forward",

                // Listing
                ["s"] = "sort",
                ["."] = "toggle-hidden",

                // Selection
                ["<Space>"] = "select-toggle",
                ["<Ctrl+a>"] = "select-all",
                ["<Escape>"] = "select-none",
                ["<Ctrl+r>"] = "select-invert",

                // Tabs
                ["gt"] = "tab-next",
                ["gT"] = "tab-prev",
                ["<Ctrl+t>"] = "tab-new",
                ["<Ctrl+w>"] = "tab-close",
                ["<Ctrl+Shift+t>"] = "tab-undo-close",

                // Modes
                ["v"] = "visual-mode",
                [":"] = "command-mode",

                // Operators. These wait for a motion; the mode manager does not dispatch
                // them on sight.
                ["y"] = "yank",
                ["d"] = "select-range",
            },

            ["visual"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["j"] = "move-down",
                ["k"] = "move-up",
                ["<Down>"] = "move-down",
                ["<Up>"] = "move-up",
                ["gg"] = "move-first",
                ["G"] = "move-last",
                ["<Ctrl+d>"] = "move-half-page-down",
                ["<Ctrl+u>"] = "move-half-page-up",

                // Operators run at once here: the range already exists.
                ["y"] = "yank",
                ["d"] = "select-range",
                ["o"] = "visual-swap",
                ["v"] = "mode-leave",
                ["<Escape>"] = "mode-leave",
            },

            ["operator-pending"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["j"] = "move-down",
                ["k"] = "move-up",
                ["gg"] = "move-first",
                ["G"] = "move-last",
                ["<Ctrl+d>"] = "move-half-page-down",
                ["<Ctrl+u>"] = "move-half-page-up",
            },
        };

    /// <summary>Installs the defaults into a <see cref="KeyBindings"/>.</summary>
    public static void Load(KeyBindings bindings, KeyModeRegistry modes)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(modes);

        foreach (var (modeName, entries) in All)
        {
            if (!modes.TryParse(modeName, out var mode))
            {
                throw new CommandException($"Default bindings name unknown mode '{modeName}'.");
            }

            foreach (var (keys, command) in entries)
            {
                bindings.AddDefault(mode, keys, command);
            }
        }
    }

    /// <summary>
    /// Applies the user's <c>bindings:</c> section over the defaults.
    /// </summary>
    /// <remarks>
    /// An empty command unbinds, which masks the default rather than deleting it so
    /// <c>:bind-default</c> can restore it. Unknown mode names are collected rather than
    /// thrown, so one typo does not cost the user every other binding they wrote.
    /// </remarks>
    public static IReadOnlyList<string> Apply(
        KeyBindings bindings,
        KeyModeRegistry modes,
        IReadOnlyDictionary<string, Dictionary<string, string>> userBindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(userBindings);

        var problems = new List<string>();

        foreach (var (modeName, entries) in userBindings)
        {
            if (!modes.TryParse(modeName, out var mode))
            {
                problems.Add($"unknown mode '{modeName}'");
                continue;
            }

            foreach (var (keys, command) in entries)
            {
                try
                {
                    if (string.IsNullOrEmpty(command))
                    {
                        bindings.Unbind(mode, keys);
                    }
                    else
                    {
                        bindings.Bind(mode, keys, command);
                    }
                }
                catch (Core.Keys.KeyParseException ex)
                {
                    problems.Add($"{modeName}/{keys}: {ex.Message}");
                }
            }
        }

        return problems;
    }
}
