// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using ABChapterize.Cli;
using ABChapterize.Language;
using ABChapterize.Transcription;
using ABChapterize.Vad;
using static ABChapterize.Detection.DetectionFormatting;
using static ABChapterize.Detection.JingleGeometry;
using static ABChapterize.Detection.PhraseMatching;

namespace ABChapterize.Detection;

/// <summary>
/// The per-file constants a mark placement needs, gathered so <see cref="MarkPlacer.PlaceAsync"/>
/// does not take them one by one. Only <paramref name="Transcript"/> varies from mark to mark.
/// </summary>
/// <param name="File">Path of the audio file.</param>
/// <param name="InputDecoder">Explicit input decoder to force, or null.</param>
/// <param name="Announcement">What the corrections look for at the mark: the chapter phrase for a
/// numbered chapter (or a number spoken alone under <c>--chapter-phrase none</c>), the matching
/// prologue/epilogue phrase for a named mark. Per mark rather than per file, since a run detects
/// all three at once and a correction that re-transcribed a prologue while looking for "chapter"
/// could only ever fail to confirm it.</param>
/// <param name="AllSilences">Every silence Pass 1 stored, for the --mark-before-jingle walk and for
/// precise marking's onset anchor (<see cref="PreciseMarkRefiner.AnchorOnsetToSoundAsync"/>).</param>
/// <param name="SpeechSegments">Raw VAD speech segments for the whole file, for that walk and its
/// verification search.</param>
/// <param name="Transcript">The window this mark's phrase was found in - its segments in absolute
/// file time, so the backward walk can tell genuine preceding narration apart from a musical or
/// vocal transient inside the jingle (see <see cref="JingleGeometry.IsGenuineSpeech"/>), and the
/// span of audio they came from. That span's end doubles as what precise marking anchors its
/// search against: the window's own planned end, not the last segment's timestamp, which is
/// exactly the kind of figure precise marking exists because it cannot trust. It only tightens that
/// search where the matched segment ends inside the window, though - a segment is admitted to a
/// window by its start alone, so one that Whisper stretched past the window's end describes audio
/// this figure knows nothing about. See
/// <see cref="PreciseMarkRefiner.LocatePhraseByShrinkingWindowAsync"/>'s ceiling, where that is
/// resolved.</param>
/// <param name="Language">The file's resolved language code, for the <c>--pass3-model</c> retry a
/// failed refinement falls back on. Carried here rather than taken from
/// <see cref="NumberCheck.Profile"/> because a named (prologue/epilogue/<c>--custom</c>) mark has no
/// <see cref="NumberCheck"/> and is refined exactly like a numbered one.</param>
internal readonly record struct MarkContext(
    string File, string? InputDecoder, AnnouncementMatcher Announcement, List<Silence> AllSilences,
    List<SpeechSegment> SpeechSegments, TranscriptWindow Transcript, string Language);

/// <summary>
/// The chapter number a placement is for, plus what it takes to check that number against the
/// refinement's own view of the announcement (<see cref="RefinedNumberVote"/>). Named marks have no
/// number and pass none; everything else about their placement is identical.
/// </summary>
/// <param name="Number">The chapter number the detecting window read.</param>
/// <param name="Profile">The resolved language profile, for re-reading a number from the
/// refinement's probe transcripts.</param>
/// <param name="Bounds">Where in the chapter sequence this announcement sits, which is what a
/// corrected number still has to satisfy to be adopted.</param>
internal readonly record struct NumberCheck(int Number, LanguageProfile Profile, NumberBounds Bounds)
{
    /// <summary>
    /// Which numbers the refinement may take for this announcement when the book has no chapter
    /// phrase and the number <em>is</em> the announcement: one the sequence can hold here, or the
    /// one the detecting window already read.
    /// <para>
    /// The second half is not redundant. A number the bounds reject can still reach here, having
    /// survived <see cref="SuspectNumberMender"/> unmended because no re-framing read anything
    /// better; without it the refinement would reject the very announcement it was called to pin
    /// down, every probe would answer "no", and the mark would keep its unrefined default-mode
    /// position - the exact failure that made bare-number mode unrefinable in the first place.
    /// </para>
    /// </summary>
    /// <param name="number">A number read out of one refinement probe's transcript.</param>
    internal bool AdmitsAsAnnouncement(int number) => number == Number || Bounds.Admits(number);
}

