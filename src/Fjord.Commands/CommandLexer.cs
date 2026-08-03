// Vendored from QuteSharp (src/QuteSharp.Commands/CommandLexer.cs). See VENDORED.md.
using System.Text;

namespace Fjord.Commands;

/// <summary>
/// Splits a command line into tokens.
/// </summary>
/// <remarks>
/// Shell-like quoting rather than a plain split, because arguments routinely contain
/// spaces — filenames above all.
/// <para>
/// <b>Backslash handling diverges from QuteSharp deliberately.</b> There, any backslash
/// inside double quotes escapes the next character, because its arguments are URLs and
/// JavaScript. Fjord's are overwhelmingly Windows paths, and that rule silently turns
/// <c>"C:\Users\igor\a b.txt"</c> into <c>C:Usersigora b.txt</c> — a mangled path with no
/// error, about the worst failure a file manager can have.
/// </para>
/// <para>
/// So inside double quotes <b>only <c>\"</c> is an escape</b>. Not <c>\\</c>: treating it
/// as one breaks UNC paths, turning <c>"\\server\share"</c> into <c>\server\share</c>, and
/// a path is far more likely than someone wanting a literal backslash-pair.
/// </para>
/// <para>
/// Single quotes escape nothing at all, so they can carry any text that contains no
/// single quote — which is what <see cref="Quote"/> relies on.
/// </para>
/// </remarks>
public static class CommandLexer
{

    /// <summary>Tokenises <paramref name="text"/>.</summary>
    /// <param name="keepTrailingEmpty">
    /// When the line ends with a separator, emit a trailing empty token. Completion needs
    /// this to tell "user finished a word" from "user is starting the next one";
    /// execution does not.
    /// </param>
    public static IReadOnlyList<string> Split(string text, bool keepTrailingEmpty = false)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var hasToken = false;

        var inSingle = false;
        var inDouble = false;
        var escaped = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (escaped)
            {
                current.Append(c);
                escaped = false;
                continue;
            }

            switch (c)
            {
                // Only before a double quote. Every other backslash — every separator in
                // every Windows path — falls through to the default case and is kept.
                case '\\' when inDouble && i + 1 < text.Length && text[i + 1] == '"':
                    escaped = true;
                    hasToken = true;
                    continue;

                case '\'' when !inDouble:
                    inSingle = !inSingle;
                    hasToken = true;
                    continue;

                case '"' when !inSingle:
                    inDouble = !inDouble;
                    hasToken = true;
                    continue;

                case ' ' or '\t' when !inSingle && !inDouble:
                    if (hasToken)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                        hasToken = false;
                    }

                    continue;

                default:
                    current.Append(c);
                    hasToken = true;
                    continue;
            }
        }

        if (inSingle || inDouble)
        {
            // Refusing the line beats silently dropping the rest of it: a stray quote
            // otherwise turns a command into the same command with a truncated argument,
            // and the resulting error describes the wrong problem entirely.
            throw new CommandException("Unbalanced quotes in command.");
        }

        if (hasToken)
        {
            tokens.Add(current.ToString());
        }
        else if (keepTrailingEmpty && text.Length > 0 && (text[^1] == ' ' || text[^1] == '\t'))
        {
            tokens.Add("");
        }

        return tokens;
    }

    /// <summary>
    /// Splits a line into the sub-commands separated by <c>;;</c>.
    /// </summary>
    /// <remarks>
    /// Separators inside quotes are left alone, so a filename containing <c>;;</c> stays
    /// one command.
    /// </remarks>
    public static IReadOnlyList<string> SplitCommands(string text)
    {
        var parts = new List<string>();
        var current = new StringBuilder();

        var inSingle = false;
        var inDouble = false;
        var escaped = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (escaped)
            {
                current.Append(c);
                escaped = false;
                continue;
            }

            // Same rule as Split: a backslash only escapes a double quote, so path
            // separators survive.
            if (c == '\\' && inDouble && i + 1 < text.Length && text[i + 1] == '"')
            {
                current.Append(c);
                escaped = true;
                continue;
            }

            if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
            }
            else if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
            }
            else if (c == ';' && i + 1 < text.Length && text[i + 1] == ';' && !inSingle && !inDouble)
            {
                parts.Add(current.ToString().Trim());
                current.Clear();
                i++;
                continue;
            }

            current.Append(c);
        }

        var last = current.ToString().Trim();
        if (last.Length > 0 || parts.Count == 0)
        {
            parts.Add(last);
        }

        return parts;
    }

    /// <summary>
    /// Quotes a token so <see cref="Split"/> returns it unchanged.
    /// </summary>
    /// <remarks>
    /// The inverse of the lexer, needed wherever Fjord builds a command line from real
    /// data — filenames with spaces, quotes or backslashes are the normal case here, not
    /// an edge case. A round-trip property test pins the pair.
    /// <para>
    /// Single quotes are the default because they escape nothing, so a Windows path, a UNC
    /// path or a trailing backslash all survive verbatim. A token containing a single
    /// quote cannot be written that way, so it is emitted as alternating runs — the lexer
    /// concatenates adjacent quoted sections into one token, which is the same trick a
    /// POSIX shell uses.
    /// </para>
    /// </remarks>
    public static string Quote(string token)
    {
        ArgumentNullException.ThrowIfNull(token);

        if (token.Length > 0 && !token.Any(NeedsQuoting))
        {
            return token;
        }

        if (!token.Contains('\'', StringComparison.Ordinal))
        {
            return "'" + token + "'";
        }

        // don't  ->  'don'"'"'t'
        return string.Join("\"'\"", token.Split('\'').Select(part => "'" + part + "'"));
    }

    private static bool NeedsQuoting(char c) => c is ' ' or '\t' or '\'' or '"' or '\\';
}
