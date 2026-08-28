// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Formatting;
using ABChapterize.Transcription;
using static ABChapterize.Detection.DetectionTuning;

namespace ABChapterize.Detection;

/// <summary>Small stateless string-formatting helpers shared across <see cref="ChapterDetector"/>'s
/// --verbose/log output.</summary>
internal static class DetectionFormatting
{
    /// <summary>Trailing note for a detection log line listing the chapter numbers still missing
    /// below the highest found; empty when the sequence so far is complete.</summary>
    /// <param name="missing">The chapter numbers still missing below the highest found.</param>
    internal static string MissingNote(List<int> missing)
        => missing.Count > 0 ? $" - still missing: {string.Join(", ", missing)}" : "";

    /// <summary>Trailing note appended to a --verbose detection log line when the segment
    /// confidence is below <see cref="DetectionTuning.LowConfidenceThreshold"/>.</summary>
    /// <param name="confidence">Whisper's probability for the segment the number was read from.</param>
    internal static string LowConfidenceNote(double confidence)
        => confidence < LowConfidenceThreshold ? " - LOW CONFIDENCE, worth a manual check" : "";

    /// <summary>Formats a position in the file as h:mm:ss.ff for log messages.</summary>
    /// <param name="seconds">Position in seconds.</param>
    internal static string FormatTimestamp(double seconds) => TimeFormat.Hms(seconds, 2);

    /// <summary>Formats a <em>length</em> of time - how much audio a pass is about to read, say -
    /// as opposed to a position in the file, which <see cref="FormatTimestamp"/> is for. The
    /// distinction is worth keeping in the log: "0:41:12" of audio read out of "3:17:40" says
    /// something a pair of positions does not.</summary>
    /// <param name="seconds">The length in seconds.</param>
    internal static string FormatLength(double seconds)
        => TimeFormat.Duration(TimeSpan.FromSeconds(Math.Max(0, seconds)));

    /// <summary>
    /// Renders one Whisper transcript as a single log line: the caller's context, then every
    /// segment with its window-relative times and confidence. One line rather than one per segment
    /// because both consumers - the --verbose-transcripts console stream and the --debug file - are
    /// read by grepping for a phrase and wanting the whole decode that produced it, which a line
    /// break in the middle of a transcript destroys.
    /// </summary>
    /// <param name="context">Description of the decoded window, e.g. "probe 50s@0:12:34.00".</param>
    /// <param name="segments">The transcribed segments, with times relative to the window.</param>
    internal static string FormatTranscript(string context, List<TranscriptSegment> segments)
        => segments.Count == 0
            ? $"{context}: (no speech recognized)"
            : $"{context}: " + string.Join(" | ",
                segments.Select(s =>
                    $"{s.StartSeconds:0.0}-{s.EndSeconds:0.0} (p={s.Probability:0.00}) \"{s.Text.Trim()}\""));
}
