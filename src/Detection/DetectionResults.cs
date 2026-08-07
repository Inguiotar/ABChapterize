// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Cli;
using ABChapterize.Language;
using ABChapterize.Processing;

namespace ABChapterize.Detection;

/// <summary>A detected chapter start: number plus position in the file.</summary>
/// <param name="Number">Chapter number as spoken/parsed.</param>
/// <param name="TimeSeconds">Position of the chapter marking in seconds.</param>
/// <param name="Confidence">Whisper's probability for the segment the chapter number was parsed
/// from (0-1); 1.0 when unknown. Below <see cref="DetectionTuning.LowConfidenceThreshold"/> the
/// number surfaces in <see cref="DetectionResult.LowConfidenceNumbers"/>.</param>
/// <param name="NumberUnverified">True when <see cref="SuspectNumberMender"/> was asked about this
/// number - it leaves an implausible hole below it (see <see cref="NumberBounds.Admits"/>) - and
/// nothing could corroborate or correct it. Such a mark keeps its number and its position; what it
/// loses is the authority to say how far the book runs, so <see cref="GapPlanning.FindGaps"/> opens
/// no gap beneath it and <see cref="GapPlanning.ChapterProgress"/> counts nothing under it as
/// missing.
/// <para>
/// The shape this exists for is a number heard perfectly that belongs to something other than a
/// chapter, which every other defence is blind to because they all reason about mishearing.
/// "Corsa nello spazio" (Italian, 18 h, <c>--chapter-phrase none</c>, build 251, 2026-08-06) heads
/// its epilogue "Epilogo / 2179 / Spazio profondo", and 2179 is the year: spoken alone, flanked by
/// real pauses so <see cref="AnnouncementIsolation"/> passes it, and read as 2179 by the mender's
/// 15 s and 45 s re-framings and by every one of the refinement's probes.
/// <see cref="GapPlanning.Normalize"/> keeps it as well, 1..65 followed by 2179 being strictly
/// ascending. The sequence therefore declared chapters 66 to 2178 missing, which cost some 25
/// minutes of a 90-minute run in Pass 2.5/3/3.5 sweeps over audio holding nothing, and left the
/// file tagged ".missing-marks" with too many numbers to name and so outside
/// <see cref="ABChapterize.Processing.MissingMarksTag.IsResumable"/>.
/// </para>
/// <para>
/// Deliberately not a reason to drop the mark. An unmendable number is also what a real chapter
/// looks like when Pass 2 missed the several chapters before it, and dropping that would trade a
/// lost chapter for saved time - the wrong way round here. Only a mark provably belonging to a
/// named announcement's own heading is removed; see
/// <see cref="ChapterDetector.DropNamedMarkEchoes"/>.
/// </para></param>
public readonly record struct DetectedChapter(
    int Number, double TimeSeconds, double Confidence = 1.0, bool NumberUnverified = false);

