// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using ABChapterize.Cli;
using ABChapterize.Transcription;
using ABChapterize.Vad;
using static ABChapterize.Detection.DetectionTuning;
using static ABChapterize.Detection.GapPlanning;

namespace ABChapterize.Detection;

/// <summary>
/// A gap between two consecutive <see cref="SpeechSegment"/>s found by the VAD pre-pass: a region
/// VAD considers non-speech, flanked by speech on both sides. A silence-less jingle transition
/// shows up as one of these, music reading as non-speech to a speech detector just as silence
/// does. Deliberately excludes non-speech at the very start/end of the file - the synthetic
/// file-start candidate (Start = 0) already covers the jingle-before-chapter-1 edge case.
/// </summary>
/// <param name="StartSeconds">Where VAD stopped detecting speech.</param>
/// <param name="EndSeconds">Where VAD resumed detecting speech.</param>
internal readonly record struct NonSpeechRegion(double StartSeconds, double EndSeconds);

/// <summary>
/// One decoded window's transcript in absolute file time, carried together with the span of audio
/// it was actually decoded from. The span cannot be recovered from the segments: Whisper reports
/// nothing at all for a stretch it heard no words in, and
/// <see cref="JingleGeometry.TrimLeadingNonSpeech"/> then moves the first segment's start forward
/// past any leading silence/jingle - so on exactly the windows this matters for (one opening on a
/// jingle, the announcement its only speech) the earliest segment timestamp can sit most of a
/// jingle's length after the window's true start. <see cref="JingleGeometry.IsGenuineSpeech"/>
/// distinguishes "the transcript covers this moment and heard nothing" from "the transcript does
/// not reach back this far", and only the recorded span can tell the two apart.
/// </summary>
/// <param name="Segments">The window's transcript, shifted to absolute file time and already
/// leading-non-speech-corrected.</param>
/// <param name="StartSeconds">Absolute start of the audio that was decoded.</param>
/// <param name="EndSeconds">Absolute end of that audio - the window's own planned end, not the
/// last segment's timestamp.</param>
internal readonly record struct TranscriptWindow(
    List<TranscriptSegment> Segments, double StartSeconds, double EndSeconds);

/// <summary>Locates and resolves VAD non-speech regions (jingles) and silence-based anchors
/// around a chapter phrase: computing the regions themselves from raw VAD speech segments,
/// matching a region to its phrase, and deriving where the chapter mark and the various
/// --min-silence-length/--max-jingle-length auto statistics should anchor.</summary>
internal static class JingleGeometry
{
    /// <summary>
    /// Inverts consecutive VAD speech segments into the non-speech gaps between them, then cleans
    /// up two things Silero VAD is not reliable about on jingle music: a "speech" blip shorter
    /// than <see cref="MergeShortSpeechGapSeconds"/> (a vocal-like transient or rhythmic passage
    /// in otherwise instrumental music) does not end a jingle - the regions on either side are
    /// merged instead of fragmenting one jingle into several too-short ones; and a region whose
    /// longest single <em>contiguous</em> non-speech run falls short of
    /// <see cref="MinJingleObservationSeconds"/> is dropped.
    /// <para>
    /// That floor deliberately measures the longest contiguous run, not the merged span: a jingle
    /// contains one genuinely long unbroken music block (still long between brief misfires),
    /// whereas narration cadence produces only short inter-word pauses - individually well under
    /// the floor, but able to chain-merge across the equally short speech between them into a span
    /// that clears it. Measuring the span would resurface exactly the breath-pause false positives
    /// the floor exists to suppress.
    /// </para>
    /// Internal for unit testing.
    /// </summary>
    internal static List<NonSpeechRegion> ComputeNonSpeechRegions(List<SpeechSegment> speech)
    {
        var merged = new List<NonSpeechRegion>();
        // Longest raw gap each merged region was built from, parallel by index - kept alongside
        // rather than on NonSpeechRegion so the record stays purely (Start, End) for equality.
        var longestRun = new List<double>();
        for (var i = 1; i < speech.Count; i++)
        {
            var start = speech[i - 1].EndSeconds;
            var end = speech[i].StartSeconds;
            var run = end - start;
            if (merged.Count > 0 && start - merged[^1].EndSeconds < MergeShortSpeechGapSeconds)
            {
                merged[^1] = merged[^1] with { EndSeconds = end };
                longestRun[^1] = Math.Max(longestRun[^1], run);
            }
            else
            {
                merged.Add(new NonSpeechRegion(start, end));
                longestRun.Add(run);
            }
        }
        return merged.Where((_, i) => longestRun[i] >= MinJingleObservationSeconds).ToList();
    }

    /// <summary>
    /// The silencedetect silence that leads a VAD non-speech region - the low-amplitude part
    /// before the jingle's music starts, whose end lies inside the region - or null when the
    /// region has none (a silence-less jingle, or an in-text pause ending before the region rather
    /// than inside it). Picks the earliest-ending silence when several overlap the leading edge.
    /// This geometry is what distinguishes a genuine "silence then jingle" transition from a false
    /// in-text pause that merely triggered a probe.
    /// <para>
    /// The candidate must also <em>start</em> within
    /// <see cref="LeadingSilenceStartToleranceSeconds"/> of the region's own start, not merely end
    /// somewhere inside it: a genuine lead-in hush and its region begin at the same moment (VAD
    /// stops seeing speech right as the hush starts), so their starts line up to within detector
    /// jitter however long the hush runs. Without the check, a long region whose music never dips
    /// below the noise floor - except for a breath pause right before the announcement, deep
    /// inside it - would have that unrelated pause mistaken for the lead-in, marking just before
    /// the phrase instead of at the jingle start (seen on real audio: regions running 5-15 s
    /// before the only silence in them).
    /// </para>
    /// <para>
    /// For the same reason no VAD speech blip may sit between the region's start and the silence's
    /// start: the hush abuts the previous narration directly, so a blip in between means the
    /// silence follows some other sound (the jingle's opening sting, say) rather than leading the
    /// region, and anchoring to it would cut that opening into the previous chapter.
    /// </para>
    /// </summary>
    internal static Silence? LeadingSilence(
        NonSpeechRegion region, List<Silence> silences, List<SpeechSegment> speech)
        => silences
            .Where(s => s.EndSeconds > region.StartSeconds && s.EndSeconds <= region.EndSeconds
                     && s.StartSeconds <= region.StartSeconds + LeadingSilenceStartToleranceSeconds
                     && !speech.Any(b => b.StartSeconds > region.StartSeconds && b.StartSeconds < s.StartSeconds))
            .OrderBy(s => s.EndSeconds)
            .Cast<Silence?>()
            .FirstOrDefault();

