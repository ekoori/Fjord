using Fjord.Core.Navigation;

namespace Fjord.Tests.Navigation;

public class MarkFileTests
{
    [Fact]
    public void AnUnsetMarkReadsAsNull()
    {
        Assert.Null(new MarkFile().Get("nothing"));
        Assert.Empty(new MarkFile().Names);
    }

    [Fact]
    public void AMarkRemembersTheDirectoryAndTheCursor()
    {
        var marks = new MarkFile();
        marks.Set("projects", "/home/igor/projects", "fjord");

        var mark = marks.Get("projects");

        Assert.NotNull(mark);
        Assert.Equal("/home/igor/projects", mark!.Value.Directory);
        Assert.Equal("fjord", mark.Value.CursorName);
    }

    [Fact]
    public void SettingTheSameNameReplacesIt()
    {
        // Deliberately silent. Re-marking a place you have moved is the common case, and a
        // prompt on every one would make quickmarks not worth setting.
        var marks = new MarkFile();
        marks.Set("work", "/first");
        marks.Set("work", "/second");

        Assert.Equal("/second", marks.Get("work")!.Value.Directory);
        Assert.Single(marks.Names);
    }

    [Fact]
    public void MarksAreListedInNameOrder()
    {
        var marks = new MarkFile();
        marks.Set("zebra", "/z");
        marks.Set("apple", "/a");
        marks.Set("mango", "/m");

        Assert.Equal(["apple", "mango", "zebra"], marks.Names);
    }

    [Fact]
    public void NamesAreCaseInsensitive()
    {
        // ":cd Work" and ":cd work" are the same request. Two quickmarks differing only in
        // case would be a trap the user set without meaning to.
        var marks = new MarkFile();
        marks.Set("Work", "/work");

        Assert.Equal("/work", marks.Get("work")!.Value.Directory);
        Assert.Single(marks.Names);
    }

    [Theory]
    [InlineData("work")]
    [InlineData("my-project")]
    [InlineData("my_project")]
    [InlineData("v2")]
    public void OrdinaryWordsAreValidNames(string name) => Assert.True(MarkFile.IsValidName(name));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    [InlineData("two words")]
    [InlineData("src/nested")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("~")]
    [InlineData("C:")]
    [InlineData("12")]
    public void SomethingThatCouldBeAPathIsNot(string? name)
    {
        // The names share a namespace with directories, so a quickmark that could be read
        // as a path would shadow it. "12" is excluded for a different reason: a bare number
        // reads as a count everywhere else in the app.
        Assert.False(MarkFile.IsValidName(name));
    }

    [Fact]
    public void SettingAnInvalidNameIsRefused() =>
        Assert.Throws<ArgumentException>(() => new MarkFile().Set("two words", "/somewhere"));

    [Fact]
    public void ClearingOneLeavesTheRest()
    {
        var marks = new MarkFile();
        marks.Set("alpha", "/a");
        marks.Set("beta", "/b");

        marks.Clear("alpha");

        Assert.Null(marks.Get("alpha"));
        Assert.NotNull(marks.Get("beta"));
    }

    [Fact]
    public void ClearingWithoutANameForgetsEverything()
    {
        var marks = new MarkFile();
        marks.Set("alpha", "/a");
        marks.Set("beta", "/b");

        marks.Clear();

        Assert.Empty(marks.Names);
    }

    [Fact]
    public void LoadReplacesTheWholeSet()
    {
        var marks = new MarkFile();
        marks.Set("stale", "/stale");

        marks.Load(new Dictionary<string, Mark>
        {
            ["restored"] = new("/restored", "entry"),
        });

        Assert.Null(marks.Get("stale"));
        Assert.Equal("/restored", marks.Get("restored")!.Value.Directory);
    }

    [Fact]
    public void AMarkNeedsADirectory()
    {
        // A mark pointing nowhere would fail at :cd time, a long way from the mistake.
        Assert.Throws<ArgumentException>(() => new MarkFile().Set("here", string.Empty));
    }
}
