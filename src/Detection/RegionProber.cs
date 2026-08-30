// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using ABChapterize.Cli;
using ABChapterize.Language;
using ABChapterize.Transcription;
using ABChapterize.Ui;
using ABChapterize.Vad;
using static ABChapterize.Language.NumberWordParser;
using static ABChapterize.Detection.DetectionFormatting;
using static ABChapterize.Detection.DetectionTuning;
using static ABChapterize.Detection.GapPlanning;
using static ABChapterize.Detection.JingleGeometry;
using static ABChapterize.Detection.PhraseMatching;
using static ABChapterize.Detection.TranscriptTime;

namespace ABChapterize.Detection;

/// <summary>
/// Everything a <see cref="RegionProber"/> borrows from the <see cref="ChapterDetector"/> that
/// created it: the tools it probes with, and the detector-owned operations that must stay the
/// detector's (recognition that tallies toward the file's Whisper statistics, the once-per-file
/// language resolution, the --verbose transcript log, the
/// <see cref="CliOptions.EffectiveMaxChapterNumber"/>-capped phrase matcher and the shared mark
/// placer).
/// Bundled so a prober's constructor is about the region it probes rather than about plumbing;
/// one instance serves every region of one file.
/// </summary>
/// <param name="Options">Validated command line options.</param>
/// <param name="Audio">Audio source the probe windows are decoded from.</param>
/// <param name="Vad">The voice-activity detector, or null when the VAD pre-pass did not run - which
/// switches probing between its VAD-aware and its silence-only geometry throughout.</param>
/// <param name="Log">Sink for --verbose log messages, or null when not verbose.</param>
/// <param name="Marks">The file's mark placer, shared with every other pass.</param>
/// <param name="TranscribeCounting">The detector's statistics-counting transcribe wrapper.</param>
/// <param name="LogTranscript">Logs a decoded window's transcript under --verbose.</param>
/// <param name="FindCappedPhraseReadings">The detector's --max-chapter-number-capped phrase matcher,
/// in its every-reading form: Probe is the one caller that can act on a wording another one
/// superseded (see <see cref="PhraseMatching.FindPhraseReadings"/>).</param>
/// <param name="SecondOpinion">Transcribes samples with the heavier <c>--upgrade-model</c> in a given
/// language, for the two Probe steps that are worth asking a better recognizer:
/// <see cref="SuspectNumberMender"/>'s re-read of an implausible chapter number, and
/// <see cref="RegionProber.RereadJingleSpeechAsync"/>'s second look at an announcement the first
/// decode's framing lost. Null when no upgrade model was chosen, and null for a Re-probe re-probe,
/// which already decodes every window through that model and so has no better opinion left to
/// ask.</param>
/// <param name="Denoiser">Asks the detector for the speech denoiser a garbled announcement may be
/// re-read through, which answers null when this file is not to be denoised - the run switched it
/// off, or the file sounds clean enough not to need it. Deliberately a request rather than the
/// object: deciding means measuring the file's fidelity, and a book whose announcements all come
/// through cleanly should never pay for that (see
/// <see cref="ChapterDetector.DenoiserForFileAsync"/>, which measures at most once per file). Null
/// only where no detector supplied one at all, as the recovery passes' own environments do.</param>
internal sealed record ProbeEnvironment(
    CliOptions Options,
    IAudioSource Audio,
    IVoiceActivityDetector? Vad,
    Action<string>? Log,
    MarkPlacer Marks,
    Func<float[], CancellationToken, ITranscriber?, Task<List<TranscriptSegment>>> TranscribeCounting,
    Action<string, List<TranscriptSegment>> LogTranscript,
    Func<List<TranscriptSegment>, LanguageProfile, int?, BareNumberReading,
        IEnumerable<IReadOnlyList<PhraseMatch>>> FindCappedPhraseReadings,
    Func<float[], string, CancellationToken, Task<List<TranscriptSegment>>>? SecondOpinion = null,
    Func<CancellationToken, Task<SpeechDenoiser?>>? Denoiser = null);

/// <summary>
/// Region-loop-invariant Probe inputs, gathered here instead of threading each field through
/// <see cref="RegionProber"/>'s constructor on its own. One instance per file, shared by every
/// region of it.
/// </summary>
/// <param name="File">Path of the audio file.</param>
/// <param name="Info">The file's probed media info (duration, size, decoder).</param>
/// <param name="Work">Progress tracker for the phase/byte accounting.</param>
/// <param name="BytesPerSecond">The file's average bytes per second of play time, used to
/// convert probed play time into the byte-based progress the bar counts in.</param>
/// <param name="AllSilences">Every silence Analyze retained, down to
/// <see cref="MinStoredSilenceSeconds"/> - seam snapping and mark anchoring, not candidates.</param>
/// <param name="Silences">The silences that become probe candidates: the subset at or above
/// --min-silence-length, or - for one of Re-probe's sub-floor sweeps - a single band below it
/// (see <see cref="RegionProber"/>'s sweep remarks).</param>
/// <param name="NonSpeechRegions">The VAD pre-pass's non-speech regions, empty when it did not run.</param>
/// <param name="SpeechSegments">The VAD pre-pass's speech segments, empty when it did not run.</param>
/// <param name="Jingles">The file's music stretches as Analyze measured them
/// (<see cref="JingleCensus"/>), empty when the VAD pre-pass did not run. The primary scan's
/// jingle candidates are built from these; the recovery passes ignore them.</param>
/// <param name="EarlyAbortSeconds">Play time that may be probed without a single find before
/// --early-abort gives up, or +infinity when the check does not apply.</param>
/// <param name="ExpectedStartChapter">--expected-start-chapter's abort threshold, or null when
/// the check does not apply.</param>
/// <param name="AdaptiveFloorSeconds">The shortest pause this run will entertain as a chapter break
/// - what the sub-floor sweep sweeps down to. <see cref="RegionProber.SandwichedSilences"/> holds a
/// promoted pause to the same bar, so the two passes agree on what a chapter break can sound like
/// rather than each carrying its own idea of it.</param>
/// <param name="Transcriber">The recognizer this region's probes decode with - the Probe
/// transcriber for Probe proper, the upgrade one for a Re-probe (see
/// <see cref="ChapterDetector.RunReprobeAsync"/>). Only the probe transcriptions follow it; mark
/// placement keeps refining on the probe model either way, exactly as Scan already does.</param>
/// <param name="SecondGuessNumbers">Whether an implausible chapter number is re-read before being
/// acted on (<see cref="SuspectNumberMender"/>). False for a Re-probe re-probe: its windows already go
/// through the heavier model, and its whole purpose is to re-read the numbers a gap is missing, so
/// questioning its readings against the very sequence it is repairing would be circular. Scan never
    /// probes and so never asks. Probe's own sequence-gap re-probe <em>does</em> ask, and used to be
    /// exempted alongside Re-probe on the reasoning that a wider window was already the remedy being
    /// applied - which a real book refuted: the wider window is what produced the mishearing. A
    /// re-probe is now the <em>best</em> place to question a number, since it alone knows both
/// ends of the hole it is filling (see <see cref="RegionProber.SequenceBounds"/>).</param>
/// <remarks>Notes: the mishearing that refuted "a wider window is already the remedy".
/// <include file='../../notes/Detection/RegionProber.xml' path='doc/member[@name="ProbeContext.SecondGuessNumbers"]/*' /></remarks>
internal readonly record struct ProbeContext(
    string File, MediaInfo Info, WorkTracker Work, double BytesPerSecond,
    List<Silence> AllSilences, List<Silence> Silences, List<NonSpeechRegion> NonSpeechRegions,
    List<SpeechSegment> SpeechSegments, List<Jingle> Jingles, double EarlyAbortSeconds,
    int? ExpectedStartChapter, ITranscriber Transcriber, double AdaptiveFloorSeconds = 0.8,
    bool SecondGuessNumbers = true);

/// <summary>One position Probe may probe: the region start, a silence's end, or the start of a
/// VAD jingle region. Exactly one of the two anchors is set, except for the region-start candidate,
/// which has neither.</summary>
/// <param name="Start">Absolute time the probe window starts at.</param>
/// <param name="Silence">The silence whose end this is, when a silence triggered the candidate.</param>
/// <param name="VadRegion">The VAD non-speech region this starts, when one triggered the candidate.</param>
/// <param name="ExpectAtSeconds">Where the announcement is expected, which is not always where the
/// window opens: a plain pause expects it at the silence's end, a jingle expects it where the music
/// stops. Null for a candidate with no expectation of its own (the region start, and every
/// candidate of a recovery pass), which reads as "at <paramref name="Start"/>" and reproduces the
/// behaviour that predates the classification.</param>
/// <param name="WindowSeconds">This candidate's own probe window length, or null to use the pass's
/// shared <see cref="RegionProber"/> window - which is what the gap re-probe and the recovery
/// passes do.</param>
/// <param name="Class">What made this a candidate - reported by <c>--verbose</c> for every mark, and
/// the input to <see cref="RegionProber.ThresholdSilenceFor"/>. It does not reach window sizing,
/// which <see cref="RegionProber"/>'s own classification flag governs: a recovery pass labels its
/// candidates truthfully while still probing them all with the pass's shared window.</param>
/// <param name="MusicFromSeconds">Where this candidate's music begins, when the announcement may be
/// spoken <em>over</em> it rather than after it; null otherwise. Not a window of its own - the
/// window still opens on the speech behind the music - but the licence for
/// <see cref="RegionProber.RereadJingleMusicAsync"/> to tile the music once that window comes back
/// empty. See <see cref="RegionProber.JingleCandidate"/> for why the two are separate looks.</param>
internal readonly record struct ProbeCandidate(
    double Start, Silence? Silence, NonSpeechRegion? VadRegion,
    double? ExpectAtSeconds = null, double? WindowSeconds = null,
    CandidateClass Class = CandidateClass.None, double? MusicFromSeconds = null)
{
    /// <summary>Where this candidate expects its announcement - its own start unless the
    /// classification put the expectation somewhere else.</summary>
    internal double ExpectAt => ExpectAtSeconds ?? Start;

    /// <summary>Whether this candidate is a jingle of either shape, which is the distinction the
    /// threshold rule turns on - a mark found at music says nothing about how long this book's
    /// chapter-break pauses run.</summary>
    internal bool IsJingle => Class is CandidateClass.Jingle or CandidateClass.JingleEmbedded;
}

/// <summary>
/// What made a place a Probe candidate. Three of the four shapes the primary scan reasons about;
/// the fourth - a pause with a jingle right behind it - never becomes a candidate for it at all, so
/// it has no value here (see <see cref="RegionProber.CandidatesIn"/>, where a recovery pass keeps
/// exactly that shape as an ordinary silence).
/// </summary>
internal enum CandidateClass
{
    /// <summary>Neither: the region's own start, which exists so a book announcing its first
    /// chapter in the opening seconds is not missed for want of a pause in front of it.</summary>
    None,

    /// <summary>A pause, with the announcement expected directly behind it.</summary>
    Silence,

    /// <summary>A jingle, with the announcement expected in the first speech behind the music.</summary>
    Jingle,

    /// <summary>A jingle the VAD pre-pass heard a bridged blip inside, so the announcement may be
    /// spoken over the music rather than after it and the window covers the jingle as well.</summary>
    JingleEmbedded,
}

/// <summary>
/// Which of a region's candidates one probing walk takes. <see cref="Everything"/> is the shape
/// probing has always had; the rest are the halves a two-part Probe splits it into - jingles then
/// pauses (see <see cref="JingleFirstScan"/>), or long pauses then the rest (see
/// <see cref="DescendingSilenceScan"/>). Both exist for the same reason: most of a book's probe
/// windows can only ever confirm what a handful of them already said, and reading the informative
/// ones first is what makes the others skippable.
/// <para>
/// Only the walk's own candidate list is filtered. A sequence-gap re-probe inside either half keeps
/// the union set it has always had, that being a second look at a stretch which has already failed
/// rather than a first look at a class of candidate.
/// </para>
/// </summary>
internal enum ProbeShape
{
    /// <summary>The region start, its jingles and its pauses, in chronological order.</summary>
    Everything,

    /// <summary>The region start and its jingles only - the jingle-first scan's first half.</summary>
    JinglesOnly,

    /// <summary>Its pauses only, the region start included in neither: the jingle-first scan's
    /// second half, run over the stretches the first half left unsettled.</summary>
    SilencesOnly,

    /// <summary>
    /// Every candidate, as <see cref="Everything"/> takes them - but read once through in descending
    /// pause length first, to find out where the chapters are, before the ordinary forward walk runs
    /// (see <see cref="DescendingSilenceScan"/>). The odd one out in this enum, which otherwise only
    /// says which candidates a walk takes: this member says in what order they are looked at.
    /// <para>
    /// That first read concludes nothing, and cannot. Every mechanism that decides whether an
    /// announcement is a chapter - the mender, the sequence check, the restart tracking, the
    /// refinement vote - reads the chapters below it, which a walk visiting 8:12:04 before 0:03:19
    /// does not have. So it only reads windows and notes what they say; the forward walk behind it
    /// draws every conclusion, out of the transcripts already in hand.
    /// </para>
    /// </summary>
    SilencesDescending,
}

/// <summary>
/// One probe window as planned, together with the candidate sequence it came from - enough for the
/// decode to work out how far it may read ahead (see <see cref="RegionProber.ExtendToPlannedSeam"/>)
/// without the intervening layers having to thread a candidate list through by hand.
/// </summary>
/// <param name="Candidates">The candidate sequence being walked - the region's own, or the skipped
/// subset a sequence-gap re-probe forms.</param>
/// <param name="Index">Index of this window's candidate within <paramref name="Candidates"/>.</param>
/// <param name="End">The window's planned end (see <see cref="RegionProber.WindowEndFor"/>).</param>
internal readonly record struct WindowPlan(
    IReadOnlyList<ProbeCandidate> Candidates, int Index, double End);

/// <summary>
/// One probe window after it has been decoded and before anything has been made of it - what
/// <see cref="RegionProber.ReadWindowAsync"/> hands <see cref="RegionProber.MarksFromWindowAsync"/>.
/// <para>
/// It exists so the two can happen at different times, which is the whole of what
/// <see cref="ProbeShape.SilencesDescending"/> needs: the transcript of a window read out of file
/// order is worth keeping, the verdict on it is not, because the verdict depends on chapters that
/// walk has not reached yet.
/// </para>
/// </summary>
/// <param name="Start">Absolute time the window starts at.</param>
/// <param name="WindowEnd">Its planned end (see <see cref="RegionProber.WindowEndFor"/>).</param>
/// <param name="Segments">The transcript in window-relative time, for phrase matching.</param>
/// <param name="TrimmedAbs">The same transcript in absolute file time, for the jingle anchor.</param>
/// <param name="MergeBoundarySegIndex">The cache/fresh boundary, if any; see
/// <see cref="RegionProber.AssembleWindowTranscriptAsync"/>.</param>
internal readonly record struct WindowRead(
    double Start, double WindowEnd, List<TranscriptSegment> Segments,
    List<TranscriptSegment> TrimmedAbs, int? MergeBoundarySegIndex);

/// <summary>One chapter mark a probe window produced.</summary>
/// <remarks>Notes: the misheard Roman numeral that invented a second part, and the two repairs the split then disarmed.
/// <include file='../../notes/Detection/RegionProber.xml' path='doc/member[@name="ProbeMark.NumberUnverified"]/*' /></remarks>
/// <param name="Number">The detected chapter number.</param>
/// <param name="ThresholdSilence">The silence this mark may teach --min-silence-length auto from,
/// or null where it must teach it nothing - see <see cref="RegionProber.ThresholdSilenceFor"/>.
/// Deliberately not "the silence the mark fell into": tightening is this field's only consumer, and
/// naming it after the measurement rather than the geometry keeps the two from being confused the
/// next time something wants to know where a mark landed.</param>
/// <param name="Confidence">Whisper's confidence for the segment the phrase was found in, which
/// decides whether this mark settles its whole overlapping window sequence.</param>
/// <param name="NumberUnverified">True when the sequence could not hold this number and re-reading
/// the audio produced nothing better - the mark is kept where it was found and nothing under it is
/// counted missing (see <see cref="DetectedChapter.NumberUnverified"/>, which this becomes).
/// <para>
/// Such a number is weak evidence about how far the book has got, so it opens no sequence gap behind
/// it and may not displace a floor something corroborated (<see cref="RegionProber.AdvanceLastNumber"/>
/// states the rule). The floor is what every later announcement is judged against, and an
/// uncorroborated number installed over a corroborated one reclassifies the real chapters after it
/// as below the sequence - which is precisely the shape a new part has, so a run of three of them
/// confirms a restart (<see cref="DetectionTuning.SequenceRestartRunLength"/>) and splits the file's
/// numbering in two.
/// </para>
/// <para>
/// That split is the expensive half, because it also disarms the two repairs that would otherwise
/// undo the mishearing: <see cref="GapPlanning.Normalize"/> and
/// <see cref="ChapterDetector.RepairSequenceOutliersAsync"/> both work one sequence at a time, and
/// inside a part of its own an uncorroborated number is not an outlier to drop but the ascending
/// last entry. Left off the floor it stays in one sequence with the chapters around it, where the
/// longest-increasing-subsequence filter drops it and the bracketing chapters usually name its real
/// number without consulting the audio at all.
/// </para>
/// <para>
/// Only the number is held back. The mark's position is as good as any other's, and
/// <see cref="RegionProber.TightenThreshold"/> still folds its silence into the auto threshold -
/// though its own "at least the second mark" test reads <see cref="RegionProber._lastNumber"/>, so
/// an uncorroborated <em>first</em> mark costs the next one's tightening. That errs loose, which is
/// the harmless direction: a threshold left wide probes more candidates, never fewer.
/// </para></param>
internal readonly record struct ProbeMark(
    int Number, Silence? ThresholdSilence, double Confidence, bool NumberUnverified = false);

/// <summary>
/// One announcement heard below the running sequence and held back while it is still unclear
/// whether it is an in-text mention or the first chapter of a new part. Everything
/// <see cref="RegionProber.AcceptMatchAsync"/> would have been given had it been accepted straight
/// away, so a run that turns out to be real can be placed later without re-probing the audio - the
/// transcript is already in hand, and re-deciding a mark from a window that has since scrolled past
/// would be a second, differently framed reading of the same announcement.
/// </summary>
/// <param name="Match">The phrase match, in window-relative time.</param>
/// <param name="Candidate">The candidate whose window this probe decoded.</param>
/// <param name="Start">Absolute start of that window.</param>
/// <param name="WindowEnd">Absolute planned end of that window.</param>
/// <param name="PhraseAbs">Absolute phrase start time.</param>
/// <param name="TranscriptAbs">That window's transcript in absolute file time.</param>
internal readonly record struct PendingRestart(
    PhraseMatch Match, ProbeCandidate Candidate, double Start, double WindowEnd, double PhraseAbs,
    List<TranscriptSegment> TranscriptAbs);

/// <summary>
/// Runs Probe candidate probing for a single <see cref="DetectionRegion"/>, appending every
/// accepted chapter mark to the caller's accumulator in place.
/// <para>
/// Constructed per region, which is what makes the invariant hold that every piece of per-region
/// probe state - the probe window size and its adaptive resizing, the --min-silence-length auto
/// threshold, the transcript-reuse cache and the "last accepted number" - starts fresh: a region is
/// probed as if it were its own small file, not a continuation of whatever an earlier region
/// happened to learn (see <see cref="DetectionRegion"/>'s remarks for why carrying it over would be
/// wrong in both directions). The one thing that does carry across regions is the language
/// resolution, handed in and read back out as a <see cref="LanguageState"/>.
/// </para>
/// </summary>
internal sealed class RegionProber
{
    private readonly ProbeEnvironment _env;
    private readonly ProbeContext _ctx;
    private readonly DetectionRegion _region;

    /// <summary>Re-reads a chapter number from the audio when the one in hand cannot be used: a
    /// number the sequence cannot continue with (gated by <see cref="ProbeContext.SecondGuessNumbers"/>
    /// at the call site) or no readable number at all. Region-scoped like the prober itself, since the
    /// windows it re-frames are clipped to the region's bounds.</summary>
    private readonly SuspectNumberMender _mender;

    /// <summary>How many unreadable-number re-reads this region has spent, against
    /// <see cref="MaxUnnumberedMendsPerRegion"/>.</summary>
    private int _unnumberedMends;

    /// <summary>Accumulator of confirmed chapters across all regions of the file; mutated in place
    /// as marks are accepted, so the sequence Scan later inspects is one seamless list regardless
    /// of which region contributed what.</summary>
    private readonly List<DetectedChapter> _found;

    /// <summary>The file's named marks and every rule about admitting another, shared across regions
    /// exactly as <see cref="_found"/> is - and shared with Scan, which is what keeps one
    /// announcement from being marked twice by two passes.</summary>
    private readonly NamedMarkLedger _named;

    /// <summary>Accumulator of the file's non-numbered marks - <see cref="NamedMarkLedger.Marks"/>
    /// under the name the reading code here uses. Holds at most one mark per non-repeatable
    /// <see cref="NamedPhrase.Kind"/> (prologue, epilogue) and any number of repeatable ones
    /// (<c>--custom</c>) - see <see cref="AcceptNamedMatchAsync"/> for both rules.</summary>
    private readonly List<DetectedMark> _namedFound;

    /// <summary>
    /// True while the sequence-gap recovery re-probes the stretch since the last mark. It makes that
    /// stretch's candidates a recovery pass's (see <see cref="Recovering"/>) and stops the decodes
    /// reading ahead (see <see cref="ExtendToPlannedSeam"/>), both for the same reason: what a
    /// second look is worth is its own framing, and anything shared with the first look throws that
    /// away.
    /// <para>
    /// Assigned only through <see cref="Reprobing"/>, so the progress bar cannot disagree with it.
    /// </para>
    /// </summary>
    private bool _reprobing;

    /// <summary>Backing field of <see cref="Reprobing"/>.</summary>
    private (double FromSeconds, double ToSeconds)? _reprobedGap;

    /// <summary>
    /// The gap a sequence-gap re-probe is walking, or null when none is - <see cref="_reprobing"/>
    /// together with everything the progress bar shows about it, which is why nothing assigns that
    /// field directly. A re-probe walks backwards through candidates the phase has already counted,
    /// so its percentage falls: without the label saying why (<see cref="WorkTracker.PhaseRevisiting"/>)
    /// and the gap marked out on the bar (<see cref="WorkTracker.RegionSpan"/>) that reads as the bar
    /// malfunctioning. Setting them together is what keeps them from drifting apart across the
    /// several places a re-probe can end - including the early return when a gap yields no
    /// candidates at all.
    /// </summary>
    private (double FromSeconds, double ToSeconds)? Reprobing
    {
        get => _reprobedGap;
        set
        {
            _reprobedGap = value;
            _reprobing = value is not null;
            _ctx.Work.PhaseRevisiting = _reprobing;
            // Back to the walk's own region on the way out, not to nothing: a re-probe inside a
            // recovery pass is a stretch within a stretch, and the outer one is still being worked.
            if (value is { } gap)
                _ctx.Work.RegionSpan = SpanOnBar(gap.FromSeconds, gap.ToSeconds);
            else
                ShowRegionOnBar();
        }
    }

    /// <summary>Where the last accepted mark's own candidate expected its announcement, or null
    /// before this region has one. The lower bound of a sequence-gap re-probe: the stretch worth a
    /// second look begins behind the last chapter found, not at the window that found it.</summary>
    private double? _lastMarkExpectAt;

    /// <summary>
    /// While a sequence-gap re-probe runs and can afford it, the shortest silence its candidates are
    /// built from - reaching under <see cref="ProbeContext.Silences"/>, which was filtered at
    /// --min-silence-length before Probe ever saw it. Null at every other time, and null for a
    /// re-probe that cannot afford the extra candidates.
    /// <para>
    /// The adaptive threshold can only ever restrict that pre-filtered list and can never reach
    /// under the demand the run opened at, however far it adapts, so a book whose breaks are shorter
    /// than the default assumes had no way to act on its own measurement until the sweeps ran.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the worked example of a book whose breaks were never candidates.
    /// <include file='../../notes/Detection/RegionProber.xml' path='doc/member[@name="_subFloorSeconds"]/*' /></remarks>
    private double? _subFloorSeconds;

