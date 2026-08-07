using Fjord.Commands;
using Fjord.Core.Diagnostics;
using Fjord.Core.Panes;

namespace Fjord.Tests.Commands;

/// <summary>An in-memory workspace, so tab commands are tested without a window.</summary>
internal sealed class FakeWorkspace : IWorkspace
{
    private readonly List<FakeTab> _tabs = [];
    private readonly Stack<string> _closed = new();

    public FakeWorkspace(params string[] directories)
    {
        foreach (var directory in directories.DefaultIfEmpty("/a"))
        {
            _tabs.Add(new FakeTab(new FakePane(directory)));
        }
    }

    public IReadOnlyList<ITab> Tabs => _tabs;

    public int CurrentIndex { get; set; }

    public ITab Current => _tabs[CurrentIndex];

    public bool QuitCalled { get; private set; }

    public IEnumerable<string> Directories => _tabs.Select(t => t.Active.Directory);

    public ITab OpenTab(string directory, NewTabPosition position = NewTabPosition.AfterCurrent,
        bool background = false)
    {
        var tab = new FakeTab(new FakePane(directory));
        var index = position == NewTabPosition.End ? _tabs.Count : CurrentIndex + 1;
        _tabs.Insert(index, tab);

        if (!background)
        {
            CurrentIndex = index;
        }

        return tab;
    }

    public void CloseTab(int index)
    {
        _closed.Push(_tabs[index].Active.Directory);
        _tabs.RemoveAt(index);
        CurrentIndex = Math.Clamp(CurrentIndex, 0, _tabs.Count - 1);
    }

    public bool UndoClose()
    {
        if (_closed.Count == 0)
        {
            return false;
        }

        _tabs.Add(new FakeTab(new FakePane(_closed.Pop())));
        return true;
    }

    public void MoveTab(int from, int to)
    {
        var tab = _tabs[from];
        _tabs.RemoveAt(from);
        _tabs.Insert(Math.Clamp(to, 0, _tabs.Count), tab);
        CurrentIndex = to;
    }

    public void Quit() => QuitCalled = true;

    private sealed class FakeTab(FakePane pane) : ITab
    {
        public IPane Active => pane;

        public IReadOnlyList<IPane> Panes => [pane];

        public string Title => pane.Directory;

        public bool IsLocked { get; set; }
    }
}

public class TabCommandTests
{
    private static CommandParser Parser()
    {
        var registry = new CommandRegistry();
        registry.Register(new TabCommands());
        return new CommandParser(registry);
    }

    private static CommandContext Context(
        FakeWorkspace workspace, IMessageSink? messages = null, int? count = null) =>
        new()
        {
            Workspace = workspace,
            Pane = workspace.Current.Active,
            Messages = messages ?? NullMessageSink.Instance,
            Count = count,
        };

    [Fact]
    public void TabNextWraps()
    {
        var workspace = new FakeWorkspace("/a", "/b", "/c");
        var parser = Parser();

        parser.Execute("tab-next", Context(workspace));
        Assert.Equal(1, workspace.CurrentIndex);

        parser.Execute("tab-next", Context(workspace));
        parser.Execute("tab-next", Context(workspace));
        Assert.Equal(0, workspace.CurrentIndex);
    }

    [Fact]
    public void TabPrevWraps()
    {
        var workspace = new FakeWorkspace("/a", "/b", "/c");

        Parser().Execute("tab-prev", Context(workspace));

        Assert.Equal(2, workspace.CurrentIndex);
    }

    [Fact]
    public void CountedTabNextGoesToThatTabOneBased()
    {
        // <count>gt, as vim numbers tabs.
        var workspace = new FakeWorkspace("/a", "/b", "/c");

        Parser().Execute("tab-next", Context(workspace, count: 3));

        Assert.Equal(2, workspace.CurrentIndex);
    }

    [Fact]
    public void CountedTabNextClampsRatherThanThrowing()
    {
        var workspace = new FakeWorkspace("/a", "/b");

        Parser().Execute("tab-next", Context(workspace, count: 99));

        Assert.Equal(1, workspace.CurrentIndex);
    }

    [Fact]
    public void TabPrevWithACountStepsBackThatMany()
    {
        var workspace = new FakeWorkspace("/a", "/b", "/c", "/d") { CurrentIndex = 3 };

        Parser().Execute("tab-prev", Context(workspace, count: 2));

        Assert.Equal(1, workspace.CurrentIndex);
    }

    [Fact]
    public void TabNewWithoutAnArgumentClonesTheCurrentDirectory()
    {
        var workspace = new FakeWorkspace("/home/u");

        Parser().Execute("tab-new", Context(workspace));

        Assert.Equal(2, workspace.Tabs.Count);
        Assert.Equal("/home/u", workspace.Current.Active.Directory);
    }

