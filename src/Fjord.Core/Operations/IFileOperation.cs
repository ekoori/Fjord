namespace Fjord.Core.Operations;

/// <summary>What to do when a destination already exists.</summary>
public enum ConflictResolution
{
    /// <summary>Stop and report. The default, because the alternatives all lose data or surprise.</summary>
    Fail,

    /// <summary>Leave the destination alone and carry on with the rest.</summary>
    Skip,

    /// <summary>Replace the destination.</summary>
    Overwrite,

    /// <summary>Write alongside it as "name (2)".</summary>
    Rename,
}

public enum OperationState
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>How far along an operation is.</summary>
/// <param name="Current">Items finished so far.</param>
/// <param name="Total">Items expected, or 0 when not yet known.</param>
/// <param name="Item">What is being worked on right now, for the status line.</param>
public readonly record struct OperationProgress(int Current, int Total, string? Item)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Current / Total, 0, 1);
}

/// <summary>
/// One unit of work the user asked for, and the knowledge of how to take it back.
/// </summary>
/// <remarks>
/// Operations are objects rather than method calls so that the queue can report on them,
/// cancel them, and stack them for undo. An operation that reports
/// <see cref="CanUndo"/> must be able to restore the filesystem to what it was, including
/// after a partial failure — which is why each records what it actually did rather than
/// what it was asked to do.
/// </remarks>
public interface IFileOperation
{
    /// <summary>Short description for the task panel and the undo prompt.</summary>
    string Description { get; }

    /// <summary>
    /// Whether <see cref="UndoAsync"/> can restore what this changed.
    /// </summary>
    /// <remarks>
    /// False for permanent deletion, and false for anything that has not run yet or that
    /// failed before recording enough to reverse.
    /// </remarks>
    bool CanUndo { get; }

    Task ExecuteAsync(IOperationContext context, CancellationToken ct);

    Task UndoAsync(IOperationContext context, CancellationToken ct);
}

/// <summary>Services an operation needs while running.</summary>
public interface IOperationContext
{
    /// <summary>Reports progress. Called often; the queue coalesces before it reaches any UI.</summary>
    void Report(OperationProgress progress);

    /// <summary>
    /// Asks how to resolve a conflict.
    /// </summary>
    /// <remarks>
    /// Routed through the context rather than fixed on the operation so a future prompt can
    /// ask the user per item, and so tests can answer without one.
    /// </remarks>
    ConflictResolution ResolveConflict(string destination);

    /// <summary>Where to put files the user deleted.</summary>
    ITrashService Trash { get; }
}

/// <summary>Moving files to the platform's trash, and putting them back.</summary>
public interface ITrashService
{
    /// <summary>Whether this platform can trash rather than delete outright.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Moves a file or directory to the trash.
    /// </summary>
    /// <returns>
    /// A token that <see cref="RestoreAsync"/> understands, or null when the platform can
    /// trash but not restore. A null return still means the trash succeeded.
    /// </returns>
    Task<string?> TrashAsync(string path, CancellationToken ct = default);

    /// <summary>Puts a trashed item back where it came from.</summary>
    /// <returns>False when this platform cannot restore, or the item is gone.</returns>
    Task<bool> RestoreAsync(string token, CancellationToken ct = default);
}
