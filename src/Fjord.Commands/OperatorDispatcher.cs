using Fjord.Core.Diagnostics;
using Fjord.Core.Panes;
using Fjord.Core.Selection;

namespace Fjord.Commands;

/// <summary>
/// Runs an operator against the range a motion covers, as <c>d3j</c> does.
/// </summary>
/// <remarks>
/// The range is obtained by <b>running the motion for real and diffing the cursor</b>,
/// rather than maintaining a parallel "what would this motion do" implementation. Two
/// things fall out of that: every motion becomes a valid operator object the day it is
/// added, and the two can never disagree about where a motion lands.
/// <para>
/// It works only because motions are pure cursor moves. That is not a hope — a test
/// reflects over the registry and asserts that every command declaring a
/// <see cref="MotionKind"/> leaves the snapshot untouched.
/// </para>
/// </remarks>
public sealed class OperatorDispatcher
{
    private readonly CommandParser _parser;
    private readonly CommandRegistry _registry;

    public OperatorDispatcher(CommandParser parser, CommandRegistry registry)
    {
        _parser = parser;
        _registry = registry;
    }

    /// <summary>
    /// Runs <paramref name="operatorCommand"/> over the rows <paramref name="motionCommand"/> spans.
    /// </summary>
    /// <param name="count">
    /// The effective repeat. Vim multiplies the two counts in
    /// <c>[count1] operator [count2] motion</c>, so <c>3d2j</c> covers six rows; the mode
    /// manager does that multiplication before calling here.
    /// </param>
    public void Execute(
        string operatorCommand,
        string motionCommand,
        int? count,
        char register,
        IPane pane,
        IMessageSink messages)
    {
        ArgumentNullException.ThrowIfNull(pane);

        var motionName = FirstWord(motionCommand);
        if (_registry.Find(motionName) is not { IsMotion: true })
        {
            throw new CommandException($"{motionName}: not a motion, so it cannot follow an operator.");
        }

        var before = pane.CursorIndex;

        _parser.Execute(motionCommand, new CommandContext
        {
            Count = count,
            Pane = pane,
            Messages = messages,
        });

        var after = pane.CursorIndex;

        // An operator does not leave the cursor where the motion put it; it acts on the
        // span and returns. Restoring before running the operator also means the operator
        // body sees the cursor it was invoked from.
        pane.CursorIndex = before;

        var range = EntryRange.Between(before, after);

        _parser.Execute(operatorCommand, new CommandContext
        {
            Pane = pane,
            Range = range,
            Register = register,
            Messages = messages,
        });
    }

    /// <summary>
    /// Runs an operator over an already-selected range, as visual mode does.
    /// </summary>
    /// <remarks>
    /// No motion is involved: in visual mode the range exists before the operator is
    /// typed, which is why an operator key in visual mode must not enter operator-pending.
    /// </remarks>
    public void ExecuteOverRange(
        string operatorCommand,
        EntryRange range,
        char register,
        IPane pane,
        IMessageSink messages)
    {
        ArgumentNullException.ThrowIfNull(pane);

        _parser.Execute(operatorCommand, new CommandContext
        {
            Pane = pane,
            Range = range,
            Register = register,
            Messages = messages,
        });
    }

    private static string FirstWord(string command)
    {
        var text = command.TrimStart();
        if (text.StartsWith(':'))
        {
            text = text[1..];
        }

        var end = text.IndexOfAny([' ', '\t']);
        return end < 0 ? text : text[..end];
    }
}
