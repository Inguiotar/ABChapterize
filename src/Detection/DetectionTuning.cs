// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace ABChapterize.Detection;

/// <summary>
/// Every tuning constant <see cref="ChapterDetector"/> and its detection-geometry helpers use
/// to decide silence/jingle/phrase thresholds, search radii and safety margins. Kept as a single
/// catalog rather than split across the classes that consume each value, since many of these
/// constants are calibrated relative to one another (their doc comments cross-reference the
/// related constants they were chosen against) and are easiest to reason about, and re-tune,
/// side by side.
/// </summary>
internal static class DetectionTuning
{
    internal const int SilenceNoiseDb = -35;

    /// <summary>Probe window length in seconds when --max-jingle-length is 0 (no jingle
    /// expected). Above 0, the window is --max-jingle-length seconds (plus
    /// <see cref="PhraseMarginSeconds"/>) instead, regardless of whether the VAD pre-pass
    /// itself ends up running - see <see cref="CliOptions.RunVadPrePass"/>.</summary>
    internal const double ProbeSecondsPlain = 12;

    /// <summary>
    /// The shortest silence Pass 1 retains in memory (see the <c>allSilences</c>/<c>silences</c>
    /// split in <see cref="DetectAsync"/>) for use as a window-seam snap target (see
    /// <see cref="FindNearestSeam"/> and its callers: Pass 2's window plan, the reuse-time
    /// split, and Pass 3's chunk borders) and for pinpointing a mark at the silence directly
    /// preceding its phrase, regardless of how high --min-silence-length is set.
    /// Only silences at or above --min-silence-length are ever reported as Pass 2 candidates or
    /// logged; this lower floor exists purely so a silence-mid-point seam (or a mark anchor) is
    /// available even when the nearest real silence is shorter than the book's candidate
    /// threshold. Kept low enough to catch ordinary clause pauses without noticeably
    /// growing Pass 1's silence list.
    /// </summary>
    internal const double MinStoredSilenceSeconds = 0.5;

    /// <summary>
    /// How far past a Pass 2 window's natural end <see cref="PlanWindowEnd"/> searches for a
    /// seam when that end does not lie inside the next window (no shared border to snap): the
    /// nearest silence - or, when the VAD pre-pass ran, VAD non-speech region - mid-point within this many
    /// seconds after the natural end becomes the window's end, so even a stand-alone window
    /// stops at a word-safe cut instead of possibly mid-word (a mid-word tail is exactly what
    /// makes Whisper garble a window's final phrase). Extension only: a target before the
    /// natural end would shrink the window and could cut off the very phrase the probe exists
    /// to find. When no target lies within reach, the window keeps its natural length.
    /// </summary>
    internal const double WindowEndSnapSearchSeconds = 5.0;

    /// <summary>Without a VAD pre-pass, the phrase must start within this many seconds after
    /// the silence that triggered its probe (or a closer anchor silence still within the
    /// window) to be accepted as a real chapter announcement rather than an unrelated in-text
    /// mention.</summary>
    internal const double PhraseLatestStart = 5.0;

    /// <summary>Flat margin added to --max-jingle-length so the phrase after the jingle
    /// still fits into the probe window.</summary>
    internal const double PhraseMarginSeconds = 5.0;

    /// <summary>
    /// The fallback lead <see cref="ComputeMarkBeforeJingle"/>'s step 5 backs off by when its
    /// backward walk runs out of VAD data before ever finding the previous chapter's real
    /// trailing narration (typically a jingle sitting at the very start of the file, before
    /// chapter 1). The same flat 0.5 s lead used elsewhere as a last resort when nothing more
    /// precise is known.
    /// </summary>
    internal const double JingleLeadSeconds = 0.5;

    /// <summary>
    /// Without --mark-before-jingle, the chapter mark is placed this many seconds before the
    /// detected phrase itself, no matter what precedes it (no silence/jingle anchor is
    /// consulted for the timestamp at all - only for the --min-silence-length/
    /// --max-jingle-length auto statistics, which keep working exactly as before).
    /// </summary>
    internal const double DefaultMarkLeadSeconds = 0.25;

