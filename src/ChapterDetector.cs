// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Text;
using System.Text.RegularExpressions;

namespace ABChapterize;

/// <summary>A detected chapter start: number plus position in the file.</summary>
/// <param name="Number">Chapter number as spoken/parsed.</param>
/// <param name="TimeSeconds">Position of the chapter marking in seconds.</param>
/// <param name="Confidence">Whisper's probability for the segment the chapter number was parsed
/// from (0-1); 1.0 when unknown. Below <see cref="ChapterDetector.LowConfidenceThreshold"/> the
/// number surfaces in <see cref="DetectionResult.LowConfidenceNumbers"/>.</param>
public readonly record struct DetectedChapter(int Number, double TimeSeconds, double Confidence = 1.0);

/// <summary>Outcome of chapter detection for one file.</summary>
/// <param name="Chapters">Detected chapters in chronological order; empty when none were found.</param>
/// <param name="GapRemains">True when a chapter sequence gap could not be resolved; the file must be left unchanged.</param>
/// <param name="MissingNumbers">The chapter numbers that could not be located (only when <paramref name="GapRemains"/>).</param>
/// <param name="LowConfidenceNumbers">Chapter numbers whose Whisper probability fell below
/// <see cref="ChapterDetector.LowConfidenceThreshold"/> - worth a manual spot-check.</param>
/// <param name="Profile">The language profile actually used for this file - the resolved
/// per-file profile with <c>--lang auto</c>, or the run's fixed <see cref="CliOptions.DefaultProfile"/>
/// otherwise.</param>
/// <param name="DetectedLanguage">Whisper's raw language guess with <c>--lang auto</c>; null
/// when auto-detection was not active, or was skipped because the file was too short to probe.</param>
/// <param name="DetectedProbability">Whisper's probability for <paramref name="DetectedLanguage"/>;
/// 0 when <paramref name="DetectedLanguage"/> is null. Note this may differ from
/// <see cref="Profile"/>'s language when the probability fell below
/// <see cref="ChapterDetector.AutoLanguageProbabilityThreshold"/> and the run fell back to English.</param>
public readonly record struct DetectionResult(
    IReadOnlyList<DetectedChapter> Chapters, bool GapRemains, IReadOnlyList<int> MissingNumbers,
    IReadOnlyList<int> LowConfidenceNumbers, LanguageProfile Profile,
    string? DetectedLanguage, double DetectedProbability);

/// <summary>Outcome of checking pre-existing chapter markings against the audio (--verify).</summary>
/// <param name="Passed">True when every checkable marking was confirmed; also true when none
/// of the file's markings had a parseable expected number (nothing to disprove).</param>
/// <param name="Checked">Number of markings that had a parseable expected number and were
/// actually probed. Markings without one (e.g. a prelude/intro entry) are not counted.</param>
/// <param name="Failed">Of <paramref name="Checked"/>, how many could not be confirmed.</param>
public readonly record struct VerifyResult(bool Passed, int Checked, int Failed);

/// <summary>
/// Finds chapter starts in an audiobook. Fast path: detect longer-than-usual silences and
/// probe the audio following each silence with Whisper. If the resulting chapter numbers
/// contain sequence gaps, the audio between the mismatched markings is fully transcribed.
/// </summary>
public sealed class ChapterDetector
{
    /// <summary>Noise floor in dBFS for silence detection.</summary>
    private const int SilenceNoiseDb = -35;

    /// <summary>Probe window length in seconds when no jingle is expected.
    /// With --jingle the window is --max-jingle-length seconds instead.</summary>
    private const double ProbeSecondsPlain = 12;

    /// <summary>Without a jingle the phrase must start within this many seconds after the silence.</summary>
    private const double PhraseLatestStart = 5.0;

    /// <summary>Flat margin added to --max-jingle-length so the phrase after the jingle
    /// still fits into the probe window.</summary>
    private const double PhraseMarginSeconds = 5.0;

    /// <summary>Chapter marks are placed this many seconds before a jingle (per specification).</summary>
    private const double JingleLeadSeconds = 0.5;

    /// <summary>Chunk length in seconds for full transcription of gap regions.</summary>
    private const double GapChunkSeconds = 600;

    /// <summary>Overlap between gap transcription chunks so no phrase is cut in half.</summary>
    private const double GapChunkOverlapSeconds = 10;

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

