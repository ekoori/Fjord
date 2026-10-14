using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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
                UseParentConsole();
                return Benchmark.RunAsync(directory).GetAwaiter().GetResult();

            // Lets a release script ship a copy of the documented default config without
            // launching a UI. The file is generated from the settings schema, so asking
            // the app for it is the only way to keep it from drifting.
            case ["--write-default-config", var path, ..]:
                UseParentConsole();
                return WriteDefaultConfig(path);

            case ["--version", ..] or ["-v", ..]:
                UseParentConsole();
                Console.WriteLine(Fjord.Core.Diagnostics.BuildInfo.Summary);
                return 0;

            case ["--config-path", ..]:
                UseParentConsole();
                Console.WriteLine(FjordPaths.Default.ConfigFile);
                return 0;
        }

        // A second launch hands its folders to the window already open and stops. This
        // is what makes Fjord usable as the system's file manager: every double-clicked
        // folder starts a process, and without this each one would be its own window.
        if (SingleInstance.TryForward(PathsIn(args)))
        {
            return 0;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>
    /// The arguments that are paths, in order.
    /// </summary>
    /// <remarks>
    /// Anything beginning with a dash is a switch, and a switch forwarded to a running
    /// window would be opened as a folder called "--help".
    /// </remarks>
    public static IReadOnlyList<string> PathsIn(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return [.. args.Where(a => a.Length > 0 && !a.StartsWith('-'))];
    }

    // Also used by the Avalonia visual designer; do not rename.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Borrows the console of whatever launched this, on Windows.
    /// </summary>
    /// <remarks>
    /// Fjord is a WinExe so that double-clicking it does not flash a console window. The
    /// cost is that it has no stdout at all: every one of the switches above wrote its
    /// answer into the void, and <c>--config-path</c> in a terminal printed nothing while
    /// returning success. Attaching to the parent gives them somewhere to write without
    /// giving the GUI a console it does not want.
    /// <para>
    /// The streams have to be reopened afterwards. <see cref="Console"/> caches a writer on
    /// first use bound to the handle that existed then, which for a WinExe is invalid.
    /// </para>
    /// </remarks>
    private static void UseParentConsole()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        AttachParentConsole();
    }

    [SupportedOSPlatform("windows")]
    private static void AttachParentConsole()
    {
        const uint Parent = 0xFFFFFFFF;

        if (!AttachConsole(Parent))
        {
            // Launched from Explorer rather than a terminal: there is no console to borrow
            // and nowhere for the output to go, which is not worth failing over.
            return;
        }

        try
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
        catch (IOException)
        {
            // The handle was attached and then closed under us. Nothing left to do.
        }
    }

    /// <remarks>
    /// DllImport rather than LibraryImport: the source generator requires
    /// <c>AllowUnsafeBlocks</c>, and turning unsafe code on across the UI project for one
    /// call that passes a single integer is a poor trade.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

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
