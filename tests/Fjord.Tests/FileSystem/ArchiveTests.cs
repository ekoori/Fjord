using System.Formats.Tar;
using System.IO.Compression;
using Fjord.Core.FileSystem;

namespace Fjord.Tests.FileSystem;

public class ArchivePathTests
{
    [Theory]
    [InlineData("src.zip")]
    [InlineData("SRC.ZIP")]
    [InlineData("bundle.tar")]
    [InlineData("bundle.tar.gz")]
    [InlineData("bundle.tgz")]
    [InlineData("thing.jar")]
    public void KnownExtensionsAreArchives(string name) => Assert.True(ArchivePath.IsArchive(name));

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("archive")]
    [InlineData("bundle.gz")]
    public void AnythingElseIsNot(string name) => Assert.False(ArchivePath.IsArchive(name));

    [Fact]
    public void TarGzIsNotMistakenForGz()
    {
        // Longest-first matching. Read as ".gz" it would be refused, and browsing a
        // .tar.gz is the common case of the two.
        Assert.True(ArchivePath.IsArchive("bundle.tar.gz"));
        Assert.False(ArchivePath.IsArchive("data.gz"));
    }

    [Fact]
    public void APathInsideAnArchiveRoundTrips()
    {
        var location = new ArchivePath(Path.Combine("C:", "work", "src.zip"), "lib/net10.0");

        var parsed = ArchivePath.Parse(location.ToString());

        Assert.NotNull(parsed);
        Assert.Equal(location.Archive, parsed!.Value.Archive);
        Assert.Equal("lib/net10.0", parsed.Value.Entry);
    }

    [Fact]
    public void TheArchiveRootRoundTrips()
    {
        var location = new ArchivePath("/home/u/src.zip", string.Empty);

        Assert.Equal(location, ArchivePath.Parse(location.ToString()));
    }

    [Fact]
    public void AnOrdinaryPathIsNotOne()
    {
        Assert.Null(ArchivePath.Parse("/home/u/documents"));
        Assert.Null(ArchivePath.Parse(@"C:\work\src"));
    }

    [Fact]
    public void ABangInSomethingThatIsNotAnArchiveIsJustAName()
    {
        // A bang is a legal filename character on Unix. Treating every one as an archive
        // boundary would make a folder called "hello!" unreachable.
        Assert.Null(ArchivePath.Parse("/home/u/hello!/inside"));
    }

    [Fact]
    public void GoingUpWalksTheEntryThenStops()
    {
        var deep = new ArchivePath("/a/src.zip", "lib/net10.0");

        var up = deep.Parent();
        Assert.Equal("lib", up!.Value.Entry);

        var root = up.Value.Parent();
        Assert.Equal(string.Empty, root!.Value.Entry);

        // Null at the root: leaving an archive is going to the directory holding it, which
        // is an ordinary path and not this type's business.
        Assert.Null(root.Value.Parent());
    }

    [Fact]
    public void ChildAppendsWithForwardSlashesWhateverThePlatform()
    {
        var location = new ArchivePath("/a/src.zip", "lib");

        Assert.Equal("lib/net10.0", location.Child("net10.0").Entry);
    }
}

public class ArchiveReaderTests
{
    private static string MakeZip(TempDirectory temp, params string[] entries)
    {
        var path = Path.Combine(temp.Path, "sample.zip");

        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);

        foreach (var entry in entries)
        {
            var created = zip.CreateEntry(entry);

            if (!entry.EndsWith('/'))
            {
                using var stream = created.Open();
                stream.Write("contents"u8);
            }
        }

