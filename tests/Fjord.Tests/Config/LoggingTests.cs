using Fjord.Config.Logging;
using Fjord.Tests.FileSystem;
using Microsoft.Extensions.Logging;

namespace Fjord.Tests.Config;

public class LogLevelTests
{
    [Theory]
    [InlineData("off", LogLevel.None)]
    [InlineData("none", LogLevel.None)]
    [InlineData("error", LogLevel.Error)]
    [InlineData("err", LogLevel.Error)]
    [InlineData("warning", LogLevel.Warning)]
    [InlineData("warn", LogLevel.Warning)]
    [InlineData("information", LogLevel.Information)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("trace", LogLevel.Trace)]
    [InlineData("  DEBUG  ", LogLevel.Debug)]
    public void LevelNamesAreRead(string name, LogLevel expected) =>
        Assert.Equal(expected, LogLevels.Parse(name));

    [Theory]
    [InlineData("verbose")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnknownLevelFallsBackToInformation(string? name)
    {
        // Not None. A typo must not silently switch logging off, because the symptom of
        // that is an empty log file at exactly the moment someone needs it.
        Assert.Equal(LogLevel.Information, LogLevels.Parse(name));
    }
}

public class RollingFileLoggerTests
{
    [Fact]
    public void RecordsReachTheFile()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "fjord.log");

        using (var provider = new RollingFileLoggerProvider(path, LogLevel.Information))
        {
            provider.CreateLogger("Fjord.App.Thing").LogInformation("listed {Count} entries", 42);
        }

        var text = File.ReadAllText(path);
        Assert.Contains("listed 42 entries", text, StringComparison.Ordinal);
        Assert.Contains("INF", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCategoryLosesItsAssemblyPrefix()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "fjord.log");

        using (var provider = new RollingFileLoggerProvider(path, LogLevel.Information))
        {
            provider.CreateLogger("Fjord.App.PaneController").LogInformation("hello");
        }

        var text = File.ReadAllText(path);
        Assert.Contains("App.PaneController: hello", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Fjord.App.PaneController", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordsBelowTheLevelCostNothing()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "fjord.log");

        using (var provider = new RollingFileLoggerProvider(path, LogLevel.Warning))
        {
            var logger = provider.CreateLogger("Test");
            Assert.False(logger.IsEnabled(LogLevel.Debug));
            logger.LogDebug("dropped");
            logger.LogWarning("kept");
        }

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("dropped", text, StringComparison.Ordinal);
        Assert.Contains("kept", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExceptionIsWrittenUnderItsMessage()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "fjord.log");

        using (var provider = new RollingFileLoggerProvider(path, LogLevel.Information))
        {
            provider.CreateLogger("Test")
                .LogError(new IOException("disk went away"), "failed {Operation}", "Copy 3 items");
        }

        var text = File.ReadAllText(path);
        Assert.Contains("failed Copy 3 items", text, StringComparison.Ordinal);
        Assert.Contains("disk went away", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFileRollsAndOldGenerationsAreDropped()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "fjord.log");

        // Three generations at a tiny cap, then far more records than fit in any of them.
        using (var provider = new RollingFileLoggerProvider(
                   path, LogLevel.Information, maxFiles: 3, maxBytes: 4096))
        {
            var logger = provider.CreateLogger("Test");
            for (var i = 0; i < 400; i++)
            {
                logger.LogInformation("record {Index} with enough text to fill the file quickly", i);
            }
        }

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(directory.Path, "fjord.1.log")));
        Assert.True(File.Exists(Path.Combine(directory.Path, "fjord.2.log")));

        // The point of a cap is that it holds. A fourth generation means unbounded growth.
        Assert.False(File.Exists(Path.Combine(directory.Path, "fjord.3.log")));
    }

    [Fact]
    public void TheNewestRecordsSurviveARoll()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "fjord.log");

        using (var provider = new RollingFileLoggerProvider(
                   path, LogLevel.Information, maxFiles: 2, maxBytes: 4096))
        {
            var logger = provider.CreateLogger("Test");
            for (var i = 0; i < 400; i++)
            {
                logger.LogInformation("record {Index} with enough text to fill the file quickly", i);
            }

            logger.LogInformation("the last thing that happened");
        }

        Assert.Contains("the last thing that happened", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnwritableLogDoesNotTakeTheAppDown()
    {
        // The whole point of the swallow: a read-only directory, a full disk or a locked
        // file must cost the user their log, never their session.
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "a-file-in-the-way", "fjord.log");
        File.WriteAllText(Path.Combine(directory.Path, "a-file-in-the-way"), "not a directory");

        using var provider = new RollingFileLoggerProvider(path, LogLevel.Information);
        var logger = provider.CreateLogger("Test");

        logger.LogInformation("first");
        logger.LogInformation("second");

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TheFileCanBeReadWhileItIsOpen()
    {
        // Tailing a log during a slow copy is the main way anyone uses one, and the default
        // sharing mode on Windows would make the file unreadable until Fjord exits.
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "fjord.log");

        using var provider = new RollingFileLoggerProvider(path, LogLevel.Information);
        provider.CreateLogger("Test").LogInformation("while running");

        using var reader = new StreamReader(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));

        Assert.Contains("while running", reader.ReadToEnd(), StringComparison.Ordinal);
    }
}
