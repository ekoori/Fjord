using Avalonia.Threading;
using Fjord.Commands;
using Fjord.Core.Operations;
using Fjord.Operations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

    public IFileOperation Extract(IReadOnlyList<string> members, string destinationDirectory) =>
        new ExtractOperation(members, destinationDirectory);

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
    private readonly ILogger _log;

    public OperationHost(ITrashService trash, ILogger? log = null)
    {
        _log = log ?? NullLogger.Instance;

        // Duplicate and paste both land in a directory that may already hold the name, and
        // refusing would make the common case fail. Renaming alongside is the only policy
        // that neither loses data nor stops the user.
        _queue = new OperationQueue(trash, _ => ConflictResolution.Rename);

        _queue.Completed += (_, operation) => Post(() => _undo.Push(operation));
        _queue.Changed += (_, handle) => Post(() =>
        {
            Record(handle);
            Changed?.Invoke(this, handle);
        });
    }

    /// <summary>Raised on the UI thread whenever an operation is queued, progresses or ends.</summary>
    public event EventHandler<OperationHandle>? Changed;

    /// <summary>Raised after work that may have altered the current directory.</summary>
    public event EventHandler? Settled;

    /// <summary>Raised on the UI thread when an operation ended in failure.</summary>
    public event EventHandler<(string Operation, string Reason)>? Failed;

    /// <summary>
    /// What to tell the user about a failure.
    /// </summary>
    /// <remarks>
    /// The base exception, because the interesting one is at the bottom: an aggregate or a
    /// wrapper names the machinery rather than the file. Windows does not say which process
    /// holds the handle in the message, so "in use by another program" is as far as this
    /// can honestly go without asking the OS a much harder question.
    /// </remarks>
    private static string Explain(Exception? error) =>
        error?.GetBaseException() is { } cause ? cause.Message : "no reason given";

    public Fjord.Core.Registers.RegisterFile Registers { get; } = new();

    /// <summary>
    /// Asks the question, or does not.
    /// </summary>
    /// <remarks>
    /// Supplied by the window, which owns the keyboard and reads the setting that decides
    /// whether a prompt appears. Unset means no prompt, which is what a test wants.
    /// </remarks>
    public Action<string, Action>? Confirmation { get; set; }

    /// <summary>Puts text on the system clipboard. Supplied by the window.</summary>
    public Func<string, Task>? ClipboardWriter { get; set; }

    public void Confirm(string question, Action confirmed)
    {
        ArgumentNullException.ThrowIfNull(confirmed);

        if (Confirmation is { } ask)
        {
            ask(question, confirmed);
        }
        else
        {
            confirmed();
        }
    }

    public Task SetClipboardTextAsync(string text) =>
        ClipboardWriter?.Invoke(text) ?? Task.CompletedTask;

    /// <summary>The unnamed register, which is what a bare yank and a bare paste use.</summary>
    public ClipboardContents Clipboard
    {
        get => Registers.Unnamed;
        set => Registers.Unnamed = value;
    }

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
    /// Writes one line per operation state change.
    /// </summary>
    /// <remarks>
    /// This is the log that matters: everything here moved or removed the user's files, and
    /// a failure that only ever appeared in the status bar for three seconds is the one they
    /// will ask about afterwards. Failures carry the exception; the rest are Information.
    /// </remarks>
    private void Record(OperationHandle handle)
    {
        switch (handle.State)
        {
            case OperationState.Running:
                _log.LogInformation("running {Operation}", handle.Operation.Description);
                break;

            case OperationState.Completed:
                _log.LogInformation("completed {Operation}", handle.Operation.Description);
                break;

            case OperationState.Cancelled:
                _log.LogInformation("cancelled {Operation}", handle.Operation.Description);
                break;

            case OperationState.Failed:
                _log.LogError(handle.Error, "failed {Operation}", handle.Operation.Description);

                // Said out loud, not only written to the log. A trash that could not touch
                // a file because something has it open finished as far as the queue is
                // concerned: the task panel empties, the entry is still there, and without
                // this the only evidence that anything went wrong is a file that did not
                // move.
                Failed?.Invoke(this, (handle.Operation.Description, Explain(handle.Error)));
                break;

            default:
                break;
        }
    }

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
