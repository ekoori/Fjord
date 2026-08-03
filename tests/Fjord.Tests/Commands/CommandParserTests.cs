using Fjord.Commands;
using Fjord.Core.Diagnostics;
using Fjord.Core.FileSystem;
using Fjord.Core.Panes;
using Fjord.Core.Selection;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Fjord.Tests.Commands;

/// <summary>Command bodies exercised by the parser tests.</summary>
public sealed class TestCommands
{
    public List<string> Calls { get; } = [];

    [Command("noop", Description = "Does nothing")]
    public void Noop() => Calls.Add("noop");

    [Command("echo", Aliases = ["e"], MaxSplit = 0, Description = "Echoes free text")]
    public void Echo(string text) => Calls.Add($"echo:{text}");

    [Command("open", Description = "Opens a path")]
    public void Open(string path, bool background = false) =>
        Calls.Add($"open:{path}:{background}");

    [Command("sort", Description = "Sorts the listing")]
    public void Sort(
        [CommandArgument(Flag = 'k', Choices = ["name", "size", "modified"])] string key = "name",
        bool reverse = false) =>
        Calls.Add($"sort:{key}:{reverse}");

    [Command("count-test")]
    public void CountTest([CommandArgument(Value = CommandValue.Count)] int count = 1) =>
        Calls.Add($"count:{count}");

    [Command("pane-test")]
    public void PaneTest([CommandArgument(Value = CommandValue.Pane)] IPane pane) =>
        Calls.Add($"pane:{pane.Directory}");

    [Command("range-test")]
    public void RangeTest(
        [CommandArgument(Value = CommandValue.Pane)] IPane pane,
        [CommandArgument(Value = CommandValue.Range)] EntryRange range,
        [CommandArgument(Value = CommandValue.Register)] char register) =>
        Calls.Add($"range:{range.Start}-{range.End}:{register}");

    [Command("say")]
    public void Say(
        [CommandArgument(Value = CommandValue.Messages)] IMessageSink messages, string text) =>
        messages.Info(text);

    [Command("collect")]
    public void Collect(params string[] items) => Calls.Add($"collect:{string.Join('|', items)}");

    [Command("level", Description = "Enum argument")]
    public void Level(SortKey key) => Calls.Add($"level:{key}");

    [Command("boom")]
    public void Boom() => throw new InvalidOperationException("something broke");

    [Command("hidden-thing", Hidden = true)]
    public void HiddenThing() => Calls.Add("hidden");

    [Command("run", NoCommandSplit = true, MaxSplit = 0)]
    public void Run(string line) => Calls.Add($"run:{line}");
}

public class CommandParserTests
{
    private static (CommandParser Parser, TestCommands Target, CommandRegistry Registry) Setup()
    {
        var target = new TestCommands();
        var registry = new CommandRegistry();
        registry.Register(target);
        return (new CommandParser(registry), target, registry);
    }

    [Fact]
    public void RunsASimpleCommand()
    {
        var (parser, target, _) = Setup();

        parser.Execute("noop");

        Assert.Equal(["noop"], target.Calls);
    }

    [Fact]
    public void LeadingColonIsOptional()
    {
        var (parser, target, _) = Setup();

        parser.Execute(":noop");

        Assert.Equal(["noop"], target.Calls);
    }

    [Fact]
    public void AliasesResolve()
    {
        var (parser, target, _) = Setup();

        parser.Execute("e hello");

        Assert.Equal(["echo:hello"], target.Calls);
    }

