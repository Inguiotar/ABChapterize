// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Security.Cryptography;
using ABChapterize.Cli;
using ABChapterize.Errors;

namespace ABChapterize.Transcription;

/// <summary>
/// Maps the model names accepted on the command line to their GGML model files and
/// downloads missing models from Hugging Face on first use.
/// </summary>
public static class ModelCatalog
{
    /// <summary>Download base URL of ggerganov's GGML model repository on Hugging Face.</summary>
    private const string BaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    /// <summary>Browsable overview page of the model repository, for error messages.</summary>
    private const string BrowseUrl = "https://huggingface.co/ggerganov/whisper.cpp/tree/main";

    /// <summary>A download stalled for this long is treated as a dead connection.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Every built-in model selector, smallest first. The one list of them: <see cref="Models"/>
    /// must hold an entry for each (pinned by the catalog's tests, since nothing in the type system
    /// says so), and <c>CliOptions</c> validates a command line against this rather than against a
    /// copy of its own. The order is the quality order the ranking behind <c>--upgrade-model</c>
    /// reads, so a new model belongs in its rightful place rather than appended: "turbo"
    /// (large-v3-turbo) sits just below "large", a distilled large that trades a little accuracy
    /// for a lot of speed and is still clearly ahead of "medium".
    /// </summary>
    public static readonly string[] BuiltInNames = ["tiny", "base", "small", "medium", "turbo", "large"];

    /// <summary>
    /// GGML file name, exact size in bytes, approximate download size, and pinned SHA-256/SHA3-256
    /// digests for every model selector. Two independently-designed hash algorithms must both match
    /// before a download is accepted (see <see cref="VerifyHashes"/>): a compromise of the Hugging
    /// Face repository that swapped a model file for something else would have to defeat both
    /// digests at once, not just one, to go undetected. The digests were captured directly from the
    /// repository's Git LFS metadata (SHA-256) and by hashing the downloaded bytes (SHA3-256) at
    /// the time this list was written; the byte sizes were measured on the same files (2026-07-28)
    /// and describe exactly the bytes those digests cover.
    /// <para>
    /// The sizes exist for <see cref="ApproximateSizeBytes"/>, which needs a model's weight before
    /// the file is necessarily on disk. They ascend in the order of <see cref="BuiltInNames"/>, so
    /// ranking two models by size agrees with the catalog's own quality order - which is what lets
    /// a <c>custom:</c> model be ranked against a built-in one at all.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, (string FileName, long Bytes, string ApproxSize, string Sha256, string Sha3_256)> Models = new()
    {
        ["tiny"] = ("ggml-tiny.bin", 77_691_713, "75 MB",
            "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21",
            "735e6afc370ed51e69fc219361339fed771ee9770faff27057333af7ad2e00ba"),
        ["base"] = ("ggml-base.bin", 147_951_465, "140 MB",
            "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe",
            "11f83e4f60e406bab839beb4769d1a9218033b2ba51acadda7a9afff73818c8d"),
        ["small"] = ("ggml-small.bin", 487_601_967, "465 MB",
            "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b",
            "40c4027bd9b0a5d0a3a05eb7d8f4b38f3005fbb5da02b6cb86027fc478a924cc"),
        ["medium"] = ("ggml-medium.bin", 1_533_763_059, "1.5 GB",
            "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208",
            "34f7f9d75840a25317fc18961c308f12392e110cbaee04d25fccf75bff24ea5f"),
        ["turbo"] = ("ggml-large-v3-turbo.bin", 1_624_555_275, "1.6 GB",
            "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69",
            "01060757e2cbdec1eb55cdbc9f65293256103fb5b4c9fe4e4f237263637609b0"),
        ["large"] = ("ggml-large-v3.bin", 3_095_033_483, "3.1 GB",
            "64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2",
            "0a1556022c6b7846bbd12d41342c1ca320647949c96cbe3900cbc07bc3d15a66"),
    };

    /// <summary>
    /// Prefix marking a <c>--model</c>/<c>--upgrade-model</c> selector as a GGML file of the user's
    /// own rather than one of the catalog's, e.g. <c>custom:D:\models\my-finetune.bin</c>. What
    /// follows it is a path, already resolved to an absolute one by
    /// <see cref="CliOptions.ParseModelSelector"/>, so two spellings of the same file compare equal
    /// and a run fails at parse time rather than after loading a model.
    /// </summary>
    public const string CustomPrefix = "custom:";

