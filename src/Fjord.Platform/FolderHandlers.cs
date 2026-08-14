using System.Runtime.Versioning;
using Fjord.Core.Platform;
using Microsoft.Win32;

namespace Fjord.Platform;

/// <summary>Picks the folder-handler registration for the running system.</summary>
public static class FolderHandlers
{
    public static IFolderHandler Create() => OperatingSystem.IsWindows()
        ? new WindowsFolderHandler()
        : OperatingSystem.IsLinux()
            ? new FreedesktopFolderHandler()
            : new UnsupportedFolderHandler();
}

/// <summary>For a platform with no user-level way to claim folders.</summary>
/// <remarks>
/// macOS is the one that matters. Its association database is keyed by bundle identifier
/// through <c>LSSetDefaultRoleHandlerForContentType</c>, and Fjord ships as a bare
/// executable with no <c>.app</c> bundle to identify. Saying so is better than writing
/// something that appears to work and does not.
/// </remarks>
internal sealed class UnsupportedFolderHandler : IFolderHandler
{
    public bool IsSupported => false;

    public bool IsDefault() => false;

    public HandlerResult Register(string executable) => Unavailable();

    public HandlerResult Unregister() => Unavailable();

    private static HandlerResult Unavailable() => new(
        HandlerOutcome.Unsupported,
        OperatingSystem.IsMacOS()
            ? "macOS needs an application bundle to register a folder handler; Fjord has none yet."
            : "This system offers no way to set a folder handler for one user.");
}

/// <summary>
/// Claims folders through the current user's shell associations.
/// </summary>
/// <remarks>
/// Writes under <c>HKCU\Software\Classes</c>, which the shell consults before
/// <c>HKLM</c> and which the user owns — so this needs no administrator rights at all,
/// and refuses rather than asks when something has locked it down.
/// <para>
/// Three classes, because the shell uses different ones depending on what was clicked:
/// <c>Directory</c> for an ordinary folder, <c>Drive</c> for a drive in This PC, and
/// <c>Folder</c> for the shell's abstract idea of one. Registering only Directory works
/// until the first double-clicked drive opens Explorer instead, which reads as the setting
/// having half worked.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsFolderHandler : IFolderHandler
{
    /// <summary>The classes the shell resolves a double-clicked folder through.</summary>
    private static readonly string[] Classes = ["Directory", "Drive", "Folder"];

    /// <summary>Where the previous command is kept, so unregistering can put it back.</summary>
    private const string BackupValue = "FjordPreviousCommand";

    public bool IsSupported => true;

    public bool IsDefault()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(CommandPath("Directory"));
            return key?.GetValue(null) is string command && LooksLikeFjord(command);
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return false;
        }
    }

    public HandlerResult Register(string executable)
    {
        ArgumentNullException.ThrowIfNull(executable);

        // Quoted, and "%1" quoted too: a folder path with a space in it is the common case,
        // not the edge one, and an unquoted %1 hands the shell two arguments.
        var command = $"\"{executable}\" \"%1\"";

        try
        {
            foreach (var className in Classes)
            {
                using var key = Registry.CurrentUser.CreateSubKey(CommandPath(className));

                if (key is null)
                {
                    return Denied();
                }

                // Remember what was there once, and only once: registering twice must not
                // overwrite the real previous command with Fjord's own.
                if (key.GetValue(null) is string existing
                    && !LooksLikeFjord(existing)
                    && key.GetValue(BackupValue) is null)
                {
                    key.SetValue(BackupValue, existing);
                }

                key.SetValue(null, command);

                // Explorer's own entry carries a DDE conversation that predates all of
                // this. Left in place, the shell tries to talk to a DDE server Fjord does
                // not run, waits for it, and then opens Explorer anyway.
                RemoveDde(className);
            }

            return new HandlerResult(
                HandlerOutcome.Done,
                "Fjord now opens folders for this user. Run :default-file-manager again to undo it.");
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return Denied();
        }
    }

    public HandlerResult Unregister()
    {
        try
        {
            foreach (var className in Classes)
            {
                using var key = Registry.CurrentUser.OpenSubKey(CommandPath(className), writable: true);

                if (key is null)
                {
                    continue;
                }

                if (key.GetValue(BackupValue) is string previous)
                {
                    key.SetValue(null, previous);
                    key.DeleteValue(BackupValue, throwOnMissingValue: false);
                }
                else
                {
                    // Nothing was there before Fjord, so removing the value is what puts
                    // the user back where they started: the shell falls through to the
                    // machine-wide association, which is Explorer.
                    key.DeleteValue(string.Empty, throwOnMissingValue: false);
                }
            }

            return new HandlerResult(HandlerOutcome.Done, "Folders open with the system default again.");
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return Denied();
        }
    }

    private static void RemoveDde(string className)
    {
        try
        {
            using var open = Registry.CurrentUser.OpenSubKey(
                $@"Software\Classes\{className}\shell\open", writable: true);

            open?.DeleteSubKeyTree("ddeexec", throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            // The association still works without this; it only costs a pause.
        }
    }

    private static string CommandPath(string className) =>
        $@"Software\Classes\{className}\shell\open\command";

    private static bool LooksLikeFjord(string command) =>
        command.Contains("Fjord", StringComparison.OrdinalIgnoreCase);

    private static HandlerResult Denied() => new(
        HandlerOutcome.NeedsElevation,
        "The registry refused the change.");

    private static bool IsRegistryFailure(Exception ex) =>
        ex is UnauthorizedAccessException or System.Security.SecurityException or IOException;
}

