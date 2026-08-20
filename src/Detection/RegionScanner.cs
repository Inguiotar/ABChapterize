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
/// Everything a <see cref="RegionScanner"/> borrows from the <see cref="ChapterDetector"/> that
/// created it: the tools it reads with, and the detector-owned operations that must stay the
/// detector's (recognition that tallies toward the file's Whisper statistics, the --verbose
/// transcript log, the <see cref="CliOptions.EffectiveMaxChapterNumber"/>-capped phrase matcher and
/// the shared mark placer). One instance serves every region of one file.
/// <para>
/// Deliberately its own type rather than a share of <see cref="ProbeEnvironment"/>, which carries
/// seven of the same members. The three that differ are the reason: Scan wants the flattened phrase
/// matcher rather than Probe's every-reading one, wants the upgrade transcriber unconditionally
/// rather than as an optional second opinion, and must never be handed Probe's denoiser or its
/// probe-model transcriber. A merged record would give each pass fields the other's code ignores,
/// which is how a pass ends up reading with the wrong model.
/// </para>
/// </summary>
/// <param name="Options">Validated command line options.</param>
/// <param name="Audio">Audio source the chunks are decoded from.</param>
/// <param name="Vad">The voice-activity detector, or null when the VAD pre-pass did not run - which
/// switches Scan between its VAD-aware and its silence-only geometry throughout.</param>
/// <param name="Log">Sink for --verbose log messages, or null when not verbose.</param>
/// <param name="Marks">The file's mark placer, shared with every other pass.</param>
/// <param name="Transcriber">The recognizer every read of this pass goes through: the
/// <c>--upgrade-model</c> one. Held explicitly rather than defaulted, so no read here can quietly
/// fall back to the probe model - the premise of the whole pass is that the probe model has already
/// had its turn on this audio. The caller sets its language before each region, since a distinct
/// upgrade model carries none of its own until then.</param>
/// <param name="TranscribeCounting">The detector's statistics-counting transcribe wrapper. Takes
/// the progress callback <see cref="ProbeEnvironment.TranscribeCounting"/> has no use for: a Scan
/// chunk is minutes of audio inside a single recognizer call, so the bar can only move on the
/// recognizer's own position through it.</param>
/// <param name="LogTranscript">Logs a decoded window's transcript under --verbose.</param>
/// <param name="FindCappedPhraseMatches">The detector's --max-chapter-number-capped phrase matcher,
/// in its flattened form - one match per announcement, the reading that claimed it. Scan has no
/// accept loop to try a superseded reading in, so the every-reading form
/// <see cref="ProbeEnvironment.FindCappedPhraseReadings"/> supplies would have nothing here to act
/// on it.</param>
internal sealed record ScanEnvironment(
    CliOptions Options,
    IAudioSource Audio,
    IVoiceActivityDetector? Vad,
    Action<string>? Log,
    MarkPlacer Marks,
    ITranscriber Transcriber,
    Func<float[], CancellationToken, ITranscriber?, Action<double>?,
        Task<List<TranscriptSegment>>> TranscribeCounting,
    Action<string, List<TranscriptSegment>> LogTranscript,
    Func<List<TranscriptSegment>, LanguageProfile, int?, BareNumberReading,
        IEnumerable<PhraseMatch>> FindCappedPhraseMatches);