    /// <summary>How far before a pre-existing chapter marking's own timestamp --verify starts
    /// probing - the marking may sit slightly after the phrase actually started.</summary>
    private const double VerifyMarginBeforeSeconds = 5;

    /// <summary>Total length of the --verify probe window, starting <see
    /// cref="VerifyMarginBeforeSeconds"/> before the marking. Comparable in scale to the plain
    /// post-silence probe window (<see cref="ProbeSecondsPlain"/>).</summary>
    private const double VerifyWindowSeconds = 15;

    private readonly CliOptions _options;
    private readonly IAudioSource _audio;
    private readonly ITranscriber _transcriber;

    /// <summary>Per-file --verbose log sink set by <see cref="DetectAsync"/>; null when not verbose.</summary>
    private Action<string>? _log;

    /// <summary>Creates a detector bound to the given tools and options.</summary>
    /// <param name="options">Validated command line options.</param>
    /// <param name="audio">Audio source used for silence detection and PCM decoding.</param>
    /// <param name="transcriber">Loaded speech recognizer.</param>
    public ChapterDetector(CliOptions options, IAudioSource audio, ITranscriber transcriber)
    {
        _options = options;
        _audio = audio;
        _transcriber = transcriber;
    }

    /// <summary>
    /// Runs the complete detection pipeline for one file.
    /// </summary>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="info">Probe result of the file.</param>
    /// <param name="work">Progress tracker fed with processed bytes.</param>
    /// <param name="log">Sink for --verbose log messages, or null when not verbose.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<DetectionResult> DetectAsync(
        string file, MediaInfo info, WorkTracker work, Action<string>? log, CancellationToken ct)
    {
        _log = log;
        var bytesPerSecond = info.DurationSeconds > 0 ? info.SizeBytes / info.DurationSeconds : 0;
        var probeSeconds = _options.Jingle
            ? _options.MaxJingleSeconds + PhraseMarginSeconds
            : ProbeSecondsPlain;

        // Pass 1: silence scan (one full pass over the file).
        work.BeginPhase("Pass 1", info.SizeBytes);
        var silences = await _audio.DetectSilencesAsync(
            file, info.DurationSeconds, _options.MinSilenceSeconds, SilenceNoiseDb,
            seconds => work.SetPhaseProgress((long)(seconds * bytesPerSecond)), info.InputDecoder, ct);

        _log?.Invoke($"Pass 1: {silences.Count} silence(s) of >= {_options.MinSilenceSeconds:0.#} s found");

        // Pass 2: probe the beginning of the file and the end of every silence.
        var probeStarts = new List<double> { 0 };
        probeStarts.AddRange(silences
            .Where(s => s.EndSeconds < info.DurationSeconds - 1)
            .Select(s => s.EndSeconds));

        var probeBytes = (long)(probeSeconds * bytesPerSecond);
        work.BeginPhase("Pass 2", probeBytes * probeStarts.Count);

        // With --lang auto, the language is resolved once per file, from the very first probe
        // window's samples (always at start 0, decoded below like any other window - no extra
        // decode needed) - then fixed for the rest of the file via ChangeLanguage, rather than
        // re-detected per probe, which would be both slower and inconsistent within one file.
        LanguageProfile? profile = null;
        string? detectedLanguage = null;
        var detectedProbability = 0.0;

        var found = new List<DetectedChapter>();
        foreach (var start in probeStarts)
        {
            ct.ThrowIfCancellationRequested();
            var samples = await _audio.DecodePcmAsync(file, start,
                Math.Min(probeSeconds, info.DurationSeconds - start), info.InputDecoder, ct);

            if (profile == null)
            {
                (profile, detectedLanguage, detectedProbability) = await ResolveLanguageAsync(samples, ct);
                _transcriber.ChangeLanguage(profile.Language);
            }

            var segments = await _transcriber.TranscribeAsync(samples, ct);
            LogTranscript($"probe @{FormatTimestamp(start)}", segments);

            foreach (var match in FindPhraseMatches(segments, profile))
            {
                if (!_options.Jingle && match.PhraseStartSeconds > PhraseLatestStart)
                    continue; // without a jingle the phrase must directly follow the silence
                var time = _options.Jingle
                    ? AnchorJingleMark(start, match.PhraseStartSeconds, silences)
                    : Math.Max(0, start + (start == 0 ? match.PhraseStartSeconds : 0));
                _log?.Invoke($"chapter {match.Number} detected, mark placed at {FormatTimestamp(time)} " +
                             $"(confidence {match.Confidence:0.00}){LowConfidenceNote(match.Confidence)}");
                found.Add(new DetectedChapter(match.Number, time, match.Confidence));
                work.ChaptersFound = CountDistinct(found);
                break; // one chapter per probe window
            }
            work.Advance(probeBytes);
        }

        var chapters = Normalize(found);

        // Pass 3 (only when needed): resolve sequence gaps by fully transcribing the regions
        // between mismatched markings (and before the first marking, if it is not chapter 1).
        var gaps = FindGaps(chapters, info.DurationSeconds);
        if (gaps.Count > 0)
        {
            work.BeginPhase("Pass 3",
                (long)(gaps.Sum(g => g.ToSeconds - g.FromSeconds) * bytesPerSecond));
        }
        foreach (var gap in gaps)
        {
            _log?.Invoke($"Pass 3: transcribing suspicious region " +
                         $"{FormatTimestamp(gap.FromSeconds)} - {FormatTimestamp(gap.ToSeconds)}");
            var fills = await TranscribeRegionAsync(file, info, gap.FromSeconds, gap.ToSeconds,
                silences, bytesPerSecond, work, profile!, ct);
            chapters = Normalize(chapters.Concat(fills).ToList());
            work.ChaptersFound = chapters.Count;
        }

        // Final consistency check: internal gaps that remain are fatal for this file.
        var missing = new List<int>();
        for (var i = 1; i < chapters.Count; i++)
            for (var n = chapters[i - 1].Number + 1; n < chapters[i].Number; n++)
                missing.Add(n);

        var lowConfidence = chapters
            .Where(c => c.Confidence < LowConfidenceThreshold)
            .Select(c => c.Number)
            .ToList();
        return new DetectionResult(
            chapters, missing.Count > 0, missing, lowConfidence,
            profile!, detectedLanguage, detectedProbability);
    }

    /// <summary>
    /// Checks pre-existing chapter markings against the audio (--verify), far cheaper than the
    /// full silence-scan/probe pipeline since only the markings' own timestamps are visited:
    /// for every marking whose title yields a parseable expected chapter number, a short window
    /// around its timestamp is probed with Whisper and checked for a phrase match with that
    /// number. A marking whose title has no parseable number (e.g. a prelude/intro entry
    /// without one) cannot be checked and does not count against or for the result; if none of
    /// a file's markings have a parseable number, verification trivially passes - there is
    /// nothing to disprove, so the file is left alone rather than needlessly re-detected.
    /// </summary>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="info">Probe result of the file, including its pre-existing chapter markings.</param>
    /// <param name="work">Progress tracker, advanced once per marking (checked or skipped).</param>
    /// <param name="log">Sink for --verbose log messages, or null when not verbose.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<VerifyResult> VerifyExistingChaptersAsync(
        string file, MediaInfo info, WorkTracker work, Action<string>? log, CancellationToken ct)
    {
        _log = log;
        // With an explicit --lang, the profile is known upfront - no probing needed to resolve
        // it, so title parsing below always uses the real language. With --lang auto it stays
        // null until the first marking that actually gets decoded, resolved the same way
        // DetectAsync resolves its own first probe window.
        LanguageProfile? profile = _options.AutoLanguage ? null : _options.DefaultProfile;
        if (profile != null)
            _transcriber.ChangeLanguage(profile.Language);
        var checkedCount = 0;
        var failed = 0;

        work.BeginPhase("Verify", info.ExistingChapters.Count);
        foreach (var marking in info.ExistingChapters)
        {
            ct.ThrowIfCancellationRequested();

            var windowStart = Math.Max(0, marking.StartSeconds - VerifyMarginBeforeSeconds);
            var windowLen = Math.Min(VerifyWindowSeconds, info.DurationSeconds - windowStart);
            if (windowLen <= 0)
            {
                work.Advance(1);
                continue;
            }

            var placeholderLanguage = profile?.Language ?? "en";
            if (!TryParseExpectedNumber(marking.Title, placeholderLanguage, out var expected))
            {
                work.Advance(1);
                continue;
            }

            var samples = await _audio.DecodePcmAsync(file, windowStart, windowLen, info.InputDecoder, ct);
            if (profile == null)
            {
                (profile, _, _) = await ResolveLanguageAsync(samples, ct);
                _transcriber.ChangeLanguage(profile.Language);
                // The number may have been parsed above using "en" as a placeholder before the
                // real language was known; re-parse now in case that made a difference.
                if (!TryParseExpectedNumber(marking.Title, profile.Language, out expected))
                {
                    work.Advance(1);
                    continue;
                }
            }

            var segments = await _transcriber.TranscribeAsync(samples, ct);
            LogTranscript($"verify @{FormatTimestamp(marking.StartSeconds)}", segments);

            checkedCount++;
            var confirmed = FindPhraseMatches(segments, profile).Any(m => m.Number == expected);
            _log?.Invoke(confirmed
                ? $"chapter {expected} marking at {FormatTimestamp(marking.StartSeconds)} confirmed"
                : $"chapter {expected} marking at {FormatTimestamp(marking.StartSeconds)} NOT confirmed - phrase not found nearby");
            if (!confirmed)
                failed++;
            work.Advance(1);
        }

        return new VerifyResult(failed == 0, checkedCount, failed);
    }

    /// <summary>
    /// Extracts an expected chapter number from a pre-existing marking's title: a plain digit
    /// sequence first (works regardless of language, and covers titles from other tools like
    /// "Chapter 05" or "05 - Title"), then a spelled-out number via <see cref="NumberWordParser"/>
    /// for the given language. Returns false when the title has no parseable number at all.
    /// </summary>
    private static bool TryParseExpectedNumber(string title, string language, out int number)
    {
        var digits = Regex.Match(title, @"\d+");
        if (digits.Success && int.TryParse(digits.Value, out number))
            return true;
        return NumberWordParser.TryExtractNumber(title, language, out number);
    }

    /// <summary>
    /// Resolves the language profile to use for this file: with an explicit --lang, always
    /// <see cref="CliOptions.DefaultProfile"/> (no detection call at all); with --lang auto,
    /// runs Whisper's language detector on a short clip and applies
    /// <see cref="AutoLanguageProbabilityThreshold"/>, falling back to English when the
    /// detection is inconclusive or the clip is too short to probe.
    /// </summary>
    /// <param name="samples">Decoded samples of the first probe window (start of the file).</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<(LanguageProfile Profile, string? DetectedLanguage, double DetectedProbability)> ResolveLanguageAsync(
        float[] samples, CancellationToken ct)
    {
        if (!_options.AutoLanguage)
            return (_options.DefaultProfile, null, 0);

        if (samples.Length < FfmpegClient.SampleRate / 2)
        {
            _log?.Invoke("language auto-detection skipped (clip too short); using en");
            return (_options.ResolveProfile("en"), null, 0);
        }

        var (detected, probability) = await _transcriber.DetectLanguageWithProbability(samples, ct);
        var conclusive = probability >= AutoLanguageProbabilityThreshold && !string.IsNullOrWhiteSpace(detected);
        var effective = conclusive ? detected.ToLowerInvariant() : "en";
        _log?.Invoke(conclusive
            ? $"language auto-detected: {effective} (p={probability:0.00})"
            : $"language auto-detection inconclusive ({detected ?? "?"} p={probability:0.00}); falling back to en");
        return (_options.ResolveProfile(effective), detected, probability);
    }

    /// <summary>A time region suspected to contain undetected chapter starts.</summary>
    /// <param name="FromSeconds">Region start.</param>
    /// <param name="ToSeconds">Region end.</param>
    internal readonly record struct GapRegion(double FromSeconds, double ToSeconds);

    /// <summary>
    /// Determines the regions to fully transcribe: between every pair of consecutive detected
    /// chapters whose numbers are not consecutive, and before the first chapter when its
    /// number is greater than 1. Internal for unit testing.
    /// </summary>
    internal static List<GapRegion> FindGaps(List<DetectedChapter> chapters, double duration)
    {
        var gaps = new List<GapRegion>();
        if (chapters.Count == 0)
            return gaps;
        if (chapters[0].Number > 1 && chapters[0].TimeSeconds > 30)
            gaps.Add(new GapRegion(0, chapters[0].TimeSeconds));
        for (var i = 1; i < chapters.Count; i++)
        {
            if (chapters[i].Number > chapters[i - 1].Number + 1)
                gaps.Add(new GapRegion(chapters[i - 1].TimeSeconds, chapters[i].TimeSeconds));
        }
        return gaps;
    }

    /// <summary>
    /// Sorts detections chronologically, removes duplicates of the same chapter number
    /// (keeping the earliest) and drops out-of-order regressions, which are typically
    /// in-text mentions like "as seen in chapter three". Internal for unit testing.
    /// </summary>
    internal static List<DetectedChapter> Normalize(List<DetectedChapter> found)
    {
        var result = new List<DetectedChapter>();
        foreach (var c in found.OrderBy(c => c.TimeSeconds).ThenBy(c => c.Number))
        {
            if (result.Count == 0 || c.Number > result[^1].Number)
                result.Add(c);
        }
        return result;
    }

    /// <summary>
    /// Determines where to place a jingle-mode chapter mark found in a probe window. The window
    /// can span the trailing speech of the previous chapter, the real inter-chapter silence, the
    /// jingle and the phrase, so anchoring at the probe's own silence would mark the chapter too
    /// early. Instead the mark is anchored at the latest detected silence that ends before the
    /// phrase, falling back to the window start (the end of the silence that triggered the probe).
    /// </summary>
    /// <param name="windowStart">Absolute start of the probe window in seconds.</param>
    /// <param name="phraseStartSeconds">Phrase start relative to the window start.</param>
    /// <param name="silences">All silences found by the silence scan.</param>
    private static double AnchorJingleMark(
        double windowStart, double phraseStartSeconds, List<Silence> silences)
    {
        var phraseAbs = windowStart + phraseStartSeconds;
        var silence = silences.LastOrDefault(s =>
            s.EndSeconds > windowStart && s.EndSeconds <= phraseAbs);
        var anchor = silence == default ? windowStart : silence.EndSeconds;
        return Math.Max(0, anchor - JingleLeadSeconds);
    }

    /// <summary>Counts distinct chapter numbers in a raw detection list (for progress display).</summary>
    private static int CountDistinct(List<DetectedChapter> found)
        => found.Select(c => c.Number).Distinct().Count();

    /// <summary>A phrase match inside a transcribed window.</summary>
    /// <param name="Number">Parsed chapter number.</param>
    /// <param name="PhraseStartSeconds">Phrase start relative to the window start.</param>
    /// <param name="Confidence">Whisper's probability for the segment the match was found in.</param>
    private readonly record struct PhraseMatch(int Number, double PhraseStartSeconds, double Confidence);

    /// <summary>
    /// Searches the transcribed segments for the chapter phrase and parses the chapter number,
    /// either from the regexp capturing group or from the words following the phrase
    /// ("Chapter Seven"); when neither yields a number, the words directly preceding the
    /// phrase are tried ("Erstes Kapitel", "Birinci Bölüm").
    /// </summary>
    private static IEnumerable<PhraseMatch> FindPhraseMatches(List<TranscriptSegment> segments, LanguageProfile profile)
    {
        if (segments.Count == 0)
            yield break;

        // Concatenate all segment texts and remember which character belongs to which segment
        // so a match position can be mapped back to a time.
        var sb = new StringBuilder();
        var segStartChar = new int[segments.Count];
        for (var i = 0; i < segments.Count; i++)
        {
            segStartChar[i] = sb.Length;
            sb.Append(segments[i].Text);
            sb.Append(' ');
        }
        var text = sb.ToString();

        foreach (Match m in profile.PhraseRegex.Matches(text))
        {
            int number;
            if (profile.PhraseHasNumberGroup && m.Groups.Count > 1 && m.Groups[1].Success)
            {
                if (!int.TryParse(m.Groups[1].Value, out number))
                    continue;
            }
            else
            {
                var tail = text[(m.Index + m.Length)..];
                if (tail.Length > 80)
                    tail = tail[..80];
                if (!NumberWordParser.TryExtractNumber(tail, profile.Language, out number))
                {
                    // No number after the phrase - try the ordinal-first announcement
                    // order ("Erstes Kapitel", "2. Kapitel", "Birinci Bölüm").
                    var head = text[..m.Index];
                    if (head.Length > 80)
                        head = head[^80..];
                    if (!NumberWordParser.TryExtractNumberBefore(head, profile.Language, out number))
                        continue;
                }
            }

            // Map the match position back to the segment that contains it.
            var segIndex = 0;
            for (var i = 0; i < segments.Count; i++)
            {
                if (segStartChar[i] <= m.Index)
                    segIndex = i;
                else
                    break;
            }
            yield return new PhraseMatch(number, segments[segIndex].StartSeconds, segments[segIndex].Probability);
        }
    }

    /// <summary>
    /// Fully transcribes a region of the file in overlapping chunks and returns all chapter
    /// starts found in it. Used to close sequence gaps left by the silence-probe fast path.
    /// </summary>
    private async Task<List<DetectedChapter>> TranscribeRegionAsync(
        string file, MediaInfo info, double fromSeconds, double toSeconds,
        List<Silence> silences, double bytesPerSecond, WorkTracker work, LanguageProfile profile, CancellationToken ct)
    {
        var found = new List<DetectedChapter>();
        for (var chunkStart = fromSeconds; chunkStart < toSeconds; chunkStart += GapChunkSeconds - GapChunkOverlapSeconds)
        {
            ct.ThrowIfCancellationRequested();
            var chunkLen = Math.Min(GapChunkSeconds, toSeconds - chunkStart);
            var samples = await _audio.DecodePcmAsync(file, chunkStart, chunkLen, info.InputDecoder, ct);
            var segments = await _transcriber.TranscribeAsync(samples, ct);
            LogTranscript($"gap chunk @{FormatTimestamp(chunkStart)}", segments);

            foreach (var match in FindPhraseMatches(segments, profile))
            {
                var phraseAbs = chunkStart + match.PhraseStartSeconds;
                double time;
                if (_options.Jingle)
                {
                    // The jingle sits between the preceding silence and the phrase; place the
                    // mark 0.5 s before the jingle, i.e. before the end of that silence.
                    var silence = silences.LastOrDefault(s =>
                        s.EndSeconds <= phraseAbs &&
                        s.EndSeconds >= phraseAbs - (_options.MaxJingleSeconds + PhraseMarginSeconds));
                    time = silence == default
                        ? Math.Max(0, phraseAbs - JingleLeadSeconds)
                        : Math.Max(0, silence.EndSeconds - JingleLeadSeconds);
                }
                else
                {
                    time = phraseAbs;
                }
                _log?.Invoke($"chapter {match.Number} found in gap, mark placed at {FormatTimestamp(time)} " +
                             $"(confidence {match.Confidence:0.00}){LowConfidenceNote(match.Confidence)}");
                found.Add(new DetectedChapter(match.Number, time, match.Confidence));
            }
            work.Advance((long)(chunkLen * bytesPerSecond));
        }
        return found;
    }

    /// <summary>
    /// Logs a Whisper transcript in --verbose mode: every segment with its start/end time
    /// relative to the decoded window. Does nothing when not verbose.
    /// </summary>
    /// <param name="context">Description of the decoded window, e.g. "probe @0:12:34.00".</param>
    /// <param name="segments">The transcribed segments.</param>
    private void LogTranscript(string context, List<TranscriptSegment> segments)
    {
        _log?.Invoke(segments.Count == 0
            ? $"{context}: (no speech recognized)"
            : $"{context}: " + string.Join(" | ",
                segments.Select(s =>
                    $"{s.StartSeconds:0.0}-{s.EndSeconds:0.0} (p={s.Probability:0.00}) \"{s.Text.Trim()}\"")));
    }

    /// <summary>Trailing note appended to a --verbose detection log line when the segment
    /// confidence is below <see cref="LowConfidenceThreshold"/>.</summary>
    private static string LowConfidenceNote(double confidence)
        => confidence < LowConfidenceThreshold ? " - LOW CONFIDENCE, worth a manual check" : "";

    /// <summary>Formats a position in the file as h:mm:ss.ff for log messages.</summary>
    /// <param name="seconds">Position in seconds.</param>
    private static string FormatTimestamp(double seconds)
        => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"h\:mm\:ss\.ff");
}