    /// <summary>
    /// Slack allowed when matching a VAD non-speech region (the jingle) to a Whisper phrase: the
    /// region's end is where VAD resumes detecting speech, which should coincide with the phrase
    /// start, but the two detectors time boundaries slightly differently - Whisper's segment
    /// timestamps can be a touch earlier (coarser) than VAD's frame-precise resume, leaving the
    /// region ending just <em>after</em> the phrase start. Without this slack such a region would
    /// be missed and a silence-less jingle would fall back to the (possibly false) nearest
    /// silence. Kept small - far below any real jingle length or inter-chapter spacing - so it
    /// only absorbs boundary jitter and can never grab an unrelated, later non-speech region.
    /// </summary>
    internal const double JinglePhraseMatchToleranceSeconds = 0.5;

    /// <summary>
    /// How far a candidate <see cref="LeadingSilence"/> may start after its VAD non-speech
    /// region's own start and still count as leading it, rather than being an unrelated silence
    /// deep inside a long region (see that method's remarks). A true lead-in silence and its
    /// region begin at essentially the same instant regardless of how long the hush runs, so
    /// this only needs to absorb detector-to-detector jitter (VAD's frame granularity vs.
    /// silencedetect's own onset timing) - observed well under 1 s on real audio, against
    /// false-candidate gaps of several seconds or more.
    /// </summary>
    internal const double LeadingSilenceStartToleranceSeconds = 1.5;

    /// <summary>
    /// How close a silencedetect silence boundary and a VAD speech segment's end must be to
    /// count as describing the same transition, when <see cref="ComputeMarkBeforeJingle"/>'s
    /// step 2 asks "does real narration end essentially where this silence begins" - the plain
    /// in-narration-pause case, with no jingle to walk back through. Reuses <see
    /// cref="LeadingSilenceStartToleranceSeconds"/>'s own value under its own name: both absorb
    /// the same silencedetect-vs-VAD detector jitter, just for a different boundary pairing
    /// (silence vs. VAD region start there, silence vs. VAD speech end here).
    /// </summary>
    internal const double JingleWalkAdjacencyToleranceSeconds = LeadingSilenceStartToleranceSeconds;

    /// <summary>
    /// Longest stretch of VAD-speech "glue" the anchor-time jingle edge adjustment (see
    /// <see cref="AdjustJingleRegion"/>) will step across at the jingle's leading edge - both
    /// when trimming trailing-narration blips off the front of a merged region and when bridging
    /// backward across an untranscribed music vocal to an earlier region the same jingle was
    /// split into. Real trailing-narration fragments and mid-jingle vocals alike run well under
    /// this (observed up to ~1.1 s on real audio); anything longer separating two non-speech
    /// stretches is treated as genuine narration territory the jingle cannot extend across.
    /// </summary>
    internal const double JingleGlueMaxSeconds = 3.0;

    /// <summary>
    /// Minimum overlap between a VAD non-speech region and the matched phrase's own transcript
    /// segment span for the smeared-phrase rescue (see <see cref="FindSmearedJingleRegion"/>) to
    /// accept that region as the jingle. Deliberately jingle-scale (matching
    /// <see cref="MinJingleObservationSeconds"/>): a correctly timed announcement's short segment
    /// barely grazes a following pause region (well under this), while a segment Whisper smeared
    /// across the jingle - the failure this rescues - overlaps it by many seconds.
    /// </summary>
    internal const double SmearedPhraseMinOverlapSeconds = 2.0;

    /// <summary>
    /// Slack allowed when deciding a Whisper segment <em>starts with</em> a stored silence or VAD
    /// non-speech region (see <see cref="TrimLeadingNonSpeech"/>). Whisper timestamps a segment
    /// from where its decoded audio block begins, which can be a touch before silencedetect's or
    /// VAD's frame-precise onset; without this slack a silence starting a hair after the segment's
    /// timestamp would not be recognised as leading it. Kept small so it only absorbs that
    /// boundary jitter and never trims a segment that genuinely opens with speech.
    /// </summary>
    internal const double SegmentLeadTrimToleranceSeconds = 0.5;

