using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Fjord.App.Views;
using Fjord.Config;
using Fjord.Core.FileSystem;

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
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var paths = FjordPaths.Default;

            // Writing the documented default on first run gives the user something to edit
            // and read rather than an empty file and a wiki page.
            TryWriteDefaultConfig(paths);

            desktop.MainWindow = new MainWindow(
                paths, ConfigFile.Load(paths.ConfigFile), new DirectoryLister());
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void TryWriteDefaultConfig(FjordPaths paths)
    {
        try
        {
            ConfigFile.WriteDefaultIfMissing(paths.ConfigFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only or unavailable config directory is survivable: defaults apply
            // and the app still runs.
        }
    }
}
