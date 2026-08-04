using Fjord.Core.Operations;
using Fjord.Operations;
using Fjord.Tests.FileSystem;

namespace Fjord.Tests.Operations;

/// <summary>An operation whose timing a test controls.</summary>
internal sealed class ControllableOperation : IFileOperation
{
    private readonly TaskCompletionSource _gate = new();

    public string Description { get; init; } = "test";

    public bool CanUndo { get; set; } = true;

    public bool Started { get; private set; }

    public bool Finished { get; private set; }

    public int UndoCount { get; private set; }

    public Exception? Throw { get; set; }

    /// <summary>Releases the operation to complete.</summary>
    public void Release() => _gate.TrySetResult();

    public async Task ExecuteAsync(IOperationContext context, CancellationToken ct)
    {
        Started = true;
        context.Report(new OperationProgress(0, 1, Description));

        await using (ct.Register(() => _gate.TrySetCanceled(ct)))
        {
            await _gate.Task.ConfigureAwait(false);
        }

        if (Throw is not null)
        {
            throw Throw;
        }

        Finished = true;
        context.Report(new OperationProgress(1, 1, null));
    }

    public Task UndoAsync(IOperationContext context, CancellationToken ct)
    {
        UndoCount++;
        return Task.CompletedTask;
    }
}

