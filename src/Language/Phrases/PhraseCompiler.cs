// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Text;
using System.Text.RegularExpressions;
using ABChapterize.Errors;

namespace ABChapterize.Language.Phrases;

/// <summary>What a phrase names, which is the one thing the syntax reads differently.</summary>
internal enum PhraseKind
{
    /// <summary>A chapter announcement: it carries a number, so a bare word is read as "the number
    /// sits beside this word" and <c>none</c> means "the number is the whole announcement".</summary>
    Chapter,

    /// <summary>A prologue, epilogue or <c>--custom</c> announcement: no number is parsed and none
    /// is expected, so a bare word is exactly the word and <c>none</c> is a word like any other.</summary>
    Named,
}

/// <summary>
/// Turns what a user wrote into a <see cref="PhrasePattern"/>: alternatives split out of the
/// alternation, <c>^</c>/<c>$</c> taken off as guard requests, and <c>()</c> expanded into the
/// language's own number notation.
/// <para>
/// The splitting is what the rest of the design rests on. <c>^</c> and <c>$</c> are not anchors
/// here - against a flattened multi-segment window they would be useless as anchors anyway - but
/// requests for a pause in front of the announcement and behind it, and a request belongs to the
/// wording that made it. So the alternation is taken apart until each leaf is one wording, and each
/// leaf is compiled and matched on its own. Only the edges of a leaf carry the meaning:
/// <c>/^(?:a|b)$/</c> and <c>/(?:^a$|b$)/</c> both work, while a <c>$</c> in the middle of a
/// wording stays the ordinary regex anchor it always was (and, mid-pattern, matches nothing).
/// </para>
/// </summary>
internal static class PhraseCompiler
{
    /// <summary>The spelling that says a chapter is announced by its number and nothing else.
    /// Shorthand for <c>/^()$/</c> and expanded into exactly that, so the two are one rule rather
    /// than two implementations of one idea.</summary>
    internal const string BareNumberWord = "none";

    /// <summary>What <see cref="BareNumberWord"/> stands for.</summary>
    private const string BareNumberBody = "^()$";

    /// <summary>
    /// Compiles one option's resolved entries into the phrase a run listens for.
    /// </summary>
    /// <param name="entries">The alternatives as written, already resolved for one language (see
    /// <see cref="PhraseSpec"/>): tags stripped, <c>default</c> expanded, empty entries removed.</param>
    /// <param name="language">Two-letter code deciding what <c>()</c> expands to.</param>
    /// <param name="kind">Whether this phrase carries a chapter number; see <see cref="PhraseKind"/>.</param>
    /// <param name="what">How to name this phrase in an error message ("chapter phrase",
    /// "--custom mapping \"...\"").</param>
    /// <exception cref="CliError">Thrown for an invalid regexp or an unterminated group.</exception>
    internal static PhrasePattern Compile(
        IReadOnlyList<string> entries, string language, PhraseKind kind, string what)
    {
        var leaves = new List<Leaf>();
        foreach (var entry in entries)
            Split(BodyOf(entry, kind, what), leadIn: false, leadOut: false, leaves, what);

        // A number spoken alone goes last however the user ordered the entries, and the rest keep
        // the order they were written in. It is the wording with nothing to recognize an
        // announcement by except the pauses around the number, so it is the reading to fall back on
        // rather than the one to lead with - and the phrase's own order is what says so, both where
        // two wordings claim the same words and in the -v log.
        var ordered = leaves.Where(l => !IsBareNumber(l)).Concat(leaves.Where(IsBareNumber));

        var alternatives = new List<PhraseAlternative>();
        foreach (var leaf in ordered)
            alternatives.Add(Build(leaf, alternatives.Count, language, what));
        return new PhrasePattern(alternatives, string.Join(';', entries));
    }