/// <summary>
/// A detected non-numbered mark - a prologue or epilogue announcement, or a <c>--custom</c> match
/// (see <see cref="ABChapterize.Language.NamedPhrase"/>). Kept in its own list rather than mixed into
/// <see cref="DetectionResult.Chapters"/> precisely because it carries no chapter number: the whole
/// gap machinery (<see cref="GapPlanning.FindGaps"/>, <see cref="GapPlanning.Normalize"/>,
/// <see cref="GapPlanning.ChapterProgress"/>) reasons in consecutive numbers, and a numberless entry
/// threaded through it could only ever be a special case to remember at every one of those sites.
/// The two lists meet again exactly once, where they belong together: in
/// <see cref="FileProcessor.BuildChapters"/>, which merges them by time into the chapter list
/// actually written.
/// </summary>
/// <param name="Kind">The phrase kind that produced it ("prologue", "epilogue", "custom 1", ...),
/// for log lines and as the key a mark is replaced or deduplicated under.</param>
/// <param name="Title">The title to write - <see cref="ABChapterize.Language.NamedPhrase.Title"/>
/// with any capturing-group references already expanded against the match this mark came from.</param>
/// <param name="TimeSeconds">Position of the mark in seconds.</param>
/// <param name="Confidence">Whisper's probability for the segment the phrase was found in (0-1);
/// 1.0 when unknown, e.g. for a mark carried over from a file's existing markings.</param>
/// <param name="PhraseTimeSeconds">Where the announcement itself was heard, as opposed to where the
/// mark ended up after placement. Kept only so a repeatable phrase can tell one announcement heard
/// twice by two overlapping probe windows from two genuine ones (see
/// <see cref="DetectionTuning.NamedMarkDedupeSeconds"/>) - the placed time is unusable for that,
/// since two detections of one announcement can be walked to quite different marks.</param>
/// <param name="Repeatable">Copied from the phrase that produced this mark, so the per-file
/// <c>--custom</c> cap can count what it caps without looking the phrase up again.</param>
public readonly record struct DetectedMark(
    string Kind, string Title, double TimeSeconds, double Confidence = 1.0,
    double PhraseTimeSeconds = 0, bool Repeatable = false);

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
/// <param name="NamedMarks">Detected prologue/epilogue and <c>--custom</c> marks in chronological
/// order; empty when none were found or every named phrase was switched off. Never part of
/// <paramref name="Chapters"/> - see <see cref="DetectedMark"/>.</param>
/// <param name="GapRemains">True when a chapter sequence gap could not be resolved; the file must
/// be left unchanged.</param>
/// <param name="MissingNumbers">The chapter numbers that could not be located (only when
/// <paramref name="GapRemains"/>).</param>
/// <param name="LowConfidenceNumbers">Chapter numbers whose Whisper probability fell below
/// <see cref="DetectionTuning.LowConfidenceThreshold"/> - worth a manual spot-check.</param>
/// <param name="Profile">The language profile actually used for this file - the resolved per-file
/// profile with <c>--lang auto</c>, else the run's fixed <see cref="CliOptions.DefaultProfile"/>.</param>
/// <param name="DetectedLanguage">Whisper's raw language guess with <c>--lang auto</c> - the sample
/// that settled the file, or the strongest of the samples that failed to; null when auto-detection
/// was not active, or found nothing decodable to probe.</param>
/// <param name="DetectedProbability">Whisper's probability for <paramref name="DetectedLanguage"/>;
/// 0 when that is null. May disagree with <see cref="Profile"/>'s language, which is the case worth
/// looking at: it means <see cref="LanguageResolver"/> overruled the strongest single reading, by
/// vote or by the English fallback.</param>
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
/// <param name="CustomMarkLimitHit">True when <see cref="DetectionTuning.MaxCustomMarksPerFile"/>
/// was reached and further <c>--custom</c> matches were therefore dropped. Surfaced in the file's
/// summary line rather than kept to the --verbose log: silently discarding marks the user asked for
/// would look exactly like a mapping that stopped matching halfway through the book.</param>
/// <param name="SequenceRestartSkips">How many announcements were heard, numbered and then dropped
/// because this file's chapter numbering restarts partway through (see
/// <see cref="RegionProber.SequenceRestartSkips"/>); 0 for the ordinary book. Surfaced in the
/// file's summary line for the same reason as <paramref name="CustomMarkLimitHit"/>: a book that
/// stops yielding chapters halfway through looks like a detection failure until someone reads the
/// --verbose log and finds every later announcement listed as heard and skipped.</param>
/// <param name="UnverifiedNumbers">The chapter numbers written with
/// <see cref="DetectedChapter.NumberUnverified"/> set - heard, marked, and never corroborated. Null
/// (rather than empty) for the ordinary file, so the common case allocates nothing. Surfaced in the
/// file's summary line for the same reason as <paramref name="CustomMarkLimitHit"/>, and it is the
/// more important of the two: the marks are written and the passes that would have chased the hole
/// beneath them were called off, so nothing else about the output looks wrong.</param>
public readonly record struct DetectionResult(
    IReadOnlyList<DetectedChapter> Chapters, IReadOnlyList<DetectedMark> NamedMarks,
    bool GapRemains, IReadOnlyList<int> MissingNumbers,
    IReadOnlyList<int> LowConfidenceNumbers, LanguageProfile Profile,
    string? DetectedLanguage, double DetectedProbability, DetectionStats Stats, bool EarlyAborted = false,
    int? BelowExpectedStartNumber = null, bool LeadInHasSpeech = true, bool CustomMarkLimitHit = false,
    int SequenceRestartSkips = 0, IReadOnlyList<int>? UnverifiedNumbers = null);

/// <summary>Outcome of checking one pre-existing chapter marking against the audio, in file order -
/// the raw material <see cref="GapPlanning.BuildGapRegions"/> groups into gap-scoped recovery
/// regions for <see cref="ChapterDetector.DetectGapsAsync"/>.</summary>
/// <param name="StartSeconds">The marking's own pre-existing timestamp.</param>
/// <param name="ExpectedNumber">The chapter number parsed from the marking's title, or null when
/// its title had none (e.g. a prelude/intro entry) - such a marking counts neither as confirmed
/// nor as a gap boundary and is skipped when regions are built.</param>
/// <param name="Confirmed">True when Whisper found the expected phrase near this marking.</param>
/// <param name="CorrectedStartSeconds">Where <c>--fix</c> worked out this marking really belongs,
/// or null when it was not asked to, could not confirm the marking, or found it already close
/// enough to be worth leaving alone (see
/// <see cref="DetectionTuning.VerifyFixMinShiftSeconds"/>).</param>
public readonly record struct VerifyMarkingOutcome(
    double StartSeconds, int? ExpectedNumber, bool Confirmed, double? CorrectedStartSeconds = null)
{
    /// <summary>Where this marking should sit: the correction <c>--fix</c> computed, or the
    /// position it already has.</summary>
    public double FixedStartSeconds => CorrectedStartSeconds ?? StartSeconds;
}

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
/// <param name="NamedMarks">Pre-existing markings recognized as this file's prologue/epilogue by
/// their title, carried over verbatim so a gap recovery that rewrites the whole marking set does
/// not silently drop them - they have no chapter number, so nothing else would ever notice they
/// were gone. Null (rather than empty) when nothing was recognized, so the common case allocates
/// nothing.</param>
public readonly record struct VerifyResult(
    bool Passed, int Checked, int Failed,
    IReadOnlyList<DetectedChapter> ConfirmedChapters, IReadOnlyList<VerifyMarkingOutcome> Markings,
    LanguageProfile Profile, string? DetectedLanguage, double DetectedProbability,
    IReadOnlyList<DetectedMark>? NamedMarks = null);
