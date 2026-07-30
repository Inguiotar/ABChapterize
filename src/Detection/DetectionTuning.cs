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
    /// <summary>Level below which ffmpeg's silencedetect counts audio as silence, in dBFS.</summary>
    internal const int SilenceNoiseDb = -35;

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
    /// advances by. The first makes it the tool's onset-accuracy guarantee: a reported onset always
    /// sits at or before the true one and never more than this far before it. Matches
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
    /// Whisper's fixed decode chunk. The encoder always runs on a 30-second mel spectrogram, zero-padding
    /// anything shorter, so a decode's cost is roughly <c>ceil(length / 30)</c> encoder passes plus the
    /// tokens it emits - <em>not</em> proportional to its length. A 12-second decode costs about what a
    /// 30-second one does.
    /// <para>
    /// This is why <see cref="RegionProber.FindReusablePrefixEnd"/> gates transcript reuse on removing a
    /// whole pass rather than on saving seconds: shaving a 50-second window down to 47 saves an encoder
    /// pass exactly never, while adding a Whisper call and a second ffmpeg decode. It is also why the
    /// "Whisper audio processed" figure in the summary overstates what short decodes cost - useful as a
    /// coverage measure, misleading as a cost one.
    /// </para>
    /// </summary>
    internal const double WhisperChunkSeconds = 30.0;

    /// <summary>
    /// How far before a probe window's end an earlier transcript stops being trusted for reuse, when that
    /// end was <em>not</em> snapped to a silence or VAD region (see
    /// <see cref="RegionProber.FindReusablePrefixEnd"/>). Whisper can drop speech that runs past the edge
    /// of its input entirely rather than emitting a partial word - confirmed on BARDIOC.m4b (2026-07-30),
    /// where four windows ended mid-announcement and the run's log carries not one "phrase heard but no
    /// readable number" note for them: the truncated announcements produced no text at all, not even a
    /// recognizable chapter phrase. Reusing right up to such an edge would therefore inherit a hole
    /// rather than a partial phrase, and the re-probe that exists to find that very chapter would decode
    /// from the middle of the announcement it is hunting.
    /// <para>
    /// <see cref="PhraseMarginSeconds"/> is the right size for it, not a coincidence: it is how much room
    /// an announcement needs after its onset to be read in full. A reused prefix ending that far before
    /// the edge was decoded with at least that much following context, so an announcement inside it would
    /// have been transcribed <em>and</em> read - meaning a candidate that came back empty cannot be
    /// hiding one there.
    /// </para>
    /// </summary>
    internal const double PrefixReuseBackoffSeconds = PhraseMarginSeconds;

    /// <summary>
    /// With --min-silence-length auto, the Pass 2 probing threshold is this factor times a mark's
    /// anchor silence length, i.e. a 25 % margin below the shortest observed inter-chapter break,
    /// mirroring <see cref="JingleObservationSafetyFactor"/>. Monotonic: the first qualifying mark
    /// (the second one found) raises the threshold off the floor, every later mark can only lower
    /// it again - a threshold above an observed inter-chapter silence would by definition skip the
    /// very kind of silence proven to precede this book's chapters.
    /// </summary>
    internal const double AdaptiveTightenFactor = 0.75;

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
    /// Whisper language-detection probability below which the result counts as inconclusive and
    /// the file falls back to English, with <c>--lang auto</c>. Reuses
    /// <see cref="LowConfidenceThreshold"/>'s cutoff for the same reason.
    /// </summary>
    internal const double AutoLanguageProbabilityThreshold = 0.5;

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