    /// <summary>
    /// While the sequence-gap recovery re-probes, the chapter number that closes the gap - the mark
    /// that revealed it. Null at every other time.
    /// <para>
    /// It is the single most informative fact available anywhere in Probe and it used to be
    /// discarded: a re-probe of the hole between chapters 13 and 15 is searching for chapter 14 and
    /// nothing else, yet an announcement carrying quite another number could be accepted there
    /// unquestioned while <see cref="ReprobeGapCandidatesAsync"/> held a <c>missing</c> set
    /// containing exactly one number. Feeding it into
    /// <see cref="SequenceBounds"/> lets both the mender and the refinement vote hold a re-read to
    /// the hole it is filling. Scan has enforced the same rule on its own gap chunks all along.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the announcement accepted unquestioned while the gap named exactly one number.
    /// <include file='../../notes/Detection/RegionProber.xml' path='doc/member[@name="_gapAbove"]/*' /></remarks>
    private int? _gapAbove;

    /// <summary>The last chapter number accepted in this region, seeded from
    /// <see cref="DetectionRegion.LowerNumber"/> when a chapter is already confirmed to precede it
    /// and null for a from-file-start region. Holds the previous value (not yet the current
    /// window's) while a probe is in flight, which is exactly what a gap re-probe needs to accept
    /// the in-between numbers.</summary>
    private int? _lastNumber;

    /// <summary>The previous probe's decoded span, transcribed, in absolute file time; see
    /// <see cref="_cacheTo"/> for what it is for. Wider than that probe's own window whenever the
    /// decode read ahead (<see cref="ExtendToPlannedSeam"/>), which is the point of reading ahead:
    /// the surplus is what later windows are served from.</summary>
    private List<TranscriptSegment> _cacheSegmentsAbs = [];

    /// <summary>Start of the absolute span <see cref="_cacheSegmentsAbs"/> covers.</summary>
    private double _cacheFrom;

    /// <summary>
    /// End of that span - as far as the previous decode may be trusted, which is at or beyond the
    /// window that decode was run for but not necessarily as far as it read (see
    /// <see cref="CacheableEnd"/>). When the next candidate's window overlaps the span, the
    /// overlapping segments are reused verbatim instead of being re-run through Whisper: a window
    /// that fits inside it costs no Whisper call at all, and one that reaches past it decodes only
    /// the fresh tail beyond the planned seam. The span test (start inside
    /// [<see cref="_cacheFrom"/>, <see cref="_cacheTo"/>)) doubles as the seam-stitching check: it
    /// holds exactly when the previous decode really did run up to a seam this window's plan can
    /// pick up from, and when it does not (e.g. that window was skipped by the adaptive threshold)
    /// the probe falls back to decoding its full window from the candidate start - nothing is ever
    /// left covered by neither decode. Starts at negative infinity so the very first probe of a
    /// region never counts as an overlap and always does a full transcribe.
    /// </summary>
    private double _cacheTo = double.NegativeInfinity;

    /// <summary>
    /// What this region's marks have measured this book's chapter breaks down to, or null while
    /// nothing has qualified yet. Probing proceeds unthrottled until the second mark is found (its
    /// anchor silence is the first real inter-chapter break - the silence before the first mark is
    /// typically the intro/title silence, often longer, so it must not be used to tighten). From
    /// there each mark's anchor silence proposes <see cref="AdaptiveTightenFactor"/> times its own
    /// length, bounded below by <see cref="CliOptions.AdaptiveFloorSeconds"/>, and this is the
    /// running <em>minimum</em> of those proposals - the first one sets the figure, in either
    /// direction from the starting demand, and every later one can only lower it (see
    /// <see cref="AdaptiveTightenFactor"/> for why a raise is never safe).
    /// <para>
    /// This is <em>evidence about the narrator</em>, not the probing budget: every accepted mark
    /// feeds it, gap-recovered ones included, and it is read by the two gap-scoped mechanisms that
    /// act on a short break - <see cref="SubFloorForReprobe"/> and, through
    /// <see cref="AdaptedThresholdSeconds"/>, <see cref="ChapterDetector.SweepAdaptiveSubFloorAsync"/>.
    /// The primary scan's own gate is <see cref="_scanFloorSeconds"/>, which is deliberately fed
    /// less; see there for the measurement that separated the two.
    /// </para>
    /// </summary>
    private double? _adaptedThresholdSeconds;

    /// <summary>
    /// The same running minimum, minus one class of observation: a mark recovered by a sequence-gap
    /// re-probe whose break came out <em>at or under</em>
    /// <see cref="CliOptions.AdaptiveFloorSeconds"/>. This is what
    /// <see cref="AdoptProposedThreshold"/> copies into <see cref="_threshold"/>, and therefore the
    /// only thing that decides how much the forward scan probes.
    /// <para>
    /// The split exists because one field was serving two purposes that pull in opposite
    /// directions: a short break is <em>evidence</em> that this narrator's chapter breaks can be
    /// short, and separately a <em>licence to spend</em> probes on every short pause left in the
    /// book. Where the evidence arrives from a gap recovery at floor level, the second does not
    /// follow from the first - the break is below anything the forward scan probes for anyway, and
    /// the two gap-scoped mechanisms already reach down there on their own budget
    /// (<see cref="SubFloorForReprobe"/>, and the sub-floor sweep), while the forward scan has
    /// nothing telling it anything is missing.
    /// </para>
    /// <para>
    /// The floor test is what makes the rule safe rather than merely cheap, and it was arrived at by
    /// being wrong first: withholding <em>every</em> gap recovery from this gate broke
    /// <c>AutoMinSilence_AfterAGapRecovery_...</c>, which builds the case that needs the opposite -
    /// a chapter recovered at a 3 s break teaching a 2.25 s gate, so that a <em>later</em> chapter at
    /// 2.5 s is still probed, and that later chapter being last means no gap could ever bracket it.
    /// A bounded "lower by at most a factor" variant cannot serve both: that case needs the gate to
    /// follow 3.75 -> 2.25 (a factor of 0.6 or less) while the case below needs it not to follow
    /// 2.5 -> 0.8 (0.9 or more). Whether the proposal was clamped to the floor separates them
    /// cleanly, because it asks whether the break is one the forward scan could ever have probed.
    /// </para>
    /// <para>
    /// What the lowering does buy, it buys through the gap-scoped consumers, so this split keeps it.
    /// A book whose breaks really are short still throttles itself, because its <em>own forward
    /// scan</em> measures them.
    /// </para>
    /// </summary>
    /// <remarks>Notes: what the lowering cost on one book that gained nothing from it, and the three books whose short breaks it does pay for.
    /// <include file='../../notes/Detection/RegionProber.xml' path='doc/member[@name="_scanFloorSeconds"]/*' /></remarks>
    private double? _scanFloorSeconds;

    /// <summary>What the current sequence-gap re-probe withheld from <see cref="_scanFloorSeconds"/>,
    /// or null where it withheld nothing. Exists only so the log line at the end of
    /// <see cref="ReprobeGapCandidatesAsync"/> can state what actually happened: the evidence
    /// minimum moving is not the same event, since a gap recovery above the floor moves it
    /// <em>and</em> is applied to the scan gate, and announcing that as withheld would put a line in
    /// the debug log contradicting the "threshold lowered" one right beneath it.</summary>
    private double? _withheldFromScanSeconds;

    /// <summary>
    /// The descending scan's own stop rule (<see cref="GatherLongestPauseFirstAsync"/>): the pause
    /// length below which this walk stops reading, or null while nothing has been heard yet.
    /// <para>
    /// Kept apart from <see cref="_adaptedThresholdSeconds"/> although both are the same arithmetic
    /// over the same constant, because they are fed by different evidence at different times: that
    /// one learns from a <em>placed mark</em>, which this walk has none of by design, and this one
    /// from a window that merely held an announcement. Sharing a field would make the descending
    /// walk teach the file-order gate from readings nothing has vouched for yet.
    /// </para>
    /// </summary>
    private double? _gatherFloorSeconds;

    /// <summary>What the descending first look read, by candidate index, so the file-order walk
    /// behind it can make its marks from those transcripts instead of decoding the same windows
    /// again. Empty for every other shape.</summary>
    private readonly Dictionary<int, WindowRead> _gatheredReads = [];

    /// <summary>
    /// Stretches of the region the descending first look closed - see
    /// <see cref="DescendingSilenceScan.SettledSpans"/> - whose candidates the file-order walk passes
    /// over. Empty for every other shape, and empty
    /// during the descent itself, which is what stops it from skipping its own candidates.
    /// </summary>
    private List<(double From, double To)> _settledSpans = [];

    /// <summary>
    /// What this region's marks measured this book's chapter breaks to be, or null where nothing
    /// ever qualified. Read after <see cref="RunAsync"/> by
    /// <see cref="ChapterDetector.SweepAdaptiveSubFloorAsync"/>: below --min-silence-length it is
    /// the only evidence in the run that the starting demand was too strict for this narrator, and
    /// it is evidence the region paid for either way.
    /// </summary>
    internal double? AdaptedThresholdSeconds => _adaptedThresholdSeconds;

    /// <summary>The silence length a candidate must reach to be probed at all; the
    /// --min-silence-length the run opened at until <see cref="_scanFloorSeconds"/> starts
    /// moving it, up or down. Without --min-silence-length auto every candidate is probed
    /// unconditionally and this never changes, exactly as before that feature existed.</summary>
    private double _threshold;

    /// <summary>The file's language resolution, settled before Probe started and read-only from
    /// here - see <see cref="LanguageResolver"/>.</summary>
    private readonly LanguageState _language;

    /// <summary>Whether --early-abort fired in this region: enough play time probed without a
    /// single find that further probing is pointless.</summary>
    internal bool EarlyAborted { get; private set; }

    /// <summary>The first chapter number found, when it sat below --expected-start-chapter and
    /// detection was therefore abandoned for this file; null otherwise.</summary>
    internal int? BelowExpectedStartNumber { get; private set; }

    /// <summary>Whether <see cref="DetectionTuning.MaxCustomMarksPerFile"/> was reached and further
    /// --custom matches were therefore dropped. Read off the file's shared ledger, so it answers for
    /// the file rather than for this region - a cap reached in an earlier region, or by Scan, is
    /// still reached here.</summary>
    internal bool CustomLimitHit => _named.CustomLimitHit;

    /// <summary>
    /// Every announcement this region gave up on for sitting below the sequence, in the order they
    /// were heard - the raw material for <see cref="SequenceRestartSkips"/>. Numbers only: what
    /// distinguishes a book divided into parts from an in-text mention is the shape of the numbers
    /// over time, and nothing else about the rejected match is needed to see it.
    /// <para>
    /// A number only lands here once it is certain it will not become a chapter: while a restart is
    /// still being tracked its announcements sit in <see cref="_pendingRestart"/> instead, and
    /// <see cref="AbandonPendingRestart"/> tips them in here if the run breaks down.
    /// </para>
    /// </summary>
    private readonly List<int> _belowSequenceNumbers = [];

    /// <summary>Whether <see cref="NoteOutOfSequence"/> has already said in the log that this
    /// region is losing announcements below the sequence, so the observation is reported once
    /// rather than on every further rejection.</summary>
    private bool _restartReported;

    /// <summary>
    /// The 0-based chapter sequence this region is currently reading (see
    /// <see cref="DetectedChapter.Sequence"/>), seeded from <see cref="DetectionRegion.Sequence"/>
    /// and advanced by <see cref="CommitRestartAsync"/> when a new part is confirmed. Every number
    /// comparison this class makes is against this sequence and no other.
    /// </summary>
    private int _sequence;

    /// <summary>
    /// The announcements of a suspected new part, held back until there are enough of them to
    /// believe in (see <see cref="TrackRestartAsync"/>). Strictly consecutive and ascending by
    /// construction; empty for the whole of an ordinary book.
    /// </summary>
    private readonly List<PendingRestart> _pendingRestart = [];

    /// <summary>
    /// How many announcements this region heard, numbered, and then dropped for sitting below the
    /// sequence without ever adding up to a new part - zero unless that pattern is present at all
    /// (see <see cref="NoteOutOfSequence"/>). Nothing acts on it: it exists so the run can say what
    /// happened, since the alternative is a book that silently stops yielding chapters halfway
    /// through with every announcement after that point plainly logged as heard.
    /// </summary>
    internal int SequenceRestartSkips
        => LongestAscendingRun(_belowSequenceNumbers) >= SequenceRestartRunLength
            ? _belowSequenceNumbers.Count
            : 0;

    /// <summary>
    /// Whether this prober is one of Re-probe's sub-floor silence sweeps
    /// (<see cref="ChapterDetector.SweepSubFloorSilencesAsync"/>) rather than an ordinary region
    /// probe. A sweep's <see cref="ProbeContext.Silences"/> is a single band of silences that all
    /// sit <em>below</em> --min-silence-length, which changes three things and nothing else: every
    /// one of them is probed (the threshold that excluded them is exactly what the sweep is
    /// suspending, so consulting it again would skip the entire band), the adaptive threshold is
    /// left alone (a sweep's marks are recovered from silences the run's own demand had already
    /// ruled out, so they are the last thing that should be teaching it what a break looks like -
    /// and a tightening one would skip the rest of its own band), and neither the region-start
    /// candidate nor the VAD jingle regions are
    /// probed again - the ordinary attempt on this gap already covered both, and a sweep re-running
    /// them would pay a full window decode per band for audio it has read once.
    /// </summary>
    private readonly bool _sweeping;

    /// <summary>
    /// Whether this prober is a recovery pass - Re-probe, a sub-floor sweep, or (per probe, via
    /// <see cref="_reprobing"/>) Probe's own sequence-gap re-probe - rather than the primary scan.
    /// It changes two things about the candidates and nothing else:
    /// <list type="bullet">
    /// <item><description>the candidate <em>set</em> is the union - every silence and every jingle,
    /// with none of the primary scan's suppressions. <see cref="BuildCandidates"/> drops a silence
    /// falling inside a jingle's span on the census's word about where that jingle's announcement
    /// is, and a pass that exists <em>because</em> the primary scan failed here is the wrong place
    /// to keep taking that word;</description></item>
    /// <item><description>the window <em>geometry</em> is the classification's, trimmed - see
    /// <see cref="RecoveryLeadInTrimSeconds"/> for why a second look reads the same places in a
    /// different framing rather than in a wider one.</description></item>
    /// </list>
    /// <para>
    /// A --verify gap region is deliberately not one of these: nothing in this run has read that
    /// audio yet, so it gets the primary scan's own framing exactly as a fresh file would.
    /// </para>
    /// </summary>
    private readonly bool _recovery;

    /// <summary>Whether the candidates being built right now are a recovery pass's: this prober's
    /// own kind, or - inside Probe's sequence-gap re-probe - the stretch that re-probe rebuilds.
    /// Both want the same union set and the same trimmed framing, for the same reason.</summary>
    private bool Recovering => _recovery || _reprobing;

    /// <summary>
    /// The chapter numbers this run was sent to find, emptied as they are found, or null where the
    /// run has no such list - the primary scan, which is looking for whatever the book holds.
    /// Emptying it ends the walk (see <see cref="RunAsync"/>).
    /// <para>
    /// A recovery pass over a gap knows both its ends, so everything past the last chapter it was
    /// missing is a chapter's worth of audio with no announcement left in it - probed, refined and
    /// discarded as a duplicate. Probe's own sequence-gap re-probe has always stopped there; this
    /// is the same stop for the passes that drive a whole region, which knew the region's number
    /// <em>bounds</em> but not which numbers they were sent for.
    /// </para>
    /// <para>
    /// Accepted cost (2026-08-08): a named mark - prologue, epilogue, --custom - sitting in the tail
    /// is given up on. Re-probe probes on the upgrade model and can hear one the primary scan
    /// missed, but finding named marks that way is incidental, and Probe's re-probe has always
    /// accepted the same exposure.
    /// </para>
    /// </summary>
    private readonly HashSet<int>? _hunting;

    /// <summary>
    /// Which of the region's candidates this walk takes; see <see cref="ProbeShape"/>. Always
    /// <see cref="ProbeShape.Everything"/> for a recovery pass and for a sub-floor sweep, whose
    /// candidate sets are already chosen for them.
    /// </summary>
    private readonly ProbeShape _shape;

    /// <summary>Creates a prober for one region.</summary>
    /// <param name="env">The detector-owned tools and callbacks to probe with.</param>
    /// <param name="ctx">Region-loop-invariant Probe inputs.</param>
    /// <param name="region">The region to probe.</param>
    /// <param name="found">Accumulator of confirmed chapters across all regions.</param>
    /// <param name="named">The file's named-mark ledger - its prologue/epilogue/<c>--custom</c>
    /// marks and the rules for admitting another, shared with Scan.</param>
    /// <param name="language">The file's settled language resolution.</param>
    /// <param name="sweepingSubFloorSilences">Whether this is a Re-probe sub-floor sweep; see
    /// <see cref="_sweeping"/>.</param>
    /// <param name="recovery">Whether this is a recovery pass rather than the primary scan; see
    /// <see cref="_recovery"/>.</param>
    /// <param name="hunting">The chapter numbers this run was sent to find, or null for a scan with
    /// no such list; see <see cref="_hunting"/>.</param>
    /// <param name="shape">Which of the region's candidates to walk; see <see cref="ProbeShape"/>.
    /// Defaults to all of them, which is every caller but a two-part Probe's halves.</param>
    internal RegionProber(ProbeEnvironment env, ProbeContext ctx, DetectionRegion region,
        List<DetectedChapter> found, NamedMarkLedger named, LanguageState language,
        bool sweepingSubFloorSilences = false,
        bool recovery = false, IEnumerable<int>? hunting = null,
        ProbeShape shape = ProbeShape.Everything)
    {
        _env = env;
        _ctx = ctx;
        _region = region;
        _mender = new SuspectNumberMender(env, ctx, region);
        _found = found;
        _named = named;
        _namedFound = named.Marks;
        _language = language;
        _sweeping = sweepingSubFloorSilences;
        _recovery = recovery || sweepingSubFloorSilences;
        _hunting = hunting is null ? null : [.. hunting];
        _shape = shape;
        _sequence = region.Sequence;
        _lastNumber = region.LowerNumber > 0 ? region.LowerNumber : null;
        _cacheFrom = region.FromSeconds;
        _threshold = env.Options.MinSilenceSeconds;
    }

    /// <summary>
    /// Probes every candidate of the region, stopping early on an --early-abort or
    /// --expected-start-chapter abort. Reports its outcome through <see cref="EarlyAborted"/> and
    /// <see cref="BelowExpectedStartNumber"/>; the marks themselves land in the accumulator this
    /// prober was constructed with.
    /// <para>
    /// In file order, except under <see cref="ProbeShape.SilencesDescending"/>, which reads the
    /// windows longest pause first and only then reads what they say - in file order, so everything
    /// downstream of a transcript is unaffected by the order they arrived in.
    /// </para>
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    internal async Task RunAsync(CancellationToken ct)
    {
        var candidates = BuildCandidates();
        // Cleared however the walk ends: an abandoned region would otherwise leave the next phase's
        // bar highlighting a stretch nothing is working on. The same belt-and-braces the re-probe
        // marker and the skim's own bar shape have.
        try
        {
            ShowRegionOnBar();
            if (_shape == ProbeShape.SilencesDescending)
                await GatherThenResolveAsync(candidates, ct);
            else
                await WalkInFileOrderAsync(candidates, ct);
        }
        finally
        {
            _ctx.Work.RegionSpan = null;
        }
        // A restart still being tracked when the region runs out never earned its chapters, so its
        // announcements are booked as lost here rather than quietly forgotten.
        AbandonPendingRestart();
    }

    /// <summary>
    /// Highlights this region's own stretch of the progress bar, unless the region is the whole
    /// file - the primary forward walk, where there is no piece of the book to point at (see
    /// <see cref="WorkTracker.RegionSpan"/>).
    /// </summary>
    /// <remarks>
    /// Called again after every step that resets the bar underneath a running walk: a re-probe
    /// finishing (which borrowed the highlight for its gap) and the skim handing over to the
    /// file-order walk (which begins a phase, and with it clears the highlight).
    /// </remarks>
    private void ShowRegionOnBar()
        => _ctx.Work.RegionSpan =
            _region.FromSeconds <= 0 && _region.ToSeconds >= _ctx.Info.DurationSeconds
                ? null
                : SpanOnBar(_region.FromSeconds, _region.ToSeconds);

    /// <summary>Where a stretch of this region's audio falls on the bar.</summary>
    /// <param name="fromSeconds">Absolute start of the stretch.</param>
    /// <param name="toSeconds">Absolute end of the stretch.</param>
    private (long FromBytes, long ToBytes) SpanOnBar(double fromSeconds, double toSeconds)
        => WorkTracker.Span(fromSeconds, toSeconds, _ctx.BytesPerSecond);

    /// <summary>
    /// The walk probing has always had: every candidate in file order, each one's verdict settled
    /// before the next one is looked at.
    /// </summary>
    /// <param name="candidates">The region's candidates, in file order.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task WalkInFileOrderAsync(List<ProbeCandidate> candidates, CancellationToken ct)
    {
        for (var ci = 0; ci < candidates.Count; ci++)
        {
            var candidate = candidates[ci];
            ReportProgress(candidate.Start);

            if (ShouldEarlyAbort(candidate))
                break;
            if (ShouldSkipCandidate(candidate))
                continue;

            var foundNoneYet = _found.Count == 0;
            var plan = new WindowPlan(candidates, ci, WindowEndFor(candidates, ci));
            // A window a descending first look already read is not read again - it is the same
            // candidate planned by the same arithmetic, so the transcript is the one this decode
            // would produce. The re-reads a window yielding nothing is entitled to still run, which
            // is why this reuses the transcript rather than skipping the candidate.
            var probeMarks = _gatheredReads.TryGetValue(ci, out var read)
                ? await MarksFromWindowAsync(candidate, read, ct)
                : await ProbeAsync(candidate, plan, ct);

            if (await ApplyWindowOutcomeAsync(candidate, probeMarks, foundNoneYet, ct))
            {
                if (_hunting is { Count: 0 } && ci + 1 < candidates.Count)
                    _env.Log?.Invoke($"stretch complete, nothing left missing - stopped after " +
                                     $"{ci + 1} of {candidates.Count} candidate(s)");
                break;
            }
            ci = SkipSettledWindows(candidates, ci, plan.End, probeMarks);
        }
    }

    /// <summary>
    /// Everything one probed window changes about the region's running state, and whether the walk
    /// is to stop after it. Shared by both walks, which differ in the order they arrive here and in
    /// nothing else.
    /// </summary>
    /// <param name="candidate">The candidate whose window this was.</param>
    /// <param name="probeMarks">The marks it produced, in window order.</param>
    /// <param name="foundNoneYet">Whether the region still had no chapter when this window was
    /// probed - the one case --expected-start-chapter's abort asks about.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when the walk is to stop.</returns>
    private async Task<bool> ApplyWindowOutcomeAsync(
        ProbeCandidate candidate, List<ProbeMark> probeMarks, bool foundNoneYet, CancellationToken ct)
    {
        if (foundNoneYet && IsBelowExpectedStart())
            return true;

        await ApplyProbeMarksAsync(probeMarks, candidate.ExpectAt, ct);
        // After the marks are applied, so a gap among them is still bounded below by the mark
        // before this window rather than by this window's own.
        if (probeMarks.Count > 0)
            _lastMarkExpectAt = candidate.ExpectAt;
        return _hunting is { Count: 0 };
    }

