// Chapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace Chapterize;

/// <summary>A period of silence detected in the audio stream.</summary>
/// <param name="StartSeconds">Start of the silence in seconds.</param>
/// <param name="EndSeconds">End of the silence in seconds.</param>
public readonly record struct Silence(double StartSeconds, double EndSeconds);

/// <summary>
/// Audio analysis and decoding operations needed by chapter detection. Implemented by
/// <see cref="FfmpegClient"/>; the abstraction exists so <see cref="ChapterDetector"/>
/// can be unit-tested with synthetic silences and audio windows.
/// </summary>
public interface IAudioSource
{
    /// <summary>
    /// Scans the whole file for silence periods.
    /// </summary>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="durationSeconds">Total play time; used to close a trailing silence
    /// and to detect a prematurely ended scan.</param>
    /// <param name="minSilenceSeconds">Minimum silence duration to report.</param>
    /// <param name="noiseDb">Noise floor in dBFS below which audio counts as silence.</param>
    /// <param name="progress">Callback receiving the processed play time in seconds.</param>
    /// <param name="inputDecoder">Explicit input decoder to force (e.g. "libfdk_aac"), or null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All detected silence periods in chronological order.</returns>
    Task<List<Silence>> DetectSilencesAsync(
        string file, double durationSeconds, double minSilenceSeconds, int noiseDb,
        Action<double>? progress, string? inputDecoder, CancellationToken ct);

    /// <summary>
    /// Decodes a section of the file to 16 kHz mono 32-bit float PCM suitable for Whisper.
    /// </summary>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="startSeconds">Start position in seconds.</param>
    /// <param name="durationSeconds">Length in seconds, or null to decode to the end of the file.</param>
    /// <param name="inputDecoder">Explicit input decoder to force (e.g. "libfdk_aac"), or null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The decoded samples.</returns>
    Task<float[]> DecodePcmAsync(
        string file, double startSeconds, double? durationSeconds, string? inputDecoder, CancellationToken ct);
}
