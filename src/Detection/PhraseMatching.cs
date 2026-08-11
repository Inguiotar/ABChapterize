// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ABChapterize.Language;
using ABChapterize.Transcription;
using static ABChapterize.Language.NumberWordParser;

namespace ABChapterize.Detection;

/// <summary>Finds the chapter-announcement phrase (and its chapter number) inside a window's
/// transcribed segments.</summary>
internal static partial class PhraseMatching
{
    /// <summary>A phrase match inside a transcribed window.</summary>
    /// <param name="Number">Parsed chapter number.</param>
    /// <param name="PhraseStartSeconds">Phrase start relative to the window start.</param>
    /// <param name="PhraseEndSeconds">End of the transcript segment the phrase was found in,
    /// relative to the window start. Whisper can smear that segment across a whole jingle (its
    /// start pulled seconds before the words are spoken), so the span [start, end] - not the
    /// start alone - is what the smeared-phrase rescue in <see cref="JingleGeometry.ResolveJingleAnchor"/>
    /// matches against VAD regions.</param>
    /// <param name="Confidence">Whisper's probability for the segment the match was found in.</param>
    /// <param name="SpansMerge">True when the text actually used to find the phrase and parse its
    /// number straddles a Pass 2 overlap's cache/fresh boundary - see <see cref="FindPhraseMatches"/>'s
    /// <c>mergeBoundarySegIndex</c> parameter.</param>
    /// <param name="SpokenAlone">Whether the announcement opened the transcript segment it was
    /// found in. Only ever false under <c>--chapter-phrase none</c>'s wider reading
    /// (<see cref="NumberWordParser.BareNumberReading.LeadingASentence"/>), and carried because it is
    /// what such a match falls back on when <see cref="AnnouncementIsolation"/> cannot be run: an
    /// opening number is one the stricter reading would have taken anyway, a later one rests
    /// entirely on the isolation check and must not be kept without it.</param>
    internal readonly record struct PhraseMatch(
        int Number, double PhraseStartSeconds, double PhraseEndSeconds, double Confidence,
        bool SpansMerge = false, bool SpokenAlone = true);

    /// <summary>A non-numbered announcement (prologue, epilogue or a <c>--custom</c> mapping) found
    /// inside a transcribed window.</summary>
    /// <param name="Phrase">The phrase that matched.</param>
    /// <param name="Title">The title this particular match writes: the phrase's own template with
    /// its capturing-group references expanded (see <see cref="NamedPhrase.ResolveTitle"/>).
    /// Resolved here, while the <see cref="Match"/> is still around, rather than carried along as a
    /// match object nothing downstream has any other use for.</param>
    /// <param name="PhraseStartSeconds">Phrase start, in the time base <paramref name="Phrase"/> was
    /// matched in (window-relative for a Pass 2 probe).</param>
    /// <param name="PhraseEndSeconds">End of the transcript segment the phrase was found in, in the
    /// same time base - the smeared-segment span <see cref="JingleGeometry.ResolveJingleAnchor"/>
    /// needs, exactly as for <see cref="PhraseMatch.PhraseEndSeconds"/>.</param>
    /// <param name="Confidence">Whisper's probability for the segment the match was found in.</param>
    /// <param name="Text">The transcript segment the match was found in, whitespace-normalized -
    /// what a phrase other than this one would have had to match to claim the same announcement.
    /// Carried because a built-in epilogue dropped for sitting mid-book is offered to the
    /// <c>--custom</c> mappings before it is discarded, and by then the transcript is long gone (see
    /// <see cref="ChapterDetector.ResolveEpiloguePlacement"/>). The segment rather than the whole
    /// window: a window runs to half a minute of narration, and a mapping matching somewhere in
    /// <em>that</em> says nothing about this announcement.</param>
    internal readonly record struct NamedMatch(
        NamedPhrase Phrase, string Title,
        double PhraseStartSeconds, double PhraseEndSeconds, double Confidence, string Text = "");

