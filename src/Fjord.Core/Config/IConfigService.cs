namespace Fjord.Core.Config;

/// <summary>One setting, as <c>:set</c> and completion see it.</summary>
/// <param name="Type">Human-readable, e.g. <c>true | false</c>. Shown when a value is rejected.</param>
/// <param name="Current">What the file says now, already rendered as it appears there.</param>
public sealed record SettingInfo(string Name, string Type, string Doc, string? Current);

/// <summary>
/// The configuration file, as commands see it.
/// </summary>
/// <remarks>
/// Exists so <c>Fjord.Commands</c> can offer <c>:config</c> and <c>:set</c> without
/// depending on <c>Fjord.Config</c> — the edge the architecture deliberately avoids.
/// <para>
/// <c>:bind</c> is still absent, and so is a method for it. Editing a binding means
/// rewriting a nested map rather than a scalar, and the shape of that call is worth
/// deciding when there is a caller to shape it.
/// </para>
/// </remarks>
public interface IConfigService
{
    /// <summary>Full path of the file the user edits.</summary>
    string ConfigFilePath { get; }

    /// <summary>
    /// Writes the documented default file if none exists.
    /// </summary>
    /// <returns>True when a file was created.</returns>
    /// <remarks>
    /// Called before opening it, so <c>:config</c> on a fresh install shows something worth
    /// reading rather than an editor complaining the file is missing.
    /// </remarks>
    bool EnsureExists();

    /// <summary>Re-reads the file now, rather than waiting for the watcher.</summary>
    /// <returns>The error if it failed to parse, or null on success.</returns>
    string? Reload();

    /// <summary>Every documented setting, with what the file currently says.</summary>
    IReadOnlyList<SettingInfo> Settings { get; }

    /// <summary>
    /// Named listings, in the order <c>:sort</c> cycles through them.
    /// </summary>
    /// <remarks>
    /// Order is part of the contract. A cycle whose sequence came from a dictionary's
    /// enumeration would change between runs, and a key that lands somewhere different
    /// each time is not a key anyone learns.
    /// </remarks>
    IReadOnlyList<string> ListingNames { get; }

    /// <summary>One named listing, or null when there is none under that name.</summary>
    (FileSystem.SortSpec Sort, FileSystem.FilterSpec Filter)? Listing(string name);

    /// <summary>Writes a listing to the file under this name, replacing any of the same.</summary>
    /// <returns>Why it was refused, or null when it was written.</returns>
    string? SaveListing(string name, FileSystem.SortSpec sort, FileSystem.FilterSpec filter);

    /// <summary>Removes a named listing from the file.</summary>
    /// <returns>Why it was refused, or null when it was removed.</returns>
    string? RemoveListing(string name);

    /// <summary>
    /// Writes one setting to the file and applies it.
    /// </summary>
    /// <returns>Why it was refused, or null when it was written.</returns>
    /// <remarks>
    /// Goes through the file rather than an in-memory override, so what the user sees when
    /// they next open <c>config.yaml</c> is what is actually in effect. A <c>:set</c> that
    /// only lived until restart would be a worse kind of surprise than no <c>:set</c> at all.
    /// </remarks>
    string? Set(string name, string value);
}
