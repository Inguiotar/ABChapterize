// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Cli;
using ABChapterize.Vad;

namespace ABChapterize.Detection;

/// <summary>
/// Every tuning constant <see cref="ChapterDetector"/> and its detection-geometry helpers use for
/// silence/jingle/phrase thresholds, search radii and safety margins. One catalog rather than a
/// value per consuming class: many are calibrated against one another (their doc comments name
/// which), and that is easiest to reason about - and re-tune - side by side.
/// </summary>
internal static class DetectionTuning
{
    /// <summary>
    /// Level below which ffmpeg's silencedetect counts audio as silence, in dBFS - the default of
    /// <c>--noise-floor</c>, and the value <see cref="SilenceThresholdProbe"/>'s automatic mode
    /// keeps unless a master's own levels argue against it.
    /// <para>
    /// Measured over the fourteen-book corpus of 2026-08-05 (eight 20 s excerpts per book, 50 ms
    /// RMS frames): -35 sits inside every one of those books' gap between room tone and speech,
    /// with room to spare at both ends. The quietest sustained speech was "I Shall Wear Midnight"
    /// at -25.1 dBFS (p75 of its frames) and the loudest room tone "The Philosopher's Stone" at
    /// -50.1 (p5), so the narrowest margins the value actually had were 9.9 dB below speech and
    /// 15.1 dB above hiss. Those two numbers are where <see cref="SpeechHeadroomDb"/> and
    /// <see cref="NoiseFloorHeadroomDb"/> come from, each rounded down for slack.
    /// </para>
    /// </summary>
    internal const double DefaultSilenceNoiseDb = -35;

    /// <summary>How many excerpts <see cref="SilenceThresholdProbe"/> decodes to judge a file's
    /// levels, spread evenly between the 5% and 95% marks of its play time - a book's opening
    /// label jingle and closing credits are not what its body sounds like. Eight 20 s excerpts cost
    /// about a second of ffmpeg seeking on a 15-hour .m4b (measured 2026-08-05), which is nothing
    /// beside the full decode Pass 1 is about to do anyway.</summary>
    internal const int NoiseProbeExcerpts = 8;

    /// <summary>Length of one <see cref="NoiseProbeExcerpts"/> excerpt, in seconds. Eight of these
    /// yield 3200 frames at <see cref="NoiseProbeFrameSeconds"/>, enough for the percentiles below
    /// to be stable.</summary>
    internal const double NoiseProbeExcerptSeconds = 20;

    /// <summary>Length of one RMS frame in the level histogram. Short enough that the pauses
    /// between words register as their own frames rather than being averaged into the speech
    /// around them, which is the whole point of measuring in frames at all.</summary>
    internal const double NoiseProbeFrameSeconds = 0.05;

    /// <summary>Percentile of the frame levels taken for the master's room tone. Low enough to be
    /// about the pauses rather than the speech, high enough not to be dominated by whatever
    /// stretches of true digital silence the file happens to contain.</summary>
    internal const int NoiseProbeFloorPercentile = 5;

    /// <summary>Percentile of the frame levels taken for "sustained speech" - a level continuous
    /// narration reliably exceeds. Not a peak: what has to stay above the threshold is the body of
    /// the speech, since silencedetect only declares silence after a run of quiet frames.</summary>
    internal const int NoiseProbeSpeechPercentile = 75;

    /// <summary>How far above the measured room tone the silence threshold must sit, in dB. Below
    /// this the hiss itself never counts as silence and the file yields no candidates at all.</summary>
    /// <remarks>
    /// The corpus's noisiest master ("The Philosopher's Stone", room tone -50.1 dBFS) left the
    /// default exactly 15.1 dB of room, so 15 is the largest value that would leave every reference
    /// book untouched - and a tenth of a decibel of margin is no margin at all, since re-measuring
    /// the same book from different excerpts moves the reading by more than that. Rounded down to
    /// 14 for a full decibel of slack. The same reasoning, in the other direction, gives
    /// <see cref="SpeechHeadroomDb"/>.
    /// </remarks>
    internal const double NoiseFloorHeadroomDb = 14;

    /// <summary>How far below sustained speech the silence threshold must sit, in dB. Above this
    /// the narration itself reads as silence and the file yields thousands of spurious candidates.
    /// The corpus's quietest speech ("I Shall Wear Midnight", p75 -25.1 dBFS) allowed at most 9.9;
    /// 8 for the margin, as <see cref="NoiseFloorHeadroomDb"/> explains.</summary>
    internal const double SpeechHeadroomDb = 8;

    /// <summary>The range an automatically chosen silence threshold is confined to, whatever the
    /// measurement says. A reading far outside it means the excerpts were unrepresentative (an
    /// entirely silent stretch, a corrupt decode) rather than that the book really is like that,
    /// and the fixed default has a far better record than an outlier would.</summary>
    internal const double MinAutoSilenceNoiseDb = -60;

    /// <inheritdoc cref="MinAutoSilenceNoiseDb"/>
    internal const double MaxAutoSilenceNoiseDb = -20;

    /// <summary>Probe window length in seconds when --max-jingle-length is 0 (no jingle
    /// expected). Above 0, the window is --max-jingle-length plus
    /// <see cref="PhraseMarginSeconds"/> instead, whether or not the VAD pre-pass ends up
    /// running - see <see cref="CliOptions.RunVadPrePass"/>.</summary>
    internal const double ProbeSecondsPlain = 12;

    /// <summary>
    /// The shortest silence Pass 1 keeps (see the <c>allSilences</c>/<c>silences</c> split in
    /// <see cref="ChapterDetector.DetectAsync"/>), regardless of --min-silence-length. Only
    /// silences at or above --min-silence-length ever become Pass 2 candidates or get logged;
    /// this lower floor exists purely so a window seam (see
    /// <see cref="GapPlanning.FindNearestSeam"/>) or a mark anchor can still snap to a silence
    /// mid-point when the nearest real one is shorter than the book's candidate threshold. Low
    /// enough to catch ordinary clause pauses without noticeably growing Pass 1's list.
    /// </summary>
    internal const double MinStoredSilenceSeconds = 0.5;

    /// <summary>
    /// How far past a Pass 2 window's natural end <see cref="GapPlanning.PlanWindowEnd"/> looks
    /// for a seam when that end has no next window to share a border with: the nearest silence -
    /// or VAD non-speech region, where the pre-pass ran - mid-point within this range becomes the
    /// window's end, so even a stand-alone window stops at a word-safe cut (a mid-word tail is
    /// exactly what makes Whisper garble a window's final phrase). Extension only: a target
    /// before the natural end could cut off the very phrase the probe exists to find. With
    /// nothing in reach the window keeps its natural length.
    /// </summary>
    internal const double WindowEndSnapSearchSeconds = 5.0;

    /// <summary>Without a VAD pre-pass, the phrase must start within this many seconds after the
    /// silence that triggered its probe (or a closer anchor silence still inside the window) to
    /// count as a real announcement rather than an in-text mention.</summary>
    internal const double PhraseLatestStart = 5.0;

    /// <summary>Flat margin added to --max-jingle-length so the phrase after the jingle still
    /// fits into the probe window.</summary>
    internal const double PhraseMarginSeconds = 5.0;

    /// <summary>
    /// The fallback lead <see cref="JingleGeometry.ComputeMarkBeforeJingle"/>'s step 5 backs off
    /// by when its backward walk runs out of VAD data before finding the previous chapter's real
    /// trailing narration - typically a jingle at the very start of the file, before chapter 1.
    /// The same flat 0.5 s used elsewhere as a last resort when nothing more precise is known.
    /// </summary>
    internal const double JingleLeadSeconds = 0.5;

    /// <summary>
    /// Default for <see cref="ABChapterize.Cli.CliOptions.MarkLeadSeconds"/> (--mark-lead): without
    /// --mark-before-jingle, the mark goes this many seconds before the detected phrase, whatever
    /// precedes it - no silence/jingle anchor is consulted for the timestamp at all, only for the
    /// --min-silence-length/--max-jingle-length auto statistics.
    /// </summary>
    /// <remarks>
    /// Raised from 0.25 to 0.35 on 2026-07-29 after listening to real marks: at 0.25 the mark lands
    /// so close to the announcement that a plosive onset - the /k/ of "Kapitel" - can be clipped
    /// without the listener being able to say whether they heard it. That consonant is the awkward
    /// case on purpose: a stop's burst carries almost no energy before its release, so neither
    /// Whisper's onset estimate nor an attentive ear resolves it to a tenth of a second, and the
    /// cheapest insurance is to start earlier. The refiner's own accuracy is not the limit here -
    /// it pins onsets to 0.1 s - the audible margin is.
    /// </remarks>
    internal const double DefaultMarkLeadSeconds = 0.35;

    /// <summary>
    /// Slack when matching a VAD non-speech region (the jingle) to a Whisper phrase. The region
    /// ends where VAD hears speech again, which should coincide with the phrase start, but
    /// Whisper's segment timestamps can be a touch earlier than VAD's frame-precise resume,
    /// leaving the region ending just <em>after</em> the phrase. Without this slack such a region
    /// is missed and a silence-less jingle falls back to the (possibly false) nearest silence.
    /// Far below any real jingle length or inter-chapter spacing, so it only absorbs boundary
    /// jitter and can never grab an unrelated later region.
    /// </summary>
    internal const double JinglePhraseMatchToleranceSeconds = 0.5;

