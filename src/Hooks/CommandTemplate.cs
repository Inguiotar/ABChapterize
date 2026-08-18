// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Text;
using ABChapterize.Errors;

namespace ABChapterize.Hooks;

/// <summary>
/// A <c>--run-before</c> / <c>--run-after</c> command line with its "$..." placeholders still in it,
/// ready to be resolved against one file at a time (see <see cref="Expand(string)"/>).
/// </summary>
/// <remarks>
/// The result is handed to a shell, so every value substituted into it has to survive that shell's
/// own parsing intact - an audiobook called "Rock &amp; Roll 2.m4b" must not turn into three
/// arguments and a command separator. Which is why this class does its own tokenizing and quoting
/// rather than plain string replacement, and why the two platforms are quoted differently:
/// <list type="bullet">
/// <item>Windows has no in-place escape a value can rely on, so the whole <i>token</i> a placeholder
/// sits in is wrapped in double quotes - <c>move $1.bak $0.bak</c> becomes
/// <c>move "buch 1.m4b.bak" "buch 1.bak"</c>, the ".bak" inside the quotes. Wrapping only the
/// substituted value would leave cmd splitting the token at the closing quote. A backslash run
/// ending such a token is doubled first, or it would escape that closing quote away again - see
/// <see cref="DoubleTrailingBackslashes"/>.</item>
/// <item>POSIX shells take a backslash before any metacharacter, so the value alone is escaped
/// where it stands. That keeps the rest of the token doing what it says: <c>~/archive/$1</c> still
/// expands the tilde, which it would not inside quotes.</item>
/// </list>
/// Nothing is quoted that does not need it, so <c>echo $0</c> stays <c>echo buch</c> for an ordinary
/// file name rather than acquiring quotes the command would print.
/// <para>
/// One known gap, Windows only and unfixable from here: cmd expands <c>%NAME%</c> inside double
/// quotes as readily as outside, and offers no escape for a percent sign on the command line. A file
/// whose name happens to contain an existing environment variable's name in percent signs therefore
/// reaches the command with that expanded. Documented in the manual rather than worked around, there
/// being nothing to work around it with short of not using a shell at all.
/// </para>
/// </remarks>
public sealed class CommandTemplate
{
    /// <summary>The command line exactly as it was given.</summary>
    public string Raw { get; }

    /// <summary>Quote state the scanner is in, which decides how a value is escaped.</summary>
    private enum Quote
    {
        /// <summary>Outside any quotes: the shell would split and interpret here.</summary>
        None,

        /// <summary>Inside '...', where a POSIX shell interprets nothing but the closing quote.</summary>
        Single,

        /// <summary>Inside "...", where a POSIX shell still expands and neither shell splits.</summary>
        Double,
    }

    private CommandTemplate(string raw) => Raw = raw;

