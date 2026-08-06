using Fjord.Core.Operations;
using Fjord.Core.Registers;

namespace Fjord.Tests.Registers;

public class RegisterFileTests
{
    [Fact]
    public void AnUntouchedRegisterIsEmpty()
    {
        var registers = new RegisterFile();

        Assert.True(registers['a'].IsEmpty);
        Assert.True(registers.Unnamed.IsEmpty);
        Assert.Empty(registers.Names);
    }

    [Fact]
    public void NamedRegistersAreSeparate()
    {
        var registers = new RegisterFile
        {
            ['a'] = new ClipboardContents(["one.txt"], Cut: false),
            ['b'] = new ClipboardContents(["two.txt", "three.txt"], Cut: true),
        };

        Assert.Equal(["one.txt"], registers['a'].Paths);
        Assert.Equal(["two.txt", "three.txt"], registers['b'].Paths);
        Assert.False(registers['a'].Cut);
        Assert.True(registers['b'].Cut);
    }

    [Fact]
    public void WritingANamedRegisterAlsoWritesTheUnnamedOne()
    {
        // vim's behaviour, and the reason "ay then a bare p does what you meant rather
        // than pasting whatever was lying around in the unnamed register beforehand.
        var registers = new RegisterFile
        {
            ['q'] = new ClipboardContents(["kept.txt"], Cut: false),
        };

        Assert.Equal(["kept.txt"], registers.Unnamed.Paths);
    }

    [Fact]
    public void WritingTheUnnamedRegisterLeavesNamedOnesAlone()
    {
        var registers = new RegisterFile
        {
            ['a'] = new ClipboardContents(["mine.txt"], Cut: false),
        };

        registers.Unnamed = new ClipboardContents(["other.txt"], Cut: false);

        Assert.Equal(["mine.txt"], registers['a'].Paths);
        Assert.Equal(["other.txt"], registers.Unnamed.Paths);
    }

    [Fact]
    public void RegisterNamesFoldCase()
    {
        // Without folding, "A would be a register the user could never find again from
        // the listing, which shows lower-case names.
        var registers = new RegisterFile
        {
            ['A'] = new ClipboardContents(["one.txt"], Cut: false),
        };

        Assert.Equal(["one.txt"], registers['a'].Paths);
    }

    [Fact]
    public void TheUnnamedRegisterIsListedFirst()
    {
        var registers = new RegisterFile
        {
            ['z'] = new ClipboardContents(["z.txt"], Cut: false),
            ['a'] = new ClipboardContents(["a.txt"], Cut: false),
        };

        Assert.Equal(['"', 'a', 'z'], registers.Names);
    }

    [Fact]
    public void EmptyRegistersAreNotListed()
    {
        var registers = new RegisterFile
        {
            ['a'] = new ClipboardContents(["a.txt"], Cut: false),
        };

        registers.Clear('a');

        Assert.DoesNotContain('a', registers.Names);
    }

    [Fact]
    public void ClearingWithoutANameEmptiesEverything()
    {
        var registers = new RegisterFile
        {
            ['a'] = new ClipboardContents(["a.txt"], Cut: false),
            ['b'] = new ClipboardContents(["b.txt"], Cut: false),
        };

        registers.Clear();

        Assert.Empty(registers.Names);
        Assert.True(registers.Unnamed.IsEmpty);
    }
}