    /// <summary>
    /// The true start of the jingle within a VAD non-speech region: the end of a
    /// <see cref="LeadingSilence"/> (when present), or the region's own start when no such
    /// silence exists - see "Why both detectors are required" in the design notes.
    /// </summary>
    internal static double JingleStart(
        NonSpeechRegion region, List<Silence> silences, List<SpeechSegment> speech)
        => LeadingSilence(region, silences, speech)?.EndSeconds ?? region.StartSeconds;

    /// <summary>
    /// Resolves the jingle/silence anchor for a matched phrase, independent of whichever silence
    /// happened to trigger the probe. Used whenever the VAD pre-pass ran: to place the mark with
    /// --mark-before-jingle, and - regardless of that option - to feed the
    /// --min-silence-length/--max-jingle-length auto mechanisms and the per-file statistics. The
    /// jingle is the VAD non-speech region the phrase belongs to (see
    /// <see cref="FindJingleRegionForPhrase"/>, by containment, so an announcement spoken
    /// <em>inside</em> the jingle does not lose the region); a silence is accepted as the anchor
    /// <em>only</em> when it <see cref="LeadingSilence">leads that region</see> - the classic
    /// "silence then jingle" transition, where --mark-before-jingle marks 0.5 s before the
    /// silence. Without a leading silence the region itself is the anchor and the mark goes at the
    /// jingle start with no lead.
    /// <para>
    /// A false in-text pause earlier in the previous chapter does <em>not</em> lead the jingle
    /// region, so it is never mistaken for the anchor even when it is the candidate that triggered
    /// the probe - which keeps a silence-less jingle from being marked at the pause and keeps the
    /// pause's length out of the auto mechanisms. Only when VAD found no region near the phrase at
    /// all (VAD off, or a transition with neither jingle nor VAD-registered silence) does this
    /// fall back to the nearest preceding silence.
    /// </para>
    /// </summary>
    /// <param name="phraseAbs">Absolute phrase start time.</param>
    /// <param name="phraseEndAbs">Absolute end of the transcript segment the phrase was found
    /// in, for the smeared-phrase rescue (see <see cref="FindSmearedJingleRegion"/>).</param>
    /// <param name="earliestAnchor">Earliest time an anchor may lie at: the probe window start
    /// (Pass 2) or <c>phraseAbs - lookback</c> (Pass 3).</param>
    /// <param name="silences">Every silence Pass 1 stored, down to
    /// <see cref="MinStoredSilenceSeconds"/> - even a sub-threshold silence leading the jingle
    /// is the more accurate anchor (and jingle-length reference) than the region alone.</param>
    /// <param name="nonSpeechRegions">All VAD non-speech regions (empty when VAD is off).</param>
    /// <param name="candidateVadRegion">The region a VAD candidate carries, if this probe was
    /// triggered by one; used directly instead of re-deriving it. Null for silence candidates
    /// and for Pass 3.</param>
    /// <param name="speech">The raw VAD speech segments behind the regions, for the jingle edge
    /// adjustment and the leading-silence blip gate.</param>
    /// <param name="transcriptAbs">The window's transcript in absolute file time (untrimmed), so
    /// the edge adjustment can tell trailing narration from mid-jingle music vocals.</param>
    /// <returns><c>AnchorSilence</c>: the silence leading the jingle (or, with no jingle region,
    /// the silence directly preceding the phrase), or null for a silence-less jingle.
    /// <c>VadRegion</c>: the jingle region the phrase belongs to, its start already corrected by
    /// <see cref="AdjustJingleRegion"/> so callers can use it for the auto statistics and (via
    /// <see cref="ResolveDefaultPhraseOnset"/>) for default-mode mark placement directly, or null
    /// when none was found. Both are returned together for the "silence then jingle" shape, so the
    /// jingle can be measured whichever of the two <see cref="ComputeMarkBeforeJingle"/> walks
    /// back to.</returns>
    internal static (Silence? AnchorSilence, NonSpeechRegion? VadRegion) ResolveJingleAnchor(
        double phraseAbs, double phraseEndAbs, double earliestAnchor, List<Silence> silences,
        List<NonSpeechRegion> nonSpeechRegions, NonSpeechRegion? candidateVadRegion,
        List<SpeechSegment> speech, List<TranscriptSegment> transcriptAbs)
    {
        var jingleRegion = candidateVadRegion
            ?? FindJingleRegionForPhrase(earliestAnchor, phraseAbs, nonSpeechRegions)
            ?? FindSmearedJingleRegion(earliestAnchor, phraseAbs, phraseEndAbs, nonSpeechRegions);
        if (jingleRegion is { } jr)
        {
            var adjusted = AdjustJingleRegion(jr, nonSpeechRegions, speech, transcriptAbs, phraseAbs);
            return (LeadingSilence(adjusted, silences, speech), adjusted);
        }
        return (FindRealAnchorSilence(earliestAnchor, phraseAbs, silences), null);
    }

    /// <summary>
    /// Whether a VAD speech blip at the leading edge of a jingle is a fragment of the previous
    /// chapter's <em>trailing narration</em>, as opposed to a vocal-like transient in the
    /// jingle's own music: narration exactly when Whisper transcribed words over it that end
    /// before <paramref name="narrationBound"/>. This rests on the only real speech <em>inside</em>
    /// a jingle being the announcement itself - so transcribed non-phrase words over a blip mean
    /// narration, and an untranscribed blip means music (Whisper does not silently skip genuine
    /// narration). The phrase's own segment never qualifies, ending after the bound.
    /// </summary>
    /// <param name="blip">The VAD speech segment to classify.</param>
    /// <param name="transcriptAbs">The window's transcript in absolute file time.</param>
    /// <param name="narrationBound">Latest a narration segment may end: the phrase start, or
    /// just past the region start when the phrase timestamp is known to lie even earlier (the
    /// smeared-phrase case) - see <see cref="AdjustJingleRegion"/>.</param>
    internal static bool IsTrailingNarrationBlip(
        SpeechSegment blip, List<TranscriptSegment> transcriptAbs, double narrationBound)
        => transcriptAbs.Any(t => !string.IsNullOrWhiteSpace(t.Text)
                                  && t.EndSeconds <= narrationBound
                                  && t.StartSeconds < blip.EndSeconds
                                  && t.EndSeconds > blip.StartSeconds);

