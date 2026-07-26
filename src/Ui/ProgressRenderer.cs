// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Diagnostics;

namespace ABChapterize.Ui;

/// <summary>
/// Tracks the progress of the current processing phase of one file. Each phase (e.g. silence
/// scan, probing) has its own bar running from 0 to 100 %. The unit of work is chosen per phase:
/// usually processed bytes (file size for full passes, or a play-time position rescaled to the
/// same byte unit via the file's bytes-per-second rate for windowed passes), but a plain item
/// count for phases with no continuous audio position (e.g. --verify's per-chapter checks).
/// Safe to update from one file's worker while the renderer's timer thread reads it
/// concurrently for redraws.
/// </summary>
public sealed class WorkTracker
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private long _phaseTotalBytes;
    private long _phaseDoneBytes;
    private long _phaseCurrentBytes;

    /// <summary>Wall-clock time since this tracker was constructed, i.e. since this file's
    /// processing began - shown as a coarse timer in front of the file name in the progress bar
    /// (see <see cref="ProgressRenderer.FormatElapsedTimer"/>).</summary>
    public TimeSpan Elapsed => _stopwatch.Elapsed;

    /// <summary>Short name of the current phase (e.g. "Pass 1"); shown directly after the bar.</summary>
    public string PhaseLabel { get; private set; } = "";

    /// <summary>Highest chapter number detected so far; 0 while none has been found yet
    /// (rendered as "----", since a zero count carries no information during e.g. Pass 1).</summary>
    public int HighestChapter { get; set; }

    /// <summary>How many chapter numbers below <see cref="HighestChapter"/> are still
    /// undetected (gaps that Pass 3 will chase); rendered as "(-N)" after the chapter number.</summary>
    public int MissingChapters { get; set; }

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
/// Renders one progress bar line per file currently being processed, periodically refreshed
/// and capped to the terminal's height (re-checked on every redraw, so a terminal resize is
/// picked up automatically). Each finished file's bar is replaced by a one-line summary that
/// scrolls up normally, while the remaining active bars keep redrawing below it. With a
/// single file in flight (the common case without --jobs > 1) this degenerates to exactly
/// the single-line behavior of earlier versions. Degrades gracefully when output is redirected.
/// </summary>
public sealed class ProgressRenderer : IDisposable
{
    private readonly bool _interactive;
    private readonly bool _quiet;
    private readonly bool _verbose;
    private readonly bool _logStyle;
    private readonly Timer? _timer;
    private readonly List<(WorkTracker Tracker, string Label)> _slots = [];
    private int _blockLineCount;
    /// <summary>The exact lines last drawn on screen, so a timer tick that would redraw an
    /// identical block can be skipped entirely (see <see cref="Render"/>). Empty whenever no
    /// block is currently drawn - including right after <see cref="ClearBlock"/> erased it.</summary>
    private List<string> _lastLines = [];
    private readonly Lock _lock = new();

    /// <summary>Creates the renderer; when the console is redirected no bar is drawn.</summary>
    /// <param name="quiet">Suppress the bar and non-important summary lines (--quiet).</param>
    /// <param name="verbose">Print <see cref="Log"/> messages as timestamped log lines (--verbose).</param>
    /// <param name="noBar">Suppress the progress bar; summary lines use the log format (--no-bar).</param>
    public ProgressRenderer(bool quiet, bool verbose = false, bool noBar = false)
    {
        _quiet = quiet;
        _verbose = verbose;
        // In both verbose and no-bar mode the per-file summaries become part of the log
        // stream, so they use the same timestamped format (and appear exactly once).
        _logStyle = verbose || noBar;
        _interactive = !quiet && !noBar && !Console.IsOutputRedirected;
        if (_interactive)
        {
            // Hide the cursor for the whole interactive run: the block is erased and redrawn
            // every timer tick, and a visible cursor would flicker between the top of the bar
            // block (where ClearBlock parks it) and the empty line below the last bar (where the
            // final WriteLine leaves it) on every redraw. Restored in Dispose.
            TrySetCursorVisible(false);
            _timer = new Timer(_ => Render(), null, Timeout.Infinite, Timeout.Infinite);
        }
    }