    /// <summary>
    /// The descending scan's walk: read this region's longest pauses first to find out where its
    /// chapters are, then walk the region in file order as probing always has - reusing what was
    /// already read, and passing over the stretches those readings closed.
    /// <para>
    /// <b>One walk, not two halves</b>, and that is the whole design. The obvious shape - resolve
    /// the gathered windows, then hand the rest to a second walk over the unsettled stretches, the
    /// way <see cref="JingleFirstScan"/> does - was built first and gives up something the file-order
    /// walk owns: a part restart is recognized by seeing a chapter number drop and then climb again,
    /// which no walk confined to one stretch can see. The Forever War's part 1 chapter 15 sits in
    /// front of part 2's chapter 1, and split into stretches it is found, refused and dropped. Here
    /// the descent only decides <em>which candidates are worth reading</em>; every conclusion is
    /// still drawn by one forward walk over the whole region, so the restart tracking, the sequence
    /// gap re-probe and <c>--early-abort</c> all work exactly as they always have.
    /// </para>
    /// </summary>
    /// <param name="candidates">The region's candidates, in file order.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>Notes: the clip that proved the two-half shape loses a part restart.
    /// <include file='../../notes/Detection/RegionProber.xml' path='doc/member[@name="GatherThenResolveAsync"]/*' /></remarks>
    private async Task GatherThenResolveAsync(List<ProbeCandidate> candidates, CancellationToken ct)
    {
        List<(int Index, WindowRead Read, List<int> Numbers)> gathered;
        // The skim's own bar shape is cleared however it ends, an abandoned one otherwise leaving
        // the next phase counting locations it is not exploring; the phase below clears it too, and
        // both is the same belt-and-braces the re-probe marker has.
        try
        {
            _ctx.Work.LocationsExplored = 0;
            gathered = await GatherLongestPauseFirstAsync(candidates, ct);
        }
        finally
        {
            _ctx.Work.LocationsExplored = null;
        }

        foreach (var (index, read, _) in gathered)
            _gatheredReads[index] = read;
        _settledSpans = DescendingSilenceScan.SettledSpans(
            candidates, [.. gathered.Select(g => (g.Index, g.Numbers))]);
        if (_settledSpans.Count > 0)
            _env.Log?.Invoke(
                $"SD-probe: {_settledSpans.Count} stretch(es) closed by consecutive chapter " +
                "numbers - their pauses are passed over");

        // The skim really is a phase of its own and ends here, so the walk gets the bar back: the
        // same file, the same stretches, and a label for what it is. Begun from here rather than by
        // the caller because only this method knows when the skim finished - it may stop at any of
        // its three termination conditions or run the list out. The enclosing phase's stretches are
        // handed straight back rather than recomputed, since the walk covers exactly what the skim
        // did; beginning a phase clears the current-region highlight, hence the second call.
        _ctx.Work.BeginPhase(
            WalkPhaseName(candidates), _ctx.Work.PhaseTotalBytes, _ctx.Work.PhaseSpans);
        ShowRegionOnBar();
        await WalkInFileOrderAsync(candidates, ct);
    }

    /// <summary>
    /// What to call the file-order walk on the bar: <see cref="PhaseNames.Probe"/> where it has
    /// music to read as well as pauses, <see cref="PhaseNames.ChronologicalProbe"/> where all it
    /// has is pauses.
    /// </summary>
    /// <param name="candidates">The walk's own candidate list.</param>
    /// <remarks>
    /// Decided once, when the phase begins, and not revisited as the walk consumes its jingles: the
    /// label describes what the pass set out to read, and a name that changed part way through
    /// would read as one phase having ended and another begun.
    /// </remarks>
    private static string WalkPhaseName(List<ProbeCandidate> candidates)
        => candidates.Any(c => c.IsJingle) ? PhaseNames.Probe : PhaseNames.ChronologicalProbe;


    /// <summary>
    /// Reads windows in descending pause length until the pauses stop being long enough to be this
    /// book's chapter breaks, and hands back what was read.
    /// <para>
    /// <b>Three termination conditions, none of which can lose a chapter</b> - whatever this walk
    /// leaves unsettled is walked in file order afterwards, exactly as the jingle-first shape's
    /// second half walks what the music left open (see <see cref="DescendingSilenceScan"/> for the
    /// argument, which is the same one):
    /// </para>
    /// <list type="number">
    /// <item>the gather floor, which is the adaptive threshold's own rule read from the other end:
    /// once a candidate is shorter than <see cref="DetectionTuning.AdaptiveTightenFactor"/> of the
    /// shortest pause that has yielded an announcement here, every candidate left is shorter still.
    /// Measured against the sixteen-book corpus, it is also where the descent stops paying: at
    /// 0.6 rather than 0.75 the corpus probes far more windows and recovers two chapters the walk
    /// that follows recovers anyway;</item>
    /// <item>the dry-start budget, for the walk that has found nothing at all and so has no floor to
    /// stop at - see <see cref="DryStartBudget"/>, which is <c>--early-abort</c>'s own budget;</item>
    /// <item>a <c>--chapter-count</c> that is already accounted for, which is the same "nothing left
    /// to look for" exit a gap hunt takes.</item>
    /// </list>
    /// </summary>
    /// <param name="candidates">The region's candidates, in file order.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>Notes: where the corpus's chapter-bearing pauses rank, and what each stop rule costs.
    /// <include file='../../notes/Detection/RegionProber.xml' path='doc/member[@name="GatherLongestPauseFirstAsync"]/*' /></remarks>
    private async Task<List<(int Index, WindowRead Read, List<int> Numbers)>> GatherLongestPauseFirstAsync(
        List<ProbeCandidate> candidates, CancellationToken ct)
    {
        var gathered = new List<(int Index, WindowRead Read, List<int> Numbers)>();
        var numbers = new HashSet<int>();
        var budget = DryStartBudget(candidates);
        foreach (var ci in DescendingSilenceScan.LongestPauseFirst(candidates))
        {
            var candidate = candidates[ci];
            if (ShouldSkipCandidate(candidate))
                continue;
            if (DescendingSilenceScan.PauseSecondsOf(candidate) is { } pause &&
                _gatherFloorSeconds is { } floor &&
                pause < floor)
            {
                _env.Log?.Invoke(
                    $"SD-probe: down to {pause:0.##} s pauses, below the {floor:0.##} s this book " +
                    $"announces its chapters after - read {gathered.Count} of {candidates.Count} " +
                    "candidate(s)");
                break;
            }
            if (_gatherFloorSeconds is null && gathered.Count >= budget)
            {
                _env.Log?.Invoke(
                    $"SD-probe: nothing announced at the {budget} longest candidate(s) - reading " +
                    "the rest of this file in order instead");
                break;
            }

            var plan = new WindowPlan(candidates, ci, WindowEndFor(candidates, ci));
            var read = await ReadWindowAsync(candidate, plan, ct);
            var (held, heardNumbers) = AnnouncementIn(read);
            gathered.Add((ci, read, heardNumbers));
            _ctx.Work.LocationsExplored = gathered.Count;
            if (!held)
                continue;
            // A jingle teaches nothing about how long this book's pauses run, which is the same rule
            // ThresholdSilenceFor applies to a finished mark and for the same reason.
            if (DescendingSilenceScan.PauseSecondsOf(candidate) is { } yielding)
                ProposeGatherFloor(yielding);
            numbers.UnionWith(heardNumbers);
            if (SequenceAccountedFor(numbers))
            {
                _env.Log?.Invoke(
                    $"SD-probe: all {_env.Options.ChapterCount} chapter(s) accounted for - read " +
                    $"{gathered.Count} of {candidates.Count} candidate(s)");
                break;
            }
        }
        return gathered;
    }

    /// <summary>
    /// How many of the longest candidates may be read before a walk that has heard nothing at all
    /// gives up: exactly the ones <c>--early-abort</c> would have let the file-order walk read
    /// before it gave up, since with nothing found no threshold is ever learned and nothing is
    /// skipped. So this is not an approximation of that budget, it is that budget, spent on the
    /// longest pauses in the file rather than on whichever ones happen to come first.
    /// <para>
    /// <c>--early-abort 0</c> reaches here as an infinite <see cref="ProbeContext.EarlyAbortSeconds"/>
    /// and therefore as the whole candidate list, which is what disabling it should mean.
    /// </para>
    /// </summary>
    /// <param name="candidates">The region's candidates, in file order.</param>
    /// <remarks>Notes: the book this was verified against, probe for probe.
    /// <include file='../../notes/Detection/RegionProber.xml' path='doc/member[@name="DryStartBudget"]/*' /></remarks>
    private int DryStartBudget(List<ProbeCandidate> candidates)
        => candidates.Count(c => c.Start < _ctx.EarlyAbortSeconds);

    /// <summary>
    /// Lowers the gather floor to this pause, the walk having just heard an announcement after one.
    /// A running minimum like <see cref="ProposeThreshold"/>'s, and floored the same way: a book
    /// whose breaks reach <see cref="CliOptions.AdaptiveFloorSeconds"/> is one whose pauses say
    /// nothing, and the walk should read them all rather than stop on the strength of them.
    /// </summary>
    /// <param name="pauseSeconds">The length of the pause that yielded the announcement.</param>
    private void ProposeGatherFloor(double pauseSeconds)
    {
        var proposed = Math.Max(
            _env.Options.AdaptiveFloorSeconds, AdaptiveTightenFactor * pauseSeconds);
        _gatherFloorSeconds = Math.Min(_gatherFloorSeconds ?? proposed, proposed);
    }

    /// <summary>
    /// Whether the numbers heard so far already cover a declared <c>--chapter-count</c> with no hole
    /// in them, in which case there is nothing left for this walk to look for. Answered on the
    /// numbers as read rather than on accepted chapters, this walk having accepted none yet; a
    /// wrongly read number can only make this <em>less</em> likely to be true, since a hole it opens
    /// keeps the run from closing.
    /// </summary>
    /// <param name="numbers">Every chapter number heard by the walk so far.</param>
    private bool SequenceAccountedFor(HashSet<int> numbers)
    {
        if (_env.Options.ChapterCount is not { } count)
            return false;
        var first = _env.Options.ExpectedStartChapter ?? 1;
        return Enumerable.Range(first, count).All(numbers.Contains);
    }

    /// <summary>
    /// What one read window says, without deciding anything about it: the chapter numbers it holds,
    /// and whether it holds an announcement of any kind at all.
    /// <para>
    /// A prologue, a <c>--custom</c> mapping and a chapter phrase whose number came out unreadable
    /// all count, none of them being a chapter and all of them being this book separating one
    /// section from the next - which is the only question the gather floor asks. The adaptive
    /// threshold takes the same view of a named mark, and for the same reason.
    /// </para>
    /// <para>
    /// The <c>--max-chapter-number</c> cap is applied here as
    /// <see cref="ProbeEnvironment.FindCappedPhraseReadings"/> would, but without its log line: a
    /// year read out of a front-matter timetable must not lower the floor, and this window is going
    /// to be read again in file order, where that line belongs.
    /// </para>
    /// </summary>
    /// <param name="read">The window transcript to look at.</param>
    private (bool Held, List<int> Numbers) AnnouncementIn(WindowRead read)
    {
        var numbers = new List<int>();
        foreach (var readings in FindPhraseReadings(
                     read.Segments, _language.Profile, read.MergeBoundarySegIndex,
                     BareNumberReadingFor(WideBareNumberReading)))
            foreach (var match in readings)
            {
                if (_env.Options.EffectiveMaxChapterNumber is { } cap && match.Number > cap)
                    continue;
                numbers.Add(match.Number);
                break;
            }
        var held = numbers.Count > 0 ||
                   FindNamedMatches(read.Segments, _language.Profile).Any() ||
                   FindUnnumberedAnnouncements(read.Segments, _language.Profile).Any();
        return (held, numbers);
    }



    /// <summary>Reports how far probing has got as the byte-based progress the bar counts in.
    /// Probe costs vary wildly - full window decode vs. reused overlap vs. skipped candidate - so a
    /// fixed per-probe budget would drift far off; position is honest about <em>where</em> the pass
    /// is, at the price of nonlinear (and, during gap re-probes, briefly backwards) movement.</summary>
    /// <param name="positionSeconds">Absolute position in the file that probing has reached.</param>
    private void ReportProgress(double positionSeconds)
        => _ctx.Work.SetPhaseProgress(WorkTracker.Position(positionSeconds, _ctx.BytesPerSecond));

    /// <summary>
    /// The probe candidates for this region: its own start (mirroring the whole-file case's
    /// start-of-file candidate), plus every silence and - when the VAD pre-pass ran - every VAD
    /// non-speech region whose own candidate position falls inside
    /// [<see cref="DetectionRegion.FromSeconds"/>, <see cref="DetectionRegion.ToSeconds"/>), in
    /// chronological order - bar the region's last second, which a silence candidate is held clear
    /// of: its window would be clamped to under a second of audio, too little to hold an
    /// announcement and enough to cost a Whisper pass finding that out. A window can never decode
    /// past the region end regardless (see
    /// <see cref="GapPlanning.PlanWindowEnd"/>'s duration clamp), so the region boundary alone is
    /// enough containment - no extra check is needed here for that. VAD regions only qualify when
    /// they start at their own jingle start (i.e. nothing else leads them) and are long enough to be
    /// worth observing yet short enough to still be this book's jingle.
    /// <para>
    /// A sub-floor sweep takes the silences and nothing else - see <see cref="_sweeping"/>. So does
    /// the jingle-first scan's second half, and its first half takes the jingles and the region
    /// start - see <see cref="ProbeShape"/>. The filter sits here rather than in
    /// <see cref="CandidatesIn"/> on purpose: this method builds the walk's own list, while that one
    /// also builds a sequence-gap re-probe's, which must keep its union set whatever shape the walk
    /// around it has.
    /// </para>
    /// </summary>
    private List<ProbeCandidate> BuildCandidates()
    {
        var candidates = _sweeping || _shape == ProbeShape.SilencesOnly
            ? []
            : new List<ProbeCandidate> { RegionStartCandidate() };
        candidates.AddRange(CandidatesIn(_region.FromSeconds, _region.ToSeconds).Where(Walks));
        // A jingle candidate opens after its own music, so the list is no longer in silence order.
        return candidates.OrderBy(c => c.Start).ToList();
    }

    /// <summary>Whether this walk takes the given candidate at all; see <see cref="ProbeShape"/>.</summary>
    /// <param name="candidate">One candidate of the region.</param>
    private bool Walks(ProbeCandidate candidate) => _shape switch
    {
        ProbeShape.JinglesOnly => candidate.IsJingle,
        ProbeShape.SilencesOnly => !candidate.IsJingle,
        _ => true,
    };

    /// <summary>
    /// The candidates of one stretch of the region, where what made a place a candidate also decides
    /// where its window opens and where in it the announcement is expected. Four shapes, and the
    /// classification is the whole point - one window shape for all of them is what forced every
    /// probe to be as long as this book's longest jingle:
    /// <list type="bullet">
    /// <item>a silence with a jingle right behind it is <em>not</em> a candidate for the primary
    /// scan: the jingle below covers the same transition and knows where its speech resumes, so
    /// probing the silence would spend a window on the music. A recovery pass keeps it, on the
    /// grounds that the census's word about that transition is exactly what has just failed;</item>
    /// <item>a silence with no jingle behind it expects the announcement immediately after it -
    /// which is what a chapter break without music sounds like;</item>
    /// <item>a jingle expects it where speech resumes (<see cref="Jingle.AnnouncementSeconds"/>),
    /// with the window opening a <see cref="JingleLeadInSeconds"/> run-up earlier inside the music;</item>
    /// <item>a jingle whose announcement may be spoken over the music instead gets that music read
    /// afterwards, in tiles - see <see cref="JingleCandidate"/>.</item>
    /// </list>
    /// <para>
    /// Both lead-ins are clamped to non-speech - into the silence, into the music - and never reach
    /// back into the previous narration: <see cref="SilenceLeadInSeconds"/> says why that matters.
    /// A recovery pass trims both ends of every window; see <see cref="RecoveryLeadInTrimSeconds"/>.
    /// </para>
    /// </summary>
    /// <param name="fromSeconds">Start of the stretch; a candidate's own anchor must reach it.</param>
    /// <param name="toSeconds">End of the stretch, bar its last second - a window from there would be
    /// clamped to under a second of audio, too little to hold an announcement and enough to cost a
    /// Whisper pass finding that out. A window can never decode past the region end regardless (see
    /// <see cref="GapPlanning.PlanWindowEnd"/>'s duration clamp), so nothing else is needed for
    /// containment.</param>
    private List<ProbeCandidate> CandidatesIn(double fromSeconds, double toSeconds)
    {
        var candidates = new List<ProbeCandidate>();
        var jingles = _sweeping ? [] : JinglesInRegion(fromSeconds, toSeconds);

        foreach (var jingle in jingles)
            candidates.Add(JingleCandidate(jingle));
        var leadIn = Recovering ? SilenceLeadInSeconds - RecoveryLeadInTrimSeconds : SilenceLeadInSeconds;
        foreach (var silence in SilenceSource)
        {
            if (silence.EndSeconds < fromSeconds || silence.EndSeconds >= toSeconds - 1)
                continue;
            // A silence that ends anywhere between a jingle's first note and the speech behind it -
            // its lead-in hush, a dip in the middle of the music, the hush after it - is part of
            // that transition rather than one of its own. Everything a window from here would hear
            // is the jingle's, and the jingle candidate hears it from a better place.
            //
            // Measured from the music and not from the jingle candidate's own window, deliberately.
            // Scoping it to that window was tried (2026-08-12) as a leash on the unbounded length a
            // VAD region may now have, and it costs more than it saves: the candidate opens only a
            // JingleLeadInSeconds run-up before the announcement, so every jingle's lead-in hush
            // becomes a candidate again - a probe per jingle, corpus-wide, that can only ever hear
            // music. The unbounded case it was guarding is benign anyway: a stretch long enough to
            // worry about is one silencedetect found dips in and VAD heard no speech in, i.e. music,
            // and no chapter is announced inside music that nothing is announcing.
            if (!Recovering && jingles.Any(j =>
                    silence.EndSeconds >= j.StartSeconds - JinglePhraseMatchToleranceSeconds &&
                    silence.EndSeconds < j.AnnouncementSeconds))
                continue;
            var start = Math.Max(silence.StartSeconds, silence.EndSeconds - leadIn);
            candidates.Add(new ProbeCandidate(
                start, silence, null,
                ExpectAtSeconds: silence.EndSeconds,
                WindowSeconds: silence.EndSeconds - start + ReachSeconds,
                Class: CandidateClass.Silence));
        }
        foreach (var silence in PromotableSilences(candidates, fromSeconds, toSeconds))
        {
            var start = Math.Max(silence.StartSeconds, silence.EndSeconds - leadIn);
            candidates.Add(new ProbeCandidate(
                start, silence, null,
                ExpectAtSeconds: silence.EndSeconds,
                WindowSeconds: silence.EndSeconds - start + ReachSeconds,
                Class: CandidateClass.Silence));
        }
        return candidates;
    }

    /// <summary>
    /// The sub-threshold pauses that look like the front of an announcement rather than a break in
    /// narration: long enough to be a chapter pause at all, and followed within
    /// <see cref="SandwichedAnnouncementSeconds"/> by a pause that <em>is</em> a candidate. Speech
    /// bracketed by two pauses that short is the shape of an announcement, and without this the only
    /// window covering it opens on the second pause - after the announcement has been spoken. See
    /// <see cref="SandwichedAnnouncementSeconds"/> for the two corpus chapters that named the rule
    /// and for what the bound costs.
    /// <para>
    /// Only for a pass that is still probing at the user's own threshold. A gap re-probe or a
    /// sub-floor sweep has already opened the floor (<see cref="SilenceSource"/>), so every silence
    /// this would promote is a candidate there already, and promoting again would merely duplicate
    /// windows the sweep is budgeting for.
    /// </para>
    /// </summary>
    /// <param name="candidates">The candidates built so far, whose starts say which pauses already
    /// have a window; the promoted ones are appended by the caller.</param>
    /// <param name="fromSeconds">Start of the stretch, as <see cref="CandidatesIn"/> bounds it.</param>
    /// <param name="toSeconds">End of the stretch, as <see cref="CandidatesIn"/> bounds it.</param>
    private IEnumerable<Silence> PromotableSilences(
        List<ProbeCandidate> candidates, double fromSeconds, double toSeconds)
    {
        if (_sweeping || _subFloorSeconds is not null)
            return [];

        var probed = candidates
            .Where(c => c.Class == CandidateClass.Silence && c.Silence is not null)
            .Select(c => c.Silence!.Value)
            .ToList();
        return SandwichedSilences(
            _ctx.AllSilences, probed, _ctx.AdaptiveFloorSeconds, fromSeconds, toSeconds);
    }

    /// <summary>
    /// The rule itself, over explicit lists: every stored pause that is long enough to be a chapter
    /// break and has a probed pause beginning within <see cref="SandwichedAnnouncementSeconds"/> of
    /// its end. Internal and static for unit testing, exactly as
    /// <see cref="GapPlanning.PlanWindowEnd"/> is.
    /// </summary>
    /// <param name="allSilences">Every silence Analyze kept, in time order.</param>
    /// <param name="probed">The pauses that already have a window of their own.</param>
    /// <param name="floorSeconds">Shortest pause that may be promoted; see
    /// <see cref="ProbeContext.AdaptiveFloorSeconds"/>.</param>
    /// <param name="fromSeconds">Start of the stretch being planned.</param>
    /// <param name="toSeconds">End of the stretch being planned.</param>
    internal static IEnumerable<Silence> SandwichedSilences(
        IReadOnlyList<Silence> allSilences, IReadOnlyCollection<Silence> probed,
        double floorSeconds, double fromSeconds, double toSeconds)
    {
        var alreadyProbed = probed.Select(s => s.StartSeconds).ToHashSet();
        var pauseStarts = probed.Select(s => s.StartSeconds).OrderBy(t => t).ToList();
        if (pauseStarts.Count == 0)
            yield break;

        foreach (var silence in allSilences)
        {
            if (silence.EndSeconds < fromSeconds || silence.EndSeconds >= toSeconds - 1)
                continue;
            if (alreadyProbed.Contains(silence.StartSeconds))
                continue;
            // Long enough to be a chapter break at all - the bar the sub-floor sweep uses - or
            // "sandwiched" would promote every breath drawn shortly before a real pause.
            if (silence.EndSeconds - silence.StartSeconds < floorSeconds)
                continue;

            var index = pauseStarts.BinarySearch(silence.EndSeconds);
            var behind = index >= 0 ? index : ~index;
            if (behind >= pauseStarts.Count)
                continue;
            // The speech in between runs from this pause's end to where the next one begins.
            if (pauseStarts[behind] - silence.EndSeconds <= SandwichedAnnouncementSeconds)
                yield return silence;
        }
    }

    /// <summary>
    /// Which silences <see cref="CandidatesIn"/> draws on: the context's own list - Analyze's, cut at
    /// --min-silence-length, or a sweep's band - unless a gap re-probe has opened the floor
    /// (<see cref="_subFloorSeconds"/>), in which case every silence Analyze kept that is at least
    /// that long.
    /// </summary>
    private IEnumerable<Silence> SilenceSource
        => _subFloorSeconds is { } floor
            ? _ctx.AllSilences.Where(s => s.EndSeconds - s.StartSeconds >= floor)
            : _ctx.Silences;

    /// <summary>How far past its expected announcement a window of this pass reaches; see
    /// <see cref="RecoveryReachTrimSeconds"/> for why a recovery pass reaches less far.</summary>
    private double ReachSeconds
        => Recovering ? ExpectedAnnouncementSeconds - RecoveryReachTrimSeconds : ExpectedAnnouncementSeconds;

    /// <summary>
    /// What one probe of a recovery pass covers - a trimmed lead-in plus a trimmed reach, i.e. the
    /// widest window a pass of silence candidates can ask for. The recovery sweeps budget themselves
    /// in Whisper decode windows and need a per-probe cost to do it with; naming it here keeps that
    /// cost tied to the geometry it is actually measuring rather than to a constant of its own.
    /// </summary>
    internal static double RecoveryProbeSeconds
        => SilenceLeadInSeconds - RecoveryLeadInTrimSeconds +
           ExpectedAnnouncementSeconds - RecoveryReachTrimSeconds;

    /// <summary>The region's own start, which is a candidate in its own right: a book whose first
    /// chapter is announced in the opening seconds has no silence in front of it to trigger one.</summary>
    private ProbeCandidate RegionStartCandidate()
        => new(_region.FromSeconds, null, null,
            ExpectAtSeconds: _region.FromSeconds, WindowSeconds: ReachSeconds);

    /// <summary>
    /// This region's jingles, one per announcement. The census splits a jingle its music dipped
    /// below the noise floor in the middle into two entries sharing that announcement; probing both
    /// would decode the same transition twice, so the earliest entry stands for the whole run - it
    /// also carries the earliest start, which is what the music tiling needs.
    /// <para>
    /// A recovery pass picks its jingles by where their <em>announcement</em> falls rather than
    /// where their music starts: the stretch it is re-reading is bounded by two chapter marks, so a
    /// transition belongs to it when the announcement does, however far back the music reaches.
    /// The primary scan keeps selecting by the music, where the bound is a whole region and the two
    /// answers differ only for a jingle straddling its edge.
    /// </para>
    /// </summary>
    /// <param name="fromSeconds">Start of the stretch the jingles must belong to.</param>
    /// <param name="toSeconds">End of that stretch.</param>
    private List<Jingle> JinglesInRegion(double fromSeconds, double toSeconds)
        => _env.Vad == null
            ? []
            : _ctx.Jingles
                .Select(j => (Jingle: j, At: Recovering ? j.AnnouncementSeconds : j.StartSeconds))
                .Where(j => j.At >= fromSeconds && j.At < toSeconds)
                .Select(j => j.Jingle)
                .GroupBy(j => j.AnnouncementSeconds)
                .Select(g => g.OrderBy(j => j.StartSeconds).First())
                .OrderBy(j => j.StartSeconds)
                .ToList();

