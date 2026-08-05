using Avalonia;
using Fjord.Config;

namespace Fjord.App;

public static class Program
{
    // Nothing here may touch Avalonia or anything relying on a SynchronizationContext
    // before AppMain runs.
    [STAThread]
    public static int Main(string[] args)
    {
        switch (args)
        {
            case ["--bench", var directory, ..]:
                return Benchmark.RunAsync(directory).GetAwaiter().GetResult();

            // Lets a release script ship a copy of the documented default config without
            // launching a UI. The file is generated from the settings schema, so asking
            // the app for it is the only way to keep it from drifting.
            case ["--write-default-config", var path, ..]:
                return WriteDefaultConfig(path);

            case ["--config-path", ..]:
                Console.WriteLine(FjordPaths.Default.ConfigFile);
                return 0;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    // Also used by the Avalonia visual designer; do not rename.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static int WriteDefaultConfig(string path)
    {
        try
        {
            ConfigService.WriteDefault(path);
            Console.WriteLine($"Wrote default config to {path}");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not write config: {ex.Message}");
            return 1;
        }
    }
}