    /// <summary>
    /// Corrects the leading edge of the jingle region a mark is about to anchor to, using the
    /// transcript to arbitrate what the two blind detectors cannot. Two symmetric defects of
    /// <see cref="ComputeNonSpeechRegions"/>'s fixed 1 s speech-gap merge are undone here, where
    /// the transcript is finally available:
    /// <list type="bullet">
    /// <item><b>Swallowed trailing narration:</b> a short final sentence of the previous chapter
    /// ("Dann war nichts mehr.") that VAD chopped into sub-second fragments gets merged into the
    /// region's head, dragging its start back into speech. Each leading blip overlapping
    /// transcribed narration (see <see cref="IsTrailingNarrationBlip"/>) moves the jingle start
    /// past it.</item>
    /// <item><b>Split jingle:</b> a vocal-like transient just over the merge limit splits one
    /// jingle into two regions, so a mark at the selected region's start lands mid-jingle. When
    /// another region ends within <see cref="JingleGlueMaxSeconds"/> before the (possibly
    /// just-trimmed) start with no transcribed narration in between - an untranscribed blip there
    /// is music - the jingle extends back to that region's start, repeatedly if it was split more
    /// than once. Trimmed narration blocks the bridge by itself: the trim leaves it inside the gap
    /// the bridge would have to cross.</item>
    /// </list>
    /// Only the start moves; the end (irrelevant to mark placement, and clipped at the phrase
    /// wherever lengths are measured) stays as merged.
    /// </summary>
    /// <param name="region">The jingle region selected for the phrase.</param>
    /// <param name="nonSpeechRegions">All VAD non-speech regions, chronological.</param>
    /// <param name="speech">The raw VAD speech segments behind the regions.</param>
    /// <param name="transcriptAbs">The window's transcript in absolute file time.</param>
    /// <param name="phraseAbs">Absolute phrase start time.</param>
    internal static NonSpeechRegion AdjustJingleRegion(
        NonSpeechRegion region, List<NonSpeechRegion> nonSpeechRegions,
        List<SpeechSegment> speech, List<TranscriptSegment> transcriptAbs, double phraseAbs)
    {
        // Narration must end by the phrase - except when the phrase timestamp lies before the
        // region (the smeared-phrase rescue selected it), where "just past the region start" is
        // the honest bound: Whisper's segment ends overhang real speech by about the same jitter
        // the leading-silence proximity check absorbs.
        var narrationBound = Math.Max(phraseAbs, region.StartSeconds + LeadingSilenceStartToleranceSeconds);

        var start = region.StartSeconds;
        foreach (var blip in speech)
        {
            if (blip.StartSeconds <= region.StartSeconds || blip.EndSeconds >= region.EndSeconds)
                continue;
            // Only blips near the current start are trimmed - deeper ones are past the jingle's
            // onset (the announcement itself, say) - and never across the phrase.
            if (blip.StartSeconds - start > JingleGlueMaxSeconds || blip.EndSeconds >= phraseAbs)
                break;
            if (!IsTrailingNarrationBlip(blip, transcriptAbs, narrationBound))
                break;
            start = blip.EndSeconds;
        }

        // Bridge backward across untranscribed music vocals to earlier fragments of the same
        // jingle. nonSpeechRegions is chronological, so the last region ending at or before the
        // current start is the bridge candidate.
        while (true)
        {
            NonSpeechRegion? previous = null;
            foreach (var r in nonSpeechRegions)
                if (r.EndSeconds <= start)
                    previous = r;
                else
                    break;
            if (previous is not { } prev)
                break;
            var gap = start - prev.EndSeconds;
            if (gap <= 0 || gap > JingleGlueMaxSeconds)
                break;
            var narrationInGap = transcriptAbs.Any(t => !string.IsNullOrWhiteSpace(t.Text)
                                                        && t.EndSeconds <= narrationBound
                                                        && t.StartSeconds < start
                                                        && t.EndSeconds > prev.EndSeconds);
            if (narrationInGap)
                break;
            start = prev.StartSeconds;
        }

        return start == region.StartSeconds ? region : region with { StartSeconds = start };
    }

    /// <summary>
    /// Rescue lookup for the jingle region when plain containment (<see
    /// cref="FindJingleRegionForPhrase"/>) finds nothing because Whisper timestamped the phrase
    /// <em>before</em> the region even starts: with a long silence/jingle between the last
    /// narration and the announcement, Whisper sometimes smears the phrase's segment across the
    /// whole jingle, its start pulled back to the end of the narration. The segment's span betrays
    /// this - it then overlaps the jingle region by many seconds - so the last region overlapping
    /// [phrase start, phrase segment end] by at least
    /// <see cref="SmearedPhraseMinOverlapSeconds"/> is accepted as the jingle. A correctly timed
    /// announcement's segment at most grazes a following pause region, well under the threshold.
    /// </summary>
    /// <param name="windowStart">Earliest a qualifying region may end, as in
    /// <see cref="FindJingleRegionForPhrase"/>.</param>
    /// <param name="phraseAbsSeconds">Absolute phrase start (the segment start).</param>
    /// <param name="phraseEndAbsSeconds">Absolute end of the phrase's transcript segment.</param>
    /// <param name="regions">All VAD non-speech regions, chronological.</param>
    internal static NonSpeechRegion? FindSmearedJingleRegion(
        double windowStart, double phraseAbsSeconds, double phraseEndAbsSeconds,
        List<NonSpeechRegion> regions)
    {
        NonSpeechRegion? found = null;
        foreach (var r in regions)
        {
            var overlap = Math.Min(r.EndSeconds, phraseEndAbsSeconds) - Math.Max(r.StartSeconds, phraseAbsSeconds);
            if (r.EndSeconds > windowStart && overlap >= SmearedPhraseMinOverlapSeconds)
                found = r;
        }
        return found;
    }

