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

    /// <summary>How many named marks of every kind - including the chapter announcements that
    /// <c>--ignore-chapter-numbers</c> files here rather than under a number - have been found so
    /// far. Shown as "mk N" in place of the chapter number in that mode alone, where the slot would
    /// otherwise sit at "----" from start to finish however much the file is yielding.</summary>
    public int NamedMarks { get; set; }

    /// <summary>How many of <see cref="NamedMarks"/> are extra marks rather than chapters
    /// (prologue, epilogue, <c>--custom</c>); rendered as "(+N)" after the chapter number. The
    /// intro mark is not among them: it is synthesized at write time, not detected.</summary>
    public int ExtraMarks { get; set; }

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
/// Renders the progress bar of the file currently being processed - one line, periodically
/// refreshed and truncated to the terminal's width (re-checked on every redraw, so a resize is
/// picked up automatically). When the file is finished the bar is replaced by a one-line summary
/// that scrolls up normally. Degrades gracefully when output is redirected.
/// </summary>
/// <remarks>
/// One bar, because the run processes one file at a time (see
/// <see cref="ABChapterize.Processing.FileProcessor"/>). Log lines and summaries interleave with the
/// bar by erasing it first and letting the next timer tick redraw it below - which is the only
/// reason this class needs to track what is currently on screen at all.
/// </remarks>
public sealed class ProgressRenderer : IDisposable
{
    /// <summary>
    /// The progress bar's colors. Kept restrained on purpose: the bar is on screen for hours at a
    /// stretch, so structure (brackets, separators) recedes into dark grey and the informational
    /// sections get one muted color each, purely so the eye can jump straight to the one it wants.
    /// Only two things are allowed to stand out - the bar fill and the file name - and only one is
    /// warm, the count of chapters still missing.
    /// </summary>
    private static class Palette
    {
        /// <summary>Brackets and the "|" separators - shape, not information.</summary>
        public const ConsoleColor Structure = ConsoleColor.DarkGray;

        /// <summary>The bar fill, sharing the file name's white: together they are the line's
        /// two "what and how far" anchors, and everything else is detail hung off them.</summary>
        public const ConsoleColor Bar = ConsoleColor.White;

        /// <summary>The phase label ("Pass 2"), and "Muxing..." standing in for the chapter count.</summary>
        public const ConsoleColor Phase = ConsoleColor.DarkCyan;

        /// <summary>The two numbers that only ever count upward, percentage and timer.</summary>
        public const ConsoleColor Progress = ConsoleColor.Cyan;

        /// <summary>The chapter/mark count - the run's actual yield.</summary>
        public const ConsoleColor Chapters = ConsoleColor.DarkGreen;

        /// <summary>The count of extra marks found alongside the chapters, sharing
        /// <see cref="Chapters"/>' green because it is the same kind of news: yield, not a
        /// problem. Only the sign tells the two bracketed counts apart at a glance.</summary>
        public const ConsoleColor Extras = ConsoleColor.DarkGreen;

        /// <summary>The chapter placeholder before anything has been found, deliberately as
        /// muted as the separators: there is nothing to read there yet.</summary>
        public const ConsoleColor NoChapters = ConsoleColor.DarkGray;

        /// <summary>The count of chapters still missing below the highest one found - the only
        /// part of the line reporting something outstanding, so the only warm color in it.</summary>
        public const ConsoleColor Missing = ConsoleColor.DarkRed;

        /// <summary>The file name, sharing <see cref="Bar"/>'s white.</summary>
        public const ConsoleColor Label = ConsoleColor.White;
    }

    /// <summary>The " | " between two sections of a bar line.</summary>
    private static readonly ColoredSpan Separator = new(" | ", Palette.Structure);

    private readonly bool _interactive;
    private bool _color;
    private readonly bool _quiet;
    private readonly bool _logToConsole;
    private readonly LogFile? _logFile;
    private readonly bool _logStyle;
    private readonly Timer? _timer;

    /// <summary>The file currently being processed and its label, or null between files.</summary>
    private (WorkTracker Tracker, string Label)? _slot;

    /// <summary>Whether a bar is currently on screen and therefore has to be erased before
    /// anything else is written.</summary>
    private bool _barDrawn;

    /// <summary>The exact line last drawn on screen, so a timer tick that would redraw an identical
    /// bar can be skipped entirely (see <see cref="Render"/>). Null whenever no bar is currently
    /// drawn - including right after <see cref="ClearBar"/> erased it.</summary>
    private string? _lastLine;
    private readonly Lock _lock = new();

