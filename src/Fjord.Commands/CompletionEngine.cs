namespace Fjord.Commands;

/// <summary>One completion candidate.</summary>
public sealed record CompletionItem(string Text, string Description = "")
{
    /// <summary>What is inserted, quoted if the lexer would otherwise re-split it.</summary>
    public string Insertion => CommandLexer.Quote(Text);
}

/// <summary>
/// Candidates for the word under the caret, and the span they replace.
/// </summary>
/// <remarks>
/// The span matters as much as the list: applying a completion has to know what to
/// overwrite, or repeatedly pressing Tab appends rather than cycles and you get
/// <c>:cdcdcd</c>.
/// </remarks>
public sealed record CompletionResult(
    IReadOnlyList<CompletionItem> Items, int ReplaceStart, int ReplaceLength)
{
    public static readonly CompletionResult Empty = new([], 0, 0);

    public bool IsEmpty => Items.Count == 0;

    /// <summary>
    /// Applies a candidate, returning the new line and where the caret lands.
    /// </summary>
    /// <param name="line">
    /// Must be the line this result was computed from. The span describes <i>that</i>
    /// text, so cycling through candidates has to re-apply to the original each time
    /// rather than to whatever the previous candidate produced.
    /// </param>
    public (string Text, int Caret) Apply(string line, CompletionItem item)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(item);

        var start = Math.Clamp(ReplaceStart, 0, line.Length);
        var length = Math.Clamp(ReplaceLength, 0, line.Length - start);

        var replacement = item.Insertion;
        var text = string.Concat(line.AsSpan(0, start), replacement, line.AsSpan(start + length));
        return (text, start + replacement.Length);
    }
}

/// <summary>Somewhere the user has been, or named, offered ahead of the filesystem.</summary>
/// <param name="Kind">Shown beside the name so a quickmark is distinguishable from a visit.</param>
public sealed record Destination(string Name, string Path, string Kind);

/// <summary>Quickmarks and recent directories, for <c>:cd</c>.</summary>
/// <remarks>
/// Separate from <see cref="IPathCompletionSource"/> because it is answered from the
/// session database rather than from disk, and because a test can supply a handful of
/// entries without either.
/// </remarks>
public interface IDestinationSource
{
    /// <summary>Named places, offered first.</summary>
    IEnumerable<Destination> Quickmarks();

    /// <summary>Directories visited recently, newest first.</summary>
    IEnumerable<Destination> Recents();
}

/// <summary>Lists filesystem entries for path completion.</summary>
/// <remarks>
/// An interface so the engine stays testable without touching disk, and so the App can
/// decide how to resolve a relative path against the active pane.
/// </remarks>
public interface IPathCompletionSource
{
    /// <summary>
    /// Names directly inside <paramref name="directory"/>, with a trailing separator on
    /// directories so completing one leads straight into it.
    /// </summary>
    IEnumerable<string> List(string directory, bool directoriesOnly);

    /// <summary>Resolves a possibly-relative, possibly <c>~</c>-prefixed path.</summary>
    string Resolve(string path);
}

/// <summary>
/// Offers completions for the command line.
/// </summary>
/// <remarks>
/// Positional dispatch: word 0 completes command names, later words dispatch on the
/// resolved command and which argument the caret is in.
/// </remarks>
public sealed class CompletionEngine
{
    private readonly CommandRegistry _registry;
    private readonly IPathCompletionSource? _paths;
    private readonly Func<IReadOnlyList<CompletionItem>>? _settings;
    private readonly IDestinationSource? _destinations;

    /// <param name="settings">
    /// Setting names and their documentation. A delegate rather than a list because the
    /// current value shown alongside each name changes as the file is edited.
    /// </param>
    public CompletionEngine(
        CommandRegistry registry,
        IPathCompletionSource? paths = null,
        Func<IReadOnlyList<CompletionItem>>? settings = null,
        IDestinationSource? destinations = null)
    {
        _registry = registry;
        _paths = paths;
        _settings = settings;
        _destinations = destinations;
    }