    /// <summary>
    /// The shortest span this codebase ever treats as "plausibly a real jingle". Used two ways:
    /// (1) a VAD non-speech region whose longest single contiguous run is shorter than this (see
    /// <see cref="ComputeNonSpeechRegions"/> for why the longest run, not the merged span) is
    /// dropped outright rather than ever becoming a candidate - too short to be a jingle at any
    /// book's pacing, more likely an in-narration breath pause VAD happened to classify as
    /// non-speech; (2) with
    /// --max-jingle-length auto, an observed phrase offset below this is treated as "this chapter
    /// had no jingle (or an ultra-short one)" and excluded from tightening the probe window: some
    /// audiobooks only play the jingle for some chapters, and such a chapter gives no information
    /// about how long the window needs to be for chapters that do have one - using it anyway
    /// could shrink the window before a later, genuinely full-length jingle is ever probed.
    /// </summary>
    internal const double MinJingleObservationSeconds = 2.0;

    /// <summary>
    /// When the VAD pre-pass ran, a VAD "speech" segment shorter than this, sandwiched between two non-speech
    /// regions, does not end the surrounding jingle - the two regions are merged and the blip is
    /// treated as VAD noise rather than a genuine return to narration. Silero VAD is not reliable
    /// on jingle music: a vocal-like transient or a strong rhythmic passage can cross its speech
    /// threshold for a fraction of a second in the middle of an otherwise instrumental jingle,
    /// which would otherwise fragment one continuous jingle into several too-short regions (see
    /// <see cref="ComputeNonSpeechRegions"/>). Deliberately well below any real inter-chapter
    /// narration gap, so a genuine speech resume is never merged away.
    /// </summary>
    internal const double MergeShortSpeechGapSeconds = 1.0;

    /// <summary>
    /// The speech-duration floor <see cref="AdvancePastNonSpeech"/> uses to tell a genuine
    /// spoken onset from a jingle's own musical/vocal transients (or an occasional Whisper
    /// hallucination inside one) that VAD still classifies as "speech". Calibrated empirically
    /// against a real audiobook (see <c>tools\vadprobe</c>'s sweep data): the shortest such
    /// transient measured 0.352s and stubbornly survived raising <see cref="VadSegmenter.Threshold"/>
    /// as far as 0.70, while the shortest genuine chapter-announcement word measured 0.608s -
    /// this sits roughly midway between the two, erring toward not skipping real speech since a
    /// real onset in some other supported language could plausibly be shorter than the one
    /// German data point available. Deliberately tighter than <see
    /// cref="MergeShortSpeechGapSeconds"/>'s cluster-grouping gap, since the two solve different
    /// problems: this rejects a single too-short blip outright, that decides whether separate
    /// blips belong to the same jingle-internal cluster.
    /// </summary>
    internal const double TransientSpeechFloorSeconds = 0.4;

    /// <summary>
    /// Minimum letters-plus-digits-per-second a transcript segment must average for <see
    /// cref="JingleGeometry"/>'s corroboration checks (<c>IsGenuineSpeech</c>) to trust it as
    /// evidence that a VAD blip is real spoken narration rather than jingle music. "The
    /// transcript covers this blip with real words" has a hidden assumption baked in: ordinary
    /// narration runs several times this fast, so a segment claiming coverage at a small
    /// fraction of that pace is not continuous speech at all - it is a long stretch of
    /// near-silent jingle music/reverb that Whisper folded into one oversized segment together
    /// with only a few real words at its edges (a single merged segment has been observed
    /// spanning almost 30s of audio while containing at most a couple of seconds of actual
    /// speech). Confirmed on real audio (Perry Rhodan "Die Dritte Macht", chapters 8 and 10,
    /// 2026-07-26): two genuine, un-smeared narration segments in the same book measured roughly
    /// 10-12 letters/second, while two segments smeared this way - one merging a full preceding
    /// sentence together with the following "Kapitel 8", the other stretching "Kapitel 10"
    /// itself across musically-paced syllable gaps - measured roughly 0.4-1.0 letters/second, an
    /// order of magnitude slower. Set well below the real-narration floor to leave comfortable
    /// margin for legitimately slow delivery or short, punctuation-heavy segments, while still
    /// rejecting anything smeared by an order of magnitude.
    /// </summary>
    internal const double MinPlausibleSpeechCharsPerSecond = 3.0;

