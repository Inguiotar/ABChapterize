// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using ABChapterize.Cli;
using ABChapterize.Language;
using ABChapterize.Transcription;
using ABChapterize.Vad;
using static ABChapterize.Detection.DetectionFormatting;
using static ABChapterize.Detection.DetectionTuning;

namespace ABChapterize.Detection;

/// <summary>
/// A file's settled language resolution. Always resolved: <see cref="LanguageResolver"/> runs to a
/// profile before Pass 2 begins, so no pass ever has to cope with "not known yet" - which is what
/// the detection passes' language handling used to be threaded with, back when the first probe
/// window doubled as the language sample.
/// </summary>
/// <param name="Profile">The language profile the file is processed with.</param>
/// <param name="DetectedLanguage">What Whisper's language detector reported, or null when it never
/// ran (an explicit <c>--lang</c>) or had nothing to say. May disagree with
/// <paramref name="Profile"/>, which is the interesting case: it means detection was overruled by
/// the vote or by the English fallback.</param>
/// <param name="DetectedProbability">Its confidence in <paramref name="DetectedLanguage"/>;
/// 0 when that is null.</param>
internal readonly record struct LanguageState(
    LanguageProfile Profile, string? DetectedLanguage, double DetectedProbability);

/// <summary>
/// Resolves which language an audiobook is in, once per file, for <c>--lang auto</c>.
/// <para>
/// The stakes are higher than "a title comes out in the wrong language": the resolved profile
/// supplies the chapter phrase every pass matches against, so a German book resolved as English
/// looks for <c>/chapter/</c> and cannot see "Kapitel 25" at all. That is not hypothetical - it
/// is what this class was written for. "Das Mutantenkorps" (15:35:42, 2026-08-03, build 233):
/// detection sampled the file's first probe window, which is 47 s of label music, got
/// <c>en p=0.36</c>, fell back to English, and ran the whole book that way. Whisper, told the
/// audio was English while hearing German, mostly obliged with English renderings - 292
/// "Chapter N" against 17 "Kapitel N" across the log - so 29 of 30 chapters were still found.
/// Chapter 25 was the one whose announcement never once came back in English. It was heard,
/// correctly, three times (pass 2.5 at p=0.81, Pass 3 at p=0.79, Pass 3.5 at p=0.80) and
/// discarded every time, at the cost of a further 40 minutes of Pass 2.5/3/3.5 hunting a phrase
/// that was already sitting in their own transcripts.
/// </para>
/// <para>
/// Two defences follow from that, and they address different halves of the failure. Sampling
/// (<see cref="SpeechPositions"/> and friends) attacks the cause: never hand the detector the
/// file's opening, which on an audiobook is label music, a copyright card or a title read over a
/// bed far more often than it is narration. Re-probing attacks what is left: a single sample can
/// still land on a song, a foreign-language quotation or a stretch of shouting, so a weak answer
/// is retried elsewhere in the book rather than believed.
/// </para>
/// <para>
/// What this deliberately does not do is decide per region. The language belongs to the file;
/// letting it drift mid-book would mean a phrase that matches in chapter 3 and silently stops
/// matching in chapter 20, which is a worse failure than this class exists to fix because
/// nothing about it looks wrong in a log.
/// </para>
/// </summary>
internal sealed class LanguageResolver
{
    private readonly CliOptions _options;
    private readonly IAudioSource _audio;
    private readonly ITranscriber _transcriber;
    private readonly Action<string>? _log;

    /// <summary>Creates a resolver bound to one run's options and tools.</summary>
    /// <param name="options">Validated command line options.</param>
    /// <param name="audio">Audio source the sample windows are decoded from.</param>
    /// <param name="transcriber">The recognizer whose language detector answers each probe.</param>
    /// <param name="log">Sink for the per-probe log lines, or null when nothing is listening.</param>
    internal LanguageResolver(
        CliOptions options, IAudioSource audio, ITranscriber transcriber, Action<string>? log)
    {
        _options = options;
        _audio = audio;
        _transcriber = transcriber;
        _log = log;
    }