    /// <summary>
    /// How far a candidate <see cref="JingleGeometry.LeadingSilence"/> may start after its VAD
    /// non-speech region's own start and still count as leading it, rather than being an
    /// unrelated silence deep inside a long region (see that method's remarks). A true lead-in
    /// silence and its region begin at essentially the same instant however long the hush runs,
    /// so this only absorbs detector jitter (VAD's frame granularity vs. silencedetect's onset
    /// timing) - observed well under 1 s on real audio, against false-candidate gaps of several
    /// seconds or more.
    /// </summary>
    internal const double LeadingSilenceStartToleranceSeconds = 1.5;

    /// <summary>
    /// How close a silence boundary and a VAD speech segment's end must be to describe the same
    /// transition, for <see cref="JingleGeometry.ComputeMarkBeforeJingle"/>'s step 2 ("does real
    /// narration end where this silence begins" - the plain in-narration-pause case, no jingle to
    /// walk back through). Reuses <see cref="LeadingSilenceStartToleranceSeconds"/>'s value under
    /// its own name: same silencedetect-vs-VAD jitter, different boundary pairing.
    /// </summary>
    internal const double JingleWalkAdjacencyToleranceSeconds = LeadingSilenceStartToleranceSeconds;

    /// <summary>
    /// How far past a jingle's own musical start a VAD speech blip swallowed into its region must
    /// begin before <see cref="JingleGeometry.ResolveDefaultPhraseOnset"/> will take it for the
    /// announcement rather than for the previous chapter's trailing words.
    /// <para>
    /// The two are told apart by which detector saw the gap in front of the blip. Silencedetect does
    /// not read jingle music as silence, so a blip that starts where the region's leading silence
    /// ends is speech resuming after a pause - in front of the music, not inside it. Measured on the
    /// two marks this was written for (2026-08-02): both blips began within 3 ms of that silence's
    /// end. This sits two orders of magnitude above that jitter and still far below where a real
    /// announcement sits from its jingle's start, since an announcement comes at the music's end.
    /// </para>
    /// </summary>
    internal const double PreJingleSpeechToleranceSeconds = 0.5;

    /// <summary>
    /// Longest stretch of VAD-speech "glue" the anchor-time jingle edge adjustment (see
    /// <see cref="JingleGeometry.AdjustJingleRegion"/>) steps across at the jingle's leading edge -
    /// both when trimming trailing-narration blips off a merged region's front and when bridging
    /// backward across an untranscribed music vocal to an earlier region the jingle was split
    /// into. Real trailing-narration fragments and mid-jingle vocals both run well under this
    /// (observed up to ~1.1 s); anything longer between two non-speech stretches is genuine
    /// narration the jingle cannot extend across.
    /// </summary>
    internal const double JingleGlueMaxSeconds = 3.0;

    /// <summary>
    /// Minimum overlap between a VAD non-speech region and the matched phrase's transcript-segment
    /// span for the smeared-phrase rescue (see
    /// <see cref="JingleGeometry.FindSmearedJingleRegion"/>) to accept that region as the jingle.
    /// Deliberately jingle-scale (matching <see cref="MinJingleObservationSeconds"/>): a correctly
    /// timed announcement's short segment barely grazes a following pause region, while a segment
    /// Whisper smeared across the jingle - the failure this rescues - overlaps it by many seconds.
    /// </summary>
    internal const double SmearedPhraseMinOverlapSeconds = 2.0;

    /// <summary>
    /// Slack when deciding a Whisper segment <em>starts with</em> a stored silence or VAD
    /// non-speech region (see <see cref="JingleGeometry.TrimLeadingNonSpeech"/>). Whisper
    /// timestamps a segment from where its decoded audio block begins, which can be a touch before
    /// the frame-precise onset; without this slack a silence starting a hair after the segment's
    /// timestamp would not count as leading it. Small enough never to trim a segment that
    /// genuinely opens with speech.
    /// </summary>
    internal const double SegmentLeadTrimToleranceSeconds = 0.5;

    /// <summary>
    /// The shortest span this codebase treats as "plausibly a real jingle", used two ways.
    /// (1) A VAD non-speech region whose longest contiguous run falls below it (see
    /// <see cref="JingleGeometry.ComputeNonSpeechRegions"/> for why the longest run, not the
    /// merged span) is dropped rather than ever becoming a candidate: too short for a jingle at
    /// any book's pacing, more likely a breath pause VAD called non-speech. (2) With
    /// --max-jingle-length auto, an observed phrase offset below it means "this chapter had no
    /// jingle (or an ultra-short one)" and is excluded from tightening the probe window - some
    /// books only play the jingle for some chapters, and such a chapter says nothing about the
    /// window a full-length jingle needs.
    /// </summary>
    internal const double MinJingleObservationSeconds = 2.0;

    /// <summary>
    /// With the VAD pre-pass, a "speech" segment shorter than this between two non-speech regions
    /// does not end the surrounding jingle - the regions are merged and the blip treated as VAD
    /// noise. Silero VAD is not reliable on jingle music: a vocal-like transient or a strong
    /// rhythmic passage can cross its speech threshold for a fraction of a second inside an
    /// otherwise instrumental jingle, fragmenting one continuous jingle into several too-short
    /// regions (see <see cref="JingleGeometry.ComputeNonSpeechRegions"/>). Well below any real
    /// inter-chapter narration gap, so a genuine speech resume is never merged away.
    /// </summary>
    internal const double MergeShortSpeechGapSeconds = 1.0;

    /// <summary>
    /// The speech-duration floor <see cref="JingleGeometry.AdvancePastNonSpeech"/> uses to tell a
    /// genuine spoken onset from a jingle's musical/vocal transients (or a Whisper hallucination
    /// inside one) that VAD still calls "speech". Calibrated against real audio (see
    /// <c>tools\vadprobe</c>'s sweep data): the shortest such transient measured 0.352 s and
    /// survived raising <see cref="VadSegmenter.Threshold"/> as far as 0.70, while the shortest
    /// genuine announcement word measured 0.608 s - this sits roughly midway, erring toward not
    /// skipping real speech, since another supported language could plausibly be shorter than the
    /// one German data point. Deliberately tighter than
    /// <see cref="MergeShortSpeechGapSeconds"/>'s cluster-grouping gap: this rejects a single
    /// too-short blip, that decides whether separate blips belong to the same cluster.
    /// </summary>
    internal const double TransientSpeechFloorSeconds = 0.4;

    /// <summary>
    /// Non-speech an announcement must have in front of it before
    /// <see cref="AnnouncementIsolation"/> accepts it: the pause (or jingle) separating it from the
    /// previous section's narration. Applies to a bare number and to the prologue/epilogue.
    /// <para>
    /// Bounded by measurement 2026-08-05, replaying the guard over each run's own Pass 1 speech
    /// segments across the fourteen-book corpus. Every genuine announcement clears it with room:
    /// "Corsa nello spazio"'s 65 bare numbers sit at 3.0-3.9 s, and the corpus's twelve genuine
    /// prologue/epilogue/<c>--custom</c> marks range from 1.56 s ("The Forever War"'s epilogue, the
    /// tightest) to 36 s. The false positive this exists to stop - <c>/epilogo/</c> matching inside
    /// Italian "riepilogo" mid-sentence - measures 0.64 s. So anything in (0.64, 1.56) is defensible
    /// and this sits low in that window: the corpus is fourteen books, the tightest genuine mark in
    /// it is one data point, and an announcement dropped for want of a pause is a chapter lost
    /// outright, whereas the false positives this catches are all far below 0.64 s rather than
    /// clustered just under the line.
    /// </para>
    /// </summary>
    internal const double AnnouncementLeadInSeconds = 0.85;

    /// <summary>
    /// Non-speech a <em>bare number</em> announcement must have after it as well
    /// (<see cref="IsolationRule.Both"/>). Only bare numbers: the number is spoken alone, so the
    /// pause behind it is as much a part of its shape as the one in front, and on "Corsa nello
    /// spazio" every chapter measured 1.0-2.2 s there - the tightest being chapter 20 at 0.99 s -
    /// against 0.73 s for the false epilogue. Set well below that tightest real measurement rather
    /// than just below it, for the same reason as the lead-in: one book's narrator sets the pace of
    /// these pauses, and another's need not be so generous.
    /// <para>
    /// Deliberately <em>not</em> asked of the prologue and epilogue, and not of <c>--custom</c> at
    /// all. A heading word is routinely run straight into the text that follows it, and the corpus
    /// has it both ways round: Gruelfin's "Zeittafel" leaves 0.16 s behind it and "I Shall Wear
    /// Midnight"'s epilogue 0.44 s, both genuine, so at this threshold the first would still be
    /// thrown away and the second only just survives. The lead-in alone already rejects every
    /// mid-sentence false positive on record, so there is nothing to buy here and a real mark to
    /// lose.
    /// </para>
    /// </summary>
    internal const double AnnouncementLeadOutSeconds = 0.3;