    /// <summary>True when the selector names a file of the user's own.</summary>
    /// <param name="model">A model selector.</param>
    public static bool IsCustom(string model) => model.StartsWith(CustomPrefix, StringComparison.Ordinal);

    /// <summary>The file path inside a <c>custom:</c> selector.</summary>
    /// <param name="model">A selector <see cref="IsCustom"/> holds for.</param>
    public static string CustomPath(string model) => model[CustomPrefix.Length..];

    /// <summary>
    /// How big the model behind a selector is, in bytes: the catalog's recorded size for a built-in
    /// one - available whether or not it has been downloaded yet - and the file's real size for a
    /// <c>custom:</c> one. This is the whole basis on which two models are ranked against each
    /// other (see <see cref="CliOptions.UpgradeModelIsBetter"/>): nothing about a GGML file
    /// advertises how capable it is, but within the Whisper family a larger file has always been
    /// the more capable model, and a fine-tune keeps the size of the model it was derived from.
    /// </summary>
    /// <param name="model">A validated model selector.</param>
    /// <returns>The size in bytes, or 0 for a custom file that has since disappeared - which ranks
    /// it below everything rather than throwing, as the run's own model load will fail with a far
    /// more useful message moments later.</returns>
    public static long ApproximateSizeBytes(string model)
    {
        if (!IsCustom(model))
            return Models.TryGetValue(model, out var m) ? m.Bytes : 0;
        try { return new FileInfo(CustomPath(model)).Length; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    /// <summary>
    /// Returns the full path of the GGML file for a model selector (tiny, base, small, medium,
    /// turbo, large), downloading the file into the "models" folder next to the executable when
    /// it is not there yet. Partial downloads are written to a temporary file first, so an
    /// aborted download never counts as an installed model. A <c>custom:</c> selector is simply
    /// its own path: nothing is downloaded, and nothing is verified against a pinned digest either
    /// - the file came from the user, not from the network.
    /// </summary>
    /// <param name="model">Validated model selector from the command line.</param>
    /// <param name="ct">Cancellation token bound to Ctrl+C.</param>
    /// <param name="report">Where to write the download's own lines, or null for the console;
    /// see <see cref="Report"/>.</param>
    /// <exception cref="AppError">
    /// Thrown when the download fails; the message contains step-by-step manual instructions.
    /// Also when a custom model file has disappeared since the command line was parsed - which is
    /// worth its own check because the upgrade model is only resolved once a file reaches Scan,
    /// possibly hours into a run.
    /// </exception>
    public static async Task<string> EnsureModelAsync(
        string model, CancellationToken ct, Action<string>? report = null)
    {
        if (IsCustom(model))
        {
            var custom = CustomPath(model);
            return File.Exists(custom)
                ? custom
                : throw new AppError($"The Whisper model file \"{custom}\" does not exist (any more).");
        }
        if (!Models.TryGetValue(model, out var m))
            throw new AppError($"Unknown model \"{model}\".");

        var dir = Path.Combine(AppContext.BaseDirectory, "models");
        var path = Path.Combine(dir, m.FileName);
        if (File.Exists(path))
            return path;

        var url = BaseUrl + m.FileName;
        var tempPath = path + ".download";
        Report(report,
            $"Whisper model \"{model}\" not found - downloading it now (about {m.ApproxSize}, one time only)...");
        try
        {
            Directory.CreateDirectory(dir);
            await DownloadAsync(url, tempPath, m.Sha256, m.Sha3_256, report, ct);
            File.Move(tempPath, path, overwrite: true);
            Report(report, $"Model downloaded and verified at {path}");
            return path;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AppError(BuildFailureMessage(model, m.ApproxSize, url, path, m.Sha256, ex.Message));
        }
        finally
        {
            // Never leave a partial download behind, it would only cause confusion.
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Writes one of a download's own lines to wherever it is being reported. Null means the
    /// console, which is where the run's model is fetched from - before any progress bar exists. A
    /// <c>--upgrade-model</c> is resolved hours into a run with the bar live and redrawing four times
    /// a second, so <see cref="UpgradeTranscriber"/> passes a reporter that goes through the renderer
    /// instead; two things writing to the same terminal line garble both.
    /// </summary>
    /// <param name="report">The caller's reporter, or null for the console.</param>
    /// <param name="line">The line to write.</param>
    private static void Report(Action<string>? report, string line)
    {
        if (report != null)
            report(line);
        else
            Console.WriteLine(line);
    }

    /// <summary>How often the console's single-line download display is rewritten in place.</summary>
    private const int ConsoleProgressIntervalMs = 250;

    /// <summary>How often a <em>reported</em> download emits a progress line. Far rarer than the
    /// console's, because these scroll past instead of overwriting one another - see
    /// <see cref="DownloadAsync"/>.</summary>
    private const int ReportedProgressIntervalMs = 5000;

    /// <summary>
    /// Streams a URL into a file, showing a single-line console progress display, while hashing
    /// the bytes as they arrive (no second read pass over a multi-gigabyte file). Detects stalled
    /// connections, truncated transfers, implausibly small downloads (e.g. an HTML error page
    /// served instead of the model), and - the whole point of computing the hashes at all - a
    /// file that does not match the digests pinned in <see cref="Models"/>. That last check is
    /// what defends against a compromised or tampered-with source: if the Hugging Face repository
    /// ever served something other than the exact bytes this project was built and tested
    /// against, the download is rejected rather than silently accepted and fed to the native
    /// Whisper model loader.
    /// </summary>
    /// <param name="url">Source URL.</param>
    /// <param name="targetPath">Destination file; overwritten when it exists.</param>
    /// <param name="expectedSha256">Pinned SHA-256 digest (lowercase hex) the download must match.</param>
    /// <param name="expectedSha3_256">Pinned SHA3-256 digest (lowercase hex) the download must match.</param>
    /// <param name="report">Where to write progress, or null for the console's in-place display.</param>
    /// <param name="ct">Cancellation token bound to Ctrl+C.</param>
    private static async Task DownloadAsync(
        string url, string targetPath, string expectedSha256, string expectedSha3_256,
        Action<string>? report, CancellationToken ct)
    {
        using var http = new HttpClient();
        // The overall transfer may take arbitrarily long; stalls are detected per read instead.
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"abchapterize/{typeof(ModelCatalog).Assembly.GetName().Version?.ToString(3) ?? "0"}");

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = new FileStream(
            targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);

        // SHA3-256 needs OS-level crypto support (Windows CNG / OpenSSL 3+ on Linux) that may be
        // missing on an older or minimal system; SHA-256 alone is still enforced in that case.
        var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var sha3 = SHA3_256.IsSupported ? IncrementalHash.CreateHash(HashAlgorithmName.SHA3_256) : null;
        try
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var buffer = new byte[1 << 20];
            long done = 0;
            long lastReport = 0;
            while (true)
            {
                int read;
                readCts.CancelAfter(StallTimeout);
                try
                {
                    read = await source.ReadAsync(buffer, readCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new IOException(
                        $"the connection stalled (no data for {StallTimeout.TotalSeconds:0} seconds)");
                }
                if (read == 0)
                    break;
                var chunk = buffer.AsMemory(0, read);
                await target.WriteAsync(chunk, ct);
                sha256.AppendData(chunk.Span);
                sha3?.AppendData(chunk.Span);
                done += read;

                var interval = report != null ? ReportedProgressIntervalMs : ConsoleProgressIntervalMs;
                if ((report != null || !Console.IsOutputRedirected)
                    && Environment.TickCount64 - lastReport >= interval)
                {
                    lastReport = Environment.TickCount64;
                    var line = total is > 0
                        ? $"Downloading... {done * 100 / total.Value,3}% ({done >> 20} of {total.Value >> 20} MB)"
                        : $"Downloading... {done >> 20} MB";
                    if (report != null)
                        report(line);
                    else
                        Console.Write("\r" + line + "   ");
                }
            }
            if (report == null && !Console.IsOutputRedirected)
                Console.Write("\r" + new string(' ', 50) + "\r");

            if (total is > 0 && done != total.Value)
                throw new IOException($"the connection closed early ({done} of {total.Value} bytes received)");
            if (done < 10_000_000)
                throw new IOException($"the downloaded file is implausibly small ({done} bytes) - " +
                                      "probably an error page was served instead of the model");

            VerifyHashes(sha256, sha3, expectedSha256, expectedSha3_256, report);
        }
        finally
        {
            sha256.Dispose();
            sha3?.Dispose();
        }
    }

    /// <summary>
    /// Compares the digests accumulated while streaming the download against the pinned values.
    /// Both must match - SHA-256 and, when the platform supports it, SHA3-256 as well - since the
    /// whole point of checking two independently-designed algorithms is that a compromised source
    /// would have to defeat both simultaneously to substitute a file undetected. A single-hash
    /// check can (at least in theory) be satisfied by an engineered collision against one specific
    /// algorithm; doing so against two unrelated algorithms at once is not a realistic threat.
    /// </summary>
    /// <param name="sha256">The SHA-256 accumulated over the downloaded bytes.</param>
    /// <param name="sha3">The SHA3-256 accumulated over them, or null on a platform without it.</param>
    /// <param name="expectedSha256">The pinned SHA-256.</param>
    /// <param name="expectedSha3_256">The pinned SHA3-256.</param>
    /// <param name="report">Where to write the one note this can emit, or null for the console.
    /// Threaded through for the reason <see cref="Report"/> records: a --upgrade-model download runs
    /// hours into a run with the progress bar live, and a direct Console.WriteLine lands on the very
    /// line the bar is redrawing.</param>
    private static void VerifyHashes(
        IncrementalHash sha256, IncrementalHash? sha3, string expectedSha256, string expectedSha3_256,
        Action<string>? report)
    {
        var actualSha256 = Convert.ToHexStringLower(sha256.GetHashAndReset());
        if (!actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException(
                $"SHA-256 checksum mismatch (expected {expectedSha256}, got {actualSha256}) - " +
                "the download may be corrupted, or the file at the source has changed unexpectedly");

        if (sha3 == null)
        {
            Report(report, "Note: this platform has no SHA3-256 support, only SHA-256 was verified.");
            return;
        }

        var actualSha3 = Convert.ToHexStringLower(sha3.GetHashAndReset());
        if (!actualSha3.Equals(expectedSha3_256, StringComparison.OrdinalIgnoreCase))
            throw new IOException(
                $"SHA3-256 checksum mismatch (expected {expectedSha3_256}, got {actualSha3}) - " +
                "the download may be corrupted, or the file at the source has changed unexpectedly");
    }

    /// <summary>
    /// Builds the fool-proof error message shown when the automatic download fails,
    /// including step-by-step instructions for installing the model manually.
    /// </summary>
    /// <param name="model">Model selector from the command line.</param>
    /// <param name="approxSize">Human readable approximate model size.</param>
    /// <param name="url">Direct download URL of the model file.</param>
    /// <param name="path">Full local path the model must end up at.</param>
    /// <param name="expectedSha256">Pinned SHA-256 digest, shown so a manual install can be checked too.</param>
    /// <param name="reason">Description of what went wrong.</param>
    private static string BuildFailureMessage(
        string model, string approxSize, string url, string path, string expectedSha256, string reason) => $"""
        Could not download the Whisper model "{model}": {reason}

        You can install the model manually instead:
          1. Download this file (about {approxSize}) with your browser:
               {url}
          2. Verify its SHA-256 checksum matches exactly (e.g. `certutil -hashfile <file> SHA256`
             on Windows, `sha256sum <file>` on Linux) before using it:
               {expectedSha256}
          3. Put the file - without renaming it - here:
               {path}
             (Create the "models" folder next to {CliOptions.ExeName} if it does not exist.)
          4. Run abchapterize again with the same options.

        All models can be browsed at {BrowseUrl}

        Things worth checking: Is the machine online? Is enough disk space free (the model
        needs about {approxSize})? Is the folder writable? If {CliOptions.ExeName} resides in a
        write-protected location, either install the model manually as described above or
        move {CliOptions.ExeName} (together with its "runtimes" and "models" folders) to a
        writable location such as a folder in your user profile.
        """;
}
