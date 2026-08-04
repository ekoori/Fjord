using System.Threading.Channels;
using Fjord.Core.Operations;

namespace Fjord.Operations;

/// <summary>A queued or running operation, as the task panel sees it.</summary>
public sealed class OperationHandle
{
    private readonly CancellationTokenSource _cts = new();

    internal OperationHandle(int id, IFileOperation operation)
    {
        Id = id;
        Operation = operation;
    }

    public int Id { get; }

    public IFileOperation Operation { get; }

    public string Description => Operation.Description;

    public OperationState State { get; internal set; } = OperationState.Queued;

    public OperationProgress Progress { get; internal set; }

    /// <summary>Why it failed, or null.</summary>
    public Exception? Error { get; internal set; }

    internal CancellationToken Token => _cts.Token;

    /// <summary>Asks this operation to stop. A queued one never starts.</summary>
    public void Cancel() => _cts.Cancel();

    internal void Dispose() => _cts.Dispose();
}

/// <summary>
/// Runs file operations one at a time, off the UI thread.
/// </summary>
/// <remarks>
/// <b>Sequential on purpose.</b> The plan's bounded worker pool is right for metadata and
/// thumbnails, which are mostly waiting; bulk file I/O is not. Two concurrent copies on one
/// spinning disk are slower than one after the other, and on any disk they make progress
/// reporting meaningless and conflict prompts race. One queue, in order, is both faster and
/// comprehensible.
/// <para>
/// Cancellation is per operation, so cancelling a copy does not disturb what is queued
/// behind it.
/// </para>
/// </remarks>
public sealed class OperationQueue : IAsyncDisposable
{
    private readonly Channel<OperationHandle> _channel =
        Channel.CreateUnbounded<OperationHandle>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });

    private readonly List<OperationHandle> _active = [];
    private readonly Lock _activeLock = new();
    private readonly ITrashService _trash;
    private readonly Func<string, ConflictResolution> _conflictPolicy;
    private readonly Task _worker;

    private int _nextId;

    /// <param name="conflictPolicy">
    /// Consulted per conflicting destination. A prompt can replace this later without the
    /// operations knowing.
    /// </param>
    public OperationQueue(ITrashService trash, Func<string, ConflictResolution>? conflictPolicy = null)
    {
        _trash = trash;
        _conflictPolicy = conflictPolicy ?? (_ => ConflictResolution.Fail);
        _worker = Task.Run(RunAsync);
    }

    /// <summary>Raised whenever an operation is queued, progresses, or finishes.</summary>
    /// <remarks>
    /// Fired from the worker thread. The UI is responsible for marshalling; the queue does
    /// not know about a dispatcher.
    /// </remarks>
    public event EventHandler<OperationHandle>? Changed;

    /// <summary>Raised when an operation completed successfully and can be undone.</summary>
    public event EventHandler<IFileOperation>? Completed;

    /// <summary>Operations queued or running, oldest first.</summary>
    public IReadOnlyList<OperationHandle> Active
    {
        get
        {
            lock (_activeLock)
            {
                return _active.ToList();
            }
        }
    }

    public OperationHandle Enqueue(IFileOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var handle = new OperationHandle(Interlocked.Increment(ref _nextId), operation);

        lock (_activeLock)
        {
            _active.Add(handle);
        }

        Changed?.Invoke(this, handle);
        _channel.Writer.TryWrite(handle);
        return handle;
    }

    /// <summary>Runs an operation immediately, bypassing the queue. Used by undo.</summary>
    /// <remarks>
    /// Undo has to be synchronous from the user's point of view — pressing <c>u</c> then
    /// looking at the list must show the result — and it is small work by definition, since
    /// it reverses something that already happened.
    /// </remarks>
    public Task UndoAsync(IFileOperation operation, CancellationToken ct = default) =>
        operation.UndoAsync(new Context(this, null), ct);

    private async Task RunAsync()
    {
        await foreach (var handle in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (handle.Token.IsCancellationRequested)
            {
                Finish(handle, OperationState.Cancelled, null);
                continue;
            }

            handle.State = OperationState.Running;
            Changed?.Invoke(this, handle);

            try
            {
                await handle.Operation
                    .ExecuteAsync(new Context(this, handle), handle.Token)
                    .ConfigureAwait(false);

                Finish(handle, OperationState.Completed, null);

                if (handle.Operation.CanUndo)
                {
                    Completed?.Invoke(this, handle.Operation);
                }
            }
            catch (OperationCanceledException)
            {
                // Whatever was recorded before the cancel is still undoable, so a
                // half-finished copy can be cleaned up with a single u.
                Finish(handle, OperationState.Cancelled, null);

                if (handle.Operation.CanUndo)
                {
                    Completed?.Invoke(this, handle.Operation);
                }
            }
            catch (Exception ex) when (ex is IOException
                                           or UnauthorizedAccessException
                                           or PlatformNotSupportedException
                                           or NotSupportedException)
            {
                Finish(handle, OperationState.Failed, ex);

                // Same reasoning: a failure part-way leaves real changes on disk.
                if (handle.Operation.CanUndo)
                {
                    Completed?.Invoke(this, handle.Operation);
                }
            }
        }
    }

    private void Finish(OperationHandle handle, OperationState state, Exception? error)
    {
        handle.State = state;
        handle.Error = error;

        lock (_activeLock)
        {
            _active.Remove(handle);
        }

        Changed?.Invoke(this, handle);
        handle.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
    }

    private sealed class Context : IOperationContext
    {
        private readonly OperationQueue _queue;
        private readonly OperationHandle? _handle;

        public Context(OperationQueue queue, OperationHandle? handle)
        {
            _queue = queue;
            _handle = handle;
        }

        public ITrashService Trash => _queue._trash;

        public void Report(OperationProgress progress)
        {
            if (_handle is null)
            {
                return;
            }

            _handle.Progress = progress;
            _queue.Changed?.Invoke(_queue, _handle);
        }

        public ConflictResolution ResolveConflict(string destination) =>
            _queue._conflictPolicy(destination);
    }
}

