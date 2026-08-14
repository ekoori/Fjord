using Fjord.Core.Diagnostics;
using Fjord.Core.Panes;
using Fjord.Core.Platform;

namespace Fjord.Commands;

/// <summary>
/// Making Fjord the thing that opens folders, and taking it back.
/// </summary>
/// <remarks>
/// One command that toggles, rather than a register/unregister pair. The state is a single
/// yes-or-no the user can see the answer to, and a pair would mean guessing which of the
/// two applies before pressing anything.
/// </remarks>
public sealed class DefaultHandlerCommands
{
    private readonly IFolderHandler _handler;
    private readonly IModeHost _host;
    private readonly Func<string> _executable;
    private readonly Func<bool>? _restartElevated;

    /// <param name="executable">
    /// The path to register. Supplied rather than read here so the app can name its own
    /// launcher, which for a single-file publish is not the assembly location.
    /// </param>
    /// <param name="restartElevated">
    /// Restarts Fjord with administrator rights, returning false when it could not be
    /// started — the user dismissing the prompt is a false, not an error. Null on a
    /// platform or a build where there is no such thing.
    /// </param>
    public DefaultHandlerCommands(
        IFolderHandler handler,
        IModeHost host,
        Func<string> executable,
        Func<bool>? restartElevated = null)
    {
        _handler = handler;
        _host = host;
        _executable = executable;
        _restartElevated = restartElevated;
    }

    /// <remarks>
    /// Current user only. Making Fjord the file manager for everyone on the machine needs
    /// administrator rights and changes what other people's accounts do, which is not a
    /// decision to take on their behalf from a status line.
    /// </remarks>
    [Command("default-file-manager", Aliases = ["set-default-file-manager"], Group = "Application",
        Description = "Toggle whether Fjord opens folders for this user")]
    public void Toggle(
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (!_handler.IsSupported)
        {
            messages.Error(_handler.Register(string.Empty).Detail);
            return;
        }

        if (_handler.IsDefault())
        {
            Report(messages, _handler.Unregister());
            return;
        }

        var result = _handler.Register(_executable());

        if (result.Outcome != HandlerOutcome.NeedsElevation)
        {
            Report(messages, result);
            return;
        }

        // Writing under the current user's own classes normally needs nothing at all, so
        // arriving here means something has locked the machine down. Elevating is the only
        // thing left to try, and it is not something to do to someone without asking.
        if (_restartElevated is null)
        {
            messages.Error($"{result.Detail} Fjord cannot ask for higher privileges here.");
            return;
        }

        _host.Ask(
            $"{result.Detail} Restart Fjord with administrator rights?",
            () =>
            {
                if (!_restartElevated())
                {
                    messages.Error("Fjord was not restarted.");
                }
            });
    }

    /// <remarks>
    /// Told rather than shown as a setting, because it is a fact about the system rather
    /// than about Fjord: something else may have taken it back since.
    /// </remarks>
    [Command("default-file-manager?", Aliases = ["default-file-manager-status"],
        Group = "Application", Description = "Say whether Fjord opens folders for this user")]
    public void Status(
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        messages.Info(!_handler.IsSupported
            ? "This system has no user-level folder handler to set."
            : _handler.IsDefault()
                ? "Fjord opens folders for this user."
                : "Folders open with the system default.");
    }

    private static void Report(IMessageSink messages, HandlerResult result) =>
        messages.Message(
            result.Succeeded ? MessageLevel.Info : MessageLevel.Error,
            result.Detail);
}