/// <summary>The outcome of placing one mark.</summary>
/// <param name="TimeSeconds">The final mark position.</param>
/// <param name="Number">The chapter number to record it under - the one that came in, unless the
/// refinement's probes overruled it; null for a named mark.</param>
internal readonly record struct MarkPlacement(double TimeSeconds, int? Number);

/// <summary>
/// Turns a default-mode mark into the final one and records what the file's statistics need to
/// know about it. Both passes that detect a chapter - Pass 2's window probing and Pass 3's gap
/// transcription - compute their default-mode mark differently (a probe window has a candidate and
/// a window start to anchor against, a gap chunk has neither), but everything from there on is
/// identical, and lives here: the precise marking correction, the --mark-before-jingle backward
/// walk, the per-chapter silence/jingle measurements behind <see cref="DetectionStats"/>, and the
/// loudness figure the --verbose mark lines carry.
/// <para>
/// Constructed per file, like <see cref="PreciseMarkRefiner"/> (which it owns): the statistics
/// dictionaries are per-file state, and the transcribe delegate it is handed closes over the
/// detector's own counting wrapper so the corrections' extra transcriptions still tally into the
/// same per-file Whisper statistics.
/// </para>
/// </summary>
internal sealed class MarkPlacer
{
    private readonly CliOptions _options;
    private readonly IAudioSource _audio;
    private readonly Action<string>? _log;
    private readonly PreciseMarkRefiner _refiner;

    /// <summary>The detector's <c>--max-chapter-number</c>-capped phrase matcher, for the
    /// refinement's number re-read (<see cref="RefinedNumberVote"/>). Borrowed rather than
    /// re-implemented so a number the cap rules out during detection is ruled out here too.</summary>
    private readonly Func<List<TranscriptSegment>, LanguageProfile, int?, IEnumerable<PhraseMatch>> _findMatches;

    /// <summary>Per detected chapter number, the length of the silence that preceded its phrase
    /// (when the VAD pre-pass ran, the silence preceding the jingle - see <see cref="Record"/>).
    /// Keyed by number so a re-detection overwrites; filtered to the surviving chapters when the
    /// per-file minimum is computed.</summary>
    private readonly Dictionary<int, double> _silenceSeconds = [];

    /// <summary>Per detected chapter number, the length of the jingle that preceded its phrase
    /// (only measured when the VAD pre-pass ran); feeds the per-file maximum-jingle statistic.</summary>
    private readonly Dictionary<int, double> _jingleSeconds = [];

    /// <summary>Creates a placer for one file.</summary>
    /// <param name="audio">Audio source for the corrections' own decodes.</param>
    /// <param name="options">Validated command line options.</param>
    /// <param name="log">This file's log sinks; default when nothing is listening.</param>
    /// <param name="transcribeCounting">The detector's statistics-counting transcribe wrapper.</param>
    /// <param name="transcribeUpgraded">The same, through the heavier <c>--pass3-model</c>, for the
    /// retry a failed refinement falls back on; null when no upgrade model was chosen.</param>
    /// <param name="findMatches">The detector's --max-chapter-number-capped phrase matcher.</param>
    internal MarkPlacer(IAudioSource audio, CliOptions options, DetectionLog log,
        Func<float[], CancellationToken, Task<List<TranscriptSegment>>> transcribeCounting,
        Func<float[], string, CancellationToken, Task<List<TranscriptSegment>>>? transcribeUpgraded,
        Func<List<TranscriptSegment>, LanguageProfile, int?, IEnumerable<PhraseMatch>> findMatches)
    {
        _audio = audio;
        _options = options;
        _log = log.Fanout();
        _findMatches = findMatches;
        _refiner = new PreciseMarkRefiner(audio, options, log, transcribeCounting, transcribeUpgraded);
    }

    /// <summary>
    /// The refinement itself, for the one caller that wants it without the rest of a placement:
    /// <c>--verify --fix</c>, which is correcting a mark whose chapter number is already settled by
    /// the marking's own title (so no <see cref="RefinedNumberVote"/>) and which is not part of a
    /// detection run (so no per-chapter statistics to record).
    /// </summary>
    internal PreciseMarkRefiner Refiner => _refiner;

