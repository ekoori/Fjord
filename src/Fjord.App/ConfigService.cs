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

    public IReadOnlyList<SettingInfo> Settings
    {
        get
        {
            var text = ReadOrEmpty();

            return [.. SettingsSchema.All.Select(s =>
                new SettingInfo(s.Name, s.Type, s.Doc, ConfigFile.GetScalar(text, s.Name)))];
        }
    }

    /// <summary>
    /// Validates a value, writes it into the file, and reloads.
    /// </summary>
    /// <remarks>
    /// Three failure modes worth distinguishing, because they need different things from
    /// the user: an unknown setting, a value the type will not take, and a key the file no
    /// longer contains. The last one is real — the file is the user's to edit, and deleting
    /// a line is a normal thing to do — so it is reported rather than silently appended
    /// somewhere the surrounding comments no longer describe it.
    /// </remarks>
    public string? Set(string name, string value)
    {
        if (SettingsSchema.Find(name) is not { } setting)
        {
            return $"unknown setting '{name}'";
        }

        if (SettingsSchema.Validate(setting, value, out var rendered) is { } problem)
        {
            return problem;
        }

        try
        {
            EnsureExists();

            var text = File.ReadAllText(ConfigFilePath);
            var updated = ConfigFile.SetScalar(text, name, rendered);

            if (ReferenceEquals(text, updated) || string.Equals(text, updated, StringComparison.Ordinal))
            {
                return $"'{name}' is not in {Path.GetFileName(ConfigFilePath)}; add it there first";
            }

            File.WriteAllText(ConfigFilePath, updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }

        // The watcher would get there eventually, but :set has to be synchronous from the
        // user's point of view: type it, see the list change.
        return _reload();
    }

    private string ReadOrEmpty()
    {
        try
        {
            return File.Exists(ConfigFilePath) ? File.ReadAllText(ConfigFilePath) : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
