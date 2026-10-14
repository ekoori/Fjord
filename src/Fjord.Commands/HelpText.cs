using System.Text;
using Fjord.Core.Diagnostics;
using Fjord.Core.Input;

namespace Fjord.Commands;

/// <summary>
/// Renders the command registry as readable help.
/// </summary>
/// <remarks>
/// A pure function of the registry and the bindings, so it is fully testable and cannot
/// drift from what the app actually does — the same reason the config file is generated
/// rather than hand-written.
/// <para>
/// The bindings are <b>inverted</b> here: they are stored keys-to-command, but the question
/// a user asks is "what key runs this", so each command is shown with every key that
/// reaches it, in every mode.
/// </para>
/// </remarks>
public static class HelpText
{
    /// <summary>Column at which descriptions start, so names and keys line up.</summary>
    private const int DescriptionColumn = 34;

    /// <summary>Every command, grouped, with the keys that run it.</summary>
    public static string Overview(CommandRegistry registry, KeyBindings bindings, KeyModeRegistry modes)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var keys = InvertBindings(bindings, modes);
        var output = new StringBuilder();

        output.Append("Fjord ").Append(BuildInfo.Version).AppendLine(" — commands and keys");
        output.AppendLine();
        output.AppendLine($"  {BuildInfo.Runtime}");
        output.AppendLine();
        output.AppendLine("  :help <command>   detail for one command");
        output.AppendLine("  :config           open the configuration file");
        output.AppendLine("  Escape or q       close this page");
        output.AppendLine();

        foreach (var group in registry.ByGroup())
        {
            output.Append("── ").Append(group.Key).AppendLine(" ──");

            foreach (var command in group)
            {
                AppendSummary(output, command, keys);
            }

            output.AppendLine();
        }

