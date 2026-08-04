using Avalonia.Threading;
using Fjord.Commands;
using Fjord.Core.Operations;
using Fjord.Operations;

namespace Fjord.App;

/// <summary>Builds the concrete operations for <see cref="OperationCommands"/>.</summary>
public sealed class FileOperationFactory : IFileOperationFactory
{
    public IFileOperation Copy(IReadOnlyList<string> sources, string destinationDirectory) =>
        new CopyOperation(sources, destinationDirectory);

    public IFileOperation Move(IReadOnlyList<string> sources, string destinationDirectory) =>
        new MoveOperation(sources, destinationDirectory);

    /// <remarks>
    /// A copy into the directory the sources already live in. The rename conflict policy
    /// is what turns that into "report (2).pdf" rather than a refusal.
    /// </remarks>
    public IFileOperation Duplicate(IReadOnlyList<string> sources, string destinationDirectory) =>
        new CopyOperation(sources, destinationDirectory);

    public IFileOperation Rename(string path, string newName) => new RenameOperation(path, newName);

    public IFileOperation Trash(IReadOnlyList<string> paths) => new TrashOperation(paths);

    public IFileOperation Delete(IReadOnlyList<string> paths) => new DeleteOperation(paths);

    public IFileOperation Create(string path, bool directory) => new CreateOperation(path, directory);
}

/// <summary>
/// Connects the command layer to the queue and the undo stack.
/// </summary>
/// <remarks>
/// Also the place the UI thread is reintroduced: the queue raises its events from its own
/// worker, and everything that reaches a view has to be marshalled.
/// </remarks>
public sealed class OperationHost : IOperationHost, IAsyncDisposable
{
    private readonly OperationQueue _queue;
    private readonly UndoStack _undo = new();

    public OperationHost(ITrashService trash)
    {
        // Duplicate and paste both land in a directory that may already hold the name, and
        // refusing would make the common case fail. Renaming alongside is the only policy
        // that neither loses data nor stops the user.
        _queue = new OperationQueue(trash, _ => ConflictResolution.Rename);

        _queue.Completed += (_, operation) => Post(() => _undo.Push(operation));
        _queue.Changed += (_, handle) => Post(() => Changed?.Invoke(this, handle));
    }

    /// <summary>Raised on the UI thread whenever an operation is queued, progresses or ends.</summary>
    public event EventHandler<OperationHandle>? Changed;

    /// <summary>Raised after work that may have altered the current directory.</summary>
    public event EventHandler? Settled;

    public ClipboardContents Clipboard { get; set; } = ClipboardContents.Empty;

    public string? NextUndoDescription => _undo.NextUndoDescription;

    public string? NextRedoDescription => _undo.NextRedoDescription;

    public IReadOnlyList<OperationHandle> Active => _queue.Active;

    public void Enqueue(IFileOperation operation)
    {
        var handle = _queue.Enqueue(operation);
        WatchForSettle(handle);
    }

    public async Task<string?> UndoAsync(CancellationToken ct = default)
    {
        var description = _undo.NextUndoDescription;
        var operation = await _undo.UndoAsync(_queue, ct).ConfigureAwait(true);

        if (operation is null)
        {
            return null;
        }

        Settled?.Invoke(this, EventArgs.Empty);
        return description;
    }

    public Task<string?> RedoAsync(CancellationToken ct = default)
    {
        if (_undo.TakeRedo() is not { } operation)
        {
            return Task.FromResult<string?>(null);
        }

        Enqueue(operation);
        return Task.FromResult<string?>(operation.Description);
    }

    /// <summary>Asks every queued and running operation to stop.</summary>
    public void CancelAll()
    {
        foreach (var handle in _queue.Active)
        {
            handle.Cancel();
        }
    }

    public ValueTask DisposeAsync() => _queue.DisposeAsync();

    /// <summary>
    /// Refreshes the view once an operation stops changing the filesystem.
    /// </summary>
    /// <remarks>
    /// Filesystem watching is not in M1, so the listing has to be told. Refreshing on the
    /// operation ending — rather than per item — keeps a bulk copy from re-reading the
    /// directory hundreds of times.
    /// </remarks>
    private void WatchForSettle(OperationHandle handle)
    {
        void OnChanged(object? sender, OperationHandle changed)
        {
            if (!ReferenceEquals(changed, handle) || changed.State
                is OperationState.Queued or OperationState.Running)
            {
                return;
            }

            _queue.Changed -= OnChanged;
            Post(() => Settled?.Invoke(this, EventArgs.Empty));
        }

        _queue.Changed += OnChanged;
    }

    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
