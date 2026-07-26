// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Cli;
using ABChapterize.Language;

namespace ABChapterize.Detection;

/// <summary>A detected chapter start: number plus position in the file.</summary>
/// <param name="Number">Chapter number as spoken/parsed.</param>
/// <param name="TimeSeconds">Position of the chapter marking in seconds.</param>
/// <param name="Confidence">Whisper's probability for the segment the chapter number was parsed
/// from (0-1); 1.0 when unknown. Below <see cref="DetectionTuning.LowConfidenceThreshold"/> the
/// number surfaces in <see cref="DetectionResult.LowConfidenceNumbers"/>.</param>
public readonly record struct DetectedChapter(int Number, double TimeSeconds, double Confidence = 1.0);

/// <summary>Per-file diagnostic statistics gathered during detection, surfaced per file under
/// --verbose (or --verbose-transcripts) and aggregated run-wide under --summary. The silence and
/// jingle extremes come in two flavours: one over every detected chapter, and an "inter-chapter"
/// one that excludes chapter 1 - whose intro-to-first-chapter transition is often atypically long
/// or short and would otherwise skew the picture of the book's regular chapter breaks.</summary>
/// <param name="MinPrecedingSilenceSeconds">Shortest silence directly before a detected chapter
/// phrase - when the VAD pre-pass ran, the silence leading the jingle (a jingle framed by two
/// silences counts only its leading one); null when no chapter had a qualifying one.</param>
/// <param name="MinInterChapterSilenceSeconds">As <paramref name="MinPrecedingSilenceSeconds"/>,
/// but excluding chapter 1; null when no other chapter had a qualifying silence.</param>
/// <param name="MaxJingleLengthSeconds">Longest jingle before a detected chapter phrase (only
/// measured when the VAD pre-pass ran); null when it did not run or nothing was measured.</param>
/// <param name="MaxInterChapterJingleSeconds">As <paramref name="MaxJingleLengthSeconds"/>, but
/// excluding chapter 1; null when no other chapter had a measured jingle.</param>
/// <param name="WhisperAudioSeconds">Total audio decoded and handed to Whisper during detection,
/// counting re-probed stretches each time they were transcribed; compare against the file's run
/// length for the fed-in share.</param>
/// <param name="WhisperTranscribeSeconds">Wall-clock time inside the Whisper transcription calls
/// themselves (not decoding). <see cref="WhisperAudioSeconds"/> over this is the transcription
/// speed relative to real time.</param>
public readonly record struct DetectionStats(
    double? MinPrecedingSilenceSeconds, double? MinInterChapterSilenceSeconds,
    double? MaxJingleLengthSeconds, double? MaxInterChapterJingleSeconds,
    double WhisperAudioSeconds, double WhisperTranscribeSeconds);

