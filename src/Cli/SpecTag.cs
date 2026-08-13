// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Text.RegularExpressions;
using ABChapterize.Errors;
using ABChapterize.Language;

namespace ABChapterize.Cli;

/// <summary>
/// A parsed <c>[...]</c> tag: the language a spec entry is restricted to, plus - for a
/// <c>--custom</c> mapping - the hints saying how the mapping behaves.
/// <para>
/// The tag started life as a bare <c>[xx]</c> language code shared by the phrase, title and
/// <c>--custom</c> options, and is now a comma-separated token list so that a mapping can also say
/// <em>what kind</em> of thing it names: <c>--custom "[de,before-first-chapter,once]/vorwort/:Vorwort"</c>.
/// Everything the hints reach already existed - the built-in prologue and epilogue are this same
/// machinery with different values, and that mapping resolves to exactly the phrase the prologue is
/// - so what is new is the spelling rather than the behaviour.
/// </para>
/// <para>
/// Every field is a scalar, deliberately: this ends up inside
/// <see cref="CliOptions.RunFingerprint"/>, where a record's generated equality and
/// <c>ToString</c> have to describe the tag rather than the identity of a list held inside it.
/// </para>
/// </summary>
/// <param name="Language">The two-letter code this entry is restricted to, or null for one that
/// applies to every file. Checked for shape only, not against <see cref="LanguageRegistry"/>,
/// matching <c>--lang</c>, which also accepts a code this tool has no defaults for.</param>
/// <param name="Scope">Where in the book the phrase is accepted;
/// <see cref="NamedPhraseScope.Anywhere"/> unless a position keyword said otherwise.</param>
/// <param name="Once">Whether the mapping may produce only one mark, the last match winning
/// (<c>once</c>) - <see cref="NamedPhrase.Repeatable"/> inverted.</param>
/// <param name="Heading">Whether a match must be preceded by a real pause to become a mark
/// (<c>heading</c>); see <see cref="NamedPhrase.RequiresLeadIn"/>.</param>
/// <param name="MaxMarks">What a <c>max=N</c> token asked for, or null when none was given.</param>
public readonly record struct SpecTag(
    string? Language, NamedPhraseScope Scope = NamedPhraseScope.Anywhere,
    bool Once = false, bool Heading = false, int? MaxMarks = null)
{
    /// <summary>The position keywords, long form first - the long form is what the documentation
    /// uses and the short one an accepted alias, so a command line can stay readable without being
    /// unwieldy.
    /// <para>
    /// "between-chapters" is deliberately absent as a name for
    /// <see cref="NamedPhraseScope.AfterFirstChapter"/>: that scope does not exclude the tail - it
    /// is exactly how the built-in epilogue uses it - so the word would promise an exclusion which
    /// is not delivered. It stays free for an honest fourth keyword if the strict scope is ever
    /// built.
    /// </para></summary>
    private static readonly (string Long, string Short, NamedPhraseScope Scope)[] ScopeKeywords =
    [
        ("before-first-chapter", "before-first", NamedPhraseScope.BeforeFirstChapter),
        ("after-first-chapter", "after-first", NamedPhraseScope.AfterFirstChapter),
        ("after-last-chapter", "after-last", NamedPhraseScope.AfterLastChapter),
    ];

    /// <summary>Every keyword this tool understands, for the "expected one of" half of an error
    /// message - a typo'd hint is otherwise reported without ever saying what was expected.</summary>
    internal static string KeywordList =>
        string.Join(", ", ScopeKeywords.Select(k => k.Long).Append("once").Append("heading"));

    /// <summary>Whether this tag says anything beyond which language it applies to. What the
    /// options other than <c>--custom</c> check, a hint meaning nothing to them.</summary>
    internal bool HasHints
        => Scope != NamedPhraseScope.Anywhere || Once || Heading || MaxMarks != null;

    /// <summary>
    /// Builds the <see cref="NamedPhrase"/> this tag asks for. The one place the hints turn into
    /// behaviour.
    /// <para>
    /// <see cref="Heading"/> is read straight rather than inferred from <see cref="Once"/>, which
    /// happens to divide the same way for the two built-in phrases: see
    /// <see cref="NamedPhrase.RequiresLeadIn"/> for why the two are separate questions.
    /// </para>
    /// </summary>
    /// <param name="kind">The phrase kind, e.g. "custom 1".</param>
    /// <param name="regex">The compiled phrase.</param>
    /// <param name="title">The raw title template.</param>
    internal NamedPhrase ToPhrase(string kind, Regex regex, string title)
        => new(kind, regex, title, Scope, Repeatable: !Once, RequiresLeadIn: Heading,
            MaxMarks: MaxMarks);

    /// <summary>
    /// Strips a leading <c>[...]</c> tag off a spec entry, returning what it said and what follows
    /// it - or null and the entry unchanged when there is none.
    /// <para>
    /// A bracket run counts as a tag only when at least one of its comma-separated tokens is
    /// recognized; otherwise the brackets are ordinary phrase text. That rule is not pedantry.
    /// Whisper writes bracketed non-speech tags into its transcripts, inherited from the subtitle
    /// corpora it was trained on, and 14 of the 16 books in one test corpus carry them - [Musik]
    /// 259 times, [Abspann] 130, [BLANK_AUDIO] 103 (measured 2026-08-09). So
    /// <c>--custom "[Musik]:Zwischenmusik"</c> is a mapping somebody will plausibly write to catch
    /// the jingles, and it has to go on matching those words rather than being read as a tag for a
    /// language called "Musik". A keyword typo beside a good one (<c>[once,headnig]</c>) is still
    /// an error, one token having been recognized.
    /// </para>
    /// <para>
    /// A token of exactly two ASCII letters is the language code, checked for shape only - the same
    /// test this had before hints existed, and deliberately not a lookup in
    /// <see cref="LanguageRegistry"/>: <c>--lang</c> accepts any two-letter code Whisper knows, so a
    /// mapping written for a language this tool has no number grammar for must be taggable too. The
    /// residual is a literal phrase whose bracket run happens to be two letters - a character class
    /// such as <c>[Kk]apitel</c> - which is read as a language tag. Write it as a regexp
    /// (<c>/[Kk]apitel/</c>) and the question does not arise, the entry no longer starting with a
    /// bracket; that is also how anyone would write it anyway.
    /// </para>
    /// </summary>
    /// <param name="entry">One entry of a spec, already trimmed.</param>
    /// <param name="rest">The entry with any tag removed, trimmed.</param>
    /// <param name="where">How to name this entry in an error message.</param>
    /// <returns>The parsed tag, or null when the entry carries none.</returns>
    /// <exception cref="CliError">Thrown for an unrecognized token beside a recognized one, a
    /// repeated language code, two position keywords, or a malformed <c>max=N</c>.</exception>
    internal static SpecTag? Take(string entry, out string rest, string where)
    {
        rest = entry;
        if (entry.Length < 3 || entry[0] != '[')
            return null;
        var close = entry.IndexOf(']');
        if (close < 0)
            return null;

        var tag = new SpecTag(null);
        var scoped = false;
        var unknown = new List<string>();
        foreach (var token in entry[1..close].Split(',').Select(t => t.Trim()))
        {
            if (token.Length == 2 && char.IsAsciiLetter(token[0]) && char.IsAsciiLetter(token[1]))
            {
                if (tag.Language != null)
                    throw new CliError($"{where}: the tag names more than one language.");
                tag = tag with { Language = token.ToLowerInvariant() };
            }
            else if (ScopeFor(token) is { } scope)
            {
                if (scoped)
                    throw new CliError(
                        $"{where}: the tag names more than one position - a mapping has one scope.");
                (tag, scoped) = (tag with { Scope = scope }, true);
            }
            else if (token.Equals("once", StringComparison.OrdinalIgnoreCase))
            {
                tag = tag with { Once = true };
            }
            else if (token.Equals("heading", StringComparison.OrdinalIgnoreCase))
            {
                tag = tag with { Heading = true };
            }
            else if (TryTakeMax(token, where, out var max))
            {
                if (tag.MaxMarks != null)
                    throw new CliError($"{where}: the tag gives \"max=\" more than once.");
                tag = tag with { MaxMarks = max };
            }
            else
            {
                unknown.Add(token);
            }
        }

        // Nothing recognized at all: this is not a tag, and the brackets belong to the phrase.
        if (tag.Language == null && !tag.HasHints)
            return null;
        if (unknown.Count > 0)
            throw new CliError(
                $"{where}: \"{unknown[0]}\" is not a language code or a known keyword. " +
                $"Keywords are {KeywordList} and \"max=<n>\".");

        rest = entry[(close + 1)..].TrimStart();
        return tag;
    }

    /// <summary>The scope one position keyword stands for, long form or short alias, or null when
    /// the token is no position keyword at all.</summary>
    /// <param name="token">One comma-separated token of a tag, trimmed.</param>
    private static NamedPhraseScope? ScopeFor(string token)
    {
        foreach (var (longForm, shortForm, scope) in ScopeKeywords)
            if (token.Equals(longForm, StringComparison.OrdinalIgnoreCase) ||
                token.Equals(shortForm, StringComparison.OrdinalIgnoreCase))
                return scope;
        return null;
    }

    /// <summary>
    /// Reads a <c>max=N</c> token. <c>max=1</c> is rejected rather than accepted as a synonym for
    /// <c>once</c>: the two would cap a mapping at one mark by opposite rules - <c>once</c> keeps
    /// the <em>last</em> match, as the prologue and epilogue do, while a cap keeps the first N and
    /// drops the rest - and a spelling whose meaning turns on the value is a trap rather than a
    /// convenience.
    /// </summary>
    /// <param name="token">One comma-separated token of a tag, trimmed.</param>
    /// <param name="where">How to name this entry in an error message.</param>
    /// <param name="max">Receives the cap on success.</param>
    /// <returns>True when the token was a <c>max=</c> token; an invalid one throws rather than
    /// falling through to "unknown keyword", which would name the wrong problem.</returns>
    /// <exception cref="CliError">Thrown for a non-numeric, zero, negative or 1-valued cap.</exception>
    private static bool TryTakeMax(string token, string where, out int max)
    {
        max = 0;
        if (!token.StartsWith("max=", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!int.TryParse(token[4..], out max) || max < 1)
            throw new CliError(
                $"{where}: \"{token}\" - \"max=\" expects a mark count of 1 or higher.");
        if (max == 1)
            throw new CliError(
                $"{where}: write \"once\" rather than \"max=1\". They are not the same rule: " +
                "\"once\" keeps the last match in the file, a cap keeps the first ones and drops " +
                "the rest.");
        return true;
    }
}
