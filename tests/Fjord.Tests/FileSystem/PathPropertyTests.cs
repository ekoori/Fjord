using Fjord.Core.FileSystem;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Fjord.Tests.FileSystem;

/// <summary>
/// Properties of the path handling the rest of Fjord is built on.
/// </summary>
/// <remarks>
/// Fjord stores an entry's <i>name</i> and its pane's <i>directory</i>, and reconstitutes a
/// full path on demand. Every one of those reconstitutions is a
/// <see cref="Path.Combine(string, string)"/>, and several parts of the app depend on
/// behaviour that is easy to state and easy to be wrong about — that combining a rooted
/// name returns the name, that the name survives the round trip, that normalisation
/// settles. Hand-picked examples cover the cases someone thought of; these cover the ones
/// nobody did, which is where cross-platform path bugs live.
/// </remarks>
public class PathPropertyTests
{
    /// <summary>
    /// Entry names, weighted towards the ones that break things.
    /// </summary>
    /// <remarks>
    /// Trailing dots and spaces, reserved device names, leading dashes, Unicode, and the
    /// separators of the other platform. Everything is filtered through
    /// <see cref="Path.GetInvalidFileNameChars"/>, which is what makes the same table mean
    /// different things per platform: a backslash or a colon in a name is ordinary on Linux
    /// and unrepresentable on Windows, so the Linux CI run exercises those and the Windows
    /// one does not. A name the running platform cannot hold is not worth asserting about.
    /// </remarks>
    private static readonly string[] Interesting =
    [
        "a", "a.txt", "A.TXT", ".hidden", "-leading-dash", "with space",
        "trailing.", "trailing ", "CON", "NUL", "COM1", "LPT1", "aux.txt",
        "back\\slash", "colon:name", "quote\"name", "star*name", "pipe|name",
        "émoji", "日本語", "́combining", "a b  c", "..dots", "...",
        new string('x', 250),
    ];

    private static Arbitrary<string> Names() =>
        Gen.OneOf(
                Gen.Elements(Interesting.Where(Representable).ToArray()),
                ArbMap.Default.ArbFor<string>().Generator
                    .Where(s => !string.IsNullOrEmpty(s) && Representable(s)))
            .ToArbitrary();

    /// <summary>Whether this platform could hold a file with this name at all.</summary>
    private static bool Representable(string name) =>
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !name.Contains('/', StringComparison.Ordinal)
        && !name.Contains('\\', StringComparison.Ordinal);

    private static Arbitrary<string> Directories() =>
        Gen.Elements(
                Path.GetTempPath(),
                Path.Combine(Path.GetTempPath(), "one", "two"),
                Path.Combine(Path.GetTempPath(), "with space"),
                Path.Combine(Path.GetTempPath(), "trailing") + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? @"C:\" : "/",
                OperatingSystem.IsWindows() ? @"\\server\share\folder" : "/mnt/share/folder")
            .ToArbitrary();

    [Property]
    public Property TheNameSurvivesTheRoundTrip() =>
        Prop.ForAll(Directories(), Names(), (directory, name) =>
        {
            // The reason storing only the name is safe: 100,000 entries hold no paths, and
            // the path is rebuilt on demand for the forty on screen. If a name could not be
            // recovered from what Combine produced, that whole design would be unsound.
            var combined = Path.Combine(directory, name);
            return string.Equals(Path.GetFileName(combined), name, StringComparison.Ordinal);
        });

    [Property]
    public Property CombiningARootedNameReturnsTheName() =>
        Prop.ForAll(Directories(), Names(), (directory, name) =>
        {
            // What makes the scrapbook work. Its entries come from many directories, so
            // each entry's name IS its full path, and PathAt combines it against a
            // synthetic label like "scrapbook:work" that is not a directory at all.
            var rooted = Path.Combine(Path.GetTempPath(), name);
            return string.Equals(
                Path.Combine(directory, rooted), rooted, StringComparison.Ordinal);
        });

