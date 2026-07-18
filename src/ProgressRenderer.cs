using System.Text;

namespace Chapterize;

/// <summary>
/// Tracks the byte-based work of the current processing phase of one file. Each phase
/// (e.g. silence scan, probing) has its own bar running from 0 to 100 %. Work is measured
/// in processed bytes (file size for full passes, proportional amounts for partial decodes),
/// never in play time. Thread-safe enough for the single-producer/single-renderer usage
/// in this tool.
/// </summary>
public sealed class WorkTracker
{
    private long _phaseTotalBytes;
    private long _phaseDoneBytes;
    private long _phaseCurrentBytes;

    /// <summary>Short name of the current phase (e.g. "Pass 1"); shown directly after the bar.</summary>
    public string PhaseLabel { get; private set; } = "";

    /// <summary>Number of chapters discovered so far; shown next to the progress bar.</summary>
    public int ChaptersFound { get; set; }

    /// <summary>Starts a new phase: resets the bar to 0 % and sets its label and total work.</summary>
    /// <param name="label">Phase name shown after the bar.</param>
    /// <param name="totalBytes">Total number of bytes this phase will process.</param>
    public void BeginPhase(string label, long totalBytes)
    {
        PhaseLabel = label;
        Interlocked.Exchange(ref _phaseTotalBytes, Math.Max(0, totalBytes));
        Interlocked.Exchange(ref _phaseDoneBytes, 0);
        Interlocked.Exchange(ref _phaseCurrentBytes, 0);
    }

    /// <summary>Reports transient progress of the work item currently running within the phase.</summary>
    /// <param name="bytes">Bytes processed so far by the current work item.</param>
    public void SetPhaseProgress(long bytes) => Interlocked.Exchange(ref _phaseCurrentBytes, Math.Max(0, bytes));

    /// <summary>Books finished work within the current phase and clears the transient progress.</summary>
    /// <param name="bytes">The full byte size of the finished work item.</param>
    public void Advance(long bytes)
    {
        Interlocked.Add(ref _phaseDoneBytes, Math.Max(0, bytes));
        Interlocked.Exchange(ref _phaseCurrentBytes, 0);
    }

    /// <summary>Completion of the current phase as a fraction between 0 and 1.</summary>
    public double Fraction
    {
        get
        {
            var total = Interlocked.Read(ref _phaseTotalBytes);
            if (total <= 0)
                return 0;
            var done = Interlocked.Read(ref _phaseDoneBytes) + Interlocked.Read(ref _phaseCurrentBytes);
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
    private readonly bool _quiet;
    private readonly Timer? _timer;
    private WorkTracker? _tracker;
    private string _label = "";
    private int _lastLineLength;
    private readonly Lock _lock = new();

    /// <summary>Creates the renderer; when the console is redirected no bar is drawn.</summary>
    /// <param name="quiet">Suppress the bar and non-important summary lines (--quiet).</param>
    public ProgressRenderer(bool quiet)
    {
        _quiet = quiet;
        _interactive = !quiet && !Console.IsOutputRedirected;
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
    /// In quiet mode the line is only printed when it is marked important.
    /// </summary>
    /// <param name="summary">Summary text describing what was (not) done.</param>
    /// <param name="important">True for warnings/errors that must show even with --quiet.</param>
    public void FinishWithSummary(string summary, bool important = false)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        lock (_lock)
        {
            _tracker = null;
            ClearLine();
            if (!_quiet || important)
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

            const int barWidth = 24;
            var filled = (int)Math.Round(fraction * barWidth);
            var bar = new string('#', filled).PadRight(barWidth, '-');

            var phase = tracker.PhaseLabel is { Length: > 0 } label ? $" {label}" : "";
            var line = $"[{bar}]{phase} {percent,3}% | {tracker.ChaptersFound} ch | {_label}";
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
