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

    public bool EnsureExists() => WriteDefaultIfMissing(ConfigFilePath);

    /// <summary>
    /// Writes the generated default file, if none exists.
    /// </summary>
    /// <remarks>
    /// Static and shared so every route to a fresh config — first run, the
    /// <c>--write-default-config</c> switch, and <c>:config</c> — produces the same file.
    /// They diverged once, and the switch silently emitted an empty bindings section.
    /// </remarks>
    public static bool WriteDefaultIfMissing(string path) =>
        ConfigFile.WriteDefaultIfMissing(
            path, Commands.DefaultBindings.All, Commands.CommandCatalogue.Descriptions);

    /// <summary>Writes the generated default file, replacing anything already there.</summary>
    public static void WriteDefault(string path) =>
        ConfigFile.WriteDefault(
            path, Commands.DefaultBindings.All, Commands.CommandCatalogue.Descriptions);

    public string? Reload() => _reload();
}