    [Property]
    public Property CombineDoesNotLoseTheDirectory() =>
        Prop.ForAll(Directories(), Names(), (directory, name) =>
            Path.Combine(directory, name)
                .StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal));

    [Property]
    public Property NormalisingIsIdempotent() =>
        Prop.ForAll(Directories(), Names(), (directory, name) =>
        {
            // Navigation calls GetFullPath on every hop. If it did not settle, going into a
            // directory and back out could land somewhere that is not where you started.
            var once = Path.GetFullPath(Path.Combine(directory, name));
            return string.Equals(Path.GetFullPath(once), once, StringComparison.Ordinal);
        });

    [Property]
    public Property GoingUpAndBackDownReturnsToTheSamePlace() =>
        Prop.ForAll(Directories(), Names(), (directory, name) =>
        {
            // What h then l has to do. Only asked of paths Fjord would actually hand out:
            // a name the platform resolves elsewhere is refused by PathSafety long before
            // anything tries to navigate to it.
            var combined = Path.Combine(directory, name);

            if (!PathSafety.IsAddressable(combined))
            {
                return true;
            }

            var start = Path.GetFullPath(combined);
            var parent = Path.GetDirectoryName(start);

            if (parent is null)
            {
                // A root has no parent; nothing to assert.
                return true;
            }

            var back = Path.GetFullPath(Path.Combine(parent, Path.GetFileName(start)));
            return string.Equals(back, start, StringComparison.Ordinal);
        });

    [Property]
    public Property AnAddressablePathKeepsItsNameThroughNormalisation() =>
        Prop.ForAll(Directories(), Names(), (directory, name) =>
        {
            // The guarantee PathSafety buys, and the precondition the property above needs.
            // Without the guard, "..." on Windows normalises to the directory holding it —
            // which is how a trash of one entry becomes a trash of its parent.
            var combined = Path.Combine(directory, name);

            return !PathSafety.IsAddressable(combined)
                || string.Equals(
                    Path.GetFileName(Path.GetFullPath(combined)),
                    name,
                    StringComparison.Ordinal);
        });

    [Property]
    public Property NoPathOperationThrows() =>
        Prop.ForAll(Directories(), Names(), (directory, name) =>
        {
            // The blunt one, and the one most likely to fire. A reserved device name or a
            // trailing dot must not take the app down from inside a listing — the plan's
            // rule for path trouble is log and skip, never crash.
            var combined = Path.Combine(directory, name);
            Path.GetFullPath(combined);
            Path.GetFileName(combined);
            Path.GetDirectoryName(combined);
            Path.GetExtension(combined);
            Path.GetFileNameWithoutExtension(combined);
            return true;
        });

    [Property]
    public Property AbbreviatingNeverInventsATilde() =>
        Prop.ForAll(Names(), name =>
        {
            // The bug this guards: "/home/igor2" abbreviating to "~2" for a user whose home
            // is /home/igor. A prefix match is not a path match.
            const string home = "/home/igor";
            var path = "/home/igor" + name;
            var display = Fjord.Platform.UnixMounts.Abbreviate(path, home);

            return display.StartsWith('~')
                ? string.Equals(display, "~" + path[home.Length..], StringComparison.Ordinal)
                    && path[home.Length] == '/'
                : string.Equals(display, path, StringComparison.Ordinal);
        });

    [SkippableTheory]
    [InlineData("...")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    public void WindowsResolvesTheseSomewhereElseSoTheyAreRefused(string name)
    {
        // Found by the properties above rather than by anyone thinking of it, and worth
        // pinning as an example because the consequence is severe and invisible: Windows
        // strips trailing dots and spaces while resolving, so an entry named "..." in
        // C:\work resolves to C:\work itself. Trash it and you trash the folder.
        Skip.IfNot(OperatingSystem.IsWindows(), "Only Windows rewrites paths this way.");

        var path = Path.Combine(@"C:\work", name);

        Assert.False(PathSafety.IsAddressable(path));
    }

    [Fact]
    public void OrdinaryNamesAreAddressableEverywhere()
    {
        foreach (var name in new[] { "a.txt", "with space", "émoji", "CON", ".hidden", "..dots" })
        {
            Assert.True(
                PathSafety.IsAddressable(Path.Combine(Path.GetTempPath(), name)),
                $"'{name}' should be addressable");
        }
    }

    [Property]
    public Property AbbreviatingLeavesUnrelatedPathsAlone() =>
        Prop.ForAll(Names(), name =>
        {
            var path = "/var/log/" + name;
            return string.Equals(
                Fjord.Platform.UnixMounts.Abbreviate(path, "/home/igor"),
                path,
                StringComparison.Ordinal);
        });
}
