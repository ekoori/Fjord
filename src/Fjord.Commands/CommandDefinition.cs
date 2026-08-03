// Vendored from QuteSharp (src/QuteSharp.Commands/CommandDefinition.cs). See VENDORED.md.
// Changed: carries IsOperator, Motion and Group.
using System.Reflection;

namespace Fjord.Commands;

/// <summary>Raised when a command cannot be parsed, found, or bound to its arguments.</summary>
public sealed class CommandException : Exception
{
    public CommandException(string message)
        : base(message)
    {
    }

    public CommandException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>One argument of a command, derived from a method parameter.</summary>
public sealed class CommandArgument
{
    public required string Name { get; init; }

    public required Type Type { get; init; }

    public required bool IsOptional { get; init; }

    public object? DefaultValue { get; init; }

    /// <summary>
    /// True when the argument is written as <c>--name</c> rather than positionally.
    /// </summary>
    /// <remarks>
    /// A bool parameter is a flag by virtue of its type. A non-bool parameter becomes one
    /// only when given an explicit flag character, which is how a value-taking option such
    /// as <c>:sort -k size</c> is spelled.
    /// </remarks>
    public bool IsFlag => Type == typeof(bool) || (ExplicitFlag && Type != typeof(bool));

    /// <summary>Whether the flag consumes the following token as its value.</summary>
    public bool TakesValue => IsFlag && Type != typeof(bool);

    /// <summary>Set when the declaration named a flag character explicitly.</summary>
    public bool ExplicitFlag { get; init; }

    /// <summary>Short form, e.g. <c>-k</c>.</summary>
    public char ShortFlag { get; init; }

    /// <summary>Supplied by the runtime rather than parsed from the command line.</summary>
    public CommandValue Value { get; init; } = CommandValue.None;

    public bool IsRuntimeSupplied => Value != CommandValue.None;

    /// <summary>Collects all remaining positional arguments.</summary>
    public bool IsVariadic { get; init; }

    public IReadOnlyList<string> Choices { get; init; } = [];

    public string Description { get; init; } = "";

    /// <summary>What the command line offers while typing this argument.</summary>
    public CompletionKind Completion { get; init; } = CompletionKind.None;
}

/// <summary>A registered command: its names, its arguments, and the method behind it.</summary>
public sealed class CommandDefinition
{
    public required string Name { get; init; }

    public required IReadOnlyList<string> Aliases { get; init; }

    public required MethodInfo Method { get; init; }

    public required object? Target { get; init; }

    public required IReadOnlyList<CommandArgument> Arguments { get; init; }

    public string Description { get; init; } = "";

    public string Group { get; init; } = "General";

    public IReadOnlyList<string> Modes { get; init; } = [];

    public bool NoCommandSplit { get; init; }

    public bool Hidden { get; init; }

    /// <summary>Times the argument text is split; -1 splits on every space.</summary>
    public int MaxSplit { get; init; } = -1;

    /// <summary>Whether this command waits for a motion to give it a range.</summary>
    public bool IsOperator { get; init; }

    /// <summary>Whether this command can be an operator's object.</summary>
    public MotionKind Motion { get; init; } = MotionKind.None;

    public bool IsMotion => Motion != MotionKind.None;

    public string? DeprecatedBy { get; init; }

    /// <summary>Arguments the user types, in order.</summary>
    public IEnumerable<CommandArgument> Positional =>
        Arguments.Where(a => !a.IsFlag && !a.IsRuntimeSupplied);

    public IEnumerable<CommandArgument> Flags =>
        Arguments.Where(a => a.IsFlag && !a.IsRuntimeSupplied);

    /// <summary>Usage line for help and error messages.</summary>
    public string Usage
    {
        get
        {
            var parts = new List<string> { ":" + Name };

            foreach (var flag in Flags)
            {
                parts.Add($"[--{flag.Name}]");
            }

            foreach (var argument in Positional)
            {
                var text = argument.IsVariadic ? $"{argument.Name}..." : argument.Name;
                parts.Add(argument.IsOptional || argument.IsVariadic ? $"[{text}]" : $"<{text}>");
            }

            return string.Join(" ", parts);
        }
    }

    public override string ToString() => Usage;
}