    /// <summary>
    /// How far past a VAD speech segment's end an announcement onset may still be counted as
    /// belonging to it (<see cref="AnnouncementIsolation.Measure"/>). Absorbs the disagreement
    /// between the two clocks involved: the refinement anchors an onset to where the waveform's
    /// sound resumes (<see cref="PreciseMarkRefiner.AnchorOnsetToSoundAsync"/>), while VAD needs a
    /// few frames of speech-like signal before it commits - so an onset can land just inside the
    /// tail of the preceding segment. One <see cref="VadSegmenter.MinSilenceSeconds"/> hangover is
    /// the natural size for that, and it is far below any real inter-chapter pause.
    /// </summary>
    internal const double OnsetSegmentToleranceSeconds = 0.1;

    /// <summary>
    /// Minimum letters-plus-digits per second a transcript segment must average for
    /// <see cref="JingleGeometry"/>'s corroboration checks (<c>IsGenuineSpeech</c>) to accept it
    /// as evidence that a VAD blip is real narration rather than jingle music. "The transcript
    /// covers this blip with real words" hides an assumption: ordinary narration runs several
    /// times this fast, so coverage at a fraction of that pace is not continuous speech but a long
    /// near-silent stretch of music/reverb that Whisper folded into one oversized segment together
    /// with a few real words at its edges (one merged segment was observed spanning almost 30 s
    /// while containing a couple of seconds of speech). Confirmed on real audio (Perry Rhodan
    /// "Die Dritte Macht", chapters 8 and 10, 2026-07-26): two genuine segments measured roughly
    /// 10-12 letters/second, two smeared ones roughly 0.4-1.0 - an order of magnitude apart. Set
    /// well below the real-narration floor, leaving margin for slow delivery or short,
    /// punctuation-heavy segments.
    /// </summary>
    internal const double MinPlausibleSpeechCharsPerSecond = 3.0;

    /// <summary>
    /// Ceiling on a VAD speech blip's duration for <see cref="MinPlausibleSpeechCharsPerSecond"/>'s
    /// pace check to apply at all: only a blip this short could plausibly <em>be</em> the brief
    /// transient that check exists to unmask - the premise behind
    /// <see cref="TransientSpeechFloorSeconds"/>, from the other side. A blip many seconds long is
    /// substantial spoken content by duration alone, whatever an overlapping segment's timestamps
    /// say, and scrutinising its pace only risks rejecting it over <em>that segment's</em>
    /// smearing (a regression this fixed: a 640 s blip covering an entire preceding chapter was
    /// rejected because the only segment reaching that far was itself a smeared announcement).
    /// Comfortably above the longest wrongly-corroborated blip observed (under 2 s in both
    /// real-audio cases above) and below ordinary narration blips, commonly several seconds once
    /// mid-sentence micro-pauses are cleared.
    /// </summary>
    internal const double MaxPaceScrutinizedBlipSeconds = 2.0;

    /// <summary>
    /// Length of the decode precise marking transcribes to check whether a mark's chapter phrase
    /// is really the first thing heard there (see
    /// <see cref="PreciseMarkRefiner.RefinePreciseMarkAsync"/>). A real announcement is never
    /// anywhere near this long and a jingle - the only other thing a mark can land on - rarely
    /// shorter, so one window normally tells the two apart without probes of increasing length.
    /// </summary>
    internal const double PreciseMarkCheckWindowSeconds = 4.0;

    /// <summary>
    /// Real audio lead-in precise marking decodes before every position it checks, widening the
    /// window backward rather than shifting it so no
    /// <see cref="PreciseMarkCheckWindowSeconds"/> of fresh audio is lost off the tail. A
    /// VAD-detected onset can lag the true word-start (a soft consonant takes VAD's amplitude
    /// threshold a moment to cross); without this margin, decoding from exactly such an onset can
    /// clip the leading sound enough that Whisper drops the word entirely rather than mishearing
    /// it - confirmed on real audio (see <c>tools\vadprobe</c>'s <c>precise</c> prototype). A
    /// synthetic silence lead-in was tried first and rejected: it made Whisper misrecognize the
    /// next word's leading consonant right at the padding boundary ("Kapitel" heard as "Spitel"),
    /// which real audio never showed. Kept well under a syllable's worth rather than the few
    /// tenths the onset lag can reach: too much pulls trailing syllables of whatever precedes the
    /// phrase into the window.
    /// </summary>
    internal const double PreciseMarkLeadInSeconds = 0.1;

    /// <summary>
    /// How far behind the pre-walk mark --mark-before-jingle's backward walk must have landed
    /// before <see cref="PreciseMarkRefiner.VerifyMarkBeforeJingleAsync"/> probes the result at
    /// all. The probe decodes <see cref="PreciseMarkCheckWindowSeconds"/> forward from the walked
    /// mark while the announcement it retreated from starts
    /// <see cref="DefaultMarkLeadSeconds"/> after the pre-walk mark; any smaller gap puts that
    /// announcement inside the probe's own window, where "still audible" is a foregone conclusion
    /// that reads a short jingle, a deliberate "no jingle here" outcome and a failed walk exactly
    /// alike. Below this gap the walk is trusted unprobed.
    /// </summary>
    internal const double MarkBeforeJingleVerifyMinGapSeconds =
        PreciseMarkCheckWindowSeconds - DefaultMarkLeadSeconds;

    /// <summary>
    /// How much audio <see cref="MarkLoudness.MeasureDbfsAsync"/> averages when reporting a
    /// finished mark's level under --verbose. Long enough that one near-zero-crossing sample
    /// cannot pass a loud passage off as silence, short enough to still describe the mark itself
    /// rather than the sentence after it.
    /// </summary>
    internal const double MarkLoudnessWindowSeconds = 0.25;

    /// <summary>
    /// The finest granularity precise marking probes at: the resolution both of its bisections -
    /// <see cref="PreciseMarkRefiner.FindOnsetEdgeAsync"/> and
    /// <see cref="PreciseMarkRefiner.FindPhraseSurvivalEdgeAsync"/> - narrow their bracket down to,
    /// and the step <see cref="PreciseMarkRefiner.VerifyMarkBeforeJingleAsync"/>'s backward scan
    /// advances by. The first makes it the accuracy with which the plateau's right edge is located:
    /// the position returned confirms and the position one step later does not. That is a statement
    /// about the plateau, not about the announcement - the two are up to half a second apart, which
    /// is what <see cref="PreciseMarkSilenceAnchorSeconds"/> exists to close. Matches
    /// <see cref="PreciseMarkLeadInSeconds"/>'s magnitude - both are about the finest granularity
    /// worth probing at, given <see cref="PreciseMarkCheckWindowSeconds"/> - rather than some
    /// unrelated value.
    /// </summary>
    internal const double PreciseMarkFixedStepSeconds = 0.1;

    /// <summary>
    /// How far back from the survival edge
    /// <see cref="PreciseMarkRefiner.FindPhraseSurvivalEdgeAsync"/> reported that
    /// <see cref="PreciseMarkRefiner.LocatePhraseByShrinkingWindowAsync"/> tries, in turn, to place
    /// a probe the ordinary <see cref="PreciseMarkCheckWindowSeconds"/> check will confirm.
    /// <para>
    /// The edge itself is the wrong place to ask: it is the last position from which the phrase
    /// still survives being cut off at the front, so it sits <em>at or just past</em> the onset -
    /// Whisper tolerates losing the first few tens of milliseconds of a word and still spells it
    /// correctly. A check window opening there starts mid-syllable, and the fragment it transcribes
    /// no longer matches the phrase. Each backoff steps further into the quiet (or the jingle)
    /// ahead of the announcement, where the phrase is cleanly the first thing heard.
    /// </para>
    /// <para>
    /// Geometric rather than fixed-step, and only four of them, because this is a foothold hunt and
    /// not a measurement: whichever one lands, <see cref="PreciseMarkRefiner.FindOnsetEdgeAsync"/>
    /// walks forward from it to the onset anyway, so a backoff that overshoots into the jingle costs
    /// that walk one or two extra probes and nothing else. The last one is deliberately larger than
    /// a typical announcement's lead-in silence: past that, a "no" means the phrase is genuinely not
    /// where the edge claimed, which is worth learning quickly rather than creeping toward.
    /// </para>
    /// </summary>
    internal static readonly double[] PreciseMarkFootholdBackoffsSeconds = [0.0, 0.5, 1.5, 4.0];