    /// <summary>
    /// Searches the transcribed segments for the chapter phrase and parses the chapter number,
    /// either from the regexp capturing group or from the words following the phrase
    /// ("Chapter Seven"); when neither yields a number, the words directly preceding the
    /// phrase are tried ("Erstes Kapitel", "Birinci Bölüm").
    /// </summary>
    /// <param name="segments">The window's transcript segments, in window-relative time.</param>
    /// <param name="profile">Language profile supplying the chapter phrase and number parsing.</param>
    /// <param name="mergeBoundarySegIndex">For a window assembled by Pass 2's overlap reuse (see
    /// ProbeAsync), the index of the first segment that came from the fresh tail decode rather
    /// than the reused cache; null for a window that is entirely one or the other (a plain probe,
    /// a fully-reused window, a gap chunk, or a --verify window). Used only to flag
    /// <see cref="PhraseMatch.SpansMerge"/> - it does not affect which matches are found.</param>
    /// <param name="reading">How far into a transcript segment a <c>--chapter-phrase none</c>
    /// announcement may sit; ignored entirely for a phrase-based book. See
    /// <see cref="FindBareNumbers"/>.</param>
    internal static IEnumerable<PhraseMatch> FindPhraseMatches(
        List<TranscriptSegment> segments, LanguageProfile profile, int? mergeBoundarySegIndex = null,
        BareNumberReading reading = BareNumberReading.SpokenAloneAtSegmentStart)
    {
        if (segments.Count == 0)
            yield break;
        if (profile.BareNumberAnnouncements)
        {
            foreach (var match in FindBareNumbers(segments, profile, reading))
                yield return match;
            yield break;
        }

        var (text, segStartChar) = Flatten(segments);
        var mergeBoundaryChar = mergeBoundarySegIndex is { } idx && idx > 0 && idx < segments.Count
            ? segStartChar[idx] : (int?)null;

        foreach (Match m in profile.PhraseRegex.Matches(text))
        {
            if (!TryParseAnnouncedNumber(text, m, profile, out var number, out var consumedStart, out var consumedEnd))
                continue;

            var segIndex = SegmentIndexAt(segStartChar, m.Index);
            var spansMerge = mergeBoundaryChar is { } b && consumedStart < b && b < consumedEnd;
            yield return new PhraseMatch(
                number, segments[segIndex].StartSeconds, segments[segIndex].EndSeconds,
                segments[segIndex].Probability, spansMerge);
        }
    }

    /// <summary>
    /// The <c>--chapter-phrase none</c> reading of a window: a chapter is announced by speaking its
    /// number and nothing else, so what is looked for is a <em>sentence</em> that is a number and
    /// nothing besides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "And nothing else" is what keeps every year, price and house number in the prose out, and it
    /// is the sentence boundary - not Whisper's segmentation - that establishes it. Until
    /// 2026-08-05 the test was that the whole transcript <em>segment</em> be a number, on the
    /// reasoning that Whisper ends a segment where the speaking stops. It does not reliably: see
    /// <see cref="NumberWordParser.FindBareNumberAnnouncement"/> for the ten chapters that cost on
    /// one book, all of them glued to the first sentence of their own chapter.
    /// </para>
    /// <para>
    /// <paramref name="reading"/> then decides how far into a segment to look, and the two levels
    /// buy different things at different prices.
    /// <see cref="BareNumberReading.SpokenAloneAtSegmentStart"/> is what Pass 2's forward scan uses: it costs
    /// nothing over the old rule and recovers the glued announcements, while keeping a cheap
    /// second signal ("Whisper opened a segment here") for a scan that walks the whole book with no
    /// upper bound on the sequence. <see cref="BareNumberReading.LeadingASentence"/> drops even that and
    /// is reserved for the passes that hunt named missing numbers inside a bounded stretch, where
    /// the hole itself says which numbers may appear and
    /// <see cref="AnnouncementIsolation"/> vets the position afterwards. Turning the wider reading
    /// loose on the forward scan instead would mean paying a refinement for every number spoken in
    /// the prose of an entire book.
    /// </para>
    /// <para>
    /// Either way this mode leans on the chapter-number sequence far more heavily than the
    /// phrase-based one does - a lone "Seventeen." in dialogue is indistinguishable from an
    /// announcement by any amount of local evidence, and only its number being out of sequence
    /// rejects it. That is why <c>--ignore-chapter-numbers</c>, which switches that check off, is
    /// refused in combination with it.
    /// </para>
    /// </remarks>
    /// <param name="segments">The window's transcript segments, in the caller's time base.</param>
    /// <param name="profile">Language profile supplying the number grammar.</param>
    /// <param name="reading">How far into each segment to look.</param>
    private static IEnumerable<PhraseMatch> FindBareNumbers(
        List<TranscriptSegment> segments, LanguageProfile profile, BareNumberReading reading)
    {
        foreach (var segment in segments)
            if (NumberWordParser.FindBareNumberAnnouncement(
                    segment.Text, profile.Language, reading) is { } announced)
                // The segment's own span stays the match's time base even when the number sits
                // further in: it is the bracket the refinement searches, and it must contain the
                // announcement rather than start exactly on it.
                yield return new PhraseMatch(
                    announced.Number, segment.StartSeconds, segment.EndSeconds, segment.Probability,
                    SpokenAlone: announced.SpokenAlone);
    }