/// <summary>
/// The measurements and bookkeeping of the file being scanned, constant across every region of it -
/// <see cref="ProbeContext"/>'s counterpart for Scan, and separate from it for the same reason
/// <see cref="ScanEnvironment"/> is separate from <see cref="ProbeEnvironment"/>. A
/// <see cref="ProbeContext"/> additionally carries the probe model, the candidate silence list, the
/// jingle census, <c>--early-abort</c>'s budget and the adaptive floor, none of which Scan may act
/// on; it would also have to gain this pass's language profile and jingle reach, neither of which
/// Probe wants.
/// </summary>
/// <param name="File">Path of the audio file.</param>
/// <param name="Info">The file's probed media info (duration, size, decoder).</param>
/// <param name="Work">Progress tracker for the phase/byte accounting.</param>
/// <param name="BytesPerSecond">The file's average bytes per second of play time, used to convert
/// transcribed play time into the byte-based progress the bar counts in.</param>
/// <param name="AllSilences">Every silence Analyze stored, down to
/// <see cref="MinStoredSilenceSeconds"/> - used as chunk-border seam targets, to pinpoint each mark
/// at the end of the silence directly preceding its phrase, and as the candidate list
/// <see cref="RegionScanner.ScanGapRetriesAsync"/> re-reads.</param>
/// <param name="NonSpeechRegions">The VAD pre-pass's non-speech regions (empty when it did not
/// run), used as chunk-border seam targets alongside the silences.</param>
/// <param name="SpeechSegments">The raw VAD speech segments behind
/// <paramref name="NonSpeechRegions"/> (empty when VAD is off), for the jingle edge adjustment
/// inside <see cref="ResolveJingleAnchor"/> and, with precise marking, as its candidate
/// positions.</param>
/// <param name="Profile">The language profile this file resolved to.</param>
/// <param name="JingleReachSeconds">How far back a mark may look for the music that introduces it
/// (<see cref="JingleCensus.ReachSeconds"/>). A Scan chunk has no probe window start of its own, so
/// this fixed lookback stands in for one.</param>
/// <param name="ExpectedStartChapter">The number this file's sequence is expected to start at, or
/// null - <see cref="GapPlanning.ExpectedStartFor"/> as it read when the pass began. Supplies the
/// lower bracket for an announcement with no chapter before it.</param>
internal readonly record struct ScanContext(
    string File, MediaInfo Info, WorkTracker Work, double BytesPerSecond,
    List<Silence> AllSilences, List<NonSpeechRegion> NonSpeechRegions,
    List<SpeechSegment> SpeechSegments, LanguageProfile Profile,
    double JingleReachSeconds, int? ExpectedStartChapter);

/// <summary>
/// The Scan pass over one region: reads a stretch of the file straight through and reports every
/// chapter start in it. Where <see cref="RegionProber"/> samples an audiobook at the positions its
/// pauses and music suggest, this reads the audio itself - which is what closes a gap the sampling
/// missed, and what sweeps a book's unbounded tail.
/// <para>
/// One instance per region, like <see cref="RegionProber"/>, so the accumulators
/// (<see cref="_found"/>, <see cref="_remaining"/>) and the region's own bounds are fields rather
/// than arguments threaded through five methods - and so a second region structurally cannot
/// inherit the first one's bookkeeping.
/// </para>
/// </summary>
internal sealed class RegionScanner
{
    /// <summary>The tools this scan borrows from its detector.</summary>
    private readonly ScanEnvironment _env;

    /// <summary>The file being scanned.</summary>
    private readonly ScanContext _ctx;

    /// <summary>Start of the region to transcribe, in seconds.</summary>
    private readonly double _fromSeconds;

    /// <summary>End of the region to transcribe, in seconds.</summary>
    private readonly double _toSeconds;

    /// <summary>The chapter numbers this region exists to recover (see
    /// <see cref="MissingNumbersInGap"/>). Transcription stops as soon as all of them are found -
    /// continuing would only re-scan audio that cannot yield anything new - so the caller can
    /// advance to the next gap (or finish Scan) immediately.
    /// <para>
    /// Null instead runs the region <em>open-ended</em>, as the trailing scan needs: there is no
    /// known set of numbers to satisfy, so nothing can ever be complete and the region is always
    /// scanned through to its end. With no target list to filter by, the only thing that makes a
    /// match new is being numbered above every chapter already known - otherwise an in-text
    /// mention of an earlier chapter would be reported as a find and merely dropped later by
    /// <see cref="Normalize"/>.
    /// </para></summary>
    private readonly IReadOnlyList<int>? _expectedNumbers;

    /// <summary>Whether chunk borders may snap to a seam at all. False turns the
    /// region into plain <see cref="GapChunkSeconds"/> chunks, every border taking the raw-cut
    /// fallback described on <see cref="RunAsync"/> - which is what makes
    /// <see cref="ChapterDetector.RescanShiftedAsync"/>'s displacement a guarantee rather than a
    /// hope. Snapping searches
    /// <see cref="ScanSeamSearchSeconds"/> either way while the two attempts' natural borders lie
    /// only <see cref="RescanShiftSeconds"/> apart, so both can snap to the same silence and the
    /// re-read can hand a later chunk exactly the framing that already failed. Unsnapped, chunk
    /// <em>k</em> of the re-read always starts one shift past chunk <em>k</em> of the first attempt.
    /// The price is a border that may cut through an announcement, which is what snapping exists to
    /// avoid - but a cut border is precisely the case the overlap covers, and the announcement it
    /// cuts is one the shifted re-read is then certain to hear whole. Passed false by both attempts
    /// whenever a re-read may follow; a Scan that will not be re-read keeps its seams.</summary>
    private readonly bool _snapSeams;