    /// <summary>One language-detection probe's answer.</summary>
    /// <param name="Position">Where in the file the sample was taken, for the log.</param>
    /// <param name="Language">Whisper's language guess, lower-cased; empty when it had none.</param>
    /// <param name="Probability">Its confidence in <paramref name="Language"/>.</param>
    private readonly record struct Attempt(double Position, string Language, double Probability);

    /// <summary>
    /// Resolves the file's language profile. With an explicit <c>--lang</c> the answer is
    /// <see cref="CliOptions.DefaultProfile"/> and no audio is touched at all; with
    /// <c>--lang auto</c> it is the outcome of the probe loop described on this class.
    /// </summary>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="info">The file's probed media info, for its duration and input decoder.</param>
    /// <param name="positions">Where to sample, best guess first - see <see cref="SpeechPositions"/>,
    /// <see cref="MarkingPositions"/> and <see cref="DurationPositions"/>. Only the first
    /// <see cref="DetectionTuning.AutoLanguageProbeAttempts"/> are ever used, and a position whose
    /// window decodes to nothing usable does not consume an attempt.</param>
    /// <param name="ct">Cancellation token.</param>
    internal async Task<LanguageState> ResolveAsync(
        string file, MediaInfo info, IEnumerable<double> positions, CancellationToken ct)
    {
        if (!_options.AutoLanguage)
            return new LanguageState(_options.DefaultProfile, null, 0);

        var attempts = new List<Attempt>();
        foreach (var position in positions)
        {
            if (attempts.Count >= AutoLanguageProbeAttempts)
                break;

            var length = Math.Min(AutoLanguageProbeSeconds, info.DurationSeconds - position);
            if (length <= 0)
                continue;
            var samples = await _audio.DecodePcmAsync(file, position, length, info.InputDecoder, ct);
            // Shorter than half a second is not audio the detector can say anything about. Skipping
            // without counting an attempt matters at the end of a short file, where several planned
            // positions can collapse onto the same unusable tail.
            if (samples.Length < FfmpegClient.SampleRate / 2)
                continue;

            var (detected, probability) = await _transcriber.DetectLanguageWithProbability(samples, ct);
            var attempt = new Attempt(
                position, (detected ?? "").Trim().ToLowerInvariant(), probability);
            attempts.Add(attempt);

            if (IsConclusive(attempt))
            {
                _log?.Invoke($"language auto-detected: {attempt.Language} (p={attempt.Probability:0.00}) " +
                             $"from the sample at {FormatTimestamp(position)}" +
                             (attempts.Count > 1 ? $", on attempt {attempts.Count}" : ""));
                return new LanguageState(
                    _options.ResolveProfile(attempt.Language), attempt.Language, attempt.Probability);
            }

            _log?.Invoke($"language probe at {FormatTimestamp(position)} inconclusive: " +
                         $"{(attempt.Language.Length > 0 ? attempt.Language : "?")} " +
                         $"(p={attempt.Probability:0.00}, below {AutoLanguageProbabilityThreshold:0.00})");
        }

        if (attempts.Count == 0)
        {
            _log?.Invoke("language auto-detection skipped (nothing decodable to probe); using en");
            return new LanguageState(_options.ResolveProfile("en"), null, 0);
        }

        return ResolveByVote(attempts);
    }

    /// <summary>
    /// A probe answer good enough to stop on. Both halves matter: Whisper returns an empty language
    /// with a probability attached often enough that the probability alone is not a usable test.
    /// </summary>
    /// <param name="attempt">The probe answer to judge.</param>
    private static bool IsConclusive(Attempt attempt)
        => attempt.Probability >= AutoLanguageProbabilityThreshold && attempt.Language.Length > 0;