    /// <summary>
    /// Computes --mark-before-jingle's final mark by walking backward from <paramref
    /// name="originalMark"/> - default-mode placement's result (<see cref="RefineDefaultMark"/>/
    /// <see cref="ResolveDefaultPhraseOnset"/>, already corrected by precise marking unless
    /// --quick-marks turned that off) - to the true edge of whatever jingle precedes the
    /// announcement, by retracing the audio with the same two detectors used everywhere else in
    /// this file rather than picking from a short list of pre-resolved shapes (preceding silence,
    /// else VAD region start, else flat lead) as the placement this replaces did. That
    /// independence from whichever silence/region a probe resolved is also what makes this
    /// compatible with precise marking, which the older rule was not: it corrects the mark precise
    /// marking settled on instead of replacing default-mode placement outright.
    /// <para>
    /// <b>Step 1:</b> back out of any silence <paramref name="originalMark"/> itself sits in - a
    /// leading hush before the phrase, jingle or not.
    /// </para>
    /// <para>
    /// <b>Step 2:</b> if real (<see cref="TransientSpeechFloorSeconds"/>-or-longer) VAD speech
    /// covers that point - or ends within <see cref="JingleWalkAdjacencyToleranceSeconds"/> of it,
    /// absorbing silencedetect/VAD boundary jitter - the previous chapter's narration led straight
    /// into an ordinary pause with no jingle at all, and <paramref name="originalMark"/> is
    /// returned unchanged.
    /// </para>
    /// <para>
    /// <b>Steps 3-4:</b> otherwise keep retreating, now through the jingle's music, via
    /// <see cref="RetreatPastNonSpeech"/>. Two stop conditions are checked at every step and
    /// whichever lies nearer wins; the walk never continues past it:
    /// <list type="bullet">
    /// <item>Any stored silence, unqualified - <see cref="MinStoredSilenceSeconds"/> already floors
    /// every stored interval well above <see cref="TransientSpeechFloorSeconds"/>, so there is no
    /// separate floor to apply. Silencedetect never reads jingle music as silence, so a silence met
    /// while retreating through what VAD calls one unbroken non-speech run is a real break in the
    /// music, and the walk stops at its end. Whether that break is the hush between the previous
    /// chapter's trailing narration and the jingle (the relationship
    /// <see cref="LeadingSilence"/> anchors default-mode placement to) or the gap between two
    /// <em>separate</em> jingles - a preceding outro sting, or the book's title theme before
    /// chapter 1 - is deliberately not judged: either way the music after the silence is this
    /// chapter's jingle and its start is where the mark belongs. The distinction is futile anyway,
    /// since the separating silence's length does not carry it: real audio has a 3.28 s
    /// inter-jingle gap in one chapter and a 1.70 s one in another whose pre-announcement hush is
    /// a longer 2.45 s.</item>
    /// <item>Real VAD speech as step 2 defines it, <em>plus</em> transcript corroboration (see
    /// <see cref="IsGenuineSpeech"/>). Corroboration matters specifically here: a musical or
    /// vocal-like transient inside the music can run well past the floor - longer than any
    /// transient the floor was calibrated against - so duration alone is not enough this deep in,
    /// unlike at the announcement's own edge where step 2 operates. A blip failing corroboration is
    /// walked straight through, exactly like a too-short one.</item>
    /// </list>
    /// The first point meeting either condition is the jingle's true leading edge - where the
    /// previous chapter's trailing narration ends - and is returned with no further lead: unlike
    /// step 2's case a real jingle sits here, and the mark belongs at its start.
    /// </para>
    /// <para>
    /// <b>Step 5:</b> if the walk runs out of both VAD and silencedetect data before finding a stop
    /// (the jingle sits at the very start of the file, with no narration to find), the position
    /// reached is backed off by <see cref="JingleLeadSeconds"/> rather than trusted outright.
    /// </para>
    /// A final backward-only quiet-point snap - precise marking's own last step - still runs on
    /// whatever this returns; see <see cref="PreciseMarkRefiner.SnapToQuietestPointAsync"/> and its
    /// caller in <see cref="ChapterDetector"/>.
    /// </summary>
    /// <param name="originalMark">The mark default-mode placement (optionally already corrected
    /// by precise marking) computed for this phrase.</param>
    /// <param name="silences">Every silence Pass 1 stored, down to
    /// <see cref="MinStoredSilenceSeconds"/>.</param>
    /// <param name="speech">The raw VAD speech segments for the whole file, chronological.</param>
    /// <param name="transcript">The window's transcript with its decoded span, for telling
    /// genuine preceding narration apart from a musical/vocal transient in the jingle (see
    /// <see cref="IsGenuineSpeech"/>).</param>
    internal static double ComputeMarkBeforeJingle(
        double originalMark, List<Silence> silences, List<SpeechSegment> speech,
        TranscriptWindow transcript)
    {
        var afterSilence = silences
            .Where(s => s.StartSeconds <= originalMark && originalMark <= s.EndSeconds)
            .Cast<Silence?>().FirstOrDefault()
            is { } leadIn ? leadIn.StartSeconds : originalMark;

        if (RealSpeechAt(afterSilence, speech, transcript))
            return originalMark;

        var (position, foundBoundary) = RetreatPastNonSpeech(afterSilence, speech, silences, transcript, TransientSpeechFloorSeconds);
        return foundBoundary ? position : Math.Max(0, position - JingleLeadSeconds);
    }

