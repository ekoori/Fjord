// Vendored from QuteSharp (src/QuteSharp.Commands/CommandParser.cs). See VENDORED.md.
// Changed: CommandContext carries a pane, workspace, register and range instead of a
// browser tab and window id; Supply gained the matching cases.
using System.Globalization;
using System.Reflection;
using Fjord.Core.Diagnostics;
using Fjord.Core.Panes;
using Fjord.Core.Selection;

namespace Fjord.Commands;

/// <summary>Context the runtime supplies to a command, separate from what the user typed.</summary>
public sealed class CommandContext
{
    /// <summary>Numeric prefix, as in <c>3j</c>.</summary>
    public int? Count { get; init; }

    public IPane? Pane { get; init; }

    public IWorkspace? Workspace { get; init; }

    /// <summary>The register named with <c>"</c>, defaulting to the unnamed one.</summary>
    public char Register { get; init; } = '"';

    /// <summary>The rows an operator is acting on, when one is running.</summary>
    public EntryRange? Range { get; init; }

    public IMessageSink Messages { get; init; } = NullMessageSink.Instance;
}

/// <summary>A parsed command, ready to run.</summary>
public sealed record ParsedCommand(CommandDefinition Definition, object?[] Arguments)
{
    /// <summary>
    /// Invokes the command body.
    /// </summary>
    /// <remarks>
    /// Reflection wraps anything the body throws in a
    /// <see cref="TargetInvocationException"/>, which would surface to the user as that
    /// rather than as what actually went wrong. Unwrapping keeps the message useful.
    /// </remarks>
    public object? Invoke()
    {
        try
        {
            return Definition.Method.Invoke(Definition.Target, Arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
        {
            if (inner is CommandException)
            {
                throw inner;
            }

            throw new CommandException($"{Definition.Name}: {inner.Message}", inner);
        }
    }
}

/// <summary>Turns command-line text into an invocation.</summary>
public sealed class CommandParser
{
    private readonly CommandRegistry _registry;

    public CommandParser(CommandRegistry registry) => _registry = registry;

    /// <summary>The commands this parser resolves against.</summary>
    /// <remarks>
    /// Exposed so a caller building a command line can ask how its argument will be read —
    /// a command taking free text must not be handed quoted values.
    /// </remarks>
    public CommandRegistry Registry => _registry;

    /// <summary>Parses a line, which may contain several commands separated by <c>;;</c>.</summary>
    public IReadOnlyList<ParsedCommand> Parse(string line, CommandContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(line);
        context ??= new CommandContext();

        var trimmed = line.TrimStart();
        if (trimmed.StartsWith(':'))
        {
            trimmed = trimmed[1..];
        }

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return [];
        }

        // Whether ';;' splits depends on the command, so the first word has to be resolved
        // before deciding.
        var firstWord = trimmed.Split([' ', '\t'], 2)[0];

        if (_registry.Find(firstWord) is { NoCommandSplit: true })
        {
            return [ParseOne(trimmed, context)];
        }

        return CommandLexer.SplitCommands(trimmed)
            .Where(part => part.Length > 0)
            .Select(part => ParseOne(part, context))
            .ToList();
    }

    /// <summary>Parses and runs a line, returning how many commands ran.</summary>
    public int Execute(string line, CommandContext? context = null)
    {
        var parsed = Parse(line, context);

        foreach (var command in parsed)
        {
            command.Invoke();
        }

        return parsed.Count;
    }

    private ParsedCommand ParseOne(string text, CommandContext context)
    {
        var tokens = CommandLexer.Split(text);
        if (tokens.Count == 0)
        {
            throw new CommandException("No command given.");
        }

        var name = tokens[0];
        var definition = _registry.Find(name)
            ?? throw new CommandException($"{name}: no such command");

        var arguments = definition.MaxSplit >= 0
            ? SplitLimited(text, definition.MaxSplit, definition)
            : tokens.Skip(1).ToList();

        return new ParsedCommand(definition, Bind(definition, arguments, context));
    }