    /// <summary>
    /// How far past a converged plateau edge <see cref="PreciseMarkRefiner.FindOnsetEdgeAsync"/>
    /// looks for the plateau resuming, and at what spacing - the guard against bisecting a
    /// predicate that turned out not to be the single clean step function it was built for.
    /// <para>
    /// The failure this exists to catch, measured 2026-07-31 on Die Dritte Macht chapter 7
    /// (announcement "Kapitel 7" truly at 10784.5 s, established with <c>tools\wprobe</c>: a window
    /// ending at 10784.60 transcribes only "[Musik]", one ending at 10784.80 catches "Kapitel").
    /// The ordinary check answered, at 0.1 s spacing with the ggml-small model:
    /// yes at 10783.44, <em>no</em> from 10783.49 to 10784.04, yes again at 10784.24 and 10784.44,
    /// no from 10784.54 onward. That middle "no" run is a hole in the plateau roughly 0.8 s wide
    /// sitting 0.45-1.0 s before the true onset, not the plateau's end: the gallop struck it at
    /// 10783.84, bisected between 10783.04 and 10783.84, and returned the hole's left edge, putting
    /// the mark 1.16 s early. The pre-refinement heuristic mark had been 0.04 s off, so refinement
    /// made an already-good mark worse - the only imperfect mark in a seven-book run.
    /// </para>
    /// <para>
    /// The hole is a property of the model, not of the audio: the same grid run against
    /// ggml-large-v3-turbo is perfectly monotone with no hole at all, its last yes at 10784.64 -
    /// 0.2 s past the truth, because a bigger model reconstructs a clipped first syllable - which
    /// <see cref="PreciseMarkRefiner.OnsetOf"/>'s lead-in subtraction absorbs. So both models land
    /// within a fifth of a second once the search looks for the <em>rightmost</em> plateau rather
    /// than stopping at the first edge it meets.
    /// </para>
    /// <para>
    /// Spacing and reach are calibrated against that one measured hole and are deliberately coarse:
    /// this is a "did the plateau resume?" question, and whichever probe lands, the walk restarts
    /// from it and re-derives the edge at the usual
    /// <see cref="PreciseMarkFixedStepSeconds"/> accuracy. 0.3 s spacing puts a probe inside a
    /// resumed plateau as narrow as the 0.2 s one measured there (10784.24-10784.44); 1.2 s of
    /// reach clears a hole half again as wide as the one observed. Four probes is what that costs
    /// every refinement, holes or not - about 4 s per mark, ~1.5 minutes on a book with 30
    /// chapters, against a mark that would otherwise be a second out.
    /// </para>
    /// </summary>
    internal static readonly double[] PreciseMarkPlateauProbesSeconds = [0.3, 0.6, 0.9, 1.2];

    /// <summary>
    /// How many times <see cref="PreciseMarkRefiner.FindOnsetEdgeAsync"/> may restart its walk after
    /// finding the plateau resuming past an edge (see
    /// <see cref="PreciseMarkPlateauProbesSeconds"/>). A bound rather than a measurement: one
    /// resume is all the observed failure needs, and each one is required to move strictly later, so
    /// this only caps a pathological alternation of holes and plateaus - which would otherwise cost
    /// an unbounded number of transcriptions on a single mark.
    /// </summary>
    internal const int PreciseMarkPlateauResumeLimit = 2;

    /// <summary>
    /// The shortest stretch of audio <see cref="PreciseMarkRefiner.PhraseSurvivesFromAsync"/> will
    /// put in front of Whisper, however little is left between the probe position and the search's
    /// end anchor. Its answer is only a step function while it is also <em>reliable</em>, and
    /// reliability collapses on a short clip: a probe of a stretch that a longer window transcribes
    /// confidently comes back as a coin flip, and one flip in the wrong direction moves the bisection
    /// into the half that does not hold the onset, where every further probe agrees with it.
    /// <para>
    /// Measured on Stalker.m4b's "Zeittafel" (true onset 52.7 s, end anchor 54.19 s - the detecting
    /// window's own end, which left barely a second of the announcement inside the bracket,
    /// 2026-07-30): the same phrase came back at p=0.84 from a 6.15 s clip, p=0.54 from 4.55 s and
    /// p=0.49 from 2.95 s, and it was the 2.95 s one that failed on the run's GPU and sent the edge
    /// back 4.7 s early - past which no foothold could be confirmed and the mark was left at its
    /// (30 s early) default position. At 6 s the whole sweep from 46 s to 53.5 s is monotone across
    /// the onset, and BARDIOC.m4b's chapter 21 bisects to the same edge either way.
    /// </para>
    /// <para>
    /// Not larger, although "more audio, surer answer" is the obvious extrapolation: it is wrong on
    /// the very audio this exists for. A jingle is music, Whisper writes music off as a single
    /// "[Musik]" segment, and the more of it a window holds the likelier the announcement goes with
    /// it - at 8 s the same Stalker probe from 48.04 s came back "[Musik]" where the unextended
    /// 6.15 s window had read "Zeittafel" cleanly. The floor has to clear the coin-flip zone without
    /// buying more music than it has to.
    /// </para>
    /// <para>
    /// The extension reaches past the end anchor, i.e. into audio the detecting window never saw.
    /// That is sound for the question being asked - "is the announcement still in front of me" - as
    /// long as it cannot reach a <em>second</em> occurrence of the phrase, which at this length means
    /// the announcement would have to be repeated within a few seconds of itself.
    /// </para>
    /// </summary>
    internal const double PreciseMarkMinSurvivalSeconds = 6.0;

    /// <summary>
    /// How far before its end anchor <see cref="PreciseMarkRefiner.LocatePhraseByShrinkingWindowAsync"/>
    /// will look for the announcement when the matched segment's own start timestamp does not reach
    /// that far back on its own. The bracket is normally drawn one
    /// <see cref="PhraseMarginSeconds"/> below that timestamp, which quietly assumes Whisper never
    /// times a segment <em>later</em> than the words in it - and it does: chapter 21 of BARDIOC.m4b
    /// was announced at 12:26:33.4 and its segment timestamped 12:26:38.6 (2026-07-30, a 5.2 s lag),
    /// putting the whole bracket past the announcement so that no probe inside it could ever hear it.
    /// The mark was left where default-mode placement had put it, 1.1 s into the spoken announcement.
    /// <para>
    /// Widening the floor cannot move the answer, only find it: the survival edge is the
    /// <em>largest</em> position the phrase still survives from, which does not depend on how far
    /// below it the search is allowed to reach - an earlier floor only gives the backward gallop
    /// somewhere to land instead of running out of bracket. The cost is a handful of extra probes,
    /// and only on a mark that was going to fail anyway.
    /// </para>
    /// <para>
    /// It extends the search's reach only, never where the gallop sets out from: that first probe
    /// spans the whole distance to the end anchor and is the longest window the search ever asks
    /// about, so starting it a stretch further back buys nothing and risks the failure mode in
    /// <see cref="PreciseMarkMinSurvivalSeconds"/>'s remarks - the same Stalker.m4b probe read
    /// "Zeittafel" over 15.65 s from the segment bracket and "[Musik]" over 25 s from here.
    /// </para>
    /// <para>
    /// Sized to one <see cref="WhisperChunkSeconds"/> less a phrase margin of headroom. Past a chunk
    /// a window re-segments differently for a shift of a few hundred milliseconds, which is what
    /// makes the predicate stop being a step function - see
    /// <see cref="PreciseMarkRefiner.FindPhraseSurvivalEdgeAsync"/>'s remarks on the two measurements
    /// that established it.
    /// </para>
    /// </summary>
    internal const double PreciseMarkMaxBracketSeconds = WhisperChunkSeconds - PhraseMarginSeconds;

    /// <summary>
    /// The stride whisper.cpp decodes in: it converts audio to a mel spectrogram of exactly this
    /// length at a time, so a window at or above it is transcribed as several passes whose results
    /// are stitched together, while a shorter one is a single pass over the whole thing.
    /// <para>
    /// This is not an implementation detail that stays inside the recognizer, which is why it has a
    /// name here: an announcement is one or two words against minutes of narration, and crossing the
    /// boundary is enough to lose it. Gruelfin.m4b's prologue (2026-07-30) is the case on record -
    /// the identical decode starting at 0:03:16.18 yields "Prolog. Die Jahre des Versteckspiels..."
    /// at 17.5 s and at 23.5 s, and at 30.0 s and 50.0 s yields the narration alone with the word
    /// gone. It was a 50 s window that probed it live, because --max-jingle-length auto had not yet
    /// seen a jingle to narrow the window with, and the prologue was simply never heard.
    /// </para>
    /// </summary>
    internal const double WhisperChunkSeconds = 30.0;

    /// <summary>
    /// Length of the second, deliberately short look <see cref="RegionProber.RereadJingleSpeechAsync"/>
    /// takes at a probe window that heard no announcement although VAD heard speech inside its
    /// jingle. Sized like <see cref="PreciseMarkMaxBracketSeconds"/> and for the same reason: the
    /// whole point of the re-read is to get the announcement into a single-pass decode, so it has to
    /// stay clear of <see cref="WhisperChunkSeconds"/>, with a phrase margin of headroom in case the
    /// window ends up anchored a little later than planned.
    /// </summary>
    internal const double JingleRereadWindowSeconds = WhisperChunkSeconds - PhraseMarginSeconds;

