using Fjord.Core.Config;
using Fjord.Core.Diagnostics;
using Fjord.Core.Input;
using Fjord.Core.Platform;

namespace Fjord.Commands;

/// <summary>Showing the command list and opening the configuration file.</summary>
public sealed class HelpCommands
{
    private readonly CommandRegistry _registry;
    private readonly KeyBindings _bindings;
    private readonly KeyModeRegistry _modes;
    private readonly IHelpView _view;
    private readonly IConfigService _config;
    private readonly IPlatformServices _platform;

    public HelpCommands(
        CommandRegistry registry,
        KeyBindings bindings,
        KeyModeRegistry modes,
        IHelpView view,
        IConfigService config,
        IPlatformServices platform)
    {
        _registry = registry;
        _bindings = bindings;
        _modes = modes;
        _view = view;
        _config = config;
        _platform = platform;
    }

    [Command("help", Aliases = ["h"], Group = "Help", MaxSplit = 0,
        Description = "Show the command list, or detail for one command")]
    public void Help(
        [CommandArgument(Description = "Command to describe")] string command = "")
    {
        var name = command.Trim().TrimStart(':');

        if (name.Length == 0)
        {
            _view.Show("Help", HelpText.Overview(_registry, _bindings, _modes));
            return;
        }

        if (_registry.Find(name) is not { } definition)
        {
            _view.Show("Help", HelpText.UnknownCommand(name, _registry));
            return;
        }

        _view.Show($"Help — :{definition.Name}", HelpText.ForCommand(definition, _bindings, _modes));
    }

    [Command("help-close", Group = "Help", Hidden = true, Description = "Close the help page")]
    public void HelpClose() => _view.Hide();

    /// <remarks>
    /// Opens the file in whatever the OS uses for YAML rather than editing in-app. Fjord
    /// already watches the file and hot-reloads it, so saving in that editor rebinds keys
    /// immediately — an in-app editor would be a worse version of a tool the user already
    /// has configured.
    /// </remarks>
    [Command("config", Aliases = ["config-edit"], Group = "Help",
        Description = "Open the configuration file in the default application")]
    public async Task Config([CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        var path = _config.ConfigFilePath;

        try
        {
            // A fresh install has no file yet; opening a missing path just makes the
            // editor complain. The generated default is documented and worth reading.
            var created = _config.EnsureExists();

            await _platform.OpenAsync(path).ConfigureAwait(true);

            messages.Info(created
                ? $"Created and opened {path} — saving applies changes immediately."
                : $"Opened {path} — saving applies changes immediately.");
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception
                                       or InvalidOperationException
                                       or PlatformNotSupportedException)
        {
            messages.Error($"Could not open {path}: {ex.Message}");
        }
    }

    [Command("config-path", Group = "Help", Description = "Show where the configuration file lives")]
    public void ConfigPath([CommandArgument(Value = CommandValue.Messages)] IMessageSink messages) =>
        messages.Info(_config.ConfigFilePath);

    /// <remarks>
    /// The watcher normally handles this. Kept for the cases it cannot see: an editor that
    /// writes through a path the watcher is not on, a file restored from elsewhere, or a
    /// watcher that failed to start at all.
    /// </remarks>
    [Command("config-reload", Group = "Help", Description = "Re-read the configuration file now")]
    public void ConfigReload([CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        var error = _config.Reload();

        messages.Message(
            error is null ? MessageLevel.Info : MessageLevel.Error,
            error is null ? "Configuration reloaded." : $"Configuration not reloaded: {error}");
    }
}
