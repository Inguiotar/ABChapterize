using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Chapterize;

/// <summary>A chapter marking with start time and title.</summary>
/// <param name="StartSeconds">Chapter start in seconds from the beginning of the file.</param>
/// <param name="Title">Chapter title as written into the file metadata.</param>
public readonly record struct Chapter(double StartSeconds, string Title);

/// <summary>A period of silence detected in the audio stream.</summary>
/// <param name="StartSeconds">Start of the silence in seconds.</param>
/// <param name="EndSeconds">End of the silence in seconds.</param>
public readonly record struct Silence(double StartSeconds, double EndSeconds);

/// <summary>Result of probing a media file with ffprobe.</summary>
/// <param name="DurationSeconds">Total play time in seconds.</param>
/// <param name="SizeBytes">File size in bytes.</param>
/// <param name="ChapterCount">Number of pre-existing chapter markings.</param>
public readonly record struct MediaInfo(double DurationSeconds, long SizeBytes, int ChapterCount);

/// <summary>
/// Thin wrapper around the ffmpeg and ffprobe command line tools: media probing,
/// silence detection, PCM decoding for Whisper, and safe chapter writing.
/// </summary>
public sealed class FfmpegClient
{
    private readonly string _ffmpeg;
    private readonly string _ffprobe;

    /// <summary>Sample rate of decoded PCM audio; Whisper requires 16 kHz.</summary>
    public const int SampleRate = 16000;

    /// <summary>Creates a client using the given executable paths (see <see cref="FfmpegLocator"/>).</summary>
    /// <param name="ffmpegPath">Full path of ffmpeg.exe.</param>
    /// <param name="ffprobePath">Full path of ffprobe.exe.</param>
    public FfmpegClient(string ffmpegPath, string ffprobePath)
    {
        _ffmpeg = ffmpegPath;
        _ffprobe = ffprobePath;
    }

    /// <summary>Reads duration, size and pre-existing chapter count of a media file.</summary>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="ct">Cancellation token for graceful Ctrl+C handling.</param>
    public async Task<MediaInfo> ProbeAsync(string file, CancellationToken ct)
    {
        var (stdout, _, exit) = await RunAsync(_ffprobe,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_chapters", file],
            null, ct);
        if (exit != 0)
            throw new AppError($"ffprobe failed for \"{file}\".");

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        double duration = 0;
        if (root.TryGetProperty("format", out var format) &&
            format.TryGetProperty("duration", out var dur))
            duration = double.Parse(dur.GetString()!, CultureInfo.InvariantCulture);
        var chapters = root.TryGetProperty("chapters", out var ch) ? ch.GetArrayLength() : 0;
        var size = new FileInfo(file).Length;
        return new MediaInfo(duration, size, chapters);
    }