    /// <summary>
    /// Floor under the --max-jingle-length auto probe window (see
    /// <see cref="RegionProber.ObserveJingleLength"/>), applied beneath the ceiling so an explicit
    /// --max-jingle-length still wins outright. A book whose jingles are all short would otherwise
    /// narrow the window to a width the recognizer handles badly, and the announcement is then lost
    /// in exactly the windows the narrowing was supposed to make cheap.
    /// <para>
    /// The failure is the recognizer's, not the geometry's. Whisper pads anything shorter than
    /// <see cref="WhisperChunkSeconds"/> out to a full mel chunk, and the large models degenerate on
    /// the padding: the whole window comes back as one unpunctuated run-on segment with the
    /// announcement simply missing from it. Measured on "Paula Monti.m4b" (French, 4:33:37, 25
    /// chapters, 2026-08-08), whose 4.48 s longest jingle narrows the window to 10.6 s - replaying
    /// that run's own Pass 2 probe windows through the real recognizer, ggml-large-v3-turbo matched
    /// the announcement in 4 of 15 windows at 11-22 s and in 13 of 13 at 50 s, while ggml-small
    /// matched 14 of 14 at the same narrow widths. That is why the book detected perfectly under
    /// <c>-m small</c> and lost chapters 19 and 21-25 under turbo, and why the floor is a property of
    /// the window rather than of the model.
    /// </para>
    /// <para>
    /// One phrase margin below the chunk rather than at it, which the same five windows settle by
    /// measurement: 0 of 5 at 20 s, 5 of 5 at 25 s, 4 of 5 at 30 s and at 50 s. Chapter 22 is the one
    /// that only 25 s finds. Crossing into a multi-pass decode is its own way of losing an
    /// announcement - see <see cref="WhisperChunkSeconds"/> for Gruelfin.m4b's prologue, heard at
    /// 23.5 s and gone at 30.0 s - so the floor keeps every probe a single pass. Same value and the
    /// same reasoning as <see cref="JingleRereadWindowSeconds"/>.
    /// </para>
    /// </summary>
    internal const double MinAdaptiveProbeSeconds = WhisperChunkSeconds - PhraseMarginSeconds;

    /// <summary>
    /// How far <em>before</em> a confirmed or left-as-is mark
    /// <see cref="PreciseMarkRefiner.SnapToQuietestPointAsync"/> may search for a quieter point to
    /// move it to. Backward-only, so a one-sided lookback rather than a window centered on the
    /// mark, and independent of (and larger than) the candidate search step size above.
    /// </summary>
    internal const double PreciseMarkQuietSnapRadiusSeconds = 0.15;

    /// <summary>
    /// Width of the sliding RMS window <see cref="PreciseMarkRefiner.SnapToQuietestPointAsync"/>
    /// scans across <see cref="PreciseMarkQuietSnapRadiusSeconds"/>. Short enough to land inside a
    /// genuine micro-pause between words rather than averaging across most of one, long enough
    /// that a single sample near a zero-crossing in loud audio cannot masquerade as a quiet spot.
    /// </summary>
    internal const double PreciseMarkQuietWindowSeconds = 0.01;

    /// <summary>
    /// Minimum power-ratio improvement, in dB, a backward candidate within
    /// <see cref="PreciseMarkQuietSnapRadiusSeconds"/> must offer before
    /// <see cref="PreciseMarkRefiner.SnapToQuietestPointAsync"/> nudges the mark to it. 6 dB is a
    /// 4x power ratio - comfortably audible, not noise-floor jitter - so a nudge only happens for
    /// a genuine improvement, never as a coin flip between two near-identical spots.
    /// </summary>
    internal const double PreciseMarkQuietSnapMinImprovementDb = 6.0;

    /// <summary>
    /// How far behind a refined onset <see cref="PreciseMarkRefiner.PrecedingSilenceEnd"/> will
    /// look for the silence <see cref="PreciseMarkRefiner.AnchorOnsetToSoundAsync"/> scans forward
    /// from, i.e. how much of a gap between a silence's end and the reported onset is still read as
    /// "that pause is the one this announcement follows" rather than as unrelated audio in between.
    /// <para>
    /// The correction exists because the plateau <see cref="PreciseMarkRefiner.FindOnsetEdgeAsync"/>
    /// walks does not end where the announcement starts. Its right edge is where Whisper stops
    /// recognizing the phrase from a window cut into it, and Whisper reconstructs a clipped leading
    /// word remarkably well: "Chapter Five" read from audio starting a third of the way into
    /// "Chapter" still comes back as "Chapter 5". So the reported onset is biased <em>late</em>, and
    /// a mark placed one --mark-lead before it lands on the announcement instead of in front of it.
    /// The effect is per-narrator, not per-language: it cost nothing on the German jingle books and
    /// broke five marks across the two English ones and the French one.
    /// </para>
    /// <para>
    /// Measured over the fourteen-book run of 2026-08-03 (328 refinements that reported an onset,
    /// debug logs replayed): the gap between a refined onset and the end of the silence in front of
    /// it is 0.00-0.57 s for 114 marks and 4.5 s or more for the remaining 214, with nothing at all
    /// in between - the second group being the jingle books, where music, not silence, precedes the
    /// announcement and there is no boundary to anchor to. Any threshold from 0.75 s to 4.5 s
    /// therefore produces the identical result on that corpus; 1 s sits in it with room on both
    /// sides. Not one of the 328 had its onset land <em>before</em> the preceding silence ended,
    /// which is what makes a backward-only correction the right shape.
    /// </para>
    /// </summary>
    internal const double PreciseMarkSilenceAnchorSeconds = 1.0;

    /// <summary>
    /// How far below the loudest thing in its window <see cref="PreciseMarkRefiner.AnchorOnsetToSoundAsync"/>
    /// still counts audio as the pause rather than as the announcement. Relative to that peak - the
    /// announcement's own level - rather than an absolute dBFS figure, so a quietly mastered book
    /// and a loud one behave identically and nothing needs recalibrating per file. That is also why
    /// no loudness normalization pass is wanted upstream: it would change every level in the file
    /// and leave this test measuring exactly the same ratio.
    /// <para>
    /// 25 dB is a power ratio of about 300. Comfortably above the room tone a narrator's pause is
    /// actually made of - measured at 50-58 dB below the announcement on the two cases this was
    /// calibrated against, Paula Monti chapter 19 and The Philosopher's Stone chapter 5 - and
    /// comfortably below the announcement's own opening consonant, which on Philosopher's Stone
    /// chapter 5 is the affricate of "Chapter", reaching within 27 dB of the peak in its first
    /// 10 ms.
    /// </para>
    /// </summary>
    internal const double PreciseMarkOnsetFloorDb = 25;

    /// <summary>
    /// How long audio must stay above <see cref="PreciseMarkOnsetFloorDb"/> before
    /// <see cref="PreciseMarkRefiner.AnchorOnsetToSoundAsync"/> accepts it as the announcement
    /// beginning rather than as a click inside the pause. The failure it exists for is on record:
    /// silencedetect closed Paula Monti chapter 19's pause at 3:18:50.72 on a -47 dBFS mouth noise
    /// lasting 20 ms, while "Première" did not begin until 3:18:51.13 - so a rule that stopped at
    /// the silence's end alone put that mark 0.39 s early. Long enough to outlast such a transient,
    /// short enough that no real speech onset is missed: a syllable runs several times this.
    /// </summary>
    internal const double PreciseMarkOnsetSustainSeconds = 0.05;

    /// <summary>
    /// How many announcements must be rejected as below the sequence, their own numbers ascending,
    /// before <see cref="RegionProber.SequenceRestartSkips"/> reports the file as one whose chapter
    /// numbering restarts (see <see cref="RegionProber.NoteOutOfSequence"/>).
    /// <para>
    /// A single announcement numbered below the last accepted one is the ordinary in-text mention
    /// ("as I said in chapter three") the rejection message already guesses at; a run of them
    /// climbing 1, 2, 3 is a book divided into parts. Measured over the fourteen-book run of
    /// 2026-08-03: thirteen books produced no such rejection at all - every rejection they had was
    /// a re-detection of the chapter just accepted, equal to it rather than below it - and "The
    /// Forever War", which restarts its numbering in each of its parts, produced 27 of them in two
    /// runs of 9 and 8. Three is a comfortable floor between those.
    /// </para>
    /// </summary>
    internal const int SequenceRestartRunLength = 3;

    /// <summary>
    /// With --max-jingle-length auto, the resized probe window is this factor times the longest
    /// jingle observed so far (plus <see cref="PhraseMarginSeconds"/>), i.e. a 25 % margin for
    /// normal length variation between chapters. Monotonic: after the first observation (at the
    /// second mark) sets the window, later ones can only widen it - a window below an observed
    /// jingle length would by definition have been too short for that chapter. The exact mirror of
    /// <see cref="AdaptiveTightenFactor"/>.
    /// </summary>
    internal const double JingleObservationSafetyFactor = 1.25;

    /// <summary>
    /// Ceiling on how far a single gap-recovered chapter may widen the --max-jingle-length auto window
    /// (see <see cref="RegionProber.ProposeJingleWindow"/>): at most this factor times the window in
    /// effect, so a reach far above it takes several recoveries to be honoured in full instead of one
    /// jump. Deliberately the same 1.25 as <see cref="JingleObservationSafetyFactor"/> - both answer
    /// the same question, how far a single observation may move a running maximum.
    /// <para>
    /// The cap exists because the window's cost is superlinear in a way the reach figure alone does not
    /// reveal. Measured on BARDIOC.m4b (2026-07-30, 15 h 39 min, 1575 silences, so ~36 s average
    /// spacing): at the ~24 s window the run actually used, most candidate windows do not touch each
    /// other and Pass 2 decoded 593 minutes of audio in 1659 probes (434 in the main loop, 158 inside
    /// the four gap re-probes). Chapter 10's reach was ~38.5 s, which uncapped proposes ~43.5 s - and
    /// at that width nearly every candidate overlaps its neighbour, so decoding approaches covering the
    /// whole file once, ~939 minutes. Because the adapted window is a monotonic maximum, one outlier
    /// chapter would hold that width for the remaining ~10 hours of the book: several hundred minutes
    /// added to save the ~85 the re-probes cost. Capped, that same chapter lifts 24 s to 30 s, which
    /// still covers the 19-25 s reaches the other three recoveries needed.
    /// </para>
    /// </summary>
    internal const double GapReachGrowthFactor = 1.25;