    /// <summary>
    /// Splits the argument text a limited number of times, leaving the remainder intact.
    /// </summary>
    /// <remarks>
    /// Operates on the raw text rather than rejoining already-split tokens, so the
    /// remainder keeps its original spacing — it may be a search pattern or a message the
    /// user wrote, where collapsing runs of spaces changes the meaning. Leading flags are
    /// still recognised, since they are written before the free text.
    /// </remarks>
    private static IReadOnlyList<string> SplitLimited(string text, int maxSplit, CommandDefinition definition)
    {
        var rest = text.TrimStart();

        // Drop the command name.
        var afterName = rest.IndexOfAny([' ', '\t']);
        rest = afterName < 0 ? "" : rest[(afterName + 1)..].TrimStart();

        var result = new List<string>();

        // Leading flags first; everything after the first non-flag word is free text.
        while (rest.StartsWith('-') && rest.Length > 1 && !char.IsDigit(rest[1]))
        {
            var end = rest.IndexOfAny([' ', '\t']);
            if (end < 0)
            {
                result.Add(rest);
                return result;
            }

            var flag = rest[..end];
            result.Add(flag);
            rest = rest[(end + 1)..].TrimStart();

            // A flag that takes a value consumes the next word as that value. Without
            // this, the value and the free-text remainder merge into one token.
            if (!TakesValue(definition, flag))
            {
                continue;
            }

            var valueEnd = rest.IndexOfAny([' ', '\t']);
            if (valueEnd < 0)
            {
                result.Add(rest);
                return result;
            }

            result.Add(rest[..valueEnd]);
            rest = rest[(valueEnd + 1)..].TrimStart();
        }

        for (var i = 0; i < maxSplit && rest.Length > 0; i++)
        {
            var end = rest.IndexOfAny([' ', '\t']);
            if (end < 0)
            {
                break;
            }

            result.Add(rest[..end]);
            rest = rest[(end + 1)..].TrimStart();
        }

        if (rest.Length > 0)
        {
            result.Add(rest);
        }

        return result;
    }

    /// <summary>Maps tokens onto the method's parameters.</summary>
    private static object?[] Bind(
        CommandDefinition definition, IReadOnlyList<string> tokens, CommandContext context)
    {
        var values = new Dictionary<CommandArgument, object?>();
        var positional = new List<string>();

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            // "--" ends flag parsing, so a value that looks like a flag can still be
            // passed — a filename beginning with a dash is the usual case.
            if (token == "--")
            {
                positional.AddRange(tokens.Skip(i + 1));
                break;
            }

            if (token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2)
            {
                var flagName = token[2..];
                var argument = definition.Flags.FirstOrDefault(a => a.Name == flagName)
                    ?? throw new CommandException($"{definition.Name}: unknown flag --{flagName}");

                values[argument] = TakeFlagValue(definition, argument, tokens, ref i);
                continue;
            }

            if (token.StartsWith('-') && token.Length > 1 && !char.IsDigit(token[1]))
            {
                // Short flags combine: -tb is -t -b. A value-taking flag can only be last
                // in such a group, since it consumes the next token.
                foreach (var c in token[1..])
                {
                    var argument = definition.Flags.FirstOrDefault(a => a.ShortFlag == c)
                        ?? throw new CommandException($"{definition.Name}: unknown flag -{c}");

                    values[argument] = TakeFlagValue(definition, argument, tokens, ref i);
                }

                continue;
            }

            positional.Add(token);
        }

        var result = new object?[definition.Arguments.Count];
        var next = 0;

        for (var i = 0; i < definition.Arguments.Count; i++)
        {
            var argument = definition.Arguments[i];

            if (argument.IsRuntimeSupplied)
            {
                result[i] = Supply(argument, context, definition);
                continue;
            }

            if (argument.IsFlag)
            {
                result[i] = values.TryGetValue(argument, out var flag)
                    ? flag
                    : argument.DefaultValue ?? (argument.TakesValue ? null : false);
                continue;
            }

            if (argument.IsVariadic)
            {
                var rest = positional.Skip(next).ToArray();
                next = positional.Count;

                var element = argument.Type.GetElementType() ?? typeof(string);
                var array = Array.CreateInstance(element, rest.Length);
                for (var j = 0; j < rest.Length; j++)
                {
                    array.SetValue(Convert(rest[j], element, argument, definition), j);
                }

                result[i] = array;
                continue;
            }

            if (next < positional.Count)
            {
                result[i] = Convert(positional[next++], argument.Type, argument, definition);
                continue;
            }

            if (!argument.IsOptional)
            {
                throw new CommandException(
                    $"{definition.Name}: missing argument '{argument.Name}'. Usage: {definition.Usage}");
            }

            result[i] = argument.DefaultValue;
        }

