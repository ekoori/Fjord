namespace Fjord.Core.Config;

/// <summary>
/// The configuration file, as commands see it.
/// </summary>
/// <remarks>
/// Deliberately narrow. The plan sketched a <c>Get</c>/<c>Set</c>/<c>Bind</c> surface, but
/// nothing needs one yet — <c>:set</c> and <c>:bind</c> do not exist, and editing the file
/// is how settings change today. Adding those methods now would be guessing at their shape
/// before there is a caller to shape them.
/// <para>
/// It exists at all so <c>Fjord.Commands</c> can offer <c>:config</c> without depending on
/// <c>Fjord.Config</c> — the edge the architecture deliberately avoids.
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
}
