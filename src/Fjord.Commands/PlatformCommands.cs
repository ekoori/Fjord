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

    /// <remarks>
    /// Separate from <c>open-with-default</c> rather than a flag on it, so a binding can
    /// reach either one directly: the whole point of asking is that you did not want the
    /// default this time.
    /// <para>
    /// On a folder it opens the system file manager instead. "Open with" on a folder is a
    /// dialog listing programs that cannot open one, and what someone pressing it on a
    /// folder wants is that folder in Explorer or Finder — the one thing Fjord, having
    /// possibly taken the association over, is no longer the way to get.
    /// </para>
    /// </remarks>
    [Command("open-with", Group = "Platform",
        Description = "Open the entry under the cursor with an application you choose, or a folder in the system file manager")]
    public async Task OpenWith(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(messages);

        if (pane.PathAt(pane.CursorIndex) is not { } path)
        {
            return;
        }

        try
        {
            if (IsDirectory(pane))
            {
                if (!await _platform.OpenInFileManagerAsync(path).ConfigureAwait(true))
                {
                    messages.Error("No system file manager to open this folder with.");
                }

                return;
            }

            if (!await _platform.OpenWithAsync(path).ConfigureAwait(true))
            {
                messages.Error("This system offers no \"open with\" dialog.");
            }
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

    /// <remarks>
    /// A listing rather than a line of names, because the answer to "where can I go" is
    /// something you pick from — and because a drive's label, capacity and free space are
    /// most of the reason for asking. Enter goes there like any other row.
    /// </remarks>
    [Command("roots", Aliases = ["drives"], Group = "Platform",
        Description = "Show drives and mounted volumes as a list")]
    public async Task Roots(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(messages);

        if (!await RootListing.ShowAsync(_platform, pane).ConfigureAwait(true))
        {
            messages.Warning("No roots found.");
        }
    }

    /// <summary>Whether the row under the cursor is a folder, as the listing already knows.</summary>
    private static bool IsDirectory(IPane pane)
    {
        var view = pane.Snapshot?.View ?? default;
        return pane.CursorIndex >= 0 && pane.CursorIndex < view.Count && view[pane.CursorIndex].IsDirectory;
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
