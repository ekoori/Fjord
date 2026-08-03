// Vendored from QuteSharp (src/QuteSharp.Core/Keys/KeySequence.cs). See VENDORED.md.
using System.Collections;
using System.Text;

namespace Fjord.Core.Keys;

/// <summary>Result of matching an entered key sequence against a configured one.</summary>
public enum SequenceMatch
{
    /// <summary>The entered sequence can never lead to the configured one.</summary>
    NoMatch,

    /// <summary>The entered sequence is a proper prefix; more keys may still follow.</summary>
    PartialMatch,

    /// <summary>The sequences are equal.</summary>
    ExactMatch,
}

/// <summary>
/// An immutable sequence of <see cref="KeyInfo"/>.
/// </summary>
/// <remarks>
/// qutebrowser stores a sequence as several <c>QKeySequence</c> objects of at most four
/// keys each, because that is Qt's per-object limit, then matches them pairwise. That
/// chunking is an artefact of Qt rather than part of the semantics — the net behaviour
/// is plain prefix matching — so this implements the semantics directly and has no
/// four-key boundary.
/// </remarks>
public sealed class KeySequence : IReadOnlyList<KeyInfo>, IEquatable<KeySequence>, IComparable<KeySequence>
{
    private readonly KeyInfo[] _keys;

    public KeySequence(params KeyInfo[] keys) => _keys = keys;

    public KeySequence(IEnumerable<KeyInfo> keys) => _keys = keys.ToArray();

    public static KeySequence Empty { get; } = new();

    public int Count => _keys.Length;

    public KeyInfo this[int index] => _keys[index];

    public KeySequence Slice(int start, int length) =>
        new(_keys.Skip(start).Take(length).ToArray());

    /// <summary>Returns a new sequence with <paramref name="info"/> appended.</summary>
    public KeySequence Append(KeyInfo info) => new([.. _keys, info]);

    /// <summary>Removes the Shift modifier from every key.</summary>
    public KeySequence StripModifiers() =>
        new(_keys.Select(k => k.WithStrippedModifiers(KeyModifiers.Shift)).ToArray());

    /// <summary>Rewrites keys according to a mapping of single-key sequences.</summary>
    public KeySequence WithMappings(IReadOnlyDictionary<KeySequence, KeySequence> mappings)
    {
        var result = new List<KeyInfo>();
        foreach (var info in _keys)
        {
            var single = new KeySequence(info);
            if (mappings.TryGetValue(single, out var mapped))
            {
                result.AddRange(mapped);
            }
            else
            {
                result.Add(info);
            }
        }

        return new KeySequence(result);
    }

    /// <summary>
    /// Checks whether this (entered) sequence matches <paramref name="other"/> (configured).
    /// </summary>
    public SequenceMatch Matches(KeySequence other)
    {
        if (_keys.Length > other._keys.Length)
        {
            // More keys entered than configured — no match is possible any more.
            return SequenceMatch.NoMatch;
        }

        for (var i = 0; i < _keys.Length; i++)
        {
            if (!_keys[i].Equals(other._keys[i]))
            {
                return SequenceMatch.NoMatch;
            }
        }

        return _keys.Length == other._keys.Length
            ? SequenceMatch.ExactMatch
            : SequenceMatch.PartialMatch;
    }

    /// <summary>Parses a keystring such as <c>&lt;Ctrl-a&gt;b</c>.</summary>
    /// <exception cref="KeyParseException">The keystring is malformed or names an unknown key.</exception>
    public static KeySequence Parse(string keystr)
    {
        var keys = new List<KeyInfo>();
        foreach (var part in SplitKeyString(keystr))
        {
            keys.Add(ParseSingle(keystr, part));
        }

        var sequence = new KeySequence(keys);
        sequence.Validate(keystr);
        return sequence;
    }

    private void Validate(string keystr)
    {
        foreach (var info in _keys)
        {
            if ((int)info.Key < (int)Key.Space || info.Key >= Key.Unknown)
            {
                throw new KeyParseException(keystr, "Got invalid key!");
            }
        }
    }

    /// <summary>
    /// Splits a keystring into normalised parts. An unterminated <c>&lt;</c> is treated
    /// as a literal less-than followed by single keys.
    /// </summary>
    private static IEnumerable<string> SplitKeyString(string keystr)
    {
        var key = new StringBuilder();
        var special = false;

        foreach (var c in keystr)
        {
            if (c == '>')
            {
                if (special)
                {
                    yield return NormaliseSpecial(key.ToString());
                    key.Clear();
                    special = false;
                }
                else
                {
                    yield return ">";
                }
            }
            else if (c == '<')
            {
                special = true;
            }
            else if (special)
            {
                key.Append(c);
            }
            else
            {
                yield return NormaliseSingle(c.ToString());
            }
        }

        if (special)
        {
            yield return "<";
            foreach (var c in key.ToString())
            {
                yield return NormaliseSingle(c.ToString());
            }
        }
    }