/// <summary>
/// The undo and redo stacks.
/// </summary>
/// <remarks>
/// Bounded, because an operation holds the paths it touched and an unbounded history of
/// bulk operations is a slow leak. Redo is cleared by any new operation, as every editor
/// does — a redo branch that survives new work is a way to lose data, not recover it.
/// </remarks>
public sealed class UndoStack
{
    public const int DefaultDepth = 50;

    private readonly List<IFileOperation> _undo = [];
    private readonly List<IFileOperation> _redo = [];

    public UndoStack(int depth = DefaultDepth)
    {
        if (depth < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(depth), depth, "Depth must be at least 1.");
        }

        Depth = depth;
    }

    public int Depth { get; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public IReadOnlyList<IFileOperation> History => _undo;

    /// <summary>Describes what <c>u</c> would reverse, for confirmation and the status line.</summary>
    public string? NextUndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;

    public string? NextRedoDescription => _redo.Count > 0 ? _redo[^1].Description : null;

    public void Push(IFileOperation operation)
    {
        if (!operation.CanUndo)
        {
            return;
        }

        _undo.Add(operation);
        _redo.Clear();

        if (_undo.Count > Depth)
        {
            _undo.RemoveRange(0, _undo.Count - Depth);
        }
    }

    /// <summary>Reverses the most recent operation.</summary>
    public async Task<IFileOperation?> UndoAsync(OperationQueue queue, CancellationToken ct = default)
    {
        if (_undo.Count == 0)
        {
            return null;
        }

        var operation = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        await queue.UndoAsync(operation, ct).ConfigureAwait(false);
        _redo.Add(operation);
        return operation;
    }

    /// <summary>Re-runs the most recently undone operation.</summary>
    public IFileOperation? TakeRedo()
    {
        if (_redo.Count == 0)
        {
            return null;
        }

        var operation = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        return operation;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
