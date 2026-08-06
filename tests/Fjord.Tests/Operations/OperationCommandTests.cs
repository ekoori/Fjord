using Fjord.Commands;
using Fjord.Core.Diagnostics;
using Fjord.Core.Operations;
using Fjord.Core.Selection;
using Fjord.Tests.Commands;

namespace Fjord.Tests.Operations;

/// <summary>Records what commands asked for, without doing any of it.</summary>
internal sealed class FakeOperationHost : IOperationHost
{
    public List<string> Enqueued { get; } = [];

    public Fjord.Core.Registers.RegisterFile Registers { get; } = new();

    /// <summary>The unnamed register, same as the real host.</summary>
    public ClipboardContents Clipboard
    {
        get => Registers.Unnamed;
        set => Registers.Unnamed = value;
    }

    public string? NextUndoDescription { get; set; }

    public string? NextRedoDescription { get; set; }

    public void Enqueue(IFileOperation operation) => Enqueued.Add(operation.Description);

    public Task<string?> UndoAsync(CancellationToken ct = default)
    {
        var description = NextUndoDescription;
        NextUndoDescription = null;
        return Task.FromResult(description);
    }

    public Task<string?> RedoAsync(CancellationToken ct = default) => Task.FromResult(NextRedoDescription);
}

/// <summary>Describes what was asked for, so tests can assert without running anything.</summary>
internal sealed class FakeOperationFactory : IFileOperationFactory
{
    public IFileOperation Copy(IReadOnlyList<string> s, string d) => new Fake($"copy:{Join(s)}->{d}");

    public IFileOperation Move(IReadOnlyList<string> s, string d) => new Fake($"move:{Join(s)}->{d}");

    public IFileOperation Duplicate(IReadOnlyList<string> s, string d) => new Fake($"duplicate:{Join(s)}");

    public IFileOperation Rename(string path, string name) => new Fake($"rename:{path}->{name}");

    public IFileOperation Trash(IReadOnlyList<string> p) => new Fake($"trash:{Join(p)}");

    public IFileOperation Delete(IReadOnlyList<string> p) => new Fake($"delete:{Join(p)}");

    public IFileOperation Create(string path, bool directory) =>
        new Fake($"create:{(directory ? "dir" : "file")}:{path}");

    private static string Join(IReadOnlyList<string> paths) =>
        string.Join('|', paths.Select(Path.GetFileName));

    private sealed class Fake(string description) : IFileOperation
    {
        public string Description => description;

        public bool CanUndo => true;

        public Task ExecuteAsync(IOperationContext c, CancellationToken ct) => Task.CompletedTask;

        public Task UndoAsync(IOperationContext c, CancellationToken ct) => Task.CompletedTask;
    }
}

public class OperationCommandTests
{
    private static (CommandParser Parser, FakeOperationHost Host) Setup()
    {
        var host = new FakeOperationHost();
        var registry = new CommandRegistry();
        registry.Register(new OperationCommands(host, new FakeOperationFactory()));
        return (new CommandParser(registry), host);
    }

    private static FakePane Pane() => new("/tmp", "a.txt", "b.txt", "c.txt", "d.txt");

    private static CommandContext Context(
        FakePane pane, IMessageSink? messages = null, EntryRange? range = null, char register = '"') =>
        new()
        {
            Pane = pane,
            Messages = messages ?? NullMessageSink.Instance,
            Range = range,
            Register = register,
        };

    [Fact]
    public void YankFillsTheClipboardForCopying()
    {
        var (parser, host) = Setup();
        var pane = Pane();

        parser.Execute("yank-paths", Context(pane, range: new EntryRange(0, 1)));

        Assert.False(host.Clipboard.Cut);
        Assert.Equal(2, host.Clipboard.Paths.Count);
    }

    [Fact]
    public void CutFillsTheClipboardForMoving()
    {
        var (parser, host) = Setup();

        parser.Execute("cut-paths", Context(Pane(), range: new EntryRange(0, 0)));

        Assert.True(host.Clipboard.Cut);
    }

    [Fact]
    public void PastingAYankCopies()
    {
        var (parser, host) = Setup();
        host.Clipboard = new ClipboardContents(["/other/a.txt"], Cut: false);

        parser.Execute("paste", Context(Pane()));

        Assert.Equal(["copy:a.txt->/tmp"], host.Enqueued);
    }

