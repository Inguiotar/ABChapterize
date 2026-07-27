// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Formatting;
using static ABChapterize.Detection.DetectionTuning;

namespace ABChapterize.Detection;

/// <summary>Small stateless string-formatting helpers shared across <see cref="ChapterDetector"/>'s
/// --verbose/log output.</summary>
internal static class DetectionFormatting
{
    /// <summary>Trailing note for a detection log line listing the chapter numbers still missing
    /// below the highest found; empty when the sequence so far is complete.</summary>
    internal static string MissingNote(List<int> missing)
        => missing.Count > 0 ? $" - still missing: {string.Join(", ", missing)}" : "";

    /// <summary>Trailing note appended to a --verbose detection log line when the segment
    /// confidence is below <see cref="DetectionTuning.LowConfidenceThreshold"/>.</summary>
    internal static string LowConfidenceNote(double confidence)
        => confidence < LowConfidenceThreshold ? " - LOW CONFIDENCE, worth a manual check" : "";

    /// <summary>Formats a position in the file as h:mm:ss.ff for log messages.</summary>
    /// <param name="seconds">Position in seconds.</param>
    internal static string FormatTimestamp(double seconds) => TimeFormat.Hms(seconds, 2);
}
