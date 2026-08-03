using Fjord.Core.FileSystem;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Fjord.Tests.FileSystem;

public class NaturalComparerTests
{
    private static int Compare(string a, string b) => Math.Sign(NaturalComparer.Instance.Compare(a, b));

    [Theory]
    [InlineData("file2", "file10")]
    [InlineData("file9", "file10")]
    [InlineData("a1", "a2")]
    [InlineData("img1.png", "img12.png")]
    [InlineData("2", "10")]
    [InlineData("v1.9.0", "v1.10.0")]
    [InlineData("chapter 2", "chapter 11")]
    public void DigitRunsCompareAsNumbers(string smaller, string larger)
    {
        Assert.Equal(-1, Compare(smaller, larger));
        Assert.Equal(1, Compare(larger, smaller));
    }

    [Theory]
    [InlineData("apple", "banana")]
    [InlineData("a", "ab")]
    [InlineData("", "a")]
    public void NonDigitsCompareLexicographically(string smaller, string larger) =>
        Assert.Equal(-1, Compare(smaller, larger));

    [Fact]
    public void CaseIsSecondary()
    {
        // "README" and "readme" must sit next to each other rather than be split by
        // everything in between, but they still need a stable relative order.
        Assert.Equal(0, NaturalComparer.CompareNatural("README", "readme"));
        Assert.NotEqual(0, Compare("README", "readme"));
    }

    [Fact]
    public void CaseInsensitiveOrderingBeatsOrdinal()
    {
        // Ordinal would put every uppercase name before every lowercase one, so "Zebra"
        // would sort before "apple". Natural order interleaves them.
        Assert.Equal(-1, Compare("apple", "Zebra"));
    }

    [Theory]
    // Larger numbers of digits win only after leading zeros are discounted.
    [InlineData("007", "8")]
    [InlineData("08", "9")]
    [InlineData("0000001", "2")]
    public void LeadingZerosDoNotInflateANumber(string smaller, string larger) =>
        Assert.Equal(-1, Compare(smaller, larger));

    [Fact]
    public void EqualNumbersDifferingOnlyInZeroPaddingStillOrderDeterministically()
    {
        Assert.Equal(0, NaturalComparer.CompareNatural("01", "1"));

        // The ordinal tiebreak has to pick one, consistently and in both directions.
        Assert.Equal(-Compare("1", "01"), Compare("01", "1"));
        Assert.NotEqual(0, Compare("01", "1"));
    }

    [Fact]
    public void AllZeroRunsAreEqualToZero()
    {
        Assert.Equal(0, NaturalComparer.CompareNatural("000", "0"));
        Assert.Equal(-1, Math.Sign(NaturalComparer.CompareNatural("000", "1")));
    }

    [Fact]
    public void ArbitrarilyLongNumbersDoNotOverflow()
    {
        // Well past long.MaxValue: the comparison is digit-by-digit, not parsed.
        var big = new string('9', 40);
        var bigger = "1" + new string('0', 40);

        Assert.Equal(-1, Compare(big, bigger));
    }

    [Fact]
    public void OnlyAsciiDigitsAreTreatedAsNumbers()
    {
        // char.IsDigit matches Arabic-Indic digits too, but "٢" in a filename is not
        // a number the user is counting with.
        const string arabicTwo = "file٢";
        Assert.Equal(0, Compare(arabicTwo, arabicTwo));
        Assert.NotEqual(0, Compare("file٢", "file2"));
    }

    [Fact]
    public void NullsSortFirstAndConsistently()
    {
        Assert.Equal(0, Compare(null!, null!));
        Assert.Equal(-1, Compare(null!, "a"));
        Assert.Equal(1, Compare("a", null!));
    }

    [Fact]
    public void SortsARealisticListing()
    {
        string[] names =
        [
            "file10.txt", "file2.txt", "File1.txt", "archive.tar.gz", "Zebra", "apple", "10", "9",
        ];

        Array.Sort(names, NaturalComparer.Instance);

        Assert.Equal(
            ["9", "10", "apple", "archive.tar.gz", "File1.txt", "file2.txt", "file10.txt", "Zebra"],
            names);
    }

    // ---- Properties. A comparator that is not a total order makes Array.Sort throw
    // "IComparer.Compare() returns inconsistent results" on some inputs and not others,
    // which is exactly the failure hand-written examples miss.

    /// <summary>Filenames biased towards the shapes that break natural comparison.</summary>
    private static Arbitrary<string> Names() =>
        Gen.OneOf(
                Gen.Elements("", "0", "1", "01", "9", "10", "a", "A", "z", ".", "-", " ", "é", "٢"),
                ArbMap.Default.ArbFor<string>().Generator.Where(s => s is not null))
            .ListOf()
            .Select(parts => string.Concat(parts))
            .ToArbitrary();

    [Property]
    public Property IsAntisymmetric() =>
        Prop.ForAll(Names(), Names(), (a, b) => Compare(a, b) == -Compare(b, a));

    [Property]
    public Property IsReflexive() => Prop.ForAll(Names(), a => Compare(a, a) == 0);

    [Property]
    public Property IsConsistentWithEquality() =>
        Prop.ForAll(Names(), Names(), (a, b) =>
            (Compare(a, b) == 0) == string.Equals(a, b, StringComparison.Ordinal));

    [Property]
    public Property IsTransitive() =>
        Prop.ForAll(Names(), Names(), Names(), (a, b, c) =>
        {
            // Order the three, then assert the ordering really is a chain.
            var sorted = new[] { a, b, c };
            Array.Sort(sorted, NaturalComparer.Instance);

            return Compare(sorted[0], sorted[1]) <= 0
                && Compare(sorted[1], sorted[2]) <= 0
                && Compare(sorted[0], sorted[2]) <= 0;
        });

    [Property]
    public Property SortingNeverThrows() =>
        Prop.ForAll(Names().Generator.ListOf().ToArbitrary(), names =>
        {
            // Array.Sort validates comparer consistency internally on larger inputs and
            // throws InvalidOperationException if it is violated.
            var array = names.ToArray();
            Array.Sort(array, NaturalComparer.Instance);

            for (var i = 1; i < array.Length; i++)
            {
                if (Compare(array[i - 1], array[i]) > 0)
                {
                    return false;
                }
            }

            return true;
        });
}
