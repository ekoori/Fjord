namespace Fjord.Core.Diagnostics;

public enum MessageLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Where a command reports to the user.
/// </summary>
/// <remarks>
/// Kept from QuteSharp's <c>IBrowserTab.cs</c> unchanged. Commands must not reach for a
/// dialog or a logger directly: a file manager reports in its status line, and a test
/// needs to assert what a command said without a UI.
/// </remarks>
public interface IMessageSink
{
    void Message(MessageLevel level, string text);

    void Info(string text) => Message(MessageLevel.Info, text);

    void Warning(string text) => Message(MessageLevel.Warning, text);

    void Error(string text) => Message(MessageLevel.Error, text);
}

/// <summary>Discards everything. For tests and headless runs.</summary>
public sealed class NullMessageSink : IMessageSink
{
    public static readonly NullMessageSink Instance = new();

    public void Message(MessageLevel level, string text)
    {
        // Intentionally empty.
    }
}

/// <summary>Records messages so a test can assert on them.</summary>
public sealed class RecordingMessageSink : IMessageSink
{
    private readonly List<(MessageLevel Level, string Text)> _messages = [];

    public IReadOnlyList<(MessageLevel Level, string Text)> Messages => _messages;

    public string? Last => _messages.Count > 0 ? _messages[^1].Text : null;

    public void Message(MessageLevel level, string text) => _messages.Add((level, text));

    public void Clear() => _messages.Clear();
}