    /// <summary>
    /// With --min-silence-length auto, the Pass 2 probing threshold is this factor times a mark's
    /// anchor silence length, i.e. a 25 % margin below the shortest observed inter-chapter break,
    /// mirroring <see cref="JingleObservationSafetyFactor"/>. Monotonic: the first qualifying mark
    /// (the second one found) raises the threshold off the floor, every later mark can only lower
    /// it again - a threshold above an observed inter-chapter silence would by definition skip the
    /// very kind of silence proven to precede this book's chapters.
    /// </summary>
    internal const double AdaptiveTightenFactor = 0.75;

    /// <summary>
    /// How short a silence --min-silence-length auto may talk itself down to
    /// (<see cref="RegionProber.ProposeThreshold"/>), independent of the length the run
    /// <em>starts</em> at. The two are separate questions: --min-silence-length is the demand a run
    /// opens with, this is how far the book's own inter-chapter breaks may argue it down once they
    /// have been measured. Pass 1's candidate list is cut here rather than at --min-silence-length
    /// (see <see cref="CliOptions.ProbeSilenceFloorSeconds"/>), because a threshold below the
    /// shortest candidate in existence would skip nothing and the whole lowering half of the
    /// adaptation was unreachable while the two values were one.
    /// <para>
    /// 0.8 s is where the evidence sits. "Paula Monti" (2026-07-31, 4 h 34 min, French) lost five
    /// chapters to pauses of 1.39-1.49 s - that narrator's non-jingle chapter break simply sits on
    /// the 1.5 s line - and "Die Dritte Macht" (2026-07-28, 15 h 10 min) had a genuine inter-chapter
    /// silence of 0.54 s. Below <see cref="MinStoredSilenceSeconds"/> nothing is retained by Pass 1
    /// to probe in the first place, so the reachable range is [0.5, 1.5); 0.8 covers the sub-floor
    /// sweep's whole span (<see cref="SubFloorSweepBandCount"/> bands reaching 1.0 s) with room to
    /// spare while leaving the very shortest stored band - ordinary clause pauses, which is what
    /// that floor was chosen to catch - out of Pass 2's reach.
    /// </para>
    /// <para>
    /// The cost is real and worth knowing before this number is moved again: because
    /// <see cref="RegionProber.ProposeThreshold"/> keeps a running <em>minimum</em>, one tight
    /// transition anywhere in a book pins the threshold at this floor for the rest of the run - on
    /// "Die Dritte Macht" that 0.54 s silence proposes 0.41 s, i.e. the floor, from the chapter it
    /// belongs to onwards. Every silence at or above the floor is then probed. The order of
    /// magnitude, measured on "Stalker.m4b" (2026-07-28, 13 h 28 min): 1032 silences of >= 1.5 s
    /// against 5962 of >= 1.0 s. Lowering this floor buys reach into exactly the pauses Pass 2.5's
    /// sub-floor sweep was invented to reach after the fact, and pays for it in Pass 2 probes.
    /// </para>
    /// </summary>
    internal const double AdaptiveSilenceFloorSeconds = 0.8;

    /// <summary>
    /// How many chapters a single announcement may leave missing before its number is treated as
    /// suspect and re-read (<see cref="SuspectNumberMender"/>). Above this, a misheard number is by
    /// far the likelier explanation than that many consecutive announcements going unheard: the
    /// numbers that sound alike are exactly the ones that are far apart, since the confusion is in
    /// the word, not the value. Observed on BARDIOC.m4b (2026-07-30) where "neunzehn" (19) came back
    /// as 90, and every earlier gap on that book was one or two chapters wide.
    /// <para>
    /// Set at three because that is where the two costs cross. Gaps of one to three are the ordinary
    /// kind - a chapter with no jingle, an unreadable number, an announcement the narrator rushed -
    /// and the re-probe plus Pass 2.5/3 close them at a cost proportional to the gap. A gap of dozens
    /// costs a full transcription of hours of audio, and the mark it eventually produces still carries
    /// the wrong number, so paying a handful of transcriptions to question it is a bargain that only
    /// gets better the wider the gap.
    /// </para>
    /// </summary>
    internal const int SuspectGapMinMissing = 3;

    /// <summary>
    /// The re-framings <see cref="SuspectNumberMender"/> tries on a suspect announcement with the
    /// pass-2 model, as (seconds of lead before the announcement, total window length). Whisper's
    /// output for a given stretch of audio depends on the window it arrives in, so a second look at
    /// the same announcement through differently sized windows is a genuinely different reading and
    /// not a re-roll of the same dice - the same property <see cref="RegionProber"/>'s gap re-probe
    /// relies on.
    /// <para>
    /// The two widths straddle the case that first demonstrated it: chapter 13 of "I Shall Wear
    /// Midnight" came back as "CHAPTER XIII" from a 16.1 s window and as "Chapter 13" from a 48.8 s
    /// one over the same announcement (2026-07-30). The leads differ as well as the widths, so the
    /// announcement's offset inside Whisper's fixed 30 s mel frame moves too rather than only the
    /// amount of context around it.
    /// </para>
    /// </summary>
    internal static readonly (double LeadSeconds, double LengthSeconds)[] SuspectGapReframes =
        [(2.0, 15.0), (12.0, 45.0)];

    /// <summary>
    /// How many of a mark refinement's own probes must have read a chapter number before their
    /// verdict may overrule the detecting window's (<see cref="RefinedNumberVote"/>). The winner
    /// additionally needs a strict majority, so this is really "at least two against one".
    /// <para>
    /// The whole check is free - those probes are transcribed either way, and only their yes/no
    /// answer used to be kept - and the ten-book test run of 2026-08-01 says it is also close to
    /// unerring. Over 271 marks the refinement's plurality reading agreed with the accepted number
    /// 267 times, disagreed once (the mishearing this was built for: "Die Cyber-Brutzellen" chapter
    /// 14 announced at 7:01:30, read as 40 by the one 44 s window that found it and as 14 by all ten
    /// refinement probes) and offered nothing at all 3 times. Four refinements split their vote and
    /// the majority was right in all four ({18:3, 8:2}, {15:8, 510:4}, {15:11, 4:1, 5:1, 50:1},
    /// {19:4, 9:1}), which is what the majority requirement is calibrated against.
    /// </para>
    /// <para>
    /// Three rather than two because a two-probe refinement is a thin sample and abstaining costs
    /// nothing: the vote only ever acts when it <em>disagrees</em>, and a number left uncorrected
    /// here still faces <see cref="SuspectNumberMender"/>, the ascending-sequence filter in
    /// <see cref="GapPlanning.Normalize"/> and the repair in
    /// <see cref="ChapterDetector.RepairSequenceOutliersAsync"/>. Measured on the same run, a minimum of
    /// three abstains on 4 of 271 marks and 3 more have no numbered reading at all; the median
    /// refinement offers five.
    /// </para>
    /// </summary>
    internal const int RefinedNumberVoteMinimum = 3;

    /// <summary>
    /// How many outliers <see cref="ChapterDetector.RepairSequenceOutliersAsync"/> may spend audio
    /// re-reads on in one file. Only the ambiguous ones cost anything: an outlier whose bracketing
    /// chapters leave a single number unaccounted for is settled from the sequence alone, for free
    /// and without a cap.
    /// <para>
    /// Generous rather than tight, because reaching this cap means the book has more mis-numbered
    /// marks than it has chapters worth trusting, and eight re-reads (at most two decodes each) is a
    /// rounding error next to the Pass 3 such a book is heading for anyway. Over the ten-book run of
    /// 2026-08-01 exactly one file produced a single outlier.
    /// </para>
    /// </summary>
    internal const int MaxSequenceRepairsPerFile = 8;

    /// <summary>
    /// How many unreadable-number re-reads (<see cref="SuspectNumberMender.ReadUnnumberedAsync"/>)
    /// one <see cref="RegionProber"/> region may run. The same guard
    /// <see cref="MaxUnnumberedRetriesPerChunk"/> puts on Pass 3, for the same reason: an in-text
    /// mention reaches the re-read too, and each one costs up to three decodes.
    /// <para>
    /// Set well above what real books need rather than tightly, because the event is rare and the
    /// cases it does fire on are the ones worth paying for. Counted over the seven-book test run of
    /// 2026-07-31 (4.5-15.6 h each, "chapter phrase heard, no number readable, window produced no
    /// mark"): zero occurrences on five of them, exactly one on "I Shall Wear Midnight" ("CHAPTER X"
    /// re-hearing an already-marked chapter 10) and one on "Paula Monti" ("chapitre ban 5", the
    /// genuine chapter 25 that the run lost). A per-region cap therefore only ever bites on a book
    /// whose prose talks about chapters constantly, which is exactly the book where it should.
    /// </para>
    /// </summary>
    internal const int MaxUnnumberedMendsPerRegion = 8;

