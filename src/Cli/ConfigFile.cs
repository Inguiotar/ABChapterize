// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Errors;

namespace ABChapterize.Cli;

/// <summary>
/// <c>--config</c>: options read from a file, one option per line, exactly as they would have been
/// typed. Expanded into the argument array before <see cref="CliOptions.Parse"/> sees it, so every
/// option behaves identically whether it came from a file or from the shell - there is no second
/// parser to keep in step, and a new option needs no work here at all.
/// </summary>
internal static class ConfigFile
{
    /// <summary>The option that names a config file, spelled once so the parser, the expansion and
    /// the error messages cannot drift apart.</summary>
    internal const string Option = "--config";

    /// <summary>
    /// Replaces every <c>--config &lt;path&gt;</c> in <paramref name="args"/> with the options that
    /// file holds, and returns the result.
    /// </summary>
    /// <remarks>
    /// The expanded options are moved to the <em>front</em> rather than left where the option stood,
    /// which is what makes "an option typed on the command line always wins" true regardless of
    /// where <c>--config</c> sat. In-place expansion would instead have let a config file override
    /// whatever preceded it, so the same two files and the same intent would behave differently
    /// depending on argument order.
    /// <para>
    /// A repeatable option (<c>--custom</c>, <c>--chapter-phrase</c>) accumulates instead of being
    /// overridden, here as everywhere else: repeating it is how the option is meant to be used, so a
    /// file's entries and the command line's are both applied, the file's first.
    /// </para>
    /// </remarks>
    /// <param name="args">The raw command line.</param>
    /// <exception cref="CliError">Thrown for a missing path, an unreadable file, a line that is not
    /// an option, or a cycle of files including one another.</exception>
    internal static string[] Expand(string[] args)
    {
        if (!args.Contains(Option, StringComparer.Ordinal))
            return args;
        // A request for the usage text is answered whatever else is on the command line, the way
        // --list-gpus is: someone reaching for --help because their --config is not working should
        // get the help rather than the error they already know about.
        if (args.Any(a => a is "--help" or "-?" or "/?"))
            return args;

        var fromFiles = new List<string>();
        // Everything as given, --config and its path included. The option is left in rather than
        // consumed here so that CliOptions.Parse still sees it and applies the "options precede the
        // file arguments" rule to it like any other; it is a no-op there, this having done the work.
        // Deciding that here instead would mean telling an option's argument from a file argument,
        // which needs the arity of every option - a second parser, and the one thing this design is
        // built to avoid.
        var rest = new List<string>();
        // Full paths of the files already expanded, so a cycle is reported rather than followed.
        // A set of files that cannot repeat also cannot nest for ever, so this is the whole of the
        // recursion guard - no separate depth cap is needed.
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            rest.Add(args[i]);
            if (!string.Equals(args[i], Option, StringComparison.Ordinal))
                continue;
            if (i + 1 >= args.Length)
                throw new CliError($"Option {Option} requires a parameter.");
            rest.Add(args[++i]);
            ExpandInto(args[i], fromFiles, visited, Option);
        }

        return [.. fromFiles, .. rest];
    }

    /// <summary>Reads one config file's options into <paramref name="into"/>, following any
    /// <c>--config</c> of its own.</summary>
    /// <param name="path">Path of the file to read, as written by whoever named it.</param>
    /// <param name="into">Accumulator for the expanded option tokens.</param>
    /// <param name="visited">Full paths already expanded; see <see cref="Expand"/>.</param>
    /// <param name="where">How to name the including context in an error message.</param>
    private static void ExpandInto(string path, List<string> into, HashSet<string> visited, string where)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CliError($"Cannot read {where} \"{path}\": {ex.Message}");
        }
        if (!visited.Add(full))
            throw new CliError(
                $"Config file \"{path}\" includes itself, directly or through another config file.");

        string[] lines;
        try
        {
            lines = File.ReadAllLines(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            // Same wrapping as CustomMappingParser.ParseFile, for the same reason: a path shape the
            // platform refuses is a command line error, not an unexpected exception.
            throw new CliError($"Cannot read {where} \"{path}\": {ex.Message}");
        }

        for (var n = 0; n < lines.Length; n++)
        {
            if (LineTokens(lines[n], $"config file \"{path}\", line {n + 1}") is not { } tokens)
                continue;
            // A nested --config is resolved relative to the file naming it, not to the working
            // directory: a set of config files that include one another is normally kept together,
            // and would otherwise only work when the tool is run from one particular folder.
            if (string.Equals(tokens[0], Option, StringComparison.Ordinal))
            {
                if (tokens.Count < 2)
                    throw new CliError(
                        $"Option {Option} requires a parameter (config file \"{path}\", line {n + 1}).");
                var dir = Path.GetDirectoryName(full);
                var nested = dir is null ? tokens[1] : Path.Combine(dir, tokens[1]);
                ExpandInto(nested, into, visited, $"config file \"{path}\", line {n + 1}");
                continue;
            }
            into.AddRange(tokens);
        }
    }

    /// <summary>
    /// One config file line as argument tokens, or null for a line that carries nothing (blank, or
    /// a <c>#</c> comment - the same two skips <c>--custom-file</c> makes).
    /// </summary>
    /// <remarks>
    /// A line is split once, at its first run of whitespace: everything after that is the option's
    /// argument, verbatim. That is what lets a <c>--chapter-phrase</c> regexp or a <c>--custom</c>
    /// mapping be written with the spaces it needs and no quoting grammar to learn - one option per
    /// line means there is nothing for a quote to disambiguate.
    /// <para>
    /// One layer of surrounding double quotes is stripped anyway, because the argument people paste
    /// here is the one they last typed at a shell, and it usually still has the shell's quotes on
    /// it. That is also how an intentionally empty argument is written (<c>""</c>).
    /// </para>
    /// <para>
    /// An option whose argument is simply missing consumes the next line's option instead, exactly
    /// as it would consume the next argument on a command line. Left to behave identically rather
    /// than guarded here: the file <em>is</em> the command line, and a config file that diagnosed
    /// its own arity would be a second parser with its own idea of which options take a value.
    /// </para>
    /// </remarks>
    /// <param name="line">The raw line.</param>
    /// <param name="where">How to name this line in an error message.</param>
    /// <exception cref="CliError">Thrown for a line that does not begin with an option.</exception>
    internal static List<string>? LineTokens(string line, string where)
    {
        var text = line.Trim();
        if (text.Length == 0 || text.StartsWith('#'))
            return null;
        if (!text.StartsWith('-'))
            throw new CliError(
                $"A config file holds options, one per line, and \"{text}\" is not one ({where}). " +
                "Files and directories to process belong on the command line.");

        var split = text.IndexOfAny([' ', '\t']);
        if (split < 0)
            return [text];

        var argument = text[(split + 1)..].TrimStart();
        if (argument.Length >= 2 && argument[0] == '"' && argument[^1] == '"')
            argument = argument[1..^1];
        return [text[..split], argument];
    }
}
