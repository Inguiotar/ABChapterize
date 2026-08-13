// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Text;
using System.Text.RegularExpressions;

namespace ABChapterize.Language;

/// <summary>
/// Where in a book a <see cref="NamedPhrase"/> may legitimately be announced. The first two values
/// are expressed against the numbered chapter sequence rather than against absolute time, because
/// that is the only landmark detection has: a book's front matter can run for a minute or for
/// half an hour, and no fixed offset separates the two.
/// </summary>
public enum NamedPhraseScope
{
    /// <summary>Only before the first numbered chapter has been found - a prologue's own place,
    /// and the reason a later "the prologue explained..." cannot become a second mark.</summary>
    BeforeFirstChapter,

    /// <summary>Only once at least one numbered chapter has been found - an epilogue follows the
    /// book's chapters, so a mention before any of them is front matter, not the epilogue.</summary>
    AfterFirstChapter,

    /// <summary>
    /// Only after the file's <em>last</em> numbered chapter - what the built-in epilogue is
    /// actually held to, available to a <c>--custom</c> mapping through the
    /// <c>after-last-chapter</c> hint.
    /// <para>
    /// Alone among these, this cannot be checked while detection runs: which chapter is the last
    /// one is unknown until every pass has finished. So it is applied twice - as
    /// <see cref="AfterFirstChapter"/> during detection, which is the strongest thing observable
    /// then and never wrong (nothing before the first chapter can be after the last), and then
    /// properly at result-build time, where a mark that turned out to sit mid-book is dropped.
    /// Precision only: unlike the other two it saves no transcription, since the announcement has
    /// already been heard and placed by the time it can be judged.
    /// </para>
    /// </summary>
    AfterLastChapter,

    /// <summary>Anywhere in the file. What a <c>--custom</c> mapping gets unless one of its hints
    /// says otherwise: it names a recurring structural element ("Zwischenspiel", "Zeittafel") whose
    /// place in the book is the user's business, not something detection could second-guess from
    /// the chapter sequence.</summary>
    Anywhere,
}

