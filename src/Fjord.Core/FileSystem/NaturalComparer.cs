namespace Fjord.Core.FileSystem;

/// <summary>
/// Orders filenames the way people expect: <c>file2</c> before <c>file10</c>.
/// </summary>
/// <remarks>
/// Runs of ASCII digits are compared as numbers of arbitrary length; everything else is
/// compared case-insensitively by ordinal value. When two names tie under that rule
/// (<c>README</c> vs <c>readme</c>, or <c>1</c> vs <c>01</c>) the comparison falls back
/// to a plain ordinal compare.
/// <para>
/// That fallback is not cosmetic. <see cref="Array.Sort(Array)"/> throws
/// <c>InvalidOperationException</c> when a comparer is inconsistent, and a comparer that
/// returns 0 for strings that are not equal is exactly that. The fallback makes the
/// order total and consistent with equality, so sorting is stable and deterministic
/// across runs and platforms.
/// </para>
/// <para>
/// Only ASCII '0'-'9' count as digits. <see cref="char.IsDigit(char)"/> also matches
/// Arabic-Indic and other Unicode digits, whose numeric value is not what a filename
/// like <c>file10.txt</c> means to a user.
/// </para>
/// </remarks>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var natural = CompareNatural(x.AsSpan(), y.AsSpan());
        return natural != 0 ? natural : string.CompareOrdinal(x, y);
    }

    /// <summary>
    /// The case-insensitive natural comparison, without the ordinal tiebreak.
    /// </summary>
    /// <remarks>
    /// Returns 0 for names that differ only in case or in leading zeros. Callers that
    /// need a total order must break that tie themselves; <see cref="Compare"/> does.
    /// </remarks>
    public static int CompareNatural(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        int i = 0, j = 0;

        while (i < a.Length && j < b.Length)
        {
            if (IsAsciiDigit(a[i]) && IsAsciiDigit(b[j]))
            {
                var byNumber = CompareDigitRuns(a, ref i, b, ref j);
                if (byNumber != 0)
                {
                    return byNumber;
                }

                continue;
            }

            var ca = char.ToUpperInvariant(a[i]);
            var cb = char.ToUpperInvariant(b[j]);
            if (ca != cb)
            {
                return ca < cb ? -1 : 1;
            }

            i++;
            j++;
        }

        // One side ran out: the shorter name sorts first.
        var remainingA = a.Length - i;
        var remainingB = b.Length - j;
        return remainingA.CompareTo(remainingB);
    }

    /// <summary>
    /// Compares the digit runs starting at <paramref name="i"/> and <paramref name="j"/>,
    /// advancing both past the run.
    /// </summary>
    /// <remarks>
    /// Leading zeros are skipped before comparing, so <c>007</c> and <c>7</c> are equal
    /// here and the ordinal tiebreak decides. Comparison is by significant-digit count
    /// first, then digit by digit, so it works for runs far longer than any integer type.
    /// </remarks>
    private static int CompareDigitRuns(ReadOnlySpan<char> a, ref int i, ReadOnlySpan<char> b, ref int j)
    {
        var startA = SkipZeros(a, ref i);
        var startB = SkipZeros(b, ref j);

        var endA = RunEnd(a, i);
        var endB = RunEnd(b, j);

        var lengthA = endA - startA;
        var lengthB = endB - startB;

        i = endA;
        j = endB;

        if (lengthA != lengthB)
        {
            // More significant digits means a larger number.
            return lengthA < lengthB ? -1 : 1;
        }

        for (var k = 0; k < lengthA; k++)
        {
            var da = a[startA + k];
            var db = b[startB + k];
            if (da != db)
            {
                return da < db ? -1 : 1;
            }
        }

        return 0;
    }

    /// <summary>
    /// Advances past leading zeros, leaving at least one digit when the run is all zeros.
    /// </summary>
    private static int SkipZeros(ReadOnlySpan<char> s, ref int index)
    {
        while (index < s.Length - 1 && s[index] == '0' && IsAsciiDigit(s[index + 1]))
        {
            index++;
        }

        return index;
    }

    private static int RunEnd(ReadOnlySpan<char> s, int index)
    {
        while (index < s.Length && IsAsciiDigit(s[index]))
        {
            index++;
        }

        return index;
    }

    private static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';
}
