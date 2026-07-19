// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Text;
using System.Text.RegularExpressions;

namespace ABChapterize;

/// <summary>A detected chapter start: number plus position in the file.</summary>
/// <param name="Number">Chapter number as spoken/parsed.</param>
/// <param name="TimeSeconds">Position of the chapter marking in seconds.</param>
public readonly record struct DetectedChapter(int Number, double TimeSeconds);

/// <summary>Outcome of chapter detection for one file.</summary>
/// <param name="Chapters">Detected chapters in chronological order; empty when none were found.</param>
/// <param name="GapRemains">True when a chapter sequence gap could not be resolved; the file must be left unchanged.</param>
/// <param name="MissingNumbers">The chapter numbers that could not be located (only when <paramref name="GapRemains"/>).</param>
public readonly record struct DetectionResult(
    IReadOnlyList<DetectedChapter> Chapters, bool GapRemains, IReadOnlyList<int> MissingNumbers);

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

        var found = new List<DetectedChapter>();
        foreach (var start in probeStarts)
        {
            ct.ThrowIfCancellationRequested();
            var samples = await _audio.DecodePcmAsync(file, start,
                Math.Min(probeSeconds, info.DurationSeconds - start), info.InputDecoder, ct);
            var segments = await _transcriber.TranscribeAsync(samples, ct);
            LogTranscript($"probe @{FormatTimestamp(start)}", segments);

            foreach (var match in FindPhraseMatches(segments))
            {
                if (!_options.Jingle && match.PhraseStartSeconds > PhraseLatestStart)
                    continue; // without a jingle the phrase must directly follow the silence
                var time = _options.Jingle
                    ? AnchorJingleMark(start, match.PhraseStartSeconds, silences)
                    : Math.Max(0, start + (start == 0 ? match.PhraseStartSeconds : 0));
                _log?.Invoke($"chapter {match.Number} detected, mark placed at {FormatTimestamp(time)}");
                found.Add(new DetectedChapter(match.Number, time));
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
                silences, bytesPerSecond, work, ct);
            chapters = Normalize(chapters.Concat(fills).ToList());
            work.ChaptersFound = chapters.Count;
        }

        // Final consistency check: internal gaps that remain are fatal for this file.
        var missing = new List<int>();
        for (var i = 1; i < chapters.Count; i++)
            for (var n = chapters[i - 1].Number + 1; n < chapters[i].Number; n++)
                missing.Add(n);

        return new DetectionResult(chapters, missing.Count > 0, missing);
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
    private readonly record struct PhraseMatch(int Number, double PhraseStartSeconds);

    /// <summary>
    /// Searches the transcribed segments for the chapter phrase and parses the chapter number,
    /// either from the regexp capturing group or from the words following the phrase
    /// ("Chapter Seven"); when neither yields a number, the words directly preceding the
    /// phrase are tried ("Erstes Kapitel", "Birinci Bölüm").
    /// </summary>
    private IEnumerable<PhraseMatch> FindPhraseMatches(List<TranscriptSegment> segments)
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

        foreach (Match m in _options.PhraseRegex.Matches(text))
        {
            int number;
            if (_options.PhraseHasNumberGroup && m.Groups.Count > 1 && m.Groups[1].Success)
            {
                if (!int.TryParse(m.Groups[1].Value, out number))
                    continue;
            }
            else
            {
                var tail = text[(m.Index + m.Length)..];
                if (tail.Length > 80)
                    tail = tail[..80];
                if (!NumberWordParser.TryExtractNumber(tail, _options.Language, out number))
                {
                    // No number after the phrase - try the ordinal-first announcement
                    // order ("Erstes Kapitel", "2. Kapitel", "Birinci Bölüm").
                    var head = text[..m.Index];
                    if (head.Length > 80)
                        head = head[^80..];
                    if (!NumberWordParser.TryExtractNumberBefore(head, _options.Language, out number))
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
            yield return new PhraseMatch(number, segments[segIndex].StartSeconds);
        }
    }

    /// <summary>
    /// Fully transcribes a region of the file in overlapping chunks and returns all chapter
    /// starts found in it. Used to close sequence gaps left by the silence-probe fast path.
    /// </summary>
    private async Task<List<DetectedChapter>> TranscribeRegionAsync(
        string file, MediaInfo info, double fromSeconds, double toSeconds,
        List<Silence> silences, double bytesPerSecond, WorkTracker work, CancellationToken ct)
    {
        var found = new List<DetectedChapter>();
        for (var chunkStart = fromSeconds; chunkStart < toSeconds; chunkStart += GapChunkSeconds - GapChunkOverlapSeconds)
        {
            ct.ThrowIfCancellationRequested();
            var chunkLen = Math.Min(GapChunkSeconds, toSeconds - chunkStart);
            var samples = await _audio.DecodePcmAsync(file, chunkStart, chunkLen, info.InputDecoder, ct);
            var segments = await _transcriber.TranscribeAsync(samples, ct);
            LogTranscript($"gap chunk @{FormatTimestamp(chunkStart)}", segments);

            foreach (var match in FindPhraseMatches(segments))
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
                _log?.Invoke($"chapter {match.Number} found in gap, mark placed at {FormatTimestamp(time)}");
                found.Add(new DetectedChapter(match.Number, time));
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
                segments.Select(s => $"{s.StartSeconds:0.0}-{s.EndSeconds:0.0} \"{s.Text.Trim()}\"")));
    }

    /// <summary>Formats a position in the file as h:mm:ss.ff for log messages.</summary>
    /// <param name="seconds">Position in seconds.</param>
    private static string FormatTimestamp(double seconds)
        => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"h\:mm\:ss\.ff");
}