        if (next < positional.Count)
        {
            throw new CommandException(
                $"{definition.Name}: too many arguments. Usage: {definition.Usage}");
        }

        return result;
    }

    /// <summary>
    /// Whether a flag token as typed (<c>-k</c> or <c>--sort-key</c>) names an argument
    /// that consumes a value.
    /// </summary>
    private static bool TakesValue(CommandDefinition definition, string flag)
    {
        if (flag.StartsWith("--", StringComparison.Ordinal))
        {
            return definition.Flags.FirstOrDefault(a => a.Name == flag[2..])?.TakesValue == true;
        }

        // Only the last flag in a combined group could take a value.
        return definition.Flags.FirstOrDefault(a => a.ShortFlag == flag[^1])?.TakesValue == true;
    }

    private static object? TakeFlagValue(
        CommandDefinition definition, CommandArgument argument, IReadOnlyList<string> tokens, ref int index)
    {
        if (!argument.TakesValue)
        {
            return true;
        }

        if (index + 1 >= tokens.Count)
        {
            throw new CommandException(
                $"{definition.Name}: --{argument.Name} needs a value. Usage: {definition.Usage}");
        }

        index++;
        return Convert(tokens[index], argument.Type, argument, definition);
    }

    private static object? Supply(
        CommandArgument argument, CommandContext context, CommandDefinition definition) => argument.Value switch
        {
            // A command taking a count is written to work without one, so an absent count
            // falls back to the parameter's own default rather than erroring.
            CommandValue.Count => context.Count ?? argument.DefaultValue,
            CommandValue.Pane => context.Pane ?? Missing(definition, "a pane"),
            CommandValue.Workspace => context.Workspace ?? Missing(definition, "a workspace"),
            CommandValue.Register => context.Register,
            CommandValue.Range => context.Range ?? Missing(definition, "a range"),
            CommandValue.Messages => context.Messages,
            _ => argument.DefaultValue,
        };

    /// <summary>
    /// Reports a command invoked without the context it declared.
    /// </summary>
    /// <remarks>
    /// A missing pane or range means the caller built the context wrongly — an operator
    /// dispatched without a motion having resolved, say. Failing here beats a
    /// <see cref="NullReferenceException"/> from inside the command body.
    /// </remarks>
    private static object Missing(CommandDefinition definition, string what) =>
        throw new CommandException($"{definition.Name}: needs {what}, but none was supplied.");

    private static object? Convert(
        string text, Type type, CommandArgument argument, CommandDefinition definition)
    {
        if (argument.Choices.Count > 0 && !argument.Choices.Contains(text, StringComparer.Ordinal))
        {
            throw new CommandException(
                $"{definition.Name}: invalid value '{text}' for {argument.Name}; " +
                $"expected one of: {string.Join(", ", argument.Choices)}");
        }

        if (type == typeof(string))
        {
            return text;
        }

        if (type.IsEnum)
        {
            // Users type hyphenated names; enum members are Pascal case.
            var candidate = text.Replace("-", "", StringComparison.Ordinal);
            if (Enum.TryParse(type, candidate, ignoreCase: true, out var parsed))
            {
                return parsed;
            }

            throw new CommandException(
                $"{definition.Name}: invalid value '{text}' for {argument.Name}; " +
                $"expected one of: {string.Join(", ", Enum.GetNames(type).Select(CommandNaming.ToHyphenated))}");
        }

        try
        {
            return System.Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new CommandException(
                $"{definition.Name}: invalid value '{text}' for {argument.Name} (expected {type.Name}).");
        }
    }
}
