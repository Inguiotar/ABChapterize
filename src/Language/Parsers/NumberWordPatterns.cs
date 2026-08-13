// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Text;
using System.Text.RegularExpressions;

namespace ABChapterize.Language.Parsers;

/// <summary>
/// The pieces every <see cref="INumberWordParser.NumberWordPattern"/> is assembled from. A parser
/// builds its pattern out of the very tables it parses with, so the two cannot drift apart: adding
/// a spelling to a dictionary adds it to the pattern in the same edit.
/// <para>
/// The patterns are a deliberate <em>superset</em> of what the parsers accept. Their job is only to
/// say where a number ends inside a phrase match - the captured text is handed straight back to the
/// parser, which decides whether it really is one. A pattern that admits "zweiste" costs nothing;
/// one that misses "einundzwanzigste" costs a chapter, so every doubtful case is resolved towards
/// admitting more. <c>NumberWordPatternTests</c> checks the direction that matters, replaying every
/// spelling the test project's independent reference spellers produce for 0-999.
/// </para>
/// </summary>
internal static class NumberWordPatterns
{
    /// <summary>
    /// An alternation of literal words, longest first. The ordering is not cosmetic: .NET picks the
    /// first alternative that lets the <em>overall</em> match succeed, so with "drei" ahead of
    /// "dreizehn" a pattern ending in the number would capture the short one. The trailing
    /// word-boundary guard in <see cref="ABChapterize.Language.Phrases.NumberPattern"/> makes that
    /// recoverable by backtracking, and this ordering makes it unnecessary in the first place.
    /// </summary>
    /// <param name="words">The words to admit; duplicates and empties are dropped.</param>
    /// <param name="diacritics">Per-character alternatives to fold in, for a parser that normalizes
    /// its input rather than listing every spelling - see <see cref="Tolerant"/>.</param>
    internal static string Alt(IEnumerable<string> words, string? diacritics = null)
    {
        var pattern = new StringBuilder("(?:");
        var first = true;
        foreach (var word in words.Where(w => w.Length > 0).Distinct()
                     .OrderByDescending(w => w.Length).ThenBy(w => w, StringComparer.Ordinal))
        {
            if (!first)
                pattern.Append('|');
            pattern.Append(Tolerant(word, diacritics));
            first = false;
        }
        return pattern.Append(')').ToString();
    }

    /// <summary>An alternation of ready-made fragments, for a language whose words cannot all be
    /// listed - a Spanish fused ordinal is a scale stem times a unit, which as literals would be
    /// several hundred alternatives saying one thing.</summary>
    /// <param name="fragments">Sub-patterns, each already a group or a single term.</param>
    internal static string AnyOf(params string[] fragments) => $"(?:{string.Join('|', fragments)})";

    /// <summary>
    /// One atom as a run of several, separated by a space or a hyphen - the shape of a number in a
    /// language that writes it as separate words ("one hundred and five", "vingt et un"). The
    /// connectors belong in the atom alongside the number words themselves; a run ending in one
    /// ("one and") is harmless, since the value is read by the parser and not by this.
    /// </summary>
    /// <param name="atom">The pattern one token of a number matches.</param>
    internal static string Run(string atom) => $"(?:{atom}(?:[- ]{atom})*)";

    /// <summary>A run of words, the common case - <see cref="Run"/> over <see cref="Alt"/>.</summary>
    /// <param name="words">Every token a number of this language may consist of.</param>
    /// <param name="diacritics">Per-character alternatives, as for <see cref="Alt"/>.</param>
    internal static string TokenRun(IEnumerable<string> words, string? diacritics = null)
        => Run(Alt(words, diacritics));

    /// <summary>
    /// One word with its final vowel made optional - the stem an ordinal suffix attaches to in the
    /// languages that elide it ("venti" + "esimo" = "ventesimo", "uno" + "esimo" = "unesimo").
    /// Derived rather than listed, so a new cardinal spelling brings its ordinal along.
    /// </summary>
    /// <param name="word">The cardinal word.</param>
    /// <param name="diacritics">Per-character alternatives, as for <see cref="Alt"/>.</param>
    internal static string Stem(string word, string? diacritics = null)
        => word.Length > 1 && "aeiou".Contains(char.ToLowerInvariant(word[^1]))
            ? $"{Tolerant(word[..^1], diacritics)}(?:{Tolerant(word[^1..], diacritics)})?"
            : Tolerant(word, diacritics);

    /// <summary>
    /// Escapes a word and widens each character a parser's own <c>Normalize</c> folds, so that the
    /// accented spelling a transcript actually carries matches a table keyed without it. The map is
    /// written as a run of "&lt;plain&gt;&lt;variants&gt;;" groups - Polish's
    /// <c>"aą;cć;eę;ll;nń;oó;sś;zźż;"</c> - and mirrors that parser's <c>Normalize</c> exactly;
    /// keeping the two in one file is what keeps them in step.
    /// </summary>
    /// <param name="word">The word as the parser's table keys it.</param>
    /// <param name="diacritics">The fold map, or null when the parser lists its spellings itself.</param>
    private static string Tolerant(string word, string? diacritics)
    {
        if (diacritics == null)
            return Regex.Escape(word);
        var pattern = new StringBuilder();
        foreach (var c in word)
        {
            var group = Group(diacritics, c);
            pattern.Append(group == null ? Regex.Escape(c.ToString()) : $"[{group}]");
        }
        return pattern.ToString();
    }

    /// <summary>The character class one plain character stands for, or null when the map says
    /// nothing about it.</summary>
    /// <param name="diacritics">The fold map; see <see cref="Tolerant"/>.</param>
    /// <param name="plain">The character as a table key spells it.</param>
    private static string? Group(string diacritics, char plain)
    {
        foreach (var group in diacritics.Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (group[0] == plain)
                return group;
        return null;
    }
}