    /// <summary>
    /// Finishes one chapter's mark: precise marking corrects the default-mode position by direct
    /// re-transcription (unless --quick-marks turned it off), --mark-before-jingle then walks the
    /// result back to the jingle's leading edge, and the chapter's silence/jingle measurements are
    /// recorded either way - the auto mechanisms and statistics stay meaningful even when marks are
    /// placed at the plain default offset.
    /// <para>
    /// Precise marking also settles the chapter <em>number</em> here, from the transcripts its own
    /// probes produced (<see cref="RefinedNumberVote"/>), which is why the number goes out as well
    /// as in. It has to happen at this point and not at the call site: the measurements below are
    /// keyed by chapter number, and recording them under a number the very next step overturns
    /// would file a chapter's silence and jingle under a chapter that does not exist.
    /// </para>
    /// </summary>
    /// <param name="chapter">The detected chapter number and what it takes to double-check it, or
    /// null for a named (prologue/epilogue) mark - which is placed identically but contributes
    /// nothing to the per-chapter measurements, since those describe the book's regular chapter
    /// breaks and a prologue or epilogue transition is exactly the atypical one the "inter-chapter"
    /// statistics already exclude chapter 1 for.</param>
    /// <param name="defaultMark">The default-mode mark the caller's own pass computed.</param>
    /// <param name="phraseAbs">Absolute phrase start time, the clip point for the jingle length.</param>
    /// <param name="phraseEndAbs">Absolute end of the transcript segment(s) the phrase was matched
    /// in. Together with <paramref name="phraseAbs"/> this brackets where the announcement can
    /// actually be - see <see cref="PreciseMarkRefiner.RefinePreciseMarkAsync"/>.</param>
    /// <param name="statSilence">The silence the mark anchored to, or null when none.</param>
    /// <param name="statRegion">The jingle region preceding the phrase, or null (always null when
    /// the VAD pre-pass did not run).</param>
    /// <param name="ctx">The file's mark-placement constants.</param>
    /// <param name="isolation">What the announcement's surroundings must look like for this mark to
    /// be kept; <see cref="IsolationCheck.None"/> for the callers that do not ask.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The final mark position and the number to record it under, or null when the
    /// isolation check rejected the announcement - a match the caller must then drop entirely
    /// rather than mark.</returns>
    internal async Task<MarkPlacement?> PlaceAsync(
        NumberCheck? chapter, double defaultMark, double phraseAbs, double phraseEndAbs,
        Silence? statSilence, NonSpeechRegion? statRegion, MarkContext ctx,
        IsolationCheck isolation, CancellationToken ct)
    {
        var time = defaultMark;
        var phraseHeard = false;
        double? onset = null;
        var number = chapter?.Number;
        if (_options.PreciseMark)
        {
            var refined = await _refiner.RefinePreciseMarkAsync(
                time, ctx.File, ctx.InputDecoder, ctx.Announcement, ctx.Language,
                phraseAbs, phraseEndAbs, ctx.Transcript.EndSeconds, ctx.AllSilences, ct);
            (time, phraseHeard, onset) = (refined.Mark, refined.PhraseHeard, refined.OnsetSeconds);
            if (chapter is { } check &&
                RefinedNumberVote.Recount(
                    refined.PhraseReadings, check.Profile, _findMatches, check.Number, check.Bounds,
                    phraseAbs, _log) is { } recounted)
                number = recounted;
        }
        if (!IsolationHolds(onset, isolation, number, ctx))
            return null;
        if (_options.MarkBeforeJingle)
            time = await ApplyMarkBeforeJingleAsync(time, phraseHeard, ctx, ct);
        if (number is { } chapterNumber)
            Record(chapterNumber, statSilence, statRegion, phraseAbs);
        return new MarkPlacement(time, number);
    }