/// <summary>
/// A phrase that produces a mark with a fixed title instead of a numbered chapter: the prologue and
/// epilogue announcements, and every <c>--custom</c> mapping. Unlike
/// <see cref="LanguageProfile.PhraseRegex"/> no number is parsed and none is expected, so such a
/// mark takes no part in the chapter-number sequence - it neither closes nor opens a gap, and it can
/// never make a book look like it is missing a chapter.
/// </summary>
/// <param name="Kind">Short identifier used in log lines, and the key a mark is deduplicated or
/// replaced under - so it must be unique per phrase ("prologue", "epilogue", "custom 1", ...).
/// Never user-visible in a written chapter title.</param>
/// <param name="Regex">Compiled, case-insensitive expression that recognizes the announcement.</param>
/// <param name="Title">The chapter title written for a mark this phrase produced, possibly with
/// <c>$1</c>-style references to <paramref name="Regex"/>'s capturing groups - see
/// <see cref="ResolveTitle"/>.</param>
/// <param name="Scope">Where in the book the phrase is accepted; see <see cref="NamedPhraseScope"/>.</param>
/// <param name="Repeatable">Whether every occurrence produces its own mark. False for the prologue
/// and epilogue, of which a book has at most one each, so a later match replaces an earlier one;
/// true for <c>--custom</c> mappings, where a book may well hold a dozen "Zwischenspiel"s and each
/// deserves its own entry.</param>
/// <param name="RequiresLeadIn">Whether a match must be preceded by a real pause to become a mark
/// (<see cref="ABChapterize.Detection.IsolationRule.LeadIn"/>). True for the prologue and epilogue,
/// where the phrase is a heading word at a section boundary and a mid-sentence match is always
/// wrong - Italian "riepilogo" contains "epilogo", and one such match destroyed a book's real
/// epilogue mark (2026-08-05), a non-repeatable phrase's later detection replacing its earlier one.
/// False for <c>--custom</c>: those name whatever recurring element the user says they do, at
/// whatever position, and second-guessing that is not this code's business. Deliberately a flag of
/// its own rather than an inference from <paramref name="Repeatable"/>, which happens to divide the
/// same way today for an unrelated reason.</param>
/// <param name="MaxMarks">How many marks this phrase may produce in one file (the
/// <c>max=&lt;n&gt;</c> hint), or null for no cap of its own - which is every built-in phrase and
/// every mapping that does not ask. Counted first-N-wins, matching
/// <see cref="ABChapterize.Detection.DetectionTuning.MaxCustomMarksPerFile"/>, the file-wide cap
/// this narrows: a mapping that knows how many of its element a book holds should not be able to
/// spend the file's whole allowance on a phrase that turned out to match prose. Never 1 - see
/// <c>SpecTag.TryTakeMax</c> for why that spelling is refused in favour of
/// <paramref name="Repeatable"/>.</param>
public sealed record NamedPhrase(
    string Kind, Regex Regex, string Title, NamedPhraseScope Scope, bool Repeatable = false,
    bool RequiresLeadIn = false, int? MaxMarks = null)
{
    /// <summary><see cref="Kind"/> of the built-in prologue phrase. A constant because two rules
    /// outside the phrase itself key on it - the epilogue's end-of-book placement check and the
    /// expected-start-chapter a detected prologue implies - and a kind spelled out at each of them
    /// is a string nothing would catch when it changes.</summary>
    public const string PrologueKind = "prologue";

    /// <summary><see cref="Kind"/> of the built-in epilogue phrase; see <see cref="PrologueKind"/>
    /// for why it is named here.</summary>
    public const string EpilogueKind = "epilogue";

    /// <summary>Prefix of every <c>--custom</c> mapping's <see cref="Kind"/>, the rest being the
    /// mapping's 1-based position in the option. What tells a user's own mapping from the two
    /// built-in phrases, which is the distinction the epilogue's placement check turns on.</summary>
    public const string CustomKindPrefix = "custom ";

    /// <summary>Whether this phrase is one of the user's own <c>--custom</c> mappings rather than a
    /// built-in one.</summary>
    public bool IsCustom => Kind.StartsWith(CustomKindPrefix, StringComparison.Ordinal);

    /// <summary>Matches a <c>$1</c>, <c>$12</c> or <c>${name}</c> group reference in a title
    /// template. <c>$$</c> (the escape for a literal <c>$</c>) is not one, and neither is a lone
    /// <c>$</c> before an ordinary word - both are left to <see cref="Match.Result"/>, which reads
    /// them exactly as .NET's own <see cref="Regex.Replace(string, string)"/> does.</summary>
    private static readonly Regex GroupRefPattern =
        new(@"(?<!\$)\$(\d+|\{[^}]*\})", RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether <see cref="Title"/> references a capturing group at all. Checked once here rather
    /// than left to <see cref="Match.Result"/> per mark, because that method would also rewrite a
    /// title that merely happens to contain a dollar sign - <c>--prologue-title "Cost: $5"</c> is a
    /// perfectly ordinary title, and it must survive verbatim when the phrase has no group 5 to
    /// substitute.
    /// </summary>
    private bool TitleHasGroupRefs { get; } =
        GroupRefPattern.IsMatch(Title) && Regex.GetGroupNumbers().Length > 1;

    /// <summary>
    /// Recognizes titles this phrase has written before, for the resume/verify carry-over in
    /// <see cref="ABChapterize.Detection.ChapterDetector"/>: a rewrite of a file's whole marking set
    /// must not silently drop a numberless mark, and its written text is all there is to match on.
    /// Every group reference becomes a wildcard, since what it expanded to is exactly what varies
    /// between one file's marks and the next's.
    /// </summary>
    public Regex TitleMatcher { get; } = BuildTitleMatcher(Title);

    /// <summary>
    /// The title to write for one match: <see cref="Title"/> with its group references expanded, or
    /// verbatim when it has none. Group expansion follows .NET's own substitution syntax, so
    /// <c>$1</c>, <c>${name}</c> and <c>$$</c> all mean here what they mean in
    /// <see cref="Regex.Replace(string, string)"/>.
    /// </summary>
    /// <param name="match">The match this mark was produced from; must come from
    /// <see cref="Regex"/>, whose groups the references are resolved against.</param>
    public string ResolveTitle(Match match) => TitleHasGroupRefs ? match.Result(Title) : Title;

    /// <summary>
    /// Compiles the anchored, case-insensitive expression behind <see cref="TitleMatcher"/>. The
    /// template is split at its group references and only the literal pieces between them are
    /// escaped - escaping the whole string first would leave the references themselves escaped
    /// beyond recognition, since <see cref="Regex.Escape"/> rewrites both <c>$</c> and <c>{</c>.
    /// </summary>
    /// <param name="title">The raw title template.</param>
    private static Regex BuildTitleMatcher(string title)
    {
        var pattern = new StringBuilder("^");
        var literalFrom = 0;
        foreach (Match reference in GroupRefPattern.Matches(title))
        {
            pattern.Append(Regex.Escape(title[literalFrom..reference.Index])).Append(".+");
            literalFrom = reference.Index + reference.Length;
        }
        pattern.Append(Regex.Escape(title[literalFrom..])).Append('$');
        return new Regex(pattern.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// The capturing groups <paramref name="title"/> references, as written - <c>1</c> for
    /// <c>$1</c>, <c>name</c> for <c>${name}</c>. The command line layer validates these against
    /// the phrase's own groups before a <see cref="NamedPhrase"/> is ever built, so that a typo
    /// fails at parse time with a readable message instead of throwing out of
    /// <see cref="Match.Result"/> halfway through a book.
    /// </summary>
    /// <param name="title">The raw title template to scan.</param>
    public static IEnumerable<string> ReferencedGroups(string title)
        => GroupRefPattern.Matches(title)
            .Select(m => m.Groups[1].Value)
            .Select(g => g.StartsWith('{') ? g[1..^1] : g);
}