    /// <summary>
    /// Whether a VAD speech blip is genuine narration rather than a musical or vocal-like transient
    /// in jingle music, by the transcript-corroboration principle
    /// <see cref="IsTrailingNarrationBlip"/> also relies on: real speech gets transcribed, music
    /// does not. Unlike that method - bounded to narration ending before the announcement, since it
    /// classifies blips at a jingle's leading edge where the announcement itself is a candidate -
    /// this needs no bound: it is used only deep in a backward retreat, already past the
    /// announcement, where any transcribed overlap can only be prior narration.
    /// <para>
    /// Falls back to the blip's VAD duration alone unless <paramref name="transcript"/>'s
    /// <em>decoded span</em> contains the blip whole. "Whisper transcribed nothing here" is only
    /// evidence of music where Whisper had the whole blip to listen to: one reaching past either
    /// edge of the window was heard in part at best (the retreat walk ranges further back than any
    /// one window reaches), and an absent transcription then says nothing about it.
    /// </para>
    /// <para>
    /// That span is the window's own bounds, deliberately not the range the segment timestamps
    /// happen to cover. Deriving it from the segments instead silently switched the check off on
    /// the windows it matters most for: a probe opening on the jingle whose only speech is the
    /// announcement yields exactly one segment, which <see cref="TrimLeadingNonSpeech"/> then
    /// advances to the announcement itself, so every music transient in the jingle looked
    /// "outside the transcript" and was trusted on duration alone. Measured on real audio
    /// 2026-07-31: a 0.83 s sting 2 s into one book's jingle and a 0.61 s one 6 s into another's
    /// each stopped the retreat where they sounded, leaving both marks inside the music instead of
    /// at its leading edge.
    /// </para>
    /// <para>
    /// A covering segment for a <see cref="MaxPaceScrutinizedBlipSeconds"/>-or-shorter blip must
    /// also be <see cref="IsPlausiblyPacedSpeech">plausibly paced</see>: Whisper can merge genuine
    /// narration with a reverb-smeared or musically-stretched announcement into one abnormally long
    /// segment (seen on real audio: "Abschied genommen. Kapitel 8" spanning 29 s for ~29
    /// characters, and "Kapitel 10" alone spanning 27 s), and any short blip inside that oversized
    /// span - including deep in the music after the real words - would otherwise be corroborated
    /// purely by falling in its time range. Longer blips skip the check; see
    /// <see cref="MaxPaceScrutinizedBlipSeconds"/> for why duration settles it there.
    /// </para>
    /// </summary>
    /// <param name="blip">The VAD speech segment to classify.</param>
    /// <param name="transcript">The window's transcript with its decoded span.</param>
    private static bool IsGenuineSpeech(SpeechSegment blip, TranscriptWindow transcript)
    {
        if (transcript.Segments.Count == 0)
            return true;
        if (blip.StartSeconds < transcript.StartSeconds || blip.EndSeconds > transcript.EndSeconds)
            return true;
        var scrutinizePace = blip.EndSeconds - blip.StartSeconds <= MaxPaceScrutinizedBlipSeconds;
        return transcript.Segments.Any(t => !string.IsNullOrWhiteSpace(t.Text)
                                     && t.StartSeconds < blip.EndSeconds && t.EndSeconds > blip.StartSeconds
                                     && (!scrutinizePace || IsPlausiblyPacedSpeech(t)));
    }

    /// <summary>
    /// Whether a transcript segment's average pace - letters and digits per second of its own span -
    /// is fast enough to be continuous real speech, rather than a segment Whisper stretched across
    /// near-silent jingle music/reverb around a few real words (see
    /// <see cref="MinPlausibleSpeechCharsPerSecond"/> for the real-audio measurements behind the
    /// floor). A zero-or-negative span (not expected in practice) counts as trivially plausible
    /// rather than being divided by.
    /// </summary>
    /// <param name="segment">The transcript segment to evaluate.</param>
    private static bool IsPlausiblyPacedSpeech(TranscriptSegment segment)
    {
        var duration = segment.EndSeconds - segment.StartSeconds;
        if (duration <= 0)
            return true;
        var chars = segment.Text.Count(char.IsLetterOrDigit);
        return chars / duration >= MinPlausibleSpeechCharsPerSecond;
    }

    /// <summary>
    /// Whether real (<see cref="TransientSpeechFloorSeconds"/>-or-longer, transcript-corroborated -
    /// see <see cref="IsGenuineSpeech"/>) VAD speech precedes <paramref name="t"/>: either by
    /// covering it (continuous narration straight through, e.g. an amplitude-only silencedetect dip
    /// VAD never stopped hearing as speech) or by ending within
    /// <see cref="JingleWalkAdjacencyToleranceSeconds"/> before it (the cross-detector boundary
    /// case). Deliberately directional: a blip starting before <paramref name="t"/> and ending
    /// after it falls under the first branch, while one starting after <paramref name="t"/> never
    /// qualifies however close, lying ahead of the tested point rather than behind it (on real
    /// audio, an undirected distance check let the announcement's own trailing word masquerade as
    /// "preceding" speech and short-circuit the retreat before it reached the jingle).
    /// <para>
    /// Used by <see cref="ComputeMarkBeforeJingle"/>'s step 2, and by
    /// <see cref="RetreatPastNonSpeech"/> to tell a genuine leading silence - directly preceded by
    /// trailing narration, which silencedetect may still report a hair past the silence's start -
    /// from an ordinary pause merely sitting near the jingle. Real audio needed both branches: a
    /// silence whose start sat 0.2 s inside VAD's still-reported speech was wrongly treated as
    /// unbacked by a first version that had only the end-nearby case.
    /// </para>
    /// </summary>
    private static bool RealSpeechAt(double t, List<SpeechSegment> speech, TranscriptWindow transcript)
        => speech.Any(b => b.EndSeconds - b.StartSeconds >= TransientSpeechFloorSeconds
                         && b.StartSeconds <= t
                         && t - b.EndSeconds <= JingleWalkAdjacencyToleranceSeconds
                         && IsGenuineSpeech(b, transcript));

