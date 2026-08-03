using System.Diagnostics;
using System.Globalization;
using Fjord.Core.FileSystem;

namespace Fjord.App;

/// <summary>
/// Measures the enumeration pipeline against a real directory, without a window.
/// </summary>
/// <remarks>
/// This is the gate the plan puts before any feature work past the list: if a 100,000
/// entry folder is not fast here, it never will be, and every feature added on top makes
/// the pipeline harder to change.
/// <para>
/// It measures the lister and the snapshot — enumeration, batching, sorting and the
/// incremental merge — which is where the time goes. Frame time is a separate question,
/// measured by the in-app frame-stats overlay.
/// </para>
/// </remarks>
internal static class Benchmark
{
    public static async Task<int> RunAsync(string directory)
    {
        if (!Directory.Exists(directory))
        {
            await Console.Error.WriteLineAsync($"No such directory: {directory}");
            return 1;
        }

        // A clean baseline, so the reported allocation is this run's and not startup's.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        var allocatedBefore = GC.GetTotalAllocatedBytes();

        var snapshot = new DirectorySnapshot(directory);
        var lister = new DirectoryLister();
        var options = ListingOptions.For(
            snapshot.Sort, snapshot.Filter, sizeColumnVisible: true, modifiedColumnVisible: true);

        var clock = Stopwatch.StartNew();
        TimeSpan? firstBatch = null;
        var batches = 0;

        await foreach (var batch in lister.ListAsync(directory, options, CancellationToken.None))
        {
            snapshot.Append(batch.Span);
            firstBatch ??= clock.Elapsed;
            batches++;
        }

        snapshot.Complete();
        var total = clock.Elapsed;

        var view = snapshot.View;
        var managed = GC.GetTotalMemory(forceFullCollection: true);

        Console.WriteLine($"directory          {directory}");
        Console.WriteLine($"entries            {snapshot.TotalCount:N0} enumerated, {view.Count:N0} visible");
        Console.WriteLine($"batches            {batches:N0}");
        Console.WriteLine($"time to first      {Ms(firstBatch ?? TimeSpan.Zero)}   (target < 50 ms)");
        Console.WriteLine($"time to complete   {Ms(total)}   (target < 1,500 ms)");
        Console.WriteLine($"throughput         {Rate(snapshot.TotalCount, total)} entries/sec");
        Console.WriteLine($"managed heap       {Mb(managed)}   (target < 20 MB)");
        Console.WriteLine($"allocated          {Mb(GC.GetTotalAllocatedBytes() - allocatedBefore)}");
        Console.WriteLine(
            $"collections        gen0 {GC.CollectionCount(0) - gen0}, " +
            $"gen1 {GC.CollectionCount(1) - gen1}, gen2 {GC.CollectionCount(2) - gen2}");

        VerifyOrdering(view);
        return 0;
    }

    /// <summary>
    /// Confirms the merge actually produced a sorted view.
    /// </summary>
    /// <remarks>
    /// A benchmark that reports a fast time for a wrong answer is worse than no benchmark.
    /// </remarks>
    private static void VerifyOrdering(SnapshotView view)
    {
        var comparer = SortSpec.Default.CreateComparer();

        for (var row = 1; row < view.Count; row++)
        {
            if (comparer.Compare(view[row - 1], view[row]) > 0)
            {
                Console.WriteLine($"ordering           BROKEN at row {row}: " +
                    $"'{view[row - 1].Name}' before '{view[row].Name}'");
                return;
            }
        }

        Console.WriteLine("ordering           verified sorted");
    }

    private static string Ms(TimeSpan value) =>
        value.TotalMilliseconds.ToString("N1", CultureInfo.InvariantCulture).PadLeft(9) + " ms";

    private static string Mb(long bytes) =>
        (bytes / 1024.0 / 1024.0).ToString("N2", CultureInfo.InvariantCulture).PadLeft(9) + " MB";

    private static string Rate(int count, TimeSpan elapsed) =>
        elapsed.TotalSeconds <= 0
            ? "n/a"
            : (count / elapsed.TotalSeconds).ToString("N0", CultureInfo.InvariantCulture);
}