    /// <summary>Normalises e.g. <c>Control-Q</c> to <c>ctrl+q</c>.</summary>
    private static string NormaliseSpecial(string keystr)
    {
        keystr = keystr.ToLowerInvariant();

        (string From, string To)[] replacements =
        [
            ("control", "ctrl"),
            ("windows", "meta"),
            ("mod4", "meta"),
            ("command", "meta"),
            ("cmd", "meta"),
            ("super", "meta"),
            ("mod1", "alt"),
            ("less", "<"),
            ("greater", ">"),
        ];

        foreach (var (from, to) in replacements)
        {
            keystr = keystr.Replace(from, to, StringComparison.Ordinal);
        }

        foreach (var mod in (string[])["ctrl", "meta", "alt", "shift", "num"])
        {
            keystr = keystr.Replace(mod + "-", mod + "+", StringComparison.Ordinal);
        }

        return keystr;
    }

    /// <summary>An uppercase letter implies Shift.</summary>
    private static string NormaliseSingle(string keystr) =>
        keystr.Length == 1 && char.IsUpper(keystr[0]) ? "Shift+" + keystr : keystr;

    /// <summary>Turns one normalised part such as <c>ctrl+shift+a</c> into a <see cref="KeyInfo"/>.</summary>
    private static KeyInfo ParseSingle(string original, string part)
    {
        var modifiers = KeyModifiers.None;

        // Split on '+' but keep a trailing '+' as the key itself, so <Ctrl++> works.
        while (true)
        {
            var idx = part.IndexOf('+', StringComparison.Ordinal);
            if (idx <= 0 || idx == part.Length - 1)
            {
                break;
            }

            var mod = part[..idx];
            var bit = mod.ToLowerInvariant() switch
            {
                "ctrl" => KeyModifiers.Control,
                "shift" => KeyModifiers.Shift,
                "alt" => KeyModifiers.Alt,
                "meta" => KeyModifiers.Meta,
                "num" => KeyModifiers.Keypad,
                "altgr" => KeyModifiers.GroupSwitch,
                _ => KeyModifiers.None,
            };

            if (bit == KeyModifiers.None)
            {
                break;
            }

            modifiers |= bit;
            part = part[(idx + 1)..];
        }

        if (part.Length == 0)
        {
            throw new KeyParseException(original, "Got empty key!");
        }

        if (!KeyInfo.TryParseKeyName(part, out var key))
        {
            throw new KeyParseException(original, $"Got invalid key: {part}");
        }

        // A bare uppercase letter already carried Shift in from NormaliseSingle; a
        // lowercase letter with Shift asked for stays as typed.
        if (part.Length == 1 && char.IsLetter(part[0]) && char.IsUpper(part[0]))
        {
            modifiers |= KeyModifiers.Shift;
        }

        return new KeyInfo(key, modifiers);
    }

    public override string ToString() => string.Concat(_keys.Select(k => k.ToString()));

    public bool Equals(KeySequence? other) =>
        other is not null && _keys.AsSpan().SequenceEqual(other._keys);

    public override bool Equals(object? obj) => Equals(obj as KeySequence);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var key in _keys)
        {
            hash.Add(key);
        }

        return hash.ToHashCode();
    }

    public int CompareTo(KeySequence? other)
    {
        if (other is null)
        {
            return 1;
        }

        var len = Math.Min(_keys.Length, other._keys.Length);
        for (var i = 0; i < len; i++)
        {
            var cmp = _keys[i].CompareTo(other._keys[i]);
            if (cmp != 0)
            {
                return cmp;
            }
        }

        return _keys.Length.CompareTo(other._keys.Length);
    }

    public IEnumerator<KeyInfo> GetEnumerator() => ((IEnumerable<KeyInfo>)_keys).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public static bool operator <(KeySequence left, KeySequence right) => left.CompareTo(right) < 0;
    public static bool operator >(KeySequence left, KeySequence right) => left.CompareTo(right) > 0;
    public static bool operator <=(KeySequence left, KeySequence right) => left.CompareTo(right) <= 0;
    public static bool operator >=(KeySequence left, KeySequence right) => left.CompareTo(right) >= 0;
}
