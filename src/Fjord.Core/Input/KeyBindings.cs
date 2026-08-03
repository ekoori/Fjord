// Vendored from QuteSharp (src/QuteSharp.KeyInput/KeyBindings.cs). See VENDORED.md.
// Changed: KeyMode is a record struct rather than an enum, and Match no longer rebuilds
// the merged binding map on every keystroke.
using Fjord.Core.Keys;

namespace Fjord.Core.Input;

/// <summary>
/// The key-to-command mapping for every mode.
/// </summary>
/// <remarks>
/// Defaults come from the shipped binding table and user changes are layered on top.
/// Keeping the two layers separate is what lets <c>:bind-default</c> restore a binding
/// and lets the config file store only what the user actually changed.
/// </remarks>
public sealed class KeyBindings
{
    private readonly Dictionary<KeyMode, Dictionary<KeySequence, string>> _defaults = [];
    private readonly Dictionary<KeyMode, Dictionary<KeySequence, string>> _user = [];

    /// <summary>
    /// Merged-and-indexed view per mode, rebuilt lazily after a mutation.
    /// </summary>
    /// <remarks>
    /// QuteSharp merges the two layers inside <c>Match</c>, so every keystroke allocated
    /// a dictionary and populated it with every binding in the mode before scanning it.
    /// Bindings change on config load, not on key press, so the merge is cached and the
    /// scan is narrowed to bindings that start with the key actually typed.
    /// </remarks>
    private readonly Dictionary<KeyMode, ModeIndex> _index = [];

    /// <summary>Installs a default binding, as read from the shipped table.</summary>
    public void AddDefault(KeyMode mode, string keys, string command)
    {
        Layer(_defaults, mode)[KeySequence.Parse(keys)] = command;
        Invalidate(mode);
    }

    /// <summary>Binds a key for the user, overriding any default.</summary>
    public void Bind(KeyMode mode, string keys, string command)
    {
        Layer(_user, mode)[KeySequence.Parse(keys)] = command;
        Invalidate(mode);
    }

    /// <summary>
    /// Removes a binding. A default binding is masked rather than deleted, so
    /// <see cref="BindDefault"/> can restore it.
    /// </summary>
    public void Unbind(KeyMode mode, string keys)
    {
        var sequence = KeySequence.Parse(keys);

        if (Layer(_defaults, mode).ContainsKey(sequence))
        {
            // An empty command marks the default as suppressed; a plain removal from
            // the user layer would let the default reappear.
            Layer(_user, mode)[sequence] = "";
        }
        else
        {
            Layer(_user, mode).Remove(sequence);
        }

        Invalidate(mode);
    }

    /// <summary>Restores the default binding for a key.</summary>
    public void BindDefault(KeyMode mode, string keys)
    {
        Layer(_user, mode).Remove(KeySequence.Parse(keys));
        Invalidate(mode);
    }

    /// <summary>Drops every user binding, as a config reload does before re-applying them.</summary>
    public void ClearUserBindings()
    {
        _user.Clear();
        _index.Clear();
    }

    /// <summary>
    /// The effective bindings for a mode, user layer over defaults, with suppressed
    /// entries removed.
    /// </summary>
    public IReadOnlyDictionary<KeySequence, string> For(KeyMode mode) => Index(mode).Effective;

    /// <summary>
    /// Matches a typed sequence against a mode's bindings.
    /// </summary>
    /// <returns>
    /// The best match. A partial match matters as much as an exact one: it is what
    /// tells the caller to keep buffering rather than discard the keys.
    /// </returns>
    public BindingMatch Match(KeyMode mode, KeySequence typed)
    {
        if (typed.Count == 0)
        {
            return new BindingMatch(SequenceMatch.NoMatch, null, null);
        }

        // Only bindings whose first key is the one typed can match at all, so the scan
        // is over a handful of candidates rather than every binding in the mode.
        if (!Index(mode).ByFirstKey.TryGetValue(typed[0], out var candidates))
        {
            return new BindingMatch(SequenceMatch.NoMatch, null, null);
        }

        var partial = false;

        foreach (var (sequence, command) in candidates)
        {
            // The receiver is the ENTERED sequence: "g" entered against "gg"
            // configured is a partial match, not the other way round.
            switch (typed.Matches(sequence))
            {
                case SequenceMatch.ExactMatch:
                    return new BindingMatch(SequenceMatch.ExactMatch, command, sequence);

                case SequenceMatch.PartialMatch:
                    partial = true;
                    break;
            }
        }

        return partial
            ? new BindingMatch(SequenceMatch.PartialMatch, null, null)
            : new BindingMatch(SequenceMatch.NoMatch, null, null);
    }

    private void Invalidate(KeyMode mode) => _index.Remove(mode);

    private ModeIndex Index(KeyMode mode)
    {
        if (_index.TryGetValue(mode, out var cached))
        {
            return cached;
        }

        var effective = new Dictionary<KeySequence, string>(Layer(_defaults, mode));

        foreach (var (sequence, command) in Layer(_user, mode))
        {
            if (command.Length == 0)
            {
                effective.Remove(sequence);
            }
            else
            {
                effective[sequence] = command;
            }
        }

        var byFirstKey = new Dictionary<KeyInfo, List<KeyValuePair<KeySequence, string>>>();
        foreach (var entry in effective)
        {
            if (entry.Key.Count == 0)
            {
                continue;
            }

            if (!byFirstKey.TryGetValue(entry.Key[0], out var bucket))
            {
                bucket = [];
                byFirstKey[entry.Key[0]] = bucket;
            }

            bucket.Add(entry);
        }

        var built = new ModeIndex(effective, byFirstKey);
        _index[mode] = built;
        return built;
    }

    private static Dictionary<KeySequence, string> Layer(
        Dictionary<KeyMode, Dictionary<KeySequence, string>> layers, KeyMode mode)
    {
        if (!layers.TryGetValue(mode, out var bindings))
        {
            bindings = [];
            layers[mode] = bindings;
        }

        return bindings;
    }

    private sealed record ModeIndex(
        Dictionary<KeySequence, string> Effective,
        Dictionary<KeyInfo, List<KeyValuePair<KeySequence, string>>> ByFirstKey);
}

/// <summary>The outcome of matching typed keys against a mode's bindings.</summary>
public readonly record struct BindingMatch(SequenceMatch Match, string? Command, KeySequence? Sequence)
{
    public bool IsExact => Match == SequenceMatch.ExactMatch;
    public bool IsPartial => Match == SequenceMatch.PartialMatch;
}