    /// <summary>
    /// Ceiling on a VAD speech blip's own duration for <see cref="MinPlausibleSpeechCharsPerSecond"/>'s
    /// pace check to apply to it at all: only a blip this short or shorter could plausibly <em>be</em>
    /// the brief musical/vocal transient the pace check exists to unmask - the same premise <see
    /// cref="TransientSpeechFloorSeconds"/> is built on, just applied from the other side. A VAD
    /// blip already many seconds (let alone minutes) long is unambiguously substantial spoken
    /// content by duration alone, regardless of how an overlapping transcript segment happens to be
    /// timestamped; scrutinising its corroborating segment's pace would only risk rejecting it over
    /// an artifact of <em>that segment's</em> smearing rather than anything wrong with the blip
    /// itself (confirmed by a regression this ceiling fixes: a 640 s VAD blip covering an entire
    /// preceding chapter's narration was wrongly rejected because the only transcript segment
    /// reaching that far was itself a smeared chapter-announcement span with a low apparent pace -
    /// the blip was never in doubt, only the segment's own timestamp was). Set comfortably above the
    /// longest wrongly-corroborated blip actually observed (under 2 s in both the chapter 8 and
    /// chapter 10 real-audio cases <see cref="MinPlausibleSpeechCharsPerSecond"/> was calibrated
    /// against) and comfortably below ordinary narration blip lengths, which the same real audio
    /// shows commonly running several seconds once mid-sentence micro-pauses are cleared.
    /// </summary>
    internal const double MaxPaceScrutinizedBlipSeconds = 2.0;

    /// <summary>
    /// Length of the decode precise marking transcribes to check whether a mark's chapter phrase
    /// is really the first thing heard there (see <see cref="RefinePreciseMarkAsync"/>). A real
    /// chapter announcement is never anywhere close to this long, and a jingle - the only other
    /// thing a mark can land on - is rarely shorter than it, so a single window is normally
    /// enough to tell the two apart without needing several probes of increasing length.
    /// </summary>
    internal const double PreciseMarkCheckWindowSeconds = 4.0;

    /// <summary>
    /// Real audio lead-in precise marking decodes before every position it checks (widening the
    /// window backward rather than shifting it, so <see cref="PreciseMarkCheckWindowSeconds"/> of
    /// fresh audio is never lost off the tail). Needed because a VAD-detected onset can lag the
    /// true acoustic word-start by a moment (a soft consonant takes VAD's amplitude threshold a
    /// moment to cross); without this margin, decoding from exactly such an onset can clip the
    /// phrase's leading sound enough that Whisper drops the word from the transcript entirely
    /// rather than merely mishearing it - confirmed on real audio (see <c>tools\vadprobe</c>'s
    /// <c>precise</c> prototype). A synthetic silence lead-in was tried first instead and
    /// rejected: it caused Whisper to misrecognize the very next word's leading consonant right
    /// at the padding/audio boundary (e.g. "Kapitel" heard as "Spitel"), an artifact plain real
    /// audio never showed. Kept small (well under a syllable's worth of audio) rather than the
    /// few tenths of a second the onset lag can reach: too generous a margin risks pulling a
    /// trailing syllable or two of whatever precedes the phrase into the decode window instead.
    /// </summary>
    internal const double PreciseMarkLeadInSeconds = 0.1;

    /// <summary>
    /// Step size precise marking's round 2 (<see cref="RefinePreciseMarkAsync"/>) advances by when
    /// blindly sweeping for the chapter phrase after round 1's VAD-speech-segment candidates never
    /// confirmed it in either direction. Matches <see cref="PreciseMarkLeadInSeconds"/>'s own
    /// magnitude - both are about the finest granularity worth probing at, given
    /// <see cref="PreciseMarkCheckWindowSeconds"/>'s window length - rather than some unrelated
    /// value.
    /// </summary>
    internal const double PreciseMarkFixedStepSeconds = 0.1;

