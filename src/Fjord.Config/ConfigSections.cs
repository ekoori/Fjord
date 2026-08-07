namespace Fjord.Config;

/// <summary>
/// Adds and removes whole named blocks in existing YAML text.
/// </summary>
/// <remarks>
/// <see cref="ConfigFile.SetScalar"/> can change a value that is already there; this puts a
/// section in or takes one out. Both are line-based for the same reason: a YamlDotNet round
/// trip would discard every comment and reorder every key, and in a file whose comments
/// <i>are</i> its documentation that is not a rewrite, it is a deletion.
/// <para>
/// The parent is created when it is absent, because <c>:listing-save</c> has to work on a
/// configuration file that has never mentioned listings.
/// </para>
/// </remarks>
public static class ConfigSections
{
    /// <summary>
    /// Writes a named block under <paramref name="parent"/>, replacing any block of that name.
    /// </summary>
    /// <param name="fields">The block's contents, as already-rendered <c>key: value</c> lines.</param>
    public static string SetSection(
        string yaml, string parent, string name, IReadOnlyList<string> fields)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentException.ThrowIfNullOrEmpty(parent);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(fields);

        var lines = Split(yaml);
        var block = new List<string> { $"  {name}:" };
        block.AddRange(fields.Select(f => $"    {f}"));

        var existing = FindSection(lines, parent, name);

        if (existing is { } span)
        {
            lines.RemoveRange(span.Start, span.Length);
            lines.InsertRange(span.Start, block);
            return Join(lines);
        }

        var parentLine = FindTopLevelKey(lines, parent);

        if (parentLine < 0)
        {
            // No parent at all. Appended rather than inserted anywhere clever: a section
            // the user has never had is not one they have arranged around.
            if (lines.Count > 0 && lines[^1].Length > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add($"{parent}:");
            lines.AddRange(block);
            return Join(lines);
        }

        // An empty map — "listings: {}" — is a parent in name only, and appending a child
        // under it would leave a block that YAML reads as still empty.
        if (lines[parentLine].TrimEnd().EndsWith("{}", StringComparison.Ordinal))
        {
            lines[parentLine] = $"{parent}:";
        }

        lines.InsertRange(EndOfSection(lines, parentLine), block);
        return Join(lines);
    }

    /// <summary>Removes a named block. Returns the text unchanged when it is not there.</summary>
    public static string RemoveSection(string yaml, string parent, string name)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        var lines = Split(yaml);

        if (FindSection(lines, parent, name) is not { } span)
        {
            return yaml;
        }

        lines.RemoveRange(span.Start, span.Length);
        return Join(lines);
    }

    /// <summary>Names of the blocks directly under a parent, in the order the file has them.</summary>
    public static IReadOnlyList<string> SectionNames(string yaml, string parent)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        var lines = Split(yaml);
        var parentLine = FindTopLevelKey(lines, parent);

        if (parentLine < 0)
        {
            return [];
        }

        var names = new List<string>();

        for (var i = parentLine + 1; i < EndOfSection(lines, parentLine); i++)
        {
            var line = lines[i];

            if (IndentOf(line) == 2 && line.TrimEnd().EndsWith(':') && !IsComment(line))
            {
                names.Add(line.Trim().TrimEnd(':').Trim('"'));
            }
        }

        return names;
    }

    /// <summary>
    /// The lines a named block occupies, including any comments directly above it.
    /// </summary>
    /// <remarks>
    /// The comments come with it. A block's documentation belongs to the block, and leaving
    /// it behind on removal would orphan it above whatever happened to follow.
    /// </remarks>
    private static (int Start, int Length)? FindSection(List<string> lines, string parent, string name)
    {
        var parentLine = FindTopLevelKey(lines, parent);

        if (parentLine < 0)
        {
            return null;
        }

        var end = EndOfSection(lines, parentLine);

        for (var i = parentLine + 1; i < end; i++)
        {
            if (IndentOf(lines[i]) != 2 || IsComment(lines[i]))
            {
                continue;
            }

            var key = lines[i].Trim().TrimEnd(':').Trim('"');

            if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var start = i;
            while (start > parentLine + 1 && IsComment(lines[start - 1]))
            {
                start--;
            }

            var stop = i + 1;
            while (stop < end && (IndentOf(lines[stop]) > 2 || lines[stop].Trim().Length == 0))
            {
                stop++;
            }

            // A trailing blank belongs to whatever comes next, not to this block.
            while (stop > i + 1 && lines[stop - 1].Trim().Length == 0)
            {
                stop--;
            }

            return (start, stop - start);
        }

        return null;
    }

    private static int FindTopLevelKey(List<string> lines, string key)
    {
        var prefix = key + ":";

        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith(prefix, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The line after the last one belonging to a top-level section.</summary>
    private static int EndOfSection(List<string> lines, int parentLine)
    {
        var end = parentLine + 1;

        while (end < lines.Count && (IndentOf(lines[end]) > 0 || lines[end].Trim().Length == 0))
        {
            end++;
        }

        // Blank lines between sections belong to the gap, not to the section above.
        while (end > parentLine + 1 && lines[end - 1].Trim().Length == 0)
        {
            end--;
        }

        return end;
    }

    private static int IndentOf(string line) => line.Length - line.TrimStart().Length;

    private static bool IsComment(string line) => line.TrimStart().StartsWith('#');

    private static List<string> Split(string yaml) => [.. yaml.ReplaceLineEndings("\n").Split('\n')];

    private static string Join(List<string> lines) => string.Join(Environment.NewLine, lines);
}