    [Fact]
    public void UnknownCommandIsReported()
    {
        var (parser, _, _) = Setup();

        var ex = Assert.Throws<CommandException>(() => parser.Execute("nope"));
        Assert.Contains("no such command", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotedArgumentsKeepTheirSpaces()
    {
        // The everyday case for a file manager: filenames with spaces.
        var (parser, target, _) = Setup();

        parser.Execute("open \"My Documents/report final.pdf\"");

        Assert.Equal(["open:My Documents/report final.pdf:False"], target.Calls);
    }

    [Fact]
    public void BackslashesSurviveInsideDoubleQuotes()
    {
        var (parser, target, _) = Setup();

        parser.Execute(@"open ""C:\Users\igor\a b.txt""");

        Assert.Equal([@"open:C:\Users\igor\a b.txt:False"], target.Calls);
    }

    [Fact]
    public void UnbalancedQuotesAreRefused()
    {
        // Silently truncating the argument would produce an error describing the wrong
        // problem entirely.
        var (parser, _, _) = Setup();

        Assert.Throws<CommandException>(() => parser.Execute("open \"unterminated"));
    }

    [Fact]
    public void BooleanFlagsWorkLongAndShort()
    {
        var (parser, target, _) = Setup();

        parser.Execute("open a --background");
        parser.Execute("open b -b");

        Assert.Equal(["open:a:True", "open:b:True"], target.Calls);
    }

    [Fact]
    public void ValueTakingFlagsConsumeTheNextToken()
    {
        var (parser, target, _) = Setup();

        parser.Execute("sort -k size");
        parser.Execute("sort --key modified --reverse");

        Assert.Equal(["sort:size:False", "sort:modified:True"], target.Calls);
    }

    [Fact]
    public void InvalidChoiceIsRejectedWithTheAlternatives()
    {
        var (parser, _, _) = Setup();

        var ex = Assert.Throws<CommandException>(() => parser.Execute("sort -k nonsense"));
        Assert.Contains("expected one of", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DoubleDashEndsFlagParsing()
    {
        // A filename beginning with a dash is the reason this exists.
        var (parser, target, _) = Setup();

        parser.Execute("open -- -weird-name.txt");

        Assert.Equal(["open:-weird-name.txt:False"], target.Calls);
    }

    [Fact]
    public void NegativeNumbersAreNotMistakenForFlags()
    {
        var (parser, target, _) = Setup();

        parser.Execute("open -3");

        Assert.Equal(["open:-3:False"], target.Calls);
    }

    [Fact]
    public void UnknownFlagIsReported()
    {
        var (parser, _, _) = Setup();

        Assert.Throws<CommandException>(() => parser.Execute("open a --nope"));
    }

    [Fact]
    public void MissingRequiredArgumentReportsUsage()
    {
        var (parser, _, _) = Setup();

        var ex = Assert.Throws<CommandException>(() => parser.Execute("open"));
        Assert.Contains("Usage: :open", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TooManyArgumentsReportsUsage()
    {
        var (parser, _, _) = Setup();

        Assert.Throws<CommandException>(() => parser.Execute("noop extra"));
    }

    [Fact]
    public void MaxSplitZeroKeepsFreeTextIntactIncludingRunsOfSpaces()
    {
        // Collapsing the spacing would change what the user typed.
        var (parser, target, _) = Setup();

        parser.Execute("echo hello   there  world");

        Assert.Equal(["echo:hello   there  world"], target.Calls);
    }

    [Fact]
    public void DoubleSemicolonSplitsIntoSeveralCommands()
    {
        var (parser, target, _) = Setup();

        var count = parser.Execute("noop ;; open a ;; noop");

        Assert.Equal(3, count);
        Assert.Equal(["noop", "open:a:False", "noop"], target.Calls);
    }

    [Fact]
    public void NoCommandSplitKeepsThePayloadIntact()
    {
        // A command whose argument is itself a command must not be torn apart.
        var (parser, target, _) = Setup();

        parser.Execute("run noop ;; open a");

        Assert.Equal(["run:noop ;; open a"], target.Calls);
    }

    [Fact]
    public void SeparatorsInsideQuotesAreNotSplitOn()
    {
        // One command, not two: a filename containing ";;" must survive.
        var (parser, target, _) = Setup();

        var count = parser.Execute("open \"a ;; b\"");

        Assert.Equal(1, count);
        Assert.Equal(["open:a ;; b:False"], target.Calls);
    }

    [Fact]
    public void MaxSplitFreeTextIsTakenRawSoQuotesAreLiteral()
    {
        // MaxSplit bypasses the lexer for the remainder on purpose, to preserve spacing.
        // The consequence is that quotes in free text are characters, not syntax — which
        // is what a user typing a search pattern expects.
        var (parser, target, _) = Setup();

        parser.Execute("echo \"quoted\" and bare");

        Assert.Equal(["echo:\"quoted\" and bare"], target.Calls);
    }

    [Theory]
    // The reason the lexer diverges from QuteSharp: these are the arguments a file manager
    // actually receives, and treating every backslash as an escape silently mangles them.
    [InlineData(@"""C:\Users\igor\a b.txt""", @"C:\Users\igor\a b.txt")]
    [InlineData(@"""C:\Program Files\App""", @"C:\Program Files\App")]
    [InlineData(@"""\\server\share\file.txt""", @"\\server\share\file.txt")]
    public void WindowsPathsSurviveQuoting(string typed, string expected)
    {
        var (parser, target, _) = Setup();

        parser.Execute("open " + typed);

        Assert.Equal([$"open:{expected}:False"], target.Calls);
    }

    [Fact]
    public void ADoubleQuotedStringCannotEndWithABackslash()
    {
        // Inherent to using \" as an escape — bash has the same ambiguity, since the
        // trailing backslash escapes the closing quote and the string never terminates.
        // Refusing it is right; SingleQuotesAreFullyLiteral covers the way to write it.
        var (parser, _, _) = Setup();

        Assert.Throws<CommandException>(() => parser.Execute(@"open ""ends with backslash\"""));
        Assert.Equal([@"ends with backslash\"], CommandLexer.Split(@"'ends with backslash\'"));
    }

    [Theory]
    // \" is the one escape that remains, so a literal double quote can still be passed.
    [InlineData(@"""say \""hi\""""", @"say ""hi""")]
    // \\ is NOT an escape: two backslashes are two backslashes, which is what makes UNC
    // paths work.
    [InlineData(@"""back\\slash""", @"back\\slash")]
    public void OnlyTheDoubleQuoteEscapeRemains(string typed, string expected)
    {
        var (parser, target, _) = Setup();

        parser.Execute("open " + typed);

        Assert.Equal([$"open:{expected}:False"], target.Calls);
    }

    [Theory]
    // Single quotes escape nothing at all, so they carry anything without a single quote.
    [InlineData(@"'C:\Users\igor\a b.txt'", @"C:\Users\igor\a b.txt")]
    [InlineData(@"'\\server\share'", @"\\server\share")]
    [InlineData(@"'trailing\'", @"trailing\")]
    [InlineData(@"'say ""hi""'", @"say ""hi""")]
    public void SingleQuotesAreFullyLiteral(string typed, string expected)
    {
        var (parser, target, _) = Setup();

        parser.Execute("open " + typed);

        Assert.Equal([$"open:{expected}:False"], target.Calls);
    }

    [Theory]
    // Quote has to handle every name a real filesystem can produce.
    [InlineData(@"C:\Users\igor\a b.txt")]
    [InlineData(@"\\server\share\file.txt")]
    [InlineData(@"trailing\")]
    [InlineData("don't")]
    [InlineData("say \"hi\"")]
    [InlineData("both ' and \" and \\")]
    [InlineData("")]
    [InlineData("   ")]
    public void QuoteRoundTripsAwkwardNames(string name)
    {
        Assert.Equal([name], CommandLexer.Split(CommandLexer.Quote(name)));
    }

    [Fact]
    public void VariadicArgumentsCollectTheRemainder()
    {
        var (parser, target, _) = Setup();

        parser.Execute("collect one two three");

        Assert.Equal(["collect:one|two|three"], target.Calls);
    }

    [Fact]
    public void EnumArgumentsAcceptHyphenatedNames()
    {
        var (parser, target, _) = Setup();

        parser.Execute("level modified");

        Assert.Equal(["level:Modified"], target.Calls);
    }

    [Fact]
    public void CountIsSuppliedByTheRuntime()
    {
        var (parser, target, _) = Setup();

        parser.Execute("count-test", new CommandContext { Count = 7 });

        Assert.Equal(["count:7"], target.Calls);
    }

    [Fact]
    public void AbsentCountFallsBackToTheParameterDefault()
    {
        // Commands taking a count are written to work without one.
        var (parser, target, _) = Setup();

        parser.Execute("count-test");

        Assert.Equal(["count:1"], target.Calls);
    }

    [Fact]
    public void PaneIsSuppliedByTheRuntime()
    {
        var (parser, target, _) = Setup();

        parser.Execute("pane-test", new CommandContext { Pane = new FakePane("/home/u") });

        Assert.Equal(["pane:/home/u"], target.Calls);
    }

    [Fact]
    public void RangeAndRegisterAreSuppliedByTheRuntime()
    {
        var (parser, target, _) = Setup();

        parser.Execute("range-test", new CommandContext
        {
            Pane = new FakePane(),
            Range = new EntryRange(2, 5),
            Register = 'a',
        });

        Assert.Equal(["range:2-5:a"], target.Calls);
    }

    [Fact]
    public void MissingRuntimeContextFailsClearlyRatherThanNullReferencing()
    {
        // A command declaring a range but dispatched without one means the caller built
        // the context wrongly; the error should say so, not surface as an NRE from inside
        // the command body.
        var (parser, _, _) = Setup();

        var ex = Assert.Throws<CommandException>(() =>
            parser.Execute("range-test", new CommandContext { Pane = new FakePane() }));

        Assert.Contains("needs a range", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MessagesAreSuppliedByTheRuntime()
    {
        var (parser, _, _) = Setup();
        var messages = new RecordingMessageSink();

        parser.Execute("say hello", new CommandContext { Messages = messages });

        Assert.Equal("hello", messages.Last);
    }

    [Fact]
    public void ExceptionsFromCommandBodiesAreUnwrapped()
    {
        // Reflection wraps them in TargetInvocationException, which would show the user
        // that instead of what actually went wrong.
        var (parser, _, _) = Setup();

        var ex = Assert.Throws<CommandException>(() => parser.Execute("boom"));
        Assert.Contains("something broke", ex.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public void HiddenCommandsRunButAreNotListed()
    {
        var (parser, target, registry) = Setup();

        parser.Execute("hidden-thing");

        Assert.Equal(["hidden"], target.Calls);
        Assert.DoesNotContain(registry.Commands, c => c.Name == "hidden-thing");
        Assert.Contains(registry.All, c => c.Name == "hidden-thing");
    }

    [Fact]
    public void DuplicateRegistrationIsRejected()
    {
        var registry = new CommandRegistry();
        registry.Register(new TestCommands());

        Assert.Throws<CommandException>(() => registry.Register(new TestCommands()));
    }

    [Fact]
    public void EmptyInputRunsNothing()
    {
        var (parser, _, _) = Setup();

        Assert.Equal(0, parser.Execute("   "));
        Assert.Equal(0, parser.Execute(":"));
    }

    [Fact]
    public void UsageIsDerivedFromTheSignature()
    {
        var (_, _, registry) = Setup();

        Assert.Equal(":open [--background] <path>", registry.Find("open")!.Usage);
    }

    // ---- Properties

    /// <summary>Tokens covering the characters that make lexing interesting.</summary>
    private static Arbitrary<string[]> Tokens() =>
        Gen.Elements("a", "b c", "with'quote", "with\"double", @"back\slash", "-flag", "--", ";;", " ", "x")
            .NonEmptyListOf()
            .Select(list => list.ToArray())
            .ToArbitrary();

    [Property]
    public Property QuotingRoundTripsThroughTheLexer() =>
        Prop.ForAll(Tokens(), tokens =>
        {
            // Fjord builds command lines from real filenames, so the quote/lex pair has to
            // be exact for names containing spaces, quotes and backslashes.
            var line = string.Join(' ', tokens.Select(CommandLexer.Quote));
            return CommandLexer.Split(line).SequenceEqual(tokens);
        });

    [Property]
    public Property ParsingArbitraryInputThrowsNothingWorseThanCommandException() =>
        Prop.ForAll(ArbMap.Default.ArbFor<string>(), input =>
        {
            // A NullReferenceException from the command line is a crash. Anything the user
            // can type must fail as a CommandException or not at all.
            var (parser, _, _) = Setup();

            try
            {
                parser.Parse(input ?? "");
                return true;
            }
            catch (CommandException)
            {
                return true;
            }
        });
}
