using Fjord.Config;
using Fjord.Core.Config;

namespace Fjord.App;

/// <summary>
/// Gives the command layer access to the configuration file.
/// </summary>
/// <remarks>
/// The implementation of the seam that keeps <c>Fjord.Commands</c> from referencing
/// <c>Fjord.Config</c>. Reload is delegated back to the window, which owns what "apply the
/// config" means — theme, sort, bindings — and already does it for the file watcher.
/// </remarks>
public sealed class ConfigService : IConfigService
{
    private readonly FjordPaths _paths;
    private readonly Func<string?> _reload;

    /// <param name="reload">
    /// Re-reads and applies the file, returning a parse error or null. The same routine the
    /// watcher uses, so <c>:config-reload</c> and saving in an editor cannot diverge.
    /// </param>
    public ConfigService(FjordPaths paths, Func<string?> reload)
    {
        _paths = paths;
        _reload = reload;
    }

    public string ConfigFilePath => _paths.ConfigFile;

    public bool EnsureExists() => ConfigFile.WriteDefaultIfMissing(ConfigFilePath, DefaultBindingsForReference);

    public string? Reload() => _reload();

    /// <summary>
    /// The shipped bindings, written into the generated file as commented reference.
    /// </summary>
    /// <remarks>
    /// So the file a user opens with <c>:config</c> shows every default next to the
    /// <c>bindings:</c> section they are about to edit, rather than making them guess the
    /// syntax or go looking for documentation.
    /// </remarks>
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>
        DefaultBindingsForReference => Commands.DefaultBindings.All;
}
