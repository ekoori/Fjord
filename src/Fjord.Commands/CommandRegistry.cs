// Vendored from QuteSharp (src/QuteSharp.Commands/CommandRegistry.cs). See VENDORED.md.
// Changed: carries the operator/motion/group metadata through.
using System.Reflection;

namespace Fjord.Commands;

/// <summary>
/// The set of known commands, discovered from methods marked with
/// <see cref="CommandAttribute"/>.
/// </summary>
/// <remarks>
/// Reflection-based discovery is what lets the attributes <i>be</i> the catalogue:
/// <c>:help</c>, completion and the generated config file all read from here, so there is
/// no parallel documentation table to fall out of step with the code.
/// </remarks>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, CommandDefinition> _byName = new(StringComparer.Ordinal);

    /// <summary>Commands in name order, excluding aliases and hidden entries.</summary>
    public IEnumerable<CommandDefinition> Commands =>
        _byName.Values.Distinct().Where(c => !c.Hidden).OrderBy(c => c.Name, StringComparer.Ordinal);

    /// <summary>Every registered command, hidden ones included.</summary>
    public IEnumerable<CommandDefinition> All =>
        _byName.Values.Distinct().OrderBy(c => c.Name, StringComparer.Ordinal);

    /// <summary>Every registered name, including aliases. Used by completion.</summary>
    public IEnumerable<string> Names => _byName.Keys.OrderBy(n => n, StringComparer.Ordinal);

    /// <summary>
    /// Registers every command on an object.
    /// </summary>
    /// <remarks>
    /// Instance methods are bound to <paramref name="target"/>; static methods on its type
    /// are registered too, so a class can mix both.
    /// </remarks>
    public void Register(object target)
    {
        ArgumentNullException.ThrowIfNull(target);

        foreach (var method in target.GetType().GetMethods(
                     BindingFlags.Public | BindingFlags.NonPublic |
                     BindingFlags.Instance | BindingFlags.Static))
        {
            if (method.GetCustomAttribute<CommandAttribute>() is { } attribute)
            {
                Add(Describe(attribute, method, method.IsStatic ? null : target));
            }
        }
    }

    public void Add(CommandDefinition definition)
    {
        foreach (var name in new[] { definition.Name }.Concat(definition.Aliases))
        {
            if (_byName.TryGetValue(name, out var existing))
            {
                throw new CommandException(
                    $"Command '{name}' is already registered by {existing.Method.Name}.");
            }

            _byName[name] = definition;
        }
    }

    /// <summary>Looks up a command by name or alias.</summary>
    public CommandDefinition? Find(string name) =>
        _byName.TryGetValue(name, out var definition) ? definition : null;

    /// <summary>Commands grouped for <c>:help</c> output.</summary>
    public IEnumerable<IGrouping<string, CommandDefinition>> ByGroup() =>
        Commands.GroupBy(c => c.Group).OrderBy(g => g.Key, StringComparer.Ordinal);

    private static CommandDefinition Describe(CommandAttribute attribute, MethodInfo method, object? target)
    {
        var parameters = method.GetParameters();
        var arguments = new List<CommandArgument>(parameters.Length);
        var usedFlags = new HashSet<char>();

        foreach (var parameter in parameters)
        {
            var meta = parameter.GetCustomAttribute<CommandArgumentAttribute>();
            var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;

            var explicitFlag = meta is { Flag: not '\0' };
            var shortFlag = '\0';

            if ((type == typeof(bool) || explicitFlag) && meta?.Value is null or CommandValue.None)
            {
                // Default to the initial letter, but never reuse one: a silently shadowed
                // flag makes a command misbehave in a way that is very hard to spot.
                shortFlag = explicitFlag ? meta!.Flag : char.ToLowerInvariant(parameter.Name![0]);
                if (!usedFlags.Add(shortFlag))
                {
                    shortFlag = '\0';
                }
            }

            arguments.Add(new CommandArgument
            {
                Name = CommandNaming.ToHyphenated(parameter.Name!),
                Type = type,
                IsOptional = parameter.IsOptional,
                DefaultValue = parameter.HasDefaultValue ? parameter.DefaultValue : null,
                ShortFlag = shortFlag,
                ExplicitFlag = explicitFlag,
                Value = meta?.Value ?? CommandValue.None,
                IsVariadic = parameter.GetCustomAttribute<ParamArrayAttribute>() != null,
                Choices = meta?.Choices ?? [],
                Description = meta?.Description ?? "",
                Completion = meta?.Completion ?? CompletionKind.None,
            });
        }

        return new CommandDefinition
        {
            Name = attribute.Name,
            Aliases = attribute.Aliases,
            Method = method,
            Target = target,
            Arguments = arguments,
            Description = attribute.Description,
            Group = attribute.Group,
            Modes = attribute.Modes,
            NoCommandSplit = attribute.NoCommandSplit,
            Hidden = attribute.Hidden,
            MaxSplit = attribute.MaxSplit,
            IsOperator = attribute.IsOperator,
            Motion = attribute.Motion,
            DeprecatedBy = attribute.DeprecatedBy,
        };
    }
}
