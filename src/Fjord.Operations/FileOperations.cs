using Fjord.Core.Operations;

namespace Fjord.Operations;

/// <summary>Shared plumbing: source list, conflict handling, progress, and an undo record.</summary>
public abstract class FileOperationBase : IFileOperation
{
    /// <summary>
    /// What actually happened, in the order it happened.
    /// </summary>
    /// <remarks>
    /// Undo walks this in reverse, so a partial failure still reverses cleanly: whatever
    /// got as far as being recorded is exactly what needs undoing. Recording what was
    /// <i>done</i> rather than what was <i>asked for</i> is the whole reason undo survives
    /// skipped conflicts and cancellation.
    /// </remarks>
    protected List<(string From, string To)> Performed { get; } = [];

    public abstract string Description { get; }

    public virtual bool CanUndo => Performed.Count > 0;

    /// <summary>Paths this operation produced, for selecting them afterwards.</summary>
    public IReadOnlyList<string> Results => Performed.Select(p => p.To).ToList();

    public abstract Task ExecuteAsync(IOperationContext context, CancellationToken ct);

    public abstract Task UndoAsync(IOperationContext context, CancellationToken ct);

    /// <summary>
    /// Resolves a destination against the conflict policy.
    /// </summary>
    /// <returns>The path to write, or null when this item should be skipped.</returns>
    protected static string? ResolveDestination(string destination, IOperationContext context)
    {
        if (!UniquePath.Exists(destination))
        {
            return destination;
        }

        return context.ResolveConflict(destination) switch
        {
            ConflictResolution.Skip => null,
            ConflictResolution.Rename => UniquePath.For(destination),
            ConflictResolution.Overwrite => destination,
            _ => throw new IOException($"'{Path.GetFileName(destination)}' already exists."),
        };
    }

    /// <summary>
    /// Copies one item, removing whatever it wrote if it does not finish.
    /// </summary>
    /// <remarks>
    /// A cancelled or failed copy has already created the destination and written part of
    /// it, and the caller never gets as far as recording it — so undo would not clean it
    /// up and the user is left with a truncated file that looks like a real one. For a file
    /// manager that is a data-loss bug, not untidiness, so the partial write is removed
    /// here where it is still known about.
    /// </remarks>
    protected static async Task CopyItemAsync(string source, string destination, CancellationToken ct)
    {
        try
        {
            await CopyRecursiveAsync(source, destination, ct).ConfigureAwait(false);
        }
        catch
        {
            TryDiscard(destination);
            throw;
        }
    }

