using Fjord.Core.FileSystem;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Fjord.Tests.FileSystem;

public class FuzzyMatcherTests
{
    private static int Score(string pattern, string candidate) =>
        FuzzyMatcher.Match(pattern, candidate).Score;

    [Theory]
    [InlineData("fjc", "Fjord.Core.csproj")]
    [InlineData("mw", "MainWindow.cs")]
    [InlineData("readme", "README.md")]
    [InlineData("", "anything")]
    [InlineData("abc", "abc")]
    public void SubsequencesMatch(string pattern, string candidate) =>
        Assert.True(FuzzyMatcher.IsMatch(pattern, candidate));

    [Theory]
    [InlineData("xyz", "abc")]
    [InlineData("cba", "abc")]
    [InlineData("abcd", "abc")]
    [InlineData("a", "")]
    public void NonSubsequencesDoNotMatch(string pattern, string candidate) =>
        Assert.False(FuzzyMatcher.IsMatch(pattern, candidate));

    [Fact]
    public void MatchesAreCaseInsensitiveUntilYouTypeUppercase()
    {
        // Smart case: typing lowercase means "I do not care", uppercase means "I do".
        Assert.True(FuzzyMatcher.IsMatch("readme", "README.md"));
        Assert.False(FuzzyMatcher.IsMatch("README", "readme.md"));
        Assert.True(FuzzyMatcher.IsMatch("README", "README.md"));
    }

    [Fact]
    public void PositionsPointAtWhatMatched()
    {
        // The view highlights these; a fuzzy match whose reasoning is invisible reads as
        // random.
        var match = FuzzyMatcher.Match("mw", "MainWindow.cs");

        Assert.Equal([0, 4], match.Positions);
    }

    [Fact]
    public void AWordBoundaryBeatsAMatchMidWord()
    {
        // "mw" should mean MainWindow, not the m and w buried in "framework".
        Assert.True(Score("mw", "MainWindow.cs") > Score("mw", "framework.cs"));
    }

    [Fact]
    public void AMatchAtTheStartBeatsOneLater()
    {
        Assert.True(Score("test", "test.cs") > Score("test", "my-test.cs"));
    }

    [Fact]
    public void ConsecutiveCharactersBeatScattered()
    {
        Assert.True(Score("abc", "abc.txt") > Score("abc", "a-b-c.txt"));
    }

    [Fact]
    public void TheShorterNameWinsWhenTheMatchIsOtherwiseEqual()
    {
        Assert.True(Score("test", "test.cs") > Score("test", "test-with-long-suffix.cs"));
    }

    [Fact]
    public void CamelCaseHumpsCountAsWordStarts()
    {
        // Getting this right is what lets "dsn" find DirectorySnapshot.
        Assert.True(Score("ds", "DirectorySnapshot.cs") > Score("ds", "adjusted.cs"));
    }

    [Fact]
    public void RankPutsTheBestMatchFirst()
    {
        string[] candidates =
        [
            "framework.cs", "MainWindow.cs", "unrelated.txt", "MainWindowTests.cs",
        ];

        var ranked = FuzzyMatcher.Rank("mw", candidates).Select(r => r.Candidate).ToList();

        Assert.Equal("MainWindow.cs", ranked[0]);
        Assert.DoesNotContain("unrelated.txt", ranked);
    }

    [Fact]
    public void RankIsStableForEqualScores()
    {
        // A list that reshuffles between identical keystrokes is unusable.
        string[] candidates = ["b.cs", "a.cs", "c.cs"];

        var first = FuzzyMatcher.Rank("cs", candidates).Select(r => r.Candidate).ToList();
        var second = FuzzyMatcher.Rank("cs", candidates).Select(r => r.Candidate).ToList();

        Assert.Equal(first, second);
    }

    [Fact]
    public void AnEmptyPatternMatchesEverythingWithoutRanking()
    {
        var match = FuzzyMatcher.Match("", "anything.txt");

        Assert.True(match.IsMatch);
        Assert.Equal(0, match.Score);
        Assert.Empty(match.Positions);
    }

    // ---- Properties

    private static Arbitrary<string> Names() =>
        Gen.Elements("a", "b", "z", "A", "Z", ".", "-", "_", " ", "9", "é")
            .ListOf()
            .Select(string.Concat)
            .ToArbitrary();

    [Property]
    public Property AnyPrefixOfANameMatchesIt() =>
        Prop.ForAll(Names(), name =>
        {
            // A prefix is trivially a subsequence, so it must always match.
            for (var length = 0; length <= name.Length; length++)
            {
                if (!FuzzyMatcher.IsMatch(name[..length], name))
                {
                    return false;
                }
            }

            return true;
        });

