using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Fjord.Core.FileSystem;

/// <summary>
/// Reads directories with <see cref="FileSystemEnumerable{TResult}"/>.
/// </summary>
/// <remarks>
/// <see cref="System.IO.DirectoryInfo.EnumerateFileSystemInfos()"/> is the obvious
/// alternative and the wrong one: it allocates a <c>FileSystemInfo</c> object and a full
/// path string per entry, which for 100,000 entries is 100,000 objects and roughly 16 MB
/// of strings — and it is implemented on top of this same type anyway.
/// <para>
/// Hand-written P/Invoke is the other alternative, and also wrong:
/// <see cref="FileSystemEnumerable{TResult}"/> <i>is</i> the P/Invoke
/// (<c>NtQueryDirectoryFile</c> on Windows, <c>opendir</c>/<c>readdir</c> on Unix), and
/// reimplementing it forfeits long-path handling, reparse-point handling and
/// security-checked path building for no gain.
/// </para>
/// </remarks>
public sealed class DirectoryLister : IDirectoryLister
{
    /// <summary>
    /// Batch sizes before settling at <see cref="ListingOptions.BatchSize"/>.
    /// </summary>
    /// <remarks>
    /// A ten-file folder arrives in one hop, and a 100,000-file folder fills the first
    /// screen within the first 128 entries instead of waiting for a full-size batch.
    /// </remarks>
    private static readonly int[] BatchRamp = [128, 512];

    /// <summary>
    /// Batches in flight before the producer parks.
    /// </summary>
    /// <remarks>
    /// This is the backpressure that keeps a fast disk from building an unbounded queue
    /// in front of a slower consumer. Four batches is about 8,000 entries.
    /// </remarks>
    private const int ChannelCapacity = 4;

    public async IAsyncEnumerable<ReadOnlyMemory<FileEntry>> ListAsync(
        string directory,
        ListingOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<FileEntry[]>(
            new BoundedChannelOptions(ChannelCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

        // Abandoning the iteration (a `break`, or navigating away) must tear the producer
        // down too, or it parks forever on a full channel holding an open directory handle.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = cts.Token;

        var producer = Task.Run(() => ProduceAsync(directory, options, channel.Writer, token), token);

        try
        {
            await foreach (var batch in channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                yield return batch;
            }
        }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(false);

            // The producer captures its own failures into the channel, so this only
            // observes the task and swallows the cancellation it was just handed.
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when the consumer stopped early.
            }
        }
    }

    private static async Task ProduceAsync(
        string directory,
        ListingOptions options,
        ChannelWriter<FileEntry[]> writer,
        CancellationToken ct)
    {
        Exception? failure = null;

        try
        {
            var enumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = false,

                // Per-entry access failures are normal in any real tree and must not
                // abort the listing; failing to open the directory itself still throws.
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                MatchType = MatchType.Simple,
                AttributesToSkip = options.IncludeHidden
                    ? 0
                    : FileAttributes.Hidden | FileAttributes.System,
                BufferSize = options.BufferSize,
            };

            var enumerable = new FileSystemEnumerable<FileEntry>(
                directory,
                options.NeedsMetadata ? TransformWithMetadata : TransformNameOnly,
                enumerationOptions);

            var batchSize = BatchRamp[0];
            var ramp = 0;
            var batch = new FileEntry[batchSize];
            var filled = 0;

            foreach (var entry in enumerable)
            {
                batch[filled++] = entry;

                if (filled < batchSize)
                {
                    continue;
                }

                // Checked once per batch, not once per entry. A cancelled 100,000-entry
                // enumeration therefore stops within one batch — a few milliseconds —
                // which is what makes abandoned navigation actually stop costing anything.
                ct.ThrowIfCancellationRequested();
                await writer.WriteAsync(batch, ct).ConfigureAwait(false);

                ramp++;
                batchSize = ramp < BatchRamp.Length ? BatchRamp[ramp] : options.BatchSize;
                batch = new FileEntry[batchSize];
                filled = 0;
            }

            if (filled > 0)
            {
                await writer.WriteAsync(batch[..filled], ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        writer.Complete(failure);
    }

    /// <summary>
    /// Builds an entry including size and timestamp.
    /// </summary>
    /// <remarks>
    /// Must stay <c>static</c>. As an instance lambda or a closure this would allocate a
    /// delegate — or worse, a display class — for every entry in the directory.
    /// </remarks>
    private static FileEntry TransformWithMetadata(ref FileSystemEntry entry) => new(
        entry.FileName.ToString(),
        entry.IsDirectory ? -1 : entry.Length,
        entry.LastWriteTimeUtc.UtcTicks,
        MapFlags(entry.Attributes, entry.IsDirectory, entry.IsHidden));

    /// <summary>
    /// Builds an entry with the name only.
    /// </summary>
    /// <remarks>
    /// Used when neither the sort nor a visible column needs metadata. On Windows this
    /// saves little, since the attributes arrive in the same buffer as the name; on Unix
    /// it avoids a <c>stat</c> syscall per entry, which on a network mount is the entire
    /// cost of the listing.
    /// </remarks>
    private static FileEntry TransformNameOnly(ref FileSystemEntry entry) => new(
        entry.FileName.ToString(),
        -1,
        0,
        MapFlags(entry.Attributes, entry.IsDirectory, entry.IsHidden) | EntryFlags.MetadataPending);

    private static EntryFlags MapFlags(FileAttributes attributes, bool isDirectory, bool isHidden)
    {
        var flags = EntryFlags.None;

        if (isDirectory)
        {
            flags |= EntryFlags.Directory;
        }

        if (isHidden)
        {
            flags |= EntryFlags.Hidden;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            flags |= EntryFlags.ReparsePoint;
        }

        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            flags |= EntryFlags.ReadOnly;
        }

        if ((attributes & FileAttributes.System) != 0)
        {
            flags |= EntryFlags.System;
        }

        return flags;
    }
}
