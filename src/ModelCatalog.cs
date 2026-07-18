namespace Chapterize;

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

    /// <summary>GGML file name and approximate download size for every model selector.</summary>
    private static readonly Dictionary<string, (string FileName, string ApproxSize)> Models = new()
    {
        ["tiny"] = ("ggml-tiny.bin", "75 MB"),
        ["base"] = ("ggml-base.bin", "140 MB"),
        ["small"] = ("ggml-small.bin", "465 MB"),
        ["medium"] = ("ggml-medium.bin", "1.5 GB"),
        ["turbo"] = ("ggml-large-v3-turbo.bin", "1.6 GB"),
        ["large"] = ("ggml-large-v3.bin", "3.1 GB"),
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
            await DownloadAsync(url, tempPath, ct);
            File.Move(tempPath, path, overwrite: true);
            Console.WriteLine($"Model downloaded to {path}");
            return path;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AppError(BuildFailureMessage(model, m.ApproxSize, url, path, ex.Message));
        }
        finally
        {
            // Never leave a partial download behind, it would only cause confusion.
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Streams a URL into a file, showing a single-line console progress display. Detects
    /// stalled connections, truncated transfers and implausibly small downloads (e.g. an
    /// HTML error page served instead of the model).
    /// </summary>
    /// <param name="url">Source URL.</param>
    /// <param name="targetPath">Destination file; overwritten when it exists.</param>
    /// <param name="ct">Cancellation token bound to Ctrl+C.</param>
    private static async Task DownloadAsync(string url, string targetPath, CancellationToken ct)
    {
        using var http = new HttpClient();
        // The overall transfer may take arbitrarily long; stalls are detected per read instead.
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"chapterize/{typeof(ModelCatalog).Assembly.GetName().Version?.ToString(3) ?? "0"}");

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = new FileStream(
            targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);

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
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
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
    }

    /// <summary>
    /// Builds the fool-proof error message shown when the automatic download fails,
    /// including step-by-step instructions for installing the model manually.
    /// </summary>
    /// <param name="model">Model selector from the command line.</param>
    /// <param name="approxSize">Human readable approximate model size.</param>
    /// <param name="url">Direct download URL of the model file.</param>
    /// <param name="path">Full local path the model must end up at.</param>
    /// <param name="reason">Description of what went wrong.</param>
    private static string BuildFailureMessage(
        string model, string approxSize, string url, string path, string reason) => $"""
        Could not download the Whisper model "{model}": {reason}

        You can install the model manually instead:
          1. Download this file (about {approxSize}) with your browser:
               {url}
          2. Put the file - without renaming it - here:
               {path}
             (Create the "models" folder next to chapterize.exe if it does not exist.)
          3. Run chapterize again with the same options.

        All models can be browsed at {BrowseUrl}

        Things worth checking: Is the machine online? Is enough disk space free (the model
        needs about {approxSize})? Is the folder writable? If chapterize.exe resides in a
        write-protected location, either install the model manually as described above or
        move chapterize.exe (together with its "runtimes" and "models" folders) to a
        writable location such as a folder in your user profile.
        """;
}
