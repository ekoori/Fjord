using System.Reflection;
using System.Runtime.InteropServices;

namespace Fjord.Core.Diagnostics;

/// <summary>
/// What build this is, for <c>:version</c>, the help page and the log.
/// </summary>
/// <remarks>
/// Read from the assembly rather than written down here, so there is one place a release
/// is numbered — <c>VersionPrefix</c> in Directory.Build.props, or whatever the packaging
/// script stamped — and no second copy to forget.
/// </remarks>
public static class BuildInfo
{
    /// <summary>
    /// The version as the compiler stamped it, including the commit when there is one.
    /// </summary>
    /// <remarks>
    /// Declared before <see cref="Version"/>, which is derived from it: static fields are
    /// initialised in declaration order, so the other way round reads this one before it
    /// has a value.
    /// <para>
    /// For the log, where "which commit" is the question being asked months later. The
    /// status bar gets <see cref="Version"/> instead: a hash after the number is noise to
    /// someone checking which release they are running.
    /// </para>
    /// </remarks>
    public static string FullVersion { get; } = Read();

    /// <summary>
    /// The version, as <c>0.1.0</c> or <c>0.1.0-beta.3</c>.
    /// </summary>
    /// <remarks>
    /// Read from the entry assembly, not this one: the version is stamped on the
    /// executable, and a library compiled into the same build still carries whatever
    /// default it was given. Falls back to this assembly for a test host, which is its own
    /// entry point.
    /// </remarks>
    public static string Version { get; } = Trim(FullVersion);

    /// <summary>The platform and runtime, for a bug report that says more than the version.</summary>
    public static string Runtime { get; } =
        $".NET {Environment.Version} on {RuntimeInformation.OSDescription.Trim()} "
        + $"{RuntimeInformation.OSArchitecture}";

    /// <summary>Version, platform and runtime as one line.</summary>
    public static string Summary => $"Fjord {Version} — {Runtime}";

    /// <summary>Drops the "+&lt;commit sha&gt;" the SDK appends when the repository is known.</summary>
    private static string Trim(string version)
    {
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus];
    }

    private static string Read()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(BuildInfo).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        return string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString() ?? "unknown"
            : informational;
    }
}