    /// <summary>Starts displaying progress for one file, in addition to any already in flight.</summary>
    /// <param name="label">Short label shown behind the bar, typically the file name.</param>
    /// <param name="tracker">The work tracker to visualize.</param>
    public void Start(string label, WorkTracker tracker)
    {
        lock (_lock)
            _slots.Add((tracker, label));
        _timer?.Change(0, 250);
    }

    /// <summary>
    /// Stops displaying progress for one file and replaces its bar with a final summary
    /// line. Any other files' bars keep redrawing below it. In quiet mode the line is only
    /// printed when it is marked important.
    /// </summary>
    /// <param name="tracker">The same tracker instance passed to <see cref="Start"/> for this file.</param>
    /// <param name="summary">Summary text describing what was (not) done.</param>
    /// <param name="important">True for warnings/errors that must show even with --quiet.</param>
    public void FinishWithSummary(WorkTracker tracker, string summary, bool important = false)
    {
        lock (_lock)
        {
            _slots.RemoveAll(s => ReferenceEquals(s.Tracker, tracker));
            ClearBlock();
            if (!_quiet || important)
                Console.WriteLine(_logStyle ? FormatLog(summary) : summary);
            if (_slots.Count == 0)
                _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Prints a --verbose log line. All active progress bars are erased first (under the
    /// same lock the bar renderer uses) so they are never left behind above the log output;
    /// the next timer tick simply redraws them below. No-op unless --verbose is active.
    /// </summary>
    /// <param name="message">The log message (without timestamp).</param>
    public void Log(string message)
    {
        if (!_verbose)
            return;
        lock (_lock)
        {
            ClearBlock();
            Console.WriteLine(FormatLog(message));
        }
    }

    /// <summary>Formats a message as a timestamped log line.</summary>
    /// <param name="message">The message to prefix.</param>
    private static string FormatLog(string message) => $"[{DateTime.Now:HH:mm:ss}] {message}";

    /// <summary>
    /// Redraws every active file's progress bar, capped to the terminal height - but only when
    /// the resulting block actually differs from what is already on screen. Each visible line is
    /// quantized (integer percent, a fixed-width bar, a chapter count), so it changes far less
    /// often than the timer ticks; skipping the erase-and-redraw for an unchanged block keeps the
    /// bar from flickering on every tick while nothing is moving.
    /// </summary>
    private void Render()
    {
        lock (_lock)
        {
            if (_slots.Count == 0)
                return;

            var maxRows = Math.Max(1, SafeWindowHeight() - 1);
            var rows = Math.Min(_slots.Count, maxRows);
            var width = SafeWindowWidth() - 1;
            var lines = new List<string>(rows);
            for (var i = 0; i < rows; i++)
            {
                var line = BuildLine(_slots[i]);
                if (width > 10 && line.Length > width)
                    line = line[..width];
                lines.Add(line);
            }

            // Nothing to do when the identical block is already drawn. When the block was erased
            // by an interleaved log/summary line, _blockLineCount is 0, so this never wrongly
            // skips the redraw needed to put the bar back.
            if (_blockLineCount > 0 && lines.SequenceEqual(_lastLines))
                return;

            ClearBlock();
            foreach (var line in lines)
                Console.WriteLine(line);
            _blockLineCount = rows;
            _lastLines = lines;
        }
    }

    /// <summary>
    /// Builds one progress bar line for a single active file. Internal for unit testing: the
    /// per-tick redraw is skipped only when this exact string is unchanged (see <see
    /// cref="Render"/>), so the tests assert that the percent number and chapter display both take
    /// part in the string and therefore always trigger a redraw when they change.
    /// </summary>
    internal static string BuildLine((WorkTracker Tracker, string Label) slot)
    {
        var fraction = slot.Tracker.Fraction;
        var percent = (int)Math.Floor(fraction * 100);

        const int barWidth = 24;
        var filled = (int)Math.Round(fraction * barWidth);
        var bar = new string('#', filled).PadRight(barWidth, '-');
        var timer = FormatElapsedTimer(slot.Tracker.Elapsed);

        // Muxing has no chapter count of its own to show (the chapters were already decided
        // by the time it runs) - it gets a plain "Muxing..." in the slot instead, with no
        // separate phase label after the bar since that would just repeat the same word.
        if (slot.Tracker.PhaseLabel == "Muxing")
            return $"[{bar}] {percent,3}% | Muxing... | {timer} | {slot.Label}";

        var phase = slot.Tracker.PhaseLabel is { Length: > 0 } phaseLabel ? $" {phaseLabel}" : "";
        // "----" until the first chapter is found (nothing can change during Pass 1 anyway);
        // then the highest detected chapter number, with the count of still-missing earlier
        // chapters - the ones Pass 3 would have to chase - as e.g. "ch 6(-2)".
        var chapters = slot.Tracker.HighestChapter is var highest and > 0
            ? $"ch {highest}" + (slot.Tracker.MissingChapters is var missing and > 0 ? $"(-{missing})" : "")
            : "----";
        return $"[{bar}]{phase} {percent,3}% | {chapters} | {timer} | {slot.Label}";
    }

    /// <summary>
    /// Formats how long a file has been processed, shown as its own "H:MM" section of the
    /// progress bar, ahead of the file name. Deliberately coarse - whole minutes only, no
    /// seconds - since a book takes many minutes to hours to process, so second-level precision
    /// would only add noise. Internal for unit testing.
    /// </summary>
    /// <param name="elapsed">Time since this file's processing began.</param>
    internal static string FormatElapsedTimer(TimeSpan elapsed)
    {
        var totalMinutes = (int)elapsed.TotalMinutes;
        return $"{totalMinutes / 60}:{totalMinutes % 60:00}";
    }

    /// <summary>
    /// Erases every line of the currently drawn block (if any), leaving the cursor at its
    /// top-left corner ready for the next redraw or for an interleaved log/summary line. Also
    /// drops the <see cref="_lastLines"/> cache so <see cref="Render"/> treats the block as gone
    /// and redraws it, rather than skipping on a stale content match.
    /// </summary>
    private void ClearBlock()
    {
        if (!_interactive || _blockLineCount == 0)
            return;
        var width = SafeWindowWidth() - 1;
        Console.SetCursorPosition(0, Math.Max(0, Console.CursorTop - _blockLineCount));
        for (var i = 0; i < _blockLineCount; i++)
            Console.WriteLine(new string(' ', Math.Max(0, width)));
        Console.SetCursorPosition(0, Math.Max(0, Console.CursorTop - _blockLineCount));
        _blockLineCount = 0;
        _lastLines = [];
    }

    /// <summary>
    /// Sets the console cursor visibility, swallowing any platform error. The
    /// <see cref="Console.CursorVisible"/> setter works on both Windows and Linux (it emits the
    /// ANSI show/hide sequence), but a redirected or dumb terminal can still throw; the cursor is
    /// purely cosmetic here, so a failure is ignored rather than aborting the run.
    /// </summary>
    /// <param name="visible">True to show the cursor, false to hide it.</param>
    private static void TrySetCursorVisible(bool visible)
    {
        try { Console.CursorVisible = visible; } catch { /* cosmetic only */ }
    }

    /// <summary>Returns the console width, tolerating consoles that do not report one.</summary>
    private static int SafeWindowWidth()
    {
        try { return Console.WindowWidth; } catch { return 120; }
    }

    /// <summary>Returns the console height, tolerating consoles that do not report one.</summary>
    private static int SafeWindowHeight()
    {
        try { return Console.WindowHeight; } catch { return 24; }
    }

    /// <summary>Stops the refresh timer and restores the cursor hidden for the interactive run.</summary>
    public void Dispose()
    {
        _timer?.Dispose();
        if (_interactive)
            TrySetCursorVisible(true);
    }
}
