using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Fjord.App.Logging;
using Fjord.App.Views;
using Fjord.Config;
using Fjord.Core.FileSystem;
using Microsoft.Extensions.Logging;

namespace Fjord.App;

/// <summary>
/// The Avalonia application, assembled entirely in code.
/// </summary>
/// <remarks>
/// There is no <c>App.axaml</c> on purpose. Avalonia's generated XAML partial classes do
/// not satisfy <c>TreatWarningsAsErrors</c>, and the usual fix is to disable it for the
/// UI project — which is where UI code most benefits from it. With the list drawn
/// immediately and the chrome built in code, there is nothing markup would buy.
/// <para>
/// Dependency injection arrives with the command layer; until then this is the
/// composition root.
/// </para>
/// </remarks>
public sealed class App : Application
{
    private ILoggerFactory? _logging;

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var paths = FjordPaths.Default;

            // Writing the documented default on first run gives the user something to edit
            // and read rather than an empty file and a wiki page.
            TryWriteDefaultConfig(paths);

            var initial = ConfigFile.Load(paths.ConfigFile);

            _logging = FjordLog.Create(paths, initial.Config.Logging);
            var log = _logging.CreateLogger<App>();
            LogStartup(log, paths, initial);

            // Flushing matters less than it looks, since the provider flushes every record,
            // but the file handle should not outlive the app that owns it.
            desktop.Exit += (_, _) => _logging?.Dispose();

            desktop.MainWindow = new MainWindow(paths, initial, new DirectoryLister(), _logging);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void LogStartup(ILogger log, FjordPaths paths, ConfigLoadResult initial)
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "unknown";

        log.LogInformation(
            "Fjord {Version} starting on {Platform}, .NET {Runtime}",
            version,
            Environment.OSVersion.VersionString,
            Environment.Version);

        log.LogInformation(
            "config {Config}, data {Data}, cache {Cache}, logs {State}",
            paths.Config,
            paths.Data,
            paths.Cache,
            paths.State);

        if (initial.Error is { } error)
        {
            log.LogWarning("config could not be parsed, running on defaults: {Error}", error);
        }
    }

    private static void TryWriteDefaultConfig(FjordPaths paths)
    {
        try
        {
            ConfigService.WriteDefaultIfMissing(paths.ConfigFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only or unavailable config directory is survivable: defaults apply
            // and the app still runs.
        }
    }
}