    [Property]
    public Property AnyNameMatchesItself() =>
        Prop.ForAll(Names(), name => FuzzyMatcher.IsMatch(name, name));

    [Property]
    public Property PositionsAreAscendingAndInRange() =>
        Prop.ForAll(Names(), Names(), (pattern, candidate) =>
        {
            var match = FuzzyMatcher.Match(pattern, candidate);

            if (!match.IsMatch)
            {
                return true;
            }

            // Highlighting indexes the candidate with these, so an out-of-range or
            // out-of-order position would throw or paint the wrong characters.
            for (var i = 0; i < match.Positions.Count; i++)
            {
                if (match.Positions[i] < 0 || match.Positions[i] >= candidate.Length)
                {
                    return false;
                }

                if (i > 0 && match.Positions[i] <= match.Positions[i - 1])
                {
                    return false;
                }
            }

            return match.Positions.Count == pattern.Length;
        });

    [Property]
    public Property MatchingNeverThrows() =>
        Prop.ForAll(Names(), Names(), (pattern, candidate) =>
        {
            _ = FuzzyMatcher.Match(pattern, candidate);
            return true;
        });
}

public class FuzzyFilterTests
{
    private static DirectorySnapshot Snapshot(params string[] names)
    {
        var snapshot = new DirectorySnapshot("/tmp");
        snapshot.Append(names.Select(n => new FileEntry(n, 10, 100, EntryFlags.None)).ToArray());
        return snapshot;
    }

    private static string[] Names(DirectorySnapshot snapshot)
    {
        var view = snapshot.View;
        return Enumerable.Range(0, view.Count).Select(i => view[i].Name).ToArray();
    }

    [Fact]
    public void AFuzzyFilterNarrowsTheListing()
    {
        var snapshot = Snapshot("Fjord.Core.csproj", "README.md", "Fjord.App.csproj");

        snapshot.SetOrder(SortSpec.Default, new FilterSpec(NameContains: "fjc"));

        Assert.Equal(2, Names(snapshot).Length);
        Assert.DoesNotContain("README.md", Names(snapshot));
    }

    [Fact]
    public void FilteredResultsAreOrderedByRelevance()
    {
        // While searching, the best match belongs at the top — alphabetical order among a
        // handful of survivors is not what the user is looking at the list for.
        var snapshot = Snapshot("framework.cs", "MainWindow.cs", "aaa-mw.cs");

        snapshot.SetOrder(SortSpec.Default, new FilterSpec(NameContains: "mw"));

        Assert.Equal("MainWindow.cs", Names(snapshot)[0]);
    }

    [Fact]
    public void WithoutAFilterTheNormalOrderIsUnchanged()
    {
        var snapshot = Snapshot("c.txt", "a.txt", "b.txt");

        Assert.Equal(["a.txt", "b.txt", "c.txt"], Names(snapshot));
    }

    [Fact]
    public void ASubstringFilterIsStillAvailable()
    {
        // Fuzzy is the default for '/', but an exact substring is what a scripted filter
        // usually wants.
        var snapshot = Snapshot("Fjord.Core.csproj", "README.md");

        snapshot.SetOrder(SortSpec.Default, new FilterSpec(NameContains: "fjc", Fuzzy: false));

        Assert.Empty(Names(snapshot));
    }

    [Fact]
    public void ClearingTheFilterRestoresEverything()
    {
        var snapshot = Snapshot("a.txt", "b.txt", "c.txt");
        snapshot.SetOrder(SortSpec.Default, new FilterSpec(NameContains: "a"));
        Assert.Single(Names(snapshot));

        snapshot.SetOrder(SortSpec.Default, FilterSpec.Default);

        Assert.Equal(3, Names(snapshot).Length);
    }

    [Fact]
    public void FilteringWhileLoadingKeepsLaterBatchesFiltered()
    {
        // The filter applies in Append too, not only on a full rebuild.
        var snapshot = new DirectorySnapshot(
            "/tmp", SortSpec.Default, new FilterSpec(NameContains: "match"));

        snapshot.Append([new FileEntry("matching.txt", 0, 0, EntryFlags.None)]);
        snapshot.Append([new FileEntry("other.txt", 0, 0, EntryFlags.None)]);

        Assert.Equal(["matching.txt"], Names(snapshot));
        Assert.Equal(2, snapshot.TotalCount);
    }
}