    /// <summary>
    /// Backward mirror of <see cref="AdvancePastNonSpeech"/>: scans from <paramref name="from"/>
    /// toward the start of the file through VAD's raw classification and silencedetect's stored
    /// silences for the nearest preceding genuine boundary, treating any speech blip shorter than
    /// <paramref name="minSpeechSeconds"/> or lacking transcript corroboration (see
    /// <see cref="IsGenuineSpeech"/>) as a musical/vocal transient. Used by
    /// <see cref="ComputeMarkBeforeJingle"/> to walk back through a jingle's music to the previous
    /// chapter's trailing narration, or to the jingle's leading silence where it has one.
    /// <para>
    /// At each step the nearer of the last stored silence and the last genuine speech blip wins
    /// (either one's end being the stop); a blip failing the duration floor or the corroboration
    /// check is skipped instead, exactly as <see cref="AdvancePastNonSpeech"/> does going forward.
    /// </para>
    /// <para>
    /// A stored silence is an unconditional stop, never walked through and needing no corroboration:
    /// silencedetect does not read jingle music as silence, so a silence met while retreating is a
    /// real break in the music whatever surrounds it, and the mark belongs at the start of whatever
    /// plays after it. No attempt is made to tell the hush before a single jingle from the gap
    /// between two consecutive ones (an outro sting followed by this chapter's jingle) - the answer
    /// is the same either way, and real audio shows the silence's length does not carry the
    /// distinction. Confirmed against a German test audiobook (2026-07-26) where the retreat
    /// previously walked past inter-jingle gaps and landed marks 9-37 s early, in front of the
    /// <em>previous</em> chapter's sting, while that book's correctly-marked chapters all have
    /// silence-free jingles and are untouched by stopping here.
    /// </para>
    /// <para>
    /// <b>Known limitation, accepted deliberately (2026-07-26).</b> Stopping unconditionally costs
    /// the one case the removed corroboration gate handled: when default-mode placement overshoots
    /// the announcement (<see cref="RefineDefaultMark"/> advancing the mark past it), the retreat
    /// starts on its far side and stops at the pause following it, leaving the mark after the
    /// announcement rather than before the jingle - chapter 1 of that test book yields 192.004
    /// where it should yield 143.368. Nothing in either detector separates a post-announcement
    /// pause from a genuine inter-jingle separator (neither has speech before it), so telling them
    /// apart needs the announcement's position, which this method is not given. With
    /// <see cref="CliOptions.PreciseMark"/> the starting mark is corrected onto the announcement
    /// first and the case cannot arise, which is why it is tolerated; fixing it for plain
    /// --mark-before-jingle means threading the phrase onset through and refusing to stop at
    /// silences beyond it.
    /// </para>
    /// <para>
    /// The same gate applies when the position already sits inside a speech blip (walked into it by
    /// a preceding step, or <paramref name="from"/> starting there): one clearing the bar is
    /// trusted immediately, real narration already covering the position, while one that does not
    /// is skipped like any uncorroborated blip and the retreat resumes from its start. This branch
    /// once accepted any straddling blip outright, and a sub-floor transient deep inside a long
    /// jingle was observed landing the mark tens of seconds short of the jingle's true start.
    /// </para>
    /// <para>
    /// A silence qualifies by <em>starting</em> before the current position rather than by ending
    /// before it, so one the position has stepped a hair inside still stops the walk - at its end,
    /// the point after which the music runs. The two detectors disagree by milliseconds at exactly
    /// this boundary: skipping a transient that VAD timed a few samples before silencedetect ended
    /// the hush leaves the position just inside the hush, and an ends-before test would then drop
    /// the very silence just skipped over and sail on into the previous chapter's narration.
    /// Measured on real audio 2026-07-31: a 0.26 s sting starting 0.01 s before its silence ended
    /// cost two chapters (in different books, same shape) 2.7 s and 0.9 s, both marks landing at
    /// the start of the pre-jingle hush instead of at the jingle itself.
    /// </para>
    /// </summary>
    /// <param name="from">The point to scan backward from.</param>
    /// <param name="speech">Raw VAD speech segments, chronological.</param>
    /// <param name="silences">Every silence Pass 1 stored, down to
    /// <see cref="MinStoredSilenceSeconds"/>.</param>
    /// <param name="transcript">The window's transcript with its decoded span, for <see
    /// cref="IsGenuineSpeech"/>.</param>
    /// <param name="minSpeechSeconds">Speech segments shorter than this are treated as noise and
    /// skipped over rather than accepted as the true offset.</param>
    /// <returns><c>(Position, true)</c>: the end of the qualifying silence or speech segment
    /// nearest to (at or before) <paramref name="from"/>, or <paramref name="from"/> itself when
    /// it already falls inside a qualifying speech segment (never moves further than necessary).
    /// <c>(Position, false)</c>: neither <paramref name="speech"/> nor <paramref name="silences"/>
    /// reach far enough back to find one - <c>Position</c> is then however far the retreat got
    /// before running out of data, for the caller's own fallback.</returns>
    internal static (double Position, bool FoundBoundary) RetreatPastNonSpeech(
        double from, List<SpeechSegment> speech, List<Silence> silences,
        TranscriptWindow transcript, double minSpeechSeconds)
    {
        var t = from;
        while (true)
        {
            var prevSpeech = speech.Where(b => b.StartSeconds < t).Cast<SpeechSegment?>().LastOrDefault();
            if (prevSpeech is { } straddling && straddling.EndSeconds >= t)
            {
                if (straddling.EndSeconds - straddling.StartSeconds >= minSpeechSeconds
                    && IsGenuineSpeech(straddling, transcript))
                    return (t, true);
                t = straddling.StartSeconds;
                continue;
            }

            var prevSilence = silences.Where(s => s.StartSeconds < t).Cast<Silence?>().LastOrDefault();
            if (prevSilence is { } sil && sil.EndSeconds >= (prevSpeech?.EndSeconds ?? double.NegativeInfinity))
                return (sil.EndSeconds, true);

            if (prevSpeech is not { } blip)
                return (t, false);
            if (blip.EndSeconds - blip.StartSeconds < minSpeechSeconds || !IsGenuineSpeech(blip, transcript))
            {
                t = blip.StartSeconds;
                continue;
            }
            return (blip.EndSeconds, true);
        }
    }

