using Fjord.Core.FileSystem;

namespace Fjord.Tests.FileSystem;

public class DirectoryListerTests
{
    private static readonly DirectoryLister Lister = new();

    private static async Task<List<FileEntry>> ListAllAsync(
        string directory, ListingOptions? options = null, CancellationToken ct = default)
    {
        var all = new List<FileEntry>();
        await foreach (var batch in Lister.ListAsync(directory, options ?? ListingOptions.Default, ct))
        {
            all.AddRange(batch.ToArray());
        }

        return all;
    }

    [Fact]
    public async Task ListsFilesAndDirectories()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("alpha.txt");
        temp.CreateFile("beta.md");
        temp.CreateDir("gamma");

        var entries = await ListAllAsync(temp.Path);

        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, e => e.Name == "alpha.txt" && !e.IsDirectory);
        Assert.Contains(entries, e => e.Name == "gamma" && e.IsDirectory);
    }

    [Fact]
    public async Task StoresTheNameOnlyNeverAPath()
    {
        // The whole memory argument for FileEntry rests on this.
        using var temp = new TempDirectory();
        temp.CreateFile("alpha.txt");

        var entry = Assert.Single(await ListAllAsync(temp.Path));

        Assert.Equal("alpha.txt", entry.Name);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, entry.Name);
    }

    [Fact]
    public async Task EmptyDirectoryYieldsNothing()
    {
        using var temp = new TempDirectory();

        Assert.Empty(await ListAllAsync(temp.Path));
    }

    [Fact]
    public async Task MissingDirectoryThrows()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "nope");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => ListAllAsync(missing));
    }

    [Fact]
    public async Task HiddenEntriesAreExcludedByDefault()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("visible.txt");
        temp.CreateHiddenFile(".secret");

        var visible = await ListAllAsync(temp.Path);
        Assert.Equal("visible.txt", Assert.Single(visible).Name);

        var all = await ListAllAsync(temp.Path, ListingOptions.Default with { IncludeHidden = true });
        Assert.Equal(2, all.Count);
        Assert.Contains(all, e => e.Name == ".secret" && e.IsHidden);
    }

    [Fact]
    public async Task PopulatesSizeAndTimestampWhenAsked()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("sized.bin", size: 4096);
        var before = DateTime.UtcNow.AddMinutes(-1);

        var entry = Assert.Single(await ListAllAsync(temp.Path));

        Assert.True(entry.HasMetadata);
        Assert.Equal(4096, entry.Size);
        Assert.InRange(entry.ModifiedUtc, before, DateTime.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task SkipsMetadataWhenNotNeeded()
    {
        // On Unix this is a stat syscall per entry; on a network mount it is the entire
        // cost of the listing, so "sort by name with no size column" must not pay it.
        using var temp = new TempDirectory();
        temp.CreateFile("sized.bin", size: 4096);

        var options = ListingOptions.Default with { NeedsSize = false, NeedsTimes = false };
        var entry = Assert.Single(await ListAllAsync(temp.Path, options));

        Assert.False(entry.HasMetadata);
        Assert.Equal(-1, entry.Size);
    }

    [Fact]
    public async Task DirectoriesReportSizeMinusOne()
    {
        using var temp = new TempDirectory();
        temp.CreateDir("folder");

        Assert.Equal(-1, Assert.Single(await ListAllAsync(temp.Path)).Size);
    }

    [Fact]
    public async Task BatchesRampUpSoTheFirstScreenArrivesImmediately()
    {
        using var temp = new TempDirectory();
        temp.CreateMany(1500);

        var sizes = new List<int>();
        await foreach (var batch in Lister.ListAsync(temp.Path, ListingOptions.Default, default))
        {
            sizes.Add(batch.Length);
        }

        // 128, then 512, then full-size batches. The first batch is what the user sees.
        Assert.Equal(128, sizes[0]);
        Assert.Equal(512, sizes[1]);
        Assert.Equal(1500, sizes.Sum());
    }

    [Fact]
    public async Task SmallDirectoryArrivesInASingleBatch()
    {
        using var temp = new TempDirectory();
        temp.CreateMany(10);

        var batches = 0;
        await foreach (var _ in Lister.ListAsync(temp.Path, ListingOptions.Default, default))
        {
            batches++;
        }

        Assert.Equal(1, batches);
    }

    [Fact]
    public async Task ATokenAlreadyCancelledYieldsNothing()
    {
        using var temp = new TempDirectory();
        temp.CreateMany(200);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in Lister.ListAsync(temp.Path, ListingOptions.Default, cts.Token))
            {
                Assert.Fail("nothing should be yielded for an already-cancelled token");
            }
        });
    }

    [Fact]
    public async Task CancellationStopsEnumerationPromptly()
    {
        // Large enough that the producer cannot possibly finish first. With a smaller tree
        // this raced: the enumeration is a handful of batches against a channel capacity of
        // four, so reading one batch frees a slot, the producer writes its last batch and
        // completes normally, and no cancellation is ever observed. That is correct
        // behaviour — work that finished before the cancel landed is simply done — but it
        // makes an assertion about cancelling meaningless. Here the producer would need the
        // consumer to keep reading, and it stops after one.
        const int total = 20_000;

        using var temp = new TempDirectory();
        temp.CreateMany(total);

        using var cts = new CancellationTokenSource();
        var seen = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var batch in Lister.ListAsync(temp.Path, ListingOptions.Default, cts.Token))
            {
                seen += batch.Length;
                await cts.CancelAsync();
            }
        });

        // Cancellation is checked per batch, so it stops within a batch or two of the
        // buffered ones rather than running the remaining thousands to completion.
        Assert.True(seen < total, $"expected to stop early, saw {seen} of {total}");
    }

    [Fact]
    public async Task AbandoningTheIterationTearsDownTheProducer()
    {
        // Navigating away mid-load must not leave a producer parked on a full channel
        // holding an open directory handle.
        using var temp = new TempDirectory();
        temp.CreateMany(5000);

        await foreach (var _ in Lister.ListAsync(temp.Path, ListingOptions.Default, default))
        {
            break;
        }

        // The handle is released, so the fixture can be deleted while still in scope.
        Directory.Delete(temp.Path, recursive: true);
        Assert.False(Directory.Exists(temp.Path));
    }

    [Fact]
    public async Task ExtensionIsComputedDuringEnumeration()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("archive.tar.gz");
        temp.CreateFile("Makefile");
        temp.CreateHiddenFile(".bashrc");

        var entries = (await ListAllAsync(temp.Path, ListingOptions.Default with { IncludeHidden = true }))
            .ToDictionary(e => e.Name);

        Assert.Equal(".gz", entries["archive.tar.gz"].Extension.ToString());
        Assert.Equal("archive.tar", entries["archive.tar.gz"].BaseName.ToString());

        Assert.False(entries["Makefile"].HasExtension);

        // A dotfile is a name, not an extension — unlike Path.GetExtension.
        Assert.False(entries[".bashrc"].HasExtension);
    }

    [Fact]
    public async Task EntriesRemovedMidEnumerationDoNotFail()
    {
        using var temp = new TempDirectory();
        temp.CreateMany(300);

        var deleted = false;
        var seen = 0;

        await foreach (var batch in Lister.ListAsync(temp.Path, ListingOptions.Default, default))
        {
            seen += batch.Length;

            if (!deleted)
            {
                deleted = true;
                foreach (var file in Directory.EnumerateFiles(temp.Path).Take(50))
                {
                    File.Delete(file);
                }
            }
        }

        // The count is racy by nature; not throwing is the assertion.
        Assert.InRange(seen, 1, 300);
    }
}
