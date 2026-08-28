// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Cli;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="CommandLineRedaction"/>, which keeps an <c>--abs-key</c> or an
/// <c>--abs-password</c> typed on the command line out of the header every log file opens with.
/// </summary>
public sealed class CommandLineRedactionTests
{
    /// <summary>A line with no credential in it comes back exactly as it went in, spacing and
    /// quoting included - the overwhelmingly common case, and the one where any rewriting at all
    /// would be a regression.</summary>
    [Theory]
    [InlineData("abchapterize --recurse  \"D:\\Audio books\"")]
    [InlineData("abchapterize")]
    [InlineData("")]
    public void OrdinaryCommandLine_IsUnchanged(string commandLine)
        => Assert.Equal(commandLine, CommandLineRedaction.Redact(commandLine));

    /// <summary>Both secret options lose their value, wherever they sit on the line.</summary>
    [Theory]
    [InlineData("abchapterize --abs-key abc123 --abs library:X",
                "abchapterize --abs-key *** --abs library:X")]
    [InlineData("abchapterize --abs-user root --abs-password hunter2 --abs all",
                "abchapterize --abs-user root --abs-password *** --abs all")]
    [InlineData("abchapterize --abs-key abc123",
                "abchapterize --abs-key ***")]
    public void SecretOptions_LoseTheirValue(string commandLine, string expected)
        => Assert.Equal(expected, CommandLineRedaction.Redact(commandLine));

    /// <summary>A quoted secret loses its quotes along with its value: the whole token goes, so
    /// nothing of it can be left behind on either side.</summary>
    [Fact]
    public void QuotedSecret_IsRemovedWholesale()
        => Assert.Equal(
            "abchapterize --abs-password *** --abs-url books.lan",
            CommandLineRedaction.Redact("abchapterize --abs-password \"pass word\" --abs-url books.lan"));

    /// <summary>The account name is not a secret and stays: it is printed in every log line that
    /// names the server, so redacting it here would only make the two disagree.</summary>
    [Fact]
    public void UserName_IsKept()
        => Assert.Contains("--abs-user root", CommandLineRedaction.Redact("abchapterize --abs-user root x"));

    /// <summary>A secret option with nothing after it - a command line the parser is about to
    /// reject - redacts nothing and does not fall off the end.</summary>
    [Fact]
    public void TrailingSecretOption_WithNoValue_IsHarmless()
        => Assert.Equal("abchapterize --abs-key", CommandLineRedaction.Redact("abchapterize --abs-key"));

    /// <summary>Everything outside the redacted token is preserved byte for byte, run of spaces
    /// and all - the header is meant to be the command that was typed.</summary>
    [Fact]
    public void SpacingAroundTheSecret_IsPreserved()
        => Assert.Equal(
            "abchapterize   --abs-key   ***   --abs   all",
            CommandLineRedaction.Redact("abchapterize   --abs-key   secret   --abs   all"));

    /// <summary>Two secrets on one line are both removed, which the splicing has to get right
    /// because each replacement moves everything after it.</summary>
    [Fact]
    public void TwoSecrets_AreBothRemoved()
        => Assert.Equal(
            "abchapterize --abs-key *** --abs-password *** --abs all",
            CommandLineRedaction.Redact("abchapterize --abs-key k1 --abs-password p2 --abs all"));
}
