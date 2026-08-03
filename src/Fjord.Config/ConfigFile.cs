using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Fjord.Config;

/// <summary>The outcome of loading a config file.</summary>
/// <param name="Config">
/// The parsed settings, or defaults when parsing failed. Never null — a broken file must
/// not leave the app without settings.
/// </param>
/// <param name="Error">Why parsing failed, or null on success.</param>
/// <param name="Existed">False when no file was present and defaults were used.</param>
public sealed record ConfigLoadResult(FjordConfig Config, string? Error, bool Existed)
{
    public bool Succeeded => Error is null;
}

/// <summary>Reads and writes <c>config.yaml</c>.</summary>
public static class ConfigFile
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)

        // A setting removed in a later version must not stop an older config from loading.
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Loads settings, falling back to defaults on any failure.
    /// </summary>
    /// <remarks>
    /// Never throws. A stray tab in a config file must not leave the user with an app that
    /// will not start; the caller reports <see cref="ConfigLoadResult.Error"/> in the
    /// status bar and carries on with what it had.
    /// </remarks>
    public static ConfigLoadResult Load(string path)
    {
        if (!File.Exists(path))
        {
            return new ConfigLoadResult(new FjordConfig(), null, Existed: false);
        }

        try
        {
            var text = File.ReadAllText(path);
            var config = Deserializer.Deserialize<FjordConfig>(text) ?? new FjordConfig();
            return new ConfigLoadResult(config, null, Existed: true);
        }
        catch (YamlException ex)
        {
            // Line and column are the only part of a YAML error a user can act on.
            return new ConfigLoadResult(
                new FjordConfig(),
                $"{Path.GetFileName(path)}({ex.Start.Line},{ex.Start.Column}): {ex.Message}",
                Existed: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ConfigLoadResult(new FjordConfig(), ex.Message, Existed: true);
        }
    }

    /// <summary>Writes the generated default file, creating its directory.</summary>
    public static void WriteDefault(
        string path, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? defaultBindings = null)
    {
        FjordPaths.EnsureDirectoryFor(path);
        File.WriteAllText(path, SettingsSchema.Render(defaultBindings));
    }

    /// <summary>Writes the default file only when none exists, and reports whether it did.</summary>
    public static bool WriteDefaultIfMissing(
        string path, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? defaultBindings = null)
    {
        if (File.Exists(path))
        {
            return false;
        }

        WriteDefault(path, defaultBindings);
        return true;
    }

    /// <summary>
    /// Sets a scalar in existing YAML text, preserving comments and key order.
    /// </summary>
    /// <remarks>
    /// Deliberately line-based rather than a YamlDotNet round trip. Serializing the object
    /// graph back out would discard every comment and reorder every key — fatal for a file
    /// whose comments <i>are</i> its documentation.
    /// <para>
    /// Returns the text unchanged if the key is not present; the caller decides whether to
    /// append. This is what <c>:set</c> will use.
    /// </para>
    /// </remarks>
    public static string SetScalar(string yaml, string dottedName, string renderedValue)
    {
        var parts = dottedName.Split('.');
        var lines = yaml.ReplaceLineEndings("\n").Split('\n');

        var depth = 0;
        var searchingFrom = 0;

        for (; depth < parts.Length - 1; depth++)
        {
            var found = FindKeyLine(lines, parts[depth], depth * 2, searchingFrom);
            if (found < 0)
            {
                return yaml;
            }

            searchingFrom = found + 1;
        }

        var target = FindKeyLine(lines, parts[^1], depth * 2, searchingFrom);
        if (target < 0)
        {
            return yaml;
        }

        lines[target] = new string(' ', depth * 2) + parts[^1] + ": " + renderedValue;
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Finds the line declaring <paramref name="key"/> at exactly the given indent.</summary>
    private static int FindKeyLine(string[] lines, string key, int indent, int from)
    {
        var prefix = new string(' ', indent) + key + ":";

        for (var i = from; i < lines.Length; i++)
        {
            var line = lines[i];

            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return i;
            }

            // A non-comment, non-blank line at or above this indent ends the block we are
            // searching, so a same-named key in a later section is not matched by mistake.
            if (from > 0 && indent > 0 && line.Length > 0 && !line.TrimStart().StartsWith('#'))
            {
                var lineIndent = line.Length - line.TrimStart().Length;
                if (lineIndent < indent)
                {
                    return -1;
                }
            }
        }

        return -1;
    }
}