    /// <summary>
    /// A chapter announcement whose phrase matched but whose number could not be read - what
    /// <see cref="FindPhraseMatches"/> drops on the floor. Reported separately so a pass can say so
    /// in the log instead of leaving "the phrase was never heard" and "the phrase was heard and
    /// discarded" looking identical from outside.
    /// </summary>
    /// <param name="PhraseStartSeconds">Start of the segment the phrase was found in, in the
    /// caller's time base.</param>
    /// <param name="PhraseEndSeconds">End of that segment, in the same time base. Carried for the
    /// same reason <see cref="PhraseMatch.PhraseEndSeconds"/> is: it is what a mark placed on this
    /// announcement anchors against once a re-read supplies the missing number
    /// (<see cref="SuspectNumberMender.ReadUnnumberedAsync"/>).</param>
    /// <param name="Confidence">Whisper's probability for that segment, so a recovered mark reports
    /// the confidence of the reading it was actually found in rather than of the re-read that only
    /// contributed the number.</param>
    /// <param name="Text">The segment's text, trimmed to a length a log line can carry - the whole
    /// point of the report is seeing <em>what</em> the recognizer wrote there.</param>
    internal readonly record struct UnnumberedAnnouncement(
        double PhraseStartSeconds, double PhraseEndSeconds, double Confidence, string Text);

    /// <summary>
    /// The announcements <see cref="FindPhraseMatches"/> could not put a number to: the phrase
    /// itself matched, but neither the following words nor the preceding ones yielded one.
    /// <para>
    /// This is a genuinely different situation from a mishearing, and one worth reporting: the
    /// recognizer was right there and got the words, and only the notation defeated the parser.
    /// It found chapter 13 of "I Shall Wear Midnight" transcribed as "CHAPTER XIII" (2026-07-30),
    /// and it still covers what Roman-numeral support does not - a word ordinal past the range a
    /// language's parser covers ("capítulo vigésimo quinto"), a number above 999, or an unsupported
    /// language reached through a hand-written --chapter-phrase.
    /// </para>
    /// <para>
    /// In-text mentions ("the next chapter was harder") match here too, so callers report these
    /// only when the transcript yielded no numbered mark at all - see the call sites.
    /// </para>
    /// </summary>
    /// <param name="segments">The window's transcript segments, in the caller's time base.</param>
    /// <param name="profile">Language profile supplying the chapter phrase and number parsing.</param>
    internal static IEnumerable<UnnumberedAnnouncement> FindUnnumberedAnnouncements(
        List<TranscriptSegment> segments, LanguageProfile profile)
    {
        // With --chapter-phrase none there is no such thing: the number *is* the announcement, so an
        // announcement whose number could not be read was never recognized as one in the first place.
        if (segments.Count == 0 || profile.BareNumberAnnouncements)
            yield break;

        var (text, segStartChar) = Flatten(segments);
        foreach (Match m in profile.PhraseRegex.Matches(text))
        {
            if (TryParseAnnouncedNumber(text, m, profile, out _, out _, out _))
                continue;
            var segment = segments[SegmentIndexAt(segStartChar, m.Index)];
            yield return new UnnumberedAnnouncement(
                segment.StartSeconds, segment.EndSeconds, segment.Probability, Snippet(segment.Text));
        }
    }