    /// <summary>Best-effort cleanup of a partial write; the original failure must survive.</summary>
    private static void TryDiscard(string path)
    {
        try
        {
            DeleteRecursive(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing useful to do: reporting this instead of the real failure would
            // describe the wrong problem.
        }
    }

    /// <summary>Copies a file or a whole directory tree, honouring cancellation throughout.</summary>
    protected static async Task CopyRecursiveAsync(string source, string destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (File.Exists(source))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            // Streamed rather than File.Copy so a large file can be cancelled part-way
            // instead of blocking until it finishes.
            await using var input = new FileStream(
                source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await using var output = new FileStream(
                destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            await input.CopyToAsync(output, ct).ConfigureAwait(false);
            return;
        }

        if (!Directory.Exists(source))
        {
            throw new FileNotFoundException($"'{source}' no longer exists.", source);
        }

        Directory.CreateDirectory(destination);

        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            ct.ThrowIfCancellationRequested();
            await CopyRecursiveAsync(entry, Path.Combine(destination, Path.GetFileName(entry)), ct)
                .ConfigureAwait(false);
        }
    }

    protected static void DeleteRecursive(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        else if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <summary>Guards against putting a directory inside itself.</summary>
    protected static void RejectSelfContainment(string source, string destination)
    {
        if (Directory.Exists(source) &&
            UniquePath.IsInside(destination, source, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                $"Cannot copy or move '{Path.GetFileName(source)}' into itself.");
        }
    }
}

/// <summary>Copies entries into a directory.</summary>
public sealed class CopyOperation : FileOperationBase
{
    private readonly IReadOnlyList<string> _sources;
    private readonly string _destinationDirectory;

    public CopyOperation(IReadOnlyList<string> sources, string destinationDirectory)
    {
        _sources = sources;
        _destinationDirectory = destinationDirectory;
    }

    public override string Description =>
        $"Copy {_sources.Count} item{(_sources.Count == 1 ? "" : "s")} to {Path.GetFileName(_destinationDirectory)}";

    public override async Task ExecuteAsync(IOperationContext context, CancellationToken ct)
    {
        Directory.CreateDirectory(_destinationDirectory);

        for (var i = 0; i < _sources.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var source = _sources[i];
            context.Report(new OperationProgress(i, _sources.Count, Path.GetFileName(source)));

            var wanted = Path.Combine(_destinationDirectory, Path.GetFileName(source));
            RejectSelfContainment(source, wanted);

            if (ResolveDestination(wanted, context) is not { } destination)
            {
                continue;
            }

            // Overwriting means the destination already exists; removing it first keeps
            // the copy from merging two directory trees together silently.
            if (UniquePath.Exists(destination))
            {
                DeleteRecursive(destination);
            }

            await CopyItemAsync(source, destination, ct).ConfigureAwait(false);
            Performed.Add((source, destination));
        }

        context.Report(new OperationProgress(_sources.Count, _sources.Count, null));
    }

    /// <remarks>Undoing a copy removes what the copy created; the sources were never touched.</remarks>
    public override Task UndoAsync(IOperationContext context, CancellationToken ct)
    {
        for (var i = Performed.Count - 1; i >= 0; i--)
        {
            ct.ThrowIfCancellationRequested();
            DeleteRecursive(Performed[i].To);
        }

        Performed.Clear();
        return Task.CompletedTask;
    }
}

/// <summary>Moves entries into a directory.</summary>
public sealed class MoveOperation : FileOperationBase
{
    private readonly IReadOnlyList<string> _sources;
    private readonly string _destinationDirectory;

    public MoveOperation(IReadOnlyList<string> sources, string destinationDirectory)
    {
        _sources = sources;
        _destinationDirectory = destinationDirectory;
    }

    public override string Description =>
        $"Move {_sources.Count} item{(_sources.Count == 1 ? "" : "s")} to {Path.GetFileName(_destinationDirectory)}";

    public override async Task ExecuteAsync(IOperationContext context, CancellationToken ct)
    {
        Directory.CreateDirectory(_destinationDirectory);

        for (var i = 0; i < _sources.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var source = _sources[i];
            context.Report(new OperationProgress(i, _sources.Count, Path.GetFileName(source)));

            var wanted = Path.Combine(_destinationDirectory, Path.GetFileName(source));
            RejectSelfContainment(source, wanted);

            if (ResolveDestination(wanted, context) is not { } destination)
            {
                continue;
            }

            await MoveAsync(source, destination, ct).ConfigureAwait(false);
            Performed.Add((source, destination));
        }

        context.Report(new OperationProgress(_sources.Count, _sources.Count, null));
    }

    /// <remarks>
    /// <see cref="Directory.Move"/> and <see cref="File.Move(string, string)"/> are a rename when source
    /// and destination share a volume, and fail with <c>IOException</c> when they do not.
    /// Falling back to copy-then-delete is what makes moving across drives work at all.
    /// </remarks>
    private static async Task MoveAsync(string source, string destination, CancellationToken ct)
    {
        try
        {
            if (File.Exists(source))
            {
                File.Move(source, destination, overwrite: true);
            }
            else
            {
                if (Directory.Exists(destination))
                {
                    Directory.Delete(destination, recursive: true);
                }

                Directory.Move(source, destination);
            }

            return;
        }
        catch (IOException)
        {
            // Falls through to copy-and-delete below.
        }

        await CopyItemAsync(source, destination, ct).ConfigureAwait(false);

        // Only after the copy has fully succeeded, so a cancellation part-way through
        // leaves the source intact rather than losing it.
        ct.ThrowIfCancellationRequested();
        DeleteRecursive(source);
    }

    public override async Task UndoAsync(IOperationContext context, CancellationToken ct)
    {
        for (var i = Performed.Count - 1; i >= 0; i--)
        {
            ct.ThrowIfCancellationRequested();
            var (from, to) = Performed[i];
            await MoveAsync(to, from, ct).ConfigureAwait(false);
        }

        Performed.Clear();
    }
}

/// <summary>Renames a single entry in place.</summary>
public sealed class RenameOperation : FileOperationBase
{
    private readonly string _path;
    private readonly string _newName;

    public RenameOperation(string path, string newName)
    {
        _path = path;
        _newName = newName;
    }

    public override string Description => $"Rename {Path.GetFileName(_path)} to {_newName}";

    public override Task ExecuteAsync(IOperationContext context, CancellationToken ct)
    {
        if (_newName.Length == 0 || _newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new IOException($"'{_newName}' is not a valid file name.");
        }

        var destination = Path.Combine(Path.GetDirectoryName(_path)!, _newName);

        // A case-only rename on a case-insensitive filesystem: the destination "exists"
        // because it is the same file, so the conflict check would refuse a legitimate
        // rename of "readme" to "README".
        var caseOnly = string.Equals(_path, destination, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(_path, destination, StringComparison.Ordinal);

        if (!caseOnly)
        {
            if (ResolveDestination(destination, context) is not { } resolved)
            {
                return Task.CompletedTask;
            }

            destination = resolved;
        }

        if (File.Exists(_path))
        {
            File.Move(_path, destination, overwrite: !caseOnly);
        }
        else
        {
            Directory.Move(_path, destination);
        }

        Performed.Add((_path, destination));
        context.Report(new OperationProgress(1, 1, _newName));
        return Task.CompletedTask;
    }

    public override Task UndoAsync(IOperationContext context, CancellationToken ct)
    {
        foreach (var (from, to) in Performed)
        {
            if (File.Exists(to))
            {
                File.Move(to, from, overwrite: true);
            }
            else if (Directory.Exists(to))
            {
                Directory.Move(to, from);
            }
        }

        Performed.Clear();
        return Task.CompletedTask;
    }
}

/// <summary>Moves entries to the platform trash.</summary>
public sealed class TrashOperation : IFileOperation
{
    private readonly IReadOnlyList<string> _paths;
    private readonly List<string> _tokens = [];

    public TrashOperation(IReadOnlyList<string> paths) => _paths = paths;

    public override string ToString() => Description;

    public string Description =>
        $"Move {_paths.Count} item{(_paths.Count == 1 ? "" : "s")} to trash";

    /// <remarks>
    /// Only when the platform handed back a token for every item. A trash that cannot
    /// restore must not advertise undo it cannot deliver.
    /// </remarks>
    public bool CanUndo => _tokens.Count > 0 && _tokens.All(t => t.Length > 0);

    public async Task ExecuteAsync(IOperationContext context, CancellationToken ct)
    {
        if (!context.Trash.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "This platform has no trash; use :delete! to remove permanently.");
        }

        for (var i = 0; i < _paths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            context.Report(new OperationProgress(i, _paths.Count, Path.GetFileName(_paths[i])));

            var token = await context.Trash.TrashAsync(_paths[i], ct).ConfigureAwait(false);
            _tokens.Add(token ?? string.Empty);
        }

        context.Report(new OperationProgress(_paths.Count, _paths.Count, null));
    }

    public async Task UndoAsync(IOperationContext context, CancellationToken ct)
    {
        for (var i = _tokens.Count - 1; i >= 0; i--)
        {
            ct.ThrowIfCancellationRequested();

            if (_tokens[i].Length > 0)
            {
                await context.Trash.RestoreAsync(_tokens[i], ct).ConfigureAwait(false);
            }
        }

        _tokens.Clear();
    }
}

/// <summary>Deletes entries outright.</summary>
/// <remarks>
/// Never undoable, and never the default: <c>dd</c> trashes, and only an explicit
/// <c>:delete!</c> reaches this. The spec is emphatic that destructive actions default to
/// the trash, and an undo stack that silently could not deliver would be worse than none.
/// </remarks>
public sealed class DeleteOperation : IFileOperation
{
    private readonly IReadOnlyList<string> _paths;

    public DeleteOperation(IReadOnlyList<string> paths) => _paths = paths;

    public string Description =>
        $"Permanently delete {_paths.Count} item{(_paths.Count == 1 ? "" : "s")}";

    public bool CanUndo => false;

    public Task ExecuteAsync(IOperationContext context, CancellationToken ct)
    {
        for (var i = 0; i < _paths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            context.Report(new OperationProgress(i, _paths.Count, Path.GetFileName(_paths[i])));

            if (File.Exists(_paths[i]))
            {
                File.Delete(_paths[i]);
            }
            else if (Directory.Exists(_paths[i]))
            {
                Directory.Delete(_paths[i], recursive: true);
            }
        }

        context.Report(new OperationProgress(_paths.Count, _paths.Count, null));
        return Task.CompletedTask;
    }

    public Task UndoAsync(IOperationContext context, CancellationToken ct) =>
        throw new NotSupportedException("Permanent deletion cannot be undone.");
}

/// <summary>Creates an empty file or a directory.</summary>
public sealed class CreateOperation : FileOperationBase
{
    private readonly string _path;
    private readonly bool _directory;

    public CreateOperation(string path, bool directory)
    {
        _path = path;
        _directory = directory;
    }

    public override string Description =>
        $"Create {(_directory ? "directory" : "file")} {Path.GetFileName(_path)}";

    public override Task ExecuteAsync(IOperationContext context, CancellationToken ct)
    {
        if (ResolveDestination(_path, context) is not { } destination)
        {
            return Task.CompletedTask;
        }

        if (_directory)
        {
            Directory.CreateDirectory(destination);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var _ = File.Create(destination);
        }

        Performed.Add((destination, destination));
        context.Report(new OperationProgress(1, 1, Path.GetFileName(destination)));
        return Task.CompletedTask;
    }

    public override Task UndoAsync(IOperationContext context, CancellationToken ct)
    {
        foreach (var (_, created) in Performed)
        {
            DeleteRecursive(created);
        }

        Performed.Clear();
        return Task.CompletedTask;
    }
}
