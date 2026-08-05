using System.Reflection;

namespace Fjord.Commands;

/// <summary>
/// Command metadata read straight from the attributes, without building anything.
/// </summary>
/// <remarks>
/// <see cref="CommandRegistry"/> needs an instance of each command class, and those need
/// panes, a queue and a window. Writing the default configuration file needs only the
/// descriptions, and happens before any of that exists — from the
/// <c>--write-default-config</c> switch, before a window is even created.
/// <para>
/// Scanning declarations rather than registrations also means the generated file describes
/// every command that exists, not merely those a particular composition happened to wire
/// up.
/// </para>
/// </remarks>
public static class CommandCatalogue
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Cached = new(Scan);

    /// <summary>Command name to one-line description, aliases included.</summary>
    public static IReadOnlyDictionary<string, string> Descriptions => Cached.Value;

    private static IReadOnlyDictionary<string, string> Scan()
    {
        var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var type in typeof(CommandCatalogue).Assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(
                         BindingFlags.Public | BindingFlags.NonPublic |
                         BindingFlags.Instance | BindingFlags.Static))
            {
                if (method.GetCustomAttribute<CommandAttribute>() is not { } attribute)
                {
                    continue;
                }

                descriptions[attribute.Name] = attribute.Description;

                foreach (var alias in attribute.Aliases)
                {
                    descriptions[alias] = attribute.Description;
                }
            }
        }

        return descriptions;
    }
}
