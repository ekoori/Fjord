using Fjord.Core.Diagnostics;
using Fjord.Core.FileSystem;
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
    /// </remarks>
    [Command("open-with", Group = "Platform",
        Description = "Open the entry under the cursor with an application you choose")]
    public async Task OpenWith(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        if (pane.PathAt(pane.CursorIndex) is not { } path)
        {
            return;
        }

        try
        {
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

        var roots = _platform.GetRoots();

        if (roots.Count == 0)
        {
            messages.Warning("No roots found.");
            return;
        }

        // Keyed by path, because that is the entry name a virtual listing carries and what
        // Enter will navigate to.
        var described = roots.ToDictionary(
            r => r.Path,
            Describe,
            StringComparer.Ordinal);

        await pane.ShowListingAsync(
            "roots",
            [.. roots.Select(r => r.Path)],
            described)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// A root's label, size, free space and filesystem, as one line.
    /// </summary>
    /// <remarks>
    /// Free before total, because free is the number anyone reading this is looking for.
    /// A volume that reported nothing shows its label alone rather than zeroes, which would
    /// read as a full disk.
    /// </remarks>
    private static string Describe(PlatformRoot root)
    {
        if (root.Volume is not { TotalBytes: { } total, FreeBytes: { } free })
        {
            return root.Label;
        }

        var used = total == 0 ? 0 : (int)Math.Round(100.0 * (total - free) / total);
        var fileSystem = root.Volume.FileSystem is { Length: > 0 } fs ? $"  {fs}" : string.Empty;

        return $"{root.Label}   {SizeFormatter.Format(free)} free of "
            + $"{SizeFormatter.Format(total)}  ({used}% used){fileSystem}";
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
