// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Errors;
using ABChapterize.Gpu;
using ABChapterize.Processing;
using ABChapterize.Ui;

namespace ABChapterize.Cli;

/// <summary>
/// Entry point of the abchapterize tool: command line handling, Ctrl+C wiring and
/// top-level error reporting.
/// </summary>
public static class Program
{
    /// <summary>
    /// Main entry point.
    /// </summary>
    /// <param name="args">Raw command line arguments.</param>
    /// <returns>0 on success (warnings included), 1 on fatal errors, 2 on usage errors, 130 on Ctrl+C.</returns>
    public static async Task<int> Main(string[] args)
    {
        // --version wins over everything else on the command line and needs no target path.
        if (args.Contains("--version"))
        {
            var buildInfo = CliOptions.BuildNumber != null
                ? $" (build {CliOptions.BuildNumber}, built {CliOptions.BuildTimestamp})"
                : "";
            Console.WriteLine($"abchapterize {CliOptions.Version}{buildInfo}");
            return 0;
        }

        // Likewise standalone: --list-gpus answers "what may I pass to --use-gpu on this machine?"
        // and needs neither a file nor a model.
        if (args.Contains("--list-gpus"))
            return ListGpus();

        CliOptions? options;
        try
        {
            options = CliOptions.Parse(args);
        }
        catch (CliError ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.Error.WriteLine("Run \"abchapterize --help\" for usage.");
            return 2;
        }

        if (options is null)
        {
            Console.WriteLine(CliOptions.UsageText);
            return 0;
        }

        // Graceful Ctrl+C: cancel all pending work (which also kills child processes)
        // instead of hard-terminating; temporary files are cleaned up on the way out.
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            // Opened before the renderer and therefore closed after it, so the renderer's last
            // summary line still reaches the file. A log file that cannot be opened is fatal
            // rather than ignored, and lands in the AppError handler below.
            using var logFile = options.LogFilePath != null ? LogFile.Open(options.LogFilePath) : null;
            using var progress = new ProgressRenderer(options.Quiet, options.Verbose, options.NoBar, logFile, options.Color);
            var processor = new FileProcessor(options, progress);
            await processor.RunAsync(cts.Token);
            return 0;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Console.Error.WriteLine("Aborted by user.");
            return 130;
        }
        catch (AppError ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            // Any unexpected error also stops the run immediately, but with a readable message.
            Console.Error.WriteLine($"Error: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Prints the Vulkan GPUs of this machine, as <c>--use-gpu</c> sees them.
    /// </summary>
    /// <returns>0 even when nothing was found: "this machine has no Vulkan GPU" is an answer to
    /// the question that was asked, not a failure to answer it.</returns>
    private static int ListGpus()
    {
        var devices = VulkanDeviceEnumerator.Enumerate();
        if (devices.Count == 0)
        {
            Console.WriteLine("No Vulkan GPUs found (Whisper will use CUDA or the CPU backend).");
            return 0;
        }

        Console.WriteLine("Vulkan GPUs on this machine:");
        foreach (var device in devices)
            Console.WriteLine($"  {device}");

        // The indices are printed for completeness, but the names are what --use-gpu is for; see
        // GpuDevice's remarks for why an index is worth less than it looks. The example names a
        // discrete card wherever there is one, for the same reason GpuSelector prefers it:
        // enumeration order differs between session types on one machine, so "the last device" is
        // not a meaningful thing to point somebody at.
        var example = devices.LastOrDefault(d => d.Kind == GpuDeviceKind.Discrete) ?? devices[^1];
        Console.WriteLine();
        Console.WriteLine("Pass any distinctive part of a name to --use-gpu, e.g. --use-gpu "
            + FirstWordOf(example.Name).ToLowerInvariant() + ".");
        return 0;
    }

    /// <summary>
    /// First whitespace-separated word of a device name, used to make the <c>--list-gpus</c> hint
    /// concrete instead of generic.
    /// </summary>
    /// <param name="name">A device name as the driver reported it.</param>
    private static string FirstWordOf(string name)
    {
        var word = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;
        return word.Length > 0 ? word : name;
    }
}