    /// <summary>
    /// How far <em>before</em> a confirmed/left-as-is precise marking mark
    /// <see cref="SnapToQuietestPointAsync"/> is allowed to search for a quieter point to move it
    /// to - the final cleanup step's own radius, independent of (and larger than) the candidate
    /// search step size above it. Backward-only (see <see cref="SnapToQuietestPointAsync"/>), so
    /// this is a one-sided lookback, not a window centered on the mark.
    /// </summary>
    internal const double PreciseMarkQuietSnapRadiusSeconds = 0.15;

    /// <summary>
    /// Width of the sliding RMS window <see cref="SnapToQuietestPointAsync"/> scans across
    /// <see cref="PreciseMarkQuietSnapRadiusSeconds"/>'s range to find the quietest point. Short
    /// enough to land inside a genuine micro-pause between words/syllables rather than averaging
    /// across most of one, long enough that a single sample near a zero-crossing inside otherwise
    /// loud audio cannot masquerade as a real quiet spot.
    /// </summary>
    internal const double PreciseMarkQuietWindowSeconds = 0.01;

    /// <summary>
    /// Minimum power-ratio improvement, in dB, a backward candidate point within
    /// <see cref="PreciseMarkQuietSnapRadiusSeconds"/> must offer over the mark's own current
    /// position before <see cref="SnapToQuietestPointAsync"/> will nudge to it. 6 dB is a 4x power
    /// ratio - comfortably audible, not a marginal difference that could just as easily be
    /// noise-floor jitter - so a nudge only ever happens for a genuine, worthwhile improvement,
    /// never as a coin-flip between two nearly-identical spots.
    /// </summary>
    internal const double PreciseMarkQuietSnapMinImprovementDb = 6.0;

    /// <summary>
    /// With --max-jingle-length auto, the resized probe window is this factor times the
    /// longest jingle observed so far (plus <see cref="PhraseMarginSeconds"/>), leaving a 25 %
    /// safety margin above the longest observed jingle for normal length variation between
    /// chapters. Applied monotonically: after the first observation (at the second mark) sets
    /// the window, later observations can only widen it, never narrow it - a window below an
    /// already observed jingle length would, by definition, have been too short for that
    /// chapter's own jingle. The exact mirror of <see cref="AdaptiveTightenFactor"/>.
    /// </summary>
    internal const double JingleObservationSafetyFactor = 1.25;

    /// <summary>
    /// With --min-silence-length auto, the Pass 2 probing threshold is this factor times a
    /// mark's anchor silence length, leaving a 25 % safety margin below the shortest observed
    /// inter-chapter break - matching <see cref="JingleObservationSafetyFactor"/>'s 25 % margin
    /// on the jingle side. Applied monotonically: the first qualifying mark (the second one
    /// found) raises the threshold from the floor; every later mark can only lower it again
    /// (when its anchor silence comes too close to the current threshold), never raise it - a
    /// threshold above an already observed inter-chapter silence would, by definition, skip
    /// the very kind of silence that has proven to precede this book's chapters.
    /// </summary>
    internal const double AdaptiveTightenFactor = 0.75;

    /// <summary>Chunk length in seconds for full transcription of gap regions.</summary>
    internal const double GapChunkSeconds = 600;

    /// <summary>Overlap between gap transcription chunks so no phrase is cut in half. Only
    /// applies to a chunk border that could not be snapped to a word-safe seam (see
    /// <see cref="Pass3SeamSearchSeconds"/>); snapped borders abut exactly and need no
    /// overlap redundancy.</summary>
    internal const double GapChunkOverlapSeconds = 10;

    /// <summary>
    /// How far around a Pass 3 chunk's natural border the seam search reaches, in both
    /// directions: the border snaps to the nearest silence - or, when the VAD pre-pass ran, VAD non-speech
    /// region - mid-point within this range, and the next chunk then starts exactly at that
    /// seam, with no overlap and nothing decoded twice. Bounded so a chunk can grow to at most
    /// <see cref="GapChunkSeconds"/> plus this: whisper.cpp has no hard input-length cap (it
    /// decodes any length in internal 30 s strides), but the decoded sample buffer scales with
    /// chunk length, so the growth is kept to a small fraction of the chunk.
    /// </summary>
    internal const double Pass3SeamSearchSeconds = 30;

