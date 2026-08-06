// Vendored from QuteSharp (src/QuteSharp.Commands/CommandAttributes.cs). See VENDORED.md.
// Changed: CommandValue targets panes rather than browser tabs, and commands can declare
// themselves operators or motions.
namespace Fjord.Commands;

/// <summary>
/// Marks a method as a command the user can run.
/// </summary>
/// <remarks>
/// The method's parameters become the command's arguments, so the signature is the single
/// source of truth for what a command accepts and there is no separate argument table to
/// drift out of step.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CommandAttribute : Attribute
{
    public CommandAttribute(string name) => Name = name;

    /// <summary>Name typed by the user, e.g. <c>move-down</c>.</summary>
    public string Name { get; }

    public string[] Aliases { get; init; } = [];

    /// <summary>One-line summary for completion and <c>:help</c>.</summary>
    public string Description { get; init; } = "";

    /// <summary>Grouping for <c>:help</c> and the generated config file.</summary>
    public string Group { get; init; } = "General";

    /// <summary>
    /// Modes the command may be run in. Empty means normal mode only.
    /// </summary>
    public string[] Modes { get; init; } = [];

    /// <summary>
    /// Whether <c>;;</c> inside the argument text splits into separate commands. A command
    /// that takes a command as an argument must opt out, or its payload is torn apart.
    /// </summary>
    public bool NoCommandSplit { get; init; }

    /// <summary>Hidden from completion, but still runnable.</summary>
    public bool Hidden { get; init; }

    /// <summary>
    /// Times the argument text is split, or -1 to split on every space.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="NoCommandSplit"/>, which is about <c>;;</c>.
    /// <c>MaxSplit = 0</c> makes the whole remainder one argument, which is what commands
    /// taking free text need — a search pattern, a shell line — so the user does not have
    /// to quote every phrase.
    /// </remarks>
    public int MaxSplit { get; init; } = -1;

    /// <summary>
    /// Whether this command waits for a motion and acts on the range the motion covers.
    /// </summary>
    /// <remarks>
    /// The <c>d</c> in <c>d3j</c>. The mode manager does not dispatch an operator on
    /// sight; it stashes it, enters operator-pending mode and dispatches once the motion
    /// resolves. In visual mode the range already exists, so the operator runs immediately.
    /// </remarks>
    public bool IsOperator { get; init; }

    /// <summary>
    /// Whether this command can be an operator's object, and how its span is measured.
    /// </summary>
    /// <remarks>
    /// The <c>j</c> in <c>d3j</c>. Motions must be pure cursor moves — the operator
    /// dispatcher runs the motion for real and diffs the cursor rather than maintaining a
    /// parallel "what would this motion do" implementation. A test enforces that.
    /// </remarks>
    public MotionKind Motion { get; init; } = MotionKind.None;

    /// <summary>Names the replacement when a command has been superseded.</summary>
    public string? DeprecatedBy { get; init; }
}

/// <summary>How an operator measures the span a motion covered.</summary>
public enum MotionKind
{
    /// <summary>Not a motion.</summary>
    None,

    /// <summary>Whole rows. Every motion in a file list today.</summary>
    Linewise,

    /// <summary>Up to but not including the destination.</summary>
    Exclusive,

    /// <summary>Up to and including the destination.</summary>
    Inclusive,
}

/// <summary>
/// Values the runtime supplies rather than the user typing them.
/// </summary>
public enum CommandValue
{
    /// <summary>An ordinary argument taken from the command line.</summary>
    None,

    /// <summary>The numeric prefix the user typed, as in <c>3j</c>.</summary>
    Count,

    /// <summary>The pane the command was invoked in.</summary>
    Pane,

    /// <summary>The window's set of tabs.</summary>
    Workspace,

    /// <summary>The register named with <c>"</c>, or the unnamed one.</summary>
    Register,

    /// <summary>The rows an operator is acting on.</summary>
    Range,

    /// <summary>Where the command reports to the user.</summary>
    Messages,
}

/// <summary>Customises how one parameter appears on the command line.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class CommandArgumentAttribute : Attribute
{
    /// <summary>
    /// Short flag character. Defaults to the first letter of the parameter name, which can
    /// collide, so it is worth setting explicitly on commands with several flags.
    /// </summary>
    public char Flag { get; init; }

    /// <summary>Supplied by the runtime rather than parsed from input.</summary>
    public CommandValue Value { get; init; } = CommandValue.None;

    /// <summary>Values offered in completion for this argument.</summary>
    public string[] Choices { get; init; } = [];

    /// <summary>Description used in help output.</summary>
    public string Description { get; init; } = "";

    /// <summary>What the command line should offer while typing this argument.</summary>
    public CompletionKind Completion { get; init; } = CompletionKind.None;
}

/// <summary>What completion offers for an argument.</summary>
public enum CompletionKind
{
    /// <summary>Only the declared <see cref="CommandArgumentAttribute.Choices"/>, if any.</summary>
    None,

    /// <summary>Files and directories.</summary>
    Path,

    /// <summary>Directories only, as <c>:cd</c> wants.</summary>
    Directory,

    /// <summary>Setting names, as <c>:set</c> wants.</summary>
    Setting,
}
