// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Text;

namespace ABChapterize.Cli;

/// <summary>One <c>--custom</c> mapping as written on the command line, before its phrase is
/// compiled and its title validated (both of which happen in
/// <see cref="CliOptions.ResolveProfile"/>, which is also where a language could still change what
/// the surrounding options resolve to).</summary>
/// <param name="Phrase">The raw phrase: a plain word, or "/regexp/".</param>
/// <param name="Title">The raw title template, possibly with <c>$1</c>-style group references.</param>
public readonly record struct CustomMapping(string Phrase, string Title);

/// <summary>
/// Parses the <c>--custom</c> mapping syntax: <c>phrase:title</c> pairs, several of them separated
/// by semicolons, or one per line in a <c>--custom-file</c>.
/// <para>
/// Two rules make the punctuation unambiguous even though both delimiters are characters a regexp
/// may legitimately contain. A phrase written as "/regexp/" ends at its closing slash, so the colon
/// in <c>/act:scene/:Scene</c> separates nothing - only the one right after the slash does; for a
/// plain-word phrase the <em>first</em> colon delimits and every later one belongs to the title
/// ("time:Time: an interlude"). A semicolon inside a spec can be written <c>\;</c> when a regexp
/// really needs one, and a mapping file needs no such escape at all, since its mappings are
/// separated by line breaks rather than semicolons.
/// </para>
/// </summary>
internal static class CustomMappingParser
{
    /// <summary>Parses one <c>--custom</c> argument: mappings separated by unescaped semicolons.
    /// Empty entries are ignored, so a trailing or doubled separator is harmless.</summary>
    /// <param name="spec">The raw option value.</param>
    /// <exception cref="CliError">Thrown for a mapping with no delimiter, an empty phrase or an
    /// empty title.</exception>
    internal static List<CustomMapping> ParseSpec(string spec)
        => SplitOnUnescapedSemicolons(spec)
            .Where(entry => entry.Trim().Length > 0)
            .Select(entry => ParseOne(entry, $"--custom mapping \"{entry.Trim()}\""))
            .ToList();

    /// <summary>
    /// Parses a <c>--custom-file</c>: one mapping per line, with blank lines and <c>#</c> comment
    /// lines skipped. Semicolons are ordinary characters here - a line is a whole mapping - so a
    /// regexp needing one can be written directly.
    /// </summary>
    /// <param name="path">Path of the mapping file.</param>
    /// <exception cref="CliError">Thrown when the file cannot be read, contains no mapping at all,
    /// or holds a malformed one (named with its line number).</exception>
    internal static List<CustomMapping> ParseFile(string path)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new CliError($"Cannot read --custom-file \"{path}\": {ex.Message}");
        }

        var mappings = new List<CustomMapping>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            mappings.Add(ParseOne(line, $"--custom-file \"{path}\", line {i + 1}"));
        }
        if (mappings.Count == 0)
            throw new CliError($"--custom-file \"{path}\" holds no mapping at all.");
        return mappings;
    }

    /// <summary>Splits a spec at every semicolon that is not written as <c>\;</c>. Any other
    /// backslash is passed through untouched, so a regexp's own <c>\d</c> or <c>\s</c> survives
    /// this step intact.</summary>
    /// <param name="spec">The raw option value.</param>
    private static IEnumerable<string> SplitOnUnescapedSemicolons(string spec)
    {
        var entry = new StringBuilder();
        for (var i = 0; i < spec.Length; i++)
        {
            if (spec[i] == '\\' && i + 1 < spec.Length && spec[i + 1] == ';')
            {
                entry.Append(';');
                i++;
            }
            else if (spec[i] == ';')
            {
                yield return entry.ToString();
                entry.Clear();
            }
            else
            {
                entry.Append(spec[i]);
            }
        }
        yield return entry.ToString();
    }

    /// <summary>Parses a single <c>phrase:title</c> mapping.</summary>
    /// <param name="text">The mapping, with surrounding whitespace still possible.</param>
    /// <param name="where">How to name this mapping in an error message.</param>
    /// <exception cref="CliError">Thrown for a missing delimiter, an unterminated "/regexp/", an
    /// empty phrase or an empty title.</exception>
    private static CustomMapping ParseOne(string text, string where)
    {
        text = text.Trim();
        var delimiter = FindDelimiter(text, where);
        var phrase = text[..delimiter].TrimEnd();
        var title = text[(delimiter + 1)..].Trim();

        if (phrase.Length == 0 || phrase == "//")
            throw new CliError($"{where}: the phrase before the \":\" must not be empty.");
        if (title.Length == 0)
            throw new CliError($"{where}: the title after the \":\" must not be empty.");
        return new CustomMapping(phrase, title);
    }

    /// <summary>The index of the colon separating phrase from title - after the closing slash for a
    /// "/regexp/" phrase, the first one otherwise.</summary>
    /// <param name="text">The trimmed mapping.</param>
    /// <param name="where">How to name this mapping in an error message.</param>
    private static int FindDelimiter(string text, string where)
    {
        if (!text.StartsWith('/'))
            return text.IndexOf(':') is var colon and >= 0
                ? colon
                : throw new CliError(
                    $"{where}: expected \"phrase:title\", but there is no \":\" to separate the two.");

        var closing = FindClosingSlash(text);
        if (closing < 0)
            throw new CliError($"{where}: the \"/regexp/\" phrase has no closing \"/\".");
        if (closing + 1 >= text.Length || text[closing + 1] != ':')
            throw new CliError(
                $"{where}: expected a \":\" directly after the \"/regexp/\" phrase's closing \"/\".");
        return closing + 1;
    }

    /// <summary>The index of the slash closing a "/regexp/" phrase, skipping any that the regexp
    /// itself escaped as <c>\/</c>; -1 when there is none.</summary>
    /// <param name="text">The trimmed mapping, known to start with a slash.</param>
    private static int FindClosingSlash(string text)
    {
        for (var i = 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
                i++;
            else if (text[i] == '/')
                return i;
        }
        return -1;
    }
}