    /// <summary>
    /// The regex body one written entry stands for. A <c>/regexp/</c> is its own body; anything
    /// else is a literal, which for a chapter phrase is the word with the number behind it and the
    /// word with the number in front of it, and for a named phrase is just the word.
    /// <para>
    /// Both wordings capture, so a literal phrase always says where its number is and a title's
    /// <c>${number}</c> works under either announcement order ("Teil sieben", "Siebter Teil"). It
    /// gets no bare-word wording, unlike a built-in default: reading the number off whatever stands
    /// around the match is what a hand-written <c>/regexp/</c> without a <c>()</c> is for, and a
    /// literal that wants it can be written as one.
    /// </para>
    /// <para>
    /// Both carry a <c>^</c>, for the same reason every built-in default does - an announcement is
    /// set off from what precedes it - and it is what pays for the number-first wording, which
    /// otherwise claims "erste Teil" on "Der erste Teil 5" and reads chapter 1. See
    /// <see cref="ILanguage.ChapterPhrase"/> for that measurement and for what a superseded reading
    /// is kept for.
    /// </para>
    /// </summary>
    /// <param name="entry">One entry as written.</param>
    /// <param name="kind">Whether this phrase carries a chapter number.</param>
    /// <param name="what">How to name this phrase in an error message.</param>
    private static string BodyOf(string entry, PhraseKind kind, string what)
    {
        if (kind == PhraseKind.Chapter && entry.Equals(BareNumberWord, StringComparison.OrdinalIgnoreCase))
            return BareNumberBody;
        if (entry.Length > 2 && entry.StartsWith('/') && entry.EndsWith('/'))
            return entry[1..^1];
        if (entry.StartsWith('/') || entry.EndsWith('/'))
            throw new CliError(
                $"{what}: \"{entry}\" looks like a \"/regexp/\" but is missing its other \"/\".");

        var word = Regex.Escape(entry);
        return kind == PhraseKind.Chapter ? $"^{word} ()|^() {word}" : word;
    }

    /// <summary>One leaf of the alternation: a wording plus the guards its edges asked for.</summary>
    /// <param name="Body">The regex body, anchors already taken off.</param>
    /// <param name="LeadIn">Whether a <c>^</c> asked for a pause in front.</param>
    /// <param name="LeadOut">Whether a <c>$</c> asked for a pause behind.</param>
    private readonly record struct Leaf(string Body, bool LeadIn, bool LeadOut);

    /// <summary>Whether a wording is nothing but the number group, which is not something a regular
    /// expression can look for: what makes a lone number an announcement is the sentence around it,
    /// and that is read off the transcript segments rather than matched
    /// (<see cref="NumberWordParser.FindBareNumberAnnouncement"/>). Asked while the token is still
    /// recognizable, before <c>()</c> is expanded into a language's whole number grammar.</summary>
    /// <param name="leaf">The wording to test.</param>
    private static bool IsBareNumber(Leaf leaf) => leaf.Body.Trim() == "()";

    /// <summary>
    /// Takes one entry's body apart into leaves: split at the top-level <c>|</c>, strip the edge
    /// anchors, descend into a group that spans the whole of what is left - which is what makes
    /// <c>/^(?:a|b)$/</c> hand its two guards to both wordings - and finally distribute what remains
    /// over any alternation still buried inside it.
    /// <para>
    /// A leaf carries no alternation at all when this is done, which is the property the whole
    /// design leans on: the wording that found an announcement is what its re-transcription is held
    /// to (<see cref="ABChapterize.Language.LanguageProfile.AnnouncementFor"/>), and a matcher that
    /// still offered a choice would let one wording vouch for another's mark. So
    /// <c>/kapit(?:el|let) ()/</c> becomes two wordings rather than one, and
    /// <c>/(?:a|b)c(?:d|e)/</c> becomes four.
    /// </para>
    /// </summary>
    /// <param name="body">The body to split.</param>
    /// <param name="leadIn">Whether an enclosing level already asked for a leading pause.</param>
    /// <param name="leadOut">Whether it already asked for a trailing one.</param>
    /// <param name="into">Accumulator for the leaves found.</param>
    /// <param name="what">How to name this phrase in an error message.</param>
    /// <exception cref="CliError">Thrown when the expansion runs past <see cref="MaxWordings"/>.</exception>
    private static void Split(string body, bool leadIn, bool leadOut, List<Leaf> into, string what)
    {
        var parts = TopLevelAlternatives(body);
        if (parts.Count > 1)
        {
            foreach (var part in parts)
                Split(part, leadIn, leadOut, into, what);
            return;
        }

        var rest = TakeAnchors(parts[0], ref leadIn, ref leadOut);
        if (WholeGroupBody(rest) is { } inner)
        {
            Split(inner, leadIn, leadOut, into, what);
            return;
        }
        if (rest.Length == 0)
            return;
        foreach (var wording in Distribute(rest, what))
            into.Add(new Leaf(wording, leadIn, leadOut));
    }

