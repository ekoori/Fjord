using System.Numerics;

namespace Fjord.Core.Selection;

/// <summary>
/// The set of selected rows, as a bitset.
/// </summary>
/// <remarks>
/// Selection lives here rather than as a flag on <c>FileEntry</c> on purpose. The entry
/// array is published to the renderer and never mutated afterwards; putting selection on
/// the entry would mean rewriting and republishing that array on every cursor move.
/// <para>
/// A bitset also makes the operation that matters cheap: <c>V</c> over 100,000 rows is a
/// handful of word writes, and 100,000 rows of selection state is 12.5 KB.
/// </para>
/// </remarks>
public sealed class SelectionSet
{
    private const int BitsPerWord = 64;

    private ulong[] _bits = [];
    private int _count;

    /// <summary>Number of selected rows. Maintained incrementally; never a full scan.</summary>
    public int Count => _count;

    public bool IsEmpty => _count == 0;

    public bool this[int index]
    {
        get
        {
            if (index < 0)
            {
                return false;
            }

            var word = index / BitsPerWord;
            return word < _bits.Length && (_bits[word] & Bit(index)) != 0;
        }

        set
        {
            if (index < 0)
            {
                return;
            }

            if (!value && index / BitsPerWord >= _bits.Length)
            {
                // Clearing something already clear; no need to allocate to store a zero.
                return;
            }

            EnsureCapacity(index + 1);

            var word = index / BitsPerWord;
            var mask = Bit(index);
            var wasSet = (_bits[word] & mask) != 0;

            if (wasSet == value)
            {
                return;
            }

            _bits[word] = value ? _bits[word] | mask : _bits[word] & ~mask;
            _count += value ? 1 : -1;
        }
    }

    /// <summary>Selects or deselects an inclusive range of rows.</summary>
    public void SetRange(int from, int to, bool value)
    {
        if (from > to)
        {
            (from, to) = (to, from);
        }

        from = Math.Max(from, 0);

        if (to < 0)
        {
            return;
        }

        if (!value && to / BitsPerWord >= _bits.Length)
        {
            to = _bits.Length * BitsPerWord - 1;
            if (to < from)
            {
                return;
            }
        }

        EnsureCapacity(to + 1);

        var firstWord = from / BitsPerWord;
        var lastWord = to / BitsPerWord;

        for (var word = firstWord; word <= lastWord; word++)
        {
            var lowBit = word == firstWord ? from % BitsPerWord : 0;
            var highBit = word == lastWord ? to % BitsPerWord : BitsPerWord - 1;
            var mask = MaskBetween(lowBit, highBit);

            var before = BitOperations.PopCount(_bits[word]);
            _bits[word] = value ? _bits[word] | mask : _bits[word] & ~mask;
            _count += BitOperations.PopCount(_bits[word]) - before;
        }
    }

    public void Toggle(int index) => this[index] = !this[index];

    /// <summary>Toggles every row in an inclusive range.</summary>
    public void ToggleRange(int from, int to)
    {
        if (from > to)
        {
            (from, to) = (to, from);
        }

        from = Math.Max(from, 0);

        if (to < 0)
        {
            return;
        }

        EnsureCapacity(to + 1);

        var firstWord = from / BitsPerWord;
        var lastWord = to / BitsPerWord;

        for (var word = firstWord; word <= lastWord; word++)
        {
            var lowBit = word == firstWord ? from % BitsPerWord : 0;
            var highBit = word == lastWord ? to % BitsPerWord : BitsPerWord - 1;
            var mask = MaskBetween(lowBit, highBit);

            var before = BitOperations.PopCount(_bits[word]);
            _bits[word] ^= mask;
            _count += BitOperations.PopCount(_bits[word]) - before;
        }
    }

    public void Clear()
    {
        Array.Clear(_bits);
        _count = 0;
    }

    /// <summary>The selected rows, ascending.</summary>
    public IEnumerable<int> Indices()
    {
        for (var word = 0; word < _bits.Length; word++)
        {
            var remaining = _bits[word];
            while (remaining != 0)
            {
                var bit = BitOperations.TrailingZeroCount(remaining);
                yield return (word * BitsPerWord) + bit;
                remaining &= remaining - 1;
            }
        }
    }

    private void EnsureCapacity(int bitCount)
    {
        var words = (bitCount + BitsPerWord - 1) / BitsPerWord;
        if (words <= _bits.Length)
        {
            return;
        }

        Array.Resize(ref _bits, Math.Max(words, Math.Max(_bits.Length * 2, 4)));
    }

    private static ulong Bit(int index) => 1UL << (index % BitsPerWord);

    /// <summary>A mask with bits <paramref name="low"/> to <paramref name="high"/> set, inclusive.</summary>
    private static ulong MaskBetween(int low, int high)
    {
        var width = high - low + 1;

        // C# masks a shift count to 63, so `1UL << 64` is 1, not 0. The full-word case
        // has to be spelled out.
        var all = width >= BitsPerWord ? ulong.MaxValue : (1UL << width) - 1;
        return all << low;
    }
}
