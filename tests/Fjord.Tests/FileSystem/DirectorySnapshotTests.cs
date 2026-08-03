using Fjord.Core.FileSystem;

namespace Fjord.Tests.FileSystem;

public class DirectorySnapshotTests
{
    private static FileEntry File(string name, long size = 100, long ticks = 1000) =>
        new(name, size, ticks, EntryFlags.None);

    private static FileEntry Dir(string name) => new(name, -1, 1000, EntryFlags.Directory);

    private static string[] Names(DirectorySnapshot snapshot)
    {
        var view = snapshot.View;
        var names = new string[view.Count];
        for (var i = 0; i < view.Count; i++)
        {
            names[i] = view[i].Name;
        }

        return names;
    }

    [Fact]
    public void StartsEmpty()
    {
        var snapshot = new DirectorySnapshot("/tmp");

        Assert.True(snapshot.View.IsEmpty);
        Assert.False(snapshot.IsComplete);
        Assert.Equal(0, snapshot.TotalCount);
    }

    [Fact]
    public void AppendSortsWithinABatch()
    {
        var snapshot = new DirectorySnapshot("/tmp");
        snapshot.Append([File("c.txt"), File("a.txt"), File("b.txt")]);

        Assert.Equal(["a.txt", "b.txt", "c.txt"], Names(snapshot));
    }

    [Fact]
    public void AppendMergesLaterBatchesIntoOrder()
    {
        // Batches arrive in whatever order the filesystem hands them over; the merged
        // view must stay globally sorted, not sorted per batch.
        var snapshot = new DirectorySnapshot("/tmp");
        snapshot.Append([File("b.txt"), File("d.txt")]);
        snapshot.Append([File("a.txt"), File("c.txt")]);
        snapshot.Append([File("e.txt")]);

        Assert.Equal(["a.txt", "b.txt", "c.txt", "d.txt", "e.txt"], Names(snapshot));
    }

    [Fact]
    public void DirectoriesSortFirst()
    {
        var snapshot = new DirectorySnapshot("/tmp");
        snapshot.Append([File("aaa.txt"), Dir("zzz"), File("bbb.txt"), Dir("mmm")]);

        Assert.Equal(["mmm", "zzz", "aaa.txt", "bbb.txt"], Names(snapshot));
    }

    [Fact]
    public void DirectoriesStayFirstWhenDescending()
    {
        // Grouping is applied before the key, or descending order dumps directories at
        // the bottom.
        var snapshot = new DirectorySnapshot("/tmp", new SortSpec(Descending: true));
        snapshot.Append([File("aaa.txt"), Dir("zzz"), File("bbb.txt"), Dir("mmm")]);

        Assert.Equal(["zzz", "mmm", "bbb.txt", "aaa.txt"], Names(snapshot));
    }

    [Fact]
    public void NaturalOrderIsUsedForNames()
    {
        var snapshot = new DirectorySnapshot("/tmp");
        snapshot.Append([File("file10.txt"), File("file2.txt"), File("file1.txt")]);

        Assert.Equal(["file1.txt", "file2.txt", "file10.txt"], Names(snapshot));
    }

    [Fact]
    public void SortsBySize()
    {
        var snapshot = new DirectorySnapshot("/tmp", new SortSpec(SortKey.Size));
        snapshot.Append([File("big", 900), File("small", 10), File("mid", 500)]);

        Assert.Equal(["small", "mid", "big"], Names(snapshot));
    }

    [Fact]
    public void TiesOnANonNameKeyFallBackToTheName()
    {
        // Equal sizes must still produce a stable, deterministic order.
        var snapshot = new DirectorySnapshot("/tmp", new SortSpec(SortKey.Size));
        snapshot.Append([File("c", 100), File("a", 100), File("b", 100)]);

        Assert.Equal(["a", "b", "c"], Names(snapshot));
    }

    [Fact]
    public void FiltersHiddenEntries()
    {
        var snapshot = new DirectorySnapshot("/tmp");
        snapshot.Append([File("visible"), new FileEntry(".hidden", 0, 0, EntryFlags.Hidden)]);

        Assert.Equal(["visible"], Names(snapshot));
        Assert.Equal(2, snapshot.TotalCount);
    }

    [Fact]
    public void SetOrderResortsEverythingAlreadyLoaded()
    {
        var snapshot = new DirectorySnapshot("/tmp");
        snapshot.Append([File("a", 300), File("b", 100), File("c", 200)]);

        snapshot.SetOrder(new SortSpec(SortKey.Size), FilterSpec.Default);

        Assert.Equal(["b", "c", "a"], Names(snapshot));
    }

    [Fact]
    public void SetOrderCanRevealHiddenEntries()
    {
        var snapshot = new DirectorySnapshot("/tmp");
        snapshot.Append([File("visible"), new FileEntry(".hidden", 0, 0, EntryFlags.Hidden)]);

        snapshot.SetOrder(SortSpec.Default, new FilterSpec(ShowHidden: true));

        Assert.Equal([".hidden", "visible"], Names(snapshot));
    }