    /// <summary>
    /// Resolves the phrase-onset estimate the <em>default</em> (non --mark-before-jingle) mark
    /// placement backs the mark lead (<see cref="ABChapterize.Cli.CliOptions.MarkLeadSeconds"/>)
    /// off from, for phrases anchored to a
    /// jingle region. Whisper's segment timestamp for a "Kapitel N" announcement spoken over or
    /// inside a jingle is exactly the failure --mark-before-jingle's containment/smeared-phrase
    /// machinery (<see cref="FindJingleRegionForPhrase"/>, <see cref="FindSmearedJingleRegion"/>)
    /// exists to route around, and it is unreliable even at the per-token level (confirmed on
    /// real audio; see tools/vadprobe's token-timestamp trace) - so this never trusts it directly
    /// once a jingle region is involved.
    /// <para>
    /// VAD's own speech-segment boundaries do not share that unreliability, and can still
    /// pinpoint the true onset in the one case that matters: <see cref="ComputeNonSpeechRegions"/>'s
    /// <see cref="MergeShortSpeechGapSeconds"/> merge - kept to bridge the announcement when it is
    /// spoken <em>inside</em> the jingle - cannot tell the announcement's own quietly-spoken
    /// word(s)/syllables apart from an incidental musical vocal transient when they are short
    /// enough (under a second), and silently merges them into the surrounding non-speech run. When
    /// the jingle's music genuinely never dips below the noise floor except right around the
    /// announcement itself, the swallowed blips inside the region are - by the same "the only
    /// speech inside a jingle is the announcement" invariant <see cref="IsTrailingNarrationBlip"/>
    /// already relies on for the region's head - the announcement's own words, not a coincidence.
    /// </para>
    /// <para>
    /// A first version took only the <em>last</em> swallowed blip's start, reasoning that a
    /// multi-word announcement ("Kapitel 35") ends with its trailing word closest to the region's
    /// end. That holds when the announcement produces exactly one swallowed blip (an earlier,
    /// unrelated vocal transient a second or more away is then correctly ignored) but breaks when
    /// its several words are each swallowed individually: on real audio, chapter 31's "Kapitel 31"
    /// was split into a 0.67 s "Kapitel" blip and a 0.9 s "31" blip 0.26 s apart, and taking only
    /// the last landed the mark after "Kapitel" was already spoken (verified by a 5.25 s
    /// re-transcription from that mark, which started mid-narration rather than on the phrase). The
    /// fix: cluster the swallowed blips by the same short-gap threshold that put them into one
    /// merged region, and take the first blip of the <em>last</em> cluster - the announcement's
    /// leading edge, one blip or several, while still skipping an earlier separately-clustered
    /// transient.
    /// </para>
    /// <para>
    /// Without any swallowed blip there is nothing more precise than <paramref name="phraseAbs"/>:
    /// at or after the region's start it is at least in the right neighbourhood (that is what
    /// qualified the region by containment) and used unchanged; only when it still precedes the
    /// region - Whisper smeared the segment past what <see cref="TrimLeadingNonSpeech"/> could
    /// bridge - is it floored at the region's end, so the mark cannot land back in the previous
    /// chapter's narration.
    /// </para>
    /// </summary>
    /// <param name="phraseAbs">The (TrimLeadingNonSpeech-corrected) phrase onset estimate.</param>
    /// <param name="jingleRegion">The jingle region <see cref="ResolveJingleAnchor"/> resolved for
    /// this phrase, or null when none was found.</param>
    /// <param name="speech">The raw VAD speech segments behind the regions.</param>
    internal static double ResolveDefaultPhraseOnset(
        double phraseAbs, NonSpeechRegion? jingleRegion, List<SpeechSegment> speech)
    {
        if (jingleRegion is not { } r)
            return phraseAbs;
        var swallowed = speech
            .Where(b => b.StartSeconds > r.StartSeconds && b.EndSeconds < r.EndSeconds)
            .OrderBy(b => b.StartSeconds)
            .ToList();
        if (swallowed.Count == 0)
            return phraseAbs < r.StartSeconds ? r.EndSeconds : phraseAbs;

        var lastClusterStart = swallowed[0].StartSeconds;
        for (var i = 1; i < swallowed.Count; i++)
            if (swallowed[i].StartSeconds - swallowed[i - 1].EndSeconds >= MergeShortSpeechGapSeconds)
                lastClusterStart = swallowed[i].StartSeconds;
        return lastClusterStart;
    }

    /// <summary>
    /// Scans forward from <paramref name="from"/> through VAD's raw speech/non-speech
    /// classification for the next genuine speech onset, ignoring any speech blip shorter than
    /// <paramref name="minSpeechSeconds"/> as detector noise-floor jitter rather than real
    /// spoken content. Unlike Whisper's segment timestamps - demonstrably unreliable near a jingle
    /// (see <see cref="ResolveDefaultPhraseOnset"/>) and sensitive to the decode window's content -
    /// VAD's classification of a stretch of audio does not depend on the window it was decoded in,
    /// making it an independent cross-check for an already-computed mark: scanning from that mark
    /// and re-deriving it as <c>onset - markLead</c> is a no-op when the
    /// mark was already correct (it then sits exactly that far before the onset the scan finds), and
    /// otherwise only moves a too-early mark forward - never backward past one already at or beyond
    /// the onset, matching the "any remaining error is too early, never too late" invariant the
    /// whole default-mode jingle-anchoring chain is built on.
    /// </summary>
    /// <param name="from">The point to scan forward from - typically an already-computed mark.</param>
    /// <param name="speech">Raw VAD speech segments, chronological, covering at least the span
    /// from <paramref name="from"/> to the true onset.</param>
    /// <param name="minSpeechSeconds">Speech segments shorter than this are treated as noise and
    /// skipped over rather than accepted as the true onset.</param>
    /// <returns>The start of the first speech segment at or after <paramref name="from"/> whose
    /// own length meets <paramref name="minSpeechSeconds"/>; <paramref name="from"/> itself when
    /// it already falls strictly inside such a segment (never moves backward); or null when
    /// <paramref name="speech"/> does not reach far enough to find one - the caller should
    /// re-run VAD over a wider window in that case, since VAD itself is cheap.</returns>
    internal static double? AdvancePastNonSpeech(double from, List<SpeechSegment> speech, double minSpeechSeconds)
    {
        var t = from;
        while (true)
        {
            var next = speech.Where(b => b.EndSeconds > t).Cast<SpeechSegment?>().FirstOrDefault();
            if (next is not { } blip)
                return null;
            if (blip.StartSeconds <= t)
                return t;
            if (blip.EndSeconds - blip.StartSeconds < minSpeechSeconds)
            {
                t = blip.EndSeconds;
                continue;
            }
            return blip.StartSeconds;
        }
    }

    /// <summary>
    /// Refines a default-mode (non --mark-before-jingle) mark by advancing past non-speech from it
    /// with <see cref="AdvancePastNonSpeech"/> and re-deriving the mark lead from whatever genuine
    /// speech onset that finds, rather than trusting
    /// <paramref name="preliminaryMark"/>'s upstream reasoning outright. Necessary on real audio:
    /// even after the clustering fix in <see cref="ResolveDefaultPhraseOnset"/>, several chapters
    /// still landed at the very start of their jingle rather than before the announcement. It also
    /// sidesteps having to decide which VAD blip "is" the announcement, or which region belongs to
    /// it: whatever upstream concluded, the true onset is simply the next place VAD hears real
    /// speech at or after the mark, and backing off from there is the rule default mode applies
    /// everywhere else - a no-op when the mark was already correct (see
    /// <see cref="AdvancePastNonSpeech"/>'s idempotency note).
    /// <para>
    /// Deliberately unbounded. An earlier version capped the scan at the resolved jingle region's
    /// end to protect a synthetic case where Whisper reports a phrase inside a region VAD shows no
    /// speech in, but that cap also defeated the fix for phrases whose region resolution failed
    /// entirely - exactly the cases still broken live. Real jingle announcements are reliably
    /// VAD-detectable (the "only speech in a jingle is the announcement" invariant), so a genuine
    /// phrase match with zero VAD speech in or after its region is not expected on real audio; see
    /// the <c>DefaultMode_PhraseAlreadyInsideTheJingleRegion_*</c> tests for the behaviour change.
    /// </para>
    /// </summary>
    /// <param name="preliminaryMark">The mark the existing default-mode logic already computed.</param>
    /// <param name="speech">Raw VAD speech segments; empty when the VAD pre-pass did not run, in
    /// which case there is nothing to advance past and <paramref name="preliminaryMark"/> is
    /// returned unchanged.</param>
    /// <param name="markLead">Seconds to place the mark ahead of the onset found, i.e.
    /// <see cref="ABChapterize.Cli.CliOptions.MarkLeadSeconds"/>. Must be the same value
    /// <paramref name="preliminaryMark"/> was derived with, or the no-op case stops being one.</param>
    /// <returns>The refined mark, or <paramref name="preliminaryMark"/> unchanged when VAD did not
    /// run or the scan found nothing further ahead.</returns>
    internal static double RefineDefaultMark(double preliminaryMark, List<SpeechSegment> speech, double markLead)
    {
        if (speech.Count == 0)
            return preliminaryMark;
        var onset = AdvancePastNonSpeech(preliminaryMark, speech, TransientSpeechFloorSeconds);
        // A mark already inside a qualifying speech segment comes back unchanged - that is
        // AdvancePastNonSpeech's no-op case, not an onset to back off from again, so the
        // o > preliminaryMark test excludes it from the redundant correction.
        if (onset is not { } o || o <= preliminaryMark)
            return preliminaryMark;
        return Math.Max(0, o - markLead);
    }

