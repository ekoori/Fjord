using System.Runtime.Versioning;
using Fjord.Core.Operations;
using Fjord.Tests.FileSystem;

namespace Fjord.Tests.Platform;

/// <summary>
/// Exercises the real freedesktop trash implementation.
/// </summary>
/// <remarks>
/// Linux only, and fully self-contained: <c>XDG_DATA_HOME</c> is redirected into a temp
/// directory, so the trash these tests write to is created and destroyed with the fixture
/// and the developer's own trash is never touched.
/// <para>
/// Worth having because the trash implementations are the one part of the operations layer
/// that cannot be covered by a fake — the whole point of them is the platform behaviour,
/// and a mistake in the <c>.trashinfo</c> format is invisible until a desktop file manager
/// refuses to restore something.
/// </para>
/// <para>
/// The class carries the platform annotation so CA1416 is satisfied at compile time —
/// <c>Skip.IfNot</c> is a runtime guard the analyser cannot see through. Every test still
/// skips itself off Linux, so this never actually runs where it should not.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public class XdgTrashServiceTests : IDisposable
{
    private readonly string? _previousDataHome;
    private readonly TempDirectory _temp = new();

    public XdgTrashServiceTests()
    {
        _previousDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_temp.Path, "data"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _previousDataHome);
        _temp.Dispose();
        GC.SuppressFinalize(this);
    }

    private string TrashRoot => Path.Combine(_temp.Path, "data", "Trash");

    private static bool Applicable => OperatingSystem.IsLinux();

    [SkippableFact]
    public async Task TrashingMovesTheFileAndWritesATrashInfo()
    {
        Skip.IfNot(Applicable, "freedesktop trash is Linux-only.");

        var service = new Fjord.Platform.XdgTrashService();
        var file = _temp.CreateFile("doomed.txt");
        await File.WriteAllTextAsync(file, "contents");

        var token = await service.TrashAsync(file);

        Assert.False(File.Exists(file));
        Assert.NotNull(token);
        Assert.True(File.Exists(token), "the trashed file should exist under files/");
        Assert.Equal("contents", await File.ReadAllTextAsync(token!));

        // The info file is what makes the desktop's own "Restore" work; without it the
        // entry is an orphan that only Fjord could put back.
        var info = Path.Combine(TrashRoot, "info", Path.GetFileName(token!) + ".trashinfo");
        Assert.True(File.Exists(info), "a .trashinfo should sit alongside it");

        var record = await File.ReadAllTextAsync(info);
        Assert.StartsWith("[Trash Info]", record, StringComparison.Ordinal);
        Assert.Contains("Path=", record, StringComparison.Ordinal);
        Assert.Contains("DeletionDate=", record, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task RestorePutsTheFileBackWhereItCameFrom()
    {
        Skip.IfNot(Applicable, "freedesktop trash is Linux-only.");

        var service = new Fjord.Platform.XdgTrashService();
        var file = _temp.CreateFile("restore-me.txt");
        await File.WriteAllTextAsync(file, "contents");

        var token = await service.TrashAsync(file);
        Assert.True(await service.RestoreAsync(token!));

        Assert.True(File.Exists(file));
        Assert.Equal("contents", await File.ReadAllTextAsync(file));

        // The record is consumed, not left behind as a stale entry.
        Assert.False(File.Exists(Path.Combine(TrashRoot, "info", Path.GetFileName(token!) + ".trashinfo")));
    }

    [SkippableFact]
    public async Task TrashingTwoFilesOfTheSameNameKeepsBoth()
    {
        // Names collide constantly in a trash — the same file deleted from two places is
        // the normal case, and one silently overwriting the other would be data loss.
        Skip.IfNot(Applicable, "freedesktop trash is Linux-only.");

        var service = new Fjord.Platform.XdgTrashService();

        var first = _temp.CreateFile(Path.Combine("a", "same.txt"));
        var second = _temp.CreateFile(Path.Combine("b", "same.txt"));
        await File.WriteAllTextAsync(first, "first");
        await File.WriteAllTextAsync(second, "second");

        var firstToken = await service.TrashAsync(first);
        var secondToken = await service.TrashAsync(second);

        Assert.NotEqual(firstToken, secondToken);
        Assert.Equal("first", await File.ReadAllTextAsync(firstToken!));
        Assert.Equal("second", await File.ReadAllTextAsync(secondToken!));

        // Both must restore to their own original directories.
        Assert.True(await service.RestoreAsync(firstToken!));
        Assert.True(await service.RestoreAsync(secondToken!));
        Assert.Equal("first", await File.ReadAllTextAsync(first));
        Assert.Equal("second", await File.ReadAllTextAsync(second));
    }

    [SkippableFact]
    public async Task DirectoriesAreTrashedWhole()
    {
        Skip.IfNot(Applicable, "freedesktop trash is Linux-only.");

        var service = new Fjord.Platform.XdgTrashService();
        _temp.CreateFile(Path.Combine("tree", "nested", "leaf.txt"));
        var tree = Path.Combine(_temp.Path, "tree");

        var token = await service.TrashAsync(tree);

        Assert.False(Directory.Exists(tree));
        Assert.True(File.Exists(Path.Combine(token!, "nested", "leaf.txt")));

        Assert.True(await service.RestoreAsync(token!));
        Assert.True(File.Exists(Path.Combine(tree, "nested", "leaf.txt")));
    }

    [SkippableFact]
    public async Task APathWithSpacesAndUnicodeRoundTrips()
    {
        // The Path= field is percent-encoded, and getting that wrong is invisible until a
        // restore lands in the wrong place or fails outright.
        Skip.IfNot(Applicable, "freedesktop trash is Linux-only.");

        var service = new Fjord.Platform.XdgTrashService();
        var file = _temp.CreateFile("a file with spaces & ünïcode.txt");
        await File.WriteAllTextAsync(file, "contents");

        var token = await service.TrashAsync(file);
        Assert.True(await service.RestoreAsync(token!));

        Assert.True(File.Exists(file));
    }

    [SkippableFact]
    public async Task TrashingSomethingAbsentFails()
    {
        Skip.IfNot(Applicable, "freedesktop trash is Linux-only.");

        var service = new Fjord.Platform.XdgTrashService();

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => service.TrashAsync(Path.Combine(_temp.Path, "never-existed.txt")));
    }

    [SkippableFact]
    public async Task RestoringAnUnknownTokenReportsFailureRatherThanThrowing()
    {
        Skip.IfNot(Applicable, "freedesktop trash is Linux-only.");

        var service = new Fjord.Platform.XdgTrashService();

        Assert.False(await service.RestoreAsync(Path.Combine(_temp.Path, "not-in-the-trash")));
    }

    [SkippableFact]
    public async Task TheTrashOperationWorksAgainstTheRealService()
    {
        // End to end: the operation layer and the platform layer together, which is what
        // actually runs when the user presses dd.
        Skip.IfNot(Applicable, "freedesktop trash is Linux-only.");

        var file = _temp.CreateFile("via-operation.txt");
        var context = new RealTrashContext(new Fjord.Platform.XdgTrashService());
        var operation = new Fjord.Operations.TrashOperation([file]);

        await operation.ExecuteAsync(context, default);
        Assert.False(File.Exists(file));
        Assert.True(operation.CanUndo, "the XDG trash returns a token, so undo must be offered");

        await operation.UndoAsync(context, default);
        Assert.True(File.Exists(file));
    }

    private sealed class RealTrashContext(ITrashService trash) : IOperationContext
    {
        public ITrashService Trash => trash;

        public void Report(OperationProgress progress)
        {
            // Not under test here.
        }

        public ConflictResolution ResolveConflict(string destination) => ConflictResolution.Fail;
    }
}
