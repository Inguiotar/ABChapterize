// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace ABChapterize.Vad;

/// <summary>
/// Converts a chronological sequence of per-frame speech probabilities from a VAD model into
/// speech segments, using the same hysteresis approach as Silero VAD's own reference
/// implementation (the <c>get_speech_timestamps</c> helper in the upstream Python package): a
/// probability crossing <see cref="Threshold"/> starts a candidate speech run; the run only
/// ends once non-speech has persisted for <see cref="MinSilenceSeconds"/>, so brief
/// within-narration dips (a consonant, a breath) don't fragment one passage into many; a
/// candidate run shorter than <see cref="MinSpeechSeconds"/> is discarded as a spurious spike.
/// Pure and stateless (no ONNX involved), so it is unit-tested directly.
/// </summary>
internal static class VadSegmenter
{
    /// <summary>Frame speech probability at/above this counts as speech. Raised from Silero's own
    /// 0.5 default: audiobooks are studio-recorded and mixed for clear intelligibility, unlike the
    /// noisy, low-SNR speech (voice assistants, phone calls) Silero is tuned for by default, so a
    /// stricter threshold has headroom to reject more jingle-music false positives without losing
    /// real narration - confirmed via <c>tools\vadprobe</c>'s threshold sweep against a real
    /// audiobook, up to 0.70, before picking this value.</summary>
    internal const float Threshold = 0.6f;

    /// <summary>A candidate speech run must persist at least this long to be kept (Silero's own
    /// default for <c>min_speech_duration_ms</c>).</summary>
    internal const double MinSpeechSeconds = 0.25;

    /// <summary>Non-speech must persist at least this long to end a speech run (Silero's own
    /// default for <c>min_silence_duration_ms</c>). Short compared to a real jingle transition
    /// (seconds long), so it does not need to be tuned for this use case: the much longer
    /// minimum jingle-gap length is enforced separately, by the caller in <see
    /// cref="ChapterDetector"/>.</summary>
    internal const double MinSilenceSeconds = 0.1;

    /// <summary>
    /// Converts frame samples into speech segments. Segment boundaries land on frame start
    /// times, so they carry roughly one frame's worth of granularity (tens of milliseconds) -
    /// negligible next to the second-scale precision chapter marks need, and any exact
    /// placement is refined downstream against the transcribed phrase and, when present, a
    /// silencedetect silence.
    /// </summary>
    /// <param name="frames">Each frame's start time in seconds and speech probability [0,1],
    /// in chronological order.</param>
    /// <param name="threshold">Frame probability at/above which counts as speech; defaults to
    /// <see cref="Threshold"/>. Exposed only so diagnostic tooling can re-smooth the same raw
    /// frames at other candidate thresholds without re-running VAD inference - production code
    /// always uses the default.</param>
    internal static List<SpeechSegment> Smooth(
        IReadOnlyList<(double TimeSeconds, float Probability)> frames, float threshold = Threshold)
    {
        var result = new List<SpeechSegment>();
        double? runStart = null;
        double? silenceStart = null;
        var lastFrameTime = 0.0;

        foreach (var (time, probability) in frames)
        {
            if (probability >= threshold)
            {
                runStart ??= time;
                silenceStart = null;
            }
            else if (runStart.HasValue)
            {
                silenceStart ??= time;
                if (time - silenceStart.Value >= MinSilenceSeconds)
                {
                    if (silenceStart.Value - runStart.Value >= MinSpeechSeconds)
                        result.Add(new SpeechSegment(runStart.Value, silenceStart.Value));
                    runStart = null;
                    silenceStart = null;
                }
            }
            lastFrameTime = time;
        }

        // The file ended mid-run: either still genuinely speaking, or in an unconfirmed dip
        // that never persisted long enough to end the run - either way the run extends to the
        // last frame observed.
        if (runStart.HasValue && lastFrameTime - runStart.Value >= MinSpeechSeconds)
            result.Add(new SpeechSegment(runStart.Value, lastFrameTime));

        return result;
    }
}