    /// <summary>
    /// Finds the VAD non-speech region (the jingle) a matched phrase belongs to, by
    /// <em>containment</em> rather than end-alignment: the last region that contains the phrase
    /// (<c>Start &lt;= phrase &lt;= End</c>) or brackets it within
    /// <see cref="JinglePhraseMatchToleranceSeconds"/> at either edge (VAD and Whisper time their
    /// boundaries slightly differently). Containment is what makes this robust to the "Kapitel N"
    /// announcement being spoken <em>inside</em> the jingle - Whisper then timestamps the phrase
    /// before the VAD region ends, so an end-alignment test would drop the region and the mark
    /// would fall back onto an unrelated earlier in-text pause, landing the chapter seconds early
    /// (the failure that motivated this). Since the mark comes from the region's
    /// <see cref="JingleStart">start</see>, where it <em>ends</em> - possibly inflated by
    /// <see cref="ComputeNonSpeechRegions"/>'s short-speech-gap merge swallowing the announcement -
    /// never affects placement. A region starting after the phrase (a post-announcement pause) is
    /// excluded. Returns null when no region qualifies within the window.
    /// </summary>
    /// <param name="windowStart">Earliest a qualifying region may end (the probe window start or
    /// the Pass 3 lookback start); a region entirely before it is ignored.</param>
    /// <param name="phraseAbsSeconds">Absolute phrase start in seconds.</param>
    /// <param name="regions">All VAD non-speech regions, in chronological order.</param>
    internal static NonSpeechRegion? FindJingleRegionForPhrase(
        double windowStart, double phraseAbsSeconds, List<NonSpeechRegion> regions)
    {
        var latestStart = phraseAbsSeconds + JinglePhraseMatchToleranceSeconds;
        var earliestEnd = phraseAbsSeconds - JinglePhraseMatchToleranceSeconds;
        NonSpeechRegion? found = null;
        foreach (var r in regions)
            if (r.EndSeconds > windowStart && r.StartSeconds <= latestStart && r.EndSeconds >= earliestEnd)
                found = r;
        return found;
    }

    /// <summary>
    /// Advances each transcript segment's start past any run of silence and/or jingle (VAD
    /// non-speech) that Whisper lumped into the head of the segment, so the timestamp points at the
    /// actual speech onset. Whisper timestamps a segment from where its decoded audio block begins;
    /// for the segment carrying a chapter announcement after a pause and/or a jingle, that is the
    /// start of the leading non-speech, not of the spoken phrase. Left uncorrected, the phrase's
    /// apparent start sits back in the previous chapter's trailing audio, which both mis-places the
    /// mark (the anchor logic keys off the phrase start) and feeds the --min-silence-length /
    /// --max-jingle-length auto mechanisms a mis-measured (usually too short) silence. Both
    /// detectors' findings are available here - silencedetect down to
    /// <see cref="MinStoredSilenceSeconds"/>, plus VAD regions when the pre-pass ran - so the real
    /// onset is the far end of the contiguous run of non-speech intervals beginning at (or a hair
    /// before, see <see cref="SegmentLeadTrimToleranceSeconds"/>) the segment's timestamp, chained
    /// through directly abutting intervals (a silence immediately followed by its jingle). The run
    /// is never followed past the segment's own end: a segment that matched a phrase always has
    /// some speech in it, so a leading run consuming all of it would be spurious. Segments are in
    /// absolute file time, matching the silence/region lists. Internal for unit testing.
    /// </summary>
    /// <param name="segmentsAbs">The window's transcript segments, in absolute file time.</param>
    /// <param name="allSilences">Every silence Pass 1 stored, down to
    /// <see cref="MinStoredSilenceSeconds"/>.</param>
    /// <param name="nonSpeechRegions">VAD non-speech regions; empty when the VAD pre-pass did not run.</param>
    /// <param name="jingle">True when the VAD pre-pass ran, enabling the region intervals.</param>
    internal static List<TranscriptSegment> TrimLeadingNonSpeech(
        List<TranscriptSegment> segmentsAbs, List<Silence> allSilences,
        List<NonSpeechRegion> nonSpeechRegions, bool jingle)
    {
        var intervals = allSilences.Select(s => (s.StartSeconds, s.EndSeconds));
        if (jingle)
            intervals = intervals.Concat(nonSpeechRegions.Select(r => (r.StartSeconds, r.EndSeconds)));
        var nonSpeech = intervals.ToList();

        return segmentsAbs.Select(seg =>
        {
            var onset = seg.StartSeconds;
            // Re-scan until stable so a silence directly abutting a jingle is chained through.
            bool advanced;
            do
            {
                advanced = false;
                foreach (var (from, to) in nonSpeech)
                {
                    if (from <= onset + SegmentLeadTrimToleranceSeconds
                        && to > onset + SegmentLeadTrimToleranceSeconds
                        && to <= seg.EndSeconds)
                    {
                        onset = to;
                        advanced = true;
                    }
                }
            } while (advanced);
            return onset > seg.StartSeconds ? seg with { StartSeconds = onset } : seg;
        }).ToList();
    }
}