    /// <summary>Shortens a transcript segment for a log line, keeping the head - which is where an
    /// announcement's number would have been.</summary>
    /// <param name="text">The segment text as the recognizer wrote it.</param>
    private static string Snippet(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= UnnumberedSnippetChars
            ? trimmed
            : trimmed[..UnnumberedSnippetChars] + "…";
    }

    /// <summary>How much of the offending segment an "unreadable number" log line carries. Long
    /// enough for a chapter heading and its title ("CHAPTER XIII. THE SHAKING OF THE SHEETS" is
    /// 39), short enough not to wrap a terminal when the segment is a whole sentence.</summary>
    private const int UnnumberedSnippetChars = 60;

    /// <summary>
    /// Reads the chapter number belonging to one phrase match, from the regexp's own capturing group
    /// if it has one, else from the words following the phrase ("Chapter Seven"), else from those
    /// preceding it ("Erstes Kapitel", "Birinci Bölüm"). The 80-character head/tail slices bound how
    /// far a number may sit from the phrase and still count as announced with it, keeping an
    /// unrelated number later in the same segment out.
    /// </summary>
    /// <param name="text">The flattened window text the match came from.</param>
    /// <param name="m">The phrase match itself.</param>
    /// <param name="profile">Language profile supplying the number grammar and group convention.</param>
    /// <param name="number">The parsed chapter number, when one was found.</param>
    /// <param name="consumedStart">Start of the character range actually consulted - the match
    /// itself, extended backwards when a preceding slice supplied the number.</param>
    /// <param name="consumedEnd">End of that range, extended forwards when a following slice
    /// supplied it. Together the two tell <see cref="FindPhraseMatches"/> whether the detection drew
    /// on text from both sides of a Pass 2 overlap's cache/fresh boundary.</param>
    private static bool TryParseAnnouncedNumber(
        string text, Match m, LanguageProfile profile,
        out int number, out int consumedStart, out int consumedEnd)
    {
        consumedStart = m.Index;
        consumedEnd = m.Index + m.Length;
        if (profile.PhraseHasNumberGroup && m.Groups.Count > 1 && m.Groups[1].Success)
            return int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number);

        var tail = text[(m.Index + m.Length)..];
        if (tail.Length > 80)
            tail = tail[..80];
        if (NumberWordParser.TryExtractNumber(tail, profile.Language, out number))
        {
            consumedEnd += tail.Length;
            return true;
        }