    /// <summary>Chapters already detected outside this region, so the per-mark progress numbers and
    /// still-missing log notes reflect the whole file rather than just this region's finds.</summary>
    private readonly IReadOnlyList<DetectedChapter> _knownChapters;

    /// <summary>Which chapter sequence this region lies in (see
    /// <see cref="DetectedChapter.Sequence"/>); 0 for every region of an ordinary book. Everything
    /// recovered here is stamped with it, and every "is this number already known" test is asked of
    /// that part alone.</summary>
    private readonly int _sequence;

    /// <summary>Chapters found in this region so far - what <see cref="RunAsync"/> hands back.</summary>
    private readonly List<DetectedChapter> _found = [];

    /// <summary>The still-missing chapter numbers of this region; emptied as they are found, at
    /// which point there is nothing left to recover here and transcription can stop early. Null for
    /// an open-ended trailing region, which has no such list and therefore no way to finish early -
    /// see <see cref="_expectedNumbers"/>.</summary>
    private readonly HashSet<int>? _remaining;

    /// <summary>Constructs a scanner for one region. Nothing is read until
    /// <see cref="RunAsync"/>.</summary>
    /// <param name="env">The tools this scan borrows; see <see cref="ScanEnvironment"/>.</param>
    /// <param name="ctx">The file being scanned; see <see cref="ScanContext"/>.</param>
    /// <param name="fromSeconds">See <see cref="_fromSeconds"/>.</param>
    /// <param name="toSeconds">See <see cref="_toSeconds"/>.</param>
    /// <param name="expectedNumbers">See <see cref="_expectedNumbers"/>.</param>
    /// <param name="snapSeams">See <see cref="_snapSeams"/>.</param>
    /// <param name="knownChapters">See <see cref="_knownChapters"/>.</param>
    /// <param name="sequence">See <see cref="_sequence"/>.</param>
    internal RegionScanner(
        ScanEnvironment env, ScanContext ctx, double fromSeconds, double toSeconds,
        IReadOnlyList<int>? expectedNumbers, bool snapSeams,
        IReadOnlyList<DetectedChapter> knownChapters, int sequence)
    {
        _env = env;
        _ctx = ctx;
        _fromSeconds = fromSeconds;
        _toSeconds = toSeconds;
        _expectedNumbers = expectedNumbers;
        _snapSeams = snapSeams;
        _knownChapters = knownChapters;
        _sequence = sequence;
        _remaining = expectedNumbers is null ? null : [.. expectedNumbers];
    }