    /// <summary>
    /// How many sub-floor silence bands Pass 2.5 sweeps through before giving a gap up to Pass 3,
    /// and how wide each band is (see <see cref="GapPlanning.SubFloorSweepBands"/>). Five bands of
    /// 0.1 s reach from just under <c>--min-silence-length</c> down to 0.5 s below it - at the
    /// default floor, [1.4, 1.5) down to [1.0, 1.1).
    /// <para>
    /// The range is measured, not guessed. On "Paula Monti" (2026-07-31, 4 h 34 min, French) every
    /// one of the five chapters Pass 2 missed was preceded by a pause just under the floor: chapter
    /// 3 by 1.46 s, 12 by 1.49 s, 14 by 1.39 s, 16 by 1.41 s and 19 by 1.41 s. That narrator's
    /// non-jingle chapter break simply sits on the 1.5 s line, and a book whose breaks are that
    /// short will have all of them in the top band or two - which is why the sweeps run longest-first
    /// and stop the moment the gap closes, instead of one wide sweep down to 1.0 s.
    /// </para>
    /// <para>
    /// Stopping at 0.5 s below the floor is where the yield dies. Band populations on that same
    /// file, top to bottom: 5, 6, 13, 36, 123 silences - roughly geometric growth downwards, so each
    /// further step buys several times as much probing as the one before it while the chance of a
    /// real chapter break hiding there keeps falling.
    /// </para>
    /// </summary>
    internal const int SubFloorSweepBandCount = 5;

    /// <summary>Width of one <see cref="SubFloorSweepBandCount"/> band, in seconds.</summary>
    internal const double SubFloorSweepBandSeconds = 0.1;

    /// <summary>
    /// How much of Pass 3's own cost the sub-floor sweep may spend on a gap before giving it up
    /// (<see cref="ChapterDetector.SweepSubFloorSilencesAsync"/>), measured in
    /// <see cref="WhisperChunkSeconds"/> decode windows on both sides.
    /// <para>
    /// Below one rather than at it because the sweep does not replace Pass 3, it precedes it: a
    /// sweep that finds nothing is spent on top of the full transcription that follows, so a budget
    /// of the whole thing would let a gap cost twice what it did before the sweeps existed. Three
    /// quarters keeps the worst case at 1.75x while still affording one probe per 80 s of gap at the
    /// 50 s ceiling window, or per 40 s at the plain 12 s one - far more than the one or two a real
    /// book's bands hold (measured on "Paula Monti", 2026-07-31: five gaps, one probe each, two for
    /// one of them).
    /// </para>
    /// </summary>
    internal const double SubFloorSweepBudgetFraction = 0.75;

    /// <summary>
    /// How far Pass 3's shifted re-scan (<see cref="ChapterDetector.RescanShiftedAsync"/>) displaces
    /// its decodes when a full transcription has left a gap open: half of
    /// <see cref="WhisperChunkSeconds"/>, which is the displacement that moves whatever sat on an
    /// internal decode window border as far from one as it can get. Any other value leaves some
    /// offset that was near a border still near one.
    /// </summary>
    internal const double Pass3ShiftSeconds = WhisperChunkSeconds / 2;

    /// <summary>Chunk length in seconds for full transcription of gap regions.</summary>
    internal const double GapChunkSeconds = 600;

    /// <summary>Overlap between gap transcription chunks so no phrase is cut in half. Only for a
    /// chunk border that could not be snapped to a word-safe seam (see
    /// <see cref="Pass3SeamSearchSeconds"/>); snapped borders abut exactly and need no
    /// redundancy.</summary>
    internal const double GapChunkOverlapSeconds = 10;

    /// <summary>
    /// How far around a Pass 3 chunk's natural border the seam search reaches in each direction:
    /// the border snaps to the nearest silence - or VAD non-speech region, where the pre-pass ran -
    /// mid-point in range, and the next chunk starts exactly there, with nothing decoded twice.
    /// Bounded so a chunk grows to at most <see cref="GapChunkSeconds"/> plus this: whisper.cpp
    /// has no hard input-length cap (it decodes in internal 30 s strides), but the decoded sample
    /// buffer scales with chunk length.
    /// </summary>
    internal const double Pass3SeamSearchSeconds = 30;

    /// <summary>
    /// At a snapped (overlap-free) Pass 3 seam, segments of the previous chunk ending within this
    /// many seconds before it are carried into the next chunk's phrase matching, so an
    /// announcement straddling the seam - the narrator pausing right where the seam silence sits,
    /// e.g. between "Chapter" and its number - is still found although neither chunk alone holds
    /// the whole phrase. Comfortably longer than any spoken announcement. Irrelevant at unsnapped
    /// borders, where <see cref="GapChunkOverlapSeconds"/> provides the redundancy.
    /// </summary>
    internal const double Pass3BridgeSeconds = 15;

    /// <summary>Whisper segment probability below which a chapter detection is flagged as
    /// low-confidence rather than silently trusted: the point below which Whisper itself is, on
    /// average, more unsure than sure about the words it heard.</summary>
    internal const double LowConfidenceThreshold = 0.5;

    /// <summary>
    /// How many consecutive candidates one confident mark may settle in
    /// <see cref="RegionProber.SkipSettledWindows"/>. The skip's premise - that an overlapping run
    /// of windows covers the one transition just found, so a run spanning two of them is unlikely -
    /// holds only while the run is short, and nothing about candidate density bounds its length: a
    /// book whose pauses cluster just above the probing threshold produces windows that each overlap
    /// the next for hours on end, and then the premise is not merely unlikely but false.
    /// <para>
    /// The case that forced this: BARDIOC.m4b, 2026-08-08, build 257 (the build that first cut the
    /// candidate list at <see cref="AdaptiveSilenceFloorSeconds"/> rather than at
    /// --min-silence-length). A single confident chapter-1 mark at 0:07:58 settled <em>6260</em>
    /// windows in one step and probing resumed at 11:13:27 - eleven hours of a fifteen-hour book
    /// skipped outright, with all 6260 also queued for the unconditional gap re-probe that any later
    /// sequence gap would trigger.
    /// </para>
    /// <para>
    /// Ten is where the corpus says the cap costs nothing. Counted over the fourteen-book run's
    /// debug logs in L:\Temp (builds 244-251, i.e. before the candidate list widened), 292 chains:
    /// lengths 1:126, 2:88, 3:38, 4:13, 5:5, 6:6, 7:4, 8:4, 9:1, 10:2, 11:1, 12:1, 15:1, 17:1,
    /// 23:1. A cap of 10 leaves 287 of them exactly as they were and clips five, for 28 extra probes
    /// across all fourteen books put together. The cap is also safe by construction in a way the
    /// skip is not: a clipped window is <em>probed</em>, never dropped, so this can only ever find
    /// more chapters than going uncapped would.
    /// </para>
    /// </summary>
    internal const int MaxSettledWindowSkip = 10;

    /// <summary>
    /// The most <c>--custom</c> marks one file may produce before the rest are dropped. A guard
    /// against a mapping that matches ordinary prose rather than a structural announcement - the
    /// obvious accident being something like <c>--custom "the:the"</c>, which would otherwise place
    /// a mark every few seconds for the length of a book and cost a mark-refinement transcription
    /// for each of them. Set well above any real book's structure (a novel with an interlude
    /// between every one of forty chapters stays far below it) and well below the point where the
    /// marks become a burden to the player and the refinement cost becomes noticeable.
    /// <para>
    /// Deliberately not applied to the prologue and epilogue: those replace rather than accumulate,
    /// so they are capped at one each by construction.
    /// </para>
    /// </summary>
    internal const int MaxCustomMarksPerFile = 100;

    /// <summary>
    /// How close two matches of the same repeatable phrase must be to count as the same
    /// announcement heard twice rather than two announcements. Overlapping probe windows routinely
    /// re-decode the same stretch of audio, and the second decode can time the phrase's segment a
    /// second or two differently than the first, so exact equality would not catch it. Chosen far
    /// above that jitter and far below any plausible spacing of two genuine structural
    /// announcements, which are minutes apart in a real book.
    /// <para>
    /// Applied twice per match, to the phrase time before placement and to the placed time after -
    /// the jitter can exceed this window on the phrase time alone (a re-decode may put the same
    /// words in a segment starting well over ten seconds earlier), while the anchors both walk back
    /// to agree exactly. Only the second check caught the four duplicate pairs seen on
    /// "Die Dritte Macht.m4b", 2026-07-28.
    /// </para>
    /// </summary>
    internal const double NamedMarkDedupeSeconds = 10;