    /// <summary>
    /// At a snapped (overlap-free) Pass 3 seam, segments of the previous chunk ending within
    /// this many seconds before the seam are carried into the next chunk's phrase matching, so
    /// a chapter phrase straddling the seam itself - the narrator pausing mid-announcement
    /// right where the seam silence sits, e.g. between "Chapter" and its number - is still
    /// found even though neither chunk alone contains the whole phrase. Comfortably longer
    /// than any spoken chapter announcement. Irrelevant at unsnapped borders, where the
    /// <see cref="GapChunkOverlapSeconds"/> overlap provides the redundancy instead.
    /// </summary>
    internal const double Pass3BridgeSeconds = 15;

    /// <summary>Whisper segment probability below which a chapter detection is flagged as
    /// low-confidence instead of being silently trusted. 0.5 was chosen as the point below
    /// which Whisper itself is, on average, more unsure than sure about the words it heard.</summary>
    internal const double LowConfidenceThreshold = 0.5;

    /// <summary>
    /// Whisper language-detection probability below which the result is treated as
    /// inconclusive and the run falls back to English for that file, with <c>--lang auto</c>.
    /// Reuses the same 0.5 cutoff as <see cref="LowConfidenceThreshold"/>: below it, Whisper
    /// itself is, on average, more unsure than sure about its own guess.
    /// </summary>
    internal const double AutoLanguageProbabilityThreshold = 0.5;

    /// <summary>
    /// Minimum length of the leading region (file start to the first detected chapter) for pass 3
    /// to transcribe it in search of earlier chapters when the first detection is not chapter 1.
    /// A first chapter within this many seconds of the start is taken as-is - the book simply
    /// begins mid-series, with no room for a missed earlier chapter, and the intro chapter covers
    /// the short lead-in regardless.
    /// </summary>
    internal const double MinLeadingGapSeconds = 10;

    /// <summary>How far before a pre-existing chapter marking's own timestamp --verify starts
    /// probing - the marking may sit slightly after the phrase actually started.</summary>
    internal const double VerifyMarginBeforeSeconds = 10;

    /// <summary>Total length of the --verify probe window, starting <see
    /// cref="VerifyMarginBeforeSeconds"/> before the marking.</summary>
    internal const double VerifyWindowSeconds = 60;

    /// <summary>
    /// Minimum length, both for a gap between transcribed segments (or before the first/after
    /// the last one) to be worth a focused re-transcription attempt, and - for Pass 3's version
    /// of the same fallback (see <see cref="ScanGapRetriesAsync"/>) - for a silence or, when the
    /// VAD pre-pass ran, VAD non-speech region overlapping that gap to count as "plausibly the real
    /// jingle/scene transition" rather than an ordinary in-narration pause. Whisper's single-shot
    /// decoding of a long window can silently skip a stretch of audio altogether - typically
    /// silence or a jingle straddling the actual chapter phrase - rather than transcribing it as
    /// empty speech; since detection's own original run already found the phrase somewhere in
    /// this same audio, a gap this size is more likely that decoding artifact than genuine
    /// silence with nothing in it.
    /// </summary>
    internal const double GapRetryThresholdSeconds = 3.0;

    /// <summary>Context padding added to each side of a gap before re-transcribing it, so the
    /// phrase is not cut off if it starts or ends right at the gap boundary.</summary>
    internal const double GapRetryPaddingSeconds = 2.0;

    /// <summary>Length of each sub-chunk a padded gap is scanned in, rather than
    /// re-transcribing it in one call. A single call spanning a long, mostly non-speech stretch
    /// (silence or a jingle around a short phrase) risks the same failure it was meant to
    /// recover from: Whisper can judge the whole call's audio as non-speech on average and
    /// return only a token leading segment, even where a short, tightly-scoped call over just
    /// the phrase itself succeeds easily.</summary>
    internal const double GapRetryChunkSeconds = 8.0;

    /// <summary>Overlap between consecutive gap-retry sub-chunks, so a phrase that straddles a
    /// chunk boundary is still fully contained within at least one of them.</summary>
    internal const double GapRetryChunkOverlapSeconds = 2.0;
}