    [Fact]
    public void PastingACutMoves()
    {
        var (parser, host) = Setup();
        host.Clipboard = new ClipboardContents(["/other/a.txt"], Cut: true);

        parser.Execute("paste", Context(Pane()));

        Assert.Equal(["move:a.txt->/tmp"], host.Enqueued);
    }

    [Fact]
    public void ACutIsConsumedByPastingButAYankIsNot()
    {
        // Copy-paste-paste should put the files in two places; cut-paste-paste must not
        // try to move something that has already moved.
        var (parser, host) = Setup();

        host.Clipboard = new ClipboardContents(["/other/a.txt"], Cut: false);
        parser.Execute("paste", Context(Pane()));
        Assert.False(host.Clipboard.IsEmpty);

        host.Clipboard = new ClipboardContents(["/other/a.txt"], Cut: true);
        parser.Execute("paste", Context(Pane()));
        Assert.True(host.Clipboard.IsEmpty);
    }

    [Fact]
    public void YankingIntoANamedRegisterKeepsItSeparate()
    {
        var (parser, host) = Setup();
        var pane = Pane();

        parser.Execute("yank-paths", Context(pane, range: new EntryRange(0, 0), register: 'a'));
        parser.Execute("yank-paths", Context(pane, range: new EntryRange(1, 3), register: 'b'));

        Assert.Single(host.Registers['a'].Paths);
        Assert.Equal(3, host.Registers['b'].Paths.Count);
    }

    [Fact]
    public void PastingReadsTheRegisterItWasGiven()
    {
        var (parser, host) = Setup();
        host.Registers['a'] = new ClipboardContents(["/other/a.txt"], Cut: false);
        host.Registers['b'] = new ClipboardContents(["/other/b.txt"], Cut: false);

        parser.Execute("paste", Context(Pane(), register: 'b'));

        Assert.Equal(["copy:b.txt->/tmp"], host.Enqueued);
    }

    [Fact]
    public void PastingAnEmptyNamedRegisterNamesItInTheMessage()
    {
        // "Nothing to paste" would be actively misleading here: something probably is in
        // the unnamed register, and the user needs to know it was "q that was empty.
        var (parser, host) = Setup();
        var messages = new RecordingMessageSink();
        host.Clipboard = new ClipboardContents(["/other/a.txt"], Cut: false);

        parser.Execute("paste", Context(Pane(), messages, register: 'q'));

        Assert.Empty(host.Enqueued);
        Assert.Equal("Register \"q is empty.", messages.Last);
    }

    [Fact]
    public void PastingACutFromANamedRegisterConsumesOnlyThatOne()
    {
        var (parser, host) = Setup();
        host.Registers['a'] = new ClipboardContents(["/other/a.txt"], Cut: true);
        host.Registers['b'] = new ClipboardContents(["/other/b.txt"], Cut: true);

        parser.Execute("paste", Context(Pane(), register: 'a'));

        Assert.True(host.Registers['a'].IsEmpty);
        Assert.False(host.Registers['b'].IsEmpty);
    }

    [Fact]
    public void RegistersListsWhatEachIsHolding()
    {
        var (parser, host) = Setup();
        var messages = new RecordingMessageSink();
        host.Registers['a'] = new ClipboardContents(["one.txt", "two.txt"], Cut: false);

        parser.Execute("registers", Context(Pane(), messages));

        Assert.Contains("\"a yank 2", messages.Last, StringComparison.Ordinal);
    }

    [Fact]
    public void RegistersSaysSoWhenThereIsNothingToShow()
    {
        var (parser, _) = Setup();
        var messages = new RecordingMessageSink();

        parser.Execute("registers", Context(Pane(), messages));

        Assert.Equal("All registers are empty.", messages.Last);
    }

    [Fact]
    public void PastingAnEmptyClipboardSaysSoRatherThanQueuingNothing()
    {
        var (parser, host) = Setup();
        var messages = new RecordingMessageSink();

        parser.Execute("paste", Context(Pane(), messages));

        Assert.Empty(host.Enqueued);
        Assert.Equal("Nothing to paste.", messages.Last);
    }

