using Fjord.Core.Operations;
using Fjord.Operations;
using Fjord.Tests.FileSystem;

namespace Fjord.Tests.Operations;

/// <summary>A trash that just moves items aside, so operations are testable off-platform.</summary>
internal sealed class FakeTrash : ITrashService
{
    private readonly string _root;
    private readonly Dictionary<string, string> _origins = new(StringComparer.Ordinal);

    public FakeTrash(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    public bool IsSupported { get; set; } = true;

    /// <summary>When false, mimics a platform that trashes but cannot put back.</summary>
    public bool CanRestore { get; set; } = true;

    public List<string> Trashed { get; } = [];

    public Task<string?> TrashAsync(string path, CancellationToken ct = default)
    {
        var token = Path.Combine(_root, Guid.NewGuid().ToString("n"));

        if (File.Exists(path))
        {
            File.Move(path, token);
        }
        else
        {
            Directory.Move(path, token);
        }

        _origins[token] = path;
        Trashed.Add(path);
        return Task.FromResult(CanRestore ? token : null);
    }

    public Task<bool> RestoreAsync(string token, CancellationToken ct = default)
    {
        if (!_origins.TryGetValue(token, out var origin))
        {
            return Task.FromResult(false);
        }

        if (File.Exists(token))
        {
            File.Move(token, origin);
        }
        else
        {
            Directory.Move(token, origin);
        }

        return Task.FromResult(true);
    }
}

internal sealed class TestContext : IOperationContext
{
    public TestContext(ITrashService trash) => Trash = trash;

    public ITrashService Trash { get; }

    public ConflictResolution Conflict { get; set; } = ConflictResolution.Fail;

    public List<OperationProgress> Reports { get; } = [];

    /// <summary>Lets a test act at a known point, instead of racing a wall-clock timer.</summary>
    public Action<OperationProgress>? OnReport { get; set; }

    public void Report(OperationProgress progress)
    {
        Reports.Add(progress);
        OnReport?.Invoke(progress);
    }

    public ConflictResolution ResolveConflict(string destination) => Conflict;
}

public class FileOperationTests
{
    private static (TempDirectory Temp, TestContext Context) Setup()
    {
        var temp = new TempDirectory();
        return (temp, new TestContext(new FakeTrash(Path.Combine(temp.Path, ".trash"))));
    }

    [Fact]
    public async Task CopyDuplicatesAFileAndLeavesTheSource()
    {
        var (temp, context) = Setup();
        using var _ = temp;
        var source = temp.CreateFile("a.txt");
        var target = temp.CreateDir("out");

        var operation = new CopyOperation([source], target);
        await operation.ExecuteAsync(context, default);

        Assert.True(File.Exists(source));
        Assert.True(File.Exists(Path.Combine(target, "a.txt")));
    }

    [Fact]
    public async Task CopyIsUndoneByRemovingWhatItCreated()
    {
        var (temp, context) = Setup();
        using var _ = temp;
        var source = temp.CreateFile("a.txt");
        var target = temp.CreateDir("out");

        var operation = new CopyOperation([source], target);
        await operation.ExecuteAsync(context, default);
        await operation.UndoAsync(context, default);

        Assert.False(File.Exists(Path.Combine(target, "a.txt")));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task CopyHandlesWholeDirectoryTrees()
    {
        var (temp, context) = Setup();
        using var _ = temp;
        temp.CreateFile(Path.Combine("tree", "sub", "deep.txt"));
        var target = temp.CreateDir("out");

        await new CopyOperation([Path.Combine(temp.Path, "tree")], target).ExecuteAsync(context, default);

        Assert.True(File.Exists(Path.Combine(target, "tree", "sub", "deep.txt")));
    }

    [Fact]
    public async Task AConflictFailsByDefault()
    {
        // The default has to be the one that cannot lose data.
        var (temp, context) = Setup();
        using var _ = temp;
        var source = temp.CreateFile("a.txt");
        var target = temp.CreateDir("out");
        File.WriteAllText(Path.Combine(target, "a.txt"), "existing");

        await Assert.ThrowsAsync<IOException>(
            () => new CopyOperation([source], target).ExecuteAsync(context, default));

        Assert.Equal("existing", File.ReadAllText(Path.Combine(target, "a.txt")));
    }

    [Fact]
    public async Task SkipLeavesTheDestinationAloneAndCarriesOn()
    {
        var (temp, context) = Setup();
        using var _ = temp;
        context.Conflict = ConflictResolution.Skip;

        var a = temp.CreateFile("a.txt");
        var b = temp.CreateFile("b.txt");
        var target = temp.CreateDir("out");
        File.WriteAllText(Path.Combine(target, "a.txt"), "existing");

        var operation = new CopyOperation([a, b], target);
        await operation.ExecuteAsync(context, default);

        Assert.Equal("existing", File.ReadAllText(Path.Combine(target, "a.txt")));
        Assert.True(File.Exists(Path.Combine(target, "b.txt")));

        // Undo must not remove the file it skipped.
        await operation.UndoAsync(context, default);
        Assert.Equal("existing", File.ReadAllText(Path.Combine(target, "a.txt")));
    }

    [Fact]
    public async Task RenameWritesAlongsideRatherThanOver()
    {
        var (temp, context) = Setup();
        using var _ = temp;
        context.Conflict = ConflictResolution.Rename;

        var source = temp.CreateFile("report.pdf");
        var target = temp.CreateDir("out");
        File.WriteAllText(Path.Combine(target, "report.pdf"), "existing");

        await new CopyOperation([source], target).ExecuteAsync(context, default);

        Assert.True(File.Exists(Path.Combine(target, "report (2).pdf")));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(target, "report.pdf")));
    }