    /// <summary>
    /// Runs the isolation guard on a placement: is this announcement really set off from the
    /// narration by the pauses its kind is supposed to have (see
    /// <see cref="AnnouncementIsolation"/>)? Sits between the refinement and everything that acts
    /// on a mark, so a rejected match costs no <c>--mark-before-jingle</c> walk and - more
    /// importantly - contributes nothing to the per-chapter silence/jingle measurements that steer
    /// <c>--min-silence-length auto</c>.
    /// <para>
    /// Measured at the refined onset where there is one, and otherwise at
    /// <see cref="IsolationCheck.FallbackPosition"/>. The fallback matters for two live cases:
    /// <c>--quick-marks</c>, which skips refinement altogether, and an announcement neither model
    /// could confirm. A caller that has no honest position to offer passes none, and its match is
    /// then dropped rather than trusted - which is the right way round for the only matches that
    /// reach here needing the check, namely bare numbers found in the middle of a segment, whose
    /// entire claim to being an announcement <em>is</em> the pause around them.
    /// </para>
    /// </summary>
    /// <param name="onset">The refined announcement onset, or null when none was confirmed.</param>
    /// <param name="isolation">The check to apply.</param>
    /// <param name="number">The chapter number, for the log line; null for a named mark.</param>
    /// <param name="ctx">The file's mark-placement constants, for the VAD speech segments.</param>
    /// <returns>True when the mark may be kept.</returns>
    private bool IsolationHolds(
        double? onset, IsolationCheck isolation, int? number, MarkContext ctx)
    {
        if (isolation.Rule == IsolationRule.None)
            return true;
        if ((onset ?? isolation.FallbackPosition) is not { } at)
            return Reject("its announcement could not be confirmed and nothing vouches for it");
        if (AnnouncementIsolation.Measure(at, ctx.SpeechSegments) is not { } flanks)
            // No VAD pre-pass, so there is nothing to measure the pauses with. Not a reason to
            // throw the mark away: the check is an extra safeguard, and a run without VAD is
            // exactly as well off as it was before this one existed.
            return true;
        return AnnouncementIsolation.Satisfies(flanks, isolation.Rule)
               || Reject($"it is not set off by a pause ({AnnouncementIsolation.Describe(flanks, isolation.Rule)})");

        bool Reject(string why)
        {
            var what = number is { } n ? $"chapter {n}" : "the named mark";
            _log?.Invoke($"discarded {what} at {FormatTimestamp(onset ?? isolation.FallbackPosition ?? 0)} - {why}");
            return false;
        }
    }

    /// <summary>
    /// The ", -42.7 dBFS" clause both passes' mark log lines append to their confidence figure: how
    /// loud the audio actually is where the finished mark landed (see <see cref="MarkLoudness"/>),
    /// which tells a real pause apart from a mark sitting mid-word or inside music without having
    /// to listen to the file. Empty - and, importantly, not measured at all - when --verbose is
    /// off, so the small extra decode per mark is only ever paid for when something will read it.
    /// </summary>
    /// <param name="mark">The finished mark position.</param>
    /// <param name="ctx">The file's mark-placement constants.</param>
    /// <param name="ct">Cancellation token.</param>
    internal async Task<string> LoudnessNoteAsync(double mark, MarkContext ctx, CancellationToken ct)
    {
        if (_log == null)
            return "";
        var dbfs = await MarkLoudness.MeasureDbfsAsync(_audio, ctx.File, mark, ctx.InputDecoder, ct);
        return $", {MarkLoudness.Format(dbfs)}";
    }

    /// <summary>The shortest recorded preceding silence over the given chapters, or null when none
    /// of them had one. Taken over only the chapters that survived into the final result, so a
    /// detection that lost out to <c>Normalize</c>, or a spurious number, contributes nothing.</summary>
    /// <param name="chapters">The chapters to aggregate over.</param>
    internal double? MinSilenceSeconds(IEnumerable<DetectedChapter> chapters)
        => Extreme(chapters, _silenceSeconds, static vs => vs.Min());

    /// <summary>The longest recorded jingle over the given chapters, or null when none of them had
    /// one (including whenever the VAD pre-pass did not run). Same survivors-only aggregation as
    /// <see cref="MinSilenceSeconds"/>.</summary>
    /// <param name="chapters">The chapters to aggregate over.</param>
    internal double? MaxJingleSeconds(IEnumerable<DetectedChapter> chapters)
        => Extreme(chapters, _jingleSeconds, static vs => vs.Max());

    /// <summary>Applies <paramref name="pick"/> to the measurements recorded for those of
    /// <paramref name="chapters"/> that have one, or returns null when none do.</summary>
    private static double? Extreme(
        IEnumerable<DetectedChapter> chapters, Dictionary<int, double> measurements,
        Func<List<double>, double> pick)
    {
        var values = chapters
            .Where(c => measurements.ContainsKey(c.Number))
            .Select(c => measurements[c.Number])
            .ToList();
        return values.Count > 0 ? pick(values) : null;
    }

