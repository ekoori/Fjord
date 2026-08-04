namespace Fjord.Core.Operations;

/// <summary>
/// How a command asks for file work to happen.
/// </summary>
/// <remarks>
/// The seam between <c>Fjord.Commands</c> and the queue, for the same reason
/// <c>IPane</c> exists: command bodies stay testable without a queue, a filesystem or a
/// window, and the commands themselves stay in the assembly the default-binding
/// resolution test can see.
/// </remarks>
public interface IOperationHost
{
    /// <summary>Queues work and returns immediately. Progress appears in the task panel.</summary>
    void Enqueue(IFileOperation operation);

    /// <summary>Reverses the most recent undoable operation.</summary>
    Task<string?> UndoAsync(CancellationToken ct = default);

    /// <summary>Re-runs the most recently undone operation.</summary>
    Task<string?> RedoAsync(CancellationToken ct = default);

    /// <summary>What <c>u</c> would reverse, or null.</summary>
    string? NextUndoDescription { get; }

    /// <summary>What redo would repeat, or null.</summary>
    string? NextRedoDescription { get; }

    /// <summary>Paths waiting to be pasted, and whether pasting should move them.</summary>
    ClipboardContents Clipboard { get; set; }
}

/// <summary>
/// What <c>p</c> will paste.
/// </summary>
/// <param name="Cut">
/// True when the paths were cut rather than yanked, so pasting moves instead of copying.
/// </param>
public readonly record struct ClipboardContents(IReadOnlyList<string> Paths, bool Cut)
{
    public static readonly ClipboardContents Empty = new([], false);

    public bool IsEmpty => Paths.Count == 0;
}
