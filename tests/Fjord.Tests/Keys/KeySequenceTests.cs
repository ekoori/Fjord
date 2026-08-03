using Fjord.Core.Keys;

namespace Fjord.Tests.Keys;

/// <summary>
/// Ported from QuteSharp, which ported them from qutebrowser's
/// <c>tests/unit/keyinput/test_keyutils.py</c>. The data tables are verbatim so any
/// divergence in key-parsing behaviour shows up as a failure.
/// </summary>
public class KeySequenceTests
{
    [Theory]
    [InlineData("<Control+x>", "<Ctrl+x>")]
    [InlineData("<Windows+x>", "<Meta+x>")]
    [InlineData("<Super+x>", "<Meta+x>")]
    [InlineData("<Mod4+x>", "<Meta+x>")]
    [InlineData("<Command+x>", "<Meta+x>")]
    [InlineData("<Cmd+x>", "<Meta+x>")]
    [InlineData("<Mod1+x>", "<Alt+x>")]
    [InlineData("<Control-->", "<Ctrl+->")]
    [InlineData("<Windows++>", "<Meta++>")]
    [InlineData("<ctrl-x>", "<Ctrl+x>")]
    [InlineData("<control+x>", "<Ctrl+x>")]
    [InlineData("<a>b", "ab")]
    public void Parse_NormalisesKeystring(string original, string normalised) =>
        Assert.Equal(normalised, KeySequence.Parse(original).ToString());

    [Theory]
    [InlineData("", 0)]
    [InlineData("a", 1)]
    [InlineData("A", 1)]
    [InlineData("<Ctrl-a>", 1)]
    [InlineData("abcde", 5)]
    public void Parse_ProducesExpectedLength(string sequence, int length) =>
        Assert.Equal(length, KeySequence.Parse(sequence).Count);

    [Theory]
    [InlineData("abc", "abcd", SequenceMatch.PartialMatch)]
    [InlineData("abcd", "abcd", SequenceMatch.ExactMatch)]
    [InlineData("ax", "abcd", SequenceMatch.NoMatch)]
    [InlineData("abcdef", "abcd", SequenceMatch.NoMatch)]
    // Cases that straddle qutebrowser's four-key QKeySequence chunk boundary.
    [InlineData("abc", "abcdef", SequenceMatch.PartialMatch)]
    [InlineData("abcde", "abcdef", SequenceMatch.PartialMatch)]
    [InlineData("abcd", "abcdef", SequenceMatch.PartialMatch)]
    [InlineData("abcdx", "abcdef", SequenceMatch.NoMatch)]
    [InlineData("ax", "abcdef", SequenceMatch.NoMatch)]
    [InlineData("abcdefg", "abcdef", SequenceMatch.NoMatch)]
    [InlineData("abcdef", "abcdef", SequenceMatch.ExactMatch)]
    [InlineData("ab", "a", SequenceMatch.NoMatch)]
    public void Matches_AgreesWithUpstream(string entered, string configured, SequenceMatch expected) =>
        Assert.Equal(expected, KeySequence.Parse(entered).Matches(KeySequence.Parse(configured)));

    [Theory]
    [InlineData("a", "a", true)]
    [InlineData("a", "A", false)]
    [InlineData("a", "<a>", true)]
    [InlineData("a", "<Shift-a>", false)]
    [InlineData("A", "<Shift-a>", true)]
    // Special-key parsing lowercases the whole part, so case inside <> is not significant.
    [InlineData("<Ctrl-A>", "<Ctrl-a>", true)]
    [InlineData("abcd", "abcde", false)]
    public void Equality_AgreesWithUpstream(string first, string second, bool equal) =>
        Assert.Equal(equal, KeySequence.Parse(first).Equals(KeySequence.Parse(second)));

    [Theory]
    [InlineData(Key.A, KeyModifiers.None, "a")]
    [InlineData(Key.A, KeyModifiers.Shift, "A")]
    [InlineData(Key.A, KeyModifiers.Control, "<Ctrl+a>")]
    [InlineData(Key.A, KeyModifiers.Control | KeyModifiers.Shift, "<Ctrl+Shift+a>")]
    [InlineData(Key.Space, KeyModifiers.None, "<Space>")]
    [InlineData(Key.Escape, KeyModifiers.None, "<Escape>")]
    // A modifier key pressed on its own must not render as <Shift+Shift>.
    [InlineData(Key.Shift, KeyModifiers.Shift, "<Shift>")]
    [InlineData(Key.Shift, KeyModifiers.Shift | KeyModifiers.Control, "<Ctrl+Shift>")]
    public void KeyInfo_ToString_AgreesWithUpstream(Key key, KeyModifiers modifiers, string expected) =>
        Assert.Equal(expected, new KeyInfo(key, modifiers).ToString());

    [Theory]
    [InlineData("\x1f")]
    [InlineData("<blubb>")]
    [InlineData("<>")]
    public void Parse_RejectsInvalidKeystrings(string keystr) =>
        Assert.Throws<KeyParseException>(() => KeySequence.Parse(keystr));

    [Fact]
    public void StripModifiers_RemovesShiftOnly()
    {
        var stripped = KeySequence.Parse("<Ctrl-A>").StripModifiers();
        Assert.Equal(KeySequence.Parse("<Ctrl-a>"), stripped);
    }

    [Fact]
    public void WithMappings_RewritesMappedKeys()
    {
        var mappings = new Dictionary<KeySequence, KeySequence>
        {
            [KeySequence.Parse("<Ctrl+x>")] = KeySequence.Parse("<Ctrl+y>"),
        };

        var result = KeySequence.Parse("foo<Ctrl+x>bar").WithMappings(mappings);
        Assert.Equal(KeySequence.Parse("foo<Ctrl+y>bar"), result);
    }

    [Fact]
    // Round-tripping is what makes config files and session files stable.
    public void Parse_RoundTripsThroughToString()
    {
        string[] samples = ["a", "A", "<Ctrl+a>", "<Ctrl+Shift+a>", "gg", "<Escape>", "<Space>"];
        foreach (var sample in samples)
        {
            var once = KeySequence.Parse(sample);
            Assert.Equal(once, KeySequence.Parse(once.ToString()));
        }
    }
}
