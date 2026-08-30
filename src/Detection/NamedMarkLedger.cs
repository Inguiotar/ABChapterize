// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Language;
using static ABChapterize.Detection.DetectionFormatting;
using static ABChapterize.Detection.DetectionTuning;
using static ABChapterize.Detection.PhraseMatching;

namespace ABChapterize.Detection;

/// <summary>
/// The named marks found in one file, and every rule about whether a further one may join them:
/// scope, the two dedupe passes, a mapping's own <c>max=&lt;n&gt;</c> cap and the file-wide
/// <c>--custom</c> cap. Constructed per file and shared by Probe and Scan, so the two passes cannot
/// come to different answers about the same announcement.
/// <para>
/// The split here is the one <see cref="MarkPlacer"/> already draws: the <em>rules</em> are shared,
/// the <em>mechanics</em> are not. Where a named mark ends up is a per-pass question - Probe has a
/// candidate and its jingle anchor to resolve against, Scan has neither and works from the phrase
/// onset like its chapter path does - so placement stays in the passes. What may become a mark at
/// all does not vary by pass, and a second copy of it is how a <c>--custom</c> cap ends up meaning
/// one thing in Probe and another in Scan.
/// </para>
/// <para>
/// Wraps the caller's list rather than owning it outright: the same <see cref="DetectedMark"/> list
/// is what <see cref="ChapterDetector"/> hands to both passes and reads back at result-build time,
/// and a ledger that copied it would leave two answers to "what did this file find".
/// </para>
/// </summary>
/// <remarks>Notes: why Scan was blind to named marks for so long, and the 13-of-14 measurement that
/// ended it.
/// <include file='../../notes/Detection/NamedMarkLedger.xml' path='doc/member[@name="NamedMarkLedger"]/*' /></remarks>
internal sealed class NamedMarkLedger(List<DetectedMark> marks)
{
    /// <summary>Phrase kinds whose out-of-scope matches have already been reported, so the note is
    /// written once per phrase rather than once per occurrence - see <see cref="IsInScope"/>.</summary>
    private readonly HashSet<string> _scopeDropsNoted = [];

    /// <summary>The file's named marks, in the order they were accepted. The same list both passes
    /// append to and <see cref="ChapterDetector"/> reads back.</summary>
    internal List<DetectedMark> Marks { get; } = marks;

    /// <summary>Whether the file-wide <c>--custom</c> cap was reached, which is reported out to the
    /// file's summary line rather than only logged.</summary>
    internal bool CustomLimitHit { get; private set; }

    /// <summary>
    /// Whether a named phrase may become a mark at this point of the file, judged purely by how many
    /// chapters are known so far - see <see cref="NamedPhraseScope"/> for why that is the only usable
    /// landmark, and why <see cref="NamedPhraseScope.AfterLastChapter"/> can only be pre-filtered
    /// here and has to be applied properly at the end of the run
    /// (<see cref="ChapterDetector.DropOutOfScopeNamedMarks"/>).
    /// <para>
    /// A rejection is noted once per phrase, not once per occurrence: "epilogue" turning up in the
    /// middle of a book is an ordinary word in ordinary prose, and one line per match would drown the
    /// log - but a mapping the user scoped by hand and then never sees a mark from is a support
    /// question, so the fact that its matches are being dropped has to be visible somewhere.
    /// </para>
    /// </summary>
    /// <param name="phrase">The phrase that matched.</param>
    /// <param name="phraseAbs">Absolute time the announcement was heard at, for the note.</param>
    /// <param name="chaptersBefore">How many chapters are known to sit before
    /// <paramref name="phraseAbs"/>. Supplied by the caller rather than counted here, because the
    /// two passes count it from different lists: Probe from the chapters of the run so far (or, under
    /// --ignore-chapter-numbers, from the chapter announcements among the named marks), Scan from
    /// everything already known plus what the region itself has found.</param>
    /// <param name="log">Sink for the once-per-phrase note, or null when not verbose.</param>
    internal bool IsInScope(
        NamedPhrase phrase, double phraseAbs, int chaptersBefore, Action<string>? log)
    {
        var inScope = phrase.Scope switch
        {
            NamedPhraseScope.Anywhere => true,
            NamedPhraseScope.BeforeFirstChapter => chaptersBefore == 0,
            _ => chaptersBefore > 0,
        };
        if (!inScope && log != null && _scopeDropsNoted.Add(phrase.Kind))
            log($"{phrase.Kind} heard at {FormatTimestamp(phraseAbs)}, outside the " +
                $"\"{ScopeName(phrase.Scope)}\" position it is restricted to - not marked " +
                "(reported once per phrase)");
        return inScope;
    }

    /// <summary>The keyword a scope is written as, for the log line above - the same word the user
    /// typed, so that a note about a dropped match names something they can look up.</summary>
    /// <param name="scope">The scope to name.</param>
    private static string ScopeName(NamedPhraseScope scope) => scope switch
    {
        NamedPhraseScope.BeforeFirstChapter => "before-first-chapter",
        NamedPhraseScope.AfterFirstChapter => "after-first-chapter",
        NamedPhraseScope.AfterLastChapter => "after-last-chapter",
        _ => "anywhere",
    };

