// Vendored from QuteSharp (src/QuteSharp.Commands/CommandNaming.cs). See VENDORED.md.
using System.Text;

namespace Fjord.Commands;

/// <summary>
/// Converts C# identifiers to the hyphenated form users type.
/// </summary>
/// <remarks>
/// The single place this conversion lives, because it has to agree in three contexts: the
/// registry deriving a command and flag name from a method and its parameters, the parser
/// reporting an argument name in an error, and completion offering an enum's values. If
/// any disagreed, the name a user is shown would not be the name the parser accepts.
/// </remarks>
public static class CommandNaming
{
    /// <summary><c>sortKey</c> becomes <c>sort-key</c>.</summary>
    public static string ToHyphenated(string name)
    {
        var result = new StringBuilder(name.Length + 4);

        foreach (var c in name)
        {
            if (char.IsUpper(c) && result.Length > 0)
            {
                result.Append('-');
            }

            result.Append(char.ToLowerInvariant(c));
        }

        return result.ToString();
    }
}
