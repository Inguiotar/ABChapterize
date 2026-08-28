// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using ABChapterize.Errors;

namespace ABChapterize.Audio;

/// <summary>A chapter mark with start time and title.</summary>
/// <param name="StartSeconds">Chapter start in seconds from the beginning of the file.</param>
/// <param name="Title">Chapter title as written into the file metadata.</param>
public readonly record struct Chapter(double StartSeconds, string Title);

/// <summary>Result of probing a media file with ffprobe.</summary>
/// <param name="DurationSeconds">Total play time in seconds.</param>
/// <param name="SizeBytes">File size in bytes.</param>
/// <param name="ChapterCount">Number of pre-existing chapter marks.</param>
/// <param name="AudioCodec">Codec name of the first audio stream (e.g. "aac"), or "" if unknown.</param>
/// <param name="AudioProfile">Codec profile of the first audio stream (e.g. "LC", "xHE-AAC"), or "" if unknown.</param>
/// <param name="InputDecoder">Explicit ffmpeg input decoder to use for this file (e.g. "libfdk_aac"), or null for ffmpeg's default.</param>
/// <param name="ExistingChapterList">Backing field for <see cref="ExistingChapters"/>; null when the
/// file was never probed for chapters at all (the xHE-AAC-without-decoder early return, and the
/// instances the unit tests construct by hand), which <see cref="ExistingChapters"/> flattens to an
/// empty list. A successful probe always supplies a list, empty or not.</param>
/// <param name="TitleTag">The container title tag, or null where the file has none.</param>
/// <param name="AlbumTag">The container album tag, or null where the file has none. For an
/// audiobook this is usually the book, the title tag naming the part - which is why both are read
/// rather than either being trusted.</param>
public readonly record struct MediaInfo(
    double DurationSeconds, long SizeBytes, int ChapterCount,
    string AudioCodec = "", string AudioProfile = "", string? InputDecoder = null,
    IReadOnlyList<Chapter>? ExistingChapterList = null,
    string? TitleTag = null, string? AlbumTag = null)
{
    /// <summary>True when the audio stream uses the xHE-AAC (USAC) profile of AAC.</summary>
    public bool IsXheAac => AudioProfile.Contains("xHE", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The file's pre-existing chapter marks (start time and title), in the order ffprobe
    /// reported them. Used by --verify to probe near each one instead of trusting it blindly.
    /// </summary>
    public IReadOnlyList<Chapter> ExistingChapters => ExistingChapterList ?? [];
}

/// <summary>
/// Thin wrapper around the ffmpeg and ffprobe command line tools: media probing,
/// silence detection, PCM decoding for Whisper, and safe chapter writing.
/// </summary>
public sealed partial class FfmpegClient : IAudioSource
{
    private readonly string _ffmpeg;
    private readonly string _ffprobe;

    /// <summary>Sample rate of decoded PCM audio; Whisper requires 16 kHz.</summary>
    public const int SampleRate = 16000;

    /// <summary>
    /// What every name this tool parks a file under carries, whatever it parks it for. Its own
    /// constant because the file enumeration skips such a file on this stem alone - it has no
    /// business asking which kind of scratch file it is looking at - and a stem that only agreed
    /// with the two names below by coincidence would put a half-written remux up for processing.
    /// </summary>
    public const string ScratchInfix = ".abchapterize.";

    /// <summary>
    /// Infix of the remux target <see cref="WriteChaptersAsync"/> writes before swapping it in
    /// (the file's own extension follows, so ffmpeg picks the muxer from it). Named here rather
    /// than spelled out at the one place it is built, because <c>--cleanup</c> has to recognize
    /// one left behind by a run that was killed before its <c>finally</c> could delete it, and a
    /// housekeeping mode that no longer matches what the writer produces is worse than none.
    /// </summary>
    public const string TempInfix = ScratchInfix + "tmp";

    /// <summary>
    /// Suffix the original is parked under while <see cref="SwapInto"/> moves the new file into
    /// place. Recognized by <c>--cleanup</c> for the same reason as <see cref="TempInfix"/> - but
    /// note that an orphaned one is the audiobook itself rather than a scratch file, so cleanup
    /// puts it back instead of deleting it.
    /// </summary>
    public const string ParkedSuffix = ScratchInfix + "orig";

    /// <summary>Creates a client using the given executable paths (see <see cref="FfmpegLocator"/>).</summary>
    /// <param name="ffmpegPath">Full path of ffmpeg.exe.</param>
    /// <param name="ffprobePath">Full path of ffprobe.exe.</param>
    public FfmpegClient(string ffmpegPath, string ffprobePath)
    {
        _ffmpeg = ffmpegPath;
        _ffprobe = ffprobePath;
    }

    /// <summary>
    /// Reads duration, size, pre-existing chapter count, and the codec/profile of the
    /// first audio stream of a media file.
    /// </summary>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="ct">Cancellation token for graceful Ctrl+C handling.</param>
    public async Task<MediaInfo> ProbeAsync(string file, CancellationToken ct)
    {
        var (stdout, stderr, exit) = await RunAsync(_ffprobe,
            ["-hide_banner", "-v", "warning", "-print_format", "json", "-show_format", "-show_chapters",
             "-show_streams", "-select_streams", "a:0", file],
            null, ct);
        if (exit != 0)
        {
            // An ffmpeg build without a capable decoder cannot even probe xHE-AAC (USAC)
            // files: it dies with "Failed to open codec" on an AAC stream reported with
            // 0 channels. Return that diagnosis instead of a generic probe failure so the
            // caller can print the libfdk_aac hint.
            if (stderr.Contains("Failed to open codec") && stderr.Contains("Audio: aac"))
                return new MediaInfo(0, new FileInfo(file).Length, 0, "aac", "xHE-AAC (suspected)");
            throw new AppError($"ffprobe failed for \"{file}\".");
        }

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        // ffprobe reports the duration as a string; some containers yield "N/A".
        double duration = 0;
        if (root.TryGetProperty("format", out var format) &&
            format.TryGetProperty("duration", out var dur) &&
            double.TryParse(dur.GetString(), CultureInfo.InvariantCulture, out var d))
            duration = d;
        var existingChapters = new List<Chapter>();
        if (root.TryGetProperty("chapters", out var ch))
        {
            foreach (var c in ch.EnumerateArray())
            {
                var start = c.TryGetProperty("start_time", out var st) &&
                            double.TryParse(st.GetString(), CultureInfo.InvariantCulture, out var s) ? s : 0;
                var title = c.TryGetProperty("tags", out var tags) && tags.TryGetProperty("title", out var t)
                    ? t.GetString() ?? ""
                    : "";
                existingChapters.Add(new Chapter(start, title));
            }
        }
        string codec = "", profile = "";
        if (root.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0)
        {
            var stream = streams[0];
            if (stream.TryGetProperty("codec_name", out var cn))
                codec = cn.GetString() ?? "";
            if (stream.TryGetProperty("profile", out var pr))
                profile = pr.GetString() ?? "";
        }
        // The container tags are read for one caller only - matching a local file against a
        // library on an Audiobookshelf server, where a file name is often the least reliable thing
        // about a book. Nothing in detection looks at them.
        string? titleTag = null, albumTag = null;
        if (root.TryGetProperty("format", out var tagged) && tagged.TryGetProperty("tags", out var formatTags))
        {
            titleTag = TagValue(formatTags, "title");
            albumTag = TagValue(formatTags, "album");
        }
        var size = new FileInfo(file).Length;
        return new MediaInfo(duration, size, existingChapters.Count, codec, profile,
            ExistingChapterList: existingChapters, TitleTag: titleTag, AlbumTag: albumTag);
    }

    /// <summary>
    /// One container tag, or null when it is absent or empty.
    /// </summary>
    /// <param name="tags">The "tags" object of an ffprobe format section.</param>
    /// <param name="name">The tag to read.</param>
    /// <remarks>
    /// Case-insensitive, because the spelling depends on the container: mp4 reports lower-case
    /// names, Matroska upper-case ones, and ID3 whatever the tagger wrote.
    /// </remarks>
    private static string? TagValue(JsonElement tags, string name)
    {
        foreach (var tag in tags.EnumerateObject())
            if (string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                var value = tag.Value.ValueKind == JsonValueKind.String ? tag.Value.GetString() : null;
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        return null;
    }

    /// <summary>Cached result of the libfdk_aac decoder availability check.</summary>
    private bool? _hasLibFdkAac;

    /// <summary>
    /// Checks (once, then cached) whether the installed ffmpeg was built with the
    /// libfdk_aac decoder, which is required to decode xHE-AAC (USAC) audio reliably.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> SupportsLibFdkAacAsync(CancellationToken ct)
    {
        if (_hasLibFdkAac is { } cached)
            return cached;
        var (stdout, _, exit) = await RunAsync(_ffmpeg, ["-hide_banner", "-decoders"], null, ct);
        var has = exit == 0 && Regex.IsMatch(stdout, @"\blibfdk_aac\b");
        _hasLibFdkAac = has;
        return has;
    }

    /// <summary>Matches the silence start lines of ffmpeg's silencedetect filter.</summary>
    [GeneratedRegex(@"silence_start:\s*(-?[\d.]+)")]
    private static partial Regex SilenceStartRegex();

    /// <summary>Matches the silence end lines of ffmpeg's silencedetect filter.</summary>
    [GeneratedRegex(@"silence_end:\s*(-?[\d.]+)")]
    private static partial Regex SilenceEndRegex();

    /// <summary>Matches the processed-time lines of ffmpeg's "-progress" output.</summary>
    [GeneratedRegex(@"out_time_us=(\d+)")]
    private static partial Regex ProgressTimeRegex();

    /// <inheritdoc/>
    /// <remarks>Uses ffmpeg's silencedetect filter in a full decode pass over the file.</remarks>
    public async Task<List<Silence>> DetectSilencesAsync(
        string file, double durationSeconds, double minSilenceSeconds, double noiseDb,
        Action<double>? progress, string? inputDecoder, CancellationToken ct)
    {
        var silences = new List<Silence>();
        double? pendingStart = null;
        double processedSeconds = 0;

        List<string> args = ["-hide_banner", "-nostats", "-nostdin"];
        if (inputDecoder != null)
            args.AddRange(["-c:a", inputDecoder]);
        args.AddRange(
            [
                "-i", file,
                // Audio only: an embedded cover art is a one-frame video stream whose
                // immediate EOF would otherwise end the whole run after a fraction of
                // a second, silently skipping the rest of the file.
                "-vn", "-sn", "-dn",
                "-af", $"silencedetect=noise={noiseDb.ToString(CultureInfo.InvariantCulture)}dB:d={minSilenceSeconds.ToString(CultureInfo.InvariantCulture)}",
                "-progress", "pipe:1",
                "-f", "null", "-"
            ]);
        var (_, _, exit) = await RunAsync(_ffmpeg, args,
            line =>
            {
                var m = SilenceStartRegex().Match(line);
                if (m.Success)
                {
                    pendingStart = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    return;
                }
                m = SilenceEndRegex().Match(line);
                if (m.Success && pendingStart is { } s)
                {
                    silences.Add(new Silence(Math.Max(0, s),
                        double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)));
                    pendingStart = null;
                    return;
                }
                m = ProgressTimeRegex().Match(line);
                if (m.Success)
                {
                    processedSeconds = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 1_000_000.0;
                    progress?.Invoke(processedSeconds);
                }
            }, ct);

        if (exit != 0)
            throw new AppError($"ffmpeg silence detection failed for \"{file}\".");

        // Guard against silent early termination: the scan must have covered the whole file.
        if (processedSeconds < durationSeconds - Math.Max(30, durationSeconds * 0.02))
            throw new AppError(
                $"Silence scan of \"{file}\" ended prematurely after {processedSeconds:0.#} of " +
                $"{durationSeconds:0.#} seconds.");

        // A silence still open at the end of the file gets closed at the file's duration.
        if (pendingStart is { } open)
            silences.Add(new Silence(Math.Max(0, open), durationSeconds));
        return silences;
    }

    /// <inheritdoc/>
    public async Task<float[]> DecodePcmAsync(
        string file, double startSeconds, double? durationSeconds, string? inputDecoder, CancellationToken ct)
    {
        var args = new List<string>
        {
            "-hide_banner", "-v", "error", "-nostdin",
            "-ss", startSeconds.ToString("0.###", CultureInfo.InvariantCulture),
        };
        if (inputDecoder != null)
            args.AddRange(["-c:a", inputDecoder]);
        if (durationSeconds is { } d)
        {
            args.Add("-t");
            args.Add(d.ToString("0.###", CultureInfo.InvariantCulture));
        }
        args.AddRange(["-i", file, "-ac", "1", "-ar", SampleRate.ToString(), "-f", "f32le", "pipe:1"]);

        using var proc = StartProcess(_ffmpeg, args, redirectStdout: true);
        using var reg = ct.Register(() => TryKill(proc));

        using var ms = new MemoryStream();
        // Both pipes are drained concurrently, as everywhere else in this class: reading stdout to
        // completion first deadlocks the moment ffmpeg fills the stderr pipe buffer, since it then
        // blocks on the write and stops producing the stdout this side is waiting for. `-v error`
        // makes that unlikely rather than impossible - a file that provokes a per-frame decoder
        // complaint reaches the buffer's few kilobytes easily.
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        var stderr = await stderrTask;
        await proc.WaitForExitAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (proc.ExitCode != 0)
            throw new AppError($"ffmpeg PCM decoding failed for \"{file}\": {stderr.Trim()}");

        var bytes = ms.GetBuffer();
        var count = (int)(ms.Length / sizeof(float));
        var samples = new float[count];
        Buffer.BlockCopy(bytes, 0, samples, 0, count * sizeof(float));
        return samples;
    }

    /// <summary>Chunk size for <see cref="ReadRawPcmAsync"/>: 65536 samples is ~4 s of 16 kHz
    /// audio, 256 KB per chunk - large enough to keep per-chunk overhead low, small enough that
    /// the whole file is never held in memory at once.</summary>
    private const int StreamChunkSamples = 65536;

    /// <summary>
    /// How far the PCM stream handed to the VAD may run out of step with the file's presentation
    /// timeline before <c>aresample</c>'s hard compensation trims or pads it back into line, in
    /// seconds. The VAD has no timestamps: it dates every frame by its sample index, so any
    /// discrepancy between "samples decoded so far" and "seconds of play time so far" becomes an
    /// error in every speech boundary it reports - and every mark those boundaries anchor.
    /// <para>
    /// The discrepancy is real and is a property of the file, not of the decoder. An AAC packet
    /// always decodes to 1024 samples, but an MP4 may declare a shorter duration for one, which is
    /// how a container assembled by concatenating separately-encoded pieces absorbs each piece's
    /// encoder delay. Every such packet leaves the raw sample stream a fraction of a second longer
    /// than the timeline silencedetect, <c>-ss</c> seeking and the written chapter marks all use, and
    /// the error only ever accumulates.
    /// </para>
    /// <para>
    /// A millisecond is far below anything the rest of the pipeline resolves (a VAD frame is 32 ms)
    /// and far above the sub-sample rounding a well-formed file produces, so a clean file is left
    /// untouched while a junction of a few milliseconds is corrected where it happens rather than
    /// being carried forward. Left at ffmpeg's own 0.1 s default the same file would still drift, just
    /// in visible 0.1 s steps instead of smoothly.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the file the drift was measured on, how far it ran, and how much of the corpus carries it.
    /// <include file='../../notes/Audio/FfmpegClient.xml' path='doc/member[@name="PcmResyncToleranceSeconds"]/*' /></remarks>
    private const double PcmResyncToleranceSeconds = 0.001;

    /// <inheritdoc/>
    public async Task<List<Silence>> DetectSilencesAndStreamPcmAsync(
        string file, double durationSeconds, double minSilenceSeconds, double noiseDb,
        Func<IAsyncEnumerable<float[]>, CancellationToken, Task> consumePcm,
        Action<double>? progress, string? inputDecoder, CancellationToken ct)
    {
        var silences = new List<Silence>();
        double? pendingStart = null;
        double processedSeconds = 0;

        List<string> args = ["-hide_banner", "-nostats", "-nostdin", "-progress", "pipe:2"];
        if (inputDecoder != null)
            args.AddRange(["-c:a", inputDecoder]);
        args.AddRange(
            [
                "-i", file,
                // A single decode forked into two independent filter branches instead of two
                // full-file ffmpeg passes: one through silencedetect (as in
                // DetectSilencesAsync, discarded to a null muxer - only its stderr log
                // matters), the other resampled for the VAD model and streamed as raw PCM on
                // stdout. Explicit -map'd outputs from a filtergraph, so (unlike
                // DetectSilencesAsync) no automatic stream selection ever needs -vn/-sn/-dn to
                // keep cover art or subtitle streams out.
                //
                // The PCM branch resamples *in sync with the presentation timeline* rather than
                // simply emitting whatever the decoder produces: async=1 with first_pts=0 pads or
                // trims so that sample N really is second N/16000 of the file, which is the one
                // thing the VAD assumes and cannot check (see PcmResyncToleranceSeconds). The
                // silencedetect branch needs nothing of the sort - it reads timestamps off the
                // frames it is handed.
                "-filter_complex",
                "[0:a]asplit=2[sd][pcm];" +
                $"[sd]silencedetect=noise={noiseDb.ToString(CultureInfo.InvariantCulture)}dB:d={minSilenceSeconds.ToString(CultureInfo.InvariantCulture)}[sdout];" +
                $"[pcm]aresample={SampleRate}:async=1:first_pts=0" +
                $":min_hard_comp={PcmResyncToleranceSeconds.ToString(CultureInfo.InvariantCulture)}," +
                "aformat=sample_fmts=flt:channel_layouts=mono[pcmout]",
                "-map", "[sdout]", "-f", "null", "-",
                "-map", "[pcmout]", "-f", "f32le", "pipe:1",
            ]);

        using var proc = StartProcess(_ffmpeg, args, redirectStdout: true);
        using var reg = ct.Register(() => TryKill(proc));

        var pcmTask = consumePcm(ReadRawPcmAsync(proc.StandardOutput.BaseStream, ct), ct);
        var stderrTask = PumpSilenceAndProgressAsync(proc, silences, s => pendingStart = s, () => pendingStart,
            s => { processedSeconds = s; progress?.Invoke(s); }, ct);

        // Await whichever finishes first: a fault in either (VAD model failure, or a
        // stderr-parsing error) must kill the process so the other task's pipe read reaches
        // EOF instead of hanging forever on a stdout/stderr pipe nothing writes to or reads
        // from anymore. A clean finish on one side needs no such nudge - the process closes
        // both pipes together at real EOF - so this only ever fires on an actual fault.
        var first = await Task.WhenAny(pcmTask, stderrTask);
        if (first.IsFaulted || first.IsCanceled)
            TryKill(proc);
        await Task.WhenAll(pcmTask, stderrTask);

        await proc.WaitForExitAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (proc.ExitCode != 0)
            throw new AppError($"ffmpeg silence/VAD scan failed for \"{file}\".");

        // Guard against silent early termination: the scan must have covered the whole file.
        if (processedSeconds < durationSeconds - Math.Max(30, durationSeconds * 0.02))
            throw new AppError(
                $"Silence scan of \"{file}\" ended prematurely after {processedSeconds:0.#} of " +
                $"{durationSeconds:0.#} seconds.");

        // A silence still open at the end of the file gets closed at the file's duration.
        if (pendingStart is { } open)
            silences.Add(new Silence(Math.Max(0, open), durationSeconds));
        return silences;
    }

    /// <summary>Reads ffmpeg's stderr for <see cref="DetectSilencesAndStreamPcmAsync"/>: silence
    /// start/end markers (appended to <paramref name="silences"/>) and "-progress pipe:2" time
    /// markers (forwarded via <paramref name="onProgress"/>).</summary>
    private static async Task PumpSilenceAndProgressAsync(
        Process proc, List<Silence> silences, Action<double?> setPendingStart, Func<double?> getPendingStart,
        Action<double> onProgress, CancellationToken ct)
    {
        while (await proc.StandardError.ReadLineAsync(ct) is { } line)
        {
            var m = SilenceStartRegex().Match(line);
            if (m.Success)
            {
                setPendingStart(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
                continue;
            }
            m = SilenceEndRegex().Match(line);
            if (m.Success && getPendingStart() is { } s)
            {
                silences.Add(new Silence(Math.Max(0, s),
                    double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)));
                setPendingStart(null);
                continue;
            }
            m = ProgressTimeRegex().Match(line);
            if (m.Success)
                onProgress(long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 1_000_000.0);
        }
    }

    /// <summary>Reads a stdout stream of raw f32le PCM in bounded-size chunks, for scanning a
    /// full audiobook without holding the entire decoded file in memory.</summary>
    private static async IAsyncEnumerable<float[]> ReadRawPcmAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        var byteBuffer = new byte[StreamChunkSamples * sizeof(float)];
        var filled = 0;
        while (true)
        {
            var read = await stream.ReadAsync(byteBuffer.AsMemory(filled, byteBuffer.Length - filled), ct);
            if (read == 0)
                break;
            filled += read;
            if (filled < byteBuffer.Length)
                continue;
            yield return BytesToFloats(byteBuffer, filled);
            filled = 0;
        }
        // A trailing partial chunk; f32le byte counts are always a multiple of 4 in practice,
        // but guard against a stray truncated sample regardless.
        var wholeBytes = filled - filled % sizeof(float);
        if (wholeBytes > 0)
            yield return BytesToFloats(byteBuffer, wholeBytes);
    }

    /// <summary>Converts the first <paramref name="byteCount"/> bytes of an f32le buffer to samples.</summary>
    private static float[] BytesToFloats(byte[] bytes, int byteCount)
    {
        var count = byteCount / sizeof(float);
        var floats = new float[count];
        Buffer.BlockCopy(bytes, 0, floats, 0, count * sizeof(float));
        return floats;
    }

    /// <summary>
    /// Writes chapter marks into the file by remuxing (stream copy) into a temporary file
    /// and atomically swapping it in. The original data is never deleted before the new file
    /// has been written and verified, so audiobooks cannot be lost even without --backup.
    /// Stream-copy remuxing does no decode/encode work, but still has to shuffle the entire
    /// file's data through ffmpeg, which on a large audiobook (or a slow disk/network share)
    /// takes long enough to warrant its own progress reporting.
    /// </summary>
    /// <param name="file">Path of the audio file to modify.</param>
    /// <param name="chapters">Chapter marks sorted by start time.</param>
    /// <param name="durationSeconds">Total duration; used as the end of the last chapter.</param>
    /// <param name="backup">True to keep the original file as "*.bak".</param>
    /// <param name="progress">Called with ffmpeg's processed play time in seconds, parsed from
    /// its "-progress" output; null to skip progress reporting.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when <paramref name="backup"/> is set and a "*.bak" file from an earlier run
    /// already existed - it is kept as it was and this file's original discarded, never an error.
    /// See <see cref="SwapInto"/> for why that way round.</returns>
    public async Task<bool> WriteChaptersAsync(
        string file, IReadOnlyList<Chapter> chapters, double durationSeconds,
        bool backup, Action<double>? progress, CancellationToken ct)
    {
        var metaFile = Path.Combine(Path.GetTempPath(), $"abchapterize-{Guid.NewGuid():N}.ffmeta");
        var tmpFile = file + TempInfix + Path.GetExtension(file);
        try
        {
            await File.WriteAllTextAsync(metaFile, BuildFfMetadata(chapters, durationSeconds), new UTF8Encoding(false), ct);

            var (_, stderr, exit) = await RunAsync(_ffmpeg,
                [
                    "-hide_banner", "-v", "error", "-nostdin", "-y",
                    "-i", file, "-f", "ffmetadata", "-i", metaFile,
                    // Map only audio and cover art; a pre-existing chapter text track must
                    // not be copied (it would clash with the new chapter marks).
                    "-map", "0:a", "-map", "0:v?", "-map_metadata", "0", "-map_chapters", "1",
                    "-c", "copy", "-progress", "pipe:1", tmpFile
                ],
                line =>
                {
                    var m = ProgressTimeRegex().Match(line);
                    if (m.Success)
                        progress?.Invoke(long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 1_000_000.0);
                }, ct);
            if (exit != 0)
                throw new AppError($"ffmpeg failed to write chapters for \"{file}\": {stderr.Trim()}");

            // Verify the new file before touching the original.
            var info = await ProbeAsync(tmpFile, ct);
            if (Math.Abs(info.DurationSeconds - durationSeconds) > 2.0)
                throw new AppError($"Verification of \"{tmpFile}\" failed: duration mismatch.");
            if (info.ChapterCount != chapters.Count)
                throw new AppError($"Verification of \"{tmpFile}\" failed: chapter count mismatch.");

            return SwapInto(file, tmpFile, backup);
        }
        finally
        {
            TryDelete(metaFile);
            TryDelete(tmpFile);
        }
    }

    /// <summary>
    /// Replaces <paramref name="file"/> with <paramref name="tmpFile"/>. Without backup - and with
    /// it, when the backup this run would write already exists - the original is parked under a
    /// temporary name and only deleted once the new file is in place, with rollback on failure.
    /// Otherwise it becomes the "*.bak".
    /// <para>
    /// A pre-existing "*.bak" is kept, not overwritten. It is the file as it stood before whichever
    /// earlier run first backed it up, which is the state a user actually wants to be able to get
    /// back to; the file being replaced here is that run's *output*, and overwriting the backup with
    /// it would quietly turn "undo everything this tool did" into "undo the last run only" - and do
    /// so on the second run, invisibly, exactly when a re-run means the first result was unsatisfactory.
    /// The one-line cost is that the backup no longer pairs with this run's original, which is why
    /// <see cref="ABChapterize.Processing.RunStatistics.FormatBackupNote"/> says so on the file's
    /// summary line rather than leaving it to be discovered.
    /// </para>
    /// </summary>
    /// <param name="file">Path of the file being replaced.</param>
    /// <param name="tmpFile">The verified new file to swap in.</param>
    /// <param name="backup">Whether --backup asked for the original to be kept.</param>
    /// <returns>True when a "*.bak" from an earlier run was found and left alone.</returns>
    /// <remarks>Internal for unit testing: which bytes end up where is the whole substance of
    /// this method, and reaching it through <see cref="WriteChaptersAsync"/> would need a real
    /// ffmpeg and a real audiobook to answer a question that is pure file shuffling.</remarks>
    internal static bool SwapInto(string file, string tmpFile, bool backup)
    {
        var bak = file + ".bak";
        var earlierBakKept = backup && File.Exists(bak);

        if (backup && !earlierBakKept)
        {
            Move(file, bak, overwrite: false, $"Could not move \"{file}\" aside to its backup name");
            try
            {
                Move(tmpFile, file, overwrite: false, $"Could not put the newly written \"{file}\" in place");
            }
            catch
            {
                // Roll back. Its own failure replaces the one that caused it, which is the right way
                // round: "the new file could not be moved in" is a run that changed nothing, where
                // "and the audiobook is now called something else" is a folder needing a hand.
                Move(bak, file, overwrite: true, $"Could not put \"{file}\" back from \"{bak}\"");
                throw;
            }
            return false;
        }

        var parked = file + ParkedSuffix;
        Move(file, parked, overwrite: false, $"Could not move \"{file}\" out of the way");
        try
        {
            Move(tmpFile, file, overwrite: false, $"Could not put the newly written \"{file}\" in place");
        }
        catch
        {
            Move(parked, file, overwrite: false, $"Could not put \"{file}\" back from \"{parked}\"");
            throw;
        }
        File.Delete(parked);
        return earlierBakKept;
    }

    /// <summary>
    /// One step of <see cref="SwapInto"/>, reported with the file it failed on.
    /// </summary>
    /// <remarks>
    /// <c>File.Move</c>'s own exceptions carry no path at all - a locked audiobook produces the
    /// bare "Access to the path is denied.", which reaches the user through the top-level handler
    /// as a type name and a sentence naming nothing. That is the least useful moment in the run to
    /// be vague: this is the one place an audiobook can end up under a name nobody asked for, so
    /// every step says which file and where it was going.
    /// </remarks>
    /// <param name="from">The file to move.</param>
    /// <param name="to">Where it is going.</param>
    /// <param name="overwrite">Whether an existing destination may be replaced.</param>
    /// <param name="what">What failed, as a sentence opening naming the file.</param>
    /// <exception cref="AppError">The move failed.</exception>
    private static void Move(string from, string to, bool overwrite, string what)
    {
        try
        {
            File.Move(from, to, overwrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new AppError($"{what}: {ex.Message}");
        }
    }

    /// <summary>Builds an FFMETADATA1 document containing only the chapter list.
    /// Internal for unit testing.</summary>
    /// <param name="chapters">Chapter marks sorted by start time.</param>
    /// <param name="durationSeconds">End time of the last chapter.</param>
    internal static string BuildFfMetadata(IReadOnlyList<Chapter> chapters, double durationSeconds)
    {
        var sb = new StringBuilder(";FFMETADATA1\n");
        for (var i = 0; i < chapters.Count; i++)
        {
            var startMs = (long)Math.Round(chapters[i].StartSeconds * 1000);
            var endMs = i + 1 < chapters.Count
                ? (long)Math.Round(chapters[i + 1].StartSeconds * 1000)
                : (long)Math.Round(durationSeconds * 1000);
            sb.Append("[CHAPTER]\nTIMEBASE=1/1000\n");
            sb.Append("START=").Append(startMs).Append('\n');
            sb.Append("END=").Append(Math.Max(endMs, startMs + 1)).Append('\n');
            sb.Append("title=").Append(EscapeMeta(chapters[i].Title)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Escapes the characters '=', ';', '#', '\' and newline for FFMETADATA files.
    /// Internal for unit testing.</summary>
    /// <param name="s">The text to escape, e.g. a chapter title.</param>
    internal static string EscapeMeta(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is '=' or ';' or '#' or '\\')
                sb.Append('\\');
            if (c == '\n') { sb.Append("\\\n"); continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Reverses <see cref="EscapeMeta"/>: strips the backslash before an escaped
    /// '=', ';', '#', '\' or newline. Internal for unit testing.</summary>
    /// <param name="s">The escaped text, as read out of an FFMETADATA file.</param>
    internal static string UnescapeMeta(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                i++;
                sb.Append(s[i]);
                continue;
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Starts a child process with redirected streams and hidden window.
    /// </summary>
    /// <remarks>
    /// Both text streams are pinned to UTF-8, which is what ffmpeg and ffprobe write whatever the
    /// console is set to. Windows would otherwise decode them with the console output code page,
    /// and ffprobe's stdout carries chapter titles: a Spanish book's "Capitulo 1" read back through
    /// the wrong page and written out again by --verify --fix would corrupt the user's file
    /// silently, the mode's whole promise being that it only moves marks. Today the pin is
    /// belt-and-braces - an unpinned read has been measured decoding correctly under every console
    /// code page tried, because InvariantGlobalization leaves .NET unable to build a legacy code page
    /// and its fallback is UTF-8 - but that makes the behaviour
    /// hostage to a csproj switch it has nothing to do with, so it is stated here instead. Harmless
    /// for the two callers that want bytes: DecodePcmAsync and DetectSilencesAndStreamPcmAsync read
    /// StandardOutput.BaseStream, which never goes near the decoder.
    /// <para>Notes: what an unpinned read actually does under every console code page tried.
    /// <include file='../../notes/Audio/FfmpegClient.xml' path='doc/member[@name="StartProcess"]/*' /></para>
    /// </remarks>
    /// <param name="exe">Executable to start.</param>
    /// <param name="args">Arguments, passed through ArgumentList so the OS quotes them.</param>
    /// <param name="redirectStdout">Whether stdout is wanted; stderr is always redirected.</param>
    private static Process StartProcess(string exe, IEnumerable<string> args, bool redirectStdout)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectStdout,
            RedirectStandardError = true,
            StandardOutputEncoding = redirectStdout ? Encoding.UTF8 : null,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        var proc = Process.Start(psi) ?? throw new AppError($"Could not start {exe}.");
        return proc;
    }

    /// <summary>
    /// Runs a process to completion, optionally forwarding every stdout/stderr line to a callback.
    /// <para>
    /// The two pipes are drained by two concurrent tasks - anything else deadlocks as soon as one
    /// of them fills - so <paramref name="lineCallback"/> may be entered from both at once. Every
    /// caller here gets away without a lock because ffmpeg keeps the two line kinds apart:
    /// <c>-progress pipe:1</c> writes only to stdout and silencedetect only to stderr, so no two
    /// threads ever touch the same captured variable. A callback that reads or writes state shared
    /// between the two streams would need its own synchronization.
    /// </para>
    /// </summary>
    /// <returns>Captured stdout, captured stderr and the exit code.</returns>
    private static async Task<(string Stdout, string Stderr, int ExitCode)> RunAsync(
        string exe, IEnumerable<string> args, Action<string>? lineCallback, CancellationToken ct)
    {
        using var proc = StartProcess(exe, args, redirectStdout: true);
        using var reg = ct.Register(() => TryKill(proc));

        var stdoutSb = new StringBuilder();
        var stderrSb = new StringBuilder();

        async Task Pump(StreamReader reader, StringBuilder sink)
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                sink.AppendLine(line);
                lineCallback?.Invoke(line);
            }
        }

        var outTask = Pump(proc.StandardOutput, stdoutSb);
        var errTask = Pump(proc.StandardError, stderrSb);
        await Task.WhenAll(outTask, errTask);
        await proc.WaitForExitAsync(ct);
        ct.ThrowIfCancellationRequested();
        return (stdoutSb.ToString(), stderrSb.ToString(), proc.ExitCode);
    }

    /// <summary>Kills a process, ignoring races with normal termination.</summary>
    /// <param name="proc">The child process to stop.</param>
    private static void TryKill(Process proc)
    {
        try
        {
            // The ordinary case is a process that finished on its own between the cancellation and
            // this callback, and asking first keeps that out of the catch entirely. What remains is
            // a genuine failure to kill ffmpeg, which is worth not pretending is the ordinary case:
            // a survivor holds the audiobook open, and the next run on that file is what discovers
            // it. Still swallowed rather than thrown - this runs from a cancellation callback, on
            // whichever thread cancelled, where an exception has nowhere to go and would replace
            // the reason for the cancellation with a report about the cleanup.
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            /* nowhere to report it from here - see above */
        }
    }

    /// <summary>Deletes a file if it exists, ignoring any error.</summary>
    /// <param name="path">The file to remove.</param>
    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