public class OperationQueueTests
{
    private static async Task<T> Eventually<T>(Func<T> read, Func<T, bool> until, string what)
    {
        // Polls rather than sleeping a fixed time: the queue runs on its own thread and a
        // fixed delay is either flaky or slow.
        for (var i = 0; i < 400; i++)
        {
            var value = read();
            if (until(value))
            {
                return value;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    private static OperationQueue Queue(TempDirectory temp) =>
        new(new FakeTrash(Path.Combine(temp.Path, ".trash")));

    [Fact]
    public async Task AnEnqueuedOperationRuns()
    {
        using var temp = new TempDirectory();
        await using var queue = Queue(temp);
        var operation = new ControllableOperation();

        var handle = queue.Enqueue(operation);
        await Eventually(() => operation.Started, s => s, "the operation to start");

        operation.Release();
        await Eventually(() => handle.State, s => s == OperationState.Completed, "completion");

        Assert.True(operation.Finished);
    }

    [Fact]
    public async Task OperationsRunOneAtATimeInOrder()
    {
        // Sequential on purpose: two concurrent bulk copies on one disk are slower than
        // one after the other, and they make progress reporting meaningless.
        using var temp = new TempDirectory();
        await using var queue = Queue(temp);

        var first = new ControllableOperation { Description = "first" };
        var second = new ControllableOperation { Description = "second" };

        queue.Enqueue(first);
        queue.Enqueue(second);

        await Eventually(() => first.Started, s => s, "the first to start");
        Assert.False(second.Started);

        first.Release();
        await Eventually(() => second.Started, s => s, "the second to start");
        second.Release();
    }

    [Fact]
    public async Task ActiveListsWhatIsQueuedAndRunning()
    {
        using var temp = new TempDirectory();
        await using var queue = Queue(temp);

        var first = new ControllableOperation();
        var second = new ControllableOperation();
        queue.Enqueue(first);
        queue.Enqueue(second);

        await Eventually(() => first.Started, s => s, "the first to start");
        Assert.Equal(2, queue.Active.Count);

        first.Release();
        second.Release();
        await Eventually(() => queue.Active.Count, c => c == 0, "the queue to drain");
    }

    [Fact]
    public async Task CancellingOneDoesNotDisturbTheRest()
    {
        using var temp = new TempDirectory();
        await using var queue = Queue(temp);

        var first = new ControllableOperation();
        var second = new ControllableOperation();
        var firstHandle = queue.Enqueue(first);
        queue.Enqueue(second);

        await Eventually(() => first.Started, s => s, "the first to start");
        firstHandle.Cancel();

        await Eventually(() => firstHandle.State, s => s == OperationState.Cancelled, "cancellation");
        await Eventually(() => second.Started, s => s, "the second to start");

        second.Release();
    }

    [Fact]
    public async Task AQueuedOperationCancelledBeforeItStartsNeverRuns()
    {
        using var temp = new TempDirectory();
        await using var queue = Queue(temp);

        var first = new ControllableOperation();
        var second = new ControllableOperation();
        queue.Enqueue(first);
        var secondHandle = queue.Enqueue(second);

        await Eventually(() => first.Started, s => s, "the first to start");
        secondHandle.Cancel();
        first.Release();

        await Eventually(() => secondHandle.State, s => s == OperationState.Cancelled, "cancellation");
        Assert.False(second.Started);
    }

    [Fact]
    public async Task AFailureIsRecordedRatherThanThrown()
    {
        // A failed operation must not take the worker down with it, or every later
        // operation silently stops running.
        using var temp = new TempDirectory();
        await using var queue = Queue(temp);

        var failing = new ControllableOperation { Throw = new IOException("disk full") };
        var handle = queue.Enqueue(failing);
        failing.Release();

        await Eventually(() => handle.State, s => s == OperationState.Failed, "failure");
        Assert.Equal("disk full", handle.Error?.Message);

        var after = new ControllableOperation();
        queue.Enqueue(after);
        after.Release();
        await Eventually(() => after.Finished, f => f, "the next operation to still run");
    }

    [Fact]
    public async Task CompletedIsRaisedForUndoableWork()
    {
        using var temp = new TempDirectory();
        await using var queue = Queue(temp);

        IFileOperation? completed = null;
        queue.Completed += (_, op) => completed = op;

        var operation = new ControllableOperation();
        queue.Enqueue(operation);
        operation.Release();

        await Eventually(() => completed, c => c is not null, "the completed event");
        Assert.Same(operation, completed);
    }

    [Fact]
    public async Task AFailedOperationIsStillOfferedForUndo()
    {
        // A failure part-way leaves real changes on disk; refusing to stack it would
        // leave the user no way to clean up.
        using var temp = new TempDirectory();
        await using var queue = Queue(temp);

        IFileOperation? completed = null;
        queue.Completed += (_, op) => completed = op;

        var failing = new ControllableOperation { Throw = new IOException("nope") };
        queue.Enqueue(failing);
        failing.Release();

        await Eventually(() => completed, c => c is not null, "the completed event");
    }

    [Fact]
    public async Task NonUndoableWorkIsNotOffered()
    {
        using var temp = new TempDirectory();
        await using var queue = Queue(temp);

        var raised = false;
        queue.Completed += (_, _) => raised = true;

        var operation = new ControllableOperation { CanUndo = false };
        var handle = queue.Enqueue(operation);
        operation.Release();

        await Eventually(() => handle.State, s => s == OperationState.Completed, "completion");
        Assert.False(raised);
    }

    [Fact]
    public async Task ProgressReachesSubscribers()
    {
        using var temp = new TempDirectory();
        await using var queue = Queue(temp);

        var seen = new List<OperationProgress>();
        queue.Changed += (_, h) => seen.Add(h.Progress);

        var operation = new ControllableOperation();
        var handle = queue.Enqueue(operation);
        operation.Release();

        await Eventually(() => handle.State, s => s == OperationState.Completed, "completion");
        Assert.Contains(seen, p => p.Fraction >= 1.0);
    }

    [Fact]
    public async Task TheQueueRunsRealOperationsEndToEnd()
    {
        using var temp = new TempDirectory();
        await using var queue = Queue(temp);

        var source = temp.CreateFile("a.txt");
        var target = temp.CreateDir("out");

        var handle = queue.Enqueue(new CopyOperation([source], target));
        await Eventually(() => handle.State, s => s == OperationState.Completed, "the copy");

        Assert.True(File.Exists(Path.Combine(target, "a.txt")));
    }
}

public class UndoStackTests
{
    private static (TempDirectory Temp, OperationQueue Queue) Setup()
    {
        var temp = new TempDirectory();
        return (temp, new OperationQueue(new FakeTrash(Path.Combine(temp.Path, ".trash"))));
    }

    [Fact]
    public async Task UndoReversesTheMostRecentOperation()
    {
        var (temp, queue) = Setup();
        using var _ = temp;
        await using var __ = queue;

        var stack = new UndoStack();
        var first = new ControllableOperation { Description = "first" };
        var second = new ControllableOperation { Description = "second" };
        stack.Push(first);
        stack.Push(second);

        var undone = await stack.UndoAsync(queue);

        Assert.Same(second, undone);
        Assert.Equal(1, second.UndoCount);
        Assert.Equal(0, first.UndoCount);
    }

    [Fact]
    public async Task UndoThenRedoRoundTrips()
    {
        var (temp, queue) = Setup();
        using var _ = temp;
        await using var __ = queue;

        var stack = new UndoStack();
        var operation = new ControllableOperation();
        stack.Push(operation);

        await stack.UndoAsync(queue);
        Assert.True(stack.CanRedo);
        Assert.False(stack.CanUndo);

        Assert.Same(operation, stack.TakeRedo());
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void NewWorkClearsTheRedoBranch()
    {
        // A redo branch that survives new work is a way to lose data, not recover it.
        var stack = new UndoStack();
        stack.Push(new ControllableOperation());

        _ = stack.UndoAsync(new OperationQueue(new UnusableTrash()));
        stack.Push(new ControllableOperation());

        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void NonUndoableOperationsAreNeverStacked()
    {
        var stack = new UndoStack();

        stack.Push(new ControllableOperation { CanUndo = false });

        Assert.False(stack.CanUndo);
    }

    [Fact]
    public void TheOldestEntriesFallOffAtDepth()
    {
        // An operation holds every path it touched; unbounded history is a slow leak.
        var stack = new UndoStack(depth: 2);
        stack.Push(new ControllableOperation { Description = "a" });
        stack.Push(new ControllableOperation { Description = "b" });
        stack.Push(new ControllableOperation { Description = "c" });

        Assert.Equal(["b", "c"], stack.History.Select(o => o.Description));
    }

    [Fact]
    public async Task UndoOnAnEmptyStackIsANoOp()
    {
        var (temp, queue) = Setup();
        using var _ = temp;
        await using var __ = queue;

        Assert.Null(await new UndoStack().UndoAsync(queue));
    }

    [Fact]
    public void TheNextUndoIsDescribedForTheStatusLine()
    {
        var stack = new UndoStack();
        stack.Push(new ControllableOperation { Description = "Move 3 items to trash" });

        Assert.Equal("Move 3 items to trash", stack.NextUndoDescription);
    }

    [Fact]
    public void DepthMustBePositive() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new UndoStack(0));

    private sealed class UnusableTrash : ITrashService
    {
        public bool IsSupported => false;

        public Task<string?> TrashAsync(string path, CancellationToken ct = default) =>
            throw new PlatformNotSupportedException();

        public Task<bool> RestoreAsync(string token, CancellationToken ct = default) =>
            Task.FromResult(false);
    }
}