    /// <summary>Creates the renderer; when the console is redirected no bar is drawn.</summary>
    /// <param name="quiet">Suppress the bar and non-important summary lines (--quiet).</param>
    /// <param name="verbose">Print <see cref="Log"/> messages as timestamped log lines (--verbose).</param>
    /// <param name="noBar">Suppress the progress bar; summary lines use the log format (--no-bar).</param>
    /// <param name="logFile">Opened <c>--log-file</c> destination, or null. It takes the log stream
    /// over entirely: with a log file the console shows its bar and summaries and nothing else,
    /// which is the point of asking for one.</param>
    /// <param name="color">Whether output is colorized (--color). This reaches the progress bar
    /// and the closing --summary block; log lines, per-file summaries and the run banner stay
    /// plain, and a --log-file always receives plain text whatever the console gets.</param>
    public ProgressRenderer(bool quiet, bool verbose = false, bool noBar = false, LogFile? logFile = null,
        ColorMode color = ColorMode.Auto)
    {
        _quiet = quiet;
        _logFile = logFile;
        _logToConsole = verbose && logFile == null;
        // Wherever the log lines end up, the per-file summaries join them there and use the same
        // timestamped format. On the console that leaves --no-bar as the remaining reason to
        // switch formats: without a bar to replace, a summary is just another line of log.
        _logStyle = _logToConsole || noBar;
        _interactive = !quiet && !noBar && !Console.IsOutputRedirected;
        // Not gated on _interactive: --quiet and --no-bar suppress the bar but still print the
        // closing --summary block, and neither of them means "and no color either" - that is what
        // --color never is for. A redirected console is already excluded by ShouldColorize.
        _color = ConsoleColors.ShouldColorize(color);
        if (_interactive)
        {
            // Hide the cursor for the whole interactive run: the bar is erased and redrawn every
            // timer tick, and a visible cursor would flicker between the start of the bar's line
            // (where ClearBar parks it) and the empty line below it (where the final WriteLine
            // leaves it) on every redraw. Restored in Dispose.
            TrySetCursorVisible(false);
            _timer = new Timer(_ => Render(), null, Timeout.Infinite, Timeout.Infinite);
        }
    }

    /// <summary>Starts displaying progress for a file.</summary>
    /// <param name="label">Short label shown behind the bar, typically the file name.</param>
    /// <param name="tracker">The work tracker to visualize.</param>
    public void Start(string label, WorkTracker tracker)
    {
        lock (_lock)
            _slot = (tracker, label);
        _timer?.Change(0, 250);
    }