    /// <summary>
    /// Settles the language when no single probe cleared
    /// <see cref="DetectionTuning.AutoLanguageProbabilityThreshold"/>, by counting how often each
    /// language was named across every attempt.
    /// <para>
    /// The winner needs a plurality with nobody level with it, not an outright majority. Five
    /// samples of one book rarely agree that cleanly, and the realistic scattered result -
    /// de, de, en, fr, it - is one where German is plainly the answer and a strict &gt;50% rule
    /// would throw it away for English. A genuine tie for first place carries no signal at all,
    /// and falls back to English like an unsupported language would.
    /// </para>
    /// <para>
    /// The probability that travels back out is the winner's best, not the vote's: it is reported
    /// as Whisper's own confidence, and averaging or summing it would invent a number no probe
    /// ever produced.
    /// </para>
    /// </summary>
    /// <param name="attempts">Every probe answer, in the order they were taken; never empty.</param>
    private LanguageState ResolveByVote(IReadOnlyList<Attempt> attempts)
    {
        var best = attempts.MaxBy(a => a.Probability);
        var tally = attempts
            .Where(a => a.Language.Length > 0)
            .GroupBy(a => a.Language)
            .Select(g => (Language: g.Key, Votes: g.Count(), Best: g.Max(a => a.Probability)))
            .OrderByDescending(t => t.Votes)
            .ThenByDescending(t => t.Best)
            .ToList();

        var decided = tally.Count == 1 || (tally.Count > 1 && tally[0].Votes > tally[1].Votes);
        if (!decided)
        {
            _log?.Invoke($"language auto-detection inconclusive after {attempts.Count} probe(s) " +
                         $"({DescribeTally(tally)}); no clear winner, falling back to en");
            return new LanguageState(_options.ResolveProfile("en"), NullIfEmpty(best.Language), best.Probability);
        }

        var winner = tally[0];
        _log?.Invoke($"language auto-detection inconclusive after {attempts.Count} probe(s) " +
                     $"({DescribeTally(tally)}); {winner.Language} named most often " +
                     $"(best p={winner.Best:0.00})");
        return new LanguageState(_options.ResolveProfile(winner.Language), winner.Language, winner.Best);
    }

    /// <summary>Renders a vote tally for the log, strongest first.</summary>
    /// <param name="tally">The grouped attempts, already ordered.</param>
    private static string DescribeTally(IEnumerable<(string Language, int Votes, double Best)> tally)
    {
        var parts = tally.Select(t => $"{t.Language} x{t.Votes}").ToList();
        return parts.Count > 0 ? string.Join(", ", parts) : "nothing recognized";
    }

    /// <summary>Keeps <see cref="LanguageState.DetectedLanguage"/>'s "the detector said nothing"
    /// case as null rather than an empty string, which is what its consumers test for.</summary>
    /// <param name="language">The raw language guess.</param>
    private static string? NullIfEmpty(string language) => language.Length > 0 ? language : null;

    /// <summary>
    /// Sample positions for a book whose VAD pre-pass ran: the start of a speech-dense window near
    /// each of <see cref="Anchors"/>.
    /// <para>
    /// Two things are being asked for at once. The window must <em>start</em> on speech, because
    /// Whisper's language detector reads the first 30 s of what it is handed and nothing after -
    /// speech that begins 25 s in buys almost nothing. And the window must be mostly speech
    /// throughout (<see cref="DetectionTuning.AutoLanguageMinSpeechInWindowSeconds"/>), which is
    /// what rules out an announcement over a jingle, a sung line, or a lone shout in a quiet scene.
    /// </para>
    /// </summary>
    /// <param name="speech">VAD speech segments for the whole file, in chronological order.</param>
    /// <param name="durationSeconds">The file's duration.</param>
    internal static IEnumerable<double> SpeechPositions(
        IReadOnlyList<SpeechSegment> speech, double durationSeconds)
    {
        var usable = new List<double>();
        for (var i = 0; i < speech.Count; i++)
            if (SpeechWithinWindow(speech, i) >= AutoLanguageMinSpeechInWindowSeconds)
                usable.Add(speech[i].StartSeconds);

        // Nothing in the book is that speech-dense - a heavily scored recording, or a VAD reading
        // it conservatively. The VAD still knows where the speech is, which beats blind positions
        // that know nothing at all, so sample the densest windows it did find.
        if (usable.Count == 0)
            usable = [.. Enumerable.Range(0, speech.Count)
                .OrderByDescending(i => SpeechWithinWindow(speech, i))
                .Take(AutoLanguageProbeAttempts)
                .Select(i => speech[i].StartSeconds)
                .Order()];
        if (usable.Count == 0)
            return DurationPositions(durationSeconds);

        return Distinct(Anchors(durationSeconds)
            .Select(anchor => usable.MinBy(start => Math.Abs(start - anchor))));
    }

