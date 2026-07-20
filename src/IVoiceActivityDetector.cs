// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace ABChapterize;

/// <summary>A contiguous region of detected speech.</summary>
/// <param name="StartSeconds">Start of the region in seconds.</param>
/// <param name="EndSeconds">End of the region in seconds.</param>
public readonly record struct SpeechSegment(double StartSeconds, double EndSeconds);

/// <summary>
/// Voice activity detection: classifies a file's audio into speech/non-speech regions over
/// time. Implemented by <see cref="SileroVadDetector"/>; the abstraction exists so <see
/// cref="ChapterDetector"/> can be unit-tested with synthetic speech segments, without loading
/// the ONNX model. Used only with --jingle, as an additional Pass 2 candidate source alongside
/// silence detection: a jingle is non-speech to a speech/non-speech classifier just like
/// silence is, so it shows up as a gap between two <see cref="SpeechSegment"/>s even on
/// audiobooks where the jingle has no detectable amplitude gap around it (silencedetect finds
/// nothing there at all in that case).
/// </summary>
public interface IVoiceActivityDetector
{
    /// <summary>
    /// Scans the whole file and returns every contiguous speech region in chronological order.
    /// The gaps between consecutive regions (and, in principle, before the first / after the
    /// last) are the non-speech regions a jingle transition shows up as.
    /// </summary>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="durationSeconds">Total play time.</param>
    /// <param name="progress">Callback receiving the processed play time in seconds.</param>
    /// <param name="inputDecoder">Explicit input decoder to force (e.g. "libfdk_aac"), or null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All detected speech regions in chronological order.</returns>
    Task<List<SpeechSegment>> DetectSpeechAsync(
        string file, double durationSeconds, Action<double>? progress, string? inputDecoder, CancellationToken ct);
}