    /// <summary>
    /// Turns one jingle into its candidate: a window on the speech behind the music, opening a
    /// <see cref="JingleLeadInSeconds"/> run-up inside it.
    /// <para>
    /// A bridged VAD blip inside the music - the one evidence available that the announcement is
    /// spoken <em>over</em> the jingle rather than after it - does not change that window. It used
    /// to: such a candidate started at the jingle's first note and ran
    /// <see cref="ExpectedAnnouncementSeconds"/> past the announcement, a width bounded by nothing
    /// but the music's own length, and most marks found that way came from windows wider than
    /// <see cref="WhisperChunkSeconds"/> - the width that constant exists to warn about, shipped by
    /// the very classification that was meant to retire it.
    /// </para>
    /// <para>
    /// So the two possibilities become two looks instead of one window: the speech behind the music
    /// first, and the music itself only when that comes back empty (see
    /// <see cref="RereadJingleMusicAsync"/>, which tiles it at single-pass width). The order is the
    /// corpus's - marks sit after the music far more often than inside it - so the second look is
    /// rarely paid for at all, and where it is, it reads the music in framings the recognizer can
    /// actually hear a word in.
    /// </para>
    /// <para>
    /// A recovery pass reads the music of <em>every</em> jingle that way, blip or no blip: the blip
    /// is the census's evidence, and a pass running because the census's transition failed to yield
    /// anything is the wrong place to keep asking it. It still costs nothing where the speech window
    /// answers, that being the order the two looks run in.
    /// </para>
    /// </summary>
    /// <remarks>Notes: how wide the old blip-driven windows really got, and the corpus split between marks after the music and inside it.
    /// <include file='../../notes/Detection/RegionProber.xml' path='doc/member[@name="JingleCandidate"]/*' /></remarks>
    /// <param name="jingle">The jingle to probe around.</param>
    private ProbeCandidate JingleCandidate(Jingle jingle)
    {
        var spans = Recovering || jingle.BridgedBlips > 0;
        var leadIn = Recovering ? JingleLeadInSeconds - RecoveryLeadInTrimSeconds : JingleLeadInSeconds;
        var start = Math.Max(jingle.StartSeconds, jingle.AnnouncementSeconds - leadIn);
        // The merged VAD region this jingle sits in, when there is one: ResolveAnnouncementMark
        // prefers the candidate's own region over any other when the phrase falls inside it, and a
        // jingle candidate without it would lose that preference to a neighbouring region.
        var region = _ctx.NonSpeechRegions
            .Where(r => r.StartSeconds <= jingle.StartSeconds + JinglePhraseMatchToleranceSeconds &&
                        r.EndSeconds >= jingle.EndSeconds - JinglePhraseMatchToleranceSeconds)
            .Cast<NonSpeechRegion?>()
            .FirstOrDefault();
        return new ProbeCandidate(start, null, region,
            ExpectAtSeconds: jingle.AnnouncementSeconds,
            WindowSeconds: jingle.AnnouncementSeconds - start + ReachSeconds,
            Class: CandidateClass.Jingle,
            // Only where the music actually reaches back past the run-up: a jingle shorter than
            // that is already inside this window, and tiling it would re-read what was just read.
            MusicFromSeconds: spans && jingle.StartSeconds < start ? jingle.StartSeconds : null);
    }

    /// <summary>
    /// Where the window of <paramref name="index"/> ends. Computed on the fly, right before that
    /// window's probe runs, rather than pre-planned in bulk: an overlapping neighbor gets the shared
    /// border snapped to a silence mid-point, which moves this window's decode end itself - possibly
    /// past its natural end - rather than merely choosing where to stop reusing cache after the
    /// fact. Deciding per window also keeps every end consistent with the candidate list actually
    /// being walked, with no stale bulk plan to drift from what earlier probes decoded.
    /// </summary>
    /// <param name="list">The candidate sequence being walked - the region's own, or the one a
    /// sequence-gap re-probe rebuilds for the stretch since the last mark.</param>
    /// <param name="index">Index within <paramref name="list"/>.</param>
    private double WindowEndFor(IReadOnlyList<ProbeCandidate> list, int index)
        => PlanWindowEnd(list[index].Start,
            index + 1 < list.Count ? list[index + 1].Start : null,
            // Every candidate carries the width its own class asks for, recovery ones included, so
            // the bare reach is only ever the fallback for one built without a class at all.
            list[index].WindowSeconds ?? ReachSeconds,
            _region.ToSeconds, _ctx.AllSilences, _ctx.NonSpeechRegions, _env.Vad != null);

    /// <summary>
    /// How far a decode that must cover up to <c>plan.End</c> is allowed to read on past it: the
    /// furthest end an upcoming candidate's window is already planned to have that still fits inside
    /// the <see cref="WhisperChunkSeconds"/> passes this decode is paying for anyway. The surplus
    /// goes into the overlap cache, so those upcoming windows are served from it outright instead of
    /// each buying a Whisper pass of its own.
    /// <para>
    /// The whole saving rests on the encoder's fixed input: it runs on a 30 s mel whatever it is
    /// handed, so a 6 s tail decode costs exactly what a 30 s one costs. A run of overlapping
    /// candidates therefore pays a full pass per candidate for a stretch that a chunk-sized decode
    /// covers in one.
    /// </para>
    /// <para>
    /// Why the reach is a <em>planned window end</em> and not simply the chunk boundary, although
    /// stopping at the boundary would save a further 124 passes: every planned end is snapped to a
    /// silence or non-speech mid-point (see <see cref="GapPlanning.PlanWindowEnd"/>), and stopping
    /// anywhere else would leave the cache ending mid-speech. A later window reusing it wholesale
    /// never re-reads that audio, so an announcement straddling the cut would be lost on both sides
    /// of it - the one failure this must not buy speed with.
    /// </para>
    /// <para>
    /// Reading ahead is all this does. How much of the surplus is then <em>trusted</em> is
    /// <see cref="CacheableEnd"/>'s question, and the answer is not "all of it": a longer decode is
    /// a worse-framed one, and where the recognizer fell silent the audio has to be read again
    /// rather than reused.
    /// </para>
    /// <para>
    /// Suppressed while a sequence gap re-probes (<see cref="_reprobing"/>). A re-probe's value is a
    /// differently framed second look at audio that has already been read once and yielded nothing;
    /// filling the cache ahead of it would hand some of its candidates the first look's transcript
    /// instead, which is exactly the trade that had to be reverted when gap re-probes last reused a
    /// transcript rather than re-decoding.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the encoder passes read-ahead saved over a whole book, and what stopping at the chunk boundary would have saved instead.
    /// <include file='../../notes/Detection/RegionProber.xml' path='doc/member[@name="ExtendToPlannedSeam"]/*' /></remarks>
    /// <param name="plan">The window being decoded, and the candidates that follow it.</param>
    /// <param name="decodeStart">Absolute time the decode itself starts at - the window start for a
    /// full decode, the overlap split point for a tail decode.</param>
    /// <returns>The absolute time the decode is to run to, never earlier than <c>plan.End</c>.</returns>
    private double ExtendToPlannedSeam(WindowPlan plan, double decodeStart)
    {
        if (_reprobing)
            return plan.End;

        var chunks = Math.Max(1, (int)Math.Ceiling((plan.End - decodeStart) / WhisperChunkSeconds));
        var budget = decodeStart + chunks * WhisperChunkSeconds;
        var reach = plan.End;
        for (var i = plan.Index + 1;
             i < plan.Candidates.Count && plan.Candidates[i].Start < budget;
             i++)
        {
            var end = WindowEndFor(plan.Candidates, i);
            if (end <= budget && end > reach)
                reach = end;
        }
        return reach;
    }

    /// <summary>
    /// --early-abort: once Probe has probed this far into the file's play time without a single
    /// chapter found, give up rather than transcribe the rest of a book that plainly will not yield
    /// any (wrong --chapter-phrase, wrong --lang, or one that announces chapters differently).
    /// </summary>
    /// <param name="candidate">The candidate about to be probed.</param>
    private bool ShouldEarlyAbort(ProbeCandidate candidate)
    {
        // "Nothing found" means no numbered chapter - a lone prologue is not enough to call the
        // file productive, and BuildDetectionResult would discard it anyway. With
        // --ignore-chapter-numbers the chapters themselves land in the named list, so that is what
        // counts instead; otherwise every such run would abort at the threshold regardless.
        var foundSomething = _found.Count > 0 ||
                             (_env.Options.IgnoreChapterNumbers && _namedFound.Count > 0);
        // A jingle-only walk has read the music and nothing else, so "nothing found in the first
        // hour of play time" is not the evidence this check reasons about - the pauses of that hour
        // have not been looked at yet. The question is asked again by the walk that does look at
        // them, whose head stretch spans the whole region precisely when nothing was found.
        // The descending shape needs no such exemption: its own walk is in file order, and the
        // reading it does ahead of that has a budget cut from this very setting (DryStartBudget).
        if (_shape == ProbeShape.JinglesOnly || candidate.Start < _ctx.EarlyAbortSeconds || foundSomething)
            return false;
        EarlyAborted = true;
        _env.Log?.Invoke($"early-abort: no chapter found within the first " +
                         $"{_env.Options.EarlyAbortMinutes:0.#} minute(s) of play time " +
                         $"(stopped probing at {FormatTimestamp(candidate.Start)})");
        return true;
    }

    /// <summary>
    /// Whether this candidate is passed over without a probe: its silence falls below the
    /// --min-silence-length auto threshold. The candidate is remembered either way, so a sequence
    /// gap can put the whole stretch back in question.
    /// <para>
    /// A sub-floor sweep skips nothing: its candidate list <em>is</em> the set it means to probe
    /// (see <see cref="_sweeping"/>).
    /// </para>
    /// </summary>
    /// <param name="candidate">The candidate to judge.</param>
    private bool ShouldSkipCandidate(ProbeCandidate candidate)
    {
        if (_sweeping)
            return false;
        // Not while a sequence gap re-probes: that pass exists to look again at a stretch the walk
        // has already been through, and a stretch the descent closed is exactly the kind a gap can
        // reopen - a misread number is how a real chapter ends up inside one.
        if (!_reprobing && _settledSpans.Any(s => candidate.Start > s.From && candidate.Start < s.To))
            return true;
        return _env.Options.AutoMinSilence && candidate.Silence is { } silence &&
               silence.EndSeconds - silence.StartSeconds < _threshold;
    }

    /// <summary>
    /// --expected-start-chapter's abort half, consulted right after the probe that found the very
    /// first chapter of a fresh run - whether it added one match or several, the one case the
    /// option cares about. A later, lower in-text mention never reaches here at all, already
    /// rejected inside the probe as not topping the last accepted number.
    /// </summary>
    /// <returns>True when detection is to be abandoned for this file, in which case the finds so
    /// far have been discarded.</returns>
    private bool IsBelowExpectedStart()
    {
        if (_ctx.ExpectedStartChapter is not { } expected || _found.Count == 0 || _found[0].Number >= expected)
            return false;
        BelowExpectedStartNumber = _found[0].Number;
        _env.Log?.Invoke($"expected-start-chapter: first chapter found is {_found[0].Number}, " +
                         $"below the expected start of {expected} - aborting detection for this file");
        _found.Clear();
        return true;
    }

    /// <summary>
    /// Probes a single window and appends every chapter mark found in it to the accumulator. Since
    /// segment timestamps plus the full stored silence list let every detection be pinpointed
    /// independently of the triggering candidate, one window can yield several marks (e.g. a wide
    /// jingle window covering two transitions) - there is no one-chapter-per-window early return.
    /// </summary>
    /// <param name="candidate">The candidate whose window to probe. Its start stays the semantic
    /// anchor for the phrase-timing rule and for progress, both of which are relative to the
    /// triggering silence rather than to whatever seam the window plan chose.</param>
    /// <param name="plan">The window to probe: its <em>planned</em> end (see
    /// <see cref="WindowEndFor"/>), possibly snapped away from the natural end its own class asked
    /// for, and the candidates that follow it, which only the decode's
    /// read-ahead looks at (see <see cref="ExtendToPlannedSeam"/>). Everything below this scans the
    /// planned window and nothing beyond it, whatever the decode read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The accepted marks in window order.</returns>
    private async Task<List<ProbeMark>> ProbeAsync(
        ProbeCandidate candidate, WindowPlan plan, CancellationToken ct)
        => await MarksFromWindowAsync(candidate, await ReadWindowAsync(candidate, plan, ct), ct);

