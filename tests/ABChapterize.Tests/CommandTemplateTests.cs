// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Errors;
using ABChapterize.Hooks;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="CommandTemplate"/> and <see cref="PathPlaceholder"/>, the
/// <c>--run-before</c> / <c>--run-after</c> placeholder expansion.
/// </summary>
/// <remarks>
/// The quoting is what these are really about, and it is the half that cannot be checked by
/// reading the code: an audiobook called "buch 1.m4b" reaching a shell as two arguments produces a
/// command that runs, succeeds, and does the wrong thing. Both platforms' rules are exercised here
/// whichever platform the suite runs on - see <see cref="CommandTemplate.Expand(string, bool)"/> -
/// because the one that is not the test machine's would otherwise be covered by nothing at all.
/// <para>
/// Paths are built with <see cref="Path.DirectorySeparatorChar"/> rather than written out, so the
/// expectations hold on either platform; only where the separator is incidental to what is being
/// checked (an escaped space, a quoted token) does a literal appear.
/// </para>
/// </remarks>
public class CommandTemplateTests
{
    /// <summary>A path under the platform's own root, so the element counting has something real to
    /// count and <see cref="Path.GetFullPath"/> leaves it alone.</summary>
    /// <param name="parts">The path elements below the root, the file name last.</param>
    private static string Rooted(params string[] parts)
        => Path.Combine(Path.GetPathRoot(Path.GetFullPath("."))!, Path.Combine(parts));

    /// <summary>Expands a template against a rooted path, under the rules that insert an ordinary
    /// path verbatim - so that a test about what a placeholder <i>is</i> does not also become a
    /// test about quoting. (POSIX escaping would backslash a Windows path's own separators.)</summary>
    /// <param name="template">The template to expand.</param>
    /// <param name="parts">The path elements below the root.</param>
    private static string Expand(string template, params string[] parts)
        => CommandTemplate.Parse(template, "--run-before").Expand(Rooted(parts), windows: true);

    [Fact]
    public void DollarZero_IsTheNameWithoutItsExtension()
        => Assert.Equal("buch", Expand("$0", "test", "buch.mp3"));

    [Fact]
    public void DollarOne_IsTheNameWithIt()
        => Assert.Equal("buch.mp3", Expand("$1", "test", "buch.mp3"));

    [Fact]
    public void DollarTwo_AddsOneParentFolder()
        => Assert.Equal(Path.Combine("test", "buch.mp3"), Expand("$2", "test", "buch.mp3"));

    [Fact]
    public void DollarN_StopsAtTheWholePath_RatherThanRunningOutOfElements()
    {
        var whole = Rooted("test", "buch.mp3");
        Assert.Equal(whole, Expand("$3", "test", "buch.mp3"));
        Assert.Equal(whole, Expand("$99", "test", "buch.mp3"));
    }

    [Fact]
    public void DollarMinusOne_IsTheFilesOwnFolder_WithATrailingSeparator()
        => Assert.Equal(Rooted("test") + Path.DirectorySeparatorChar, Expand("$-1", "test", "buch.mp3"));

    [Fact]
    public void DollarMinusN_StopsAtTheRoot_RatherThanRunningOutOfElements()
    {
        var root = Path.GetPathRoot(Path.GetFullPath("."))!;
        Assert.Equal(root, Expand("$-2", "test", "buch.mp3"));
        Assert.Equal(root, Expand("$-99", "test", "buch.mp3"));
    }

    [Fact]
    public void DollarMinusZero_IsRefusedOnTheCommandLine()
    {
        var error = Assert.Throws<CliError>(() => CommandTemplate.Parse("mv $-0 x", "--run-after"));
        Assert.Contains("$-0", error.Message);
        Assert.Contains("--run-after", error.Message);
    }

    [Fact]
    public void AnEmptyCommand_IsRefused()
    {
        Assert.Throws<CliError>(() => CommandTemplate.Parse("", "--run-before"));
        Assert.Throws<CliError>(() => CommandTemplate.Parse("   ", "--run-before"));
    }

    [Fact]
    public void ADoubledDollar_IsALiteralOne()
        => Assert.Equal("$1 buch.mp3", Expand("$$1 $1", "test", "buch.mp3"));

    [Fact]
    public void ADoubledDollar_AlsoShieldsAPlaceholderThatWouldNotParse()
        => Assert.Equal("$-0", CommandTemplate.Parse("$$-0", "--run-before").Expand(Rooted("a.mp3"), windows: true));

    [Fact]
    public void AShellVariable_IsNotAPlaceholder()
        => Assert.Equal("$HOME/buch.mp3", Expand("$HOME/$1", "test", "buch.mp3"));

