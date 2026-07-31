// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using ABChapterize.Cli;
using ABChapterize.Transcription;
using ABChapterize.Vad;
using System.Text.RegularExpressions;
using static ABChapterize.Detection.DetectionFormatting;
using static ABChapterize.Detection.JingleGeometry;

namespace ABChapterize.Detection;

/// <summary>
/// The per-file constants a mark placement needs, gathered so <see cref="MarkPlacer.PlaceAsync"/>
/// does not take them one by one. Only <paramref name="Transcript"/> varies from mark to mark.
/// </summary>
/// <param name="File">Path of the audio file.</param>
/// <param name="InputDecoder">Explicit input decoder to force, or null.</param>
/// <param name="PhraseRegex">The announcement the corrections look for at the mark: the chapter
/// phrase for a numbered chapter, the matching prologue/epilogue phrase for a named mark. Per mark
/// rather than per file, since a run detects all three at once and a correction that re-transcribed
/// a prologue while looking for "chapter" could only ever fail to confirm it.</param>
/// <param name="AllSilences">Every silence Pass 1 stored, for the --mark-before-jingle walk.</param>
/// <param name="SpeechSegments">Raw VAD speech segments for the whole file, for that walk and its
/// verification search.</param>
/// <param name="Transcript">The window this mark's phrase was found in - its segments in absolute
/// file time, so the backward walk can tell genuine preceding narration apart from a musical or
/// vocal transient inside the jingle (see <see cref="JingleGeometry.IsGenuineSpeech"/>), and the
/// span of audio they came from. That span's end doubles as what precise marking anchors its
/// search against: the window's own planned end, not the last segment's timestamp, which is
/// exactly the kind of figure precise marking exists because it cannot trust. It is known to lie
/// past the announcement, since the announcement was found inside it, which is the one thing
/// <see cref="PreciseMarkRefiner.RefinePreciseMarkAsync"/> needs of it.</param>
internal readonly record struct MarkContext(
    string File, string? InputDecoder, Regex PhraseRegex, List<Silence> AllSilences,
    List<SpeechSegment> SpeechSegments, TranscriptWindow Transcript);

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
    internal MarkPlacer(IAudioSource audio, CliOptions options, DetectionLog log,
        Func<float[], CancellationToken, Task<List<TranscriptSegment>>> transcribeCounting)
    {
        _audio = audio;
        _options = options;
        _log = log.Fanout();
        _refiner = new PreciseMarkRefiner(audio, options, log, transcribeCounting);
    }

    /// <summary>
    /// Finishes one chapter's mark: precise marking corrects the default-mode position by direct
    /// re-transcription (unless --quick-marks turned it off), --mark-before-jingle then walks the
    /// result back to the jingle's leading edge, and the chapter's silence/jingle measurements are
    /// recorded either way - the auto mechanisms and statistics stay meaningful even when marks are
    /// placed at the plain default offset.
    /// </summary>
    /// <param name="number">The detected chapter number, or null for a named (prologue/epilogue)
    /// mark - which is placed identically but contributes nothing to the per-chapter measurements,
    /// since those describe the book's regular chapter breaks and a prologue or epilogue transition
    /// is exactly the atypical one the "inter-chapter" statistics already exclude chapter 1 for.</param>
    /// <param name="defaultMark">The default-mode mark the caller's own pass computed.</param>
    /// <param name="phraseAbs">Absolute phrase start time, the clip point for the jingle length.</param>
    /// <param name="phraseEndAbs">Absolute end of the transcript segment(s) the phrase was matched
    /// in. Together with <paramref name="phraseAbs"/> this brackets where the announcement can
    /// actually be - see <see cref="PreciseMarkRefiner.RefinePreciseMarkAsync"/>.</param>
    /// <param name="statSilence">The silence the mark anchored to, or null when none.</param>
    /// <param name="statRegion">The jingle region preceding the phrase, or null (always null when
    /// the VAD pre-pass did not run).</param>
    /// <param name="ctx">The file's mark-placement constants.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The final mark position.</returns>
    internal async Task<double> PlaceAsync(
        int? number, double defaultMark, double phraseAbs, double phraseEndAbs, Silence? statSilence,
        NonSpeechRegion? statRegion, MarkContext ctx, CancellationToken ct)
    {
        var time = defaultMark;
        var phraseHeard = false;
        if (_options.PreciseMark)
            (time, phraseHeard) = await _refiner.RefinePreciseMarkAsync(
                time, ctx.File, ctx.InputDecoder, ctx.PhraseRegex,
                phraseAbs, phraseEndAbs, ctx.Transcript.EndSeconds, ct);
        if (_options.MarkBeforeJingle)
            time = await ApplyMarkBeforeJingleAsync(time, phraseHeard, ctx, ct);
        if (number is { } chapterNumber)
            Record(chapterNumber, statSilence, statRegion, phraseAbs);
        return time;
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
    /// leading edge, or leaves it unchanged when VAD finds no jingle there at all.
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
        var walked = ComputeMarkBeforeJingle(mark, ctx.AllSilences, ctx.SpeechSegments, ctx.Transcript);
        if (walked != mark)
            _log?.Invoke($"--mark-before-jingle: walked mark back from {FormatTimestamp(mark)} to {FormatTimestamp(walked)}");

        var verified = walked;
        if (_options.PreciseMark && !markConfirmed)
            verified = await _refiner.VerifyMarkBeforeJingleAsync(
                walked, mark, ctx.File, ctx.InputDecoder, ctx.PhraseRegex, ctx.SpeechSegments, ct);

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
