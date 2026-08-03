using Fjord.Core.Selection;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Fjord.Tests.Selection;

public class SelectionSetTests
{
    [Fact]
    public void StartsEmpty()
    {
        var set = new SelectionSet();

        Assert.True(set.IsEmpty);
        Assert.Equal(0, set.Count);
        Assert.False(set[0]);
    }

    [Fact]
    public void SettingAndClearingTracksCount()
    {
        var set = new SelectionSet { [5] = true, [7] = true };
        Assert.Equal(2, set.Count);

        set[5] = false;
        Assert.Equal(1, set.Count);
        Assert.False(set[5]);
        Assert.True(set[7]);
    }

    [Fact]
    public void SettingTwiceDoesNotDoubleCount()
    {
        var set = new SelectionSet { [3] = true, [3] = true };

        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void ClearingSomethingUnsetIsANoOp()
    {
        var set = new SelectionSet { [10_000] = false };

        Assert.Equal(0, set.Count);
    }

    [Fact]
    public void NegativeIndicesAreIgnored()
    {
        // Cursor arithmetic can transiently produce one; it must not throw.
        var set = new SelectionSet { [-1] = true };

        Assert.Equal(0, set.Count);
        Assert.False(set[-1]);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 63)]
    [InlineData(0, 64)]
    [InlineData(63, 64)]
    [InlineData(1, 128)]
    [InlineData(64, 191)]
    [InlineData(10, 10_000)]
    public void SetRangeSelectsExactlyTheRange(int from, int to)
    {
        // Word boundaries at 63/64 are where a mask built with `1UL << 64` silently
        // becomes 1 instead of 0.
        var set = new SelectionSet();
        set.SetRange(from, to, true);

        Assert.Equal(to - from + 1, set.Count);
        Assert.True(set[from]);
        Assert.True(set[to]);
        Assert.False(set[to + 1]);

        if (from > 0)
        {
            Assert.False(set[from - 1]);
        }
    }

    [Fact]
    public void SetRangeAcceptsReversedBounds()
    {
        // Visual mode runs the cursor above the anchor as often as below.
        var set = new SelectionSet();
        set.SetRange(20, 10, true);

        Assert.Equal(11, set.Count);
        Assert.True(set[10]);
        Assert.True(set[20]);
    }

    [Fact]
    public void SetRangeIsIdempotent()
    {
        var set = new SelectionSet();
        set.SetRange(5, 100, true);
        set.SetRange(5, 100, true);

        Assert.Equal(96, set.Count);
    }

    [Fact]
    public void SetRangeClearsWithoutDisturbingNeighbours()
    {
        var set = new SelectionSet();
        set.SetRange(0, 200, true);
        set.SetRange(50, 100, false);

        Assert.Equal(201 - 51, set.Count);
        Assert.True(set[49]);
        Assert.False(set[50]);
        Assert.False(set[100]);
        Assert.True(set[101]);
    }

    [Fact]
    public void ToggleRangeFlipsEachRow()
    {
        var set = new SelectionSet();
        set.SetRange(0, 9, true);
        set.SetRange(5, 14, false);

        set.ToggleRange(0, 14);

        Assert.Equal(10, set.Count);
        Assert.False(set[0]);
        Assert.True(set[5]);
        Assert.True(set[14]);
    }

    [Fact]
    public void IndicesEnumeratesAscending()
    {
        var set = new SelectionSet { [200] = true, [3] = true, [64] = true };

        Assert.Equal([3, 64, 200], set.Indices());
    }

    [Fact]
    public void ClearResetsEverything()
    {
        var set = new SelectionSet();
        set.SetRange(0, 5000, true);

        set.Clear();

        Assert.Equal(0, set.Count);
        Assert.Empty(set.Indices());
    }

    [Fact]
    public void HandlesAHundredThousandRows()
    {
        var set = new SelectionSet();
        set.SetRange(0, 99_999, true);

        Assert.Equal(100_000, set.Count);
        Assert.Equal(100_000, set.Indices().Count());
    }

    // ---- Properties: Count is the invariant everything else leans on, and it is
    // maintained incrementally rather than recomputed, so it is the thing most likely
    // to drift out of agreement with the bits.

    private sealed record Operation(int From, int To, bool Value, bool IsRange);

    private static Arbitrary<Operation[]> Operations() =>
        (from low in Gen.Choose(0, 300)
         from high in Gen.Choose(0, 300)
         from flags in Gen.Choose(0, 3)
         select new Operation(low, high, (flags & 1) != 0, (flags & 2) != 0))
        .ArrayOf()
        .ToArbitrary();

    [Property]
    public Property CountAlwaysEqualsThePopulation() =>
        Prop.ForAll(Operations(), operations =>
        {
            var set = new SelectionSet();

            foreach (var op in operations)
            {
                if (op.IsRange)
                {
                    set.SetRange(op.From, op.To, op.Value);
                }
                else
                {
                    set[op.From] = op.Value;
                }
            }

            return set.Count == set.Indices().Count();
        });

    [Property]
    public Property IndicesAreExactlyTheSetBits() =>
        Prop.ForAll(Operations(), operations =>
        {
            var set = new SelectionSet();
            var reference = new HashSet<int>();

            foreach (var op in operations)
            {
                var (low, high) = op.From <= op.To ? (op.From, op.To) : (op.To, op.From);

                if (op.IsRange)
                {
                    set.SetRange(op.From, op.To, op.Value);
                    for (var i = low; i <= high; i++)
                    {
                        if (op.Value)
                        {
                            reference.Add(i);
                        }
                        else
                        {
                            reference.Remove(i);
                        }
                    }
                }
                else
                {
                    set[op.From] = op.Value;
                    if (op.Value)
                    {
                        reference.Add(op.From);
                    }
                    else
                    {
                        reference.Remove(op.From);
                    }
                }
            }

            return set.Indices().ToHashSet().SetEquals(reference);
        });
}