        var head = text[..m.Index];
        if (head.Length > 80)
            head = head[^80..];
        if (!NumberWordParser.TryExtractNumberBefore(head, profile.Language, out number))
            return false;
        consumedStart -= head.Length;
        return true;
    }

    /// <summary>
    /// Searches the transcribed segments for chapter announcements without requiring any of them to
    /// carry a number - what <c>--ignore-chapter-numbers</c> runs instead of
    /// <see cref="FindPhraseMatches"/>. A number that is spoken still reaches the title, because
    /// "Kapitel 3" is what the listener expects to see even when nothing checks that a 2 came before
    /// it; one that is not simply leaves the bare title word, which is the whole point for a book
    /// that announces its chapters without numbering them.
    /// </summary>
    /// <param name="segments">The window's transcript segments, in the caller's time base.</param>
    /// <param name="profile">Language profile supplying the chapter phrase, title and number grammar.</param>
    internal static IEnumerable<NamedMatch> FindChapterAnnouncements(
        List<TranscriptSegment> segments, LanguageProfile profile)
    {
        // Unreachable with --chapter-phrase none, which CliOptions refuses to combine with
        // --ignore-chapter-numbers - see FindBareNumbers for why that pairing has no safety net.
        if (segments.Count == 0 || profile.BareNumberAnnouncements)
            yield break;

        var (text, segStartChar) = Flatten(segments);
        foreach (Match m in profile.PhraseRegex.Matches(text))
        {
            var title = TryParseAnnouncedNumber(text, m, profile, out var number, out _, out _)
                ? $"{profile.Title} {number}"
                : profile.Title;
            var segment = segments[SegmentIndexAt(segStartChar, m.Index)];
            yield return new NamedMatch(
                profile.ChapterAnnouncement, title,
                segment.StartSeconds, segment.EndSeconds, segment.Probability,
                NormalizeWhitespace(segment.Text));
        }
    }

    /// <summary>
    /// Searches the transcribed segments for every non-numbered announcement the profile knows
    /// (prologue, epilogue, <c>--custom</c>). No number is parsed and none is required, so - unlike
    /// <see cref="FindPhraseMatches"/> - a phrase that matches is always a match; whether it may
    /// become a mark is decided by its <see cref="NamedPhrase.Scope"/> at the call site, which is
    /// the only place that knows how many chapters have been found so far.
    /// </summary>
    /// <param name="segments">The window's transcript segments, in whatever time base the caller
    /// works in (this method neither reads nor rewrites the timings).</param>
    /// <param name="profile">Language profile supplying <see cref="LanguageProfile.NamedPhrases"/>.</param>
    internal static IEnumerable<NamedMatch> FindNamedMatches(
        List<TranscriptSegment> segments, LanguageProfile profile)
    {
        if (segments.Count == 0 || profile.NamedPhrases.Count == 0)
            yield break;

        var (text, segStartChar) = Flatten(segments);
        foreach (var phrase in profile.NamedPhrases)
        {
            foreach (Match m in phrase.Regex.Matches(text))
            {
                var segment = segments[SegmentIndexAt(segStartChar, m.Index)];
                yield return new NamedMatch(
                    phrase, phrase.ResolveTitle(m),
                    segment.StartSeconds, segment.EndSeconds, segment.Probability,
                    NormalizeWhitespace(segment.Text));
            }
        }
    }

    /// <summary>
    /// Concatenates all segment texts into the single string the regexes run against, remembering
    /// where each segment starts in it so a match position can be mapped back to a time. The single
    /// space between two segments is what keeps them from fusing into a spurious word across their
    /// join - and, since every text is normalized first, it really is a single space.
    /// </summary>
    /// <param name="segments">The transcript segments, in order.</param>
    private static (string Text, int[] SegStartChar) Flatten(List<TranscriptSegment> segments)
    {
        var sb = new StringBuilder();
        var segStartChar = new int[segments.Count];
        for (var i = 0; i < segments.Count; i++)
        {
            segStartChar[i] = sb.Length;
            var text = NormalizeWhitespace(segments[i].Text);
            if (text.Length == 0)
                continue;
            sb.Append(text);
            sb.Append(' ');
        }
        return (sb.ToString(), segStartChar);
    }

    /// <summary>Collapses every run of whitespace to one space and trims the ends, so that what a
    /// phrase regex is matched against is spaced the way the phrase is written.</summary>
    /// <remarks>
    /// Whisper prefixes a space to every segment it returns. <see cref="Flatten"/> then separated
    /// them with a space of its own, so each join carried <em>two</em>, and any phrase containing a
    /// space - which the built-in ones never do, being single words, and a user's --chapter-phrase
    /// routinely does - could not match across a segment boundary. Found on "Paula Monti.m4b" with
    /// <c>--chapter-phrase "[fr]/(?:premi|1).re partie.? chapitre/"</c> (2026-08-08): Pass 3 heard
    /// chapter 19 perfectly and split it as "Première partie." + "Chapitre 19.", which joined into
    /// two spaces where the phrase writes one, and the chapter was silently lost. Normalizing per
    /// segment rather than over the finished string is what keeps <see cref="Flatten"/>'s offsets
    /// pointing at the right segment.
    /// </remarks>
    /// <param name="text">One transcript segment's text, as the recognizer wrote it.</param>
    internal static string NormalizeWhitespace(string? text)
        => text is null ? "" : WhitespaceRuns().Replace(text, " ").Trim();

    /// <summary>Every run of one or more whitespace characters.</summary>
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRuns();

    /// <summary>The index of the segment a character position in <see cref="Flatten"/>'s text
    /// belongs to: the last segment starting at or before it.</summary>
    /// <param name="segStartChar">Per-segment start offsets from <see cref="Flatten"/>.</param>
    /// <param name="charIndex">The character position to locate.</param>
    private static int SegmentIndexAt(int[] segStartChar, int charIndex)
    {
        var segIndex = 0;
        for (var i = 0; i < segStartChar.Length; i++)
        {
            if (segStartChar[i] <= charIndex)
                segIndex = i;
            else
                break;
        }
        return segIndex;
    }
}