        AppendUnboundNote(output, registry, keys);
        return output.ToString();
    }

    /// <summary>Everything known about one command.</summary>
    public static string ForCommand(
        CommandDefinition command, KeyBindings bindings, KeyModeRegistry modes)
    {
        ArgumentNullException.ThrowIfNull(command);

        var keys = InvertBindings(bindings, modes);
        var output = new StringBuilder();

        output.Append(':').AppendLine(command.Name);
        output.AppendLine();

        if (command.Description.Length > 0)
        {
            output.AppendLine(command.Description);
            output.AppendLine();
        }

        output.Append("Usage    ").AppendLine(command.Usage);
        output.Append("Group    ").AppendLine(command.Group);

        if (command.Aliases.Count > 0)
        {
            output.Append("Aliases  ").AppendLine(string.Join(", ", command.Aliases.Select(a => ":" + a)));
        }

        if (keys.TryGetValue(command.Name, out var bound) && bound.Count > 0)
        {
            output.Append("Keys     ").AppendLine(string.Join("  ", bound));
        }
        else
        {
            output.AppendLine("Keys     (none — run it from the command line)");
        }

        if (command.IsOperator)
        {
            output.AppendLine();
            output.AppendLine("Operator. Waits for a motion and acts on the rows it covers,");
            output.AppendLine("so `d3j` applies it to three rows. In visual mode it runs at");
            output.AppendLine("once over the selection.");
        }

        if (command.IsMotion)
        {
            output.AppendLine();
            output.Append("Motion (").Append(command.Motion.ToString().ToLowerInvariant())
                .AppendLine("). Can follow an operator to give it a range.");
        }

        if (command.DeprecatedBy is { } replacement)
        {
            output.AppendLine();
            output.Append("Deprecated — use :").Append(replacement).AppendLine(" instead.");
        }

        AppendArguments(output, command);
        return output.ToString();
    }

    /// <summary>The message for a name that is not a command.</summary>
    /// <remarks>
    /// Suggests near matches rather than only refusing: the usual reason for asking about
    /// a command that does not exist is a half-remembered name.
    /// </remarks>
    public static string UnknownCommand(string name, CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var near = registry.Commands
            .Where(c => c.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            .Select(c => ":" + c.Name)
            .Take(8)
            .ToList();

        return near.Count > 0
            ? $"No command '{name}'. Did you mean: {string.Join(", ", near)}"
            : $"No command '{name}'. Try :help for the full list.";
    }

    private static void AppendSummary(
        StringBuilder output, CommandDefinition command, IReadOnlyDictionary<string, List<string>> keys)
    {
        var bound = keys.TryGetValue(command.Name, out var list) && list.Count > 0
            ? string.Join(" ", list.Take(3))
            : string.Empty;

        var left = $"  {bound,-12} :{command.Name}";

        output.Append(left);

        if (command.Description.Length > 0)
        {
            // Wrap to the description column, but never run a long name into its own text.
            output.Append(left.Length >= DescriptionColumn
                ? Environment.NewLine + new string(' ', DescriptionColumn)
                : new string(' ', DescriptionColumn - left.Length));

            output.Append(command.Description);
        }

        output.AppendLine();
    }

    private static void AppendArguments(StringBuilder output, CommandDefinition command)
    {
        var described = command.Arguments
            .Where(a => !a.IsRuntimeSupplied && (a.Description.Length > 0 || a.Choices.Count > 0))
            .ToList();

        if (described.Count == 0)
        {
            return;
        }

        output.AppendLine();
        output.AppendLine("Arguments");

        foreach (var argument in described)
        {
            var name = argument.IsFlag ? "--" + argument.Name : argument.Name;
            output.Append("  ").Append(name.PadRight(18));

            if (argument.Description.Length > 0)
            {
                output.Append(argument.Description);
            }

            if (argument.Choices.Count > 0)
            {
                output.Append(argument.Description.Length > 0 ? "  " : string.Empty)
                    .Append('(').Append(string.Join(" | ", argument.Choices)).Append(')');
            }

            output.AppendLine();
        }
    }

    private static void AppendUnboundNote(
        StringBuilder output, CommandRegistry registry, IReadOnlyDictionary<string, List<string>> keys)
    {
        var unbound = registry.Commands.Count(c => !keys.ContainsKey(c.Name));

        if (unbound > 0)
        {
            output.Append("Commands shown without a key are run from the command line with ':'. ")
                .Append(unbound).AppendLine(" have no binding.");
        }
    }

    /// <summary>
    /// Builds command-name to key-strings, across every mode.
    /// </summary>
    /// <remarks>
    /// Keys from a mode other than normal are labelled, because <c>y</c> in visual mode and
    /// <c>y</c> in normal mode are genuinely different bindings and showing them
    /// undifferentiated would mislead.
    /// </remarks>
    private static Dictionary<string, List<string>> InvertBindings(
        KeyBindings bindings, KeyModeRegistry modes)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        if (bindings is null || modes is null)
        {
            return result;
        }

        foreach (var mode in modes.Modes.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            foreach (var (sequence, command) in bindings.For(mode))
            {
                if (command.Length == 0)
                {
                    continue;
                }

                var name = FirstWord(command);
                var label = mode == KeyMode.Normal
                    ? sequence.ToString()
                    : $"{sequence}({mode.Name[0]})";

                if (!result.TryGetValue(name, out var list))
                {
                    list = [];
                    result[name] = list;
                }

                if (!list.Contains(label, StringComparer.Ordinal))
                {
                    list.Add(label);
                }
            }
        }

        foreach (var list in result.Values)
        {
            // Shortest first: the key a user should learn is the one that is least typing.
            list.Sort((a, b) => a.Length != b.Length
                ? a.Length - b.Length
                : string.CompareOrdinal(a, b));
        }

        return result;
    }

    private static string FirstWord(string command)
    {
        var end = command.IndexOf(' ', StringComparison.Ordinal);
        return end < 0 ? command : command[..end];
    }
}