    /// <summary>
    /// Scans the whole file for silence periods using ffmpeg's silencedetect filter.
    /// </summary>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="minSilenceSeconds">Minimum silence duration to report.</param>
    /// <param name="noiseDb">Noise floor in dBFS below which audio counts as silence.</param>
    /// <param name="progress">Callback receiving the processed play time in seconds.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All detected silence periods in chronological order.</returns>
    public async Task<List<Silence>> DetectSilencesAsync(
        string file, double durationSeconds, double minSilenceSeconds, int noiseDb,
        Action<double>? progress, CancellationToken ct)
    {
        var silences = new List<Silence>();
        double? pendingStart = null;

        var startRe = new Regex(@"silence_start:\s*(-?[\d.]+)");
        var endRe = new Regex(@"silence_end:\s*(-?[\d.]+)");
        var timeRe = new Regex(@"out_time_us=(\d+)");
        double processedSeconds = 0;

        var (_, _, exit) = await RunAsync(_ffmpeg,
            [
                "-hide_banner", "-nostats", "-nostdin",
                "-i", file,
                // Audio only: an embedded cover art is a one-frame video stream whose
                // immediate EOF would otherwise end the whole run after a fraction of
                // a second, silently skipping the rest of the file.
                "-vn", "-sn", "-dn",
                "-af", $"silencedetect=noise={noiseDb}dB:d={minSilenceSeconds.ToString(CultureInfo.InvariantCulture)}",
                "-progress", "pipe:1",
                "-f", "null", "-"
            ],
            line =>
            {
                var m = startRe.Match(line);
                if (m.Success)
                {
                    pendingStart = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    return;
                }
                m = endRe.Match(line);
                if (m.Success && pendingStart is { } s)
                {
                    silences.Add(new Silence(Math.Max(0, s),
                        double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)));
                    pendingStart = null;
                    return;
                }
                m = timeRe.Match(line);
                if (m.Success)
                {
                    processedSeconds = long.Parse(m.Groups[1].Value) / 1_000_000.0;
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

    /// <summary>
    /// Decodes a section of the file to 16 kHz mono 32-bit float PCM suitable for Whisper.
    /// </summary>
    /// <param name="file">Path of the audio file.</param>
    /// <param name="startSeconds">Start position in seconds.</param>
    /// <param name="durationSeconds">Length in seconds, or null to decode to the end of the file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The decoded samples.</returns>
    public async Task<float[]> DecodePcmAsync(
        string file, double startSeconds, double? durationSeconds, CancellationToken ct)
    {
        var args = new List<string>
        {
            "-hide_banner", "-v", "error", "-nostdin",
            "-ss", startSeconds.ToString("0.###", CultureInfo.InvariantCulture),
        };
        if (durationSeconds is { } d)
        {
            args.Add("-t");
            args.Add(d.ToString("0.###", CultureInfo.InvariantCulture));
        }
        args.AddRange(["-i", file, "-ac", "1", "-ar", SampleRate.ToString(), "-f", "f32le", "pipe:1"]);

        using var proc = StartProcess(_ffmpeg, args, redirectStdout: true);
        using var reg = ct.Register(() => TryKill(proc));

        using var ms = new MemoryStream();
        await proc.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
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

    /// <summary>
    /// Writes chapter markings into the file by remuxing (stream copy) into a temporary file
    /// and atomically swapping it in. The original data is never deleted before the new file
    /// has been written and verified, so audiobooks cannot be lost even without --backup.
    /// </summary>
    /// <param name="file">Path of the audio file to modify.</param>
    /// <param name="chapters">Chapter markings sorted by start time.</param>
    /// <param name="durationSeconds">Total duration; used as the end of the last chapter.</param>
    /// <param name="backup">True to keep the original file as "*.bak".</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteChaptersAsync(
        string file, IReadOnlyList<Chapter> chapters, double durationSeconds,
        bool backup, CancellationToken ct)
    {
        var metaFile = Path.Combine(Path.GetTempPath(), $"chapterize-{Guid.NewGuid():N}.ffmeta");
        var tmpFile = file + ".chapterize.tmp" + Path.GetExtension(file);
        try
        {
            await File.WriteAllTextAsync(metaFile, BuildFfMetadata(chapters, durationSeconds), new UTF8Encoding(false), ct);

            var (_, stderr, exit) = await RunAsync(_ffmpeg,
                [
                    "-hide_banner", "-v", "error", "-nostdin", "-y",
                    "-i", file, "-f", "ffmetadata", "-i", metaFile,
                    // Map only audio and cover art; a pre-existing chapter text track must
                    // not be copied (it would clash with the new chapter markings).
                    "-map", "0:a", "-map", "0:v?", "-map_metadata", "0", "-map_chapters", "1",
                    "-c", "copy", tmpFile
                ], null, ct);
            if (exit != 0)
                throw new AppError($"ffmpeg failed to write chapters for \"{file}\": {stderr.Trim()}");

            // Verify the new file before touching the original.
            var info = await ProbeAsync(tmpFile, ct);
            if (Math.Abs(info.DurationSeconds - durationSeconds) > 2.0)
                throw new AppError($"Verification of \"{tmpFile}\" failed: duration mismatch.");
            if (info.ChapterCount != chapters.Count)
                throw new AppError($"Verification of \"{tmpFile}\" failed: chapter count mismatch.");

            SwapInto(file, tmpFile, backup);
        }
        finally
        {
            TryDelete(metaFile);
            TryDelete(tmpFile);
        }
    }

    /// <summary>
    /// Replaces <paramref name="file"/> with <paramref name="tmpFile"/>. With backup the original
    /// is renamed to "*.bak"; otherwise it is parked under a temporary name and only deleted after
    /// the new file is in place, with rollback on failure.
    /// </summary>
    private static void SwapInto(string file, string tmpFile, bool backup)
    {
        if (backup)
        {
            var bak = file + ".bak";
            if (File.Exists(bak))
                throw new AppError($"Backup file already exists: {bak}");
            File.Move(file, bak);
            try
            {
                File.Move(tmpFile, file);
            }
            catch
            {
                File.Move(bak, file); // roll back
                throw;
            }
        }
        else
        {
            var parked = file + ".chapterize.orig";
            File.Move(file, parked);
            try
            {
                File.Move(tmpFile, file);
            }
            catch
            {
                File.Move(parked, file); // roll back
                throw;
            }
            File.Delete(parked);
        }
    }

    /// <summary>Builds an FFMETADATA1 document containing only the chapter list.</summary>
    /// <param name="chapters">Chapter markings sorted by start time.</param>
    /// <param name="durationSeconds">End time of the last chapter.</param>
    private static string BuildFfMetadata(IReadOnlyList<Chapter> chapters, double durationSeconds)
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

    /// <summary>Escapes the characters '=', ';', '#', '\' and newline for FFMETADATA files.</summary>
    private static string EscapeMeta(string s)
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

    /// <summary>Starts a child process with redirected streams and hidden window.</summary>
    private static Process StartProcess(string exe, IEnumerable<string> args, bool redirectStdout)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectStdout,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        var proc = Process.Start(psi) ?? throw new AppError($"Could not start {exe}.");
        return proc;
    }

    /// <summary>
    /// Runs a process to completion, optionally forwarding every stdout/stderr line to a callback.
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
    private static void TryKill(Process proc)
    {
        try { proc.Kill(entireProcessTree: true); } catch { /* already exited */ }
    }

    /// <summary>Deletes a file if it exists, ignoring any error.</summary>
    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