    /// <summary>
    /// Stops displaying progress for a file and replaces its bar with a final summary line. In quiet
    /// mode the line is only printed when it is marked important.
    /// </summary>
    /// <param name="tracker">The same tracker instance passed to <see cref="Start"/> for this file.</param>
    /// <param name="summary">Summary text describing what was (not) done.</param>
    /// <param name="important">True for warnings/errors that must show even with --quiet.</param>
    public void FinishWithSummary(WorkTracker tracker, string summary, bool important = false)
    {
        lock (_lock)
        {
            if (_slot is { } slot && ReferenceEquals(slot.Tracker, tracker))
                _slot = null;
            ClearBar();
            // A summary is the one line worth having in both places: --quiet may hold it back from
            // the console, but a log file exists to be complete.
            _logFile?.Write(summary);
            if (!_quiet || important)
                Console.WriteLine(_logStyle ? FormatLog(summary) : summary);
            if (_slot == null)
                _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Records a log line, on the console (--verbose) or in the --log-file. On the console the
    /// progress bar is erased first (under the same lock the bar renderer uses) so it is never left
    /// behind above the log output; the next timer tick simply redraws it below. No-op when neither
    /// destination is active.
    /// </summary>
    /// <param name="message">The log message (without timestamp).</param>
    public void Log(string message)
    {
        lock (_lock)
        {
            _logFile?.Write(message);
            if (!_logToConsole)
                return;
            ClearBar();
            Console.WriteLine(FormatLog(message));
        }
    }

    /// <summary>
    /// Prints a line that belongs to the run rather than to any one file - the model banner, the
    /// closing --summary block - and mirrors it into the --log-file. Unlike a per-file summary
    /// this is never suppressed by --quiet: every caller already decides for itself whether the
    /// line is wanted at all.
    /// </summary>
    /// <param name="line">The line to print.</param>
    public void Announce(string line) => WriteAnnouncement(line, highlight: false);

    /// <summary>
    /// Prints one line of the closing <c>--summary</c> block, colorized by
    /// <see cref="SummaryHighlighter"/>. Same as <see cref="Announce(string)"/> in every other
    /// respect, including that the <c>--log-file</c> copy stays plain text.
    /// </summary>
    /// <param name="line">The finished summary line.</param>
    public void AnnounceSummary(string line) => WriteAnnouncement(line, highlight: true);

    /// <summary>The shared body of <see cref="Announce"/> and <see cref="AnnounceSummary"/>.
    /// Not an overload of either: a <c>cref</c> to an overloaded <c>Announce</c> would no longer
    /// resolve to one method, which the documentation build reports as CS0419.</summary>
    /// <param name="line">The line to print.</param>
    /// <param name="highlight">Whether to colorize the line as a summary line.</param>
    private void WriteAnnouncement(string line, bool highlight)
    {
        lock (_lock)
        {
            _logFile?.Write(line);
            ClearBar();
            if (highlight)
                WriteSpans(SummaryHighlighter.Highlight(line));
            else
                Console.WriteLine(line);
        }
    }

    /// <summary>Formats a message as a timestamped log line.</summary>
    /// <param name="message">The message to prefix.</param>
    private static string FormatLog(string message) => $"[{DateTime.Now:HH:mm:ss}] {message}";

    /// <summary>
    /// Redraws the active file's progress bar - but only when it actually differs from what is
    /// already on screen. The visible line is quantized (integer percent, a fixed-width bar, a
    /// chapter count), so it changes far less often than the timer ticks; skipping the
    /// erase-and-redraw for an unchanged line keeps the bar from flickering on every tick while
    /// nothing is moving.
    /// </summary>
    private void Render()
    {
        lock (_lock)
        {
            if (_slot is not { } slot)
                return;

            var width = SafeWindowWidth() - 1;
            var spans = BuildSpans(slot);
            var line = ConsoleColors.PlainText(spans);
            if (width > 10 && line.Length > width)
            {
                spans = ConsoleColors.Truncate(spans, width);
                line = line[..width];
            }

            // Nothing to do when the identical line is already drawn. The comparison runs on the
            // plain text, which is the whole reason colors are applied at write time: it stays a
            // comparison of what the user actually sees. When the bar was erased by an interleaved
            // log/summary line, _barDrawn is false, so this never wrongly skips the redraw needed
            // to put the bar back.
            if (_barDrawn && line == _lastLine)
                return;

            ClearBar();
            WriteSpans(spans);
            _barDrawn = true;
            _lastLine = line;
        }
    }

    /// <summary>
    /// Builds one progress bar line for a single active file as the plain text it renders as.
    /// Internal for unit testing: the per-tick redraw is skipped only when this exact string is
    /// unchanged (see <see cref="Render"/>), so the tests assert that the percent number and
    /// chapter display both take part in the string and therefore always trigger a redraw when
    /// they change.
    /// </summary>
    internal static string BuildLine((WorkTracker Tracker, string Label) slot)
        => ConsoleColors.PlainText(BuildSpans(slot));

    /// <summary>
    /// Builds one progress bar line as its colored sections. Internal for unit testing, which
    /// guards which section gets which color.
    /// </summary>
    /// <param name="slot">The tracker and label of the file to draw a line for.</param>
    internal static List<ColoredSpan> BuildSpans((WorkTracker Tracker, string Label) slot)
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
        var muxing = slot.Tracker.PhaseLabel == "Muxing";

        var spans = new List<ColoredSpan>(14)
        {
            new("[", Palette.Structure),
            new(bar, Palette.Bar),
            new("]", Palette.Structure),
            new($" {percent,3}%", Palette.Progress),
        };
        if (!muxing && slot.Tracker.PhaseLabel is { Length: > 0 } phaseLabel)
        {
            spans.Add(Separator);
            spans.Add(new(phaseLabel, Palette.Phase));
        }
        spans.Add(Separator);
        if (muxing)
            spans.Add(new("Muxing...", Palette.Phase));
        else
            AddChapterSpans(spans, slot.Tracker);
        spans.Add(Separator);
        spans.Add(new(timer, Palette.Progress));
        spans.Add(Separator);
        spans.Add(new(slot.Label, Palette.Label));
        return spans;
    }

    /// <summary>
    /// Appends the bar's chapter section: "----" until anything at all is found (nothing can
    /// change during Pass 1 anyway); then the highest detected chapter number, followed by one
    /// bracket holding the count of still-missing earlier chapters - the ones Pass 3 would have to
    /// chase - and the count of extra marks found, as e.g. "ch 6(-2+1)". Each count is split off
    /// into its own span so the numbers alone carry their colors while the brackets stay
    /// structural. An extra mark found before the first chapter shows as "ch 0(+1)": the zero is
    /// worth printing there because the bracket next to it is not empty.
    /// </summary>
    /// <param name="spans">The line being built, appended to in place.</param>
    /// <param name="tracker">The file's work tracker.</param>
    private static void AddChapterSpans(List<ColoredSpan> spans, WorkTracker tracker)
    {
        var highest = tracker.HighestChapter;
        var missing = tracker.MissingChapters;
        var extra = tracker.ExtraMarks;

        // --ignore-chapter-numbers is the one mode where chapters land among the named marks, so
        // it is the one mode where the extra count alone would understate the yield by everything
        // the run is actually finding - it keeps the plain total instead.
        if (highest == 0 && tracker.NamedMarks > extra)
        {
            spans.Add(new($"mk {tracker.NamedMarks}", Palette.Chapters));
            return;
        }
        if (highest == 0 && extra == 0)
        {
            spans.Add(new("----", Palette.NoChapters));
            return;
        }

        spans.Add(new($"ch {highest}", Palette.Chapters));
        if (missing == 0 && extra == 0)
            return;
        spans.Add(new("(", Palette.Structure));
        if (missing > 0)
            spans.Add(new($"-{missing}", Palette.Missing));
        if (extra > 0)
            spans.Add(new($"+{extra}", Palette.Extras));
        spans.Add(new(")", Palette.Structure));
    }

    /// <summary>Writes one line of spans, colorized when <see cref="_color"/> allows it and as the
    /// plain text they render as otherwise.</summary>
    /// <param name="spans">The line's colored sections.</param>
    private void WriteSpans(IReadOnlyList<ColoredSpan> spans)
    {
        if (_color)
            WriteColored(spans);
        else
            Console.WriteLine(ConsoleColors.PlainText(spans));
    }

    /// <summary>
    /// Writes one line span by span, restoring the default color after each. A console that
    /// refuses a color change mid-line leaves that one line garbled and switches colors off from
    /// then on, so it does not keep happening; for a bar line that also self-heals, since the
    /// whole block is erased and rewritten on the next redraw.
    /// </summary>
    /// <param name="spans">The line's colored sections.</param>
    private void WriteColored(IReadOnlyList<ColoredSpan> spans)
    {
        try
        {
            foreach (var span in spans)
            {
                if (span.Color is { } color)
                {
                    Console.ForegroundColor = color;
                    Console.Write(span.Text);
                    Console.ResetColor();
                }
                else
                {
                    Console.Write(span.Text);
                }
            }
        }
        catch
        {
            _color = false;
        }
        Console.WriteLine();
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
    /// Erases the currently drawn bar (if any), leaving the cursor on its line ready for the next
    /// redraw or for an interleaved log/summary line. Also drops the <see cref="_lastLine"/> cache
    /// so <see cref="Render"/> treats the bar as gone and redraws it, rather than skipping on a
    /// stale content match.
    /// </summary>
    private void ClearBar()
    {
        if (!_interactive || !_barDrawn)
            return;
        var width = SafeWindowWidth() - 1;
        Console.SetCursorPosition(0, Math.Max(0, Console.CursorTop - 1));
        Console.WriteLine(new string(' ', Math.Max(0, width)));
        Console.SetCursorPosition(0, Math.Max(0, Console.CursorTop - 1));
        _barDrawn = false;
        _lastLine = null;
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

    /// <summary>Stops the refresh timer and restores the cursor hidden for the interactive run.
    /// The color reset is a safety net for a run torn down mid-line (Ctrl+C during a redraw), so
    /// the shell prompt never inherits a bar section's color.</summary>
    public void Dispose()
    {
        _timer?.Dispose();
        if (_interactive)
            TrySetCursorVisible(true);
        if (_color)
            try { Console.ResetColor(); } catch { /* cosmetic only */ }
    }
}
