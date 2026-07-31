// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Cli;
using ABChapterize.Detection;
using ABChapterize.Processing;

namespace ABChapterize.Transcription;

/// <summary>
/// A single Whisper model shared by every file's pass 3, loaded (and downloaded) lazily on the
/// first gap that actually needs it and never before. Used only when <c>--pass3-model</c> selects
/// a model different from <c>--model</c>: pass 2 keeps the run's own transcriber, while pass 3
/// routes through this one so a heavier (or lighter) model can take the last shot at the missing
/// chapters. Lazily, because most books never open a gap at all, and a second model loaded up front
/// would cost every run the memory and the download for something few of them use.
/// One instance is created for the whole run and disposed by <see cref="FileProcessor"/>.
/// </summary>
/// <remarks>
/// Transcriptions are serialized through a gate. Files are processed one at a time, so nothing
/// contends for it today; it stays because a Whisper context is not safe for concurrent inference
/// and this is the one object in the run that more than one caller could plausibly reach.
/// </remarks>
public sealed class SharedPass3Transcriber : IAsyncDisposable
{
    private readonly string _model;
    private readonly string _initialLanguage;
    private readonly int _threads;
    private readonly bool _forceCpu;
    private readonly int? _gpuDevice;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperTranscriber? _inner;

    /// <summary>Creates the shared transcriber for the given pass-3 model, without loading it yet.</summary>
    /// <param name="model">Validated pass-3 model selector (see <see cref="CliOptions.Pass3Model"/>).</param>
    /// <param name="initialLanguage">Placeholder language the model is loaded with; every
    /// transcription re-asserts the caller's real language before running (see
    /// <see cref="TranscribeAsync"/>), exactly as the pass-2 transcriber does.</param>
    /// <param name="threads">CPU threads for this model, the same budget pass 2 gets
    /// (--whisper-threads, see <see cref="CliOptions.EffectiveWhisperThreads"/>): the two never run
    /// at the same time, so there is nothing to divide between them.</param>
    /// <param name="forceCpu">Forwarded to the lazily-created <see cref="WhisperTranscriber"/>
    /// (--cpu-only, see <see cref="CliOptions.CpuOnly"/>).</param>
    /// <param name="gpuDevice">Forwarded likewise, so pass 3 lands on the same GPU the rest of the
    /// run chose (--use-gpu, see <see cref="CliOptions.UseGpu"/>).</param>
    public SharedPass3Transcriber(string model, string initialLanguage, int threads, bool forceCpu = false,
        int? gpuDevice = null)
    {
        _model = model;
        _initialLanguage = initialLanguage;
        _threads = threads;
        _forceCpu = forceCpu;
        _gpuDevice = gpuDevice;
    }

    /// <summary>
    /// Transcribes one gap chunk in the given language under exclusive access, loading (and, on the
    /// very first call, downloading) the pass-3 model if it has not been needed yet. The language is
    /// applied inside the gate, so it is always the one this transcription asked for.
    /// </summary>
    /// <param name="language">Two-letter language for this file's pass 3.</param>
    /// <param name="samples">16 kHz mono PCM of one gap chunk.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<TranscriptSegment>> TranscribeAsync(string language, float[] samples, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_inner == null)
            {
                var path = await ModelCatalog.EnsureModelAsync(_model, ct);
                _inner = new WhisperTranscriber(path, _initialLanguage, _threads, _forceCpu, _gpuDevice);
            }
            _inner.ChangeLanguage(language);
            return await _inner.TranscribeAsync(samples, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Releases the underlying model if it was ever loaded.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_inner != null)
            await _inner.DisposeAsync();
        _gate.Dispose();
    }
}

/// <summary>
/// The <see cref="ChapterDetector"/>-facing view of a <see cref="SharedPass3Transcriber"/>: an
/// <see cref="ITranscriber"/> that remembers the current file's language and forwards every
/// transcription to the shared model, which applies that language atomically with the transcription
/// under its gate. Keeping the language on the caller's side rather than on the shared model is what
/// lets the model stay a single lazily-loaded instance while each file still gets its own language.
/// </summary>
public sealed class Pass3TranscriberProxy : ITranscriber
{
    private readonly SharedPass3Transcriber _shared;
    private string _language;

    /// <summary>Creates a proxy onto the shared pass-3 transcriber.</summary>
    /// <param name="shared">The run-wide pass-3 model.</param>
    /// <param name="initialLanguage">Initial language until <see cref="ChangeLanguage"/> is called.</param>
    public Pass3TranscriberProxy(SharedPass3Transcriber shared, string initialLanguage)
    {
        _shared = shared;
        _language = initialLanguage;
    }

    /// <inheritdoc/>
    public Task<List<TranscriptSegment>> TranscribeAsync(float[] samples, CancellationToken ct)
        => _shared.TranscribeAsync(_language, samples, ct);

    /// <inheritdoc/>
    public void ChangeLanguage(string language) => _language = language;

    /// <summary>
    /// Not supported: language auto-detection happens once per file on a pass-2 window, never on
    /// the pass-3 model. <see cref="ChapterDetector"/> only ever calls this on its pass-2
    /// transcriber, so this proxy is never asked to detect a language.
    /// </summary>
    public Task<(string Language, float Probability)> DetectLanguageWithProbability(float[] samples, CancellationToken ct)
        => throw new NotSupportedException("The pass-3 transcriber does not perform language detection.");
}
