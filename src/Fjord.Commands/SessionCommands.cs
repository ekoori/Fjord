using Fjord.Core.Diagnostics;
using Fjord.Core.Storage;

namespace Fjord.Commands;

/// <summary>
/// Capturing and restoring what the window looked like.
/// </summary>
/// <remarks>
/// A session is deliberately just directories, cursors and selection — not the listings
/// themselves. Restoring re-reads, which is both simpler and more correct: the disk has
/// almost certainly changed since the session was saved, and a restored stale listing would
/// be a lie the user acts on.
/// </remarks>
public sealed class SessionCommands
{
    private readonly IStorage _storage;
    private readonly ISessionHost _host;

    public SessionCommands(IStorage storage, ISessionHost host)
    {
        _storage = storage;
        _host = host;
    }

    [Command("session-save", Group = "Session", MaxSplit = 0,
        Description = "Save the open tabs under a name")]
    public void SessionSave(
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages,
        [CommandArgument(Description = "Session name")] string name = "")
    {
        var chosen = Named(name);
        _storage.SaveSession(_host.Capture(chosen));

        messages.Info(chosen == SavedSession.DefaultName
            ? "Session saved."
            : $"Session '{chosen}' saved.");
    }

    [Command("session-load", Aliases = ["session"], Group = "Session", MaxSplit = 0,
        Description = "Restore a saved session")]
    public async Task SessionLoad(
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages,
        [CommandArgument(Description = "Session name")] string name = "")
    {
        var chosen = Named(name);

        if (_storage.LoadSession(chosen) is not { } session || session.Tabs.Count == 0)
        {
            messages.Warning($"No session '{chosen}'.");
            return;
        }

        await _host.RestoreAsync(session).ConfigureAwait(true);
        messages.Info($"Restored {session.Tabs.Count} tab{(session.Tabs.Count == 1 ? "" : "s")}.");
    }

    [Command("session-list", Group = "Session", Description = "List saved sessions")]
    public void SessionList([CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        // The automatic session is an implementation detail, not something to load by name.
        var names = _storage.SessionNames()
            .Where(n => n != SavedSession.DefaultName)
            .ToList();

        messages.Info(names.Count == 0
            ? "No saved sessions."
            : "Sessions: " + string.Join(", ", names));
    }

    [Command("session-delete", Group = "Session", MaxSplit = 0, Description = "Delete a saved session")]
    public void SessionDelete(
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages,
        string name)
    {
        messages.Info(_storage.DeleteSession(name)
            ? $"Deleted session '{name}'."
            : $"No session '{name}'.");
    }

    [Command("recent", Group = "Session", Description = "List recently visited directories")]
    public void Recent([CommandArgument(Value = CommandValue.Messages)] IMessageSink messages)
    {
        var recents = _storage.RecentDirectories(10);

        messages.Info(recents.Count == 0
            ? "No recent directories."
            : string.Join("  ", recents));
    }

    private static string Named(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length == 0 ? SavedSession.DefaultName : trimmed;
    }
}

/// <summary>
/// Reading and rewriting the window's tabs.
/// </summary>
/// <remarks>
/// The App implements this. Commands cannot open or close tabs themselves without knowing
/// how panes are built, and that knowledge belongs where the window lives.
/// </remarks>
public interface ISessionHost
{
    /// <summary>Describes the open tabs.</summary>
    SavedSession Capture(string name);

    /// <summary>Replaces the open tabs with the session's, and navigates each.</summary>
    Task RestoreAsync(SavedSession session);
}
