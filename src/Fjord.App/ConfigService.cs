using Fjord.Config;
using Fjord.Core.Config;
using Fjord.Core.FileSystem;

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

    /// <summary>
    /// The listings currently in the file, in the order it lists them.
    /// </summary>
    /// <remarks>
    /// Read from the text rather than from the parsed config, because the order is what
    /// <c>:sort</c> cycles in and a deserialized dictionary has none worth relying on. The
    /// file's own order is also the one the user can see and rearrange.
    /// </remarks>
    public IReadOnlyList<string> ListingNames
    {
        get
        {
            var named = ConfigSections.SectionNames(ReadOrEmpty(), "listings");
            return named.Count > 0 ? named : [.. DefaultListings.All.Keys];
        }
    }

    public (SortSpec Sort, FilterSpec Filter)? Listing(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var configured = ConfigFile.Load(ConfigFilePath).Config.Listings;

        if (configured.TryGetValue(name, out var preset)
            || DefaultListings.All.TryGetValue(name, out preset))
        {
            return (preset.ToSortSpec(), preset.ToFilterSpec());
        }

        return null;
    }

    public string? SaveListing(string name, SortSpec sort, FilterSpec filter)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!IsValidListingName(name))
        {
            return $"'{name}' is not a listing name: one word of letters, digits, - or _.";
        }

        var preset = ListingPreset.From(sort, filter);

        var fields = new[]
        {
            $"sort_key: {preset.SortKey.ToString().ToLowerInvariant()}",
            $"sort_descending: {Yaml(preset.SortDescending)}",
            $"directories_first: {Yaml(preset.DirectoriesFirst)}",
            $"natural_sort: {Yaml(preset.NaturalSort)}",
            $"show_hidden: {Yaml(preset.ShowHidden)}",
        };

        return Rewrite(text => ConfigSections.SetSection(text, "listings", name, fields));
    }

    public string? RemoveListing(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (Listing(name) is null)
        {
            return $"No listing called '{name}'.";
        }

        return Rewrite(text => ConfigSections.RemoveSection(text, "listings", name));
    }

    /// <summary>
    /// Rewrites the file and applies it.
    /// </summary>
    /// <remarks>
    /// Every write goes through here so none of them can forget the reload. A change that
    /// only reaches the file is a change the user has to restart to see, which for
    /// something they just typed reads as the command having failed.
    /// </remarks>
    private string? Rewrite(Func<string, string> change)
    {
        try
        {
            EnsureExists();

            var text = File.ReadAllText(ConfigFilePath);
            var updated = change(text);

            if (string.Equals(text, updated, StringComparison.Ordinal))
            {
                return "Nothing changed.";
            }

            File.WriteAllText(ConfigFilePath, updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }

        return _reload();
    }

    /// <remarks>
    /// The same rule quickmarks use, for the same reason: these names are typed as
    /// arguments, so anything with a space or a separator in it would need quoting to be
    /// usable and would look like a path when it was not.
    /// </remarks>
    private static bool IsValidListingName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');

    private static string Yaml(bool value) => value ? "true" : "false";

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
