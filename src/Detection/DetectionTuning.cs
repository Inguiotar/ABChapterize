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
    /// beside the full decode Analyze is about to do anyway.</summary>
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

    /// <summary>
    /// The shortest silence Analyze keeps (see the <c>allSilences</c>/<c>silences</c> split in
    /// <see cref="ChapterDetector.DetectAsync"/>), regardless of --min-silence-length. Only
    /// silences at or above --min-silence-length ever become Probe candidates or get logged;
    /// this lower floor exists purely so a window seam (see
    /// <see cref="GapPlanning.FindNearestSeam"/>) or a mark anchor can still snap to a silence
    /// mid-point when the nearest real one is shorter than the book's candidate threshold. Low
    /// enough to catch ordinary clause pauses without noticeably growing Analyze's list.
    /// </summary>
    internal const double MinStoredSilenceSeconds = 0.5;

    /// <summary>
    /// How far past a Probe window's natural end <see cref="GapPlanning.PlanWindowEnd"/> looks
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
    internal const double PhraseLatestStartSeconds = 5.0;

    /// <summary>
    /// How far past the point where Probe's primary scan <em>expects</em> an announcement its
    /// probe window reaches - after the silence for a plain pause, after the music for a jingle
    /// (see <see cref="RegionProber.BuildCandidates"/>). This is the whole probe window for those
    /// candidates: nothing has to cross a jingle any more, so no window needs to be as long as one.
    /// <para>
    /// Sized by what a Whisper pass costs, not by what an announcement needs. Recognition runs on a
    /// fixed <see cref="WhisperChunkSeconds"/> mel whatever the window holds, so a 7 s window costs
    /// exactly what a 27 s one does and there is no reason at all to be stingy - the only thing
    /// shortening buys is decode I/O, which is negligible beside the recognition. Staying inside one
    /// chunk is the real constraint, and this plus <see cref="SilenceLeadInSeconds"/> does.
    /// </para>
    /// <para>
    /// This plus <see cref="JingleLeadInSeconds"/> does <em>not</em>: it is exactly
    /// <see cref="WhisperChunkSeconds"/>, the width that constant records as losing an announcement
    /// outright. Neither number was moved to fix that - the lead-in is generous for its own measured
    /// reasons, and trimming this one would have cost two corpus marks accepted deep in their
    /// windows.
    /// The remedy sits after the fact instead, in
    /// <see cref="RegionProber.RereadInOnePassAsync"/>, which re-reads an empty jingle window at a
    /// single-pass width. Anything that changes either constant should re-read that method first.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the corpus replay that sized this reach, and the marks trimming it would have cost.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="ExpectedAnnouncementSeconds"]/*' /></remarks>
    internal const double ExpectedAnnouncementSeconds = 22.0;

    /// <summary>
    /// How much of the silence before an expected announcement a probe window opens with, so
    /// Whisper has a run-up rather than starting hard on the first syllable.
    /// <para>
    /// Taken from <em>inside</em> the silence (clamped to its start), never from the narration
    /// before it, and that constraint is load-bearing rather than tidy: several mechanisms read a
    /// probe transcript as "the first speech heard here is the announcement" - see
    /// <see cref="JingleGeometry.TrimLeadingNonSpeech"/> and
    /// <see cref="PreciseMarkRefiner"/>'s survival probes - and a run-up of the previous chapter's
    /// closing sentence would quietly make that false.
    /// </para>
    /// </summary>
    internal const double SilenceLeadInSeconds = 3.0;

    /// <summary>
    /// How much speech may sit between a sub-threshold pause and the candidate pause behind it for
    /// <see cref="RegionProber.SandwichedSilences"/> to read the two as bracketing an announcement
    /// and promote the first to a candidate of its own.
    /// <para>
    /// The defect it exists for: an announcement can be flanked by a pause too short to be a
    /// candidate and a pause long enough to be one, in which case the only window covering that
    /// stretch opens on the <em>second</em> pause - that is, immediately after the announcement has
    /// been spoken - and no pass ever reads it.
    /// </para>
    /// <para>
    /// Promotion keeps the invariant <see cref="SilenceLeadInSeconds"/> protects, which is why it is
    /// the shape of the fix: the window still opens <em>inside a silence</em>, so "the first speech
    /// heard here is the announcement" stays true. Reaching an existing candidate's window backwards
    /// into the narration instead would have broken it.
    /// </para>
    /// <para>
    /// Set with margin past the two corpus cases rather than just covering them - a chapter announced
    /// with its title runs well past 3 s - and the extra candidates that buys cost less than their
    /// count suggests, since a promoted candidate sits a few seconds ahead of the one that promoted
    /// it and the overlap cache serves most of the second window from the first.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the two corpus cases it was built for, and what each candidate bound costs.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="SandwichedAnnouncementSeconds"]/*' /></remarks>
    internal const double SandwichedAnnouncementSeconds = 3.5;

    /// <summary>
    /// How many excerpts <see cref="ChapterDetector.DenoiserForFileAsync"/> measures a file's
    /// fidelity over. Eight because one says nothing: the measure moves 3.2x to 24.1x between excerpts of the
    /// same book (sixteen-book corpus, 2026-08-17), and re-sampling a book at different positions
    /// reshuffles the bottom of the corpus ranking outright, so only a median over several is worth
    /// reading at all.
    /// </summary>
    internal const int FidelityExcerpts = 8;

    /// <summary>How much audio each of those excerpts covers. Long enough to hold speech through
    /// several sentences and their pauses, short enough that the eight together cost a fraction of
    /// one probe's decode.</summary>
    internal const double FidelityExcerptSeconds = 30.0;

    /// <summary>
    /// The same run-up for a jingle candidate, taken from inside the music, and deliberately longer:
    /// the point it is measured back from is a VAD speech onset rather than a silencedetect edge, so
    /// it carries the detector's own latency plus whatever timeline drift survives Analyze's resync
    /// (see <see cref="Audio.FfmpegClient.PcmResyncToleranceSeconds"/> - up to 2.15 s was measured
    /// across the corpus before that fix). Generous by design: the run-up costs nothing, and the
    /// announcement landing before the window opens costs a chapter.
    /// </summary>
    internal const double JingleLeadInSeconds = 8.0;

    /// <summary>Flat margin added to a measured jingle length so the phrase after the jingle still
    /// fits into the probe window.</summary>
    internal const double PhraseMarginSeconds = 5.0;

    /// <summary>
    /// How much of its lead-in a recovery pass's window gives up against the primary scan's
    /// (<see cref="SilenceLeadInSeconds"/>, <see cref="JingleLeadInSeconds"/>), and
    /// <see cref="RecoveryReachTrimSeconds"/> how much of its reach past the expectation.
    /// <para>
    /// A recovery pass exists because the primary scan came up empty on this stretch, so the one
    /// thing it must not do is ask the same question in the same words. It reads the classification
    /// exactly as the primary scan does - where the pauses are, where the music is, where each
    /// announcement is expected - and then frames every window differently, because a differently
    /// framed decode is a different reading of the same audio and that is the whole content of a
    /// second look (the same reasoning that reverted the gap re-probe's transcript reuse). Trimming
    /// rather than widening is what keeps it affordable and keeps both recovery windows inside a
    /// single recognizer pass, where a lone word survives best.
    /// </para>
    /// </summary>
    internal const double RecoveryLeadInTrimSeconds = 2.0;

    /// <summary>How much of its reach past the expected announcement a recovery pass's window gives
    /// up against <see cref="ExpectedAnnouncementSeconds"/>; see
    /// <see cref="RecoveryLeadInTrimSeconds"/> for why either is trimmed at all.</summary>
    internal const double RecoveryReachTrimSeconds = 5.0;

    /// <summary>
    /// The fallback lead <see cref="JingleGeometry.ComputeMarkBeforeJingle"/>'s step 5 backs off
    /// by when its backward walk runs out of VAD data before finding the previous chapter's real
    /// trailing narration - typically a jingle at the very start of the file, before chapter 1.
    /// The same flat 0.5 s used elsewhere as a last resort when nothing more precise is known.
    /// Nothing to do with <see cref="JingleLeadInSeconds"/>, which is how much music a jingle
    /// candidate's probe window opens with; this one is a mark placement of last resort.
    /// </summary>
    internal const double JingleWalkFallbackLeadSeconds = 0.5;

    /// <summary>
    /// Default for <see cref="ABChapterize.Cli.CliOptions.MarkLeadSeconds"/> (--mark-lead): without
    /// --mark-before-jingle, the mark goes this many seconds before the detected phrase, whatever
    /// precedes it - no silence/jingle anchor is consulted for the timestamp at all, only for the
    /// --min-silence-length auto threshold and the per-file jingle statistics --summary reports.
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
    /// The shortest span this codebase treats as "plausibly a real jingle", used three ways.
    /// (1) A VAD non-speech region whose longest contiguous run falls below it (see
    /// <see cref="JingleGeometry.ComputeNonSpeechRegions"/> for why the longest run, not the
    /// merged span) is dropped rather than ever becoming a candidate: too short for a jingle at
    /// any book's pacing, more likely a breath pause VAD called non-speech. (2) When a file's music
    /// reach is measured, an observed phrase offset below it means "this chapter had no jingle (or
    /// an ultra-short one)" and says nothing about how far the music reaches - some books only play
    /// the jingle for some chapters. (3) It is the floor <see cref="JingleCensus"/> counts the
    /// --verbose jingle tally at, so that tally means the same thing as the two decisions above and
    /// cannot drift away from them.
    /// </summary>
    internal const double MinJingleObservationSeconds = 2.0;

    /// <summary>
    /// How much music a file must have, per hour of play time, before Probe reads its jingles first
    /// and its pauses only where the chapter sequence still wants one (see
    /// <see cref="JingleFirstScan"/>). Counted over the whole <see cref="JingleCensus"/>, so it means
    /// the same thing as the --verbose jingle tally.
    /// <para>
    /// One per hour is a low bar and is meant to be: it separates two populations rather than
    /// grading one, and the corpus leaves an empty band between them, so a bar drawn anywhere in
    /// that band would be a number with nothing behind it.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the two populations' measured jingle tallies.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="JingleFirstMinPerHour"]/*' /></remarks>
    internal const double JingleFirstMinPerHour = 1.0;

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
    /// inside one) that VAD still calls "speech". Calibrated to sit roughly midway between the
    /// longest such transient and the shortest genuine announcement word measured on real audio,
    /// erring toward not skipping real speech, since another supported language could plausibly be
    /// shorter than the one German data point. Deliberately tighter than
    /// <see cref="MergeShortSpeechGapSeconds"/>'s cluster-grouping gap: this rejects a single
    /// too-short blip, that decides whether separate blips belong to the same cluster.
    /// <para>
    /// <see cref="JingleCensus"/> bridges its jingles across the same floor, deliberately sharing
    /// this constant rather than the wider merge: what the census reports as one jingle is then the
    /// same stretch --mark-before-jingle's walk would cross in one go.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the transient and announcement-word durations this sits midway between.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="TransientSpeechFloorSeconds"]/*' /></remarks>
    internal const double TransientSpeechFloorSeconds = 0.4;

    /// <summary>
    /// Non-speech an announcement must have in front of it before
    /// <see cref="AnnouncementIsolation"/> accepts it: the pause (or jingle) separating it from the
    /// previous section's narration. Asked wherever a wording carries a <c>^</c> - which every
    /// built-in chapter phrase now does, so this governs ordinary numbered chapters and not only the
    /// unusual ones - and of the prologue and epilogue, which demand it whatever phrase they are
    /// given. A bare number is held to it on both flanks.
    /// <para>
    /// Bounded by measurement across the corpus: every genuine announcement clears it with room, and
    /// the false positive it exists to stop - <c>/epilogo/</c> matching inside Italian "riepilogo"
    /// mid-sentence - falls well below. It sits low in the defensible window rather than midway,
    /// because the tightest genuine mark in the corpus is a single data point, an announcement
    /// dropped for want of a pause is a chapter lost outright, and the false positives on record are
    /// nowhere near the line.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the corpus measurements bounding that window at both ends.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="AnnouncementLeadInSeconds"]/*' /></remarks>
    internal const double AnnouncementLeadInSeconds = 0.85;

    /// <summary>
    /// Non-speech an announcement must have behind it where a wording's <c>$</c> asks for one, and
    /// what a <em>bare number</em> is held to on top of its lead-in
    /// (<see cref="IsolationRule.Both"/>). The bare number is what the figure was set from: the
    /// number is spoken alone, so the pause behind it is as much a part of its shape as the one in
    /// front. Set well below the tightest real measurement rather than just below it, for the same
    /// reason as the lead-in: one book's narrator sets the pace of these pauses, and another's need
    /// not be so generous.
    /// <para>
    /// Deliberately <em>not</em> asked of the prologue, the epilogue or a <c>--custom</c> mapping of
    /// their own accord - only where a wording writes the <c>$</c> that asks for it. A heading word
    /// is routinely run straight into the text that follows it, and the corpus has it both ways
    /// round, so the lead-in alone already rejects every mid-sentence false positive on record and
    /// there is nothing to buy here but a real mark to lose.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the lead-out measured on every bare-number chapter of one book, and the two genuine heading marks a stricter bar would cost.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="AnnouncementLeadOutSeconds"]/*' /></remarks>
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
    /// How far a finished mark may sit inside a VAD speech segment before the refinement that put it
    /// there is disbelieved (<see cref="AnnouncementIsolation.DepthInsideSpeech"/>, applied in
    /// <see cref="MarkPlacer.KeepOutOfSpeech"/>). A mark belongs in a pause or in jingle music, both
    /// of which VAD reads as non-speech, so any real depth here means the mark landed in somebody
    /// else's words.
    /// <para>
    /// Generous rather than tight on purpose, because VAD is capable of calling music speech: two
    /// marks in "Gruelfin.m4b" have it hearing speech 2.54 s and 2.65 s early inside a jingle (see
    /// <see cref="PreciseMarkMusicAnchorCapSeconds"/>). No corpus mark actually lands inside such a
    /// segment, but a spurious one is the only way this guard could fire on a good mark, and the
    /// comparison in <see cref="MarkPlacer.KeepOutOfSpeech"/> - which declines unless the
    /// default-mode position is demonstrably better - is the other half of that protection.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the two marks written into a reader's credit, and why this quantity is bimodal rather than distributed.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="MarkInsideSpeechSeconds"]/*' /></remarks>
    internal const double MarkInsideSpeechSeconds = 0.5;

    /// <summary>
    /// Minimum letters-plus-digits per second a transcript segment must average for
    /// <see cref="JingleGeometry"/>'s corroboration checks (<c>IsGenuineSpeech</c>) to accept it
    /// as evidence that a VAD blip is real narration rather than jingle music. "The transcript
    /// covers this blip with real words" hides an assumption: ordinary narration runs several
    /// times this fast, so coverage at a fraction of that pace is not continuous speech but a long
    /// near-silent stretch of music/reverb that Whisper folded into one oversized segment together
    /// with a few real words at its edges (one merged segment was observed spanning almost 30 s
    /// while containing a couple of seconds of speech). Set well below the real-narration floor,
    /// leaving margin for slow delivery or short, punctuation-heavy segments.
    /// </summary>
    /// <remarks>Notes: the measured pace of genuine segments against smeared ones.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="MinPlausibleSpeechCharsPerSecond"]/*' /></remarks>
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
    /// mark while the announcement it retreated from starts one <c>--mark-lead</c> after the
    /// pre-walk mark; any smaller gap puts that announcement inside the probe's own window, where
    /// "still audible" is a foregone conclusion that reads a short jingle, a deliberate "no jingle
    /// here" outcome and a failed walk exactly alike. Below this gap the walk is trusted unprobed.
    /// <para>
    /// The guard evaluates that subtraction against the run's own
    /// <see cref="CliOptions.MarkLeadSeconds"/>, so this constant is the value it takes at the
    /// default lead and nothing more - it is what the shipped configuration measures against, and
    /// the figure to reason about when reading a log from one.
    /// </para>
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
    /// The failure this exists to catch is a <em>hole</em> in the plateau: a run of "no" several
    /// tenths of a second wide sitting well before the true onset, which a plain bisection reads as
    /// the plateau's end and returns the hole's left edge for - putting the mark over a second early
    /// and, in the case on record, making an already-good mark worse. The hole is a property of the
    /// model rather than of the audio; a larger recognizer reads the same grid perfectly monotone,
    /// overshooting slightly instead, which <see cref="PreciseMarkRefiner.OnsetOf"/>'s lead-in
    /// subtraction absorbs. Looking for the <em>rightmost</em> plateau rather than stopping at the
    /// first edge it meets is what makes both models land within a fifth of a second of each other.
    /// </para>
    /// <para>
    /// Spacing and reach are deliberately coarse: this is a "did the plateau resume?" question, and
    /// whichever probe lands, the walk restarts from it and re-derives the edge at the usual
    /// <see cref="PreciseMarkFixedStepSeconds"/> accuracy. The spacing is set to put a probe inside
    /// the narrowest resumed plateau measured, and the reach to clear a hole half again as wide as
    /// the one observed. Four probes is what that costs every refinement, holes or not.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the Die Dritte Macht chapter 7 probe grid that measured the hole, the same grid on the upgrade model, and what four probes cost per book.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="PreciseMarkPlateauProbesSeconds"]/*' /></remarks>
    internal static readonly double[] PreciseMarkPlateauProbesSeconds = [0.3, 0.6, 0.9, 1.2];

    /// <summary>
    /// How far <see cref="PreciseMarkRefiner.FindOnsetEdgeAsync"/>'s plateau walk may run from the
    /// position it set out from, counting resumes.
    /// <para>
    /// A runaway guard, not a question about music - which is why it is a constant and not the
    /// census's jingle reach. The walk climbs forward from a confirmed phrase to the far edge of the
    /// plateau it sits on; what must never happen is that it climbs on into the <em>next</em>
    /// chapter's announcement, and what bounds that is how far apart two chapters are, not how long
    /// this book's jingles run. Handed a census-derived figure it would collapse to a few seconds on
    /// a book with no jingles and strangle the walk there.
    /// </para>
    /// <para>
    /// 60 s: comfortably past any real plateau (the longest observed runs a few seconds) and
    /// comfortably short of a chapter. It replaces the <c>--max-jingle-length + margin</c> = 50 s
    /// this read before 0.12.0, which was that same kind of number arrived at by coincidence of
    /// value. The 10 s of extra room is deliberate, and is also the one part of removing that option
    /// which can move an existing mark - a walk that used to stop at the limit can now go further.
    /// </para>
    /// <para>
    /// Capping it by the distance to the next chapter was considered and dropped: Probe's forward
    /// scan has not found that chapter yet, so the constant would still be doing the work almost
    /// everywhere it matters.
    /// </para>
    /// </summary>
    internal const double PlateauWalkLimitSeconds = 60;

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
    /// <remarks>Notes: the probe probabilities by clip length that put the floor at six seconds.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="PreciseMarkMinSurvivalSeconds"]/*' /></remarks>
    internal const double PreciseMarkMinSurvivalSeconds = 6.0;

    /// <summary>
    /// How far before its end anchor <see cref="PreciseMarkRefiner.LocatePhraseByShrinkingWindowAsync"/>
    /// will look for the announcement when the matched segment's own start timestamp does not reach
    /// that far back on its own. The bracket is normally drawn one
    /// <see cref="PhraseMarginSeconds"/> below that timestamp, which quietly assumes Whisper never
    /// times a segment <em>later</em> than the words in it - and it does, putting the whole bracket
    /// past the announcement so that no probe inside it could ever hear it.
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
    /// <remarks>Notes: the segment Whisper timestamped later than the words in it, which is why the floor is extended at all.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="PreciseMarkMaxBracketSeconds"]/*' /></remarks>
    internal const double PreciseMarkMaxBracketSeconds = WhisperChunkSeconds - PhraseMarginSeconds;

    /// <summary>
    /// The stride whisper.cpp decodes in: it converts audio to a mel spectrogram of exactly this
    /// length at a time, so a window at or above it is transcribed as several passes whose results
    /// are stitched together, while a shorter one is a single pass over the whole thing.
    /// <para>
    /// This is not an implementation detail that stays inside the recognizer, which is why it has a
    /// name here: an announcement is one or two words against minutes of narration, and crossing the
    /// boundary is enough to lose it - twice, on the same book's prologue.
    /// </para>
    /// <para>
    /// The same book lost it again in build 280 (2026-08-09), to a window one second short of a
    /// chunk's worth of narration rather than twenty: the classified jingle candidate opens
    /// <see cref="JingleLeadInSeconds"/> into the music and runs
    /// <see cref="ExpectedAnnouncementSeconds"/> past the announcement, which is exactly 30.0 s. The
    /// cliff sits between 25 and 27 s rather than at 30, so the phrase margin
    /// <see cref="JingleRereadWindowSeconds"/> keeps below a chunk is the whole of the safety margin
    /// rather than a rounding.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the two Gruelfin prologue losses and the decode grids behind the 25-27 s cliff.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="WhisperChunkSeconds"]/*' /></remarks>
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
    /// How far <see cref="RegionProber.RereadJingleMusicAsync"/> advances between two of the tiles it
    /// reads a jingle's music with. One <see cref="JingleRereadWindowSeconds"/> less two phrase
    /// margins, so consecutive tiles overlap by twice the longest announcement expected here: an
    /// announcement can then never fall across a tile border without landing whole inside one of the
    /// two - the same guarantee a spanning window gave, at a width the recognizer can still hear a
    /// lone word in.
    /// <para>
    /// Two margins rather than one, because both ends of an announcement have to clear the border:
    /// one margin would guarantee only that a phrase <em>starting</em> before the border is complete
    /// in the earlier tile, and the case this exists for - a word or two spoken over music - is
    /// exactly where the recognizer needs the run-up as much as the tail.
    /// </para>
    /// </summary>
    internal const double JingleMusicTileStepSeconds = JingleRereadWindowSeconds - 2 * PhraseMarginSeconds;

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
    /// The effect is per-narrator, not per-language.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the bimodal gap distribution that lets any threshold across a wide range give the identical result.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="PreciseMarkSilenceAnchorSeconds"]/*' /></remarks>
    internal const double PreciseMarkSilenceAnchorSeconds = 1.0;

    /// <summary>
    /// How far <see cref="PreciseMarkRefiner.AnchorOnsetToSoundAsync"/> may pull an onset back onto
    /// the point where the <em>music</em> in front of it gives way to speech - the jingle's
    /// counterpart to the silence floor, used only where no silence closed within
    /// <see cref="PreciseMarkSilenceAnchorSeconds"/> and so nothing else can correct the plateau
    /// edge's known late bias.
    /// <para>
    /// Capped rather than trusted outright, because the voice-activity detector's idea of where
    /// speech resumes is occasionally far out - one corpus book has it hearing speech more than two
    /// seconds before the announcement, and anchoring straight to that would plant those marks in
    /// the middle of the music. The cap keeps the correction worth having on the tail without
    /// letting one bad reading move a mark by seconds.
    /// </para>
    /// <para>
    /// Set to <see cref="PreciseMarkFixedStepSeconds"/>, and that identity is the argument for the
    /// value rather than a coincidence to tidy away: the onset is the output of a search that walks
    /// in steps of exactly that size, so it is only ever known to within one of them. A correction
    /// bounded by one step claims no more precision than the search that produced it.
    /// </para>
    /// </summary>
    /// <remarks>Notes: how close the VAD speech resumption normally sits to the plateau edge, and the two marks it is far out on.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="PreciseMarkMusicAnchorCapSeconds"]/*' /></remarks>
    internal const double PreciseMarkMusicAnchorCapSeconds = PreciseMarkFixedStepSeconds;

    /// <summary>
    /// How far below the loudest thing in its window <see cref="PreciseMarkRefiner.AnchorOnsetToSoundAsync"/>
    /// still counts audio as the pause rather than as the announcement. Relative to that peak - the
    /// announcement's own level - rather than an absolute dBFS figure, so a quietly mastered book
    /// and a loud one behave identically and nothing needs recalibrating per file. That is also why
    /// no loudness normalization pass is wanted upstream: it would change every level in the file
    /// and leave this test measuring exactly the same ratio.
    /// <para>
    /// 25 dB is a power ratio of about 300. Comfortably above the room tone a narrator's pause is
    /// actually made of, and comfortably below the announcement's own opening consonant.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the room-tone and opening-consonant levels measured on the two calibration cases.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="PreciseMarkOnsetFloorDb"]/*' /></remarks>
    internal const double PreciseMarkOnsetFloorDb = 25;

    /// <summary>
    /// How long audio must stay above <see cref="PreciseMarkOnsetFloorDb"/> before
    /// <see cref="PreciseMarkRefiner.AnchorOnsetToSoundAsync"/> accepts it as the announcement
    /// beginning rather than as a click inside the pause. Long enough to outlast such a transient,
    /// short enough that no real speech onset is missed: a syllable runs several times this.
    /// </summary>
    /// <remarks>Notes: the mouth noise that closed a silence early, and what it cost.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="PreciseMarkOnsetSustainSeconds"]/*' /></remarks>
    internal const double PreciseMarkOnsetSustainSeconds = 0.05;

    /// <summary>
    /// How many announcements must be rejected as below the sequence, their own numbers ascending,
    /// before <see cref="RegionProber.SequenceRestartSkips"/> reports the file as one whose chapter
    /// numbering restarts (see <see cref="RegionProber.NoteOutOfSequence"/>).
    /// <para>
    /// A single announcement numbered below the last accepted one is the ordinary in-text mention
    /// ("as I said in chapter three") the rejection message already guesses at; a run of them
    /// climbing 1, 2, 3 is a book divided into parts. Three is a comfortable floor between what an
    /// ordinary book produces and what a genuinely restarting one does.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the corpus counts on both sides of that floor.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="SequenceRestartRunLength"]/*' /></remarks>
    internal const int SequenceRestartRunLength = 3;

    /// <summary>
    /// With --min-silence-length auto, the Probe probing threshold is this factor times a mark's
    /// anchor silence length, i.e. a 25 % margin below the shortest observed inter-chapter break.
    /// Monotonic: the first qualifying mark
    /// (the second one found) raises the threshold off the floor, every later mark can only lower
    /// it again - a threshold above an observed inter-chapter silence would by definition skip the
    /// very kind of silence proven to precede this book's chapters.
    /// </summary>
    internal const double AdaptiveTightenFactor = 0.75;

    /// <summary>
    /// How short a chapter break --min-silence-length auto may end up believing in
    /// (<see cref="RegionProber.ProposeThreshold"/>), independent of the length the run
    /// <em>starts</em> at. The two are separate questions: --min-silence-length is the demand a run
    /// opens with, this is how far the book's own inter-chapter breaks may argue it down once they
    /// have been measured. What the threshold reaches below the starting demand does not widen the
    /// candidate grid - it sizes <see cref="ChapterDetector.SweepAdaptiveSubFloorAsync"/>, which is
    /// where the reasoning behind that separation is written down.
    /// <para>
    /// 0.8 s is where the evidence sits. Below <see cref="MinStoredSilenceSeconds"/> nothing is
    /// retained by Analyze to sweep in the first place, so the reachable range is [0.5, 1.5); 0.8
    /// covers the sub-floor sweep's whole span (<see cref="SubFloorSweepBandCount"/> bands reaching
    /// 1.0 s) with room to spare while leaving the very shortest stored band - ordinary clause
    /// pauses, which is what that floor was chosen to catch - out of reach.
    /// </para>
    /// <para>
    /// The reason the sweep is gap-bounded and budgeted rather than run over whole regions is this
    /// floor's one sharp edge: <see cref="RegionProber.ProposeThreshold"/> keeps a running
    /// <em>minimum</em>, so one tight transition anywhere pins the measurement here for the rest of
    /// the book, and the band would then be the full [0.8, 1.5) over every remaining hour of it.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the two books that put the floor at 0.8 s, and the silence counts showing what an unbounded band would cost.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="AdaptiveSilenceFloorSeconds"]/*' /></remarks>
    internal const double AdaptiveSilenceFloorSeconds = 0.8;

    /// <summary>
    /// How many chapters a single announcement may leave missing before its number is treated as
    /// suspect and re-read (<see cref="SuspectNumberMender"/>). Above this, a misheard number is by
    /// far the likelier explanation than that many consecutive announcements going unheard: the
    /// numbers that sound alike are exactly the ones that are far apart, since the confusion is in
    /// the word, not the value.
    /// <para>
    /// Set at three because that is where the two costs cross. Gaps of one to three are the ordinary
    /// kind - a chapter with no jingle, an unreadable number, an announcement the narrator rushed -
    /// and the re-probe plus Re-probe/3 close them at a cost proportional to the gap. A gap of dozens
    /// costs a full transcription of hours of audio, and the mark it eventually produces still carries
    /// the wrong number, so paying a handful of transcriptions to question it is a bargain that only
    /// gets better the wider the gap.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the mishearing that shows why numbers far apart are the ones that sound alike.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="SuspectGapMinMissing"]/*' /></remarks>
    internal const int SuspectGapMinMissing = 3;

    /// <summary>
    /// The re-framings <see cref="SuspectNumberMender"/> tries on a suspect announcement with the
    /// probe model, as (seconds of lead before the announcement, total window length). Whisper's
    /// output for a given stretch of audio depends on the window it arrives in, so a second look at
    /// the same announcement through differently sized windows is a genuinely different reading and
    /// not a re-roll of the same dice - the same property <see cref="RegionProber"/>'s gap re-probe
    /// relies on.
    /// <para>
    /// The two widths straddle the case that first demonstrated it. The leads differ as well as the
    /// widths, so the announcement's offset inside Whisper's fixed 30 s mel frame moves too rather
    /// than only the amount of context around it.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the chapter that read Roman from one width and digits from the other.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="SuspectGapReframes"]/*' /></remarks>
    internal static readonly (double LeadSeconds, double LengthSeconds)[] SuspectGapReframes =
        [(2.0, 15.0), (12.0, 45.0)];

    /// <summary>
    /// How many numbered readings a mark refinement's own probes must yield between them before
    /// their verdict may overrule the detecting window's (<see cref="RefinedNumberVote"/>). The
    /// winner additionally needs a strict majority, so this is really "at least two against one".
    /// Readings rather than probes: one probe transcript carrying two numbered matches votes
    /// twice, which is what the corpus figures below were measured on.
    /// <para>
    /// The whole check is free - those probes are transcribed either way, and only their yes/no
    /// answer used to be kept - and it is close to unerring on the corpus it was calibrated against.
    /// </para>
    /// <para>
    /// Three rather than two because a two-probe refinement is a thin sample and abstaining costs
    /// nothing: the vote only ever acts when it <em>disagrees</em>, and a number left uncorrected
    /// here still faces <see cref="SuspectNumberMender"/>, the ascending-sequence filter in
    /// <see cref="GapPlanning.Normalize"/> and the repair in
    /// <see cref="ChapterDetector.RepairSequenceOutliersAsync"/>.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the 271-mark agreement count, the four split votes, and what a minimum of three abstains on.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="RefinedNumberVoteMinimum"]/*' /></remarks>
    internal const int RefinedNumberVoteMinimum = 3;

    /// <summary>
    /// How many outliers <see cref="ChapterDetector.RepairSequenceOutliersAsync"/> may spend audio
    /// re-reads on in one file. Only the ambiguous ones cost anything: an outlier whose bracketing
    /// chapters leave a single number unaccounted for is settled from the sequence alone, for free
    /// and without a cap.
    /// <para>
    /// Generous rather than tight, because reaching this cap means the book has more mis-numbered
    /// marks than it has chapters worth trusting, and eight re-reads (at most two decodes each) is a
    /// rounding error next to the Scan such a book is heading for anyway.
    /// </para>
    /// </summary>
    /// <remarks>Notes: how many outliers a real corpus actually produces.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="MaxSequenceRepairsPerFile"]/*' /></remarks>
    internal const int MaxSequenceRepairsPerFile = 8;

    /// <summary>
    /// How many unreadable-number re-reads (<see cref="SuspectNumberMender.ReadUnnumberedAsync"/>)
    /// one <see cref="RegionProber"/> region may run. The same guard
    /// <see cref="MaxUnnumberedRetriesPerChunk"/> puts on Scan, for the same reason: an in-text
    /// mention reaches the re-read too, and each one costs up to three decodes.
    /// <para>
    /// Set well above what real books need rather than tightly, because the event is rare and the
    /// cases it does fire on are the ones worth paying for. A per-region cap therefore only ever
    /// bites on a book whose prose talks about chapters constantly, which is exactly the book where
    /// it should.
    /// </para>
    /// </summary>
    /// <remarks>Notes: how rarely this fires across a seven-book run, and the two cases that did.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="MaxUnnumberedMendsPerRegion"]/*' /></remarks>
    internal const int MaxUnnumberedMendsPerRegion = 8;

    /// <summary>
    /// How many sub-floor silence bands Re-probe sweeps through before giving a gap up to Scan,
    /// and how wide each band is (see <see cref="GapPlanning.SubFloorSweepBands"/>). Five bands of
    /// 0.1 s reach from just under <c>--min-silence-length</c> down to 0.5 s below it - at the
    /// default floor, [1.4, 1.5) down to [1.0, 1.1).
    /// <para>
    /// The range is measured, not guessed: on the book this was calibrated against, every chapter
    /// Probe missed was preceded by a pause just under the floor. A book whose breaks are that short
    /// will have all of them in the top band or two - which is why the sweeps run longest-first and
    /// stop the moment the gap closes, instead of one wide sweep down to 1.0 s.
    /// </para>
    /// <para>
    /// Stopping at 0.5 s below the floor is where the yield dies: band populations grow roughly
    /// geometrically downwards, so each further step buys several times as much probing as the one
    /// before it while the chance of a real chapter break hiding there keeps falling.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the five pauses that set the range, and the measured band populations.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="SubFloorSweepBandCount"]/*' /></remarks>
    internal const int SubFloorSweepBandCount = 5;

    /// <summary>Width of one <see cref="SubFloorSweepBandCount"/> band, in seconds.</summary>
    internal const double SubFloorSweepBandSeconds = 0.1;

    /// <summary>
    /// How much of Scan's own cost the sub-floor sweep may spend on a gap before giving it up
    /// (<see cref="ChapterDetector.SweepSubFloorSilencesAsync"/>), measured in
    /// <see cref="WhisperChunkSeconds"/> decode windows on both sides.
    /// <para>
    /// Below one rather than at it because the sweep does not replace Scan, it precedes it: a
    /// sweep that finds nothing is spent on top of the full transcription that follows, so a budget
    /// of the whole thing would let a gap cost twice what it did before the sweeps existed. Three
    /// quarters keeps the worst case at 1.75x while still affording one probe per 80 s of gap at the
    /// 50 s ceiling window, or per 40 s at the plain 12 s one - far more than the one or two a real
    /// book's bands hold.
    /// </para>
    /// </summary>
    /// <remarks>Notes: what a real book's bands actually cost.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="SubFloorSweepBudgetFraction"]/*' /></remarks>
    internal const double SubFloorSweepBudgetFraction = 0.75;

    /// <summary>
    /// How far Scan's shifted re-scan (<see cref="ChapterDetector.RescanShiftedAsync"/>) displaces
    /// its decodes when a full transcription has left a gap open: half of
    /// <see cref="WhisperChunkSeconds"/>, which is the displacement that moves whatever sat on an
    /// internal decode window border as far from one as it can get. Any other value leaves some
    /// offset that was near a border still near one.
    /// </summary>
    internal const double RescanShiftSeconds = WhisperChunkSeconds / 2;

    /// <summary>Chunk length in seconds for full transcription of gap regions.</summary>
    internal const double GapChunkSeconds = 600;

    /// <summary>Overlap between gap transcription chunks so no phrase is cut in half. Only for a
    /// chunk border that could not be snapped to a word-safe seam (see
    /// <see cref="ScanSeamSearchSeconds"/>); snapped borders abut exactly and need no
    /// redundancy.</summary>
    internal const double GapChunkOverlapSeconds = 10;

    /// <summary>
    /// How far around a Scan chunk's natural border the seam search reaches in each direction:
    /// the border snaps to the nearest silence - or VAD non-speech region, where the pre-pass ran -
    /// mid-point in range, and the next chunk starts exactly there, with nothing decoded twice.
    /// Bounded so a chunk grows to at most <see cref="GapChunkSeconds"/> plus this: whisper.cpp
    /// has no hard input-length cap (it decodes in internal 30 s strides), but the decoded sample
    /// buffer scales with chunk length.
    /// </summary>
    internal const double ScanSeamSearchSeconds = 30;

    /// <summary>
    /// At a snapped (overlap-free) Scan seam, segments of the previous chunk ending within this
    /// many seconds before it are carried into the next chunk's phrase matching, so an
    /// announcement straddling the seam - the narrator pausing right where the seam silence sits,
    /// e.g. between "Chapter" and its number - is still found although neither chunk alone holds
    /// the whole phrase. Comfortably longer than any spoken announcement. Irrelevant at unsnapped
    /// borders, where <see cref="GapChunkOverlapSeconds"/> provides the redundancy.
    /// </summary>
    internal const double ScanBridgeSeconds = 15;

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
    /// The case that forced this was a single confident chapter-1 mark settling thousands of windows
    /// in one step, skipping most of a fifteen-hour book outright - with all of them also queued for
    /// the unconditional gap re-probe that any later sequence gap would trigger.
    /// </para>
    /// <para>
    /// Ten is where the corpus says the cap costs nothing, and the cap is safe by construction in a
    /// way the skip is not: a clipped window is <em>probed</em>, never dropped, so this can only ever
    /// find more chapters than going uncapped would.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the run that skipped eleven hours of a book, and the chain-length histogram behind the cap.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="MaxSettledWindowSkip"]/*' /></remarks>
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
    /// to agree exactly. Only the second check catches the duplicates seen on real audio.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the book and run those duplicate pairs came from.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="NamedMarkDedupeSeconds"]/*' /></remarks>
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
    /// is simply dropped. Two numbered ones disagree about something, and dropping either one at
    /// random would be a coin toss over which number the book carries from there on.
    /// </para>
    /// <para>
    /// <see cref="ChapterDetector.DropNamedMarkEchoes"/> borrows it for a third question - is this
    /// chapter mark another line of a named announcement's heading? - on the strength of the same
    /// "well below the shortest chapter anyone writes" half. The spread it has to cover there is
    /// between two lines of one heading rather than between two readings of one onset.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the two-mark disagreement that makes this a separate rule, and the heading spread it has to cover.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="CollidingChapterMarkSeconds"]/*' /></remarks>
    internal const double CollidingChapterMarkSeconds = NamedMarkDedupeSeconds / 2;

    /// <summary>
    /// Whisper language-detection probability at or above which a single probe settles the file's
    /// language outright, with <c>--lang auto</c>. Below it the sample is re-taken elsewhere in the
    /// book (see <see cref="AutoLanguageProbeAttempts"/>) and, if nothing ever clears the bar, the
    /// attempts are voted on.
    /// <para>
    /// Raised from 0.5 to 0.6 on 2026-08-03. The old value was <see cref="LowConfidenceThreshold"/>
    /// borrowed for a question it does not really describe, and it was never the binding problem.
    /// What changed is that falling below the bar is now cheap - it costs another probe rather than
    /// the whole book - so the bar may as well sit where a weak answer stops being worth believing.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the misdetection that raising the bar would not have caught either.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="AutoLanguageProbabilityThreshold"]/*' /></remarks>
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
    /// Measured over the window rather than demanded of one contiguous VAD run, which is the version
    /// this replaced and was wrong in practice: raw VAD segments are sentence-sized, so a 20 s
    /// unbroken run is rare enough that the search for one walked far away from the anchor it was
    /// supposed to sample near. A window that is half speech is what "this is narration" actually
    /// means; whether the reader paused for breath inside it does not matter, since the detector
    /// reads the whole 30 s.
    /// </para>
    /// </summary>
    /// <remarks>Notes: how far the contiguous-run version wandered, and the segment statistics behind it.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="AutoLanguageMinSpeechInWindowSeconds"]/*' /></remarks>
    internal const double AutoLanguageMinSpeechInWindowSeconds = AutoLanguageProbeSeconds / 2;

    /// <summary>How far past an existing chapter mark a language-detection sample starts, on
    /// the --verify and resume paths. Clears the announcement and the jingle around it, landing the
    /// window in the chapter's own narration.</summary>
    internal const double AutoLanguageExistingMarkOffsetSeconds = 20;

    /// <summary>
    /// Minimum length of the leading region (file start to the first detected chapter) for Scan
    /// to transcribe it in search of earlier chapters when the first detection is not chapter 1.
    /// A first chapter within this many seconds of the start is taken as-is - the book simply
    /// begins mid-series, with no room for a missed earlier chapter, and the intro chapter covers
    /// the short lead-in anyway.
    /// </summary>
    internal const double MinLeadingGapSeconds = 10;

    /// <summary>How far before a pre-existing chapter mark's own timestamp --verify starts
    /// probing - the mark may sit slightly after the phrase actually started.</summary>
    internal const double VerifyMarginBeforeSeconds = 10;

    /// <summary>Total length of the --verify probe window, starting
    /// <see cref="VerifyMarginBeforeSeconds"/> before the mark.</summary>
    internal const double VerifyWindowSeconds = 60;

    /// <summary>
    /// How far off a confirmed mark has to be before <c>--verify --fix</c> bothers moving it.
    /// Rewriting an audiobook remuxes the whole file, so a correction has to be worth that: a tenth
    /// of a second is inside the accuracy the refinement itself claims, and moving a mark by it
    /// would be shuffling noise. Set at a quarter of a second, comfortably under the
    /// <see cref="DefaultMarkLeadSeconds"/> lead a listener would actually notice losing.
    /// </summary>
    internal const double VerifyFixMinShiftSeconds = 0.25;

    /// <summary>
    /// The largest correction <c>--verify --fix</c> will apply. Beyond this the mark is left
    /// alone and reported: a mark tens of seconds from its announcement is not a mark that drifted,
    /// it is a mark that means something else - a retailer's grouping, a different edition's
    /// numbering - and quietly dragging it onto the nearest matching phrase would destroy
    /// information rather than correct it. Half the <see cref="VerifyWindowSeconds"/> window, which
    /// is also the furthest a confirmation can be found from the mark in either direction.
    /// </summary>
    internal const double VerifyFixMaxShiftSeconds = 30;

    /// <summary>
    /// Minimum length for a gap between transcribed segments (or before the first/after the last)
    /// to be worth a focused re-transcription - and, for Scan's version of the same fallback
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
    /// How far before the phrase a Scan "heard it, could not number it" retry starts its decode.
    /// The retry exists because the notation Whisper writes a number in follows the window framing
    /// rather than the audio. This sits comfortably past the observed flip - the run-up is what has
    /// to be in the window, and a couple of seconds either way costs nothing.
    /// </summary>
    /// <remarks>Notes: the window starts that flipped one chapter between Roman numerals and digits.
    /// <include file='../../notes/Detection/DetectionTuning.xml' path='doc/member[@name="UnnumberedRetryLeadSeconds"]/*' /></remarks>
    internal const double UnnumberedRetryLeadSeconds = 8.0;

    /// <summary>Length of a Scan "heard it, could not number it" retry decode, chosen to match the
    /// framing that produced a readable number in the case <see cref="UnnumberedRetryLeadSeconds"/>
    /// documents, rather than the short sub-chunks <see cref="GapRetryChunkSeconds"/> uses - those
    /// recover audio Whisper skipped, which is the opposite problem and wants the opposite
    /// window.</summary>
    internal const double UnnumberedRetryWindowSeconds = 45.0;

    /// <summary>
    /// How many unreadable-number retries one Scan chunk may run. In-text mentions ("the next
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