    /// <summary>
    /// Applies --mark-before-jingle on top of an already-computed (and normally already
    /// precise-marking-corrected) default-mode mark: <see
    /// cref="JingleGeometry.ComputeMarkBeforeJingle"/> walks it backward to the jingle's true
    /// leading edge - into the hush before it by up to <c>--mark-lead</c>, where there is one - or
    /// leaves it unchanged when VAD finds no jingle there at all, in which case the lead the
    /// default-mode mark already carries is what the mark keeps.
    /// <para>
    /// Only when the mark the walk started from is of unknown accuracy - precise marking ran but
    /// never actually heard the phrase, or was turned off entirely by --quick-marks - is the walked
    /// result then double-checked against direct re-transcription by <see
    /// cref="PreciseMarkRefiner.VerifyMarkBeforeJingleAsync"/>. Against a confirmed mark that check
    /// cannot tell a failed walk from a correct one and is skipped; see its own remarks for why.
    /// </para>
    /// <para>
    /// Either way the same backward-only quiet-point snap precise marking's own final step applies
    /// runs on the result, so a player seeking to a --mark-before-jingle mark starts in
    /// near-silence just as it would for any other mark.
    /// </para>
    /// </summary>
    /// <param name="mark">The mark to walk backward from.</param>
    /// <param name="markConfirmed">Whether <paramref name="mark"/> is a precise-marking-confirmed
    /// announcement onset (<see cref="PreciseMarkResult.PhraseHeard"/>); false both when precise
    /// marking could not confirm the phrase and when --quick-marks skipped it altogether.</param>
    /// <param name="ctx">The file's mark-placement constants.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<double> ApplyMarkBeforeJingleAsync(
        double mark, bool markConfirmed, MarkContext ctx, CancellationToken ct)
    {
        var walked = ComputeMarkBeforeJingle(
            mark, ctx.AllSilences, ctx.SpeechSegments, ctx.Transcript, _options.MarkLeadSeconds);
        if (walked != mark)
            _log?.Invoke($"--mark-before-jingle: walked mark back from {FormatTimestamp(mark)} to {FormatTimestamp(walked)}");

        var verified = walked;
        if (_options.PreciseMark && !markConfirmed)
            verified = await _refiner.VerifyMarkBeforeJingleAsync(
                walked, mark, ctx.File, ctx.InputDecoder, ctx.Announcement, ctx.SpeechSegments, ct);

        var quietest = await _refiner.SnapToQuietestPointAsync(verified, ctx.File, ctx.InputDecoder, ct);
        if (quietest != verified)
            _log?.Invoke($"--mark-before-jingle: nudged {FormatTimestamp(verified)} to quieter {FormatTimestamp(quietest)}");
        return quietest;
    }

    /// <summary>
    /// Records, for one detected chapter, the length of the silence and (when the VAD pre-pass ran)
    /// the jingle that preceded its phrase - the raw material for the per-file minimum-silence and
    /// maximum-jingle statistics. The silence recorded is the one the mark anchored to: without a
    /// VAD pre-pass, the silence directly before the phrase; with one, the silence leading the
    /// jingle (a jingle framed between two silences contributes only its <em>leading</em> one, per
    /// <see cref="ResolveJingleAnchor"/>), or none for a silence-less jingle. The jingle length runs
    /// from its true start (that leading silence's end, else the region start) up to the phrase,
    /// clipped at the region end so an announcement spoken inside the jingle - or a merge-inflated
    /// region end - never overstates it, matching the --max-jingle-length auto observation.
    /// </summary>
    /// <param name="number">The detected chapter number.</param>
    /// <param name="precedingSilence">The silence the mark anchored to, or null when none.</param>
    /// <param name="jingleRegion">The jingle region preceding the phrase, or null.</param>
    /// <param name="phraseAbs">Absolute phrase start time, the clip point for the jingle length.</param>
    private void Record(
        int number, Silence? precedingSilence, NonSpeechRegion? jingleRegion, double phraseAbs)
    {
        if (precedingSilence is { } s && s.EndSeconds > s.StartSeconds)
            _silenceSeconds[number] = s.EndSeconds - s.StartSeconds;
        if (jingleRegion is { } r)
        {
            var jingleStart = precedingSilence?.EndSeconds ?? r.StartSeconds;
            _jingleSeconds[number] = Math.Max(0, Math.Min(r.EndSeconds, phraseAbs) - jingleStart);
        }
    }
}