    /// <summary>
    /// Validates a command line template and its placeholders. Done once at parse time so that a
    /// typo is a command line error rather than something a batch walks into on file 200 - and so
    /// that <see cref="Expand(string)"/> has nothing left to fail at.
    /// </summary>
    /// <param name="template">The raw option value.</param>
    /// <param name="optionName">The option it was given for, for error messages.</param>
    /// <exception cref="CliError">Thrown for an empty command or a malformed placeholder.</exception>
    public static CommandTemplate Parse(string template, string optionName)
    {
        if (string.IsNullOrWhiteSpace(template))
            throw new CliError($"{optionName} needs a command to run.");
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '$')
                continue;
            // Past both characters of a "$$" escape, so that the "$-0" of "$$-0" is the literal it
            // was written as rather than a placeholder to complain about.
            if (IsEscapedDollar(template, i))
            {
                i++;
                continue;
            }
            if (!PathPlaceholder.TryParse(template, i + 1, out var placeholder, out var length))
                continue;
            if (!placeholder.IsValid)
                throw new CliError(
                    $"Invalid placeholder \"{placeholder}\" in {optionName}: \"$-n\" names the folder " +
                    "n path elements above the file, so n must be at least 1. Use \"$99\" for the " +
                    "file's whole path.");
            i += length;
        }
        return new CommandTemplate(template);
    }

    /// <summary>
    /// Resolves every placeholder against one file and returns the command line to hand to the
    /// shell.
    /// </summary>
    /// <param name="filePath">The file the hook is running for; resolved to an absolute path first,
    /// since that is what the placeholders count elements of.</param>
    public string Expand(string filePath) => Expand(filePath, OperatingSystem.IsWindows());

    /// <summary>
    /// The real expansion, with the quoting rules to apply named rather than sensed. Internal so
    /// that both platforms' rules can be tested on either platform: they are the whole point of
    /// this class, and testing only the one the test machine happens to run would leave the other
    /// covered by nothing.
    /// </summary>
    /// <param name="filePath">The file the hook is running for.</param>
    /// <param name="windows">Whether to quote for cmd rather than for a POSIX shell.</param>
    internal string Expand(string filePath, bool windows)
    {
        var full = Path.GetFullPath(filePath);
        var result = new StringBuilder(Raw.Length);
        var token = new StringBuilder();
        var quote = Quote.None;
        // Whether the template quotes anything inside the current token. It is then doing the
        // user's own quoting, and a second pair around it would nest rather than help.
        var tokenIsQuoted = false;
        // Whether a value substituted into the current token needs Windows' whole-token quoting.
        var tokenNeedsQuotes = false;

        // Doubles the run of backslashes the token ends in, which is what Windows' argument
        // convention requires immediately before a closing quote - see DoubleTrailingBackslashes'
        // own remarks for the measurement. Only ever reached on Windows: tokenNeedsQuotes is set
        // nowhere else, and the template's own closing quote guards on the flag explicitly.
        void FlushToken()
        {
            if (tokenNeedsQuotes && !tokenIsQuoted)
            {
                DoubleTrailingBackslashes(token);
                result.Append('"').Append(token).Append('"');
            }
            else
                result.Append(token);
            token.Clear();
            tokenIsQuoted = false;
            tokenNeedsQuotes = false;
        }

        for (var i = 0; i < Raw.Length; i++)
        {
            var c = Raw[i];
            if (quote == Quote.None && char.IsWhiteSpace(c))
            {
                FlushToken();
                result.Append(c);
                continue;
            }
            if (c == '"' && quote != Quote.Single)
            {
                // The template's own closing quote needs the same treatment as the one FlushToken
                // adds: a value substituted just inside it went in raw, so its trailing separator
                // would eat this quote. On a POSIX shell the value's backslashes were already
                // doubled by EscapeForShell.
                if (windows && quote == Quote.Double)
                    DoubleTrailingBackslashes(token);
                quote = quote == Quote.Double ? Quote.None : Quote.Double;
                tokenIsQuoted = true;
                token.Append(c);
                continue;
            }
            // A single quote quotes nothing on Windows, where cmd knows only the double kind.
            if (c == '\'' && !windows && quote != Quote.Double)
            {
                quote = quote == Quote.Single ? Quote.None : Quote.Single;
                tokenIsQuoted = true;
                token.Append(c);
                continue;
            }
            if (c == '$' && IsEscapedDollar(Raw, i))
            {
                token.Append('$');
                i++;
                continue;
            }
            if (c == '$' && PathPlaceholder.TryParse(Raw, i + 1, out var placeholder, out var length))
            {
                var value = placeholder.Resolve(full);
                token.Append(windows ? value : EscapeForShell(value, quote));
                if (windows && quote == Quote.None && NeedsWindowsQuotes(value))
                    tokenNeedsQuotes = true;
                i += length;
                continue;
            }
            token.Append(c);
        }
        FlushToken();
        return result.ToString();
    }

    /// <summary>Whether the "$" at <paramref name="index"/> opens a "$$" escape, which is how a
    /// command line asks for a literal "$" where a placeholder would otherwise be read.</summary>
    /// <param name="text">The template.</param>
    /// <param name="index">Index of the "$".</param>
    private static bool IsEscapedDollar(string text, int index)
        => index + 1 < text.Length && text[index + 1] == '$';

    /// <summary>
    /// Characters that make cmd read one token as several, or as something other than a word.
    /// Deliberately wider than cmd's own separator set: quoting a value that did not strictly need
    /// it costs nothing, while missing one splits a file name in half.
    /// </summary>
    private const string WindowsSpecials = "&|<>^()[]{}=;,!'`\"%";

    /// <summary>Whether a substituted value has to take its whole token into quotes.</summary>
    /// <param name="value">The resolved placeholder value.</param>
    private static bool NeedsWindowsQuotes(string value)
        => value.Any(c => char.IsWhiteSpace(c) || WindowsSpecials.Contains(c));

    /// <summary>
    /// Doubles the run of backslashes a token ends in, ready for a closing double quote to be
    /// appended.
    /// </summary>
    /// <remarks>
    /// Windows' argument convention - the one the C runtime, and therefore every program started
    /// from a command line, uses to split that line back into arguments - reads a backslash before
    /// a quote as escaping the quote. So a token ending in a separator loses its closing quote and
    /// swallows whatever follows. Measured 2026-08-18 with <c>cmd /d /s /c</c> exactly as
    /// <see cref="HookRunner"/> starts it, argument list printed by the started program:
    /// <c>prog "D:\My Books\" second</c> arrives as the single argument
    /// <c>D:\My Books" second</c>, while <c>prog "D:\My Books\\" second</c> arrives as
    /// <c>D:\My Books\</c> and <c>second</c>.
    /// <para>
    /// Not an exotic case: <see cref="PathPlaceholder"/> ends every <c>$-n</c> value with a
    /// separator by construction, so any folder path holding a space - which is what puts the
    /// quotes there in the first place - walks into it. cmd's own built-ins are unharmed by the
    /// doubling, since Windows' path APIs collapse a duplicated separator (verified: <c>if exist
    /// "C:\Windows\\"</c> answers the same as with one).
    /// </para>
    /// </remarks>
    /// <param name="token">The token being built; modified in place.</param>
    private static void DoubleTrailingBackslashes(StringBuilder token)
    {
        var run = 0;
        while (run < token.Length && token[token.Length - 1 - run] == '\\')
            run++;
        token.Append('\\', run);
    }

    /// <summary>Characters a POSIX shell acts on, which a substituted value must therefore not hand
    /// it unescaped.</summary>
    private const string ShellSpecials = "|&;<>()$`\\\"'*?[]#~=%{}!";

    /// <summary>Characters a POSIX shell still acts on inside double quotes.</summary>
    private const string DoubleQuotedSpecials = "$`\"\\";

    /// <summary>
    /// Escapes a value for a POSIX shell in the quote context the template puts it in.
    /// </summary>
    /// <param name="value">The resolved placeholder value.</param>
    /// <param name="quote">The quoting the template already put around it.</param>
    private static string EscapeForShell(string value, Quote quote)
    {
        // Inside '...' nothing is special but the quote itself, which is closed, escaped and
        // reopened - the standard trick, and the only one available there. The template supplies
        // the surrounding quotes, so this adds none of its own.
        if (quote == Quote.Single)
            return value.Replace("'", "'\\''");
        // A line break cannot be backslash-escaped: "\<newline>" is a line continuation and would
        // swallow it. Rare, and impossible on Windows, but a file name may legally hold one - so
        // such a value goes into single quotes whole instead of being escaped in place.
        if (quote == Quote.None && (value.Contains('\n') || value.Contains('\r')))
            return Quoted(value);
        var escaped = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (quote == Quote.Double
                    ? DoubleQuotedSpecials.Contains(c)
                    : char.IsWhiteSpace(c) || ShellSpecials.Contains(c))
                escaped.Append('\\');
            escaped.Append(c);
        }
        return escaped.ToString();
    }

    /// <summary>Wraps a value in single quotes, the one POSIX quoting that covers every character
    /// there is.</summary>
    /// <param name="value">The value to quote.</param>
    private static string Quoted(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
