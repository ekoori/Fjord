using Fjord.Config;

namespace Fjord.Tests.Config;

public class FjordPathsTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] entries)
    {
        var map = entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        return key => map.GetValueOrDefault(key);
    }

    [Fact]
    public void WindowsUsesRoamingForConfigAndLocalForEverythingElse()
    {
        var paths = FjordPaths.For(
            PlatformKind.Windows,
            Env(("APPDATA", @"C:\U\AppData\Roaming"), ("LOCALAPPDATA", @"C:\U\AppData\Local")),
            @"C:\U");

        Assert.Equal(Path.Combine(@"C:\U\AppData\Roaming", "Fjord"), paths.Config);
        Assert.Equal(Path.Combine(@"C:\U\AppData\Local", "Fjord"), paths.Data);
        Assert.Equal(Path.Combine(@"C:\U\AppData\Local", "Fjord", "Cache"), paths.Cache);
    }

    [Fact]
    public void WindowsFallsBackWhenTheEnvironmentIsEmpty()
    {
        var paths = FjordPaths.For(PlatformKind.Windows, Env(), @"C:\U");

        Assert.Equal(Path.Combine(@"C:\U", "AppData", "Roaming", "Fjord"), paths.Config);
    }

    [Fact]
    public void LinuxHonoursXdgVariables()
    {
        var paths = FjordPaths.For(
            PlatformKind.Linux,
            Env(("XDG_CONFIG_HOME", "/x/cfg"), ("XDG_DATA_HOME", "/x/data"),
                ("XDG_CACHE_HOME", "/x/cache"), ("XDG_STATE_HOME", "/x/state")),
            "/home/u");

        Assert.Equal(Path.Combine("/x/cfg", "fjord"), paths.Config);
        Assert.Equal(Path.Combine("/x/data", "fjord"), paths.Data);
        Assert.Equal(Path.Combine("/x/cache", "fjord"), paths.Cache);
        Assert.Equal(Path.Combine("/x/state", "fjord"), paths.State);
    }

    [Fact]
    public void LinuxFallsBackToTheSpecDefaults()
    {
        var paths = FjordPaths.For(PlatformKind.Linux, Env(), "/home/u");

        Assert.Equal(Path.Combine("/home/u", ".config", "fjord"), paths.Config);
        Assert.Equal(Path.Combine("/home/u", ".local", "share", "fjord"), paths.Data);
        Assert.Equal(Path.Combine("/home/u", ".cache", "fjord"), paths.Cache);
        Assert.Equal(Path.Combine("/home/u", ".local", "state", "fjord"), paths.State);
    }

    [Fact]
    public void MacUsesApplicationSupportNotDotConfig()
    {
        // .NET maps SpecialFolder.ApplicationData to ~/.config on macOS too, which is why
        // noPDF ends up in the wrong place. Fjord follows the platform convention.
        var paths = FjordPaths.For(PlatformKind.MacOS, Env(), "/Users/u");

        Assert.Equal(Path.Combine("/Users/u", "Library", "Application Support", "Fjord"), paths.Config);
        Assert.Equal(Path.Combine("/Users/u", "Library", "Caches", "Fjord"), paths.Cache);
        Assert.Equal(Path.Combine("/Users/u", "Library", "Logs", "Fjord"), paths.State);
    }

    [Fact]
    public void MacIgnoresXdgVariables()
    {
        // XDG_CONFIG_HOME is often set on macOS by shell frameworks without meaning it.
        var paths = FjordPaths.For(PlatformKind.MacOS, Env(("XDG_CONFIG_HOME", "/x/cfg")), "/Users/u");

        Assert.DoesNotContain("/x/cfg", paths.Config, StringComparison.Ordinal);
    }

    [Fact]
    public void OverrideRedirectsConfigOnly()
    {
        // An isolated dev instance should not also fork the user's bookmarks and history.
        var paths = FjordPaths.For(
            PlatformKind.Linux, Env((FjordPaths.OverrideVariable, "/tmp/isolated")), "/home/u");

        Assert.Equal(Path.GetFullPath("/tmp/isolated"), paths.Config);
        Assert.Equal(Path.Combine("/home/u", ".local", "share", "fjord"), paths.Data);
    }

    [Fact]
    public void ConfigFileSitsInTheConfigDirectory()
    {
        var paths = FjordPaths.For(PlatformKind.Linux, Env(), "/home/u");

        Assert.Equal(Path.Combine(paths.Config, "config.yaml"), paths.ConfigFile);
    }

    [Fact]
    public void AskingWhereConfigLivesDoesNotCreateAnything()
    {
        // noPDF's ConfigPath getter calls CreateDirectory as a side effect of a read.
        var target = Path.Combine(Path.GetTempPath(), "fjord-tests", Guid.NewGuid().ToString("n"));
        var paths = FjordPaths.For(PlatformKind.Linux, Env((FjordPaths.OverrideVariable, target)), "/home/u");

        _ = paths.Config;
        _ = paths.ConfigFile;

        Assert.False(Directory.Exists(target));
    }
}
