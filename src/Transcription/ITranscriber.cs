// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace ABChapterize.Transcription;

/// <summary>A transcribed text segment with absolute timing.</summary>
/// <param name="StartSeconds">Segment start in seconds, relative to the decoded audio window.</param>
/// <param name="EndSeconds">Segment end in seconds, relative to the decoded audio window.</param>
/// <param name="Text">Recognized text.</param>
/// <param name="Probability">Whisper's average token probability for this segment (0-1);
/// 1.0 when the transcriber does not compute probabilities.</param>
public readonly record struct TranscriptSegment(
    double StartSeconds, double EndSeconds, string Text, double Probability = 1.0);

/// <summary>
/// Speech recognizer that turns PCM audio into timed text segments. Implemented by
/// <see cref="WhisperTranscriber"/>; the abstraction exists so the detection logic in
/// <see cref="ABChapterize.Detection.ChapterDetector"/> can be unit-tested with scripted transcripts.
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

    /// <summary>
    /// Detects the most likely spoken language of a short audio clip, without transcribing it.
    /// Used for <c>--lang auto</c> (the default): a clip from the start of each file is passed
    /// here once, before any transcription of that file happens.
    /// </summary>
    /// <param name="samples">The audio samples (a short clip is enough).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The detected two-letter language code and Whisper's probability for it (0-1).</returns>
    Task<(string Language, float Probability)> DetectLanguageWithProbability(float[] samples, CancellationToken ct);

    /// <summary>
    /// Switches the language used for subsequent <see cref="TranscribeAsync"/> calls. Used to
    /// apply the outcome of <see cref="DetectLanguageWithProbability"/> (or an explicit --lang,
    /// re-asserted defensively for transcriber instances reused across files with --jobs).
    /// </summary>
    /// <param name="language">Two-letter language code to switch to.</param>
    void ChangeLanguage(string language);
}