/// <summary>Outcome of chapter detection for one file.</summary>
/// <param name="Chapters">Detected chapters in chronological order; empty when none were found.</param>
/// <param name="GapRemains">True when a chapter sequence gap could not be resolved; the file must
/// be left unchanged.</param>
/// <param name="MissingNumbers">The chapter numbers that could not be located (only when
/// <paramref name="GapRemains"/>).</param>
/// <param name="LowConfidenceNumbers">Chapter numbers whose Whisper probability fell below
/// <see cref="DetectionTuning.LowConfidenceThreshold"/> - worth a manual spot-check.</param>
/// <param name="Profile">The language profile actually used for this file - the resolved per-file
/// profile with <c>--lang auto</c>, else the run's fixed <see cref="CliOptions.DefaultProfile"/>.</param>
/// <param name="DetectedLanguage">Whisper's raw language guess with <c>--lang auto</c>; null when
/// auto-detection was not active, or was skipped because the file was too short to probe.</param>
/// <param name="DetectedProbability">Whisper's probability for <paramref name="DetectedLanguage"/>;
/// 0 when that is null. May disagree with <see cref="Profile"/>'s language, when the probability
/// fell below <see cref="DetectionTuning.AutoLanguageProbabilityThreshold"/> and the run fell back
/// to English.</param>
/// <param name="Stats">Per-file diagnostic statistics (min preceding silence, max jingle, total
/// Whisper audio) for the --verbose and --summary reports.</param>
/// <param name="EarlyAborted">True when --early-abort cut detection short because no chapter was
/// found within its minute threshold; <paramref name="Chapters"/> is then always empty, same as
/// for a completed scan that genuinely found nothing.</param>
/// <param name="BelowExpectedStartNumber">The chapter number Pass 2 found first, when
/// --expected-start-chapter aborted detection because it was numbered below that expectation; null
/// otherwise. <paramref name="Chapters"/> is always empty when set, as with
/// <paramref name="EarlyAborted"/>.</param>
/// <param name="LeadInHasSpeech">True unless the VAD pre-pass ran and found no speech at all before
/// the first chapter's own mark - i.e. the first words spoken anywhere in the file are the chapter
/// phrase itself, however much silence, music or a jingle precedes it. <see cref="FileProcessor"/>'s
/// intro-chapter insertion skips inserting one when this is false, letting the mp4 muxer's own
/// start-snapping fold that lead-in into chapter 1 instead of giving it its own titled entry.
/// Always true when the VAD pre-pass did not run (nothing to check) or
/// <paramref name="Chapters"/> is empty.</param>
public readonly record struct DetectionResult(
    IReadOnlyList<DetectedChapter> Chapters, bool GapRemains, IReadOnlyList<int> MissingNumbers,
    IReadOnlyList<int> LowConfidenceNumbers, LanguageProfile Profile,
    string? DetectedLanguage, double DetectedProbability, DetectionStats Stats, bool EarlyAborted = false,
    int? BelowExpectedStartNumber = null, bool LeadInHasSpeech = true);

/// <summary>Outcome of checking one pre-existing chapter marking against the audio, in file order -
/// the raw material <see cref="GapPlanning.BuildGapRegions"/> groups into gap-scoped recovery
/// regions for <see cref="ChapterDetector.DetectGapsAsync"/>.</summary>
/// <param name="StartSeconds">The marking's own pre-existing timestamp.</param>
/// <param name="ExpectedNumber">The chapter number parsed from the marking's title, or null when
/// its title had none (e.g. a prelude/intro entry) - such a marking counts neither as confirmed
/// nor as a gap boundary and is skipped when regions are built.</param>
/// <param name="Confirmed">True when Whisper found the expected phrase near this marking.</param>
public readonly record struct VerifyMarkingOutcome(double StartSeconds, int? ExpectedNumber, bool Confirmed);

/// <summary>Outcome of checking pre-existing chapter markings against the audio (--verify).</summary>
/// <param name="Passed">True when every checkable marking was confirmed; also true when none of the
/// file's markings had a parseable expected number (nothing to disprove).</param>
/// <param name="Checked">Number of markings that had a parseable expected number and were actually
/// probed. Markings without one (e.g. a prelude/intro entry) are not counted.</param>
/// <param name="Failed">Of <paramref name="Checked"/>, how many could not be confirmed.</param>
/// <param name="ConfirmedChapters">The confirmed markings, trusted and importable directly as
/// detected chapters - the seed <see cref="ChapterDetector.DetectGapsAsync"/> builds on instead of
/// redetecting them.</param>
/// <param name="Markings">Every marking's own outcome, in file order - the input to
/// <see cref="GapPlanning.BuildGapRegions"/>.</param>
/// <param name="Profile">The language profile resolved while verifying (or the run's fixed
/// <see cref="CliOptions.DefaultProfile"/> when nothing needed resolving); reused as-is by
/// <see cref="ChapterDetector.DetectGapsAsync"/> so gap recovery never re-resolves the language.</param>
/// <param name="DetectedLanguage">Whisper's raw language guess with <c>--lang auto</c>; null when
/// auto-detection was not active or every marking's window was empty.</param>
/// <param name="DetectedProbability">Whisper's probability for <paramref name="DetectedLanguage"/>;
/// 0 when that is null.</param>
public readonly record struct VerifyResult(
    bool Passed, int Checked, int Failed,
    IReadOnlyList<DetectedChapter> ConfirmedChapters, IReadOnlyList<VerifyMarkingOutcome> Markings,
    LanguageProfile Profile, string? DetectedLanguage, double DetectedProbability);