    [Fact]
    public void TrashActsOnTheRangeAMotionCovered()
    {
        var (parser, host) = Setup();

        parser.Execute("trash", Context(Pane(), range: new EntryRange(1, 2)));

        Assert.Equal(["trash:b.txt|c.txt"], host.Enqueued);
    }

    [Fact]
    public void TrashSelectionUsesTheSelectionWhenThereIsOne()
    {
        var (parser, host) = Setup();
        var pane = Pane();
        pane.Selection.SetRange(0, 1, true);

        parser.Execute("trash-selection", Context(pane));

        Assert.Equal(["trash:a.txt|b.txt"], host.Enqueued);
    }

    [Fact]
    public void TrashSelectionFallsBackToTheCursor()
    {
        // The rule every file manager uses: a bare :trash does what the user means
        // whether or not they have selected anything.
        var (parser, host) = Setup();
        var pane = Pane();
        pane.CursorIndex = 2;

        parser.Execute("trash-selection", Context(pane));

        Assert.Equal(["trash:c.txt"], host.Enqueued);
    }

    [Fact]
    public void PermanentDeleteIsSpelledWithABangAndWarns()
    {
        var (parser, host) = Setup();
        var messages = new RecordingMessageSink();

        parser.Execute("delete!", Context(Pane(), messages));

        Assert.Equal(["delete:a.txt"], host.Enqueued);
        Assert.Equal(MessageLevel.Warning, messages.Messages[^1].Level);
    }

    [Fact]
    public void PermanentDeleteIsNotBoundToAnyKey()
    {
        // The spec is emphatic that destructive actions default to the trash and that
        // permanent deletion needs an explicit command. A key binding would undo that.
        foreach (var (_, bindings) in DefaultBindings.All)
        {
            Assert.DoesNotContain(bindings.Values, c =>
                c.StartsWith("delete!", StringComparison.Ordinal)
                || c.StartsWith("delete-permanently", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void RenameActsOnTheCursorEntry()
    {
        var (parser, host) = Setup();
        var pane = Pane();
        pane.CursorIndex = 1;

        parser.Execute("rename new-name.txt", Context(pane));

        Assert.Equal([$"rename:{Path.Combine("/tmp", "b.txt")}->new-name.txt"], host.Enqueued);
    }

    [Fact]
    public void RenameKeepsSpacesWithoutQuoting()
    {
        var (parser, host) = Setup();

        parser.Execute("rename My Report.pdf", Context(Pane()));

        Assert.Contains("->My Report.pdf", host.Enqueued[0], StringComparison.Ordinal);
    }

    [Fact]
    public void MkdirAndTouchCreateInTheCurrentDirectory()
    {
        var (parser, host) = Setup();

        parser.Execute("mkdir sub", Context(Pane()));
        parser.Execute("touch note.txt", Context(Pane()));

        Assert.Equal(
            [$"create:dir:{Path.Combine("/tmp", "sub")}", $"create:file:{Path.Combine("/tmp", "note.txt")}"],
            host.Enqueued);
    }

    [Fact]
    public async Task UndoReportsWhatItReversed()
    {
        var (parser, host) = Setup();
        host.NextUndoDescription = "Copy 2 items to out";
        var messages = new RecordingMessageSink();

        await AwaitAll(parser, "undo", Pane(), messages);

        Assert.Equal("Undid: Copy 2 items to out", messages.Last);
    }

    [Fact]
    public async Task UndoWithNothingToDoSaysSo()
    {
        var (parser, _) = Setup();
        var messages = new RecordingMessageSink();

        await AwaitAll(parser, "undo", Pane(), messages);

        Assert.Equal("Nothing to undo.", messages.Last);
    }

    [Fact]
    public async Task RedoReportsWhatItRepeated()
    {
        var (parser, host) = Setup();
        host.NextRedoDescription = "Move 1 item to out";
        var messages = new RecordingMessageSink();

        await AwaitAll(parser, "redo", Pane(), messages);

        Assert.Equal("Redid: Move 1 item to out", messages.Last);
    }

    private static async Task AwaitAll(
        CommandParser parser, string line, FakePane pane, IMessageSink messages)
    {
        var context = new CommandContext { Pane = pane, Messages = messages };

        foreach (var command in parser.Parse(line, context))
        {
            if (command.Invoke() is Task task)
            {
                await task;
            }
        }
    }
}