    [Fact]
    public async Task MoveRemovesTheSource()
    {
        var (temp, context) = Setup();
        using var _ = temp;
        var source = temp.CreateFile("a.txt");
        var target = temp.CreateDir("out");

        await new MoveOperation([source], target).ExecuteAsync(context, default);

        Assert.False(File.Exists(source));
        Assert.True(File.Exists(Path.Combine(target, "a.txt")));
    }

    [Fact]
    public async Task MoveIsUndoneByMovingBack()
    {
        var (temp, context) = Setup();
        using var _ = temp;
        var source = temp.CreateFile("a.txt");
        File.WriteAllText(source, "content");
        var target = temp.CreateDir("out");

        var operation = new MoveOperation([source], target);
        await operation.ExecuteAsync(context, default);
        await operation.UndoAsync(context, default);

        Assert.True(File.Exists(source));
        Assert.Equal("content", File.ReadAllText(source));
        Assert.False(File.Exists(Path.Combine(target, "a.txt")));
    }

    [Fact]
    public async Task MovingADirectoryIntoItselfIsRefused()
    {
        // Without the guard this recurses until the path limit and leaves a half-built tree.
        var (temp, context) = Setup();
        using var _ = temp;
        var tree = temp.CreateDir("tree");
        var inside = temp.CreateDir(Path.Combine("tree", "inside"));

        await Assert.ThrowsAsync<IOException>(
            () => new MoveOperation([tree], inside).ExecuteAsync(context, default));
    }

    [Fact]
    public async Task RenameChangesTheNameInPlace()
    {
        var (temp, context) = Setup();
        using var _ = temp;
        var source = temp.CreateFile("old.txt");

        await new RenameOperation(source, "new.txt").ExecuteAsync(context, default);

        Assert.False(File.Exists(source));
        Assert.True(File.Exists(Path.Combine(temp.Path, "new.txt")));
    }

