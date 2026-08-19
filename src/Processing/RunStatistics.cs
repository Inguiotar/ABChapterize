// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using ABChapterize.Detection;
using ABChapterize.Formatting;

namespace ABChapterize.Processing;

/// <summary>Formats per-file and run-wide detection/confidence statistics for --verbose and
/// --summary reporting, and accumulates the run-wide totals across every file <see
/// cref="FileProcessor"/> processes - one file at a time, which is why nothing here is
/// synchronized.</summary>
internal sealed class RunStatistics
{
    /// <summary>Confidence stats (for --summary) across every chapter mark actually written,
    /// run-wide - not every probe attempted, only the ones that produced a mark.</summary>
    private double _confidenceMin = double.PositiveInfinity;
    private double _confidenceMax = double.NegativeInfinity;
    private double _confidenceSum;
    private int _confidenceCount;

    /// <summary>Run-wide detection statistics (for --summary), aggregated across every processed
    /// file: the shortest silence and longest jingle seen before any chapter (each both over all
    /// chapters and over the inter-chapter subset that excludes chapter 1), and the total audio
    /// fed to Whisper and time spent transcribing it, against the total run length processed. The
    /// silence/jingle extremes stay at their infinities when no file contributed one.</summary>
    private double _minPrecedingSilence = double.PositiveInfinity;
    private double _minInterChapterSilence = double.PositiveInfinity;
    private double _maxJingle = double.NegativeInfinity;
    private double _maxInterChapterJingle = double.NegativeInfinity;
    private double _whisperAudioSecondsTotal;
    private double _whisperTranscribeSecondsTotal;
    private double _runLengthSecondsTotal;

    /// <summary>Formats a duration as h:mm:ss (or m:ss below one hour) for the summary.</summary>
    /// <param name="t">The duration to format.</param>
    internal static string FormatTime(TimeSpan t) => TimeFormat.Duration(t);

    /// <summary>
    /// Builds the ", backup kept" clause appended to a per-file summary line. A pre-existing "*.bak"
    /// is never an error - see <see cref="FfmpegClient.WriteChaptersAsync"/> - it is left exactly as
    /// it is, and the wording says so without needing --verbose. Worth the extra words on the line:
    /// the backup then predates this run, so what it restores is not what the file looked like an
    /// hour ago, and a user who assumes otherwise finds out only after reverting.
    /// </summary>
    /// <param name="backupEnabled">--backup itself.</param>
    /// <param name="earlierKept">The value <see cref="FfmpegClient.WriteChaptersAsync"/> returned.</param>
    internal static string FormatBackupNote(bool backupEnabled, bool earlierKept)
        => backupEnabled
            ? (earlierKept ? ", earlier backup kept (predates this run)" : ", backup kept")
            : "";

    /// <summary>Formats a chapter mark position as h:mm:ss.ff for the --dry-run listing.</summary>
    /// <param name="seconds">Position in seconds.</param>
    internal static string FormatTimestamp(double seconds) => TimeFormat.Hms(seconds, 2);

    /// <summary>Formats a "(NN.N% of run length)" share, or empty when the run length is unknown.</summary>
    /// <param name="part">The measured quantity (seconds).</param>
    /// <param name="whole">The file's or run's total length (seconds).</param>
    internal static string FormatPercent(double part, double whole)
        => whole > 0 ? $" ({100 * part / whole:0.0}% of run length)" : "";

    /// <summary>
    /// Formats the "; took M:SS (NN.N% of run length)" clause a processed file's summary line
    /// ends with. The share is what makes the figure comparable: six minutes is fast for a
    /// fifteen-hour book and slow for a ten-minute one, and the reader knows the book.
    /// </summary>
    /// <param name="elapsed">Wall-clock time this file took, i.e.
    /// <see cref="ABChapterize.Ui.WorkTracker.Elapsed"/> - the tracker is created immediately
    /// before the file's own stopwatch, so the two measure the same stretch.</param>
    /// <param name="runLengthSeconds">The file's run length; 0 leaves the share off.</param>
    internal static string FormatProcessingTime(TimeSpan elapsed, double runLengthSeconds)
        => $"; took {FormatTime(elapsed)}" + FormatPercent(elapsed.TotalSeconds, runLengthSeconds);

    /// <summary>Appends " (inter-chapter Y.YY s)" when the chapter-1-excluded value is present,
    /// marking the extreme taken over the book's regular chapter breaks alone.</summary>
    /// <param name="interChapter">The extreme excluding chapter 1, or null when unavailable.</param>
    internal static string FormatInterChapter(double? interChapter)
        => interChapter is { } v ? $" (inter-chapter {v:0.00} s)" : "";

    /// <summary>Formats the Whisper transcription speed as a "NNN% of real-time" clause (audio
    /// transcribed over the wall-clock time it took), or empty when it could not be measured.</summary>
    /// <param name="audioSeconds">Audio handed to Whisper.</param>
    /// <param name="transcribeSeconds">Wall-clock time spent transcribing it.</param>
    internal static string FormatSpeed(double audioSeconds, double transcribeSeconds)
        => transcribeSeconds > 0 ? $"transcription speed {100 * audioSeconds / transcribeSeconds:0}% of real-time" : "";

