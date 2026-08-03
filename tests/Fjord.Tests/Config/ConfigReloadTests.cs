using System.Text;
using Fjord.Config;
using Fjord.Core.FileSystem;

namespace Fjord.Tests.Config;

/// <summary>
/// The path a config edit actually travels: file text -> FjordConfig -> SortSpec ->
/// DirectorySnapshot.SetOrder -> the view the renderer reads.
/// </summary>
public class ConfigReloadTests
{
    private static FileEntry File(string name) => new(name, 100, 1000, EntryFlags.None);

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

    private static FjordConfig LoadEdited(params (string From, string To)[] edits)
    {
        var path = Path.Combine(Path.GetTempPath(), "fjord-tests", Guid.NewGuid().ToString("n") + ".yaml");
        ConfigFile.WriteDefault(path);

        var text = System.IO.File.ReadAllText(path);
        foreach (var (from, to) in edits)
        {
            Assert.Contains(from, text, StringComparison.Ordinal);
            text = text.Replace(from, to, StringComparison.Ordinal);
        }

        System.IO.File.WriteAllText(path, text);

        var result = ConfigFile.Load(path);
        Assert.True(result.Succeeded, result.Error);
        return result.Config;
    }

    [Fact]
    public void EditingSortDescendingReachesTheSpec()
    {
        var config = LoadEdited(("sort_descending: false", "sort_descending: true"));

        Assert.True(config.Listing.SortDescending);
        Assert.True(config.ToSortSpec().Descending);
    }

    [Fact]
    public void ApplyingTheEditedSortReordersTheSnapshot()
    {
        var config = LoadEdited(("sort_descending: false", "sort_descending: true"));
        var snapshot = new DirectorySnapshot("/tmp");
        snapshot.Append([Dir("dir0"), Dir("dir1"), Dir("dir2"), File("a.txt"), File("b.txt")]);

        Assert.Equal(["dir0", "dir1", "dir2", "a.txt", "b.txt"], Names(snapshot));

        snapshot.SetOrder(config.ToSortSpec(), config.ToFilterSpec());

        // Directories still first (grouping precedes the key), each group reversed.
        Assert.Equal(["dir2", "dir1", "dir0", "b.txt", "a.txt"], Names(snapshot));
    }

    [Fact]
    public void EditingShowHiddenRevealsEntriesWithoutReReading()
    {
        var config = LoadEdited(("show_hidden: false", "show_hidden: true"));
        var snapshot = new DirectorySnapshot("/tmp");
        snapshot.Append([File("visible.txt"), new FileEntry(".hidden", 0, 0, EntryFlags.Hidden)]);

        Assert.Equal(["visible.txt"], Names(snapshot));

        snapshot.SetOrder(config.ToSortSpec(), config.ToFilterSpec());

        Assert.Equal([".hidden", "visible.txt"], Names(snapshot));
    }

    [Fact]
    public void EditingSortKeyReachesTheSpec()
    {
        var config = LoadEdited(("sort_key: name", "sort_key: size"));

        Assert.Equal(SortKey.Size, config.ToSortSpec().Key);
        Assert.True(config.ToSortSpec().NeedsSize);
    }

    [Fact]
    public void AUtf8BomDoesNotBreakLoading()
    {
        // Windows PowerShell and several editors write UTF-8 with a BOM by default, so a
        // config saved from one must still parse.
        var path = Path.Combine(Path.GetTempPath(), "fjord-tests", Guid.NewGuid().ToString("n") + ".yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, "listing:\n  sort_descending: true\n", new UTF8Encoding(true));

        var result = ConfigFile.Load(path);

        Assert.True(result.Succeeded, result.Error);
        Assert.True(result.Config.Listing.SortDescending);
    }
}