    [Fact]
    public async Task RenameIsUndoable()
    {
        var (temp, context) = Setup();
        using var _ = temp;
        var source = temp.CreateFile("old.txt");

        var operation = new RenameOperation(source, "new.txt");
        await operation.ExecuteAsync(context, default);
        await operation.UndoAsync(context, default);

        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task ACaseOnlyRenameIsNotTreatedAsAConflict()
    {
        // On a case-insensitive filesystem the destination "exists" because it is the same
        // file; refusing would make renaming readme to README impossible.
        var (temp, context) = Setup();
        using var _ = temp;
        var source = temp.CreateFile("readme");

        await new RenameOperation(source, "README").ExecuteAsync(context, default);

        Assert.Contains(
            Directory.EnumerateFiles(temp.Path).Select(Path.GetFileName),
            n => string.Equals(n, "README", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad/name")]
    public async Task AnInvalidRenameIsRefused(string name)
    {
        var (temp, context) = Setup();
        using var _ = temp;
        var source = temp.CreateFile("a.txt");

        await Assert.ThrowsAsync<IOException>(
            () => new RenameOperation(source, name).ExecuteAsync(context, default));
    }

    [Fact]
    public async Task TrashMovesToTheTrashAndUndoRestores()
    {
        var (temp, context) = Setup();
        using var _ = temp;
        var source = temp.CreateFile("a.txt");

        var operation = new TrashOperation([source]);
        await operation.ExecuteAsync(context, default);
        Assert.False(File.Exists(source));
        Assert.True(operation.CanUndo);

        await operation.UndoAsync(context, default);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task TrashDoesNotClaimUndoItCannotDeliver()
    {
        // Windows can recycle but not put back; advertising undo there would be a lie.
        var (temp, context) = Setup();
        using var _ = temp;
        ((FakeTrash)context.Trash).CanRestore = false;

        var operation = new TrashOperation([temp.CreateFile("a.txt")]);
        await operation.ExecuteAsync(context, default);

        Assert.False(operation.CanUndo);
    }

    [Fact]
    public async Task TrashRefusesWhereThePlatformHasNone()
    {
        var (temp, context) = Setup();
        using var _ = temp;
        ((FakeTrash)context.Trash).IsSupported = false;

        await Assert.ThrowsAsync<PlatformNotSupportedException>(
            () => new TrashOperation([temp.CreateFile("a.txt")]).ExecuteAsync(context, default));
    }

    [Fact]
    public async Task PermanentDeleteIsNeverUndoable()
    {
        var (temp, context) = Setup();
        using var _ = temp;
        var source = temp.CreateFile("a.txt");

        var operation = new DeleteOperation([source]);
        await operation.ExecuteAsync(context, default);

        Assert.False(File.Exists(source));
        Assert.False(operation.CanUndo);
        await Assert.ThrowsAsync<NotSupportedException>(() => operation.UndoAsync(context, default));
    }

    [Fact]
    public async Task CreateMakesAFileOrDirectoryAndIsUndoable()
    {
        var (temp, context) = Setup();
        using var _ = temp;

        var file = new CreateOperation(Path.Combine(temp.Path, "new.txt"), directory: false);
        await file.ExecuteAsync(context, default);
        Assert.True(File.Exists(Path.Combine(temp.Path, "new.txt")));

        await file.UndoAsync(context, default);
        Assert.False(File.Exists(Path.Combine(temp.Path, "new.txt")));

        var folder = new CreateOperation(Path.Combine(temp.Path, "sub"), directory: true);
        await folder.ExecuteAsync(context, default);
        Assert.True(Directory.Exists(Path.Combine(temp.Path, "sub")));
    }

    [Fact]
    public async Task CancellingPartWayLeavesWhatWasDoneUndoable()
    {
        // The reason operations record what happened rather than what was asked: a
        // cancelled bulk copy still has real files on disk that u must clean up.
        var (temp, context) = Setup();
        using var _ = temp;
        var target = temp.CreateDir("out");

        var sources = Enumerable.Range(0, 20).Select(i => temp.CreateFile($"f{i}.txt")).ToList();

        // Deterministic: cancel once a known number of items have been reported, rather
        // than after a wall-clock delay that races with how fast the disk happens to be.
        using var cts = new CancellationTokenSource();
        context.OnReport = p =>
        {
            if (p.Current >= 5)
            {
                cts.Cancel();
            }
        };

        var operation = new CopyOperation(sources, target);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => operation.ExecuteAsync(context, cts.Token));

        Assert.NotEmpty(Directory.GetFiles(target));

        await operation.UndoAsync(context, default);
        Assert.Empty(Directory.GetFiles(target));
    }

    [Fact]
    public async Task ACancelledFileCopyLeavesNoTruncatedFileBehind()
    {
        // Cancelling mid-file has already created the destination and written part of it,
        // and the operation never records it — so undo cannot clean it up. A half-written
        // file that looks real is data loss, not untidiness.
        var (temp, context) = Setup();
        using var _ = temp;
        var target = temp.CreateDir("out");

        // Large enough that the copy is still streaming when the cancel lands.
        var big = Path.Combine(temp.Path, "big.bin");
        await File.WriteAllBytesAsync(big, new byte[32 * 1024 * 1024]);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(15));

        var operation = new CopyOperation([big], target);

        try
        {
            await operation.ExecuteAsync(context, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected; the point is what is left on disk afterwards.
        }

        var copied = Path.Combine(target, "big.bin");
        if (File.Exists(copied))
        {
            // If it survived, the copy finished before the cancel and must be complete.
            Assert.Equal(new FileInfo(big).Length, new FileInfo(copied).Length);
        }
    }

    [Fact]
    public async Task ProgressIsReportedAndEndsComplete()
    {
        var (temp, context) = Setup();
        using var _ = temp;
        var target = temp.CreateDir("out");
        var sources = Enumerable.Range(0, 3).Select(i => temp.CreateFile($"f{i}.txt")).ToList();

        await new CopyOperation(sources, target).ExecuteAsync(context, default);

        Assert.NotEmpty(context.Reports);
        Assert.Equal(3, context.Reports[^1].Current);
        Assert.Equal(1.0, context.Reports[^1].Fraction);
    }

    private static string Zip(TempDirectory temp, params string[] entries)
    {
        var path = Path.Combine(temp.Path, "sample.zip");

        using var file = File.Create(path);
        using var zip = new System.IO.Compression.ZipArchive(
            file, System.IO.Compression.ZipArchiveMode.Create);

        foreach (var entry in entries)
        {
            using var stream = zip.CreateEntry(entry).Open();
            stream.Write("contents"u8);
        }

        return path;
    }

    [Fact]
    public async Task ExtractingPutsAnArchiveEntryOnDisk()
    {
        var (temp, context) = Setup();
        using var _ = temp;
        var zip = Zip(temp, "lib/one.dll");
        var target = temp.CreateDir("out");

        var member = new Fjord.Core.FileSystem.ArchivePath(zip, "lib/one.dll").ToString();
        await new ExtractOperation([member], target).ExecuteAsync(context, default);

        Assert.Equal("contents", File.ReadAllText(Path.Combine(target, "one.dll")));
    }

    [Fact]
    public async Task ExtractingIsUndoneByRemovingWhatItWrote()
    {
        // The archive was never touched, so undo is the same as undoing a copy.
        var (temp, context) = Setup();
        using var _ = temp;
        var zip = Zip(temp, "lib/one.dll", "lib/net10.0/two.dll");
        var target = temp.CreateDir("out");

        var member = new Fjord.Core.FileSystem.ArchivePath(zip, "lib").ToString();
        var operation = new ExtractOperation([member], target);

        await operation.ExecuteAsync(context, default);
        Assert.True(Directory.Exists(Path.Combine(target, "lib")));

        await operation.UndoAsync(context, default);

        Assert.False(Directory.Exists(Path.Combine(target, "lib")));
        Assert.True(File.Exists(zip));
    }

    [Fact]
    public async Task ExtractingAnEntryThatIsGoneFails()
    {
        // The archive can have been replaced between the yank and the paste. Silently
        // writing nothing would look exactly like a successful extraction.
        var (temp, context) = Setup();
        using var _ = temp;
        var zip = Zip(temp, "lib/one.dll");
        var target = temp.CreateDir("out");

        var member = new Fjord.Core.FileSystem.ArchivePath(zip, "lib/gone.dll").ToString();

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => new ExtractOperation([member], target).ExecuteAsync(context, default));
    }
}

public class UniquePathTests
{
    [Fact]
    public void AFreePathIsReturnedUnchanged()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "free.txt");

        Assert.Equal(path, UniquePath.For(path));
    }

    [Fact]
    public void TheCounterGoesBeforeTheExtension()
    {
        // "report (2).pdf", not "report.pdf (2)" — the file still has to open in the
        // right application.
        using var temp = new TempDirectory();
        temp.CreateFile("report.pdf");

        Assert.Equal(
            Path.Combine(temp.Path, "report (2).pdf"),
            UniquePath.For(Path.Combine(temp.Path, "report.pdf")));
    }

    [Fact]
    public void TheCounterIncrementsPastExistingCopies()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("a.txt");
        temp.CreateFile("a (2).txt");

        Assert.Equal(
            Path.Combine(temp.Path, "a (3).txt"),
            UniquePath.For(Path.Combine(temp.Path, "a.txt")));
    }

    [Fact]
    public void ADotfileKeepsItsWholeName()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(".bashrc");

        Assert.Equal(
            Path.Combine(temp.Path, ".bashrc (2)"),
            UniquePath.For(Path.Combine(temp.Path, ".bashrc")));
    }

    [Fact]
    public void ADirectoryOccupyingTheNameCountsAsTaken()
    {
        using var temp = new TempDirectory();
        temp.CreateDir("thing");

        Assert.NotEqual(
            Path.Combine(temp.Path, "thing"),
            UniquePath.For(Path.Combine(temp.Path, "thing")));
    }

    [Theory]
    [InlineData("/a/b/c", "/a/b", true)]
    [InlineData("/a/b", "/a/b", true)]
    [InlineData("/a/bc", "/a/b", false)]
    [InlineData("/x/y", "/a/b", false)]
    public void IsInsideRespectsPathBoundaries(string child, string parent, bool expected) =>
        Assert.Equal(expected, UniquePath.IsInside(child, parent, StringComparison.OrdinalIgnoreCase));
}
