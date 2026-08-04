using Fjord.Core.Diagnostics;
using Fjord.Core.Panes;
using Fjord.Core.Platform;

namespace Fjord.Commands;

/// <summary>Commands that hand a path to the operating system.</summary>
public sealed class PlatformCommands
{
    private readonly IPlatformServices _platform;

    public PlatformCommands(IPlatformServices platform) => _platform = platform;

    [Command("open-with-default", Aliases = ["xdg-open"], Group = "Platform",
        Description = "Open the entry under the cursor with its default application")]
    public async Task OpenWithDefault(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        if (pane.PathAt(pane.CursorIndex) is not { } path)
        {
            return;
        }

        try
        {
            await _platform.OpenAsync(path).ConfigureAwait(true);
        }
        catch (Exception ex) when (IsLaunchFailure(ex))
        {
            messages.Error($"Could not open {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    [Command("reveal", Group = "Platform",
        Description = "Show the entry under the cursor in the system file manager")]
    public async Task Reveal(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        var path = pane.PathAt(pane.CursorIndex) ?? pane.Directory;
        if (path.Length == 0)
        {
            return;
        }

        try
        {
            await _platform.RevealAsync(path).ConfigureAwait(true);
        }
        catch (Exception ex) when (IsLaunchFailure(ex))
        {
            messages.Error($"Could not reveal {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    [Command("go-home", Group = "Navigation", Description = "Go to the home directory")]
    public Task GoHome([CommandArgument(Value = CommandValue.Pane)] IPane pane) =>
        pane.NavigateAsync(_platform.HomeDirectory);

    [Command("roots", Group = "Platform", Description = "List drives and mounted volumes")]
    public void Roots([CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        var roots = _platform.GetRoots();
        messages.Info(roots.Count == 0
            ? "No roots found."
            : string.Join("  ", roots.Select(r => r.Label)));
    }

    /// <summary>
    /// Whether an exception came from failing to launch, rather than being a bug.
    /// </summary>
    /// <remarks>
    /// A missing handler, a path that no longer exists or a blocked launch are all things
    /// the user should be told about; anything else should surface as a real failure.
    /// </remarks>
    private static bool IsLaunchFailure(Exception ex) =>
        ex is System.ComponentModel.Win32Exception
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException;
}
