// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Text.RegularExpressions;
using ABChapterize.Language.Phrases;
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
/// <param name="MaxMarks">What a <c>max=N</c> token asked for, or null when none was given.</param>
/// <remarks>
/// There is deliberately no hint for "must follow a real pause". A <c>heading</c> keyword existed
/// during 0.12.0's development and was removed before release as an exact duplicate of the phrase
/// syntax's own <c>^</c>: both resolve to <see cref="Detection.IsolationRule.LeadIn"/>, both are
/// waived the same way when the recognizer opened a segment at the match, and both measure at the
/// same fallback position (see <see cref="Detection.RegionProber.NamedIsolationFor"/>). Write
/// <c>--custom "/^vorwort/:Vorwort"</c> instead. Do not reintroduce it: a second spelling of one
/// rule is a second thing to keep in step, and the tag was the spelling that could not say
/// <em>where</em> the pause is required, only that one is.
/// </remarks>
public readonly record struct SpecTag(
    string? Language, NamedPhraseScope Scope = NamedPhraseScope.Anywhere,
    bool Once = false, int? MaxMarks = null)
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
        string.Join(", ", ScopeKeywords.Select(k => k.Long).Append("once"));

    /// <summary>Whether this tag says anything beyond which language it applies to. What the
    /// options other than <c>--custom</c> check, a hint meaning nothing to them.</summary>
    internal bool HasHints
        => Scope != NamedPhraseScope.Anywhere || Once || MaxMarks != null;

    /// <summary>
    /// Builds the <see cref="NamedPhrase"/> this tag asks for. The one place the hints turn into
    /// behaviour.
    /// <para>
    /// <see cref="NamedPhrase.RequiresLeadIn"/> is deliberately left false here whatever the tag
    /// says: a <c>--custom</c> mapping that wants a pause in front of it writes <c>^</c> into its
    /// phrase, which arrives through the wording's own guards instead. The flag stays on
    /// <see cref="NamedPhrase"/> for the built-in prologue and epilogue, which are not built from a
    /// tag and must keep it whatever phrase the user gives them.
    /// </para>
    /// </summary>
    /// <param name="kind">The phrase kind, e.g. "custom 1".</param>
    /// <param name="pattern">The compiled phrase.</param>
    /// <param name="title">The parsed title template.</param>
    internal NamedPhrase ToPhrase(string kind, PhrasePattern pattern, TitleTemplate title)
        => new(kind, pattern, title, Scope, Repeatable: !Once, MaxMarks: MaxMarks);

    /// <summary>
    /// Strips a leading <c>[...]</c> tag off a spec entry, returning what it said and what follows
    /// it - or null and the entry unchanged when there is none.
    /// <para>
    /// A bracket run counts as a tag only when at least one of its comma-separated tokens is
    /// recognized; otherwise the brackets are ordinary phrase text. That rule is not pedantry.
    /// Whisper writes bracketed non-speech tags into its transcripts, inherited from the subtitle
    /// corpora it was trained on, and most books in a real corpus carry them. So
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
    /// <remarks>Notes: how often Whisper writes bracketed tags into a real corpus.
    /// <include file='../../notes/Cli/SpecTag.xml' path='doc/member[@name="Split"]/*' /></remarks>
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
                // Named rather than left to the unknown-keyword branch, which would report it
                // alongside the list of what is accepted and leave the reader to work out that the
                // replacement is not a keyword at all. The hint was an exact duplicate of the
                // phrase syntax's ^ (see the remarks on SpecTag) and was removed before 0.12.0
                // shipped, so this is guidance for a command line written during development
                // rather than a migration path for a released spelling.
                throw new CliError(
                    $"{where}: \"heading\" is no longer a tag keyword - write \"^\" at the start of " +
                    "the phrase instead, e.g. --custom \"[before-first-chapter,once]/^vorwort/:Vorwort\".");
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
