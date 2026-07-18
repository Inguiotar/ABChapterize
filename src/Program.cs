// Chapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace Chapterize;

/// <summary>
/// Entry point of the chapterize tool: command line handling, Ctrl+C wiring and
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
            Console.WriteLine($"chapterize {CliOptions.Version}");
            return 0;
        }

        CliOptions? options;
        try
        {
            options = CliOptions.Parse(args);
        }
        catch (CliError ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(CliOptions.UsageText);
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

        using var progress = new ProgressRenderer(options.Quiet, options.Verbose, options.NoBar);
        var processor = new FileProcessor(options, progress);
        try
        {
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
}