    /// <summary>
    /// How many wordings one written entry may expand into. Generous next to anything a phrase is
    /// really written as - the widest built-in default reaches six - and low enough that a
    /// cross product nobody intended is reported rather than compiled into a window scan that
    /// runs one regex per wording.
    /// </summary>
    private const int MaxWordings = 64;

    /// <summary>
    /// Multiplies out every alternation left inside one wording, so that <c>a(?:b|c)d</c> becomes
    /// <c>abd</c> and <c>acd</c>. Only an unquantified <c>(?:...)</c> is expanded: a quantifier binds
    /// to the group as a whole, and every other bracket - a capturing group, a named one, a
    /// lookaround - either means something a copy would not preserve or holds the chapter number's
    /// own notation, which is an alternation of a language's entire number grammar and must stay one
    /// token (see <see cref="NumberPattern"/>).
    /// <para>
    /// The quantifier exemption is documented in the manual as the way to say "one wording, two
    /// transcriptions of it" (user's call, 2026-08-17), so <c>{1}</c> is a contract now rather than a
    /// side effect - <c>PhraseCompilerTests.AQuantifiedAlternation_StaysOneWordingAndMatchesEitherSpelling</c>
    /// holds it. It exists because splitting is the right default and the wrong one for exactly one
    /// case: an alternation that names two ways the *recognizer* writes the same words, where
    /// splitting leaves each wording refusing to confirm the other's spelling and the onset walk
    /// stops where the spelling flipped rather than where the announcement was clipped. Making the
    /// user spell the intent is what distinguishes that from the case splitting is *for* - two
    /// genuinely different wordings, where one vouching for the other is the build-328 defect
    /// <see cref="ABChapterize.Language.AnnouncementMatcher.ForWording"/> records.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the spelling wobble that made an unquantified alternation cost six marks.
    /// <include file='../../../notes/Language/Phrases/PhraseCompiler.xml' path='doc/member[@name="Distribute"]/*' /></remarks>
    /// </summary>
    /// <param name="body">One wording, anchors already stripped and with no top-level alternation
    /// left.</param>
    /// <param name="what">How to name this phrase in an error message.</param>
    /// <exception cref="CliError">Thrown when the expansion runs past <see cref="MaxWordings"/>.</exception>
    private static List<string> Distribute(string body, string what)
    {
        var done = new List<string>();
        Expand(body, done, body, what);
        return done;
    }

    /// <summary>
    /// One step of <see cref="Distribute"/>: expands the leftmost alternation and recurses into each
    /// branch in turn. Depth-first, so the wordings come out in the order the regex engine would have
    /// tried them - which is what decides the winner when two of them match at the same position.
    /// </summary>
    /// <param name="body">What is left to expand.</param>
    /// <param name="into">Accumulator for the finished wordings.</param>
    /// <param name="original">The wording as written, for the error message.</param>
    /// <param name="what">How to name this phrase in an error message.</param>
    /// <exception cref="CliError">Thrown when the expansion runs past <see cref="MaxWordings"/>.</exception>
    private static void Expand(string body, List<string> into, string original, string what)
    {
        var (start, length, inner) = SplittableGroupAt(body);
        if (start < 0)
        {
            if (into.Count == MaxWordings)
                throw new CliError(
                    $"The {what} \"{original}\" expands to more than {MaxWordings} wordings. " +
                    "Alternatives inside it are multiplied out, one wording per combination - " +
                    "write the ones you want as separate alternatives instead.");
            into.Add(body);
            return;
        }
        var prefix = body[..start];
        var suffix = body[(start + length)..];
        foreach (var branch in TopLevelAlternatives(inner))
            Expand(prefix + branch + suffix, into, original, what);
    }