    /// <summary>Completes the word ending at <paramref name="caret"/>.</summary>
    public CompletionResult Complete(string line, int caret)
    {
        ArgumentNullException.ThrowIfNull(line);
        caret = Math.Clamp(caret, 0, line.Length);

        // Only the text before the caret matters: completing mid-line should not consume
        // what comes after it.
        var prefix = line[..caret];
        var leading = prefix.TrimStart();
        var offset = prefix.Length - leading.Length;

        if (leading.StartsWith(':'))
        {
            leading = leading[1..];
            offset++;
        }

        var (wordStart, word) = LastWord(leading);
        var replaceStart = offset + wordStart;

        // Word zero is the command name.
        var beforeWord = leading[..wordStart].TrimEnd();
        if (beforeWord.Length == 0)
        {
            return new CompletionResult(CompleteCommandName(word), replaceStart, word.Length);
        }

        var definition = _registry.Find(FirstWord(leading));
        if (definition is null)
        {
            return CompletionResult.Empty;
        }

        return new CompletionResult(
            CompleteArgument(definition, leading, wordStart, word), replaceStart, word.Length);
    }

    private List<CompletionItem> CompleteCommandName(string word) =>
        _registry.Commands
            .Where(c => c.Name.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            .Select(c => new CompletionItem(c.Name, c.Description))
            .ToList();

    private List<CompletionItem> CompleteArgument(
        CommandDefinition definition, string leading, int wordStart, string word)
    {
        // A word starting with '-' is a flag, whatever position it is in.
        if (word.StartsWith('-'))
        {
            return definition.Flags
                .Select(f => new CompletionItem("--" + f.Name, f.Description))
                .Where(i => i.Text.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var argument = ArgumentAt(definition, leading, wordStart);
        if (argument is null)
        {
            return [];
        }

        if (argument.Choices.Count > 0)
        {
            return argument.Choices
                .Where(c => c.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                .Select(c => new CompletionItem(c))
                .ToList();
        }

        return argument.Completion switch
        {
            CompletionKind.Path => CompletePath(word, directoriesOnly: false),
            CompletionKind.Directory => CompletePath(word, directoriesOnly: true),
            CompletionKind.Setting => CompleteSetting(word),
            CompletionKind.Quickmark => CompleteQuickmark(word),
            CompletionKind.Destination => CompleteDestination(word),
            _ => [],
        };
    }

    private List<CompletionItem> CompleteQuickmark(string word) =>
        [.. (_destinations?.Quickmarks() ?? [])
            .Where(d => d.Name.Contains(word, StringComparison.OrdinalIgnoreCase))
            .Select(d => new CompletionItem(d.Name, d.Path))];

    /// <summary>
    /// Quickmarks, then recents, then whatever is on disk.
    /// </summary>
    /// <remarks>
    /// Order is the whole feature. Somewhere you named and somewhere you were yesterday are
    /// both better answers than the first alphabetical subdirectory, and putting them above
    /// the filesystem is what turns <c>:cd</c> into a picker rather than a path prompt.
    /// <para>
    /// A word containing a separator is already a path, so the named entries are dropped:
    /// once you are typing <c>src/</c> you have said what you want.
    /// </para>
    /// </remarks>
    private List<CompletionItem> CompleteDestination(string word)
    {
        var items = new List<CompletionItem>();

        if (word.IndexOfAny(['/', '\\']) < 0)
        {
            var quickmarks = (_destinations?.Quickmarks() ?? []).ToList();

            var named = quickmarks
                .Concat(_destinations?.Recents() ?? [])
                .Where(d => d.Name.Contains(word, StringComparison.OrdinalIgnoreCase)
                    || d.Path.Contains(word, StringComparison.OrdinalIgnoreCase));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var destination in named)
            {
                // A recent directory that is also a quickmark is one place, and listing it
                // twice would push a real second option off the panel.
                if (!seen.Add(destination.Path))
                {
                    continue;
                }

                // A quickmark inserts its name, because that is what :cd resolves. A recent
                // has no name to resolve, so it inserts its path and shows its last segment
                // — insert the segment and ":cd" would look for a subdirectory of wherever
                // the user happens to be standing.
                var isMark = destination.Kind == "mark";

                items.Add(isMark
                    ? new CompletionItem(destination.Name, $"mark      {destination.Path}")
                    : new CompletionItem(destination.Path, $"recent    {destination.Name}"));
            }
        }

        items.AddRange(CompletePath(word, directoriesOnly: true));
        return items;
    }

    private List<CompletionItem> CompleteSetting(string word) =>
        (_settings?.Invoke() ?? [])
            .Where(s => s.Text.Contains(word, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// Decides which declared argument the caret is sitting in.
    /// </summary>
    /// <remarks>
    /// Counts only the positional words before the caret, skipping flags and the values
    /// they consume — otherwise <c>:sort -k name &lt;tab&gt;</c> would offer completions for
    /// the second positional argument when the caret is still in the first.
    /// </remarks>
    private static CommandArgument? ArgumentAt(CommandDefinition definition, string leading, int wordStart)
    {
        var before = leading[..wordStart];

        IReadOnlyList<string> tokens;
        try
        {
            tokens = CommandLexer.Split(before);
        }
        catch (CommandException)
        {
            // Unbalanced quotes while still typing is normal; just do not complete.
            return null;
        }

        // The caret may be sitting in a flag's value rather than in a positional argument:
        // ":sort -k <tab>" is completing the choices declared on --key, and `sort` has no
        // positional arguments at all.
        if (tokens.Count > 1 && IsFlagToken(tokens[^1]) && TakesValue(definition, tokens[^1]))
        {
            return FlagArgument(definition, tokens[^1]);
        }

        var positional = 0;

        // Skip the command name itself.
        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];

            if (IsFlagToken(token))
            {
                if (TakesValue(definition, token) && i + 1 < tokens.Count)
                {
                    i++;
                }

                continue;
            }

            positional++;
        }

        var declared = definition.Positional.ToList();
        if (declared.Count == 0)
        {
            return null;
        }

        // A variadic tail keeps completing with the same rules however many words follow.
        return positional < declared.Count
            ? declared[positional]
            : declared[^1].IsVariadic ? declared[^1] : null;
    }

    private static bool IsFlagToken(string token) =>
        token.StartsWith('-') && token.Length > 1 && !char.IsDigit(token[1]);

    private static CommandArgument? FlagArgument(CommandDefinition definition, string flag) =>
        flag.StartsWith("--", StringComparison.Ordinal)
            ? definition.Flags.FirstOrDefault(a => a.Name == flag[2..])
            : definition.Flags.FirstOrDefault(a => a.ShortFlag == flag[^1]);

    private static bool TakesValue(CommandDefinition definition, string flag) =>
        flag.StartsWith("--", StringComparison.Ordinal)
            ? definition.Flags.FirstOrDefault(a => a.Name == flag[2..])?.TakesValue == true
            : definition.Flags.FirstOrDefault(a => a.ShortFlag == flag[^1])?.TakesValue == true;

    /// <summary>
    /// Completes a path, splitting what was typed into a directory to list and a stem.
    /// </summary>
    /// <remarks>
    /// The replacement keeps the directory part the user typed, so completing
    /// <c>src/Fj</c> yields <c>src/Fjord.Core/</c> rather than jumping to an absolute path.
    /// </remarks>
    private List<CompletionItem> CompletePath(string word, bool directoriesOnly)
    {
        if (_paths is null)
        {
            return [];
        }

        var separator = word.LastIndexOfAny(['/', '\\']);
        var typedDirectory = separator < 0 ? "" : word[..(separator + 1)];
        var stem = separator < 0 ? word : word[(separator + 1)..];

        string listing;
        try
        {
            listing = _paths.Resolve(typedDirectory.Length == 0 ? "." : typedDirectory);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return [];
        }

        return _paths.List(listing, directoriesOnly)
            .Where(name => name.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new CompletionItem(typedDirectory + name))
            .ToList();
    }

    /// <summary>The last whitespace-separated word, honouring quotes so a name with spaces stays one.</summary>
    private static (int Start, string Word) LastWord(string text)
    {
        // Starts at 0, not text.Length: a line with no whitespace at all is one word
        // beginning at the start, and initialising to the end makes every such line
        // complete as an empty word.
        var start = 0;
        var inSingle = false;
        var inDouble = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
            }
            else if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
            }
            else if ((c == ' ' || c == '\t') && !inSingle && !inDouble)
            {
                start = i + 1;
            }
        }

        start = Math.Min(start, text.Length);
        var word = text[start..];

        // Strip an opening quote so the stem matches against real names.
        if (word.Length > 0 && (word[0] == '"' || word[0] == '\''))
        {
            return (start, word[1..]);
        }

        return (start, word);
    }

    private static string FirstWord(string text)
    {
        var end = text.IndexOfAny([' ', '\t']);
        return end < 0 ? text : text[..end];
    }
}