    /// <summary>
    /// Builds the per-file statistics log line shown under --verbose (or --verbose-transcripts):
    /// the shortest silence and, when the VAD pre-pass ran, the longest jingle preceding a detected chapter
    /// (each with its inter-chapter, chapter-1-excluded counterpart), the total audio fed to
    /// Whisper and its share of the file's run length, and the transcription speed.
    /// </summary>
    /// <param name="stats">The file's detection statistics.</param>
    /// <param name="runLengthSeconds">The file's run length, for the Whisper-audio share.</param>
    internal static string FormatFileStats(DetectionStats stats, double runLengthSeconds)
    {
        var parts = new List<string>();
        if (stats.MinPrecedingSilenceSeconds is { } silence)
            parts.Add($"shortest silence before a chapter {silence:0.00} s" +
                      FormatInterChapter(stats.MinInterChapterSilenceSeconds));
        if (stats.MaxJingleLengthSeconds is { } jingle)
            parts.Add($"longest jingle {jingle:0.00} s" +
                      FormatInterChapter(stats.MaxInterChapterJingleSeconds));
        parts.Add($"Whisper audio {FormatTime(TimeSpan.FromSeconds(stats.WhisperAudioSeconds))}" +
                  FormatPercent(stats.WhisperAudioSeconds, runLengthSeconds));
        if (FormatSpeed(stats.WhisperAudioSeconds, stats.WhisperTranscribeSeconds) is { Length: > 0 } speed)
            parts.Add(speed);
        return "stats - " + string.Join(", ", parts);
    }

    /// <summary>Folds one processed file's detection statistics into the run-wide totals for the
    /// --summary report.</summary>
    /// <param name="stats">The file's detection statistics.</param>
    /// <param name="runLengthSeconds">The file's run length, summed toward the run-wide share.</param>
    internal void AccumulateStats(DetectionStats stats, double runLengthSeconds)
    {
        if (stats.MinPrecedingSilenceSeconds is { } silence)
            _minPrecedingSilence = Math.Min(_minPrecedingSilence, silence);
        if (stats.MinInterChapterSilenceSeconds is { } interSilence)
            _minInterChapterSilence = Math.Min(_minInterChapterSilence, interSilence);
        if (stats.MaxJingleLengthSeconds is { } jingle)
            _maxJingle = Math.Max(_maxJingle, jingle);
        if (stats.MaxInterChapterJingleSeconds is { } interJingle)
            _maxInterChapterJingle = Math.Max(_maxInterChapterJingle, interJingle);
        _whisperAudioSecondsTotal += stats.WhisperAudioSeconds;
        _whisperTranscribeSecondsTotal += stats.WhisperTranscribeSeconds;
        _runLengthSecondsTotal += runLengthSeconds;
    }

    /// <summary>
    /// Builds the run-wide statistics lines of the --summary report, in print order: the
    /// confidence spread across every chapter mark written during the run, the silence/jingle
    /// extremes seen before any chapter, and the Whisper audio total against the run length
    /// processed. Each of the three is omitted when nothing contributed to it, so a run that
    /// only skipped files reports none of them. The file counts and the total elapsed time are
    /// <see cref="FileProcessor"/>'s own to print - they are not this class's statistics.
    /// </summary>
    internal List<string> FormatRunSummaryLines()
    {
        var lines = new List<string>();
        if (_confidenceCount > 0)
            lines.Add($"Confidence of written chapter marks: min {_confidenceMin:0.00}, " +
                      $"max {_confidenceMax:0.00}, avg {_confidenceSum / _confidenceCount:0.00}");
        if (_runLengthSecondsTotal <= 0)
            return lines;

        if (FormatExtremes() is { Length: > 0 } extremes)
            lines.Add(extremes);
        var speed = FormatSpeed(_whisperAudioSecondsTotal, _whisperTranscribeSecondsTotal);
        lines.Add(
            $"Whisper audio processed: {FormatTime(TimeSpan.FromSeconds(_whisperAudioSecondsTotal))} " +
            $"of {FormatTime(TimeSpan.FromSeconds(_runLengthSecondsTotal))} run length " +
            $"({100 * _whisperAudioSecondsTotal / _runLengthSecondsTotal:0.0}%)" +
            (speed.Length > 0 ? $", {speed}" : ""));
        return lines;
    }

    /// <summary>Formats the run-wide silence/jingle extremes as one comma-separated line, or an
    /// empty string when no processed file contributed either of them (the extremes then still
    /// sit at their infinities).</summary>
    private string FormatExtremes()
    {
        var extremes = new List<string>();
        if (!double.IsPositiveInfinity(_minPrecedingSilence))
            extremes.Add($"Shortest silence before a chapter {_minPrecedingSilence:0.00} s" +
                         FormatInterChapter(double.IsPositiveInfinity(_minInterChapterSilence)
                             ? null : _minInterChapterSilence));
        if (!double.IsNegativeInfinity(_maxJingle))
            extremes.Add($"longest jingle before a chapter {_maxJingle:0.00} s" +
                         FormatInterChapter(double.IsNegativeInfinity(_maxInterChapterJingle)
                             ? null : _maxInterChapterJingle));
        return string.Join(", ", extremes);
    }

    /// <summary>Folds a set of written chapter marks into the run-wide confidence stats
    /// (for --summary).</summary>
    /// <param name="chapters">The chapters whose confidences were actually written.</param>
    internal void AccumulateConfidence(IEnumerable<DetectedChapter> chapters)
    {
        foreach (var c in chapters)
        {
            _confidenceSum += c.Confidence;
            _confidenceCount++;
            _confidenceMin = Math.Min(_confidenceMin, c.Confidence);
            _confidenceMax = Math.Max(_confidenceMax, c.Confidence);
        }
    }
}