        return path;
    }

    private static string MakeTarGz(TempDirectory temp, params string[] files)
    {
        var staging = Path.Combine(temp.Path, "staging");

        foreach (var file in files)
        {
            var full = Path.Combine(staging, file.Replace('/', Path.DirectorySeparatorChar));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "contents");
        }

        var tar = Path.Combine(temp.Path, "sample.tar");
        TarFile.CreateFromDirectory(staging, tar, includeBaseDirectory: false);

        var gz = Path.Combine(temp.Path, "sample.tar.gz");

        using (var source = File.OpenRead(tar))
        using (var target = File.Create(gz))
        using (var zip = new GZipStream(target, CompressionMode.Compress))
        {
            source.CopyTo(zip);
        }

        return gz;
    }

    [Fact]
    public void TheRootListsWhatIsDirectlyInside()
    {
        using var temp = new TempDirectory();
        var zip = MakeZip(temp, "readme.txt", "lib/one.dll", "lib/net10.0/two.dll");

        var entries = ArchiveReader.List(new ArchivePath(zip, string.Empty));

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Name == "readme.txt" && !e.IsDirectory);
        Assert.Contains(entries, e => e.Name == "lib" && e.IsDirectory);
    }

    [Fact]
    public void DirectoriesAreSynthesisedFromEntryNames()
    {
        // Most archives store only files and leave the folders implied by the slashes, so a
        // listing built from recorded entries alone would show nothing at the top level.
        using var temp = new TempDirectory();
        var zip = MakeZip(temp, "deep/nested/inside/file.txt");

        var entries = ArchiveReader.List(new ArchivePath(zip, string.Empty));

        Assert.Single(entries);
        Assert.Equal("deep", entries[0].Name);
        Assert.True(entries[0].IsDirectory);
    }

    [Fact]
    public void ASubdirectoryListsOnlyItsOwnChildren()
    {
        using var temp = new TempDirectory();
        var zip = MakeZip(temp, "readme.txt", "lib/one.dll", "lib/net10.0/two.dll");

        var entries = ArchiveReader.List(new ArchivePath(zip, "lib"));

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Name == "one.dll" && !e.IsDirectory);
        Assert.Contains(entries, e => e.Name == "net10.0" && e.IsDirectory);
    }

    [Fact]
    public void FileSizesSurvive()
    {
        using var temp = new TempDirectory();
        var zip = MakeZip(temp, "readme.txt");

        var entry = ArchiveReader.List(new ArchivePath(zip, string.Empty))[0];

        Assert.Equal("contents"u8.Length, entry.Size);
    }

    [Fact]
    public void AnExplicitDirectoryEntryIsNotAlsoAFile()
    {
        // A zip marks a directory with a trailing slash. Read as a file it would appear
        // twice, once as each.
        using var temp = new TempDirectory();
        var zip = MakeZip(temp, "lib/", "lib/one.dll");

        var entries = ArchiveReader.List(new ArchivePath(zip, string.Empty));

        Assert.Single(entries);
        Assert.True(entries[0].IsDirectory);
    }

    [Fact]
    public void ATarGzReadsTheSameWay()
    {
        using var temp = new TempDirectory();
        var archive = MakeTarGz(temp, "readme.txt", "lib/one.dll");

        var entries = ArchiveReader.List(new ArchivePath(archive, string.Empty));

        Assert.Contains(entries, e => e.Name == "readme.txt" && !e.IsDirectory);
        Assert.Contains(entries, e => e.Name == "lib" && e.IsDirectory);
    }

    [Fact]
    public async Task AnEntryExtractsToARealFile()
    {
        using var temp = new TempDirectory();
        var zip = MakeZip(temp, "lib/one.dll");
        var destination = Path.Combine(temp.Path, "one.dll");

        await ArchiveReader.ExtractAsync(new ArchivePath(zip, "lib/one.dll"), destination);

        Assert.Equal("contents", File.ReadAllText(destination));
    }

    [Fact]
    public async Task ExtractingFromATarGzWorksToo()
    {
        using var temp = new TempDirectory();
        var archive = MakeTarGz(temp, "lib/one.dll");
        var destination = Path.Combine(temp.Path, "one.dll");

        await ArchiveReader.ExtractAsync(new ArchivePath(archive, "lib/one.dll"), destination);

        Assert.Equal("contents", File.ReadAllText(destination));
    }

    [Fact]
    public async Task ExtractingSomethingAbsentLeavesNoFileBehind()
    {
        // Half a file with the right name is worse than none, because nothing afterwards
        // can tell that it is half.
        using var temp = new TempDirectory();
        var zip = MakeZip(temp, "lib/one.dll");
        var destination = Path.Combine(temp.Path, "missing.dll");

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => ArchiveReader.ExtractAsync(new ArchivePath(zip, "lib/nope.dll"), destination));

        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void AnEmptyArchiveListsNothingRatherThanFailing()
    {
        using var temp = new TempDirectory();
        var zip = MakeZip(temp);

        Assert.Empty(ArchiveReader.List(new ArchivePath(zip, string.Empty)));
    }
}

public class ArchiveListingTests
{
    [Fact]
    public async Task TheListerTreatsAnArchiveAsADirectory()
    {
        // The seam that matters: everything above the lister — the pane, history, the
        // cursor, the selection — works on an archive without knowing there is one.
        using var temp = new TempDirectory();
        var zip = Path.Combine(temp.Path, "sample.zip");

        using (var file = File.Create(zip))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            archive.CreateEntry("readme.txt");
            archive.CreateEntry("lib/one.dll");
        }

        var names = new List<string>();
        var location = new ArchivePath(zip, string.Empty).ToString();

        await foreach (var batch in new DirectoryLister().ListAsync(location, ListingOptions.Default, default))
        {
            names.AddRange(batch.ToArray().Select(e => e.Name));
        }

        Assert.Equal(2, names.Count);
        Assert.Contains("readme.txt", names);
        Assert.Contains("lib", names);
    }
}

public class ArchiveGuardTests
{
    private static (Fjord.Commands.CommandParser Parser, Fjord.Tests.Operations.FakeOperationHost Host) Setup()
    {
        var host = new Fjord.Tests.Operations.FakeOperationHost();
        var registry = new Fjord.Commands.CommandRegistry();
        registry.Register(new Fjord.Commands.OperationCommands(
            host, new Fjord.Tests.Operations.FakeOperationFactory()));

        return (new Fjord.Commands.CommandParser(registry), host);
    }

    private static Fjord.Tests.Commands.FakePane InsideArchive() =>
        new(new ArchivePath("/work/src.zip", "lib").ToString(), "one.dll", "two.dll");

    [Theory]
    [InlineData("trash-selection")]
    [InlineData("paste")]
    public void OperationsThatWouldChangeAnArchiveAreRefused(string command)
    {
        // The paths inside an archive do not exist on disk. Without the guard these queue
        // against a path the filesystem has never heard of and fail further down with a
        // message about something else entirely.
        var (parser, host) = Setup();
        var messages = new Fjord.Core.Diagnostics.RecordingMessageSink();
        host.Clipboard = new Fjord.Core.Operations.ClipboardContents(["/elsewhere/a.txt"], Cut: false);

        parser.Execute(command, new Fjord.Commands.CommandContext
        {
            Pane = InsideArchive(),
            Messages = messages,
            Range = new Fjord.Core.Selection.EntryRange(0, 0),
        });

        Assert.Empty(host.Enqueued);
        Assert.Contains("archive", messages.Last!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(":extract", messages.Last!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameCommandsWorkOutsideOne()
    {
        var (parser, host) = Setup();
        var pane = new Fjord.Tests.Commands.FakePane("/work", "a.txt");

        parser.Execute("trash-selection", new Fjord.Commands.CommandContext
        {
            Pane = pane,
            Messages = Fjord.Core.Diagnostics.NullMessageSink.Instance,
        });

        Assert.Single(host.Enqueued);
    }
}