    [Fact]
    public void TabNewOpensInTheBackgroundWithoutSwitching()
    {
        var workspace = new FakeWorkspace("/a");

        Parser().Execute("tab-new -b", Context(workspace));

        Assert.Equal(2, workspace.Tabs.Count);
        Assert.Equal(0, workspace.CurrentIndex);
    }

    [Fact]
    public void ClosingTheLastTabIsRefusedRatherThanLeavingAnEmptyWindow()
    {
        // Quitting should be an explicit decision, not a side effect of closing a tab.
        var workspace = new FakeWorkspace("/a");
        var messages = new RecordingMessageSink();

        Parser().Execute("tab-close", Context(workspace, messages));

        Assert.Single(workspace.Tabs);
        Assert.Contains("Last tab", messages.Last, StringComparison.Ordinal);
    }

    [Fact]
    public void TabCloseRemovesTheCurrentTab()
    {
        var workspace = new FakeWorkspace("/a", "/b") { CurrentIndex = 1 };

        Parser().Execute("tab-close", Context(workspace));

        Assert.Equal(["/a"], workspace.Directories);
    }

    [Fact]
    public void UndoCloseReopensTheLastClosedTab()
    {
        var workspace = new FakeWorkspace("/a", "/b") { CurrentIndex = 1 };
        var parser = Parser();

        parser.Execute("tab-close", Context(workspace));
        parser.Execute("tab-undo-close", Context(workspace));

        Assert.Contains("/b", workspace.Directories);
    }

    [Fact]
    public void UndoCloseWithNothingToReopenReports()
    {
        var workspace = new FakeWorkspace("/a");
        var messages = new RecordingMessageSink();

        Parser().Execute("tab-undo-close", Context(workspace, messages));

        Assert.Contains("No recently closed", messages.Last, StringComparison.Ordinal);
    }

    [Fact]
    public void TabMoveReordersAndFollowsTheTab()
    {
        var workspace = new FakeWorkspace("/a", "/b", "/c") { CurrentIndex = 2 };

        Parser().Execute("tab-move", Context(workspace, count: 1));

        Assert.Equal(["/c", "/a", "/b"], workspace.Directories);
        Assert.Equal(0, workspace.CurrentIndex);
    }

    [Fact]
    public void LockingTogglesAndSaysWhichWay()
    {
        var parser = Parser();
        var workspace = new FakeWorkspace("/a");
        var messages = new RecordingMessageSink();

        parser.Execute("tab-lock", new CommandContext { Workspace = workspace, Messages = messages });
        Assert.True(workspace.Current.IsLocked);
        Assert.Contains("Locked", messages.Last!, StringComparison.Ordinal);

        parser.Execute("tab-lock", new CommandContext { Workspace = workspace, Messages = messages });
        Assert.False(workspace.Current.IsLocked);
        Assert.Contains("Unlocked", messages.Last!, StringComparison.Ordinal);
    }

    [Fact]
    public void TabMoveTakesAPositionFromTheCommandLine()
    {
        // The count is the vim prefix typed before a key; ":tab-move 1" from the command
        // line is an argument. Only reading the count meant the typed form silently did
        // nothing, which is how this was reported.
        var workspace = new FakeWorkspace("/a", "/b", "/c") { CurrentIndex = 2 };

        Parser().Execute("tab-move 1", Context(workspace));

        Assert.Equal(["/c", "/a", "/b"], workspace.Directories);
        Assert.Equal(0, workspace.CurrentIndex);
    }

    [Fact]
    public void TabMoveStillHonoursACountedKeyPress()
    {
        var workspace = new FakeWorkspace("/a", "/b", "/c") { CurrentIndex = 0 };

        Parser().Execute("tab-move", Context(workspace, count: 3));

        Assert.Equal(["/b", "/c", "/a"], workspace.Directories);
    }

    [Fact]
    public void MovingLeftAndRightSwapsWithTheNeighbour()
    {
        var workspace = new FakeWorkspace("/a", "/b", "/c") { CurrentIndex = 1 };
        var parser = Parser();

        parser.Execute("tab-move-left", Context(workspace));
        Assert.Equal(["/b", "/a", "/c"], workspace.Directories);
        Assert.Equal(0, workspace.CurrentIndex);

        parser.Execute("tab-move-right", Context(workspace));
        Assert.Equal(["/a", "/b", "/c"], workspace.Directories);
    }

    [Fact]
    public void MovingLeftFromTheFrontWraps()
    {
        // A key that stops working at position one is worse than one that wraps, and
        // tab-next already wraps.
        var workspace = new FakeWorkspace("/a", "/b", "/c") { CurrentIndex = 0 };

        Parser().Execute("tab-move-left", Context(workspace));

        Assert.Equal(["/b", "/c", "/a"], workspace.Directories);
        Assert.Equal(2, workspace.CurrentIndex);
    }
}