    /// <summary>
    /// Fully transcribes a region of the file and returns all chapter starts found in it - Scan's
    /// way of closing sequence gaps the silence-probe fast path left. Every chunk border is snapped
    /// to the nearest silence (or, when the VAD pre-pass ran, VAD non-speech region) mid-point
    /// within <see cref="ScanSeamSearchSeconds"/> of its natural position; consecutive chunks then
    /// abut exactly at that word-safe seam - no overlap, nothing decoded twice - and a phrase
    /// straddling the seam is still found by carrying the previous chunk's trailing segments
    /// (<see cref="ScanBridgeSeconds"/>) into the next chunk's matching. Only where no seam target
    /// exists near a border does that joint fall back to a raw cut with
    /// <see cref="GapChunkOverlapSeconds"/> of overlap as redundancy against a possible mid-word cut.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Every chapter start found in the region, in the order they were accepted.</returns>
    internal async Task<List<DetectedChapter>> RunAsync(CancellationToken ct)
    {
        // Scan only ever reads a piece of the book - a gap, or its tail - so the bar always has a
        // region to mark out, even where that piece is the whole of what this phase covers. The
        // region begins where the phase's booked progress stands, which is exactly the regions
        // already read: see WorkTracker.MarkRegion. Nothing clears it on the way out, deliberately:
        // whatever follows either marks its own region or begins a phase (which clears it), so the
        // only moment the old mark is still up is between two regions of one phase, where it names
        // the one just finished.
        _ctx.Work.MarkRegion((long)((_toSeconds - _fromSeconds) * _ctx.BytesPerSecond));

        // Inputs to the cross-chunk bridging below: the previous chunk's transcript in absolute
        // file time, and whether the seam it ends at was snapped (overlap-free).
        List<TranscriptSegment> previousChunkAbs = [];
        var previousSeamSnapped = false;
        var chunkStart = _fromSeconds;
        while (chunkStart < _toSeconds)
        {
            ct.ThrowIfCancellationRequested();
            var naturalEnd = Math.Min(chunkStart + GapChunkSeconds, _toSeconds);
            var seam = _snapSeams && naturalEnd < _toSeconds
                ? FindNearestSeam(naturalEnd,
                    Math.Max(chunkStart, naturalEnd - ScanSeamSearchSeconds),
                    Math.Min(naturalEnd + ScanSeamSearchSeconds, _toSeconds),
                    upperInclusive: true, targetStartAtOrBefore: null,
                    _ctx.AllSilences, _ctx.NonSpeechRegions, _env.Vad != null)
                : null;
            var chunkEnd = seam ?? naturalEnd;

            var chunkSeconds = chunkEnd - chunkStart;
            var samples = await _env.Audio.DecodePcmAsync(
                _ctx.File, chunkStart, chunkSeconds, _ctx.Info.InputDecoder, ct);
            // A chunk is minutes of audio in one recognizer call, and without this the bar stands
            // still for all of it - on a long gap, for the better part of an hour. Whisper hands
            // back its segments as it produces them, so its own position through the chunk is free
            // for the taking; the transient progress is cleared by the Advance below, exactly as
            // Analyze's decode progress is. Held to a monotonic maximum inside the chunk because
            // neither property can be assumed of the raw ends: they are not strictly ordered once a
            // window re-segments, and one overshooting the audio it was given is common enough to
            // walk the bar into the next chunk's budget.
            var reachedSeconds = 0.0;
            var segments = await _env.TranscribeCounting(samples, ct, _env.Transcriber, segmentEnd =>
            {
                reachedSeconds = Math.Max(reachedSeconds, Math.Min(segmentEnd, chunkSeconds));
                _ctx.Work.SetPhaseProgress((long)(reachedSeconds * _ctx.BytesPerSecond));
            });
            _env.LogTranscript($"transcribed gap chunk @{FormatTimestamp(chunkStart)}", segments);
            var freshAbs = ShiftSegments(segments, chunkStart);

            // At a snapped seam the chunks share no audio, so a phrase straddling the seam
            // exists in neither chunk alone - bridge it by prepending the previous chunk's
            // trailing segments to this chunk's matching input. Unsnapped borders overlap
            // instead and need no bridge; bridging there would only duplicate the overlap's
            // text and risk parsing a number across the duplicated join.
            List<TranscriptSegment> carried = previousSeamSnapped
                ? previousChunkAbs.Where(s => s.EndSeconds > chunkStart - ScanBridgeSeconds).ToList()
                : [];
            List<TranscriptSegment> matchSegments = carried.Count > 0 ? [.. carried, .. freshAbs] : freshAbs;
            // Same leading silence/jingle correction Probe applies, so a phrase Whisper
            // timestamped from the pause before it is anchored from its real onset here too.
            matchSegments = TrimLeadingNonSpeech(
                matchSegments, _ctx.AllSilences, _ctx.NonSpeechRegions, _env.Vad != null);

            // Unlike Probe there is no window-relative timing rule here, so matching simply
            // runs in absolute file time: a match's PhraseStartSeconds is already absolute.
            foreach (var match in _env.FindCappedPhraseMatches(matchSegments, _ctx.Profile,
                         carried.Count > 0 ? carried.Count : null,
                         RegionProber.BareNumberReadingFor(_remaining is not null)))
            {
                var phraseAbs = match.PhraseStartSeconds;
                // A match entirely inside the carried tail was already found (and reported) by
                // the previous chunk's own pass; only a seam-straddling detection is news here.
                if (phraseAbs < chunkStart && !match.SpansMerge)
                    continue;
                // A chapter bounding this gap is already known and can resurface right at a chunk
                // border, its announcement sitting just inside the scanned range, without being
                // news. Leave its existing mark alone rather than risk Normalize preferring this
                // re-detection's timestamp.
                if (_knownChapters.Any(k => k.Sequence == _sequence && k.Number == match.Number))
                    continue;
                // An open-ended region has no expected-number list, so what makes a match new is
                // topping every chapter already known. Without this an in-text mention of an
                // earlier number would be reported as a find and then dropped by Normalize.
                if (_remaining is null && !IsAboveEveryKnownChapter(match.Number))
                {
                    _env.Log?.Invoke(
                        $"skipped chapter {match.Number} at {FormatTimestamp(phraseAbs)} - " +
                        "not above every chapter found (in-text mention?)");
                    continue;
                }
                // A bounded gap knows exactly which numbers can live in it, so anything else is a
                // mishearing or an in-text mention. Re-probe rejects those against the region's own
                // bounds and the retry scan below tests the same set; this scan did not, so a
                // "chapter seven" heard in the gap between chapters 1 and 3 was planted here and
                // then cost the genuine chapter 3 its mark, Normalize dropping it to keep the
                // sequence monotonic.
                if (_remaining is not null && !_remaining.Contains(match.Number))
                {
                    _env.Log?.Invoke(
                        $"skipped chapter {match.Number} at {FormatTimestamp(phraseAbs)} - " +
                        "not missing from this gap");
                    continue;
                }
                if (match.SpansMerge)
                    _env.Log?.Invoke(
                        $"chapter {match.Number} detection spans a Scan chunk seam " +
                        "(bridged from the previous chunk) - worth a spot check");
                // The bridged tail (see `carried` above) makes the chunk's own start no longer the
                // earliest moment the transcript speaks for; the walk's corroboration check needs
                // the true one (see TranscriptWindow).
                var chunkTranscript = new TranscriptWindow(
                    matchSegments,
                    carried.Count > 0 ? Math.Min(chunkStart, carried.Min(s => s.StartSeconds)) : chunkStart,
                    chunkEnd);
                await RecordGapChapterMatch(match, chunkTranscript, ct);
            }

            // A chunk whose normal transcript still leaves some expected number(s) unaccounted
            // for gets one more look: long inner gaps that line up with a real silence/jingle
            // (not just an ordinary narration pause) are re-scanned in small chunks, the same
            // fallback --verify uses for the same underlying Whisper failure mode.
            if (_remaining is null or { Count: > 0 })
                await ScanGapRetriesAsync(chunkStart, chunkEnd, freshAbs, ct);

            // And the other failure mode, which the scan above cannot see because the audio was
            // transcribed perfectly well: the phrase is right there and only its number is
            // unreadable. Reported and re-framed rather than dropped in silence.
            if (_remaining is null or { Count: > 0 })
                await ScanUnnumberedRetriesAsync(chunkStart, chunkEnd, matchSegments, ct);

            _ctx.Work.Advance((long)(chunkSeconds * _ctx.BytesPerSecond));

            // Everything this gap was meant to recover is found, so stop and let the caller move
            // on. The unscanned remainder still counts as this gap's work done - advance it, or
            // the Scan bar never reaches its budget.
            if (_remaining is { Count: 0 })
            {
                _env.Log?.Invoke("gap complete - all expected chapters found");
                if (chunkEnd < _toSeconds)
                    _ctx.Work.Advance((long)((_toSeconds - chunkEnd) * _ctx.BytesPerSecond));
                break;
            }
            if (chunkEnd >= _toSeconds)
                break;
            previousChunkAbs = freshAbs;
            previousSeamSnapped = seam.HasValue;
            // A snapped border needs no overlap - the next decode starts exactly at the seam;
            // an unsnapped one keeps the redundancy overlap against its possible mid-word cut.
            chunkStart = seam ?? chunkEnd - GapChunkOverlapSeconds;
        }
        return _found;
    }

