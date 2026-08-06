using Fjord.Core.Navigation;

namespace Fjord.Tests.Navigation;

public class MarkFileTests
{
    [Fact]
    public void AnUnsetMarkReadsAsNull()
    {
        Assert.Null(new MarkFile().Get('a'));
        Assert.Empty(new MarkFile().Names);
    }

    [Fact]
    public void AMarkRemembersTheDirectoryAndTheCursor()
    {
        var marks = new MarkFile();
        marks.Set('p', "/home/igor/projects", "fjord");

        var mark = marks.Get('p');

        Assert.NotNull(mark);
        Assert.Equal("/home/igor/projects", mark!.Value.Directory);
        Assert.Equal("fjord", mark.Value.CursorName);
    }

    [Fact]
    public void SettingTheSameNameReplacesIt()
    {
        // Deliberately silent. A mark is a scratch position, and a prompt on every ma
        // would make marks not worth using.
        var marks = new MarkFile();
        marks.Set('a', "/first");
        marks.Set('a', "/second");

        Assert.Equal("/second", marks.Get('a')!.Value.Directory);
        Assert.Single(marks.Names);
    }

    [Fact]
    public void MarksAreListedInNameOrder()
    {
        var marks = new MarkFile();
        marks.Set('z', "/z");
        marks.Set('a', "/a");
        marks.Set('m', "/m");

        Assert.Equal(['a', 'm', 'z'], marks.Names);
    }

    [Fact]
    public void CaseIsSignificant()
    {
        // Unlike registers, which fold. Marks are cheap and there are only so many keys;
        // a and A being different doubles the alphabet at no cost in confusion, because
        // both are visible in the listing.
        var marks = new MarkFile();
        marks.Set('a', "/lower");
        marks.Set('A', "/upper");

        Assert.Equal("/lower", marks.Get('a')!.Value.Directory);
        Assert.Equal("/upper", marks.Get('A')!.Value.Directory);
    }

    [Fact]
    public void ClearingOneLeavesTheRest()
    {
        var marks = new MarkFile();
        marks.Set('a', "/a");
        marks.Set('b', "/b");

        marks.Clear('a');

        Assert.Null(marks.Get('a'));
        Assert.NotNull(marks.Get('b'));
    }

    [Fact]
    public void ClearingWithoutANameForgetsEverything()
    {
        var marks = new MarkFile();
        marks.Set('a', "/a");
        marks.Set('b', "/b");

        marks.Clear();

        Assert.Empty(marks.Names);
    }

    [Fact]
    public void LoadReplacesTheWholeSet()
    {
        var marks = new MarkFile();
        marks.Set('x', "/stale");

        marks.Load(new Dictionary<char, Mark>
        {
            ['a'] = new("/restored", "entry"),
        });

        Assert.Null(marks.Get('x'));
        Assert.Equal("/restored", marks.Get('a')!.Value.Directory);
    }

    [Fact]
    public void AMarkNeedsADirectory()
    {
        // A mark pointing nowhere would fail at jump time, a long way from the mistake.
        Assert.Throws<ArgumentException>(() => new MarkFile().Set('a', string.Empty));
    }
}