    /// <summary>
    /// The decoding half of a probe: everything up to and including a window transcript, with
    /// nothing yet asked of what it says.
    /// <para>
    /// Split from <see cref="MarksFromWindowAsync"/> so the descending scan's first half can read a
    /// window now and decide what it means later - see <see cref="ProbeShape.SilencesDescending"/>
    /// for why a walk that visits candidates out of file order cannot do both at once.
    /// </para>
    /// </summary>
    /// <param name="candidate">The candidate whose window to read.</param>
    /// <param name="plan">The window to read; see <see cref="WindowEndFor"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<WindowRead> ReadWindowAsync(
        ProbeCandidate candidate, WindowPlan plan, CancellationToken ct)
    {
        var start = candidate.Start;
        ct.ThrowIfCancellationRequested();
        // Position-based Probe progress (see DetectCoreAsync's BeginPhase); reported here rather
        // than only in the candidate loop so gap re-probes show their (backwards) position too.
        ReportProgress(start);

        var (windowSegmentsAbs, mergeBoundarySegIndex) =
            await AssembleWindowTranscriptAsync(start, plan, ct);

        // Correct segment starts Whisper timestamped from a leading silence/jingle before shifting
        // to window-relative time (the cache keeps the raw absolute timings its reuse math needs).
        // The absolute trimmed transcript stays around for ResolveJingleAnchor's narration-aware
        // jingle edge adjustment.
        var trimmedAbs = TrimLeadingNonSpeech(
            windowSegmentsAbs, _ctx.AllSilences, _ctx.NonSpeechRegions, _env.Vad != null);
        return new WindowRead(
            start, plan.End, ShiftSegments(trimmedAbs, -start), trimmedAbs, mergeBoundarySegIndex);
    }

    /// <summary>
    /// The deciding half of a probe: what one already-read window yields, including the re-reads a
    /// window that yielded nothing is entitled to. See <see cref="ReadWindowAsync"/> for why the two
    /// halves are separable at all.
    /// </summary>
    /// <param name="candidate">The candidate whose window this is.</param>
    /// <param name="read">What <see cref="ReadWindowAsync"/> made of it.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<List<ProbeMark>> MarksFromWindowAsync(
        ProbeCandidate candidate, WindowRead read, CancellationToken ct)
    {
        var (start, windowEnd, segments, trimmedAbs, mergeBoundarySegIndex) = read;
        var namedBefore = _namedFound.Count;
        var marks = await ScanWindowForMarksAsync(
            candidate, start, windowEnd, segments, trimmedAbs, mergeBoundarySegIndex, ct);

        // A window that yielded nothing at all - no chapter, no prologue, no --custom mark - is the
        // only one worth another look; one that produced a mark has already told this candidate's
        // story. The two re-reads answer different questions and neither subsumes the other: one
        // asks about speech VAD heard and the transcript does not account for, the other about an
        // announcement the candidate expected and a window too wide to hear it in.
        if (marks.Count == 0 && _namedFound.Count == namedBefore)
            marks = await RereadJingleSpeechAsync(candidate, start, windowEnd, trimmedAbs, ct);
        if (marks.Count == 0 && _namedFound.Count == namedBefore)
            marks = await RereadInOnePassAsync(candidate, start, windowEnd, ct);
        if (marks.Count == 0 && _namedFound.Count == namedBefore)
            marks = await RereadJingleMusicAsync(candidate, ct);
        if (marks.Count == 0 && _namedFound.Count == namedBefore)
            marks = await RereadDenoisedAsync(candidate, start, windowEnd, segments, ct);
        if (marks.Count == 0)
            marks = await RecoverUnnumberedAnnouncementsAsync(
                candidate, start, windowEnd, segments, trimmedAbs, ct);
        return marks;
    }

    /// <summary>
    /// Last look at a window that heard a chapter <em>number</em> but not the word beside it, on
    /// audio the file-level check thought worth denoising: the same window, put through the speech
    /// denoiser and read again.
    /// <para>
    /// The shape it answers to is narrow on purpose. A window that came back empty is somebody
    /// else's case (the two jingle re-reads above); this one is the opposite - the recognizer heard
    /// the announcement, wrote its number, and dropped the word that would have made it one.
    /// Denoising is what fixes that, measured rather than hoped, and it is cheaper than the
    /// alternative that works equally well, a re-read on a larger model.
    /// </para>
    /// <para>
    /// Like the re-reads above it can only ever <em>add</em> a mark: it runs only where the window
    /// produced none, keeps the window's own start and end so nothing is reframed, and hands its
    /// transcript to the ordinary scan, so every acceptance rule applies to a denoised mark exactly
    /// as to a first-pass one. Denoising does not help a window whose announcement simply is not in
    /// it - that failure is geometry, and enhancement cannot recover audio a window does not contain
    /// (measured on The Philosopher's Stone chapter 11: 3 of 12 framings raw, 4 of 12 denoised).
    /// </para>
    /// </summary>
    /// <remarks>Notes: the transcript that lost a chapter to a swallowed chapter word, and what denoising recovers across window framings.
    /// <include file='../../notes/Detection/RegionProber.xml' path='doc/member[@name="RereadDenoisedAsync"]/*' /></remarks>
    /// <param name="candidate">The candidate whose window produced no mark.</param>
    /// <param name="start">Absolute start of that window, kept unchanged.</param>
    /// <param name="windowEnd">Absolute planned end of that window, kept unchanged.</param>
    /// <param name="segments">Its transcript in window-relative time, which the trigger reads.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The marks the re-read produced, or an empty list when it did not run or found
    /// nothing.</returns>
    private async Task<List<ProbeMark>> RereadDenoisedAsync(
        ProbeCandidate candidate, double start, double windowEnd,
        List<TranscriptSegment> segments, CancellationToken ct)
    {
        // The trigger first, the permission second: asking costs a fidelity measurement, and only a
        // window that failed this particular way has any use for the answer. --no-denoise is checked
        // here as well as at the permission itself, so a run that switched the rescue off does not
        // narrate windows it was never going to re-read.
        if (!_env.Options.Denoise || _env.Denoiser is not { } ask ||
            !HeardANumberWithoutItsWord(segments))
            return [];

        // Logged before the permission is sought, not after it is granted: a reader asking why a
        // chapter went missing needs to see that this window was recognized as the shape at all,
        // and the file-level refusal that may follow says nothing about which window prompted it.
        _env.Log?.Invoke(
            $"window at {FormatTimestamp(start)} heard a chapter number but not the word");
        if (await ask(ct) is not { } denoiser)
            return [];

        var samples = await _env.Audio.DecodePcmAsync(
            _ctx.File, start, windowEnd - start, _ctx.Info.InputDecoder, ct);
        var fresh = await _env.TranscribeCounting(denoiser.Denoise(samples), ct, _ctx.Transcriber);
        _env.LogTranscript(
            $"denoised re-read {windowEnd - start:0.0}s@{FormatTimestamp(start)}", fresh);

        var freshAbs = TrimLeadingNonSpeech(
            ShiftSegments(fresh, start), _ctx.AllSilences, _ctx.NonSpeechRegions, _env.Vad != null);
        return await ScanWindowForMarksAsync(
            candidate, start, windowEnd, ShiftSegments(freshAbs, -start), freshAbs, null, ct);
    }

    /// <summary>
    /// Whether this window's transcript holds a chapter number standing on its own where the phrase
    /// expected a word beside it - the one shape denoising is known to rescue.
    /// <para>
    /// The strict <see cref="BareNumberReading.SpokenAloneAtSegmentStart"/> reading, the same one
    /// Probe's forward scan trusts, because this runs on every empty window of every file that
    /// passed the fidelity check and a looser reading would spend a decode on ordinary prose - in
    /// Italian "un", "una" and "uno" all parse as 1.
    /// </para>
    /// <para>
    /// A phrase with no expression wording at all is excluded: where the number <em>is</em> the whole
    /// announcement there is no word to have been dropped, and a number this window found without
    /// producing a mark was refused by the sequence or the isolation guard rather than misheard.
    /// </para>
    /// </summary>
    /// <param name="segments">The window's transcript, in window-relative time.</param>
    private bool HeardANumberWithoutItsWord(List<TranscriptSegment> segments)
        => _language.Profile.ChapterPattern.HasRegexAlternative &&
           segments.Any(s => NumberWordParser.FindBareNumberAnnouncement(
               s.Text, _language.Profile.Language,
               BareNumberReading.SpokenAloneAtSegmentStart) is not null);

    /// <summary>
    /// Second, short look at a probe window that heard no announcement while VAD insists there was
    /// speech inside its jingle - the one shape in which "nothing here" is contradicted by evidence
    /// the tool already holds. By this codebase's own working assumption the only speech inside a
    /// jingle <em>is</em> the announcement (see
    /// <see cref="JingleGeometry.RefineDefaultMark"/>'s remarks), so a VAD speech blip there that no
    /// transcript segment has any words for means the recognizer lost it rather than that it is not
    /// there.
    /// <para>
    /// Losing it is a framing artifact before it is anything else: window width is what does it (see
    /// <see cref="WhisperChunkSeconds"/> for Gruelfin.m4b's prologue, the case on record), so the
    /// re-read asks for the same announcement inside a window narrow enough to end just past the
    /// blip. Confined to windows the re-read really does narrow, since re-reading the same span
    /// would be the same framing and could only produce the same answer - and, where the run has a
    /// <c>--upgrade-model</c> upgrade, put through that recognizer rather than the probing one, for the
    /// reason the decode itself documents.
    /// </para>
    /// <para>
    /// The re-read window ends a phrase margin past the blip - far enough for the number after
    /// "Kapitel" and no further, because everything beyond it is narration competing for the decode -
    /// and reaches back to the candidate, or one <see cref="JingleRereadWindowSeconds"/>, whichever
    /// is shorter. Its transcript then goes through the ordinary window scan, so every acceptance
    /// rule (anchoring, sequence, named-phrase scope, dedupe) applies to a re-read mark exactly as it
    /// would to a first-pass one.
    /// </para>
    /// </summary>
    /// <param name="candidate">The candidate whose window came back empty.</param>
    /// <param name="start">Absolute start of that window.</param>
    /// <param name="windowEnd">Absolute planned end of that window.</param>
    /// <param name="trimmedAbs">Its transcript in absolute file time, already corrected for leading
    /// non-speech - which is what makes "no words for this blip" answerable at all, since an
    /// untrimmed segment routinely claims to start back at the window's own beginning.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The marks the re-read produced, or an empty list when it did not run or found
    /// nothing.</returns>
    private async Task<List<ProbeMark>> RereadJingleSpeechAsync(
        ProbeCandidate candidate, double start, double windowEnd,
        List<TranscriptSegment> trimmedAbs, CancellationToken ct)
    {
        if (_env.Vad == null)
            return [];
        if (FindUnheardJingleSpeech(start, windowEnd, trimmedAbs) is not { } blip)
            return [];

        var to = Math.Min(windowEnd, blip.EndSeconds + PhraseMarginSeconds);
        var from = Math.Max(start, to - JingleRereadWindowSeconds);
        // Too short to hold an announcement, or no narrower than the window that already failed on
        // it - the second being the honest form of the chunk-boundary test this used to make. That
        // test asked whether the window crossed WhisperChunkSeconds, which classified jingle windows
        // land on exactly (JingleLeadInSeconds + ExpectedAnnouncementSeconds = 30.0) and so never
        // crossed, leaving the re-read reachable only from a recovery pass's wider window. What it
        // was reaching for is that a re-read of the same span is the same framing and can only
        // produce the same answer, and that is what is asked here.
        if (to - from <= PhraseMarginSeconds || to - from >= windowEnd - start)
            return [];

        // Both remedies at once where the run has an upgrade model: a re-framed window and a better
        // recognizer. They address different halves of the same failure - the framing lost the
        // announcement, but what makes an announcement droppable in the first place is that it is
        // one or two quiet words against a jingle, which is exactly where model size tells (see
        // PreciseMarkRefiner.RefinePreciseMarkAsync's own upgrade retry, where the probe that broke
        // each search read "* Musik *" on the small model and the announcement on the large one).
        // Costs nothing extra: this decode was going to happen either way, so the only difference is
        // which recognizer it goes through - unlike the mender's second opinion, which is a decode
        // of its own. A Re-probe re-probe reaches this with SecondOpinion null and _ctx.Transcriber
        // already the heavier model, so it re-reads through that one without needing a branch here.
        var upgradeLanguage = _env.SecondOpinion != null ? _language.Profile.Language : null;
        _env.Log?.Invoke(
            $"window at {FormatTimestamp(start)} empty, VAD speech at " +
            $"{FormatTimestamp(blip.StartSeconds)} inside the jingle - re-reading shorter" +
            (upgradeLanguage == null ? "" : ", --upgrade-model"));

        var samples = await _env.Audio.DecodePcmAsync(
            _ctx.File, from, to - from, _ctx.Info.InputDecoder, ct);
        var fresh = upgradeLanguage is { } language
            ? await _env.SecondOpinion!(samples, language, ct)
            : await _env.TranscribeCounting(samples, ct, _ctx.Transcriber);
        _env.LogTranscript($"jingle re-read {to - from:0.0}s@{FormatTimestamp(from)}", fresh);

        var freshAbs = TrimLeadingNonSpeech(
            ShiftSegments(fresh, from), _ctx.AllSilences, _ctx.NonSpeechRegions, true);
        return await ScanWindowForMarksAsync(
            candidate, from, to, ShiftSegments(freshAbs, -from), freshAbs, null, ct);
    }

    /// <summary>
    /// The first VAD speech segment inside the window that sits within one of its jingle regions and
    /// that the transcript covers with no segment at all - <see cref="RereadJingleSpeechAsync"/>'s
    /// trigger. Blips below <see cref="TransientSpeechFloorSeconds"/> are passed over for the same
    /// reason <see cref="JingleGeometry.AdvancePastNonSpeech"/> passes over them: a jingle's musical
    /// transients cross VAD's threshold too, and too short to be a spoken word is the one thing that
    /// tells them apart.
    /// <para>
    /// Known limit of "no segment covers it", accepted rather than fixed: Whisper routinely stretches
    /// a window's last segment's end timestamp far past the words in it, and such a segment then
    /// covers a jingle it has no words for, vetoing the re-read. Loosening this to "no segment
    /// <em>starts</em> inside the region" would catch it, at the price of firing on most empty long
    /// windows in the file - the stretch is that common - so the cheap, strict test stays until
    /// something measures that trade honestly. A bracketed non-speech tag is a different case and
    /// <em>is</em> discounted (see <see cref="PhraseMatching.CarriesWords"/>): a segment reading
    /// "[Musik]" over a jingle is trivially identifiable as not-words, so it can be dropped on its
    /// own evidence without the general test paying that trade.
    /// </para>
    /// </summary>
    /// <param name="start">Absolute start of the probe window.</param>
    /// <param name="windowEnd">Absolute planned end of the probe window.</param>
    /// <param name="trimmedAbs">The window's transcript in absolute time, already trimmed.</param>
    /// <remarks>Notes: the observed case where a stretched segment timestamp vetoed a re-read.
    /// <include file='../../notes/Detection/RegionProber.xml' path='doc/member[@name="FindUnheardJingleSpeech"]/*' /></remarks>
    private SpeechSegment? FindUnheardJingleSpeech(
        double start, double windowEnd, List<TranscriptSegment> trimmedAbs)
        => _ctx.SpeechSegments
            .Where(b => b.StartSeconds >= start && b.EndSeconds <= windowEnd &&
                        b.EndSeconds - b.StartSeconds >= TransientSpeechFloorSeconds)
            .Where(b => _ctx.NonSpeechRegions.Any(
                r => r.StartSeconds < b.StartSeconds && r.EndSeconds > b.EndSeconds))
            .Where(b => !trimmedAbs.Any(
                s => CarriesWords(s.Text) &&
                     s.StartSeconds < b.EndSeconds && s.EndSeconds > b.StartSeconds))
            .Cast<SpeechSegment?>()
            .FirstOrDefault();

    /// <summary>
    /// Third look at an empty window, for the shape <see cref="RereadJingleSpeechAsync"/> cannot
    /// see: the announcement is exactly where a jingle candidate expects it - in the first speech
    /// behind the music rather than inside it - and the recognizer dropped it because the window is
    /// wider than a single Whisper pass. Nothing here contradicts VAD, so there is no unheard blip
    /// to find; the evidence is only that this candidate expected an announcement and was asked for
    /// it at a width known to lose them.
    /// <para>
    /// Gruelfin.m4b's prologue is the case on record, twice over - lost first to a 50 s window and
    /// again to build 280's classified one, which is <see cref="JingleLeadInSeconds"/> plus
    /// <see cref="ExpectedAnnouncementSeconds"/> = exactly <see cref="WhisperChunkSeconds"/> wide,
    /// the one width that constant exists to warn about. So the re-read keeps the window's own start
    /// and merely stops it at <see cref="JingleRereadWindowSeconds"/>: the same single-pass width the
    /// rest of the tool probes at, and the widest one measured to still hear this word.
    /// </para>
    /// <para>
    /// Through the probing model, not a <c>--upgrade-model</c> upgrade, unlike the blip re-read. What
    /// was measured is that this model hears the announcement at this width; the failure is the
    /// framing's alone, and an upgrade would load a model the file may otherwise never need.
    /// </para>
    /// <para>
    /// Narrowing the window at planning time instead was rejected by measurement: a handful of corpus
    /// jingle marks are accepted more than 26 s into their window, so a narrower window would have
    /// traded this prologue for them. Running only where the window came back empty can add a mark
    /// but can never move one, which is what makes it safe to reach for - and it is confined to the
    /// primary scan's own planned windows, so no recovery pass pays a second decode for it.
    /// </para>
    /// </summary>
    /// <remarks>Notes: both Gruelfin prologue losses with their decode grid, and the marks a narrower planned window would have cost.
    /// <include file='../../notes/Detection/RegionProber.xml' path='doc/member[@name="RereadInOnePassAsync"]/*' /></remarks>
    /// <param name="candidate">The candidate whose window came back empty.</param>
    /// <param name="start">Absolute start of that window, kept as the re-read's start so the phrase
    /// timing rule and the jingle run-up stay exactly what this candidate planned.</param>
    /// <param name="windowEnd">Absolute planned end of that window.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The marks the re-read produced, or an empty list when it did not run or found
    /// nothing.</returns>
    private async Task<List<ProbeMark>> RereadInOnePassAsync(
        ProbeCandidate candidate, double start, double windowEnd, CancellationToken ct)
    {
        // A recovery pass's windows are trimmed to well inside a single pass already (see
        // RecoveryLeadInTrimSeconds), so the width test below is what keeps this off them rather
        // than a flag of its own: what they need is a different framing, and a narrower window from
        // the same start is not one.
        if (!candidate.IsJingle || windowEnd - start <= JingleRereadWindowSeconds)
            return [];
        var to = start + JingleRereadWindowSeconds;
        // A jingle long enough to push its own expectation out of the shortened window is not this
        // failure: re-reading it would ask about audio the announcement is not in. Those are the
        // embedded shape's business, and the blip re-read above has already had its turn at them.
        if (candidate.ExpectAt > to - PhraseMarginSeconds)
            return [];

        _env.Log?.Invoke(
            $"{windowEnd - start:0.0} s window at {FormatTimestamp(start)} empty (wider than one " +
            $"recognizer pass) - re-reading at {to - start:0.0} s");

        var samples = await _env.Audio.DecodePcmAsync(
            _ctx.File, start, to - start, _ctx.Info.InputDecoder, ct);
        var fresh = await _env.TranscribeCounting(samples, ct, _ctx.Transcriber);
        _env.LogTranscript($"one-pass re-read {to - start:0.0}s@{FormatTimestamp(start)}", fresh);

        var freshAbs = TrimLeadingNonSpeech(
            ShiftSegments(fresh, start), _ctx.AllSilences, _ctx.NonSpeechRegions, _env.Vad != null);
        return await ScanWindowForMarksAsync(
            candidate, start, to, ShiftSegments(freshAbs, -start), freshAbs, null, ct);
    }

    /// <summary>
    /// Last look at an empty jingle window, and the only one that reads the music itself: tiles
    /// [<see cref="ProbeCandidate.MusicFromSeconds"/>, the window's own start] with overlapping
    /// windows of <see cref="JingleRereadWindowSeconds"/>, stepping
    /// <see cref="JingleMusicTileStepSeconds"/>, and stops at the first tile that yields a mark.
    /// <para>
    /// This is the second half of what one spanning window used to do (see
    /// <see cref="JingleCandidate"/>), and the reason it is tiled rather than spanned is the one
    /// <see cref="WhisperChunkSeconds"/> records: past a chunk the recognizer drops a lone word
    /// outright, and an announcement spoken over music is exactly a lone word. On the corpus this is
    /// one tile for any jingle up to 20 s - which 11 of its 16 books never exceed - and two up to
    /// 34 s, so the worst case costs the two chunks the old 50 s ceiling was already paying.
    /// </para>
    /// <para>
    /// The overlap is two phrase margins, so an announcement cannot fall across a tile border
    /// without landing whole inside a neighbour. Every tile is decoded on its own: nothing is served
    /// from the overlap cache, nothing it reads becomes cache, and the decode never reads ahead. All
    /// three would hand a tile - or its successor - a framing from another window, and the framing
    /// is the entire content of this second look (the same reasoning that reverted the gap
    /// re-probe's transcript reuse). It is also what lets the tiles bypass window-end seam snapping,
    /// which could otherwise stretch one back past a chunk: nothing stitches to a tile, so no seam
    /// has to be found for it.
    /// </para>
    /// </summary>
    /// <param name="candidate">The jingle candidate whose speech window came back empty.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The marks a tile produced, or an empty list when none did or the candidate has no
    /// music to read.</returns>
    private async Task<List<ProbeMark>> RereadJingleMusicAsync(
        ProbeCandidate candidate, CancellationToken ct)
    {
        if (candidate.MusicFromSeconds is not { } musicFrom)
            return [];
        // Reported as what it is: a mark found here was heard inside the music, which is exactly
        // what the "embedded in a jingle" note counts.
        var tileCandidate = candidate with { Class = CandidateClass.JingleEmbedded };
        var namedBefore = _namedFound.Count;
        _env.Log?.Invoke(
            $"nothing behind the jingle at {FormatTimestamp(candidate.Start)} - tiling its music " +
            $"from {FormatTimestamp(musicFrom)} in {JingleRereadWindowSeconds:0.#} s steps");

        // Same exposure as SilenceThresholdProbe's frame length: the step is derived from
        // WhisperChunkSeconds and PhraseMarginSeconds, which --set: can drive to a step of zero
        // (30 - 3 x 10, say), and the guard below cannot break out of that because the window it
        // measures never moves. Tiling is a rescue that can only add a mark, so declining it costs
        // at most the mark it might have found - against a loop that re-transcribes one window for
        // ever.
        if (JingleMusicTileStepSeconds <= 0)
        {
            _env.Log?.Invoke(
                $"jingle music tiling skipped - the configured step is {JingleMusicTileStepSeconds:0.##} s");
            return [];
        }

        for (var from = musicFrom; from < candidate.Start; from += JingleMusicTileStepSeconds)
        {
            ct.ThrowIfCancellationRequested();
            var to = Math.Min(from + JingleRereadWindowSeconds, _region.ToSeconds);
            if (to - from <= PhraseMarginSeconds)
                break;
            var samples = await _env.Audio.DecodePcmAsync(
                _ctx.File, from, to - from, _ctx.Info.InputDecoder, ct);
            var fresh = await _env.TranscribeCounting(samples, ct, _ctx.Transcriber);
            _env.LogTranscript($"jingle music {to - from:0.0}s@{FormatTimestamp(from)}", fresh);

            var freshAbs = TrimLeadingNonSpeech(
                ShiftSegments(fresh, from), _ctx.AllSilences, _ctx.NonSpeechRegions, true);
            var marks = await ScanWindowForMarksAsync(
                tileCandidate, from, to, ShiftSegments(freshAbs, -from), freshAbs, null, ct);
            // A named mark counts as an answer too, even though it is not returned: it goes straight
            // into the accumulator, and the tiles after it would only re-hear the same announcement.
            if (marks.Count > 0 || _namedFound.Count > namedBefore)
                return marks;
        }
        return [];
    }

    /// <summary>
    /// Produces the probe window's full transcript in absolute file time, assembled from the
    /// previous window's cache (overlap reuse), a fresh Whisper decode, or a mix. The whole window
    /// is always represented, so nothing a reuse-only "search just the new tail" scheme would
    /// silently drop - e.g. a phrase the previous probe rejected for want of a qualifying anchor
    /// that this window can anchor - is ever lost.
    /// <para>
    /// --verbose logging only ever shows what Whisper actually transcribed just now, at its own
    /// (0-based) timestamps - never the reused portion restated at window-relative time, which would
    /// make every probe look like a fresh full-window decode even when most of it was cache. What
    /// the phrase matching then sees is unaffected; only what gets logged changes.
    /// </para>
    /// </summary>
    /// <param name="start">Absolute start of the window.</param>
    /// <param name="plan">The window being probed, and the candidates that follow it - the latter
    /// only so a decode can read ahead into them (see <see cref="ExtendToPlannedSeam"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The window transcript, plus - for a partial-overlap assembly - the index of its
    /// first fresh segment, so a detection drawing on text from both sides of the cache/fresh
    /// boundary can be flagged (see <see cref="PhraseMatch.SpansMerge"/>); null when the window is
    /// entirely one or the other.</returns>
    private async Task<(List<TranscriptSegment> Segments, int? MergeBoundarySegIndex)>
        AssembleWindowTranscriptAsync(double start, WindowPlan plan, CancellationToken ct)
    {
        // A window whose start falls outside the cached span has no usable overlap.
        if (start < _cacheFrom || start >= _cacheTo)
            return (await DecodeFullWindowAsync(start, plan, ct), null);

        var expectAt = plan.Candidates[plan.Index].ExpectAt;
        if (CacheHidesTheExpectation(start, expectAt))
        {
            _env.Log?.Invoke(
                $"re-reading window at {FormatTimestamp(start)} - cache covers the expected " +
                $"announcement at {FormatTimestamp(expectAt)} only inside an earlier segment");
            return (await DecodeFullWindowAsync(start, plan, ct), null);
        }

        if (plan.End <= _cacheTo)
            // Fully contained in the cached span: reuse its transcript wholesale, no Whisper at all.
            // The (larger) cache is deliberately left untouched so a later candidate starting within
            // it can keep reusing it too.
            return (WindowSlice(_cacheSegmentsAbs, start, plan.End), null);

        return await DecodeOverlapTailAsync(start, plan, ct);
    }

    /// <summary>
    /// Whether the overlap cache covers this window's expectation point only inside a segment that
    /// began before the window - in which case the cache cannot be reused, because
    /// <see cref="WindowSlice"/> is about to drop that segment and leave the scan with a hole
    /// exactly where the candidate is looking.
    /// <para>
    /// Deliberately not "the cache has nothing at the expectation": an empty stretch of cache is
    /// genuinely quiet audio, and re-reading it would only find it quiet again - the whole-book
    /// re-decode the overlap cache's original bargain exists to avoid (see
    /// <see cref="CacheableEnd"/>'s residual). The trigger is the opposite case, where the audio
    /// <em>was</em> transcribed and this window merely has no reading of its own to show for it.
    /// It is rare by construction: a candidate's expectation is the far side of its own pause, so a
    /// single segment reaching from before the window to past that point is one that swallowed a
    /// candidate-grade silence whole, which is the run-on signature itself.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the run-on segment that lost The Forever War's chapter 1 with no pass ever reading it.
    /// <include file='../../notes/Detection/RegionProber.xml' path='doc/member[@name="CacheHidesTheExpectation"]/*' /></remarks>
    /// <param name="start">Absolute start of the window.</param>
    /// <param name="expectAt">Where this candidate expects its announcement.</param>
    private bool CacheHidesTheExpectation(double start, double expectAt)
        => _cacheSegmentsAbs.Any(s => s.StartSeconds < start && s.EndSeconds > expectAt);

    /// <summary>
    /// The part of a decoded or cached span that belongs to one window: the segments starting inside
    /// it. Every decode may run past the window it was asked for (see
    /// <see cref="ExtendToPlannedSeam"/>), and the surplus belongs to the cache alone - letting it
    /// through to the scan would place marks from audio beyond the window the candidate was probed
    /// with, at a candidate's window width that nothing planned.
    /// </summary>
    /// <param name="segmentsAbs">A decoded or cached span, in absolute file time.</param>
    /// <param name="start">Absolute start of the window.</param>
    /// <param name="windowEnd">Absolute planned end of the window.</param>
    private static List<TranscriptSegment> WindowSlice(
        List<TranscriptSegment> segmentsAbs, double start, double windowEnd)
        => segmentsAbs.Where(s => s.StartSeconds >= start && s.StartSeconds < windowEnd).ToList();

    /// <summary>
    /// Transcribes a whole window from scratch and makes it the new cache - the path taken whenever
    /// the previous decode's span cannot serve this window, or must not (see
    /// <see cref="CacheHidesTheExpectation"/>).
    /// <para>
    /// The decode may run past the window's own planned end (<see cref="ExtendToPlannedSeam"/>).
    /// What the scan is handed is sliced back to the window either way; only the surplus the
    /// recognizer actually filled becomes cache for the windows after it (see
    /// <see cref="CacheableEnd"/>).
    /// </para>
    /// </summary>
    /// <param name="start">Absolute start of the window.</param>
    /// <param name="plan">The window being probed; see <see cref="AssembleWindowTranscriptAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<List<TranscriptSegment>> DecodeFullWindowAsync(
        double start, WindowPlan plan, CancellationToken ct)
    {
        var decodeEnd = ExtendToPlannedSeam(plan, start);
        var samples = await _env.Audio.DecodePcmAsync(
            _ctx.File, start, decodeEnd - start, _ctx.Info.InputDecoder, ct);
        var fresh = await _env.TranscribeCounting(samples, ct, _ctx.Transcriber);
        var freshAbs = ShiftSegments(fresh, start);
        var cacheEnd = CacheableEnd(freshAbs, plan.End, decodeEnd);
        _env.LogTranscript(
            $"probe {decodeEnd - start:0.0}s@{FormatTimestamp(start)}" +
            ProbeNote(false, decodeEnd, plan.End, cacheEnd), fresh);
        return WindowSlice(CacheWindow(freshAbs, start, cacheEnd), start, plan.End);
    }

    /// <summary>
    /// Partial overlap: cuts between the reused cache and a fresh tail decode. The previous decode
    /// stopped at a planned window end, i.e. a seam snapped to a silence mid-point (see
    /// <see cref="GapPlanning.PlanWindowEnd"/>) - its own window's, or a later candidate's when it
    /// read ahead - so the cache normally ends exactly at a seam and the split search re-finds it at
    /// distance zero: the fresh decode starts right where the previous one stopped, stitching the
    /// transcripts together word-safely with nothing re-decoded and nothing dropped. It genuinely
    /// decides only for overlaps that plan did not anticipate (a probe-window resize in between),
    /// snapping to the best seam still covered by the cache; the border fallback means no seam
    /// exists, and hence no chapter transition in the overlap.
    /// </summary>
    /// <param name="start">Absolute start of the window.</param>
    /// <param name="plan">The window being probed; see <see cref="AssembleWindowTranscriptAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<(List<TranscriptSegment> Segments, int? MergeBoundarySegIndex)>
        DecodeOverlapTailAsync(double start, WindowPlan plan, CancellationToken ct)
    {
        var splitPoint = FindOverlapSplitPoint(
            start, _cacheTo, plan.End, _ctx.AllSilences, _ctx.NonSpeechRegions, _env.Vad != null,
            allowBeyondBorder: false);
        var decodeEnd = ExtendToPlannedSeam(plan, splitPoint);
        var samples = await _env.Audio.DecodePcmAsync(
            _ctx.File, splitPoint, decodeEnd - splitPoint, _ctx.Info.InputDecoder, ct);
        var fresh = await _env.TranscribeCounting(samples, ct, _ctx.Transcriber);
        var reused = _cacheSegmentsAbs
            .Where(s => s.StartSeconds >= start && s.StartSeconds < splitPoint).ToList();
        List<TranscriptSegment> assembledAbs = [.. reused, .. ShiftSegments(fresh, splitPoint)];
        var cacheEnd = CacheableEnd(assembledAbs, plan.End, decodeEnd);
        _env.LogTranscript(
            $"probe {decodeEnd - splitPoint:0.0}s@{FormatTimestamp(splitPoint)}" +
            ProbeNote(true, decodeEnd, plan.End, cacheEnd), fresh);
        var assembled = CacheWindow(assembledAbs, start, cacheEnd);
        return (WindowSlice(assembled, start, plan.End), reused.Count);
    }

    /// <summary>The parenthetical a probe's --verbose line carries, so a log reader can tell what
    /// the decode was: an overlap tail rather than a whole window, how much of it ran past the
    /// window the candidate was actually probed with (that surplus is cache for the windows to
    /// come, never something this candidate's scan saw), and how much of <em>that</em> the
    /// recognizer left untranscribed and so will be read again rather than reused.</summary>
    /// <param name="tail">Whether this decode was an overlap tail.</param>
    /// <param name="decodeEnd">Absolute end of what was decoded.</param>
    /// <param name="windowEnd">Absolute planned end of the window it was decoded for.</param>
    /// <param name="cacheEnd">Absolute end of what the decode may be trusted for; see
    /// <see cref="CacheableEnd"/>.</param>
    private static string ProbeNote(bool tail, double decodeEnd, double windowEnd, double cacheEnd)
    {
        // Below a tenth of a second the read-ahead is a rounding artifact of the seam search, not
        // audio anyone gained; saying "+0.0s ahead" would only make every line noisier.
        var ahead = decodeEnd - windowEnd >= 0.05 ? $"+{decodeEnd - windowEnd:0.0}s ahead" : null;
        // Worth naming, because a log reader cannot see it otherwise: the transcript simply stops,
        // and only arithmetic against the decode length shows that it did. This is the shape
        // CacheableEnd exists for, so a run in which it never appears is a run in which the rule
        // never bit.
        var uncached = decodeEnd - cacheEnd >= 0.05 ? $"{decodeEnd - cacheEnd:0.0}s uncached" : null;
        var notes = new[] { tail ? "tail" : null, ahead, uncached }.Where(n => n != null);
        return string.Join(", ", notes) is { Length: > 0 } joined ? $" ({joined})" : "";
    }

    /// <summary>
    /// How far a decode may be trusted, i.e. how much of it becomes overlap cache: as far as the
    /// recognizer actually got, and never past that into audio it read but said nothing about.
    /// <para>
    /// A transcript that stops short of the decode end has not established silence there, it has
    /// failed to read it - and the cache cannot tell those apart, so a later window served from that
    /// stretch inherits the failure instead of decoding the audio itself with its own, better
    /// framing.
    /// </para>
    /// <para>
    /// Floored at the window's own planned end, never below: the scan has already covered that far,
    /// and a planned end is a snapped seam (see <see cref="GapPlanning.PlanWindowEnd"/>), which is
    /// what the next overlap's split search expects to find. So this can only ever give back the
    /// read-ahead surplus - the worst case for a window is the behaviour it had before reading ahead
    /// existed, and the saving survives untouched wherever the recognizer did fill the audio it was
    /// handed.
    /// </para>
    /// <para>
    /// The floor leaves one residual, deliberately: a window whose own decode came back empty still
    /// caches its planned end as read. That is not the read-ahead's doing but the overlap cache's
    /// original bargain, unchanged since before it - re-deciding it would re-decode every quiet
    /// stretch in a book, which wants a measurement this fix does not have.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the BARDIOC announcement lost for a whole run to a decode the recognizer stopped short of.
    /// <include file='../../notes/Detection/RegionProber.xml' path='doc/member[@name="CacheableEnd"]/*' /></remarks>
    /// <param name="segmentsAbs">The decode's transcript, in absolute file time.</param>
    /// <param name="windowEnd">Absolute planned end of the window it was decoded for.</param>
    /// <param name="decodeEnd">Absolute end of what was decoded.</param>
    private static double CacheableEnd(
        List<TranscriptSegment> segmentsAbs, double windowEnd, double decodeEnd)
    {
        // Max rather than the last segment's end: Whisper's segment ends are not strictly ordered
        // once a window re-segments, and an end that overshoots the audio it was given is common
        // enough that the decode end has to cap it.
        var transcribedTo = segmentsAbs.Count == 0 ? windowEnd : segmentsAbs.Max(s => s.EndSeconds);
        return Math.Min(decodeEnd, Math.Max(windowEnd, transcribedTo));
    }

    /// <summary>Makes a freshly assembled transcript the overlap cache, and returns it unchanged so
    /// the callers can assemble and cache in one expression. The segments kept are all of them, the
    /// span claimed only as far as <see cref="CacheableEnd"/> allows - which reaches past the window
    /// whenever the decode read ahead and was transcribed that far. Slicing back down to the window
    /// is <see cref="WindowSlice"/>'s job, at the caller.</summary>
    /// <param name="segments">The transcript, in absolute file time.</param>
    /// <param name="start">Absolute start of the span it covers.</param>
    /// <param name="spanEnd">Absolute end of that span, from <see cref="CacheableEnd"/>.</param>
    private List<TranscriptSegment> CacheWindow(List<TranscriptSegment> segments, double start, double spanEnd)
    {
        _cacheSegmentsAbs = segments;
        _cacheFrom = start;
        _cacheTo = spanEnd;
        return segments;
    }

    /// <summary>Finds every chapter announcement in one decoded window and turns the acceptable
    /// ones into marks.</summary>
    /// <param name="candidate">The candidate whose window this is.</param>
    /// <param name="start">Absolute start of the window.</param>
    /// <param name="windowEnd">Absolute planned end of the window - what precise marking
    /// anchors its search against (see <see cref="MarkContext.Transcript"/>).</param>
    /// <param name="segments">The window transcript in window-relative time, for phrase matching.</param>
    /// <param name="trimmedAbs">The same transcript in absolute file time, for the jingle edge
    /// adjustment inside <see cref="JingleGeometry.ResolveJingleAnchor"/>.</param>
    /// <param name="mergeBoundarySegIndex">The cache/fresh boundary, if any; see
    /// <see cref="AssembleWindowTranscriptAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<List<ProbeMark>> ScanWindowForMarksAsync(
        ProbeCandidate candidate, double start, double windowEnd, List<TranscriptSegment> segments,
        List<TranscriptSegment> trimmedAbs, int? mergeBoundarySegIndex, CancellationToken ct)
    {
        var marks = new List<ProbeMark>();
        // Window-local continuation of _lastNumber: several accepted marks within one window must
        // each top the previous one, exactly as consecutive windows' marks do.
        var windowLast = _lastNumber ?? 0;

        // The prologue's own scope closes the moment the first numbered chapter is accepted, so the
        // named scan runs first: a window holding both the prologue announcement and chapter 1
        // (a short front matter, or a wide jingle window) must still yield the prologue.
        await ScanWindowForNamedMarksAsync(candidate, start, windowEnd, segments, trimmedAbs, ct);

        // With --ignore-chapter-numbers a chapter is just another titled position, so it goes down
        // the same path the prologue does and nothing below this point applies to it.
        if (_env.Options.IgnoreChapterNumbers)
            return marks;

        foreach (var readings in _env.FindCappedPhraseReadings(
                     segments, _language.Profile, mergeBoundarySegIndex,
                     BareNumberReadingFor(WideBareNumberReading)))
        {
            // The wording that claimed these words may not be the one the narrator spoke, so a reading
            // the sequence turns down is not the end of the announcement: the wordings it superseded
            // are tried behind it, and the first reading that yields a mark is the one taken. See
            // PhrasePattern.MatchGroups for why a phrase produces rival readings at all.
            var heard = readings[0];
            var outOfSequence = false;
            var accepted = false;
            var tried = new List<(int Number, IsolationRule Guards, bool OpensSegment)>();
            for (var i = 0; i < readings.Count && !accepted; i++)
            {
                var match = readings[i];
                var phraseAbs = start + match.PhraseStartSeconds;
                // Before the mender, because a number continuing a suspected new part is not in doubt
                // at all - it is corroborated by the announcements already held back - while the
                // mender would judge it against the sequence it is in the process of leaving and
                // "correct" it into that one's numbering.
                if (RestartTrackingAllowed && ContinuesPendingRestart(match))
                {
                    marks.AddRange(await TrackRestartAsync(
                        match, candidate, start, windowEnd, phraseAbs, trimmedAbs, ct));
                    windowLast = _lastNumber ?? windowLast;
                    accepted = true;
                    break;
                }
                // Ahead of the sequence check rather than after it, because a number that fails that
                // check is exactly one of the two shapes worth questioning: a mishearing downwards is
                // indistinguishable from an in-text mention until the audio is asked again. A mend
                // that finds nothing leaves the reading untouched, so the check below then does what
                // it always did - including rejecting it.
                // Only what the window itself heard is ever mended: a rival is reached because that
                // reading was turned down, and paying a re-read of the audio to argue the next one
                // into the sequence would be spending decodes on the readings least likely to be
                // right. A rival that does not fit simply loses, as it did before it had a name.
                if (i == 0)
                {
                    if (_ctx.SecondGuessNumbers &&
                        await _mender.MendAsync(
                            match, _language.Profile, start, windowEnd,
                            SequenceBounds(windowLast), ct) is { } mended)
                        match = match with { Number = mended };
                    heard = match;
                }
                // Two readings that agree on the number and on what has to be vouched for are one
                // question asked twice - the sequence and the placement both answer them alike, and
                // placement is where the audio gets decoded again - so the rival is dropped rather
                // than sent to fail identically. Which is the ordinary case: the built-in wordings
                // differ in where the number sits, not in what it turns out to be.
                var shape = (match.Number, match.Guards, match.OpensSegment);
                if (tried.Contains(shape))
                    continue;
                tried.Add(shape);
                if (IsOutOfSequence(match, phraseAbs, windowLast))
                {
                    outOfSequence |= i == 0;
                    continue;
                }
                if (await AcceptMatchAsync(
                        match, candidate, start, windowEnd, phraseAbs, trimmedAbs, windowLast, ct)
                    is not { } mark)
                    continue;
                // A chapter of the sequence in force ends any restart being tracked: a part that had
                // really started would have no more chapters of the old numbering left to announce.
                AbandonPendingRestart();
                marks.Add(mark);
                windowLast = mark.Number;
                accepted = true;
            }
            if (accepted || !outOfSequence)
                continue;

            // The window's own reading was below the sequence and nothing behind it rescued the
            // announcement. It is then either an in-text mention or the opening of a part that counts
            // from 1 again, and only the announcements that follow can tell the two apart - a
            // question about the number the window actually heard, so it is asked of that reading and
            // not of a rival that was only ever a fallback. Strictly below, so not a re-detection of
            // the chapter just accepted.
            if (heard.Number < windowLast && RestartTrackingAllowed && OpensPendingRestart(heard))
                marks.AddRange(await TrackRestartAsync(
                    heard, candidate, start, windowEnd, start + heard.PhraseStartSeconds, trimmedAbs, ct));
            windowLast = _lastNumber ?? windowLast;
        }

        return marks;
    }

    /// <summary>
    /// Whether this match carries the number a restart being tracked is waiting for. Asked before
    /// anything else, because that answer overrides the ordinary sequence test in both directions:
    /// a number continuing the new part may sit below the old sequence (part 2's chapter 2 behind
    /// part 1's chapter 15) or above it (part 2's chapter 4 behind part 1's chapter 3), and in the
    /// second shape the old sequence would happily have swallowed it - which is precisely how two
    /// numberings get mixed together.
    /// <para>
    /// A number already in the run answers false and is then dropped as an ordinary duplicate: one
    /// announcement is routinely heard by two overlapping windows, and taking the second hearing as
    /// the start of a fresh run would reset the tracking on every book that has one.
    /// </para>
    /// </summary>
    /// <param name="match">The phrase match to judge.</param>
    private bool ContinuesPendingRestart(PhraseMatch match)
        => _pendingRestart.Count > 0 && match.Number == _pendingRestart[^1].Match.Number + 1;

    /// <summary>
    /// Whether an announcement below the sequence may open a restart being tracked. Everything but
    /// a re-hearing of one already held qualifies - the run's length is what settles a restart, not
    /// its first number - so this only exists to keep an overlapping window's second reading of the
    /// same announcement from throwing away the run it is part of.
    /// </summary>
    /// <param name="match">The phrase match to judge.</param>
    private bool OpensPendingRestart(PhraseMatch match)
        => !_pendingRestart.Any(p => p.Match.Number == match.Number);

    /// <summary>
    /// The stretch of the chapter sequence a fresh announcement is judged against
    /// (<see cref="SuspectNumberMender"/>, <see cref="RefinedNumberVote"/>).
    /// <para>
    /// The lower bound is normally the last number accepted; before this region has one, the sequence
    /// is expected to begin at --expected-start-chapter (or chapter 1), so the number below that
    /// expectation plays the same role. Without that, the one mishearing that costs the most - the
    /// file's <em>first</em> chapter read as some large number, which declares everything before it
    /// missing and sends Scan across the whole book - would be the one case never questioned.
    /// </para>
    /// <para>
    /// The upper bound exists only while something is known to follow: a --verify gap region's
    /// <see cref="DetectionRegion.UpperNumber"/>, or - during a sequence-gap re-probe - the chapter
    /// that revealed the gap (<see cref="_gapAbove"/>). Both turn the question from "could the
    /// sequence continue like this?" into "can this hole hold that number?", which for a
    /// one-chapter hole has exactly one answer.
    /// </para>
    /// </summary>
    /// <param name="windowLast">The highest number accepted so far in this window's sequence, or 0
    /// when there is none.</param>
    internal NumberBounds SequenceBounds(int windowLast)
        => new(windowLast > 0 ? windowLast : (_env.Options.ExpectedStartChapter ?? 1) - 1,
               _gapAbove ?? _region.UpperNumber);

    /// <summary>
    /// Whether this window is hunting known missing numbers inside a stretch the sequence closes
    /// from above - a sequence-gap re-probe (<see cref="_gapAbove"/>), a Re-probe or --verify gap
    /// region (<see cref="DetectionRegion.UpperNumber"/>) - rather than scanning forward into a book
    /// whose next chapter number is whatever comes next.
    /// <para>
    /// This is what decides how hard <c>--chapter-phrase none</c> looks at a transcript. The two
    /// questions are the same question: the wider reading is affordable exactly where the hole
    /// already says which numbers may appear, because a wrong one is then rejected before any work
    /// is spent on it, and unaffordable on the forward scan, where every number spoken in the prose
    /// of an 18-hour book would buy itself a refinement. It also decides whether
    /// <see cref="AnnouncementIsolation"/> then has to vouch for the position, so the licence and
    /// the check that pays for it can never be enabled apart.
    /// </para>
    /// </summary>
    private bool WideBareNumberReading => (_gapAbove ?? _region.UpperNumber) != null;

    /// <summary>
    /// Reports the announcements this window heard but could not number, and asks
    /// <see cref="SuspectNumberMender.ReadUnnumberedAsync"/> to read
    /// the number out of differently framed audio - turning the announcement into an ordinary mark
    /// when it succeeds. Only ever called for a window that produced no mark of its own - counting
    /// <see cref="RereadJingleSpeechAsync"/>'s second look as this window's own, since a window it
    /// rescued was never a window that heard nothing. With a mark, a further bare "chapter" in the
    /// same transcript is prose, not a missed announcement.
    /// <para>
    /// Does nothing under --ignore-chapter-numbers, where an announcement without a number is the
    /// normal case and has already been marked as a named one.
    /// </para>
    /// <para>
    /// Every unreadable announcement is logged whether or not its re-read succeeds: the cases the
    /// re-read cannot fix - a word ordinal past a language's parser, a number above 999 - are
    /// exactly the ones where knowing the phrase was heard and discarded saves the next
    /// investigation. A sequence gap re-frames this window again later (see
    /// <see cref="ReprobeGapCandidatesAsync"/>), so the two recoveries overlap without duplicating:
    /// chapter 13 of "I Shall Wear Midnight" is read one way from the window it was probed with and
    /// another from a wider one over the same announcement, and either route reaches that.
    /// </para>
    /// <para>
    /// A mark the re-read produces goes through <see cref="AcceptMatchAsync"/> like any other, at the
    /// position and confidence of the reading that first heard it - the re-read contributes the
    /// number and nothing else. Bounded by <see cref="MaxUnnumberedMendsPerRegion"/>; the logging
    /// continues past that cap, only the decodes stop.
    /// </para>
    /// </summary>
    /// <param name="candidate">The candidate whose window this is.</param>
    /// <param name="start">Absolute start of the window.</param>
    /// <param name="windowEnd">Absolute planned end of the window - what the upgrade-model re-read
    /// re-decodes, and what precise marking anchors its search against.</param>
    /// <param name="segments">The window transcript, in window-relative time.</param>
    /// <param name="trimmedAbs">The same transcript in absolute file time, for the jingle anchor.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The marks the re-reads produced, empty when none did.</returns>
    /// <remarks>Notes: the chapter that read Roman from one window width and digits from another.
    /// <include file='../../notes/Detection/RegionProber.xml' path='doc/member[@name="RecoverUnnumberedAnnouncementsAsync"]/*' /></remarks>
    private async Task<List<ProbeMark>> RecoverUnnumberedAnnouncementsAsync(
        ProbeCandidate candidate, double start, double windowEnd, List<TranscriptSegment> segments,
        List<TranscriptSegment> trimmedAbs, CancellationToken ct)
    {
        var marks = new List<ProbeMark>();
        if (_env.Options.IgnoreChapterNumbers)
            return marks;

        var windowLast = _lastNumber ?? 0;
        foreach (var heard in FindUnnumberedAnnouncements(segments, _language.Profile))
        {
            var phraseAbs = start + heard.PhraseStartSeconds;
            _env.Log?.Invoke(
                $"chapter phrase at {FormatTimestamp(phraseAbs)}, " +
                $"no readable number: \"{heard.Text}\"");
            if (_unnumberedMends >= MaxUnnumberedMendsPerRegion)
                continue;
            _unnumberedMends++;
            if (await _mender.ReadUnnumberedAsync(
                    heard, _language.Profile, start, windowEnd, SequenceBounds(windowLast), ct)
                is not { } number)
                continue;

            var match = new PhraseMatch(
                number, heard.PhraseStartSeconds, heard.PhraseEndSeconds, heard.Confidence);
            if (IsOutOfSequence(match, phraseAbs, windowLast))
                continue;
            if (await AcceptMatchAsync(
                    match, candidate, start, windowEnd, phraseAbs, trimmedAbs, windowLast, ct)
                is not { } mark)
                continue;
            marks.Add(mark);
            windowLast = mark.Number;
        }
        return marks;
    }

    /// <summary>
    /// Finds the prologue/epilogue announcements in one decoded window - plus the chapter
    /// announcements themselves under <c>--ignore-chapter-numbers</c> - and turns the in-scope ones
    /// into named marks. Kept apart from the numbered scan because nothing these produce takes part
    /// in the chapter sequence: no jingle length is observed and no window sequence is settled - a
    /// named mark is a title at a position and nothing more, so it must not steer machinery that
    /// reasons about consecutive chapters. It does feed the adaptive silence threshold, which is not
    /// sequence reasoning but a statement about how this book separates its sections, and which
    /// starves into probing every candidate in the file if only numbered chapters may feed it.
    /// </summary>
    /// <param name="candidate">The candidate whose window this is.</param>
    /// <param name="start">Absolute start of the window.</param>
    /// <param name="windowEnd">Absolute planned end of the window - what precise marking
    /// anchors its search against (see <see cref="MarkContext.Transcript"/>).</param>
    /// <param name="segments">The window transcript in window-relative time, for phrase matching.</param>
    /// <param name="trimmedAbs">The same transcript in absolute file time, for the jingle anchor.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ScanWindowForNamedMarksAsync(
        ProbeCandidate candidate, double start, double windowEnd, List<TranscriptSegment> segments,
        List<TranscriptSegment> trimmedAbs, CancellationToken ct)
    {
        foreach (var match in FindNamedMatches(segments, _language.Profile))
        {
            if (!IsInScope(match.Phrase, start + match.PhraseStartSeconds))
                continue;
            await AcceptNamedMatchAsync(match, candidate, start, windowEnd, trimmedAbs, ct);
        }

        if (!_env.Options.IgnoreChapterNumbers)
            return;

        // After the prologue/epilogue pass, so that a window holding both a scoped announcement and
        // a chapter still resolves the scoped one against the chapter count it had on arrival.
        foreach (var match in FindChapterAnnouncements(segments, _language.Profile))
            await AcceptNamedMatchAsync(match, candidate, start, windowEnd, trimmedAbs, ct);
    }

    /// <summary>
    /// Probe's half of the scope question: how many chapters sit before the announcement. The rule
    /// itself is <see cref="NamedMarkLedger.IsInScope"/>, shared with Scan; only the count is a
    /// per-pass matter, because the two passes read it off different lists.
    /// </summary>
    /// <param name="phrase">The phrase that matched.</param>
    /// <param name="phraseAbs">Absolute time the announcement was heard at, for the note.</param>
    private bool IsInScope(NamedPhrase phrase, double phraseAbs)
        => _named.IsInScope(phrase, phraseAbs, ChaptersBefore(phraseAbs), _env.Log);

    /// <summary>
    /// How many chapters are known to sit <em>before</em> this position in the file - the landmark
    /// both positional <see cref="NamedPhraseScope"/>s are measured against. Under
    /// <c>--ignore-chapter-numbers</c> chapters live in the named list rather than in the numbered
    /// one, and counting only the latter would leave the epilogue's scope shut for the whole file.
    /// <para>
    /// Counted by position rather than by how many have been accepted so far, which are the same
    /// number for a walk that runs strictly forward and only that walk. The jingle-first scan's
    /// second half runs back over the head of the file with every jingle-found chapter already in
    /// hand, and a prologue there is still a prologue; so is one found by a wide window that had
    /// already picked up the chapter behind it. The scopes say "before the first chapter", not
    /// "before the first chapter was noticed".
    /// </para>
    /// </summary>
    /// <param name="phraseAbs">Absolute time the announcement being judged was heard at.</param>
    private int ChaptersBefore(double phraseAbs) => _env.Options.IgnoreChapterNumbers
        ? _namedFound.Count(m => m.Kind == ChapterKind && m.TimeSeconds < phraseAbs)
        : _found.Count(c => c.TimeSeconds < phraseAbs);

    /// <summary>Seconds every default-mode mark is placed ahead of the announcement onset
    /// (<c>--mark-lead</c>), named once here because all four placement paths below must agree on
    /// it - <see cref="JingleGeometry.RefineDefaultMark"/>'s no-op case depends on the value that
    /// produced its input.</summary>
    private double MarkLead => _env.Options.MarkLeadSeconds;

    /// <summary><see cref="NamedPhrase.Kind"/> of the synthetic chapter phrase, the one named kind
    /// that is exempt from the <c>--custom</c> mark cap.</summary>
    private string ChapterKind => _language.Profile.ChapterAnnouncement.Kind;

    /// <summary>
    /// Places, logs and records one in-scope named match - unless <see cref="ShouldDropNamedMatch"/>
    /// says this one adds nothing, which is checked first so a dropped match costs no mark placement
    /// at all (that is where the refinement transcriptions are spent).
    /// <para>
    /// A non-repeatable phrase replaces any earlier mark of its own kind, so the last announcement
    /// within the scope wins rather than the first: front matter routinely mentions what is coming
    /// ("...gelesen von...; Prolog") before the narrator actually announces it, and the real
    /// announcement is by construction the later of the two - whereas nothing follows the genuine
    /// one inside its own scope, which the prologue's closes at chapter 1 and the epilogue's at the
    /// end of the file. The replaced mark's own placement work is simply discarded; at one prologue
    /// and one epilogue per book that costs at most a couple of extra refinement transcriptions.
    /// </para>
    /// <para>
    /// "Later" is measured in the file, not in the order the passes happened to hear things -
    /// <see cref="ShouldDropNamedMatch"/> holds that half. The two agreed while only Probe's
    /// forward scan produced named marks, and stopped agreeing as soon as the recovery passes did:
    /// they run after Probe and work backwards through the book's gaps, so a mid-book match found
    /// late in the run would otherwise replace the real end-of-book announcement Probe had already
    /// marked, which is precisely what happened on one real book.
    /// </para>
    /// </summary>
    /// <param name="match">The named match, in window-relative time.</param>
    /// <param name="candidate">The candidate whose window this probe decoded.</param>
    /// <param name="start">Absolute start of that window.</param>
    /// <param name="windowEnd">Absolute planned end of the window - what precise marking
    /// anchors its search against (see <see cref="MarkContext.Transcript"/>).</param>
    /// <param name="trimmedAbs">The window's transcript in absolute file time.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>Notes: the mid-book false match that displaced a real epilogue.
    /// <include file='../../notes/Detection/RegionProber.xml' path='doc/member[@name="AcceptNamedMatchAsync"]/*' /></remarks>
    private async Task AcceptNamedMatchAsync(
        NamedMatch match, ProbeCandidate candidate, double start, double windowEnd,
        List<TranscriptSegment> trimmedAbs, CancellationToken ct)
    {
        var phraseAbs = start + match.PhraseStartSeconds;
        if (ShouldDropNamedMatch(match.Phrase, phraseAbs))
            return;

        // Same reasoning as TightenThreshold's _lastNumber guard: the silence before the region's
        // very first mark is front matter's, routinely far longer than any break between sections,
        // and adopting it alone would raise the threshold past every real candidate that follows.
        var teachesThreshold = _found.Count > 0 || _namedFound.Count > 0;
        if (ResolveNamedMark(match, candidate, start, trimmedAbs) is not { } placement)
            return;
        var (time, markSilence, markRegion) = placement;
        var markCtx = new MarkContext(
            _ctx.File, _ctx.Info.InputDecoder, match.Phrase.Pattern,
            _ctx.AllSilences, _ctx.SpeechSegments, new TranscriptWindow(trimmedAbs, start, windowEnd),
            _language.Profile.Language);
        // The prologue and epilogue must sit behind a real pause, in every pass rather than only in
        // the late ones a bare number's check is reserved for. They are cheap to guard - the check
        // is Analyze geometry, no decoding - and they need it most: nothing bounds where they may
        // fall the way the chapter sequence bounds a number, and at most one of each exists per
        // book, so a false match does not merely add a mark, it replaces the real one.
        if (await _env.Marks.PlaceAsync(
                null, time, phraseAbs, start + match.PhraseEndSeconds, markSilence, markRegion,
                markCtx, NamedIsolationFor(match, phraseAbs), ct) is not { } placed)
            return;
        time = placed.TimeSeconds;

        // Second dedupe pass, now against the placed time. See NamedMarkLedger.AlreadyPlacedAt for
        // why the phrase-time pass above does not already cover this. Confirmed on
        // "Die Dritte Macht.m4b" 2026-07-28, where it produced four duplicate pairs (among them
        // "Kapitel 6" and "Kapitel 7", the same announcement heard two ways, both at 2:46:06.53).
        // Costs the placement work of the loser, which only a re-heard mark pays.
        if (_named.AlreadyPlacedAt(match.Phrase.Kind, time))
            return;

        if (teachesThreshold)
        {
            ProposeThreshold(ThresholdSilenceFor(candidate, markSilence));
            AdoptProposedThreshold($"\"{match.Title}\"");
        }
        _named.Add(match, time, phraseAbs);
        _ctx.Work.NamedMarks = _namedFound.Count;
        _ctx.Work.ExtraMarks = _namedFound.Count(m => m.Kind != ChapterKind);
        _env.Log?.Invoke($"{match.Phrase.Kind} detected (\"{match.Title}\"), mark placed at " +
                         $"{FormatTimestamp(time)} (confidence {match.Confidence:0.00}" +
                         await _env.Marks.LoudnessNoteAsync(time, markCtx, ct) +
                         CandidateNote(candidate) +
                         $"){LowConfidenceNote(match.Confidence)}");
    }

    /// <summary>
    /// The isolation check for a named (prologue/epilogue/<c>--custom</c>) mark: what the wording
    /// that matched asked for with its <c>^</c> and <c>$</c>, plus
    /// <see cref="IsolationRule.LeadIn"/> for a phrase the profile flags as a heading, which is the
    /// two built-in ones and nothing else - a <c>--custom</c> mapping asks for the pause by writing
    /// a <c>^</c>, which arrives through the wording's own guards on the line above.
    /// <para>
    /// The phrase-level flag is a floor rather than a synonym for a written <c>^</c>, deliberately:
    /// it is what a prologue <em>is</em>, so <c>--prologue-phrase vorwort</c> keeps the guard that
    /// stopped Italian "riepilogo" from replacing a real epilogue mark (2026-08-05) instead of
    /// silently giving it up. See <see cref="NamedPhrase.RequiresLeadIn"/>.
    /// </para>
    /// </summary>
    /// <param name="match">The match being placed.</param>
    /// <param name="phraseAbs">Absolute start of the segment it was found in - the position to
    /// measure at when no refinement onset is available, which for a heading word opening its own
    /// segment is the announcement itself.</param>
    internal static IsolationCheck NamedIsolationFor(NamedMatch match, double phraseAbs)
    {
        // A match the recognizer set off by itself - as a segment of its own, or behind punctuation
        // and a space - has answered the lead-in question already. Through the shared helper so a
        // named mark and a chapter cannot come to different answers about one `^`. The Italian
        // "riepilogo" this guard was built for is unaffected either way, having matched inside a
        // word in the middle of a sentence, with neither a segment start nor a space in front of it.
        var rule = AnnouncementIsolation.WithoutSatisfiedLeadIn(
            match.Guards | (match.Phrase.RequiresLeadIn ? IsolationRule.LeadIn : IsolationRule.None),
            match.OpensSegment || match.FollowsPunctuation);
        return rule == IsolationCheck.None.Rule
            ? IsolationCheck.None
            : new IsolationCheck(rule, phraseAbs);
    }

    /// <summary>Which <c>--chapter-phrase none</c> reading a pass gets, from the one flag that also
    /// decides whether <see cref="AnnouncementIsolation"/> vets the result; see
    /// <see cref="WideBareNumberReading"/>. Shared with <see cref="ChapterDetector"/>'s own passes so
    /// the two spell the pairing the same way.</summary>
    /// <param name="wide">Whether this pass hunts known numbers inside a bounded stretch.</param>
    internal static BareNumberReading BareNumberReadingFor(bool wide)
        => wide ? BareNumberReading.LeadingASentence : BareNumberReading.SpokenAloneAtSegmentStart;

    /// <summary>Whether an in-scope named match is to be passed over without becoming a mark; the
    /// rule and its four reasons are <see cref="NamedMarkLedger.ShouldDrop"/>, shared with
    /// Scan.</summary>
    /// <param name="phrase">The phrase that matched.</param>
    /// <param name="phraseAbs">Absolute time the announcement was heard at.</param>
    private bool ShouldDropNamedMatch(NamedPhrase phrase, double phraseAbs)
        => _named.ShouldDrop(phrase, phraseAbs, _language.Profile, _env.Log);

    /// <summary>
    /// The default-mode mark for a named match - the same <see cref="ResolveAnnouncementMark"/> a
    /// numbered one goes through, rejection rules included, since a prologue, an epilogue and a
    /// <c>--custom</c> phrase are announcements in exactly the sense a chapter phrase is.
    /// </summary>
    /// <param name="match">The named match, in window-relative time.</param>
    /// <param name="candidate">The candidate whose window this probe decoded.</param>
    /// <param name="start">Absolute start of that window.</param>
    /// <param name="trimmedAbs">The window's transcript in absolute file time.</param>
    private (double Time, Silence? MarkSilence, NonSpeechRegion? MarkRegion)? ResolveNamedMark(
        NamedMatch match, ProbeCandidate candidate, double start, List<TranscriptSegment> trimmedAbs)
        => ResolveAnnouncementMark(
            match.PhraseStartSeconds, match.PhraseEndSeconds, candidate, start, trimmedAbs,
            $"{match.Phrase.Kind} \"{match.Title}\"");

    /// <summary>
    /// Whether a phrase match is rejected on its number alone, before any mark placement is
    /// attempted. Either failure is logged rather than swallowed: the number was plainly heard, and
    /// without a line saying so a --verbose run gives no hint why it did not become a mark, which is
    /// indistinguishable from the phrase matcher having missed it. Neither ends the window - a real
    /// announcement later in the same window is still found.
    /// </summary>
    /// <param name="match">The phrase match to judge.</param>
    /// <param name="phraseAbs">Its absolute phrase start time, for the log line.</param>
    /// <param name="windowLast">The highest number accepted so far, in this window or before it.</param>
    private bool IsOutOfSequence(PhraseMatch match, double phraseAbs, int windowLast)
    {
        // A duplicate or regression: an in-text mention like "as seen in chapter three", the opening
        // of a part that counts from 1 again, or a re-detection of an already-marked chapter. Which
        // of the first two it is cannot be decided here - see TrackRestartAsync, which the caller
        // hands the strictly-below case to.
        if (match.Number <= windowLast)
        {
            _env.Log?.Invoke($"skipped chapter {match.Number} at {FormatTimestamp(phraseAbs)} - " +
                             $"not above last accepted {windowLast}" +
                             (match.Number < windowLast ? " (in-text mention?)" : ""));
            // Booked as lost only where nothing will watch it: a forward scan hands it to
            // TrackRestartAsync, which either turns it into a chapter or books it itself when the
            // run it was part of breaks down.
            if (match.Number < windowLast && !RestartTrackingAllowed)
                NoteOutOfSequence(match.Number);
            return true;
        }
        // A snapped window can, near a gap's own upper boundary, reach right up against the next
        // already-confirmed chapter's own announcement - reject a match at or above it outright so
        // gap recovery can never displace a chapter that is already in hand. Two kinds of gap set
        // this: a --verify region (never for a fresh run's whole-file region, whose UpperNumber is
        // always null) and a sequence-gap re-probe (see _gapAbove).
        if ((_gapAbove ?? _region.UpperNumber) is { } upperBound && match.Number >= upperBound)
        {
            _env.Log?.Invoke($"skipped chapter {match.Number} at {FormatTimestamp(phraseAbs)} - " +
                             $"at or above {upperBound}, this gap's upper bound");
            return true;
        }
        return false;
    }

    /// <summary>
    /// Whether this pass may conclude that the file's chapter numbering restarts. Only an open-ended
    /// forward scan may: a pass hunting known numbers inside a hole the sequence closes from above
    /// is not reading the book forward at all, and every number it hears below its floor is an
    /// in-text mention by construction - the part boundary it would be looking for cannot be inside
    /// a stretch bounded by two chapters of one numbering.
    /// <para>
    /// The same expression as <see cref="WideBareNumberReading"/>, inverted, and for the same
    /// underlying reason: both ask whether this window knows in advance which numbers may appear.
    /// </para>
    /// </summary>
    private bool RestartTrackingAllowed => (_gapAbove ?? _region.UpperNumber) is null;

    /// <summary>
    /// Holds one announcement back as a possible new part, and confirms the restart once enough of
    /// them have accumulated (<see cref="SequenceRestartRunLength"/>).
    /// <para>
    /// A number that does not continue the run being tracked starts a fresh one, abandoning what was
    /// held: the run has to be strictly consecutive, because "chapter one, chapter two, chapter
    /// three" spoken in order after the sequence has passed them is the shape a book divided into
    /// parts has and scattered in-text mentions do not. That strictness is also what bounds the cost
    /// of being wrong - the calibration in <see cref="SequenceRestartRunLength"/> found thirteen of
    /// fourteen corpus books producing no below-sequence announcement at all.
    /// </para>
    /// <para>
    /// Nothing is placed while a run is pending, so an in-text mention costs a log line rather than
    /// a mark refinement. The price is that a confirmed part's opening chapters are placed late,
    /// out of the window they were heard in - which is why <see cref="PendingRestart"/> carries that
    /// window's transcript along.
    /// </para>
    /// </summary>
    /// <param name="match">The phrase match, in window-relative time.</param>
    /// <param name="candidate">The candidate whose window this probe decoded.</param>
    /// <param name="start">Absolute start of that window.</param>
    /// <param name="windowEnd">Absolute planned end of that window.</param>
    /// <param name="phraseAbs">Absolute phrase start time.</param>
    /// <param name="trimmedAbs">That window's transcript in absolute file time.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The marks a confirmed restart placed, empty while the run is still pending.</returns>
    private async Task<List<ProbeMark>> TrackRestartAsync(
        PhraseMatch match, ProbeCandidate candidate, double start, double windowEnd, double phraseAbs,
        List<TranscriptSegment> trimmedAbs, CancellationToken ct)
    {
        if (!ContinuesPendingRestart(match))
            AbandonPendingRestart();
        _pendingRestart.Add(
            new PendingRestart(match, candidate, start, windowEnd, phraseAbs, trimmedAbs));
        _env.Log?.Invoke(
            $"chapter {match.Number} at {FormatTimestamp(phraseAbs)} held back as a possible new " +
            $"part ({_pendingRestart.Count} of {SequenceRestartRunLength} consecutive)");
        return _pendingRestart.Count >= SequenceRestartRunLength ? await CommitRestartAsync(ct) : [];
    }

    /// <summary>
    /// Accepts a tracked restart: opens the next chapter sequence and places the announcements held
    /// back for it, in the order they were heard.
    /// <para>
    /// <see cref="_lastNumber"/> is cleared first, so the held chapters are judged against the new
    /// part's own numbering and not against the one they sit below - and so the caller's
    /// <c>RecordMarks</c> cannot read the drop from part 1's chapter 15 to part 2's chapter 1 as a
    /// sequence gap and send a re-probe over the whole of part 1 after it.
    /// </para>
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The marks placed for the new part's opening chapters.</returns>
    private async Task<List<ProbeMark>> CommitRestartAsync(CancellationToken ct)
    {
        var opening = _pendingRestart.ToList();
        _pendingRestart.Clear();
        _sequence++;
        _lastNumber = null;
        _env.Log?.Invoke(
            $"the chapter numbering restarts at {FormatTimestamp(opening[0].PhraseAbs)} - chapters " +
            $"{string.Join(", ", opening.Select(p => p.Match.Number))} were announced in order " +
            $"below the sequence, so this file holds a further part. Every chapter is written with " +
            "its part from here on.");

        // The held chapters are placed out of order with the walk, so the "stretch worth a second
        // look" this normally accumulates points back at the previous part. Moved onto the new
        // part's opening candidate, so that a chapter missing between the held ones - a placement
        // that failed, the only way a consecutive run can come out with a hole - re-probes the few
        // minutes it can be in rather than the whole of the part before it.
        _lastMarkExpectAt = opening[0].Candidate.ExpectAt;

        var marks = new List<ProbeMark>();
        foreach (var held in opening)
        {
            if (await AcceptMatchAsync(
                    held.Match, held.Candidate, held.Start, held.WindowEnd, held.PhraseAbs,
                    held.TranscriptAbs, _lastNumber ?? 0, ct) is not { } mark)
                continue;
            marks.Add(mark);
            AdvanceLastNumber(mark, mark.Number);
        }
        return marks;
    }

    /// <summary>Gives up on a restart being tracked, booking its announcements as lost. Called when
    /// a chapter of the sequence in force is accepted (a part that had really started would have no
    /// more chapters of the old numbering to announce), when a below-sequence number breaks the run,
    /// and when the region ends with a run still open.</summary>
    private void AbandonPendingRestart()
    {
        foreach (var held in _pendingRestart)
            NoteOutOfSequence(held.Match.Number);
        _pendingRestart.Clear();
    }

    /// <summary>
    /// Records one announcement given up on for sitting strictly below the sequence, and says so in
    /// the log the first time enough of them have accumulated to look like a numbering this run is
    /// failing to follow rather than like prose mentioning an earlier chapter.
    /// <para>
    /// The two are told apart by shape, not by count. A book divided into parts announces "chapter
    /// one", "chapter two", "chapter three" again after its last accepted chapter, so the rejected
    /// numbers climb; an in-text mention is one number at one position, and several of them are
    /// scattered rather than ordered. Hence the ascending-run test, which
    /// <see cref="SequenceRestartRunLength"/> carries the corpus measurement behind.
    /// </para>
    /// <para>
    /// What reaches here is now the residue rather than the whole story: an ascending run long
    /// enough to be believed is taken as a restart and marked (see <see cref="TrackRestartAsync"/>),
    /// so the announcements booked here are the ones a run <em>almost</em> formed - a part whose
    /// opening chapters were not all heard, or a stretch where the old numbering kept producing
    /// chapters in between. Worth reporting for exactly that reason: it names the one shape this
    /// tool still cannot follow, and <c>--ignore-chapter-numbers</c> - which marks every
    /// announcement it hears and never consults a number - remains the answer for it.
    /// </para>
    /// </summary>
    /// <param name="number">The rejected announcement's own chapter number.</param>
    private void NoteOutOfSequence(int number)
    {
        _belowSequenceNumbers.Add(number);
        if (_restartReported || SequenceRestartSkips == 0)
            return;
        _restartReported = true;
        _env.Log?.Invoke(
            "WARNING - announcements below the sequence are being heard in ascending runs, which is " +
            "what a book divided into parts looks like - but not enough consecutive ones to confirm " +
            "a new part. --ignore-chapter-numbers marks every announcement regardless of its number.");
    }

    /// <summary>
    /// The length of the longest strictly ascending run in <paramref name="numbers"/>, ignoring
    /// immediate repeats. The repeats matter: one announcement is routinely rejected more than once
    /// - by the window that found it and again by an overlapping or re-probed one - and counting
    /// those as a break would hide the very run being looked for.
    /// </summary>
    /// <param name="numbers">The rejected numbers, in the order they were heard.</param>
    private static int LongestAscendingRun(IReadOnlyList<int> numbers)
    {
        var best = 0;
        var run = 0;
        int? previous = null;
        foreach (var number in numbers)
        {
            if (number == previous)
                continue;
            run = previous is { } p && number > p ? run + 1 : 1;
            previous = number;
            best = Math.Max(best, run);
        }
        return best;
    }

    /// <summary>
    /// Turns one in-sequence phrase match into a placed, logged and recorded chapter mark, or
    /// rejects it for want of a qualifying anchor (see <see cref="ResolveProbeMark"/>, which logs
    /// why). An accepted mark is appended to the accumulator here, not by the caller.
    /// </summary>
    /// <param name="match">The phrase match, in window-relative time.</param>
    /// <param name="candidate">The candidate whose window this probe decoded.</param>
    /// <param name="start">Absolute start of that window.</param>
    /// <param name="windowEnd">Absolute planned end of the window - what precise marking
    /// anchors its search against (see <see cref="MarkContext.Transcript"/>).</param>
    /// <param name="phraseAbs">Absolute phrase start time.</param>
    /// <param name="trimmedAbs">The window's transcript in absolute file time.</param>
    /// <param name="windowLast">The highest number accepted so far, in this window or before it -
    /// the sequence position the refinement's own re-read of the number is held to
    /// (<see cref="SequenceBounds"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<ProbeMark?> AcceptMatchAsync(
        PhraseMatch match, ProbeCandidate candidate, double start, double windowEnd, double phraseAbs,
        List<TranscriptSegment> trimmedAbs, int windowLast, CancellationToken ct)
    {
        if (ResolveProbeMark(match, candidate, start, trimmedAbs) is not { } placement)
            return null;
        var (time, markSilence, markRegion) = placement;

        var reading = BareNumberReadingFor(WideBareNumberReading);
        // Built before the context, because the refinement's own matcher is held to the same
        // sequence bounds the number re-read is (see NumberCheck.AdmitsAsAnnouncement).
        var check = new NumberCheck(
            _sequence, match.Number, _language.Profile, SequenceBounds(windowLast),
            CollidingChapterNumber(time, _found, _sequence));
        var markCtx = new MarkContext(
            _ctx.File, _ctx.Info.InputDecoder,
            _language.Profile.AnnouncementFor(match.Wording, reading, check.AdmitsAsAnnouncement),
            _ctx.AllSilences, _ctx.SpeechSegments, new TranscriptWindow(trimmedAbs, start, windowEnd),
            _language.Profile.Language);
        if (await _env.Marks.PlaceAsync(
                check,
                time, phraseAbs, start + match.PhraseEndSeconds, markSilence, markRegion, markCtx,
                AnnouncementIsolation.ForChapter(match, phraseAbs, WideBareNumberReading),
                ct) is not { } placed)
            return null;
        // Placement may have re-read the number out of the refinement's own probes, so everything
        // below reports and records what those settled on rather than what the window heard.
        time = placed.TimeSeconds;
        var number = placed.Number!.Value;
        // The refinement's own readings where it produced any, the window's figure otherwise; see
        // RefinedConfidence for why the window's is the worst-framed look at the announcement.
        var confidence = placed.Confidence ?? match.Confidence;

        if (match.SpansMerge)
            _env.Log?.Invoke($"chapter {number} spans the reused/fresh transcript merge " +
                             "- worth a spot check");

        // Everything reaching this point is already above the sequence and below whatever bounds it
        // from the far side, so the one way Admits can still say no is the implausible-hole case -
        // exactly what SuspectNumberMender was asked about a few lines up and could not mend.
        // Re-derived from the number placement settled on rather than from the one the window heard,
        // since the refinement vote may have replaced it with one that does fit. Recorded on the
        // mark rather than acted on here; see DetectedChapter.NumberUnverified for what it costs.
        var unverified = _ctx.SecondGuessNumbers && !SequenceBounds(windowLast).Admits(number);
        if (unverified)
            _env.Log?.Invoke(
                $"chapter {number} still does not fit the sequence after re-reading - mark kept, " +
                "chapters under it not counted as missing");

        _found.Add(new DetectedChapter(number, time, confidence, unverified, _sequence));
        // Through ExpectedStartFor rather than off the option, so the "still missing" note starts
        // counting the chapters under the first one found the moment a prologue says this file
        // holds the book's beginning - which the progress display would otherwise only learn of
        // once Probe was over and gap planning took the same view.
        var (highest, missingNumbers) = ChapterProgress(_found, ExpectedStartFor(_env.Options, _namedFound));
        _ctx.Work.HighestChapters = highest;
        _ctx.Work.MissingChapters = missingNumbers.Count;
        _env.Log?.Invoke($"chapter {number} detected, mark placed at {FormatTimestamp(time)} " +
                         $"(confidence {confidence:0.00}" +
                         await _env.Marks.LoudnessNoteAsync(time, markCtx, ct) +
                         CandidateNote(candidate) +
                         $"){LowConfidenceNote(confidence)}" +
                         MissingNote(missingNumbers));

        _hunting?.Remove(number);
        return new ProbeMark(
            number, ThresholdSilenceFor(candidate, markSilence), confidence, unverified);
    }

    /// <summary>
    /// Resolves where one phrase match found in a probe window puts its default-mode mark, and
    /// which silence/jingle region that mark anchors to. The anchors are reported for the auto
    /// mechanisms and statistics regardless of --mark-before-jingle - only what
    /// <see cref="MarkPlacer"/> subsequently does with the mark depends on that option.
    /// <para>
    /// With the VAD pre-pass, the anchor is the VAD jingle region ending at the phrase, not
    /// whichever silence triggered this probe: a false in-text pause earlier in the previous chapter
    /// does not lead that region, so it must not become the anchor (which would mark at the pause
    /// and feed the auto mechanisms a bogus jingle length) - see
    /// <see cref="JingleGeometry.ResolveJingleAnchor"/>. The candidate's own VAD region is used
    /// directly only when this phrase is plausibly attached to it; a second announcement further
    /// along the window belongs to a different transition and must re-derive its own anchor. When
    /// neither a region nor a closer silence is found, this probe's own triggering silence is the
    /// fallback.
    /// </para>
    /// <para>
    /// Without it, the mark always goes <see cref="MarkLead"/> before the phrase
    /// itself, regardless of what precedes it. A phrase directly following the triggering silence
    /// (the classic shape) anchors to that silence. One deeper in the window than the timing rule
    /// allows can still be accepted right away, without waiting for a later candidate's window, but
    /// only when a candidate-grade silence directly precedes it: within the same
    /// <see cref="PhraseLatestStartSeconds"/> seconds the classic rule grants, and at least
    /// --min-silence-length long, so a breath pause before an in-text mention ("Chapter eight had
    /// been hard.") cannot qualify as an anchor.
    /// </para>
    /// </summary>
    /// <param name="match">The phrase match, in window-relative time.</param>
    /// <param name="candidate">The candidate whose window this probe decoded.</param>
    /// <param name="start">Absolute start of that window.</param>
    /// <param name="trimmedAbs">The window's transcript in absolute file time, for the VAD edge
    /// adjustment inside <see cref="JingleGeometry.ResolveJingleAnchor"/>.</param>
    /// <returns>The default-mode mark and its anchors, or null when the match has no qualifying
    /// anchor at all and must be rejected - see <see cref="RejectProbeMark"/>, which logs why.</returns>
    private (double Time, Silence? MarkSilence, NonSpeechRegion? MarkRegion)? ResolveProbeMark(
        PhraseMatch match, ProbeCandidate candidate, double start, List<TranscriptSegment> trimmedAbs)
        => ResolveAnnouncementMark(
            match.PhraseStartSeconds, match.PhraseEndSeconds, candidate, start, trimmedAbs,
            $"chapter {match.Number}");

    /// <summary>
    /// Places a mark for any announcement, numbered or named, and applies the rejection rules that
    /// separate a real announcement from an in-text mention of the same words.
    /// </summary>
    /// <remarks>
    /// Named phrases (prologue, epilogue, <c>--custom</c>) used to skip the rejection rules, on the
    /// grounds that they have no chapter-number sequence for a spurious mark to corrupt. That
    /// reasoning covered the wrong risk: the rules exist to decide whether the words were
    /// <em>announced</em> at all, which matters just as much for a mark nothing else depends on -
    /// a book whose narration happens to mention "Zeittafel" mid-sentence should no more get a mark
    /// there than one mentioning "chapter eight" should. Unified 2026-07-29 at the user's request:
    /// a named phrase is an announcement or it is nothing.
    /// <para>
    /// Note what this does <em>not</em> reach: with a VAD pre-pass - the default - the rules below
    /// never run for either kind, because the VAD path returns first and places every match it is
    /// given - and since 0.12.0 the pre-pass always runs, so nothing below this is reachable in a
    /// production run at all. What still exercises it is the detector's own tests, which construct
    /// one without a VAD. Unifying the two therefore changed no shipped behaviour.
    /// </para>
    /// </remarks>
    /// <param name="phraseStartSeconds">Phrase start, relative to the window start.</param>
    /// <param name="phraseEndSeconds">End of the segment the phrase was found in, same time base.</param>
    /// <param name="candidate">The candidate whose window this probe decoded.</param>
    /// <param name="start">Absolute start of that window.</param>
    /// <param name="trimmedAbs">The window's transcript in absolute file time, for the VAD edge
    /// adjustment inside <see cref="JingleGeometry.ResolveJingleAnchor"/>.</param>
    /// <param name="what">How to name this announcement in a rejection log line, e.g.
    /// <c>chapter 8</c> or <c>custom mark "Zeittafel"</c>.</param>
    private (double Time, Silence? MarkSilence, NonSpeechRegion? MarkRegion)? ResolveAnnouncementMark(
        double phraseStartSeconds, double phraseEndSeconds, ProbeCandidate candidate, double start,
        List<TranscriptSegment> trimmedAbs, string what)
    {
        var phraseAbs = start + phraseStartSeconds;
        if (_env.Vad != null)
        {
            var candidateRegion = candidate.VadRegion is { } cvr &&
                phraseAbs >= cvr.StartSeconds - JinglePhraseMatchToleranceSeconds &&
                phraseAbs <= cvr.EndSeconds + JinglePhraseMatchToleranceSeconds
                ? candidate.VadRegion : null;
            var (leadingSilence, markRegion) = ResolveJingleAnchor(
                phraseAbs, start + phraseEndSeconds, start, _ctx.AllSilences,
                _ctx.NonSpeechRegions, candidateRegion, _ctx.SpeechSegments, trimmedAbs);
            var time = RefineDefaultMark(
                Math.Max(0, ResolveDefaultPhraseOnset(phraseAbs, markRegion, leadingSilence, _ctx.SpeechSegments)
                            - MarkLead),
                _ctx.SpeechSegments, MarkLead);
            // The candidate's own silence stands in for the statistics where neither a jingle nor a
            // silence leading one was found. It is deliberately not offered to the onset resolution
            // above, which asks where *this region's* music begins and would be misled by a silence
            // belonging to no region at all.
            var markSilence = leadingSilence ?? (markRegion == null ? candidate.Silence : null);
            return (time, markSilence, markRegion);
        }

        // Measured from where this candidate expects its announcement rather than from where its
        // window happens to open, which are the same point for every unclassified candidate and for
        // a plain-pause one. They part company where the window carries a lead-in, and where the
        // expectation sits past a jingle - and a phrase *before* the expected point passes too,
        // deliberately: on a jingle whose window spans the music that is the announcement being
        // spoken over it, which is the whole reason that window is shaped the way it is.
        if (phraseAbs - candidate.ExpectAt <= PhraseLatestStartSeconds)
            return (Math.Max(0, phraseAbs - MarkLead), candidate.Silence, null);

        // Each of the three ways this can fail is named separately rather than folded into one
        // rejection: two of them point straight at a --min-silence-length that is too strict for
        // this book, which is exactly what someone chasing a missing chapter needs to be told.
        if (FindRealAnchorSilence(start, phraseAbs, _ctx.AllSilences) is not { } anchor)
            return RejectProbeMark(what, phraseAbs, "no silence precedes it inside the probe window");
        if (phraseAbs - anchor.EndSeconds > PhraseLatestStartSeconds)
            return RejectProbeMark(what, phraseAbs,
                $"the nearest silence ends {phraseAbs - anchor.EndSeconds:0.0} s before it, " +
                $"more than the {PhraseLatestStartSeconds:0.#} s allowed");
        if (anchor.EndSeconds - anchor.StartSeconds < _env.Options.MinSilenceSeconds)
            return RejectProbeMark(what, phraseAbs,
                $"the silence before it is only {anchor.EndSeconds - anchor.StartSeconds:0.00} s long, " +
                $"below --min-silence-length {_env.Options.MinSilenceSeconds:0.##} s");
        return (Math.Max(0, phraseAbs - MarkLead), anchor, null);
    }

    /// <summary>Logs why <see cref="ResolveAnnouncementMark"/> is dropping an announcement the
    /// recognizer did hear, and returns the null that stands for "no mark". A missing mark is far
    /// easier to chase when the log distinguishes "never heard" from "heard, but unanchorable,
    /// because &lt;reason&gt;".</summary>
    /// <param name="what">The rejected announcement, as named for the log line.</param>
    /// <param name="phraseAbs">Absolute phrase start time, for the log line's timestamp.</param>
    /// <param name="reason">Why no mark could be placed, phrased to follow "skipped X at T - ".</param>
    private (double Time, Silence? MarkSilence, NonSpeechRegion? MarkRegion)? RejectProbeMark(
        string what, double phraseAbs, string reason)
    {
        _env.Log?.Invoke($"skipped {what} at {FormatTimestamp(phraseAbs)} - {reason}");
        return null;
    }

    /// <summary>
    /// Applies everything one probe's marks change about the region's running state: a sequence gap
    /// triggers the re-probe of everything skipped since the last mark, each mark's anchor silence
    /// may tighten the --min-silence-length auto threshold, and the last accepted number advances.
    /// The first and the last of those are a mark's number speaking for the sequence, so both are
    /// skipped for one the sequence could not hold (see <see cref="ProbeMark.NumberUnverified"/>);
    /// the tightening is about its silence and happens either way.
    /// <para>
    /// The order matters and is the order Probe resumes on: a gap re-probe runs first, so the
    /// threshold this mark then adopts and the jingle window the re-probe restores both already
    /// account for whatever the recovered chapters taught them. Where the candidate loop picks up
    /// afterwards needs no arranging - the re-probe walks its own copy of the sequence and leaves the
    /// loop index alone, so probing continues at the candidate after this mark's own window, past
    /// every position the re-probe just revisited.
    /// </para>
    /// </summary>
    /// <param name="probeMarks">The marks the probe produced, in window order.</param>
    /// <param name="expectAt">Where the candidate that produced them expected its announcement, i.e.
    /// the far end of any stretch a gap among them opens.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ApplyProbeMarksAsync(List<ProbeMark> probeMarks, double expectAt, CancellationToken ct)
    {
        foreach (var mark in probeMarks)
        {
            // The gap re-probe runs regardless of --min-silence-length mode: with the
            // overlap-sequence skip, candidates can be skipped even with an explicit threshold, and
            // a sequence gap is the signal that one of them hid a chapter.
            //
            // Except on a jingle-only walk, where a hole between two jingle-found chapters is not a
            // failure to be recovered from but the ordinary result of having deferred every pause:
            // the walk that follows re-reads exactly those stretches, and it does so in the primary
            // scan's own framing rather than a recovery pass's trimmed one, which is what a first
            // look at that audio is owed (see RecoveryLeadInTrimSeconds for why a second look is
            // framed differently at all). The descending shape keeps it: a hole there is a genuine
            // hole, its candidates having been passed over on a reading that said a chapter cannot
            // be in them, and this is what re-opens the stretch when that reading was wrong.
            if (_shape != ProbeShape.JinglesOnly && !mark.NumberUnverified &&
                _lastNumber is { } previousNumber && mark.Number > previousNumber + 1)
                await HandleSequenceGapAsync(previousNumber, mark.Number, expectAt, ct);

            if (_env.Options.AutoMinSilence && !_sweeping)
                TightenThreshold(mark);
            AdvanceLastNumber(mark, mark.Number);
        }
    }

    /// <summary>
    /// Moves <see cref="_lastNumber"/> to <paramref name="number"/>, unless that would let a number
    /// nothing corroborated displace one something did.
    /// <para>
    /// An uncorroborated number is not simply ignored here. Where the region has no floor yet it is
    /// the only evidence there is, and a floor set from weak evidence still catches what a missing
    /// floor cannot: a file opening at chapter 5 leaves an implausible hole and so arrives
    /// uncorroborated, and it is exactly that 5 that makes the next window's "chapter two" worth
    /// re-reading rather than worth believing. What such a number must not do is overrule the strong
    /// kind - see <see cref="ProbeMark.NumberUnverified"/> for what that cost on the case it was
    /// written for.
    /// </para>
    /// </summary>
    /// <param name="mark">The mark just accepted.</param>
    /// <param name="number">What the floor would become: the mark's own number, or the running
    /// maximum where a re-detection from outside the stretch being re-probed must not pull it back
    /// down.</param>
    private void AdvanceLastNumber(ProbeMark mark, int number)
    {
        if (!mark.NumberUnverified || _lastNumber is null)
            _lastNumber = number;
    }

    /// <summary>
    /// Reacts to the chapter numbers just found leaving a gap: the stretch between the two marks
    /// bracketing it gets a second, unconditional look before the region moves on. Nothing to
    /// re-probe is a routine outcome and is logged as such rather than passed over in silence - the
    /// log then distinguishes "Probe declined a candidate" from "Probe never had one", which is
    /// the first thing worth knowing when a chapter goes missing.
    /// <para>
    /// The stretch is named by the two announcements, not by what the walk happened to look at
    /// between them. It used to be the latter - the candidates skipped and probed since the last
    /// mark, replayed - which cannot express the case this pass most needs: a chapter behind a pause
    /// the candidate list never held at all, which is exactly what a list cut at --min-silence-length
    /// leaves out (see <see cref="_subFloorSeconds"/>). Bounds by announcement rather than by window
    /// start because that is what <see cref="CandidatesIn"/> selects on, open at the lower end so the
    /// window that produced the last mark is not read again for a chapter already accepted, and two
    /// seconds past the upper one to clear that method's own "not in the last second" guard.
    /// </para>
    /// </summary>
    /// <param name="previousNumber">The chapter number below the gap.</param>
    /// <param name="number">The chapter number above it, i.e. the mark that revealed the gap.</param>
    /// <param name="expectAt">Where the mark that revealed it expected its announcement.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task HandleSequenceGapAsync(
        int previousNumber, int number, double expectAt, CancellationToken ct)
    {
        await ReprobeGapCandidatesAsync(
            _lastMarkExpectAt ?? _region.FromSeconds, expectAt,
            $"sequence gap {previousNumber}-{number}: ",
            previousNumber, number, ct);
    }

    /// <summary>
    /// Re-probes, unconditionally and in recovery framing, the stretch a sequence gap has put back in
    /// question: everything Probe has looked at since the last mark, rebuilt from scratch as a
    /// recovery candidate list (see <see cref="_recovery"/>) rather than replayed from the candidates
    /// that were passed over. Rebuilding is what makes it the <em>union</em> set - the silences the
    /// primary scan suppressed inside a jingle's span are back, and every jingle's music is read
    /// where its speech window comes up empty - and the trimmed framing is what makes it a second
    /// look rather than a repetition. They form their own little window sequence, each end computed
    /// on the fly against its next neighbor in it, so adjacent re-probe windows get snapped shared
    /// borders too.
    /// <para>
    /// Stops the moment the gap is closed rather than walking the rest of the sequence: the
    /// candidates behind the recovered chapter cover the same audio and have nothing left to find,
    /// and each of them would pay for a full mark placement (the refinement alone costs tens of
    /// seconds) to arrive at a mark that is then dropped as a duplicate.
    /// </para>
    /// <para>
    /// Every accepted gap mark advances <see cref="_lastNumber"/>, which is what makes that
    /// termination merely an optimisation rather than the fix: a later window overlapping the same
    /// announcement must no longer accept it, and it cannot once the number is no longer above the
    /// floor. The floor still starts at <paramref name="previousNumber"/> so the in-between numbers
    /// are acceptable at all, and raising it as they are found costs nothing - candidates run in
    /// chronological order and chapter numbers ascend with time, so no later window can hold a
    /// number the raised floor now rejects.
    /// </para>
    /// </summary>
    /// <param name="fromSeconds">Start of the stretch to re-probe.</param>
    /// <param name="toSeconds">End of that stretch.</param>
    /// <param name="note">The log line's opening, naming the gap that triggered this.</param>
    /// <param name="previousNumber">The chapter number below the gap.</param>
    /// <param name="number">The chapter number above it, i.e. the mark that revealed the gap.</param>
