// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Cli;
using ABChapterize.Detection;
using ABChapterize.Processing;

namespace ABChapterize.Transcription;

/// <summary>
/// The run's pass-3 Whisper model, loaded (and downloaded) on the first gap that actually needs it
/// and never before. Used only when <c>--pass3-model</c> selects a model other than <c>--model</c>:
/// pass 2 keeps the run's own transcriber, while gap work (pass 2.5 and 3) routes through this one,
/// so a heavier - or lighter - model can take the last shot at the missing chapters.
/// <para>
/// Lazy because that is the whole economy of the feature: most books never open a chapter-sequence
/// gap at all, and a second model loaded up front would cost every run the memory, and a first run
/// the download, for something few of them ever use. Nothing else here is deferred - once the model
/// is in memory it stays for the rest of the run, since a book that opened one gap tends to open
/// more.
/// </para>
/// <para>
/// One instance is created for the whole run and disposed by <see cref="FileProcessor"/>. It is an
/// <see cref="ITranscriber"/> like any other, so <see cref="ChapterDetector"/> cannot tell it apart
/// from the pass-2 transcriber except by reference - which is exactly the check it uses to decide
/// whether a separate pass-3 model exists at all.
/// </para>
/// <para>
/// Used from one file's pipeline at a time, like every other <see cref="ITranscriber"/> here, and
/// therefore unsynchronized. Until 0.10.0 this was two types with a semaphore between them, so that
/// concurrently processed files could not race over one Whisper context's current language; with
/// files processed one at a time there is no second caller for any of that to protect against.
/// </para>
/// </summary>
public sealed class Pass3Transcriber : ITranscriber, IAsyncDisposable
{
    private readonly string _model;
    private readonly int _threads;
    private readonly bool _forceCpu;
    private readonly int? _gpuDevice;

    /// <summary>Where this model's download reports itself, since it happens mid-run with the
    /// progress bar on screen - see <see cref="ModelCatalog.EnsureModelAsync"/>.</summary>
    private readonly Action<string>? _report;

    /// <summary>Language the model runs in. Kept here rather than only on <see cref="_inner"/>
    /// because <see cref="ChangeLanguage"/> is normally called while there is no model yet - the
    /// caller sets the file's language up front, and the model is built later, on the first gap that
    /// needs it, from whatever this then says.</summary>
    private string _language;

    private WhisperTranscriber? _inner;

    /// <summary>Creates the pass-3 transcriber for the given model, without loading it yet.</summary>
    /// <param name="model">Validated pass-3 model selector (see <see cref="CliOptions.Pass3Model"/>).</param>
    /// <param name="language">Language to start in. A fallback rather than a real setting: every
    /// caller asserts the file's own language through <see cref="ChangeLanguage"/> before any gap
    /// work begins, so this only decides what an otherwise-unreachable first transcription would
    /// have used.</param>
    /// <param name="threads">CPU threads for this model, the same budget pass 2 gets
    /// (--whisper-threads, see <see cref="CliOptions.EffectiveWhisperThreads"/>): the two never run
    /// at the same time, so there is nothing to divide between them.</param>
    /// <param name="forceCpu">Forwarded to the <see cref="WhisperTranscriber"/> once it is built
    /// (--cpu-only, see <see cref="CliOptions.CpuOnly"/>).</param>
    /// <param name="gpuDevice">Forwarded likewise, so pass 3 lands on the same GPU the rest of the
    /// run chose (--use-gpu, see <see cref="CliOptions.UseGpu"/>).</param>
    /// <param name="report">Where a download of this model announces itself. Unlike the run's own
    /// model, this one is fetched with the progress bar already running, so it must not write to
    /// the console directly.</param>
    public Pass3Transcriber(string model, string language, int threads, bool forceCpu = false,
        int? gpuDevice = null, Action<string>? report = null)
    {
        _model = model;
        _language = language;
        _threads = threads;
        _forceCpu = forceCpu;
        _gpuDevice = gpuDevice;
        _report = report;
    }

    /// <inheritdoc/>
    /// <remarks>Loading the model here, rather than in the constructor, is what makes this the
    /// transcriber that costs nothing until a gap opens.</remarks>
    public async Task<List<TranscriptSegment>> TranscribeAsync(
        float[] samples, CancellationToken ct, Action<double>? onProgressSeconds = null)
    {
        if (_inner == null)
        {
            var path = await ModelCatalog.EnsureModelAsync(_model, ct, _report);
            _inner = new WhisperTranscriber(path, _language, _threads, _forceCpu, _gpuDevice);
        }
        return await _inner.TranscribeAsync(samples, ct, onProgressSeconds);
    }

    /// <inheritdoc/>
    /// <remarks>Applied to the model immediately when there is one, and remembered for the model's
    /// construction when there is not - so <see cref="_inner"/>'s language is always this one, with
    /// no re-assertion needed at transcription time.</remarks>
    public void ChangeLanguage(string language)
    {
        _language = language;
        _inner?.ChangeLanguage(language);
    }

    /// <summary>
    /// Not supported: language auto-detection happens once per file on a pass-2 window, never on the
    /// pass-3 model. <see cref="ChapterDetector"/> only ever calls this on its pass-2 transcriber, so
    /// this one is never asked to detect a language - which is just as well, since it would have to
    /// load the model to answer and the point of it is not to.
    /// </summary>
    /// <param name="samples">Unused.</param>
    /// <param name="ct">Unused.</param>
    public Task<(string Language, float Probability)> DetectLanguageWithProbabilityAsync(float[] samples, CancellationToken ct)
        => throw new NotSupportedException("The pass-3 transcriber does not perform language detection.");

    /// <summary>Releases the underlying model if it was ever loaded.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_inner != null)
            await _inner.DisposeAsync();
    }
}