    [Fact]
    public void NameFilterAppliesToTheView()
    {
        var snapshot = new DirectorySnapshot("/tmp");
        snapshot.Append([File("report.pdf"), File("notes.txt"), File("REPORT-2.pdf")]);

        snapshot.SetOrder(SortSpec.Default, new FilterSpec(ShowHidden: true, NameContains: "report"));

        // Matching is case-insensitive, and '-' (0x2D) orders before '.' (0x2E).
        Assert.Equal(["REPORT-2.pdf", "report.pdf"], Names(snapshot));
    }

    [Fact]
    public void SourceIndexIsStableAcrossAResort()
    {
        // Background metadata is keyed by source index precisely because rows renumber.
        var snapshot = new DirectorySnapshot("/tmp");
        snapshot.Append([File("a", 300), File("b", 100)]);

        var sourceOfA = snapshot.View.SourceIndex(snapshot.View.IndexOfName("a", StringComparison.Ordinal));
        snapshot.SetOrder(new SortSpec(SortKey.Size), FilterSpec.Default);
        var sourceOfAAfter = snapshot.View.SourceIndex(snapshot.View.IndexOfName("a", StringComparison.Ordinal));

        Assert.Equal(sourceOfA, sourceOfAAfter);
    }

    [Fact]
    public void ViewCapturedBeforeAnAppendStaysValid()
    {
        // The renderer holds a view for the duration of a frame while the producer keeps
        // appending. The captured pair must not tear or go out of range.
        var snapshot = new DirectorySnapshot("/tmp");
        snapshot.Append([File("a"), File("b")]);

        var captured = snapshot.View;
        Assert.Equal(2, captured.Count);

        // Force a grow well past the initial capacity.
        for (var batch = 0; batch < 20; batch++)
        {
            var entries = new FileEntry[200];
            for (var i = 0; i < entries.Length; i++)
            {
                entries[i] = File($"f{batch}-{i}");
            }

            snapshot.Append(entries);
        }

        Assert.Equal(2, captured.Count);
        Assert.Equal("a", captured[0].Name);
        Assert.Equal("b", captured[1].Name);
        Assert.Equal(4002, snapshot.View.Count);
    }

    [Fact]
    public void ResortReplacesTheViewEvenThoughTheCountIsUnchanged()
    {
        // The renderer caches per-row text and needs to know when to drop it. Count is not
        // that signal: a re-sort rewrites every row's content and leaves the count alone.
        // Keying on count let the list draw the old text in the new order's positions.
        var snapshot = new DirectorySnapshot("/tmp");
        snapshot.Append([File("a", 300), File("b", 100), File("c", 200)]);

        var before = snapshot.View;
        snapshot.SetOrder(new SortSpec(SortKey.Size), FilterSpec.Default);
        var after = snapshot.View;

        Assert.Equal(before.Count, after.Count);
        Assert.False(after.IsSameAs(before));
        Assert.NotEqual(before[0].Name, after[0].Name);
    }

    [Fact]
    public void AppendAlsoReplacesTheView()
    {
        // Merging a batch shifts which entry sits at a given row, so cached rows are just
        // as stale here as after a re-sort.
        var snapshot = new DirectorySnapshot("/tmp");
        snapshot.Append([File("b")]);

        var before = snapshot.View;
        snapshot.Append([File("a")]);

        Assert.False(snapshot.View.IsSameAs(before));
        Assert.Equal("a", snapshot.View[0].Name);
    }

    [Fact]
    public void ReadingTheViewTwiceWithNoChangeIsTheSameView()
    {
        var snapshot = new DirectorySnapshot("/tmp");
        snapshot.Append([File("a")]);

        Assert.True(snapshot.View.IsSameAs(snapshot.View));
    }

    [Fact]
    public void CompleteRecordsFailure()
    {
        var snapshot = new DirectorySnapshot("/tmp");
        var error = new UnauthorizedAccessException("denied");

        snapshot.Complete(error);

        Assert.True(snapshot.IsComplete);
        Assert.Same(error, snapshot.Error);
    }

    [Fact]
    public void IndexOfNameReturnsMinusOneWhenAbsent() =>
        Assert.Equal(-1, new DirectorySnapshot("/tmp").View.IndexOfName("x", StringComparison.Ordinal));

    [Fact]
    public void MergingManyBatchesProducesAFullyOrderedView()
    {
        // The merge is the part most likely to go subtly wrong; check it at a scale
        // where an off-by-one shows up.
        var snapshot = new DirectorySnapshot("/tmp");
        var random = new Random(20260803);
        var expected = new List<string>();

        for (var batch = 0; batch < 40; batch++)
        {
            var entries = new FileEntry[128];
            for (var i = 0; i < entries.Length; i++)
            {
                var name = $"f{random.Next(1_000_000):D7}-{batch}-{i}";
                entries[i] = File(name);
                expected.Add(name);
            }

            snapshot.Append(entries);
        }

        expected.Sort(NaturalComparer.Instance);

        Assert.Equal(expected, Names(snapshot));
    }
}
