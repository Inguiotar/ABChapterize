// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace ABChapterize.Cli;

/// <summary>
/// Takes the credentials back out of a command line before it is written down.
/// <para>
/// Every log this tool opens records the command that produced it, which is the single most useful
/// line in a file read days later - and the one line that would otherwise carry an
/// <c>--abs-key</c> or an <c>--abs-password</c> typed on the command line straight into a file
/// meant to be kept, and, in the case of a <c>--debug</c> log, attached to a bug report. The rest
/// of the codebase is careful about this already: <see cref="Abs.AbsConnection"/> overrides
/// <c>ToString</c> so a record's generated one cannot print the password, and the debug header
/// prints <c>Describe</c> rather than the connection. The command line was the way round all of
/// it.
/// </para>
/// <para>
/// A <c>--config</c> file needs nothing of the sort: the operating system's command line holds only
/// the path, so a credential kept in a file never reaches this. That is also why the manual
/// recommends the environment or a config file over typing a key - this makes the log side of that
/// advice true rather than merely good practice.
/// </para>
/// </summary>
public static class CommandLineRedaction
{
    /// <summary>The options whose value is a secret, exactly as the parser spells them.</summary>
    /// <remarks>
    /// <c>--abs-user</c> is deliberately not here: an account name is not a secret, and
    /// <see cref="Abs.AbsConnection.Describe"/> prints it in every log line that names the server.
    /// Neither option has a short form, so there is nothing else to catch.
    /// </remarks>
    private static readonly string[] SecretOptions = ["--abs-key", "--abs-password"];

    /// <summary>What a redacted value is replaced with.</summary>
    private const string Replacement = "***";

    /// <summary>
    /// Returns <paramref name="commandLine"/> with the value of every secret option replaced.
    /// </summary>
    /// <param name="commandLine">The raw command line, as
    /// <see cref="Environment.CommandLine"/> hands it over.</param>
    /// <returns>The same line with the secrets removed; unchanged when it holds none.</returns>
    /// <remarks>
    /// Splices into the original text rather than re-joining the tokens it found, so a line with
    /// nothing to redact comes back byte for byte and one that does keeps its own spacing and
    /// quoting everywhere else. Quoting is only interpreted far enough to find the token
    /// boundaries - what a value actually meant to the shell does not matter when the whole token
    /// is being thrown away.
    /// </remarks>
    public static string Redact(string commandLine)
    {
        var tokens = Tokenize(commandLine);
        var redacted = new List<(int Start, int Length)>();
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            var (start, length) = tokens[i];
            var text = commandLine.Substring(start, length);
            if (SecretOptions.Contains(text, StringComparer.Ordinal))
                redacted.Add(tokens[i + 1]);
        }
        if (redacted.Count == 0)
            return commandLine;

        var result = new System.Text.StringBuilder(commandLine.Length);
        var copied = 0;
        foreach (var (start, length) in redacted)
        {
            result.Append(commandLine, copied, start - copied).Append(Replacement);
            copied = start + length;
        }
        return result.Append(commandLine, copied, commandLine.Length - copied).ToString();
    }

    /// <summary>
    /// The spans of the whitespace-separated tokens of a command line, with double-quoted stretches
    /// held together.
    /// </summary>
    /// <param name="commandLine">The raw command line.</param>
    /// <returns>Each token's start and length within <paramref name="commandLine"/>.</returns>
    /// <remarks>
    /// Double quotes only, and no escape handling: this is not a shell and does not have to agree
    /// with one about what a token means. What it has to get right is where a token ends, so that
    /// the option before a secret is recognized and the whole of the secret is covered - and a
    /// quote in the middle of a value can only ever make a token longer, which errs towards
    /// redacting too much.
    /// </remarks>
    private static List<(int Start, int Length)> Tokenize(string commandLine)
    {
        var tokens = new List<(int Start, int Length)>();
        var start = -1;
        var quoted = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '"')
            {
                quoted = !quoted;
                if (start < 0)
                    start = i;
                continue;
            }
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (start >= 0)
                    tokens.Add((start, i - start));
                start = -1;
                continue;
            }
            if (start < 0)
                start = i;
        }
        if (start >= 0)
            tokens.Add((start, commandLine.Length - start));
        return tokens;
    }
}