/// <param name="ct">Cancellation token.</param>
/// <remarks>Notes: the duplicate marks that made stopping at the closed gap worth doing.
/// <include file='../../notes/Detection/RegionProber.xml' path='doc/member[@name="ReprobeGapCandidatesAsync"]/*' /></remarks>
    private async Task ReprobeGapCandidatesAsync(
        double fromSeconds, double toSeconds, string note, int previousNumber, int number,
        CancellationToken ct)
    {
        // Before the candidates are built: the recovery framing and the union candidate set are the
        // same flag, and both belong to this list.
        Reprobing = (fromSeconds, toSeconds);
        _gapAbove = number;
        _subFloorSeconds = SubFloorForReprobe();
        var candidates = ReprobeCandidates(fromSeconds, toSeconds);
        if (_subFloorSeconds is { } floor && !CanAfford(candidates.Count, toSeconds - fromSeconds))
        {
            // Cheaper to find out here than to decode it: the enriched list is built, priced and
            // thrown away, since building candidates costs nothing and probing them is the whole
            // expense. What is left is the list this re-probe would have had anyway.
            _env.Log?.Invoke(
                note + $"not reaching below {floor:0.0#} s - {candidates.Count} candidate(s) " +
                $"over the {GapProbeBudget(toSeconds - fromSeconds):0.#} decode window(s) budget");
            _subFloorSeconds = null;
            candidates = ReprobeCandidates(fromSeconds, toSeconds);
        }
        if (candidates.Count == 0)
        {
            _env.Log?.Invoke(note + "no candidates between the two marks - deferred to the gap scan");
            Reprobing = null;
            _gapAbove = null;
            return;
        }
        _env.Log?.Invoke(
            note + $"re-probing {candidates.Count} candidate(s), " +
            $"{FormatTimestamp(fromSeconds)}-{FormatTimestamp(toSeconds)}");
        var missing = Enumerable.Range(previousNumber + 1, number - previousNumber - 1).ToHashSet();
        _withheldFromScanSeconds = null;
        for (var si = 0; si < candidates.Count; si++)
        {
            var gapMarks = await ProbeAsync(
                candidates[si], new WindowPlan(candidates, si, WindowEndFor(candidates, si)), ct);
            foreach (var gapMark in gapMarks)
            {
                AdvanceLastNumber(gapMark, Math.Max(_lastNumber ?? 0, gapMark.Number));
                // A gap mark recovered here may well have an anchor silence short enough to have
                // been skipped - fold it into the running minimum so the threshold can never again
                // sit above a silence proven to precede a chapter. One whose silence cleared the
                // threshold anyway cannot lower the running minimum, so the same call is a no-op for
                // it. Only genuine gap-fillers count either way; a re-detection of a chapter outside
                // this gap must not lower anything - and a mark recovered at a jingle brings nothing
                // to fold in at all (see ThresholdSilenceFor).
                if (!missing.Remove(gapMark.Number))
                    continue;
                if (_env.Options.AutoMinSilence)
                    ProposeThreshold(gapMark.ThresholdSilence);
            }
            if (missing.Count > 0)
                continue;
            if (si + 1 < candidates.Count)
                _env.Log?.Invoke($"gap before chapter {number} closed - stopped after " +
                                 $"{si + 1} of {candidates.Count} candidate(s)");
            break;
        }
        // Without this the evidence is invisible here: the scan gate no longer follows a gap recovery
        // down (see _scanFloorSeconds), so the "threshold lowered" line this used to produce is gone,
        // and what the break was measured at would next surface only in a sub-floor sweep thousands
        // of log lines later - or, on a book that needs no sweep, nowhere at all.
        if (_withheldFromScanSeconds is { } withheld)
            _env.Log?.Invoke(
                note + $"measured a {withheld:0.##} s chapter break - kept for the gap passes, " +
                "not applied to the forward scan");
        Reprobing = null;
        _gapAbove = null;
        _subFloorSeconds = null;
    }

    /// <summary>
    /// One sequence-gap re-probe's candidate list, in window order.
    /// </summary>
    /// <param name="fromSeconds">Start of the stretch, and the last mark's own announcement: a
    /// candidate expecting one there has nothing left to find, that chapter being accepted.</param>
    /// <param name="toSeconds">End of the stretch.</param>
    private List<ProbeCandidate> ReprobeCandidates(double fromSeconds, double toSeconds)
        => CandidatesIn(fromSeconds, toSeconds)
            .Where(c => c.ExpectAt > fromSeconds)
            .OrderBy(c => c.Start)
            .ToList();

    /// <summary>
    /// How short a pause a sequence-gap re-probe may build a candidate from, or null where that is
    /// no deeper than the list it already has.
    /// <para>
    /// The demand the run opened at bounds it, not the adapted threshold alone: with an explicit
    /// --min-silence-length the user has said what a chapter break is on this book, and nothing in
    /// Probe goes under that. Where the threshold has adapted <em>below</em> the demand - only
    /// possible under auto, and only after a mark measured a break that short - the book itself has
    /// said so, and this is the first thing in Probe able to act on it. The sub-floor sweeps still
    /// go lower afterwards, on their own budget and their own log lines.
    /// </para>
    /// <para>
    /// Sweeps are excluded: a sweep's candidates are a band handed to it from outside, deliberately
    /// narrow, and drawing this list instead would re-probe the long silences the primary scan
    /// already covered.
    /// </para>
    /// </summary>
    private double? SubFloorForReprobe()
    {
        if (_sweeping)
            return null;
        // Deliberately the evidence minimum rather than the scan gate: this is one of the two
        // gap-scoped mechanisms _scanFloorSeconds withholds a gap recovery from, and withholding it
        // here as well would leave a measured short break with nothing at all able to act on it.
        var measured = _adaptedThresholdSeconds ?? _env.Options.MinSilenceSeconds;
        var floor = Math.Min(measured, _env.Options.MinSilenceSeconds);
        return floor < _env.Options.MinSilenceSeconds ? floor : null;
    }

    /// <summary>
    /// Whether this many probes fit the budget one gap may spend (see
    /// <see cref="GapPlanning.GapProbeBudget"/>), counted in the decode windows a probe and a
    /// transcription can be compared in.
    /// </summary>
    /// <param name="candidates">How many candidates the re-probe would run.</param>
    /// <param name="gapSeconds">Length of the stretch being re-probed.</param>
    private static bool CanAfford(int candidates, double gapSeconds)
        => candidates * ChunkWindows(RecoveryProbeSeconds) <= GapProbeBudget(gapSeconds);

    /// <summary>
    /// Folds one accepted mark's <see cref="ProbeMark.ThresholdSilence"/> into the
    /// --min-silence-length auto threshold and announces an actual change.
    /// <see cref="_lastNumber"/> having a value means this is at least
    /// the second mark found, so that silence is a real inter-chapter break - not the
    /// intro-to-chapter-1 silence, which is routinely longer than that and would otherwise
    /// over-tighten the threshold from the very first mark.
    /// </summary>
    /// <param name="mark">The mark whose silence to fold in, if it brought one.</param>
    private void TightenThreshold(ProbeMark mark)
    {
        if (_lastNumber.HasValue)
            ProposeThreshold(mark.ThresholdSilence);
        AdoptProposedThreshold($"chapter {mark.Number}");
    }

    /// <summary>Makes whatever <see cref="ProposeThreshold"/> has accumulated the threshold actually
    /// used from here on, announcing a real change.</summary>
    /// <param name="after">What was just marked, for the log line.</param>
    private void AdoptProposedThreshold(string after)
    {
        // The first set can go either way from the starting demand; everything after it can only
        // ever be a lowering.
        var newThreshold = _scanFloorSeconds ?? _env.Options.MinSilenceSeconds;
        if (newThreshold != _threshold)
            _env.Log?.Invoke($"threshold {(newThreshold > _threshold ? "tightened" : "lowered")} " +
                             $"to {newThreshold:0.##} s after {after}");
        _threshold = newThreshold;
    }

    /// <summary>
    /// Folds one anchor silence's proposal into <see cref="_adaptedThresholdSeconds"/>, keeping the
    /// running minimum. Bounded below by <see cref="CliOptions.AdaptiveFloorSeconds"/>, not by the
    /// --min-silence-length the run opened at, so this can settle under the starting demand and
    /// thereby say something Probe could not otherwise know: that this book's chapter breaks are
    /// shorter than the default assumes. What acts on the part below the demand never does so
    /// through the candidate grid, which does not reach there and must not (see
    /// <see cref="ChapterDetector.SweepAdaptiveSubFloorAsync"/> for the measurement behind that):
    /// a sequence-gap re-probe builds its own list (<see cref="_subFloorSeconds"/>), and the sweeps
    /// build theirs. Does nothing when the mark brought no silence to teach from
    /// - it sat on a VAD region, or <see cref="ThresholdSilenceFor"/> withheld one.
    /// </summary>
    /// <remarks>Notes: what this prunes across the corpus, and the two relaxations measured and
    /// rejected.
    /// <include file='../../notes/Detection/RegionProber.xml' path='doc/member[@name="ProposeThreshold"]/*' /></remarks>
    /// <param name="thresholdSilence">The silence to learn from, or null to learn nothing.</param>
    private void ProposeThreshold(Silence? thresholdSilence)
    {
        if (thresholdSilence is not { } silence)
            return;
        var measured = AdaptiveTightenFactor * (silence.EndSeconds - silence.StartSeconds);
        var proposed = Math.Max(_env.Options.AdaptiveFloorSeconds, measured);
        _adaptedThresholdSeconds = Math.Min(_adaptedThresholdSeconds ?? proposed, proposed);

        // A gap recovery whose break lands at or under the floor is withheld from the forward scan's
        // own gate - see _scanFloorSeconds. One that lands above it is not: that is an ordinary
        // chapter break this book demonstrably has, and the scan needs to know. The evidence above is
        // kept either way, so both gap-scoped mechanisms still act on it regardless.
        if (_reprobing && measured <= _env.Options.AdaptiveFloorSeconds)
        {
            _withheldFromScanSeconds = proposed;
            return;
        }
        _scanFloorSeconds = Math.Min(_scanFloorSeconds ?? proposed, proposed);
    }

    /// <summary>
    /// What a mark may teach the --min-silence-length auto threshold: its own anchor silence, unless
    /// a jingle candidate found it, in which case nothing.
    /// <para>
    /// The threshold's whole job is to say how long this book's chapter-break <em>pauses</em> run,
    /// because that is what decides which pauses become candidates. A jingle candidate's mark
    /// anchors to the hush leading into the music (<see cref="JingleGeometry.ResolveJingleAnchor"/>),
    /// which is a different quantity entirely - it measures the transition's lead-in, not the break
    /// between two chapters - and feeding it in distorts the threshold in whichever direction that
    /// hush happens to differ. Both directions cost: the running minimum lowering puts pauses back
    /// on the grid that this book never announces a chapter after, and the very first proposal (the
    /// one allowed to raise it) can put the threshold above the real breaks and skip them outright.
    /// It also feeds <see cref="AdaptedThresholdSeconds"/>, which sizes Re-probe's sub-floor sweep,
    /// so a wrong reading here buys wrong bands there.
    /// </para>
    /// <para>
    /// Known consequence, accepted: a book whose every mark came from a jingle now measures no
    /// break at all, so <see cref="ChapterDetector.SweepAdaptiveSubFloorAsync"/> never fires for it
    /// - the sweep runs only where a measured break came in under --min-silence-length. That sweep
    /// used to be reachable on such a book whenever some jingle's hush happened to be short, which
    /// is not evidence about its pauses and so was never a reason to run. A chapter it would have
    /// caught (a mixed book, where some chapters open with a jingle and the missing ones sit behind
    /// a short pause) now falls through to the gap passes instead, which is slower but not blind.
    /// </para>
    /// <para>
    /// Applies to a recovery pass's VAD-region candidates too, although their candidate <em>lists</em>
    /// stay frozen at pre-classification behaviour: nothing there was leaning on the old proposals.
    /// The gap re-probe and the sub-floor sweep filter by no threshold at all - the one probes
    /// unconditionally, the other short-circuits <see cref="ShouldSkipCandidate"/> - and Re-probe
    /// runs a fresh prober, so it starts at the run's own --min-silence-length and never sees the
    /// value Probe adapted. The only thing this changes for them is that Re-probe no longer
    /// tightens its own threshold off a jingle, i.e. probes a few more of its own pauses.
    /// </para>
    /// </summary>
    /// <param name="candidate">The candidate whose window produced the mark.</param>
    /// <param name="markSilence">The silence the mark actually anchored to, or null.</param>
    private static Silence? ThresholdSilenceFor(ProbeCandidate candidate, Silence? markSilence)
        => candidate.IsJingle ? null : markSilence;

    /// <summary>What <c>--verbose</c> says about the candidate a mark came from, ready to append
    /// inside the mark line's parenthetical, or empty for a candidate with no class to report. Not
    /// merely decorative: the class decides where a window opened and how far it ran, so a mark that
    /// landed oddly is read very differently depending on which of the three found it, and the log
    /// is the only place that pairing is visible.</summary>
    /// <param name="candidate">The candidate whose window produced the mark.</param>
    private static string CandidateNote(ProbeCandidate candidate)
        => candidate.Class switch
        {
            CandidateClass.Silence => ", at a silence",
            CandidateClass.Jingle => ", at a jingle",
            CandidateClass.JingleEmbedded => ", embedded in a jingle",
            _ => "",
        };

    /// <summary>
    /// A confident mark settles its whole overlapping window sequence (consecutive candidates whose
    /// windows each overlap the next): the remaining windows of the sequence cover the same
    /// continuous stretch of audio around the found transition, and a single sequence spanning two
    /// chapter transitions is highly unlikely - so they are skipped outright instead of probed. A
    /// sequence gap rebuilds the whole stretch between its two marks, so the unlikely case is
    /// recovered after all (and Scan remains the final net). A low-confidence mark settles nothing: the
    /// remaining windows keep their chance to re-detect the transition it may have gotten wrong.
    /// <para>
    /// Bounded at <see cref="DetectionTuning.MaxSettledWindowSkip"/> windows, because the premise
    /// fails on a dense candidate sequence - see that constant for the book where one mark settled
    /// 6260 of them. Reaching the cap is logged rather than passed over: it says the sequence was
    /// denser than the skip assumes, which is the first thing worth knowing if the run then behaves
    /// oddly around that mark.
    /// </para>
    /// </summary>
    /// <param name="candidates">The region's candidate sequence.</param>
    /// <param name="ci">Index of the candidate just probed.</param>
    /// <param name="windowEnd">That window's <em>actual</em> probed end, which a seam snap or a
    /// narrowed re-read can pull in ahead of the planned one and which must not be retroactively
    /// pretended narrower than what was really decoded - while the links beyond it use ends
    /// computed at the current width, the same ends those windows would be probed with.</param>
    /// <param name="probeMarks">The marks that window produced.</param>
    /// <returns>The index the candidate loop is to continue from.</returns>
    private int SkipSettledWindows(
        List<ProbeCandidate> candidates, int ci, double windowEnd, List<ProbeMark> probeMarks)
    {
        if (probeMarks.Count == 0 || probeMarks[^1].Confidence < LowConfidenceThreshold)
            return ci;

        var skipTo = ci;
        var reach = windowEnd;
        var capped = false;
        while (skipTo + 1 < candidates.Count && reach > candidates[skipTo + 1].Start)
        {
            if (skipTo - ci >= MaxSettledWindowSkip)
            {
                capped = true;
                break;
            }
            skipTo++;
            reach = WindowEndFor(candidates, skipTo);
        }
        if (skipTo == ci)
            return ci;

        _env.Log?.Invoke($"{skipTo - ci} overlapping window(s) skipped" +
                         (capped ? " - chain capped, the rest are probed" : ""));
        return skipTo;
    }
}
