// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Security.Cryptography;
using ABChapterize.Audio;
using ABChapterize.Cli;

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
    /// GGML file name, approximate download size, and pinned SHA-256/SHA3-256 digests for every
    /// model selector. Two independently-designed hash algorithms must both match before a
    /// download is accepted (see <see cref="VerifyHashes"/>): a compromise of the Hugging Face
    /// repository that swapped a model file for something else would have to defeat both digests
    /// at once, not just one, to go undetected. The digests were captured directly from the
    /// repository's Git LFS metadata (SHA-256) and by hashing the downloaded bytes (SHA3-256) at
    /// the time this list was written.
    /// </summary>
    private static readonly Dictionary<string, (string FileName, string ApproxSize, string Sha256, string Sha3_256)> Models = new()
    {
        ["tiny"] = ("ggml-tiny.bin", "75 MB",
            "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21",
            "735e6afc370ed51e69fc219361339fed771ee9770faff27057333af7ad2e00ba"),
        ["base"] = ("ggml-base.bin", "140 MB",
            "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe",
            "11f83e4f60e406bab839beb4769d1a9218033b2ba51acadda7a9afff73818c8d"),
        ["small"] = ("ggml-small.bin", "465 MB",
            "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b",
            "40c4027bd9b0a5d0a3a05eb7d8f4b38f3005fbb5da02b6cb86027fc478a924cc"),
        ["medium"] = ("ggml-medium.bin", "1.5 GB",
            "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208",
            "34f7f9d75840a25317fc18961c308f12392e110cbaee04d25fccf75bff24ea5f"),
        ["turbo"] = ("ggml-large-v3-turbo.bin", "1.6 GB",
            "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69",
            "01060757e2cbdec1eb55cdbc9f65293256103fb5b4c9fe4e4f237263637609b0"),
        ["large"] = ("ggml-large-v3.bin", "3.1 GB",
            "64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2",
            "0a1556022c6b7846bbd12d41342c1ca320647949c96cbe3900cbc07bc3d15a66"),
    };

    /// <summary>
    /// Returns the full path of the GGML file for a model selector (tiny, base, small, medium,
    /// turbo, large), downloading the file into the "models" folder next to the executable when
    /// it is not there yet. Partial downloads are written to a temporary file first, so an
    /// aborted download never counts as an installed model.
    /// </summary>
    /// <param name="model">Validated model selector from the command line.</param>
    /// <param name="ct">Cancellation token bound to Ctrl+C.</param>
    /// <exception cref="AppError">
    /// Thrown when the download fails; the message contains step-by-step manual instructions.
    /// </exception>
    public static async Task<string> EnsureModelAsync(string model, CancellationToken ct)
    {
        if (!Models.TryGetValue(model, out var m))
            throw new AppError($"Unknown model \"{model}\".");

        var dir = Path.Combine(AppContext.BaseDirectory, "models");
        var path = Path.Combine(dir, m.FileName);
        if (File.Exists(path))
            return path;

        var url = BaseUrl + m.FileName;
        var tempPath = path + ".download";
        Console.WriteLine(
            $"Whisper model \"{model}\" not found - downloading it now (about {m.ApproxSize}, one time only)...");
        try
        {
            Directory.CreateDirectory(dir);
            await DownloadAsync(url, tempPath, m.Sha256, m.Sha3_256, ct);
            File.Move(tempPath, path, overwrite: true);
            Console.WriteLine($"Model downloaded and verified at {path}");
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
    /// <param name="ct">Cancellation token bound to Ctrl+C.</param>
    private static async Task DownloadAsync(
        string url, string targetPath, string expectedSha256, string expectedSha3_256, CancellationToken ct)
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

                if (!Console.IsOutputRedirected && Environment.TickCount64 - lastReport >= 250)
                {
                    lastReport = Environment.TickCount64;
                    var line = total is > 0
                        ? $"\rDownloading... {done * 100 / total.Value,3}% ({done >> 20} of {total.Value >> 20} MB)"
                        : $"\rDownloading... {done >> 20} MB";
                    Console.Write(line + "   ");
                }
            }
            if (!Console.IsOutputRedirected)
                Console.Write("\r" + new string(' ', 50) + "\r");

            if (total is > 0 && done != total.Value)
                throw new IOException($"the connection closed early ({done} of {total.Value} bytes received)");
            if (done < 10_000_000)
                throw new IOException($"the downloaded file is implausibly small ({done} bytes) - " +
                                      "probably an error page was served instead of the model");

            VerifyHashes(sha256, sha3, expectedSha256, expectedSha3_256);
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
    private static void VerifyHashes(
        IncrementalHash sha256, IncrementalHash? sha3, string expectedSha256, string expectedSha3_256)
    {
        var actualSha256 = Convert.ToHexStringLower(sha256.GetHashAndReset());
        if (!actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException(
                $"SHA-256 checksum mismatch (expected {expectedSha256}, got {actualSha256}) - " +
                "the download may be corrupted, or the file at the source has changed unexpectedly");

        if (sha3 == null)
        {
            Console.WriteLine("Note: this platform has no SHA3-256 support, only SHA-256 was verified.");
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