    /// <summary>
    /// How close two <em>numbered</em> chapter marks have to be before they are read as one
    /// announcement heard twice under two different numbers rather than as two chapters
    /// (<see cref="ChapterDetector.SettleCollidingMarksAsync"/>).
    /// <para>
    /// Half of <see cref="NamedMarkDedupeSeconds"/>, and tighter for a reason that constant's own
    /// remarks give: the wide window there absorbs the jitter in a <em>phrase</em> timestamp, which
    /// two decodes of the same audio can put ten seconds apart, while both checks that matter after
    /// placement compare marks - and two marks derived from one onset agree to a fraction of a
    /// second unless a refinement failed for one of them. Erring small is also the safer direction:
    /// a collision missed leaves one bad number, a collision invented destroys a real chapter. Five
    /// seconds sits well above the placement spread and well below the shortest chapter anyone
    /// writes.
    /// </para>
    /// <para>
    /// The difference from the named case is what happens next, and it is the whole reason this is
    /// a separate rule. Two named matches of the same phrase are interchangeable, so the duplicate
    /// is simply dropped. Two numbered ones disagree about something - "Paula Monti", 2026-07-31,
    /// <c>-m turbo</c>: chapter 13's announcement was read as "chapitre 12" by Pass 2 and correctly
    /// as 13 by Pass 3, leaving two marks a hundredth of a second apart titled Chapitre 12 and
    /// Chapitre 13 - and dropping either one at random would be a coin toss over which number the
    /// book carries from there on.
    /// </para>
    /// <para>
    /// <see cref="ChapterDetector.DropNamedMarkEchoes"/> borrows it for a third question - is this
    /// chapter mark another line of a named announcement's heading? - on the strength of the same
    /// "well below the shortest chapter anyone writes" half. The spread it has to cover there is
    /// between two lines of one heading rather than between two readings of one onset, measured at
    /// 2.86 s on "Corsa nello spazio"'s "Epilogo / 2179 / Spazio profondo" (2026-08-06).
    /// </para>
    /// </summary>
    internal const double CollidingChapterMarkSeconds = NamedMarkDedupeSeconds / 2;

    /// <summary>
    /// Whisper language-detection probability at or above which a single probe settles the file's
    /// language outright, with <c>--lang auto</c>. Below it the sample is re-taken elsewhere in the
    /// book (see <see cref="AutoLanguageProbeAttempts"/>) and, if nothing ever clears the bar, the
    /// attempts are voted on.
    /// <para>
    /// Raised from 0.5 to 0.6 on 2026-08-03. The old value was <see cref="LowConfidenceThreshold"/>
    /// borrowed for a question it does not really describe, and it was never the binding problem:
    /// "Das Mutantenkorps" resolved to English off a single 0.36 answer, which fails either bar.
    /// What changed is that falling below the bar is now cheap - it costs another probe rather than
    /// the whole book - so the bar may as well sit where a weak answer stops being worth believing.
    /// </para>
    /// </summary>
    internal const double AutoLanguageProbabilityThreshold = 0.6;

    /// <summary>
    /// How many language-detection samples one file may take before the vote decides it. Each
    /// costs a short decode and one detector call - trivial next to a single probe window's
    /// transcription, and the reason re-probing is affordable at all - but five spread across a
    /// book is already enough to outvote any one unrepresentative stretch, and a sixth would be
    /// answering a question the first five did not settle.
    /// </summary>
    internal const int AutoLanguageProbeAttempts = 5;

    /// <summary>Length of one language-detection sample. Whisper's detector reads a single 30 s
    /// mel window and ignores anything past it, so a longer decode would be discarded audio and a
    /// shorter one would leave the window padded with silence.</summary>
    internal const double AutoLanguageProbeSeconds = 30;

    /// <summary>
    /// How much of an <see cref="AutoLanguageProbeSeconds"/> window must be speech for it to be
    /// worth sampling - half of it, which no jingle, credit roll or scene of sound effects reaches
    /// and ordinary narration clears easily.
    /// <para>
    /// Measured over the window rather than demanded of one contiguous VAD run, which is the
    /// version this replaced and was wrong in practice: raw VAD segments are sentence-sized (13779
    /// of them in "Das Mutantenkorps", averaging 4 s), so a 20 s unbroken run is rare enough that
    /// the search for one walked 80 minutes away from the anchor it was supposed to sample near.
    /// A window that is half speech is what "this is narration" actually means; whether the reader
    /// paused for breath inside it does not matter, since the detector reads the whole 30 s.
    /// </para>
    /// </summary>
    internal const double AutoLanguageMinSpeechInWindowSeconds = AutoLanguageProbeSeconds / 2;

    /// <summary>How far past a pre-existing chapter marking a language-detection sample starts, on
    /// the --verify and resume paths. Clears the announcement and the jingle around it, landing the
    /// window in the chapter's own narration.</summary>
    internal const double AutoLanguageMarkingOffsetSeconds = 20;

    /// <summary>
    /// Minimum length of the leading region (file start to the first detected chapter) for pass 3
    /// to transcribe it in search of earlier chapters when the first detection is not chapter 1.
    /// A first chapter within this many seconds of the start is taken as-is - the book simply
    /// begins mid-series, with no room for a missed earlier chapter, and the intro chapter covers
    /// the short lead-in anyway.
    /// </summary>
    internal const double MinLeadingGapSeconds = 10;

    /// <summary>How far before a pre-existing chapter marking's own timestamp --verify starts
    /// probing - the marking may sit slightly after the phrase actually started.</summary>
    internal const double VerifyMarginBeforeSeconds = 10;

    /// <summary>Total length of the --verify probe window, starting
    /// <see cref="VerifyMarginBeforeSeconds"/> before the marking.</summary>
    internal const double VerifyWindowSeconds = 60;

    /// <summary>
    /// How far off a confirmed marking has to be before <c>--verify --fix</c> bothers moving it.
    /// Rewriting an audiobook remuxes the whole file, so a correction has to be worth that: a tenth
    /// of a second is inside the accuracy the refinement itself claims, and moving a mark by it
    /// would be shuffling noise. Set at a quarter of a second, comfortably under the
    /// <see cref="DefaultMarkLeadSeconds"/> lead a listener would actually notice losing.
    /// </summary>
    internal const double VerifyFixMinShiftSeconds = 0.25;

    /// <summary>
    /// The largest correction <c>--verify --fix</c> will apply. Beyond this the marking is left
    /// alone and reported: a mark tens of seconds from its announcement is not a mark that drifted,
    /// it is a mark that means something else - a retailer's grouping, a different edition's
    /// numbering - and quietly dragging it onto the nearest matching phrase would destroy
    /// information rather than correct it. Half the <see cref="VerifyWindowSeconds"/> window, which
    /// is also the furthest a confirmation can be found from the marking in either direction.
    /// </summary>
    internal const double VerifyFixMaxShiftSeconds = 30;

    /// <summary>
    /// Minimum length for a gap between transcribed segments (or before the first/after the last)
    /// to be worth a focused re-transcription - and, for Pass 3's version of the same fallback
    /// (see <see cref="ChapterDetector.ScanGapRetriesAsync"/>), for a silence or VAD non-speech
    /// region overlapping that gap to count as "plausibly the real jingle/scene transition" rather
    /// than an in-narration pause. Whisper's single-shot decoding of a long window can silently
    /// skip a stretch of audio - typically silence or a jingle straddling the phrase - instead of
    /// transcribing it as empty; since detection already found the phrase somewhere in this audio,
    /// a gap this size is more likely that artifact than genuine emptiness.
    /// </summary>
    internal const double GapRetryThresholdSeconds = 3.0;

    /// <summary>Context padding added to each side of a gap before re-transcribing it, so the
    /// phrase is not cut off if it starts or ends right at the boundary.</summary>
    internal const double GapRetryPaddingSeconds = 2.0;

    /// <summary>
    /// How far before the phrase a Pass 3 "heard it, could not number it" retry starts its decode.
    /// The retry exists because the notation Whisper writes a number in follows the window framing
    /// rather than the audio: chapter 13 of "I Shall Wear Midnight" came out "CHAPTER XIII" from
    /// windows starting 0 s and 1.7 s before the phrase, and "Chapter 13" from one starting 4.8 s
    /// before it (measured 2026-07-30). This sits comfortably past that observed flip - the run-up
    /// is what has to be in the window, and a couple of seconds either way costs nothing.
    /// </summary>
    internal const double UnnumberedRetryLeadSeconds = 8.0;

    /// <summary>Length of a Pass 3 "heard it, could not number it" retry decode, chosen to match
    /// the framing that produced a readable number in the case above (48.8 s) rather than the
    /// short sub-chunks <see cref="GapRetryChunkSeconds"/> uses - those recover audio Whisper
    /// skipped, which is the opposite problem and wants the opposite window.</summary>
    internal const double UnnumberedRetryWindowSeconds = 45.0;

    /// <summary>
    /// How many unreadable-number retries one Pass 3 chunk may run. In-text mentions ("the next
    /// chapter was harder") reach the retry too, and each one costs a
    /// <see cref="UnnumberedRetryWindowSeconds"/> decode, so a chunk of prose that happens to talk
    /// about chapters cannot turn into an unbounded re-transcription of itself.
    /// </summary>
    internal const int MaxUnnumberedRetriesPerChunk = 3;

    /// <summary>Length of each sub-chunk a padded gap is scanned in, rather than re-transcribing
    /// it in one call. A single call spanning a long, mostly non-speech stretch risks the very
    /// failure it recovers from: Whisper can judge the whole call's audio non-speech on average
    /// and return a token leading segment, where a short call over just the phrase succeeds
    /// easily.</summary>
    internal const double GapRetryChunkSeconds = 8.0;

    /// <summary>Overlap between consecutive gap-retry sub-chunks, so a phrase straddling a chunk
    /// boundary is still fully contained in at least one of them.</summary>
    internal const double GapRetryChunkOverlapSeconds = 2.0;
}
