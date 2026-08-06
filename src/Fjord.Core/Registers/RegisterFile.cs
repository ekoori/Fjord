using Fjord.Core.Operations;

namespace Fjord.Core.Registers;

/// <summary>
/// Named holders for yanked and cut paths, addressed as <c>"a</c> through <c>"z</c>.
/// </summary>
/// <remarks>
/// The point of named registers in a file manager is the same as in vim: keeping more than
/// one set of paths alive at once. Collect a few files into <c>"a</c>, a few more into
/// <c>"b</c>, walk somewhere else and paste either.
/// <para>
/// Writing a named register also writes the unnamed one, exactly as vim does, so
/// <c>"ay</c> followed by a bare <c>p</c> pastes what was just yanked rather than whatever
/// happened to be in the unnamed register beforehand.
/// </para>
/// </remarks>
public sealed class RegisterFile
{
    /// <summary>The register a command uses when none was named.</summary>
    public const char UnnamedName = '"';

    private readonly Dictionary<char, ClipboardContents> _registers = [];

    /// <summary>What a bare <c>p</c> pastes.</summary>
    public ClipboardContents Unnamed
    {
        get => this[UnnamedName];
        set => this[UnnamedName] = value;
    }

    /// <summary>
    /// Reads or writes one register.
    /// </summary>
    /// <remarks>
    /// Names fold to lower case. In vim an upper-case name appends rather than replaces;
    /// that is not implemented here, and folding is what keeps <c>"A</c> from silently
    /// becoming a second, separate register the user cannot find again.
    /// </remarks>
    public ClipboardContents this[char name]
    {
        get => _registers.GetValueOrDefault(Normalise(name), ClipboardContents.Empty);

        set
        {
            var key = Normalise(name);
            _registers[key] = value;

            if (key != UnnamedName)
            {
                _registers[UnnamedName] = value;
            }
        }
    }

    /// <summary>Registers holding something, in name order. The unnamed one comes first.</summary>
    public IReadOnlyList<char> Names =>
        [.. _registers
            .Where(r => !r.Value.IsEmpty)
            .Select(r => r.Key)
            .OrderBy(name => name == UnnamedName ? 0 : 1)
            .ThenBy(name => name)];

    /// <summary>Empties one register, or every register when no name is given.</summary>
    public void Clear(char? name = null)
    {
        if (name is { } one)
        {
            _registers.Remove(Normalise(one));
        }
        else
        {
            _registers.Clear();
        }
    }

    private static char Normalise(char name) => char.ToLowerInvariant(name);
}
