using Fjord.Core.Navigation;

namespace Fjord.Tests.Navigation;

public class NavigationHistoryTests
{
    private static NavigationHistory WithVisits(params string[] directories)
    {
        var history = new NavigationHistory();
        foreach (var directory in directories)
        {
            history.Push(directory);
        }

        return history;
    }

    [Fact]
    public void StartsEmpty()
    {
        var history = new NavigationHistory();

        Assert.Null(history.Current);
        Assert.False(history.CanGoBack);
        Assert.False(history.CanGoForward);
    }

    [Fact]
    public void PushAdvancesTheCursor()
    {
        var history = WithVisits("/a", "/b");

        Assert.Equal("/b", history.Current!.Value.Directory);
        Assert.True(history.CanGoBack);
        Assert.False(history.CanGoForward);
    }

    [Fact]
    public void BackAndForwardWalkTheList()
    {
        var history = WithVisits("/a", "/b", "/c");

        Assert.Equal("/b", history.Back()!.Value.Directory);
        Assert.Equal("/a", history.Back()!.Value.Directory);
        Assert.Null(history.Back());

        Assert.Equal("/b", history.Forward()!.Value.Directory);
        Assert.Equal("/c", history.Forward()!.Value.Directory);
        Assert.Null(history.Forward());
    }

    [Fact]
    public void BackAcceptsACount()
    {
        // <count>Ctrl-o.
        var history = WithVisits("/a", "/b", "/c", "/d");

        Assert.Equal("/a", history.Back(3)!.Value.Directory);
    }

    [Fact]
    public void CountsClampRatherThanFail()
    {
        var history = WithVisits("/a", "/b");

        Assert.Equal("/a", history.Back(99)!.Value.Directory);
        Assert.Equal("/b", history.Forward(99)!.Value.Directory);
    }

    [Fact]
    public void ZeroOrNegativeCountsDoNothing()
    {
        var history = WithVisits("/a", "/b");

        Assert.Null(history.Back(0));
        Assert.Equal("/b", history.Current!.Value.Directory);
    }

    [Fact]
    public void PushAfterBackDiscardsTheForwardTail()
    {
        var history = WithVisits("/a", "/b", "/c");
        history.Back();

        history.Push("/d");

        Assert.False(history.CanGoForward);
        Assert.Equal(["/a", "/b", "/d"], history.Entries.Select(e => e.Directory));
    }

    [Fact]
    public void RevisitingTheCurrentDirectoryDoesNotDuplicateIt()
    {
        // Entering `..` and coming straight back should not fill the history.
        var history = WithVisits("/a");

        history.Push("/a", "child");

        Assert.Equal(1, history.Count);
        Assert.Equal("child", history.Current!.Value.CursorName);
    }

    [Fact]
    public void CursorIsRememberedByNameNotIndex()
    {
        // Indices do not survive a resort, a filter change, or files appearing while
        // the user was elsewhere.
        var history = new NavigationHistory();
        history.Push("/a", "notes.txt");
        history.Push("/b");

        Assert.Equal("notes.txt", history.Back()!.Value.CursorName);
    }

    [Fact]
    public void UpdateCursorRecordsWhereTheUserWasBeforeLeaving()
    {
        var history = new NavigationHistory();
        history.Push("/a");
        history.UpdateCursor("selected.txt");
        history.Push("/b");

        Assert.Equal("selected.txt", history.Back()!.Value.CursorName);
    }

    [Fact]
    public void UpdateCursorOnEmptyHistoryIsSafe()
    {
        var history = new NavigationHistory();

        history.UpdateCursor("x");

        Assert.Null(history.Current);
    }

    [Fact]
    public void OldestEntriesAreTrimmedAtCapacity()
    {
        var history = new NavigationHistory(capacity: 3);
        history.Push("/a");
        history.Push("/b");
        history.Push("/c");
        history.Push("/d");

        Assert.Equal(3, history.Count);
        Assert.Equal(["/b", "/c", "/d"], history.Entries.Select(e => e.Directory));
        Assert.Equal("/d", history.Current!.Value.Directory);
    }

    [Fact]
    public void TrimmingKeepsTheCursorOnTheSameEntry()
    {
        // The index has to shift with the removal or back-navigation jumps.
        var history = new NavigationHistory(capacity: 3);
        history.Push("/a");
        history.Push("/b");
        history.Push("/c");
        history.Push("/d");

        Assert.Equal("/c", history.Back()!.Value.Directory);
        Assert.Equal("/b", history.Back()!.Value.Directory);
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void CapacityMustBePositive() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new NavigationHistory(0));

    [Fact]
    public void ClearResetsEverything()
    {
        var history = WithVisits("/a", "/b");

        history.Clear();

        Assert.Equal(0, history.Count);
        Assert.Null(history.Current);
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void VisitTimeIsRecorded()
    {
        var time = new FakeTime(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        var history = new NavigationHistory();

        history.Push("/a", null, time);

        Assert.Equal(new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc), history.Current!.Value.VisitedUtc);
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