    /// <summary>
    /// Whether an in-scope named match is to be passed over without becoming a mark. The reasons are
    /// all specific to a phrase that takes no part in the chapter sequence and so has nothing to be
    /// judged against:
    /// <list type="bullet">
    /// <item><description>the same announcement was already marked - overlapping probe windows
    /// re-decode the same audio routinely, and a Scan chunk re-reads audio Probe has already been
    /// over, so without this every such overlap would yield a duplicate mark a second or two from
    /// the first (see <see cref="DetectionTuning.NamedMarkDedupeSeconds"/>);</description></item>
    /// <item><description>a non-repeatable phrase already holds a mark from an announcement
    /// <em>later</em> in the file - see <see cref="RegionProber.AcceptNamedMatchAsync"/> for why the
    /// last announcement wins and why "last" cannot be read as "most recently
    /// found";</description></item>
    /// <item><description>this mapping has reached its own <c>max=&lt;n&gt;</c> cap (see
    /// <see cref="NamedPhrase.MaxMarks"/>), which unlike the file-wide one below is something the
    /// user stated about this phrase and so is reported as an ordinary log line rather than as a
    /// file-level warning;</description></item>
    /// <item><description>the file has reached its --custom mark cap (see
    /// <see cref="DetectionTuning.MaxCustomMarksPerFile"/>), which is reported all the way out to the
    /// file's summary line rather than only logged. Chapter announcements are exempt: under
    /// --ignore-chapter-numbers they arrive through this same path, and a cap sized for structural
    /// interludes would cut an omnibus off partway through.</description></item>
    /// </list>
    /// </summary>
    /// <param name="phrase">The phrase that matched.</param>
    /// <param name="phraseAbs">Absolute time the announcement was heard at.</param>
    /// <param name="profile">The file's language profile, for the chapter announcement's own kind -
    /// the one exempt from the file-wide cap.</param>
    /// <param name="log">Sink for the two notes, or null when not verbose.</param>
    internal bool ShouldDrop(
        NamedPhrase phrase, double phraseAbs, LanguageProfile profile, Action<string>? log)
    {
        if (Marks.Any(m => m.Kind == phrase.Kind &&
                           Math.Abs(m.PhraseTimeSeconds - phraseAbs) < NamedMarkDedupeSeconds))
            return true;

        if (!phrase.Repeatable)
            return Marks.Any(m => m.Kind == phrase.Kind && m.PhraseTimeSeconds > phraseAbs);

        var chapterKind = profile.ChapterAnnouncement.Kind;
        if (phrase.Kind == chapterKind)
            return false;

        if (phrase.MaxMarks is { } cap && Marks.Count(m => m.Kind == phrase.Kind) >= cap)
        {
            log?.Invoke(
                $"skipped {phrase.Kind} at {FormatTimestamp(phraseAbs)} - this mapping's own " +
                $"limit of {cap} mark(s) is reached");
            return true;
        }

        if (Marks.Count(m => m.Repeatable && m.Kind != chapterKind) < MaxCustomMarksPerFile)
            return false;
        if (!CustomLimitHit)
            log?.Invoke($"WARNING - custom mark limit of {MaxCustomMarksPerFile} reached at " +
                        $"{FormatTimestamp(phraseAbs)} - further --custom matches are ignored " +
                        "for this file. Does the mapping match ordinary prose?");
        CustomLimitHit = true;
        return true;
    }

    /// <summary>
    /// The second dedupe pass, against the <em>placed</em> time rather than the phrase time
    /// <see cref="ShouldDrop"/> compares. The two are not redundant: the pre-placement one compares
    /// where the announcement was heard, which two reads of the same announcement can easily
    /// disagree about by more than the dedupe window, since a re-decode is re-segmented by Whisper
    /// from scratch and the same words can land in a segment starting seconds apart. Once both have
    /// been walked back to their anchor they coincide exactly, and that is the only reliable moment
    /// to notice.
    /// </summary>
    /// <param name="kind">The phrase kind being placed.</param>
    /// <param name="time">The mark time placement settled on.</param>
    internal bool AlreadyPlacedAt(string kind, double time)
        => Marks.Any(m => m.Kind == kind && Math.Abs(m.TimeSeconds - time) < NamedMarkDedupeSeconds);

    /// <summary>
    /// Records an accepted named mark, replacing any earlier one of a non-repeatable phrase - which
    /// is what makes the last announcement in the file the one that wins for a prologue or an
    /// epilogue.
    /// </summary>
    /// <param name="match">The match being recorded, for its kind, title, confidence and text.</param>
    /// <param name="time">Where the mark goes.</param>
    /// <param name="phraseAbs">Where the announcement itself was heard.</param>
    internal void Add(NamedMatch match, double time, double phraseAbs)
    {
        if (!match.Phrase.Repeatable)
            Marks.RemoveAll(m => m.Kind == match.Phrase.Kind);
        Marks.Add(new DetectedMark(
            match.Phrase.Kind, match.Title, time, match.Confidence, phraseAbs,
            match.Phrase.Repeatable, match.Text));
    }
}
