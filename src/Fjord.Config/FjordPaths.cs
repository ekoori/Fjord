namespace Fjord.Config;

public enum PlatformKind
{
    Windows,
    Linux,
    MacOS,
}

/// <summary>
/// Where Fjord keeps configuration, data, cache and logs on each platform.
/// </summary>
/// <remarks>
/// noPDF uses <see cref="Environment.SpecialFolder.ApplicationData"/>, which .NET maps to
/// <c>~/.config</c> on both Linux and macOS — accidentally right on Linux, wrong on macOS —
/// and gives no separation between the four kinds of state. That separation starts
/// mattering the moment SQLite arrives: a database living in the config directory ends up
/// in dotfile backups and sync.
/// <para>
/// The platform and the environment are parameters rather than ambient reads so that all
/// three layouts can be tested from one machine.
/// </para>
/// </remarks>
public sealed class FjordPaths
{
    /// <summary>Overrides the config directory outright. Invaluable for tests and for an isolated dev instance.</summary>
    public const string OverrideVariable = "FJORD_CONFIG_DIR";

    private FjordPaths(string config, string data, string cache, string state)
    {
        Config = config;
        Data = data;
        Cache = cache;
        State = state;
    }

    public static FjordPaths Default { get; } = Create();

    /// <summary>Human-editable settings.</summary>
    public string Config { get; }

    /// <summary>Durable state worth backing up: bookmarks, sessions, scrapbooks.</summary>
    public string Data { get; }

    /// <summary>Regenerable state: thumbnails, parsed metadata.</summary>
    public string Cache { get; }

    /// <summary>Logs and other diagnostics.</summary>
    public string State { get; }

    public string ConfigFile => Path.Combine(Config, "config.yaml");

    public static FjordPaths Create() => For(CurrentPlatform(), Environment.GetEnvironmentVariable, Home());

    /// <summary>Builds the layout for a given platform, environment and home directory.</summary>
    public static FjordPaths For(PlatformKind platform, Func<string, string?> environment, string home)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(home);

        var paths = platform switch
        {
            PlatformKind.Windows => Windows(environment, home),
            PlatformKind.MacOS => MacOs(home),
            _ => Linux(environment, home),
        };

        // The override moves config only. Data, cache and logs stay where they belong, so
        // an isolated instance does not also fork the user's bookmarks.
        var overridden = environment(OverrideVariable);
        return string.IsNullOrWhiteSpace(overridden)
            ? paths
            : new FjordPaths(Path.GetFullPath(overridden), paths.Data, paths.Cache, paths.State);
    }

    /// <summary>
    /// Creates the directory holding a file, immediately before writing it.
    /// </summary>
    /// <remarks>
    /// Directories are created by writers, never by a property getter. noPDF's
    /// <c>ConfigPath</c> calls <c>CreateDirectory</c> as a side effect of a property read,
    /// which means merely asking where the config lives creates it.
    /// </remarks>
    public static void EnsureDirectoryFor(string file)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(file));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static FjordPaths Windows(Func<string, string?> environment, string home)
    {
        var roaming = Fallback(environment("APPDATA"), Path.Combine(home, "AppData", "Roaming"));
        var local = Fallback(environment("LOCALAPPDATA"), Path.Combine(home, "AppData", "Local"));

        return new FjordPaths(
            Path.Combine(roaming, "Fjord"),
            Path.Combine(local, "Fjord"),
            Path.Combine(local, "Fjord", "Cache"),
            Path.Combine(local, "Fjord", "Logs"));
    }

    private static FjordPaths Linux(Func<string, string?> environment, string home) =>
        new(
            Path.Combine(Fallback(environment("XDG_CONFIG_HOME"), Path.Combine(home, ".config")), "fjord"),
            Path.Combine(Fallback(environment("XDG_DATA_HOME"), Path.Combine(home, ".local", "share")), "fjord"),
            Path.Combine(Fallback(environment("XDG_CACHE_HOME"), Path.Combine(home, ".cache")), "fjord"),
            Path.Combine(Fallback(environment("XDG_STATE_HOME"), Path.Combine(home, ".local", "state")), "fjord"));

    private static FjordPaths MacOs(string home)
    {
        var support = Path.Combine(home, "Library", "Application Support", "Fjord");

        return new FjordPaths(
            support,
            support,
            Path.Combine(home, "Library", "Caches", "Fjord"),
            Path.Combine(home, "Library", "Logs", "Fjord"));
    }

    private static string Fallback(string? value, string ifMissing) =>
        string.IsNullOrWhiteSpace(value) ? ifMissing : value;

    private static PlatformKind CurrentPlatform() =>
        OperatingSystem.IsWindows() ? PlatformKind.Windows
        : OperatingSystem.IsMacOS() ? PlatformKind.MacOS
        : PlatformKind.Linux;

    private static string Home() =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
}
