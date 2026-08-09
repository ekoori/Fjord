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
                // Ctrl-h/Ctrl-l rather than vim's Ctrl-o/Ctrl-i: h and l already mean
                // "out" and "in" here, so held with Ctrl they mean the same thing through
                // history. Ctrl-o and Ctrl-i are a jump list, which this is not.
                ["<Ctrl+h>"] = "back",
                ["<Ctrl+l>"] = "forward",
                ["<Back>"] = "back",
                ["<Forward>"] = "forward",

                // Listing
                ["s"] = "sort",
                ["."] = "toggle-hidden",

                // Ctrl-d was half-page-down, which j and Ctrl-f already cover between them.
                // Measuring a tree is the thing you cannot get any other way.
                ["<Ctrl+d>"] = "directory-sizes",

                // Selection
                ["<Space>"] = "select-toggle",
                ["<Ctrl+a>"] = "select-all",
                ["<Escape>"] = "select-none",
                ["<Ctrl+Shift+i>"] = "select-invert",

                // Tabs. H and L rather than vim's gt/gT: h and l already move between
                // directories here, so shifted they move between tabs, which keeps the
                // whole left-right axis meaning "sideways" instead of splitting it across
                // two unrelated conventions.
                ["L"] = "tab-next",
                ["H"] = "tab-prev",
                ["<Ctrl+t>"] = "tab-new",
                ["<Ctrl+w>"] = "tab-close",
                ["<Ctrl+Shift+t>"] = "tab-undo-close",

                // Search. Escape already clears the selection; it clears the filter too,
                // which is the more urgent of the two when one is active. The explicit key
                // is shifted because Ctrl-l is history-forward.
                ["/"] = "search-open",
                ["<Ctrl+/>"] = "search-clear",

                // Scrapbook. "s" is taken by sort, so the collect verb gets "S".
                ["S"] = "scrap-add",
                ["gs"] = "scrap-open",

                // Help
                ["?"] = "help",
                ["<F1>"] = "help",

                // Modes
                ["v"] = "visual-mode",
                [":"] = "command-mode",

                // Reads the next keystroke as a register name rather than as a binding.
                ["\""] = "register-select",

                // Command-line starting points. The leading ':' is what makes these open
                // the command line rather than run: the user finishes the line. {name}
                // arrives already quoted, so a filename with spaces stays one argument.
                ["<F2>"] = ":rename {name}",
                ["<Ctrl+F2>"] = ":tab-rename ",
                ["c"] = ":cd ",
                ["m"] = ":mark-set ",
                ["'"] = ":cd ",

                // Operators. These wait for a motion; the mode manager does not dispatch
                // them on sight.
                ["y"] = "yank-paths",
                ["d"] = "trash",
                ["V"] = "select-range",

                // Files. dd, yy and xx are the vim doubling convention: an operator
                // repeated acts on the entry under the cursor, so no motion is needed.
                ["p"] = "paste",
                ["x"] = "cut-paths",
                ["Y"] = "yank-text",
                ["D"] = "trash-selection",
                ["u"] = "undo",
                ["<Ctrl+r>"] = "redo",
            },

            ["visual"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["j"] = "move-down",
                ["k"] = "move-up",
                ["<Down>"] = "move-down",
                ["<Up>"] = "move-up",
                ["gg"] = "move-first",
                ["G"] = "move-last",

                // Operators run at once here: the range already exists.
                ["y"] = "yank-paths",
                ["d"] = "trash",
                ["x"] = "cut-paths",
                ["Y"] = "yank-text",
                ["\""] = "register-select",
                ["S"] = "scrap-add",
                ["o"] = "visual-swap",
                ["v"] = "mode-leave",
                ["<Escape>"] = "mode-leave",

                // The command line has to be reachable from here, or the only way to run
                // anything against a visual range is to bind a key to it first.
                [":"] = "command-mode",
                ["<Space>"] = "select-toggle",
            },

            // Only what differs from normal mode. Everything else — motions, tabs, the
            // command line — falls through, so this stays a short list of the keys whose
            // meaning genuinely changes when the entries are references rather than files.
            ["scrapbook"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // Taking something out of a collection, not off the disk. The doubling is
                // the same convention as everywhere else, and d on its own still waits.
                ["dd"] = "scrap-remove",
                ["D"] = "scrap-remove",
                ["n"] = ":scrap-note ",
                ["gs"] = "scrap-list",
            },

            ["operator-pending"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["j"] = "move-down",
                ["k"] = "move-up",
                ["gg"] = "move-first",
                ["G"] = "move-last",
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

            if (entries is null)
            {
                // "normal:" with nothing under it is valid YAML and deserializes to null.
                // Easy to produce while editing, and it must not take the app down.
                problems.Add($"mode '{modeName}' has no bindings");
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
