using Fjord.Core.Panes;

namespace Fjord.Commands;

/// <summary>
/// Bindings that put text on the command line rather than running it.
/// </summary>
/// <remarks>
/// A binding whose command starts with <c>:</c> opens the command line holding that text
/// instead of executing it, which is how a key becomes a starting point rather than a whole
/// action — <c>c</c> giving you <c>:cd </c> to finish, the way qutebrowser's
/// <c>cmd-set-text</c> works. Without the colon the same string runs immediately, so both
/// are available and the colon is the visible difference between them.
/// <para>
/// Placeholders fill in what the key press was about: <c>{name}</c> is the entry under the
/// cursor, so <c>&lt;F2&gt;</c> bound to <c>:rename {name}</c> arrives with the filename
/// already typed and the caret after it.
/// </para>
/// </remarks>
public static class CommandTemplate
{
    /// <summary>Whether this binding puts text on the command line instead of running.</summary>
    public static bool IsCommandLine(string command) =>
        !string.IsNullOrEmpty(command) && command[0] == ':';

    /// <summary>
    /// Substitutes the placeholders in a command-line binding.
    /// </summary>
    /// <remarks>
    /// Values are quoted the way the command line itself quotes them, because the text is
    /// about to be parsed by the same lexer. Without that, renaming a file with a space in
    /// its name would arrive as two arguments.
    /// </remarks>
    public static string Expand(string command, IPane? pane)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.IndexOf('{', StringComparison.Ordinal) < 0)
        {
            return command;
        }

        var name = pane?.CursorName;
        var directory = pane?.Directory;
        var path = name is null || directory is null ? null : Path.Combine(directory, name);

        return command
            .Replace("{}", Quote(name), StringComparison.Ordinal)
            .Replace("{name}", Quote(name), StringComparison.Ordinal)
            .Replace("{path}", Quote(path), StringComparison.Ordinal)
            .Replace("{dir}", Quote(directory), StringComparison.Ordinal);
    }

    /// <summary>
    /// The placeholders, for the generated configuration file's documentation.
    /// </summary>
    public static IReadOnlyList<(string Token, string Meaning)> Placeholders { get; } =
    [
        ("{name}", "the entry under the cursor"),
        ("{path}", "its full path"),
        ("{dir}", "the directory being shown"),
    ];

    /// <summary>An absent value becomes empty rather than the literal placeholder.</summary>
    private static string Quote(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : CommandLexer.Quote(value);
}