/// <summary>
/// Claims folders through the freedesktop MIME database.
/// </summary>
/// <remarks>
/// Two steps, both under the user's own directory and neither needing root: a
/// <c>.desktop</c> file describing Fjord, and an entry in <c>mimeapps.list</c> naming it as
/// the handler for <c>inode/directory</c>. Written directly rather than shelled out to
/// <c>xdg-mime</c>, which is a shell script that may not be installed and which does
/// exactly this.
/// </remarks>
internal sealed class FreedesktopFolderHandler : IFolderHandler
{
    private const string DesktopFile = "fjord.desktop";
    private const string DirectoryType = "inode/directory";

    private readonly string _configHome;
    private readonly string _dataHome;

    /// <param name="configHome">Where mimeapps.list lives. Defaults to the XDG location.</param>
    /// <param name="dataHome">Where the .desktop file goes. Defaults to the XDG location.</param>
    /// <remarks>
    /// Taken as parameters so a test can point the whole thing at a temporary directory.
    /// Reading the environment inside the methods would mean a test either changed the
    /// process's real XDG variables — which every other test then sees — or could not run
    /// at all.
    /// </remarks>
    public FreedesktopFolderHandler(string? configHome = null, string? dataHome = null)
    {
        _configHome = configHome ?? DefaultConfigHome();
        _dataHome = dataHome ?? DefaultDataHome();
    }

    public bool IsSupported => true;

    public bool IsDefault()
    {
        try
        {
            return File.Exists(MimeAppsPath())
                && File.ReadLines(MimeAppsPath()).Any(
                    l => l.StartsWith(DirectoryType, StringComparison.Ordinal)
                        && l.Contains(DesktopFile, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public HandlerResult Register(string executable)
    {
        ArgumentNullException.ThrowIfNull(executable);

        try
        {
            var applications = Path.Combine(_dataHome, "applications");
            Directory.CreateDirectory(applications);

            File.WriteAllText(
                Path.Combine(applications, DesktopFile),
                $"""
                [Desktop Entry]
                Type=Application
                Name=Fjord
                Comment=Keyboard-first file manager
                Exec={executable} %f
                Terminal=false
                NoDisplay=false
                Categories=System;FileTools;FileManager;
                MimeType={DirectoryType};

                """);

            SetDefault();

            return new HandlerResult(
                HandlerOutcome.NeedsRestart,
                "Fjord is registered as the folder handler. Some desktops only notice after a re-login.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HandlerResult(HandlerOutcome.NeedsElevation, ex.Message);
        }
    }

    public HandlerResult Unregister()
    {
        try
        {
            var desktop = Path.Combine(_dataHome, "applications", DesktopFile);

            if (File.Exists(desktop))
            {
                File.Delete(desktop);
            }

            RemoveDefault();

            return new HandlerResult(HandlerOutcome.Done, "Folders open with the system default again.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HandlerResult(HandlerOutcome.NeedsElevation, ex.Message);
        }
    }

    /// <remarks>
    /// Rewritten line by line rather than parsed as an ini file. The file belongs to the
    /// desktop environment and holds every other association the user has made; a
    /// round-trip through a parser that did not understand a section would drop them.
    /// </remarks>
    private void SetDefault()
    {
        var path = MimeAppsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var section = lines.FindIndex(l => l.Trim() == "[Default Applications]");

        if (section < 0)
        {
            lines.Add("[Default Applications]");
            section = lines.Count - 1;
        }

        var existing = lines.FindIndex(
            section, l => l.StartsWith(DirectoryType + "=", StringComparison.Ordinal));

        var entry = $"{DirectoryType}={DesktopFile}";

        if (existing >= 0)
        {
            lines[existing] = entry;
        }
        else
        {
            lines.Insert(section + 1, entry);
        }

        File.WriteAllLines(path, lines);
    }

    private void RemoveDefault()
    {
        var path = MimeAppsPath();

        if (!File.Exists(path))
        {
            return;
        }

        File.WriteAllLines(
            path,
            File.ReadAllLines(path).Where(
                l => !(l.StartsWith(DirectoryType + "=", StringComparison.Ordinal)
                    && l.Contains(DesktopFile, StringComparison.Ordinal))));
    }

    private string MimeAppsPath() => Path.Combine(_configHome, "mimeapps.list");

    private static string DefaultConfigHome() =>
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Home(), ".config");

    private static string DefaultDataHome() =>
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Home(), ".local", "share");

    private static string Home() =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
