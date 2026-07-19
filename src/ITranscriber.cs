// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace ABChapterize;

/// <summary>A transcribed text segment with absolute timing.</summary>
/// <param name="StartSeconds">Segment start in seconds, relative to the decoded audio window.</param>
/// <param name="EndSeconds">Segment end in seconds, relative to the decoded audio window.</param>
/// <param name="Text">Recognized text.</param>
public readonly record struct TranscriptSegment(double StartSeconds, double EndSeconds, string Text);

/// <summary>
/// Speech recognizer that turns PCM audio into timed text segments. Implemented by
/// <see cref="WhisperTranscriber"/>; the abstraction exists so the detection logic in
/// <see cref="ChapterDetector"/> can be unit-tested with scripted transcripts.
/// </summary>
public interface ITranscriber
{
    /// <summary>
    /// Transcribes a chunk of 16 kHz mono float PCM audio.
    /// </summary>
    /// <param name="samples">The audio samples.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Recognized segments in chronological order, timed relative to the chunk.</returns>
    Task<List<TranscriptSegment>> TranscribeAsync(float[] samples, CancellationToken ct);
}
