// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using ABChapterize.Transcription;
using ABChapterize.Vad;
using static ABChapterize.Detection.DetectionTuning;
using static ABChapterize.Detection.GapPlanning;

namespace ABChapterize.Detection;

/// <summary>
/// A gap between two consecutive <see cref="SpeechSegment"/>s found by the VAD pre-pass -
/// i.e. a region VAD considers non-speech, flanked by speech on both sides. A silence-less
/// jingle transition shows up as one of these (music, like silence, reads
/// as non-speech to a speech detector). Deliberately does not cover leading/trailing
/// non-speech at the very start/end of the file - the synthetic file-start candidate
/// (Start = 0) already covers a jingle-before-chapter-1 edge case without it.
/// </summary>
/// <param name="StartSeconds">Where VAD stopped detecting speech.</param>
/// <param name="EndSeconds">Where VAD resumed detecting speech.</param>
internal readonly record struct NonSpeechRegion(double StartSeconds, double EndSeconds);

/// <summary>Locates and resolves VAD non-speech regions (jingles) and silence-based anchors
/// around a chapter phrase: computing the regions themselves from raw VAD speech segments,
/// matching a region to its phrase, and deriving where the chapter mark and the various
/// --min-silence-length/--max-jingle-length auto statistics should anchor.</summary>
internal static class JingleGeometry
{
    /// <summary>
    /// Inverts consecutive VAD speech segments into the non-speech gaps between them, then cleans
    /// up two things Silero VAD is not reliable about on real jingle music: a "speech" blip
    /// shorter than <see cref="MergeShortSpeechGapSeconds"/> (a vocal-like transient or a strong
    /// rhythmic passage inside otherwise instrumental music) does not end a jingle - the non-speech
    /// regions on either side of it are merged into one, rather than fragmenting one continuous
    /// jingle into several too-short regions; and any region whose longest single <em>contiguous</em>
    /// non-speech run falls short of <see cref="MinJingleObservationSeconds"/> is dropped outright.
    /// <para>
    /// The floor is deliberately checked against the longest contiguous run, not the merged
    /// region's wall-clock span: a jingle is defined by containing one genuinely long, unbroken
    /// music block (surviving fragmentation by a brief misfire because the pieces between misfires
    /// are still long), whereas ordinary narration cadence produces only short inter-word/clause
    /// pauses - individually well under the floor, but able to chain-merge across the equally short
    /// speech between them into a span that clears the floor even though no real jingle-length
    /// silence exists anywhere in it. Measuring the span there would resurface exactly the
    /// breath-pause false positives the floor is meant to suppress; measuring the longest run
    /// keeps genuine (even mildly fragmented) jingles while rejecting stitched-together narration.
    /// </para>
    /// Internal for unit testing.
    /// </summary>
    internal static List<NonSpeechRegion> ComputeNonSpeechRegions(List<SpeechSegment> speech)
    {
        var merged = new List<NonSpeechRegion>();
        // The longest single contiguous non-speech run within each merged region (i.e. the
        // longest raw gap it was built from, before any speech blips were merged across),
        // parallel to `merged` by index - kept alongside rather than on NonSpeechRegion itself
        // so the record's shape stays purely (Start, End) for downstream use and equality.
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
    /// region has none (a silence-less jingle, or an in-text pause that ends before the region
    /// rather than inside it). Picks the earliest-ending silence when more than one overlaps the
    /// region's leading edge. This is the geometry that distinguishes a genuine "silence then
    /// jingle" transition from a false in-text pause that merely triggered a probe.
    /// <para>
    /// Crucially, the candidate must also <em>start</em> within <see
    /// cref="LeadingSilenceStartToleranceSeconds"/> of the region's own start - not merely end
    /// somewhere inside it. A genuine lead-in hush and the region both begin at essentially the
    /// same moment (VAD stops seeing speech right as the hush starts, same as silencedetect),
    /// so their starts always line up to within each detector's own timing jitter, however long
    /// the hush itself runs. Without this check, a long region whose music never dips below the
    /// noise floor - except for one ordinary breath-pause silence sitting right before the
    /// announcement, deep inside the region - would have that unrelated pause mistaken for the
    /// lead-in, placing the mark just before the phrase instead of at the true jingle start
    /// (confirmed on real audio: chapters whose region ran 5-15 s before the only silence in it).
    /// </para>
    /// <para>
    /// For the same reason, no VAD speech blip may sit between the region's start and the
    /// silence's start: the lead-in hush directly abuts the end of the previous narration, so a
    /// blip in between means the silence follows some other sound (the jingle's opening sting,
    /// say) rather than leading the region - anchoring to it would cut that opening off into the
    /// previous chapter.
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
    /// happened to trigger the probe - used whenever the VAD pre-pass ran, both to place the
    /// mark with --mark-before-jingle and to feed the --min-silence-length/--max-jingle-length
    /// auto mechanisms and per-file statistics regardless of that option. The jingle is the VAD
    /// non-speech region the phrase belongs to (see <see cref="FindJingleRegionForPhrase"/> - by
    /// containment, so the announcement being spoken <em>inside</em> the jingle does not lose the
    /// region); a silencedetect silence is accepted as the anchor <em>only</em> when it
    /// <see cref="LeadingSilence">leads that region</see> (its end lies inside it) - the classic
    /// "silence then jingle" transition, where --mark-before-jingle places the mark 0.5 s before
    /// the silence. When the region has no leading silence (a silence-less jingle) the region
    /// itself is the anchor and --mark-before-jingle places the mark at the jingle start with no
    /// lead.
    /// <para>
    /// Crucially, a false in-text pause earlier in the previous chapter's narration does
    /// <em>not</em> lead the jingle region, so it is never mistaken for the anchor even when it
    /// is the candidate that triggered this probe. That prevents a silence-less jingle transition
    /// from being marked at the pause instead of the jingle, and stops the pause's length from
    /// feeding the --min-silence-length / --max-jingle-length auto mechanisms with a bogus
    /// (inflated) observation. Only when VAD found no region near the phrase at all (VAD off, or a
    /// transition with neither a jingle nor a VAD-registered silence) does this fall back to the
    /// nearest preceding silence.
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
    /// <returns><c>AnchorSilence</c>: the silence leading the jingle (or, when no jingle region
    /// was found, the silence directly preceding the phrase), or null for a silence-less jingle.
    /// <c>VadRegion</c>: the jingle region the phrase belongs to - its start already corrected
    /// by <see cref="AdjustJingleRegion"/>, so callers can use it for the --min-silence-length/
    /// --max-jingle-length auto statistics and (via <see cref="ResolveDefaultPhraseOnset"/>) for
    /// default-mode mark placement directly - or null when none was found.
    /// The region is returned even when <c>AnchorSilence</c> also is (the "silence then jingle"
    /// shape), so a caller can measure the jingle for the auto statistics regardless of which of
    /// the two --mark-before-jingle's own placement (<see cref="ComputeMarkBeforeJingle"/>) ends
    /// up walking back to.</returns>
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
    /// jingle's own music: it is narration exactly when Whisper transcribed words over it that
    /// end before <paramref name="narrationBound"/>. This rests on the observation that the only
    /// real speech ever occurring <em>inside</em> a jingle is the chapter announcement itself -
    /// so transcribed non-phrase words over a blip mean narration, and an untranscribed blip
    /// means music (Whisper does not silently skip genuine narration). The phrase's own segment
    /// never qualifies because it ends after the bound.
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
    /// transcript to arbitrate what the two blind detectors cannot decide alone. Two symmetric
    /// defects of <see cref="ComputeNonSpeechRegions"/>'s fixed 1 s speech-gap merge are undone
    /// here, where the transcript is finally available:
    /// <list type="bullet">
    /// <item><b>Swallowed trailing narration:</b> a short final sentence of the previous chapter
    /// ("Dann war nichts mehr.") that VAD chopped into sub-second fragments gets merged into the
    /// region's head, dragging its start back into speech. Each leading blip that overlaps
    /// transcribed narration (see <see cref="IsTrailingNarrationBlip"/>) moves the jingle start
    /// forward past it.</item>
    /// <item><b>Split jingle:</b> a vocal-like transient in the music just over the merge limit
    /// splits one jingle into two regions, so a mark at the selected region's start lands
    /// mid-jingle. When another region ends within <see cref="JingleGlueMaxSeconds"/> before the
    /// (possibly just-trimmed) start and no transcribed narration lies in between - per the
    /// only-speech-in-a-jingle-is-the-phrase observation, an untranscribed blip there is music -
    /// the jingle extends back to that region's start, repeatedly if it was split more than
    /// once. Trimmed narration blocks the bridge automatically: the trim leaves them inside the
    /// gap the bridge would have to cross.</item>
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
        // Narration must end by the phrase - except when the phrase timestamp itself lies before
        // the region (the smeared-phrase rescue selected it), where "just past the region start"
        // is the honest bound: Whisper's segment ends overhang real speech by up to about the
        // same jitter the leading-silence proximity check absorbs.
        var narrationBound = Math.Max(phraseAbs, region.StartSeconds + LeadingSilenceStartToleranceSeconds);

        var start = region.StartSeconds;
        foreach (var blip in speech)
        {
            if (blip.StartSeconds <= region.StartSeconds || blip.EndSeconds >= region.EndSeconds)
                continue;
            // Blips are only trimmed near the current start (deeper ones are past the jingle's
            // onset - e.g. the announcement itself, spoken over the music) and never across the
            // phrase.
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
    /// whole jingle, its start pulled back to the end of the narration. The segment's span
    /// betrays this - it then overlaps the jingle region by many seconds - so the last region
    /// overlapping [phrase start, phrase segment end] by at least
    /// <see cref="SmearedPhraseMinOverlapSeconds"/> is accepted as the jingle. A correctly
    /// timed announcement's segment at most grazes a following pause region (well under the
    /// threshold), so the classic shapes never take this path.
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
    /// name="originalMark"/> - the mark default-mode placement already computed (<see
    /// cref="RefineDefaultMark"/>/<see cref="ResolveDefaultPhraseOnset"/>, further corrected by
    /// --precise-mark first when that option is also set) - to the true edge of whatever jingle
    /// precedes the announcement, by literally retracing the audio via the same two detectors
    /// used everywhere else in this file, rather than picking from a short list of pre-resolved
    /// shapes (a preceding silence, else a VAD region's start, else a flat lead) the way the
    /// placement this replaces did. Being independent of whichever silence/region a probe
    /// happened to resolve is also what makes this compatible with --precise-mark, which that
    /// older rule was not: it starts from whatever mark --precise-mark already settled on and
    /// corrects it further, rather than replacing default-mode placement outright.
    /// <para>
    /// <b>Step 1:</b> back out of any silencedetect silence <paramref name="originalMark"/>
    /// itself sits in - a leading hush directly before the phrase, whether or not a jingle
    /// precedes it in turn.
    /// </para>
    /// <para>
    /// <b>Step 2:</b> if real (<see cref="TransientSpeechFloorSeconds"/>-or-longer) VAD speech
    /// covers - or ends essentially right at, within <see
    /// cref="JingleWalkAdjacencyToleranceSeconds"/> of absorbing silencedetect/VAD boundary
    /// jitter - that point, the previous chapter's own narration led straight into an ordinary
    /// pause with no jingle in it at all: <paramref name="originalMark"/> needs no correction
    /// and is returned unchanged.
    /// </para>
    /// <para>
    /// <b>Steps 3-4:</b> otherwise, keep retreating - now through the jingle's own music - via
    /// <see cref="RetreatPastNonSpeech"/>, which treats any VAD speech blip shorter than <see
    /// cref="TransientSpeechFloorSeconds"/> as a musical/vocal transient rather than a genuine
    /// return to narration and does not stop for it (a silencedetect silence that short never
    /// reaches this algorithm at all - <see cref="MinStoredSilenceSeconds"/> already floors
    /// every stored interval well above it, so there is nothing extra to ignore on that side).
    /// The first point where real speech precedes is the jingle's true leading edge - the
    /// previous chapter's own trailing narration ends exactly there - and is returned as-is,
    /// with no further lead: unlike step 2's case, a real jingle sits here, and the mark
    /// belongs right at its start.
    /// </para>
    /// <para>
    /// <b>Step 5:</b> if that walk runs out of VAD data before ever finding real preceding
    /// speech (the jingle sits at the very start of the file, before there was any narration to
    /// find), the reached position is backed off by <see cref="JingleLeadSeconds"/> instead of
    /// being trusted outright - the same flat safety lead used elsewhere as a last resort.
    /// </para>
    /// A final backward-only quiet-point snap - the same one --precise-mark's own final step
    /// applies - still runs on whatever this returns; see <see
    /// cref="PreciseMarkRefiner.SnapToQuietestPointAsync"/> and its caller in
    /// <see cref="ChapterDetector"/>.
    /// </summary>
    /// <param name="originalMark">The mark default-mode placement (optionally already corrected
    /// by --precise-mark) computed for this phrase.</param>
    /// <param name="silences">Every silence Pass 1 stored, down to
    /// <see cref="MinStoredSilenceSeconds"/>.</param>
    /// <param name="speech">The raw VAD speech segments for the whole file, chronological.</param>
    internal static double ComputeMarkBeforeJingle(
        double originalMark, List<Silence> silences, List<SpeechSegment> speech)
    {
        var afterSilence = silences
            .Where(s => s.StartSeconds <= originalMark && originalMark <= s.EndSeconds)
            .Cast<Silence?>().FirstOrDefault()
            is { } leadIn ? leadIn.StartSeconds : originalMark;

        if (RealSpeechAt(afterSilence, speech))
            return originalMark;

        var (position, foundSpeech) = RetreatPastNonSpeech(afterSilence, speech, TransientSpeechFloorSeconds);
        return foundSpeech ? position : Math.Max(0, position - JingleLeadSeconds);
    }

    /// <summary>
    /// Whether real (<see cref="TransientSpeechFloorSeconds"/>-or-longer) VAD speech is
    /// happening right at <paramref name="t"/> - either because <paramref name="t"/> falls
    /// inside such a segment (continuous narration straight through, e.g. an amplitude-only
    /// silencedetect dip that VAD never stopped hearing as speech), or because one ends within
    /// <see cref="JingleWalkAdjacencyToleranceSeconds"/> of it (the cross-detector boundary
    /// case). Used by <see cref="ComputeMarkBeforeJingle"/>'s step 2.
    /// </summary>
    private static bool RealSpeechAt(double t, List<SpeechSegment> speech)
        => speech.Any(b => b.EndSeconds - b.StartSeconds >= TransientSpeechFloorSeconds
                         && (b.StartSeconds <= t && t <= b.EndSeconds
                             || Math.Abs(b.EndSeconds - t) <= JingleWalkAdjacencyToleranceSeconds));

    /// <summary>
    /// Backward mirror of <see cref="AdvancePastNonSpeech"/>: scans from <paramref name="from"/>
    /// toward the start of the file through VAD's raw speech/non-speech classification for the
    /// nearest preceding genuine speech offset, ignoring any speech blip shorter than <paramref
    /// name="minSpeechSeconds"/> as detector noise rather than real spoken content - used by
    /// <see cref="ComputeMarkBeforeJingle"/> to walk back through a jingle's own music to the
    /// previous chapter's trailing narration.
    /// </summary>
    /// <param name="from">The point to scan backward from.</param>
    /// <param name="speech">Raw VAD speech segments, chronological.</param>
    /// <param name="minSpeechSeconds">Speech segments shorter than this are treated as noise and
    /// skipped over rather than accepted as the true offset.</param>
    /// <returns><c>(Position, true)</c>: the end of the last qualifying speech segment at or
    /// before <paramref name="from"/>, or <paramref name="from"/> itself when it already falls
    /// inside one (never moves further than necessary). <c>(Position, false)</c>: <paramref
    /// name="speech"/> does not reach far enough back to find one - <c>Position</c> is then
    /// however far the retreat got before running out of data, for the caller's own fallback.</returns>
    internal static (double Position, bool FoundSpeech) RetreatPastNonSpeech(
        double from, List<SpeechSegment> speech, double minSpeechSeconds)
    {
        var t = from;
        while (true)
        {
            var prev = speech.Where(b => b.StartSeconds < t).Cast<SpeechSegment?>().LastOrDefault();
            if (prev is not { } blip)
                return (t, false);
            if (blip.EndSeconds >= t)
                return (t, true);
            if (blip.EndSeconds - blip.StartSeconds < minSpeechSeconds)
            {
                t = blip.StartSeconds;
                continue;
            }
            return (blip.EndSeconds, true);
        }
    }

    /// <summary>
    /// Resolves the phrase-onset estimate the <em>default</em> (non --mark-before-jingle) mark
    /// placement backs <see cref="DefaultMarkLeadSeconds"/> off from, for phrases anchored to a
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
    /// A first version of this fix took only the <em>last</em> swallowed blip's start, reasoning
    /// that a multi-word announcement ("Kapitel 35") ends with its own trailing word closest to
    /// the region's end. That holds when the announcement itself produces exactly one swallowed
    /// blip (an earlier, unrelated musical vocal transient sitting well before it, separated by a
    /// gap of a second or more, is correctly ignored) - but breaks when the announcement's own
    /// several words are each individually swallowed: confirmed on real audio, chapter 31's
    /// "Kapitel 31" was split into a 0.67 s "Kapitel" blip and a 0.9 s "31" blip 0.26 s apart, and
    /// taking only the last landed the mark after "Kapitel" had already been spoken, verified via a
    /// direct 5.25 s re-transcription starting at that mark landing mid-narration rather than on
    /// the phrase. The fix: cluster the swallowed blips using the same short-gap threshold that
    /// decided they belonged inside one merged region in the first place, and take the first blip
    /// of the <em>last</em> cluster - the announcement's own leading edge, whether it produced one
    /// swallowed blip or several, while still skipping past any earlier, separately-clustered
    /// incidental vocal transient.
    /// </para>
    /// <para>
    /// Absent any swallowed blip, there is nothing more precise than <paramref name="phraseAbs"/>
    /// itself to go on: if it already sits at or after the region's start, it is at least in the
    /// right neighbourhood (that is what qualified the region via containment in the first place)
    /// and is used unchanged; only when it still precedes the region - Whisper smeared the segment
    /// so badly that even <see cref="TrimLeadingNonSpeech"/>'s forward correction could not bridge
    /// it - is it floored at the region's end instead, so the mark cannot land seconds back in the
    /// previous chapter's narration.
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
    /// spoken content. Unlike Whisper's own segment timestamps - demonstrably unreliable near a
    /// jingle (see <see cref="ResolveDefaultPhraseOnset"/>) and sensitive to the surrounding
    /// decode window's exact content - VAD's classification of a given stretch of audio does
    /// not depend on what window it happens to be decoded within, making it a solid independent
    /// cross-check for a mark <see cref="ResolveDefaultPhraseOnset"/> already computed:
    /// starting the scan from that mark and re-deriving it from the found onset
    /// (<c>onset - <see cref="DefaultMarkLeadSeconds"/></c>) is a no-op whenever the mark was
    /// already correct (it sits exactly <see cref="DefaultMarkLeadSeconds"/> before the true
    /// onset, so the scan finds that same onset immediately), and only ever moves a mark that
    /// was too early forward toward the truth - never backward past a mark already at or beyond
    /// it, matching the "any remaining error is too early, never too late" invariant the whole
    /// default-mode jingle-anchoring chain is built on.
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
    /// with <see cref="AdvancePastNonSpeech"/> and re-deriving <see cref="DefaultMarkLeadSeconds"/>
    /// back from whatever genuine speech onset that finds, rather than trusting
    /// <paramref name="preliminaryMark"/>'s own upstream reasoning outright. Confirmed necessary on
    /// real audio: even after clustering-fixing <see cref="ResolveDefaultPhraseOnset"/>'s swallowed-
    /// blip handling, several chapters still landed at the very start of their jingle rather than
    /// before the announcement. This sidesteps needing to reason about which VAD blip "is" the
    /// announcement in the first place, or which non-speech region even belongs to it: whatever
    /// upstream logic decided, the true onset is simply the next place VAD says real speech resumes
    /// at or after it, and backing off <see cref="DefaultMarkLeadSeconds"/> from there is the same
    /// rule default mode already applies everywhere else - a no-op whenever
    /// <paramref name="preliminaryMark"/> was already correct (see
    /// <see cref="AdvancePastNonSpeech"/>'s own idempotency note). Deliberately unbounded: an earlier
    /// version capped the scan at the resolved jingle region's own end to protect a synthetic case
    /// where Whisper reports a phrase inside a region VAD shows no speech in at all, but that cap
    /// also silently defeated the fix for phrases whose jingle region resolution failed entirely -
    /// exactly the cases still broken live. Real jingle announcements are reliably VAD-detectable (the
    /// same "only speech in a jingle is the announcement" invariant this whole chain relies on), so a
    /// region with a genuine phrase match and zero VAD speech anywhere in or after it is not expected
    /// on real audio; trusting the scan unconditionally is the simpler, more direct reading of the fix
    /// and was validated this way per the user's own request - see the updated
    /// <c>DefaultMode_PhraseAlreadyInsideTheJingleRegion_*</c> test for the resulting behaviour change.
    /// </summary>
    /// <param name="preliminaryMark">The mark the existing default-mode logic already computed.</param>
    /// <param name="speech">Raw VAD speech segments; empty when the VAD pre-pass did not run, in
    /// which case there is nothing to advance past and <paramref name="preliminaryMark"/> is
    /// returned unchanged.</param>
    /// <returns>The refined mark, or <paramref name="preliminaryMark"/> unchanged when VAD did not
    /// run or the scan found nothing further ahead.</returns>
    internal static double RefineDefaultMark(double preliminaryMark, List<SpeechSegment> speech)
    {
        if (speech.Count == 0)
            return preliminaryMark;
        var onset = AdvancePastNonSpeech(preliminaryMark, speech, TransientSpeechFloorSeconds);
        // AdvancePastNonSpeech returns preliminaryMark itself, unchanged, when the mark already
        // sits inside a qualifying speech segment (e.g. continuous narration with no pause before
        // it) - that is its own no-op case, not a phrase onset to back DefaultMarkLeadSeconds off
        // from again, so o > preliminaryMark below excludes it from the (redundant) correction.
        if (onset is not { } o || o <= preliminaryMark)
            return preliminaryMark;
        return Math.Max(0, o - DefaultMarkLeadSeconds);
    }

    /// <summary>
    /// Finds the VAD non-speech region (the jingle) a matched phrase belongs to, by
    /// <em>containment</em> rather than end-alignment: the last region that contains the phrase
    /// (<c>Start &lt;= phrase &lt;= End</c>) or brackets it within
    /// <see cref="JinglePhraseMatchToleranceSeconds"/> at either edge (VAD and Whisper time their
    /// boundaries slightly differently). This is deliberately robust to the "Kapitel N"
    /// announcement being spoken <em>inside</em> the jingle - Whisper then timestamps the phrase
    /// before the VAD region ends, so an end-alignment test would drop the region and the mark
    /// would fall back onto an unrelated earlier in-text pause, landing the chapter seconds early
    /// (the failure that motivated this). Because the mark is taken from the region's
    /// <see cref="JingleStart">start</see>, where the region <em>ends</em> - possibly inflated by
    /// <see cref="ComputeNonSpeechRegions"/>'s short-speech-gap merge swallowing the announcement -
    /// never affects placement. A region that starts after the phrase (a post-announcement pause)
    /// is excluded. Returns null when no region qualifies within the window.
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
    /// non-speech) that Whisper lumped into the head of the segment, so the timestamp points at
    /// the actual speech onset. Whisper timestamps a segment from where its decoded audio block
    /// begins; for the segment that carries a chapter announcement after a pause and/or a jingle,
    /// that is the start of the leading non-speech, not of the spoken phrase. Left uncorrected,
    /// the phrase's apparent start sits back in the previous chapter's trailing audio, which both
    /// mis-places the mark (the anchor logic keys off the phrase start) and feeds the
    /// --min-silence-length / --max-jingle-length auto mechanisms a mis-measured (wrong, usually
    /// shorter) silence. Both detectors' findings are available here - silencedetect down to
    /// <see cref="MinStoredSilenceSeconds"/>, plus VAD regions when the VAD pre-pass ran - so the real onset
    /// is the far end of the contiguous run of non-speech intervals that begins at (or a hair
    /// before, see <see cref="SegmentLeadTrimToleranceSeconds"/>) the segment's timestamp, chained
    /// through directly abutting intervals (a silence immediately followed by its jingle). The run
    /// is never followed past the segment's own end - a segment that matched a phrase always has
    /// some speech in it, so a leading run consuming the whole segment would be spurious.
    /// Segments are in absolute file time, matching the silence/region lists. Internal for unit
    /// testing.
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
        // The non-speech intervals a segment start can be advanced through: every stored silence
        // plus, when the VAD pre-pass ran, every VAD non-speech region.
        var intervals = allSilences.Select(s => (s.StartSeconds, s.EndSeconds));
        if (jingle)
            intervals = intervals.Concat(nonSpeechRegions.Select(r => (r.StartSeconds, r.EndSeconds)));
        var nonSpeech = intervals.ToList();

        return segmentsAbs.Select(seg =>
        {
            var onset = seg.StartSeconds;
            // Chase the run: any interval that begins at or just before the current onset and
            // extends past it (without spilling beyond the segment) pushes the onset to its end.
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
