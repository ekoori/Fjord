namespace Fjord.Core.FileSystem;

/// <summary>The outcome of matching a pattern against a name.</summary>
/// <param name="Positions">
/// Indices in the candidate that the pattern matched, ascending. Kept so the view can
/// highlight them — a fuzzy match the user cannot see the reasoning for reads as random.
/// </param>
public readonly record struct FuzzyMatch(bool IsMatch, int Score, IReadOnlyList<int> Positions)
{
    public static readonly FuzzyMatch None = new(false, 0, []);
}

/// <summary>
/// Subsequence matching for filenames, in the style of every fuzzy finder.
/// </summary>
/// <remarks>
/// A pattern matches when its characters appear in order somewhere in the candidate, not
/// necessarily adjacent: <c>fjc</c> finds <c>Fjord.Core.csproj</c>. The scoring is what
/// makes it usable — an unranked subsequence match returns almost everything.
/// <para>
/// Deliberately not the Smith-Waterman-style algorithm fzf uses. That needs a full
/// dynamic-programming matrix per candidate, and this runs over every entry of a directory
/// on every keystroke. A greedy forward scan with bonuses gets the ordering right for
/// filenames, which are short and full of word boundaries, at a fraction of the cost.
/// </para>
/// </remarks>
public static class FuzzyMatcher
{
    /// <summary>A match starting a word: after a separator, or a camelCase hump.</summary>
    private const int WordBoundaryBonus = 16;

    /// <summary>
    /// A match immediately after the previous one, multiplied by the length of the run.
    /// </summary>
    /// <remarks>
    /// Accumulating, and larger than <see cref="WordBoundaryBonus"/>, because contiguity is
    /// the stronger signal of intent. A flat bonus smaller than the boundary one meant
    /// <c>abc</c> ranked <c>a-b-c.txt</c> — three word starts — above the literal prefix
    /// <c>abc.txt</c>, which is plainly wrong and is what the run multiplier fixes.
    /// </remarks>
    private const int ConsecutiveBonus = 16;

    /// <summary>Caps the run multiplier, so one long run cannot swamp everything else.</summary>
    private const int MaxConsecutiveRun = 8;

    /// <summary>A match at the very start of the name.</summary>
    private const int StartBonus = 20;

    /// <summary>Matching the case the user typed, when they typed uppercase.</summary>
    private const int CaseBonus = 4;

    /// <summary>Per character skipped before a match, so earlier matches win.</summary>
    private const int LeadingGapPenalty = 2;

    /// <summary>The most leading characters a gap can be penalised for.</summary>
    private const int MaxGapPenalty = 18;

    /// <summary>Longer names are slightly worse for the same match, so exact-ish names rise.</summary>
    private const int LengthPenaltyDivisor = 8;

    /// <summary>
    /// Matches <paramref name="pattern"/> against <paramref name="candidate"/>.
    /// </summary>
    /// <remarks>
    /// Case-insensitive unless the pattern contains an uppercase letter, which is the
    /// "smart case" convention: typing lowercase means "I do not care", typing uppercase
    /// means "I do".
    /// </remarks>
    public static FuzzyMatch Match(string pattern, string candidate)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(candidate);

        if (pattern.Length == 0)
        {
            return new FuzzyMatch(true, 0, []);
        }

        if (candidate.Length == 0 || pattern.Length > candidate.Length)
        {
            return FuzzyMatch.None;
        }

        var caseSensitive = HasUpper(pattern);
        var positions = new List<int>(pattern.Length);

        var score = 0;
        var candidateIndex = 0;
        var previousMatch = -1;
        var run = 0;

        foreach (var wanted in pattern)
        {
            var found = IndexOfFrom(candidate, wanted, candidateIndex, caseSensitive);

            if (found < 0)
            {
                return FuzzyMatch.None;
            }

            run = previousMatch >= 0 && found == previousMatch + 1 ? run + 1 : 0;

            score += ScoreAt(candidate, found, previousMatch, wanted, caseSensitive, run);
            positions.Add(found);

            previousMatch = found;
            candidateIndex = found + 1;
        }

        // Among equally-good matches, prefer the shorter name: "test.cs" should beat
        // "test-with-a-very-long-suffix.cs" for the pattern "test".
        score -= candidate.Length / LengthPenaltyDivisor;

        return new FuzzyMatch(true, score, positions);
    }

    /// <summary>Whether the pattern matches at all, skipping the scoring.</summary>
    public static bool IsMatch(string pattern, string candidate) => Match(pattern, candidate).IsMatch;

    /// <summary>Ranks candidates best-first, dropping those that do not match.</summary>
    /// <remarks>
    /// Ties break on the name so the order is stable and deterministic; a list that
    /// reshuffles between identical keystrokes is unusable.
    /// </remarks>
    public static IEnumerable<(string Candidate, FuzzyMatch Match)> Rank(
        string pattern, IEnumerable<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        return candidates
            .Select(c => (Candidate: c, Match: Match(pattern, c)))
            .Where(r => r.Match.IsMatch)
            .OrderByDescending(r => r.Match.Score)
            .ThenBy(r => r.Candidate, NaturalComparer.Instance);
    }

    private static int ScoreAt(
        string candidate, int index, int previousMatch, char wanted, bool caseSensitive, int run)
    {
        var score = 0;

        if (index == 0)
        {
            score += StartBonus;
        }
        else if (IsWordStart(candidate, index))
        {
            score += WordBoundaryBonus;
        }

        if (run > 0)
        {
            // Each further character of an unbroken run is worth more than the last, so a
            // contiguous substring pulls decisively ahead of the same characters scattered.
            score += ConsecutiveBonus * Math.Min(run, MaxConsecutiveRun);
        }

        if (!caseSensitive && char.IsUpper(wanted) && candidate[index] == wanted)
        {
            score += CaseBonus;
        }

        // Only the gap since the last match, so a long name is not punished twice.
        var gap = index - (previousMatch + 1);
        if (gap > 0)
        {
            score -= Math.Min(gap * LeadingGapPenalty, MaxGapPenalty);
        }

        return score;
    }

    /// <summary>
    /// Whether this position begins a word.
    /// </summary>
    /// <remarks>
    /// Both conventions, because filenames use both: separators (<c>my-file.txt</c>) and
    /// camelCase humps (<c>MainWindow.cs</c>). Getting camelCase right is what lets
    /// <c>mw</c> find <c>MainWindow.cs</c>.
    /// </remarks>
    private static bool IsWordStart(string candidate, int index)
    {
        var previous = candidate[index - 1];

        if (previous is '.' or '-' or '_' or ' ' or '/' or '\\')
        {
            return true;
        }

        return char.IsUpper(candidate[index]) && char.IsLower(previous);
    }

    private static int IndexOfFrom(string candidate, char wanted, int from, bool caseSensitive)
    {
        for (var i = from; i < candidate.Length; i++)
        {
            if (Same(candidate[i], wanted, caseSensitive))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool Same(char a, char b, bool caseSensitive) =>
        caseSensitive ? a == b : char.ToUpperInvariant(a) == char.ToUpperInvariant(b);

    private static bool HasUpper(string text)
    {
        foreach (var c in text)
        {
            if (char.IsUpper(c))
            {
                return true;
            }
        }

        return false;
    }
}