    /// <summary>
    /// Second chance for a Scan chunk that heard the chapter phrase but could not read a number
    /// from it. Unlike <see cref="ScanGapRetriesAsync"/>, which chases audio Whisper skipped
    /// outright, here the recognition succeeded and only the notation defeated the parser - so the
    /// retry re-decodes a window <em>framed differently</em> around the phrase rather than a
    /// shorter one over the same span. Which numeral form Whisper picks follows the framing (see
    /// <see cref="DetectionTuning.UnnumberedRetryLeadSeconds"/> for the measurement behind that),
    /// so a window that starts well before the announcement genuinely can read a number where the
    /// chunk's own transcript could not.
    /// <para>
    /// Every unreadable announcement is logged whether or not its retry succeeds: the cases this
    /// cannot fix - a word ordinal past a language's parser, a number above 999 - are exactly the
    /// ones where knowing the phrase was heard and discarded saves the next investigation.
    /// </para>
    /// </summary>
    /// <param name="chunkStart">Absolute start of the chunk being retried.</param>
    /// <param name="chunkEnd">Absolute end of that chunk.</param>
    /// <param name="matchSegments">The chunk's transcript in absolute file time, as the phrase
    /// matching saw it.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ScanUnnumberedRetriesAsync(
        double chunkStart, double chunkEnd, List<TranscriptSegment> matchSegments,
        CancellationToken ct)
    {
        var retries = 0;
        foreach (var heard in FindUnnumberedAnnouncements(matchSegments, _ctx.Profile))
        {
            // A phrase carried in from the previous chunk's tail was already reported (and
            // retried) there; only this chunk's own audio is news.
            if (heard.PhraseStartSeconds < chunkStart)
                continue;
            _env.Log?.Invoke(
                $"heard the chapter phrase at {FormatTimestamp(heard.PhraseStartSeconds)} " +
                $"but could not read a number from it: \"{heard.Text}\"");

            if (_remaining is { Count: 0 } || retries >= MaxUnnumberedRetriesPerChunk)
                continue;
            retries++;

            var retryStart = Math.Max(0, heard.PhraseStartSeconds - UnnumberedRetryLeadSeconds);
            var len = Math.Min(UnnumberedRetryWindowSeconds, _ctx.Info.DurationSeconds - retryStart);
            if (len <= 0)
                continue;

            var samples = await _env.Audio.DecodePcmAsync(
                _ctx.File, retryStart, len, _ctx.Info.InputDecoder, ct);
            var segments = await _env.TranscribeCounting(samples, ct, _env.Transcriber, null);
            _env.LogTranscript(
                $"unreadable-number retry {len:0.0}s@{FormatTimestamp(retryStart)}", segments);
            var retryAbs = TrimLeadingNonSpeech(
                ShiftSegments(segments, retryStart), _ctx.AllSilences, _ctx.NonSpeechRegions,
                _env.Vad != null);

            foreach (var match in _env.FindCappedPhraseMatches(
                         retryAbs, _ctx.Profile, null,
                         RegionProber.BareNumberReadingFor(_remaining is not null)))
            {
                var wanted = _remaining is null
                    ? IsAboveEveryKnownChapter(match.Number)
                    : _remaining.Contains(match.Number);
                if (!wanted || _knownChapters.Any(k => k.Sequence == _sequence && k.Number == match.Number))
                    continue;
                await RecordGapChapterMatch(
                    match, new TranscriptWindow(retryAbs, retryStart, retryStart + len), ct);
                if (_remaining is { Count: 0 })
                    break;
            }
        }
    }


    /// <summary>
    /// Whether a phrase match found in an <em>open-ended</em> Scan region (see the null
    /// <c>expectedNumbers</c> case of <see cref="RunAsync"/>) is genuinely new. Such a
    /// region has no expected-number list to test against, so the only usable criterion is the one
    /// <see cref="Normalize"/> would apply anyway: the number has to top every chapter already
    /// known, both the ones detected elsewhere and the ones this region has found so far. Anything
    /// at or below that is a repeat or an in-text mention, not a chapter this scan recovered.
    /// </summary>
    /// <param name="number">The matched chapter number.</param>
    private bool IsAboveEveryKnownChapter(int number)
        => _knownChapters.Concat(_found).Where(c => c.Sequence == _sequence)
            .All(c => number > c.Number);

    /// <summary>
    /// Records one phrase match found while scanning a Scan gap chunk (its normal transcript, or
    /// <see cref="ScanGapRetriesAsync"/>'s fallback) as a detected chapter: resolves the
    /// default-mode mark - a fixed offset before the phrase - hands it to <see cref="MarkPlacer"/>
    /// for the corrections and statistics every pass shares, then updates <see cref="_found"/>,
    /// <see cref="_remaining"/> and the progress bar's chapter state, and logs it. Shared
    /// between both callers so this stays in exactly one place.
    /// </summary>
    /// <param name="match">The confirmed phrase match, in absolute file time.</param>
    /// <param name="transcript">The chunk the match was found in - its segments feed the VAD edge
    /// adjustment inside <see cref="ResolveJingleAnchor"/>, its span the jingle walk and precise
    /// mark; see <see cref="MarkContext.Transcript"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task RecordGapChapterMatch(
        PhraseMatch match, TranscriptWindow transcript, CancellationToken ct)
    {
        var phraseAbs = match.PhraseStartSeconds;
        double time;
        // The silence/jingle the mark anchored to, hoisted out of the branches below so the
        // per-file statistics can be recorded once, uniformly (see RecordChapterStats).
        Silence? statSilence = null;
        NonSpeechRegion? statRegion = null;
        if (_env.Vad != null)
        {
            // Same VAD-region-primary anchor resolution as Probe, just against a fixed lookback
            // since a gap chunk has no probe window start of its own. Feeds default-mode placement
            // and the auto-mechanism statistics; --mark-before-jingle resolves from the
            // default-mode mark instead and does not consume it.
            var lookback = _ctx.JingleReachSeconds;
            var (anchorSilence, vadRegion) = ResolveJingleAnchor(
                phraseAbs, match.PhraseEndSeconds, phraseAbs - lookback, _ctx.AllSilences,
                _ctx.NonSpeechRegions, candidateVadRegion: null, _ctx.SpeechSegments, transcript.Segments);
            time = RefineDefaultMark(
                Math.Max(0, ResolveDefaultPhraseOnset(
                                phraseAbs, vadRegion, anchorSilence, _ctx.SpeechSegments)
                            - _env.Options.MarkLeadSeconds),
                _ctx.SpeechSegments, _env.Options.MarkLeadSeconds);
            (statSilence, statRegion) = (anchorSilence, vadRegion);
        }
        else
        {
            // Without a VAD pre-pass, the mark always goes --mark-lead seconds before the
            // phrase itself; the preceding silence (if any close enough) is still located
            // purely to feed the --min-silence-length auto tightening via MarkPlacer's statistics.
            var anchor = FindRealAnchorSilence(
                phraseAbs - PhraseLatestStartSeconds, phraseAbs, _ctx.AllSilences);
            time = Math.Max(0, phraseAbs - _env.Options.MarkLeadSeconds);
            statSilence = anchor;
        }
        // Built before the context, because the refinement's own matcher is held to the same
        // sequence bounds the number re-read is (see NumberCheck.AdmitsAsAnnouncement).
        var check = new NumberCheck(match.Number, _ctx.Profile,
            BracketingBounds(phraseAbs, _knownChapters, _found, _ctx.ExpectedStartChapter, _sequence));
        var markCtx = new MarkContext(
            _ctx.File, _ctx.Info.InputDecoder,
            _ctx.Profile.AnnouncementFor(
                match.Wording, RegionProber.BareNumberReadingFor(_remaining is not null),
                check.AdmitsAsAnnouncement),
            _ctx.AllSilences, _ctx.SpeechSegments, transcript, _ctx.Profile.Language);
        // Scan only ever reads a bare number under the wider reading where the gap it is filling
        // has an expected-number list - the same condition RegionProber.WideBareNumberReading
        // expresses for the probing passes - so the isolation check is asked for on exactly those.
        if (await _env.Marks.PlaceAsync(
                check,
                time, phraseAbs, match.PhraseEndSeconds, statSilence, statRegion, markCtx,
                AnnouncementIsolation.ForChapter(match, phraseAbs, _remaining is not null),
                ct) is not { } placed)
            return;
        // The refinement's own probes may have re-read the number (see RefinedNumberVote), so the
        // gap's remaining-numbers bookkeeping has to follow what they settled on.
        time = placed.TimeSeconds;
        var number = placed.Number!.Value;
        _found.Add(new DetectedChapter(number, time, match.Confidence, Sequence: _sequence));
        _remaining?.Remove(number);
        var (highest, missingNumbers) =
            ChapterProgress(_knownChapters.Concat(_found), _ctx.ExpectedStartChapter);
        _ctx.Work.HighestChapters = highest;
        _ctx.Work.MissingChapters = missingNumbers.Count;
        _env.Log?.Invoke($"chapter {number} found in gap, mark placed at {FormatTimestamp(time)} " +
                     $"(confidence {match.Confidence:0.00}" +
                     await _env.Marks.LoudnessNoteAsync(time, markCtx, ct) +
                     $"){LowConfidenceNote(match.Confidence)}" +
                     MissingNote(missingNumbers));
    }

    /// <summary>
    /// Second-chance scan for a Scan gap chunk that, after its normal transcript, still has
    /// missing chapter numbers (<see cref="_remaining"/>). Every stored silence - and, when the
    /// VAD pre-pass ran, every VAD non-speech region - at least <see cref="GapRetryThresholdSeconds"/>
    /// long, entirely inside this chunk, and covered by <em>none</em> of the chunk's own fresh
    /// segments (not the bridged tail carried in from the previous chunk, already covered by its own
    /// pass), i.e. one Whisper produced no speech at all over, is padded by
    /// <see cref="GapRetryPaddingSeconds"/> on each side and re-scanned in short, overlapping
    /// <see cref="GapRetryChunkSeconds"/> sub-chunks - the same technique --verify uses to recover a
    /// phrase Whisper silently dropped from a single call spanning a mostly non-speech stretch.
    /// Scoped to the silence/region's own bounds rather than the whole raw stretch between the
    /// segments bracketing it: with sparse narration that stretch can span most of a 600 s Scan
    /// chunk, making an already time-consuming fallback far more so, whereas a genuine jingle or
    /// scene-transition silence runs seconds to at most tens of seconds. Confirmed matches are
    /// recorded via <see cref="RecordGapChapterMatch"/> like the chunk's normal ones; scanning
    /// stops as soon as nothing is left to find.
    /// </summary>
    /// <param name="chunkStart">Absolute start of the Scan chunk just transcribed.</param>
    /// <param name="chunkEnd">Absolute end of that chunk.</param>
    /// <param name="freshAbs">That chunk's own transcript segments (absolute file time),
    /// excluding any bridged tail from the previous chunk.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ScanGapRetriesAsync(
        double chunkStart, double chunkEnd, List<TranscriptSegment> freshAbs,
        CancellationToken ct)
    {
        IEnumerable<(double Start, double End)> candidates = _ctx.AllSilences
            .Where(s => s.EndSeconds - s.StartSeconds >= GapRetryThresholdSeconds &&
                        s.StartSeconds >= chunkStart && s.EndSeconds <= chunkEnd)
            .Select(s => (s.StartSeconds, s.EndSeconds));
        if (_env.Vad != null)
            candidates = candidates.Concat(_ctx.NonSpeechRegions
                .Where(r => r.EndSeconds - r.StartSeconds >= GapRetryThresholdSeconds &&
                            r.StartSeconds >= chunkStart && r.EndSeconds <= chunkEnd)
                .Select(r => (r.StartSeconds, r.EndSeconds)));

        foreach (var (silStart, silEnd) in candidates.OrderBy(c => c.Start))
        {
            if (_remaining is { Count: 0 })
                break;
            // An ordinary sentence that merely straddles a real pause still has its own segment
            // covering the pause and needs no second look - only a stretch with nothing
            // transcribed over it at all is a candidate for having been dropped outright.
            if (freshAbs.Any(s => s.StartSeconds < silEnd && s.EndSeconds > silStart))
                continue;

            var sliceStart = Math.Max(chunkStart, silStart - GapRetryPaddingSeconds);
            var sliceEnd = Math.Min(chunkEnd, silEnd + GapRetryPaddingSeconds);
            var subStep = GapRetryChunkSeconds - GapRetryChunkOverlapSeconds;
            for (var subStart = sliceStart;
                 subStart < sliceEnd && _remaining is null or { Count: > 0 };
                 subStart += subStep)
            {
                var len = Math.Min(
                    Math.Min(GapRetryChunkSeconds, sliceEnd - subStart),
                    _ctx.Info.DurationSeconds - subStart);
                if (len <= 0)
                    continue;

                var subSamples = await _env.Audio.DecodePcmAsync(
                    _ctx.File, subStart, len, _ctx.Info.InputDecoder, ct);
                var subSegments = await _env.TranscribeCounting(subSamples, ct, _env.Transcriber, null);
                _env.LogTranscript($"gap retry {len:0.0}s@{FormatTimestamp(subStart)}", subSegments);
                var subAbs = TrimLeadingNonSpeech(
                    ShiftSegments(subSegments, subStart), _ctx.AllSilences, _ctx.NonSpeechRegions,
                    _env.Vad != null);

                foreach (var match in _env.FindCappedPhraseMatches(
                             subAbs, _ctx.Profile, null,
                             RegionProber.BareNumberReadingFor(_remaining is not null)))
                {
                    var wanted = _remaining is null
                        ? IsAboveEveryKnownChapter(match.Number)
                        : _remaining.Contains(match.Number);
                    if (!wanted ||
                        _knownChapters.Any(k => k.Sequence == _sequence && k.Number == match.Number))
                        continue;
                    await RecordGapChapterMatch(
                        match, new TranscriptWindow(subAbs, subStart, subStart + len), ct);
                    if (_remaining is { Count: 0 })
                        break;
                }
            }
        }
    }

}