    /// <summary>
    /// Total speech in the <see cref="DetectionTuning.AutoLanguageProbeSeconds"/> window opening at
    /// one segment's start. A forward walk rather than a query over the whole list: segments are
    /// chronological and sentence-sized, so it visits a handful each time and stops.
    /// </summary>
    /// <param name="speech">VAD speech segments for the whole file, in chronological order.</param>
    /// <param name="index">Index of the segment the window starts at.</param>
    private static double SpeechWithinWindow(IReadOnlyList<SpeechSegment> speech, int index)
    {
        var start = speech[index].StartSeconds;
        var end = start + AutoLanguageProbeSeconds;
        var total = 0.0;
        for (var i = index; i < speech.Count && speech[i].StartSeconds < end; i++)
            total += Math.Min(speech[i].EndSeconds, end) - speech[i].StartSeconds;
        return total;
    }

    /// <summary>
    /// Sample positions for the <c>--verify</c> and resume paths, which have no VAD pass but do
    /// have the file's committed chapter markings. A marking is a better anchor than any fraction
    /// of the duration - it is a known chapter start, so narration is guaranteed to follow -
    /// provided the window skips past the announcement and whatever jingle wraps it, which is what
    /// <see cref="DetectionTuning.AutoLanguageMarkingOffsetSeconds"/> is for.
    /// </summary>
    /// <param name="markings">The file's existing chapter markings, in chronological order.</param>
    /// <param name="durationSeconds">The file's duration.</param>
    internal static IEnumerable<double> MarkingPositions(
        IReadOnlyList<Chapter> markings, double durationSeconds)
    {
        if (markings.Count == 0)
            return DurationPositions(durationSeconds);

        return Distinct(Anchors(durationSeconds)
            .Select(anchor => markings.MinBy(m => Math.Abs(m.StartSeconds - anchor)).StartSeconds
                              + AutoLanguageMarkingOffsetSeconds)
            .Where(position => position < durationSeconds));
    }

    /// <summary>
    /// Sample positions with nothing but the duration to go on - no VAD pass and no markings.
    /// Blind, but blind at <see cref="Anchors"/> rather than at zero, which is the whole point:
    /// any of these is a better bet than the file's opening seconds.
    /// </summary>
    /// <param name="durationSeconds">The file's duration.</param>
    internal static IEnumerable<double> DurationPositions(double durationSeconds)
        => Distinct(Anchors(durationSeconds));

    /// <summary>
    /// Where in a book to look, as absolute seconds, best guess first.
    /// <para>
    /// The first anchor sits a fifth of the way in: past the label music, the copyright card, the
    /// title, the cast list and any prologue, and still far from the end, where the closing credits
    /// and the "you have been listening to" outro live - both ends of an audiobook are where the
    /// non-narration material clusters. The rest spread outward over the middle so that five
    /// probes sample five different parts of the book. Two anchors landing on the same speech run
    /// is not a problem: <see cref="Distinct"/> collapses them and the loop simply makes fewer
    /// attempts.
    /// </para>
    /// </summary>
    /// <param name="durationSeconds">The file's duration.</param>
    private static IEnumerable<double> Anchors(double durationSeconds)
        => new[] { 0.20, 0.45, 0.70, 0.10, 0.85 }.Select(f => f * durationSeconds);

    /// <summary>Drops positions that repeat one already yielded, to the second - two anchors
    /// resolving to the same speech run or the same marking must not spend two attempts on
    /// identical audio.</summary>
    /// <param name="positions">The candidate positions, in the order they should be tried.</param>
    private static IEnumerable<double> Distinct(IEnumerable<double> positions)
    {
        var seen = new HashSet<long>();
        foreach (var position in positions)
        {
            var clamped = Math.Max(0, position);
            if (seen.Add((long)Math.Round(clamped)))
                yield return clamped;
        }
    }
}