    // ------------------------------------------------------------------ Windows quoting

    /// <summary>
    /// The worked example from the feature's own specification: the quotes have to take in the
    /// ".bak" the template appended, not just the value, or cmd ends the argument at the closing
    /// quote and hands "move" three arguments instead of two.
    /// </summary>
    [Fact]
    public void Windows_QuotesTheWholeToken_NotJustTheSubstitutedValue()
    {
        var template = CommandTemplate.Parse("move $1.bak $0.bak", "--run-after");
        Assert.Equal("move \"buch 1.m4b.bak\" \"buch 1.bak\"",
            template.Expand(Rooted("test", "buch 1.m4b"), windows: true));
    }

    [Fact]
    public void Windows_LeavesAnOrdinaryNameUnquoted()
    {
        var template = CommandTemplate.Parse("echo $1", "--run-after");
        Assert.Equal("echo buch.m4b", template.Expand(Rooted("test", "buch.m4b"), windows: true));
    }

    /// <summary>An ampersand is legal in a Windows file name and is cmd's command separator, so a
    /// book called "Rock &amp; Roll" would otherwise run "Roll.m4b" as a second command.</summary>
    [Fact]
    public void Windows_QuotesAName_ThatCmdWouldReadAsTwoCommands()
    {
        var template = CommandTemplate.Parse("echo $1", "--run-after");
        Assert.Equal("echo \"Rock & Roll.m4b\"", template.Expand(Rooted("Rock & Roll.m4b"), windows: true));
    }

    [Fact]
    public void Windows_LeavesATokenTheTemplateAlreadyQuotes_Alone()
    {
        var template = CommandTemplate.Parse("copy \"$1\" d:\\arch", "--run-after");
        Assert.Equal("copy \"buch 1.m4b\" d:\\arch", template.Expand(Rooted("buch 1.m4b"), windows: true));
    }

    // -------------------------------------------------------------------- POSIX quoting

    /// <summary>
    /// Escaped where it stands rather than quoted whole, which is what keeps the rest of the token
    /// doing its job: inside quotes the shell would hand "~/archive" to "mv" as a literal folder
    /// name that does not exist.
    /// </summary>
    [Fact]
    public void Posix_EscapesTheValueInPlace_AndLeavesTheTemplateExpanding()
    {
        var template = CommandTemplate.Parse("mv $1 ~/archive/$1", "--run-after");
        Assert.Equal("mv buch\\ 1.m4b ~/archive/buch\\ 1.m4b",
            template.Expand(Rooted("buch 1.m4b"), windows: false));
    }

    [Fact]
    public void Posix_EscapesEveryMetacharacter_NotOnlyTheSpace()
    {
        var template = CommandTemplate.Parse("echo $1", "--run-after");
        Assert.Equal("echo Rock\\ \\&\\ Roll\\ \\(live\\).m4b",
            template.Expand(Rooted("Rock & Roll (live).m4b"), windows: false));
    }

    [Fact]
    public void Posix_InsideSingleQuotes_EscapesTheQuoteAndNothingElse()
    {
        var template = CommandTemplate.Parse("echo '$1'", "--run-after");
        Assert.Equal("echo 'buch 1.m4b'", template.Expand(Rooted("buch 1.m4b"), windows: false));
        Assert.Equal("echo 'it'\\''s.m4b'", template.Expand(Rooted("it's.m4b"), windows: false));
    }

    [Fact]
    public void Posix_InsideDoubleQuotes_EscapesOnlyWhatTheShellStillReads()
    {
        var template = CommandTemplate.Parse("echo \"$1\"", "--run-after");
        Assert.Equal("echo \"buch 1.m4b\"", template.Expand(Rooted("buch 1.m4b"), windows: false));
        Assert.Equal("echo \"\\$HOME.m4b\"", template.Expand(Rooted("$HOME.m4b"), windows: false));
    }

    [Fact]
    public void Posix_SingleQuotesAName_ThatCannotBeBackslashEscaped()
    {
        // A backslash before a newline is a line continuation and would swallow it, so the whole
        // value goes into single quotes instead. Only reachable where a file name may hold one.
        var template = CommandTemplate.Parse("echo $1", "--run-after");
        Assert.Equal("echo 'two\nlines.m4b'", template.Expand(Rooted("two\nlines.m4b"), windows: false));
    }

    [Fact]
    public void ASingleQuote_IsNotAQuoteOnWindows_WhereCmdKnowsOnlyTheDoubleKind()
    {
        var template = CommandTemplate.Parse("echo it's $1", "--run-after");
        Assert.Equal("echo it's \"buch 1.m4b\"", template.Expand(Rooted("buch 1.m4b"), windows: true));
    }
}