    /// <summary>
    /// Finds the first non-capturing group that carries an alternation of its own and is not
    /// quantified, as (start, length, body); start is -1 when there is none.
    /// </summary>
    /// <param name="body">The wording to scan.</param>
    private static (int Start, int Length, string Inner) SplittableGroupAt(string body)
    {
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == '\\')
            {
                i++;
                continue;
            }
            if (body[i] == '[')
            {
                while (++i < body.Length && body[i] != ']')
                    if (body[i] == '\\')
                        i++;
                continue;
            }
            if (!body.AsSpan(i).StartsWith("(?:"))
                continue;
            if (GroupEnd(body, i) is not { } end)
                continue;
            // A quantifier binds to the whole group, so its alternation cannot be distributed.
            if (end + 1 < body.Length && body[end + 1] is '?' or '*' or '+' or '{')
                continue;
            var inner = body[(i + 3)..end];
            if (TopLevelAlternatives(inner).Count > 1)
                return (i, end - i + 1, inner);
        }
        return (-1, 0, "");
    }

    /// <summary>The index of the ")" closing the group that opens at <paramref name="open"/>, or
    /// null when it is never closed - which the regex compiler will report far better than this
    /// could.</summary>
    /// <param name="body">The wording to scan.</param>
    /// <param name="open">Index of the opening "(".</param>
    private static int? GroupEnd(string body, int open)
    {
        var depth = 0;
        for (var i = open; i < body.Length; i++)
        {
            if (body[i] == '\\')
                i++;
            else if (body[i] == '[')
                while (++i < body.Length && body[i] != ']')
                {
                    if (body[i] == '\\')
                        i++;
                }
            else if (body[i] == '(')
                depth++;
            else if (body[i] == ')' && --depth == 0)
                return i;
        }
        return null;
    }

    /// <summary>
    /// Splits at every <c>|</c> that is not inside a group or a character class and not escaped.
    /// Always returns at least one part, so the caller can treat "no alternation" as "one wording".
    /// </summary>
    /// <param name="body">The regex body to scan.</param>
    private static List<string> TopLevelAlternatives(string body)
    {
        var parts = new List<string>();
        var start = 0;
        var depth = 0;
        var inClass = false;
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c == '\\')
                i++;
            else if (inClass)
                inClass = c != ']';
            else if (c == '[')
                inClass = true;
            else if (c == '(')
                depth++;
            else if (c == ')')
                depth--;
            else if (c == '|' && depth == 0)
            {
                parts.Add(body[start..i]);
                start = i + 1;
            }
        }
        parts.Add(body[start..]);
        return parts;
    }

    /// <summary>
    /// Strips a leading <c>^</c> and a trailing <c>$</c>, recording each as a guard request. A
    /// <c>$</c> written <c>\$</c> is a dollar sign and stays put, which is what an odd number of
    /// preceding backslashes says.
    /// </summary>
    /// <param name="body">One wording.</param>
    /// <param name="leadIn">Set when a <c>^</c> was found.</param>
    /// <param name="leadOut">Set when a <c>$</c> was found.</param>
    private static string TakeAnchors(string body, ref bool leadIn, ref bool leadOut)
    {
        if (body.StartsWith('^'))
        {
            leadIn = true;
            body = body[1..];
        }
        if (body.EndsWith('$') && !EndsEscaped(body[..^1]))
        {
            leadOut = true;
            body = body[..^1];
        }
        return body;
    }

    /// <summary>Whether the character after <paramref name="text"/> is escaped, i.e. whether the
    /// text ends in an odd number of backslashes.</summary>
    /// <param name="text">Everything in front of the character in question.</param>
    private static bool EndsEscaped(string text)
    {
        var backslashes = 0;
        for (var i = text.Length - 1; i >= 0 && text[i] == '\\'; i--)
            backslashes++;
        return backslashes % 2 == 1;
    }

    /// <summary>
    /// The body of a group spanning the whole wording, or null when there is none - <c>(?:a|b)</c>
    /// yields <c>a|b</c>, while <c>(?:a)b</c> and <c>(?:a)?</c> yield null, the group not being the
    /// whole of it. A capturing group is left alone: unwrapping one would drop a capture a title may
    /// reference.
    /// </summary>
    /// <param name="body">One wording, anchors already stripped.</param>
    private static string? WholeGroupBody(string body)
    {
        if (!body.StartsWith("(?:", StringComparison.Ordinal) || !body.EndsWith(')'))
            return null;
        var depth = 0;
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == '\\')
                i++;
            else if (body[i] == '(')
                depth++;
            else if (body[i] == ')' && --depth == 0)
                return i == body.Length - 1 ? body[3..^1] : null;
        }
        return null;
    }

    /// <summary>Compiles one leaf, expanding its <c>()</c> tokens on the way.</summary>
    /// <param name="leaf">The wording and its guards.</param>
    /// <param name="index">Its position in the phrase.</param>
    /// <param name="language">Two-letter code deciding what <c>()</c> expands to.</param>
    /// <param name="what">How to name this phrase in an error message.</param>
    /// <exception cref="CliError">Thrown for an invalid regexp.</exception>
    private static PhraseAlternative Build(Leaf leaf, int index, string language, string what)
    {
        if (IsBareNumber(leaf))
            return new PhraseAlternative(
                index, null, leaf.Body, HasNumberGroup: true, leaf.LeadIn, leaf.LeadOut);

        var expanded = ExpandNumberGroups(leaf.Body, language, out var hasNumberGroup);
        try
        {
            var regex = new Regex(expanded, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return new PhraseAlternative(
                index, regex, leaf.Body, hasNumberGroup, leaf.LeadIn, leaf.LeadOut);
        }
        catch (ArgumentException ex)
        {
            throw new CliError($"Invalid {what} regexp \"{leaf.Body}\": {ex.Message}");
        }
    }

    /// <summary>
    /// Rewrites every unnamed capturing group into the named number group: <c>()</c> takes the
    /// language's own number notation as its body, and a non-empty one keeps whatever the user
    /// wrote. That is the older <c>--chapter-phrase "/part (\d+)/"</c> convention generalized
    /// rather than replaced - an unnamed group always meant "the number is here" - and it is what
    /// leaves every <em>other</em> capturing group having to be named, which is what a title
    /// references them by.
    /// </summary>
    /// <param name="body">One wording.</param>
    /// <param name="language">Two-letter code deciding what <c>()</c> expands to.</param>
    /// <param name="hasNumberGroup">Set when the wording carries at least one.</param>
    private static string ExpandNumberGroups(string body, string language, out bool hasNumberGroup)
    {
        var expanded = new StringBuilder();
        var inClass = false;
        hasNumberGroup = false;
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c == '\\' && i + 1 < body.Length)
            {
                expanded.Append(c).Append(body[i + 1]);
                i++;
            }
            else if (inClass)
            {
                expanded.Append(c);
                inClass = c != ']';
            }
            else if (c == '[')
            {
                expanded.Append(c);
                inClass = true;
            }
            else if (c == '(' && (i + 1 >= body.Length || body[i + 1] != '?'))
            {
                hasNumberGroup = true;
                expanded.Append($"(?<{PhraseAlternative.NumberGroup}>");
                // An empty group is the () token: it has no body of its own, so it gets the
                // language's. Anything else keeps what the user wrote and only gains the name.
                if (i + 1 < body.Length && body[i + 1] == ')')
                {
                    expanded.Append(NumberPattern.For(language));
                    expanded.Append(')');
                    i++;
                }
            }
            else
            {
                expanded.Append(c);
            }
        }
        return expanded.ToString();
    }
}
