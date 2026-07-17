using System.Text;

namespace Chapterize;

/// <summary>
/// Tracks the byte-based work of processing one file. Work is measured in processed bytes
/// (file size for full passes, proportional amounts for partial decodes), never in play time.
/// Thread-safe enough for the single-producer/single-renderer usage in this tool.
/// </summary>
public sealed class WorkTracker
{
    private long _totalBytes;
    private long _doneBytes;
    private long _phaseBytes;

    /// <summary>Number of chapters discovered so far; shown next to the progress bar.</summary>
    public int ChaptersFound { get; set; }

    /// <summary>
    /// Adds expected work to the total, e.g. when a new processing phase is scheduled.
    /// A negative amount corrects an earlier estimate downwards; the total never drops
    /// below the work already done.
    /// </summary>
    /// <param name="bytes">Estimated number of bytes the new work will process (may be negative).</param>
    public void AddTotal(long bytes)
    {
        var total = Interlocked.Add(ref _totalBytes, bytes);
        var done = Interlocked.Read(ref _doneBytes);
        if (total < done)
            Interlocked.Exchange(ref _totalBytes, done);
    }

    /// <summary>Reports progress within the current phase (absolute bytes within that phase).</summary>
    /// <param name="bytes">Bytes processed so far in the current phase.</param>
    public void SetPhaseProgress(long bytes) => Interlocked.Exchange(ref _phaseBytes, Math.Max(0, bytes));

    /// <summary>Marks the current phase as finished and books its bytes as done.</summary>
    /// <param name="bytes">The full byte size of the finished phase.</param>
    public void CompletePhase(long bytes)
    {
        Interlocked.Add(ref _doneBytes, Math.Max(0, bytes));
        Interlocked.Exchange(ref _phaseBytes, 0);
    }

    /// <summary>Current completion as a fraction between 0 and 1.</summary>
    public double Fraction
    {
        get
        {
            var total = Interlocked.Read(ref _totalBytes);
            if (total <= 0)
                return 0;
            var done = Interlocked.Read(ref _doneBytes) + Interlocked.Read(ref _phaseBytes);
            return Math.Clamp((double)done / total, 0, 1);
        }
    }
}

/// <summary>
/// Renders a single-line console progress bar that is periodically refreshed and finally
/// replaced by a one-line summary. Degrades gracefully when output is redirected.
/// </summary>
public sealed class ProgressRenderer : IDisposable
{
    private readonly bool _interactive;
    private readonly Timer? _timer;
    private WorkTracker? _tracker;
    private string _label = "";
    private int _lastLineLength;
    private readonly Lock _lock = new();

    /// <summary>Creates the renderer; when the console is redirected no bar is drawn.</summary>
    public ProgressRenderer()
    {
        _interactive = !Console.IsOutputRedirected;
        if (_interactive)
            _timer = new Timer(_ => Render(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Starts displaying progress for one file.</summary>
    /// <param name="label">Short label shown behind the bar, typically the file name.</param>
    /// <param name="tracker">The work tracker to visualize.</param>
    public void Start(string label, WorkTracker tracker)
    {
        lock (_lock)
        {
            _label = label;
            _tracker = tracker;
        }
        _timer?.Change(0, 250);
    }

    /// <summary>
    /// Stops the progress bar and replaces it with the final per-file summary line.
    /// </summary>
    /// <param name="summary">Summary text describing what was (not) done.</param>
    public void FinishWithSummary(string summary)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        lock (_lock)
        {
            _tracker = null;
            ClearLine();
            Console.WriteLine(summary);
        }
    }

    /// <summary>Draws the current state of the progress bar.</summary>
    private void Render()
    {
        lock (_lock)
        {
            if (_tracker is not { } tracker)
                return;
            var fraction = tracker.Fraction;
            var percent = (int)Math.Floor(fraction * 100);
            if (percent >= 100)
                percent = 99; // 100 % is only reached when the summary replaces the bar

            const int barWidth = 24;
            var filled = (int)Math.Round(fraction * barWidth);
            var bar = new string('#', filled).PadRight(barWidth, '-');

            var line = $"[{bar}] {percent,3}% | {tracker.ChaptersFound} ch | {_label}";
            var max = SafeWindowWidth() - 1;
            if (max > 10 && line.Length > max)
                line = line[..max];

            var sb = new StringBuilder("\r").Append(line);
            if (line.Length < _lastLineLength)
                sb.Append(new string(' ', _lastLineLength - line.Length));
            Console.Write(sb.ToString());
            _lastLineLength = line.Length;
        }
    }

    /// <summary>Erases the progress bar line, leaving the cursor at column 0.</summary>
    private void ClearLine()
    {
        if (!_interactive || _lastLineLength == 0)
            return;
        Console.Write('\r' + new string(' ', _lastLineLength) + '\r');
        _lastLineLength = 0;
    }

    /// <summary>Returns the console width, tolerating consoles that do not report one.</summary>
    private static int SafeWindowWidth()
    {
        try { return Console.WindowWidth; } catch { return 120; }
    }

    /// <summary>Stops the refresh timer.</summary>
    public void Dispose() => _timer?.Dispose();
}
