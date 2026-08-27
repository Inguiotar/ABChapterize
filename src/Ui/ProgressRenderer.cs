// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Diagnostics;

namespace ABChapterize.Ui;

/// <summary>
/// Tracks the progress of the current processing phase of one file. Every phase's bar spans the
/// whole file, whatever fraction of it the phase reads: the unit is the file's size in bytes and
/// the progress is a play-time position rescaled to it via the file's bytes-per-second rate, so a
/// pass working a handful of gaps sits at the position of the gap it is on and marks the gaps out
/// with <see cref="PhaseSpans"/>. The one exception is a phase with no continuous audio position -
/// --verify's per-chapter checks - which counts items instead. Safe to update from one file's
/// worker while the renderer's timer thread reads it concurrently for redraws.
/// </summary>
public sealed class WorkTracker
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private string _phaseName = "";
    private long _phaseTotalBytes;
    private long _phaseDoneBytes;
    private long _phaseCurrentBytes;

    /// <summary>Wall-clock time since this tracker was constructed, i.e. since this file's
    /// processing began - shown as a coarse timer in front of the file name in the progress bar
    /// (see <see cref="ProgressRenderer.FormatElapsedTimer"/>).</summary>
    public TimeSpan Elapsed => _stopwatch.Elapsed;

    /// <summary>
    /// The current phase under its own name (one of <see cref="PhaseNames"/>' constants), which is
    /// what code compares against - <see cref="PhaseLabel"/> is the wording shown to the user and
    /// nothing should key on it.
    /// </summary>
    public string PhaseName => _phaseName;

    /// <summary>What the bar shows for the current phase: <see cref="PhaseName"/> as
    /// <see cref="PhaseNames.Display"/> spells it, with <see cref="RevisitSuffix"/> appended while
    /// <see cref="PhaseRevisiting"/> holds.</summary>
    public string PhaseLabel => PhaseNames.Display(_phaseName) + (PhaseRevisiting ? RevisitSuffix : "");

    /// <summary>What <see cref="PhaseLabel"/> gains while a phase is re-reading ground it has
    /// already covered - short and wordless, because the label sits beside a percentage that is
    /// itself running backwards, and the two say the same thing.</summary>
    public const string RevisitSuffix = " (<<)";

    /// <summary>
    /// Whether the current phase is presently re-reading audio it has already passed, which is
    /// what makes its percentage run backwards: Probe re-probing the candidates inside a sequence
    /// gap (<see cref="ABChapterize.Detection.RegionProber"/>).
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="BeginPhase"/> because a gap re-probe is not a phase of its own: it
    /// runs inside Probe, against Probe's own totals, and beginning a phase would reset the bar to
    /// zero and throw away everything the walk has covered so far. Cleared by
    /// <see cref="BeginPhase"/> so a flag left set by an abandoned re-probe cannot leak into the
    /// next phase's label.
    /// </remarks>
    public bool PhaseRevisiting { get; set; }

    /// <summary>
    /// How many locations the current phase has looked at, while it is one that does not work
    /// through the file in order - or null, which is every other phase and the ordinary case.
    /// <para>
    /// While it holds, the bar stops being a bar: there is no "how far along" to fill it with, only
    /// a position that jumps about the file, so it is drawn as a single marker at that position and
    /// this count takes the percentage's place. The one phase that sets it is the descending scan's
    /// skim (<see cref="ABChapterize.Detection.DescendingSilenceScan"/>), which reads a file's
    /// longest pauses first and so may be at 0:03 one moment and 8:12 the next. A filling bar there
    /// would be a lie in both directions - it would run backwards, and its percentage would say
    /// "nearly done" about a phase that has looked at a dozen places out of thousands.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Nullable rather than a flag plus a counter, so the two cannot disagree about whether the
    /// phase is exploring at all. Cleared by <see cref="BeginPhase"/> like
    /// <see cref="PhaseRevisiting"/>, so a value left behind by an abandoned skim cannot leak into
    /// the next phase's bar.
    /// </remarks>
    public int? LocationsExplored { get; set; }

    /// <summary>
    /// The stretch of the file that the pass is working right now - a sequence gap, a jingle-first
    /// stretch, the file's tail - or null while it is working the whole book, which is the ordinary
    /// case. Drawn as the brightest highlight inside the bar, so a fill that jumps or runs backwards
    /// can be read against the piece of the book it belongs to.
    /// </summary>
    /// <remarks>
    /// The primary whole-file walk deliberately leaves this null even though its region spans the
    /// bar: the highlight says "this is a piece of the book, not the book", and a bar tinted end to
    /// end from the first second of every run would say nothing at all. Cleared by
    /// <see cref="BeginPhase"/> like <see cref="PhaseRevisiting"/>, so a span left behind by an
    /// abandoned pass cannot leak into the next phase's bar.
    /// </remarks>
    public (long FromBytes, long ToBytes)? RegionSpan { get; set; }

    /// <summary>
    /// Every stretch of the file the current phase is going to work, in file order, or null for a
    /// phase that reads the book end to end. Drawn as a dimmer highlight than
    /// <see cref="RegionSpan"/>, which is always one of these: together they say "this pass covers
    /// these pieces, and it is on this one".
    /// </summary>
    /// <remarks>
    /// Every bar this tool draws is a map of the whole file, whatever fraction of it the phase
    /// actually reads, which is what this exists to make legible. The alternative - a bar whose
    /// total is the summed length of the phase's regions - runs a tidy 0 to 100 % and is what Scan,
    /// Re-probe, Re-scan and Probe's pause half all used to do, at the price of every bar in a run
    /// measuring a different timeline: 40 % of a Scan meant nothing about where in the book the
    /// reading head was, and the same position moved the bar differently from one phase to the next.
    /// Set by <see cref="BeginPhase"/> alone, so a phase's stretches and its total cannot be stated
    /// apart - and replaced wholesale rather than mutated, for the reason
    /// <see cref="HighestChapters"/> gives: the renderer reads it from its own timer thread, and a
    /// reference assignment is the one thing safe to do across the two without a lock.
    /// </remarks>
    public IReadOnlyList<(long FromBytes, long ToBytes)>? PhaseSpans { get; private set; }

    /// <summary>Where a position in the file's play time falls on the bar, in the progress bytes it
    /// is drawn in. Every conversion goes through this, so a phase's stretches and the positions
    /// reported inside them cannot arrive at slightly different arithmetic and leave a pass looking
    /// as though it had strayed outside its own gap.</summary>
    /// <param name="seconds">Absolute position in the file.</param>
    /// <param name="bytesPerSecond">The file's play time to progress-byte rate.</param>
    public static long Position(double seconds, double bytesPerSecond)
        => (long)(seconds * bytesPerSecond);

    /// <summary>Where a stretch of play time falls on the bar; see <see cref="Position"/>.</summary>
    /// <param name="fromSeconds">Absolute start of the stretch in the file.</param>
    /// <param name="toSeconds">Absolute end of the stretch in the file.</param>
    /// <param name="bytesPerSecond">The file's play time to progress-byte rate.</param>
    public static (long FromBytes, long ToBytes) Span(
        double fromSeconds, double toSeconds, double bytesPerSecond)
        => (Position(fromSeconds, bytesPerSecond), Position(toSeconds, bytesPerSecond));

    /// <summary>The work this phase was begun with, so a phase that turns into another one part way
    /// through can hand the same total on - see <see cref="LocationsExplored"/> for the one that
    /// does, the skim in front of Probe.</summary>
    public long PhaseTotalBytes => Interlocked.Read(ref _phaseTotalBytes);

    /// <summary>
    /// How far each of the file's chapter sequences has got: the highest number detected in
    /// each, one entry per part and in part order. Empty while nothing has been found yet
    /// (rendered as "----", since a zero carries no information during e.g. Analyze).
    /// </summary>
    /// <remarks>
    /// A list rather than a single number because a book whose numbering restarts has no single
    /// "how far in" to report - part 3's chapter 2 has not gone backwards from part 1's chapter
    /// 15. Replaced wholesale on every update, never mutated in place: the renderer reads it
    /// from its own timer thread while detection writes it, and a reference assignment is the
    /// one thing that is safe to do across the two without a lock.
    /// </remarks>
    public IReadOnlyList<int> HighestChapters { get; set; } = [];

    /// <summary>How many chapter numbers below the highs in <see cref="HighestChapters"/> are
    /// still undetected (gaps that Scan will chase); rendered as "(-N)" after the chapter
    /// numbers. One total across every part, not one per part.</summary>
    public int MissingChapters { get; set; }

    /// <summary>How many named marks of every kind - including the chapter announcements that
    /// <c>--ignore-chapter-numbers</c> files here rather than under a number - have been found so
    /// far. Shown as "mk N" in place of the chapter number in that mode alone, where the slot would
    /// otherwise sit at "----" from start to finish however much the file is yielding.
    /// <para>
    /// Under <c>--verify</c> it equals <see cref="ExtraMarks"/>, every named mark that path can
    /// confirm being an extra one, so the "mk N" form does not arise there.
    /// </para></summary>
    public int NamedMarks { get; set; }

    /// <summary>How many of <see cref="NamedMarks"/> are extra marks rather than chapters
    /// (prologue, epilogue, <c>--custom</c>); rendered as "(+N)" after the chapter number. The
    /// intro mark is not among them: it is synthesized at write time, not detected.
    /// <para>
    /// Under <c>--verify</c> it counts the named marks <em>confirmed</em> so far, one that fails
    /// leaving it alone rather than lowering it - see
    /// <see cref="ABChapterize.Detection.ChapterDetector"/>'s named-progress refresh for why the
    /// failure is not shown here at all.
    /// </para></summary>
    public int ExtraMarks { get; set; }

    /// <summary>Starts a new phase: resets the bar to 0 % and sets its label, total work and the
    /// stretches of the file it covers.</summary>
    /// <param name="label">Phase name shown after the bar.</param>
    /// <param name="totalBytes">Total number of bytes this phase will process - the whole file for
    /// every phase whose progress is a position in it.</param>
    /// <param name="spans">The stretches this phase will work (see <see cref="PhaseSpans"/>), or
    /// null for one that reads the book end to end. Stated here rather than assigned afterwards so
    /// that a phase abandoned part way through cannot leave its stretches highlighted under the
    /// next one.</param>
    public void BeginPhase(
        string label, long totalBytes, IReadOnlyList<(long FromBytes, long ToBytes)>? spans = null)
    {
        _phaseName = label;
        PhaseRevisiting = false;
        LocationsExplored = null;
        RegionSpan = null;
        PhaseSpans = spans;
        Interlocked.Exchange(ref _phaseTotalBytes, Math.Max(0, totalBytes));
        Interlocked.Exchange(ref _phaseDoneBytes, 0);
        Interlocked.Exchange(ref _phaseCurrentBytes, 0);
    }

    /// <summary>
    /// Renames the running phase without disturbing anything else about it - the bar keeps its
    /// total, its progress and both of its highlights.
    /// </summary>
    /// <param name="phase">The phase name to show from now on; one of <see cref="PhaseNames"/>'
    /// constants.</param>
    /// <remarks>
    /// For a step that runs inside another phase, against that phase's own totals, and is still
    /// worth naming: the sub-floor sweep (<see cref="PhaseNames.SubFloorProbe"/>) re-walks a gap it
    /// has already counted, exactly as a gap re-probe does, so beginning a phase for it would reset
    /// the bar and throw away everything the enclosing pass has covered. Whoever relabels restores
    /// the previous name afterwards; <see cref="BeginPhase"/> is the backstop that keeps a name left
    /// behind by an abandoned step out of the next phase.
    /// </remarks>
    public void Relabel(string phase) => _phaseName = phase;

    /// <summary>Reports transient progress of the work item currently running within the phase -
    /// for a position-based phase, which is every phase but <c>--verify</c>'s, the absolute
    /// position in the file the pass has reached.</summary>
    /// <param name="bytes">Bytes processed so far by the current work item.</param>
    public void SetPhaseProgress(long bytes) => Interlocked.Exchange(ref _phaseCurrentBytes, Math.Max(0, bytes));

    /// <summary>Records finished work within the current phase and clears the transient progress.
    /// Only a phase counting items rather than positions has anything to book here: a bar mapping
    /// the whole file states where the pass <em>is</em>, which no accumulator can be behind.</summary>
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
/// Renders the progress of the file currently being processed - two lines, the console-wide bar
/// with its percentage above and the phase, chapter state, timer and file name below, periodically
/// refreshed and fitted to the terminal's width (re-checked on every redraw, so a resize is
/// picked up automatically). When the file is finished the block is replaced by a one-line summary
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
    /// Three things are allowed to stand out - the bar fill, the stretch a pass is reading right
    /// now, and the file name - and only one is warm, the count of chapters still missing.
    /// </summary>
    private static class Palette
    {
        /// <summary>Brackets and the "|" separators - shape, not information.</summary>
        public const ConsoleColor Structure = ConsoleColor.DarkGray;

        /// <summary>The bar fill, sharing the file name's white: together they are the line's
        /// two "what and how far" anchors, and everything else is detail hung off them.</summary>
        public const ConsoleColor Bar = ConsoleColor.White;

        /// <summary>The phase label ("Probe"), and the finish label standing in for the chapter
    /// count.</summary>
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

        /// <summary>The stretches of the book a pass is going to work, where those are pieces of it
        /// rather than all of it (<see cref="WorkTracker.PhaseSpans"/>). Shares the phase label's
        /// darker cyan, which is the same statement made twice: the label says what the pass is
        /// doing, the highlight says where.</summary>
        public const ConsoleColor Planned = ConsoleColor.DarkCyan;

        /// <summary>The one stretch a pass is working right now
        /// (<see cref="WorkTracker.RegionSpan"/>), picked out of <see cref="Planned"/> by being the
        /// bright half of the same hue - the two are one statement at two levels of detail, so a
        /// second color would only make them read as unrelated. It shares
        /// <see cref="Progress"/>'s cyan, which does not confuse the two: one is inside the
        /// brackets and the other is the number after them.</summary>
        public const ConsoleColor Working = ConsoleColor.Cyan;

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

    /// <summary>The exact block last drawn on screen, its lines joined by newlines, so a timer tick
    /// that would redraw an identical bar can be skipped entirely (see <see cref="Render"/>). Null
    /// whenever no bar is currently drawn - including right after <see cref="ClearBar"/> erased
    /// it.</summary>
    private string? _lastLine;
    private readonly Lock _lock = new();

    /// <summary>Creates the renderer; when the console is redirected no bar is drawn.</summary>
    /// <param name="quiet">Suppress the bar and non-important summary lines (--quiet).</param>
    /// <param name="verbose">Print <see cref="Log"/> messages as timestamped log lines (--verbose).</param>
    /// <param name="noBar">Suppress the progress bar; summary lines use the log format (--no-bar).</param>
    /// <param name="logFile">Opened <c>--log-file</c> destination, or null. It takes the log stream
    /// over entirely: with a log file the console shows its bar and summaries and nothing else,
    /// which is the point of asking for one.</param>
    /// <param name="color">Whether output is colorized (--color). This reaches the progress bar, the
    /// file name at the front of a per-file result line, and the closing --summary block; log lines
    /// - which is what a result line becomes under --verbose or --no-bar - and the run banner stay
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
            string? name = null;
            if (_slot is { } slot && ReferenceEquals(slot.Tracker, tracker))
            {
                name = slot.Label;
                _slot = null;
            }
            ClearBar();
            // A summary is the one line worth having in both places: --quiet may hold it back from
            // the console, but a log file exists to be complete.
            _logFile?.Write(summary);
            if (!_quiet || important)
            {
                if (_logStyle)
                    Console.WriteLine(FormatLog(summary));
                else
                    WriteSpans(SummarySpans(summary, name));
            }
            if (_slot == null)
                _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    /// <summary>
    /// A finished file's result line, with the file name at the front picked out in the same white
    /// the bar gave it - the line scrolls away into a run's backlog, where the name is what somebody
    /// scanning it is looking for.
    /// </summary>
    /// <param name="summary">The finished summary line.</param>
    /// <param name="name">The file's name as the bar showed it, or null when the line belongs to no
    /// file the renderer was tracking.</param>
    /// <remarks>
    /// The name is matched against the front of the line rather than assumed to be there: every
    /// caller writes "<c>{name}: ...</c>" today, and a line that ever stops doing so falls back to
    /// plain text instead of colouring an arbitrary prefix. Matching the tracked label also settles
    /// what "the name ends here" means without a rule about colons, which a file name may itself
    /// contain.
    /// </remarks>
    private static List<ColoredSpan> SummarySpans(string summary, string? name)
        => name is { Length: > 0 } && summary.StartsWith(name + ":", StringComparison.Ordinal)
            ? [new(name, Palette.Label), new(summary[name.Length..], null)]
            : [new(summary, null)];

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

    /// <summary>
    /// Prints the line that opens a <c>--summary</c> block, and with it the one thing every mode's
    /// block has to be able to say: that the run was cut short and the figures under this line
    /// therefore cover only part of the work.
    /// </summary>
    /// <remarks>
    /// Here rather than at the three call sites so the wording exists once. A summary that appears
    /// only when a run reaches its end is a summary nobody gets on the runs that most need one -
    /// a batch of two hundred audiobooks stopped by Ctrl+C or by a server going away has still
    /// finished most of its files, and which those were is exactly what the next command line has
    /// to be built from.
    /// </remarks>
    /// <param name="counts">The heading's own text, everything after "Summary: ".</param>
    /// <param name="finished">False when the run did not get to the end of its work.</param>
    public void AnnounceSummaryHeading(string counts, bool finished)
        => AnnounceSummary(finished ? $"Summary: {counts}" : $"Summary ({UnfinishedRun}): {counts}");

    /// <summary>How the heading marks a run that was interrupted or failed part way through.</summary>
    private const string UnfinishedRun = "run did not finish";

    /// <summary>
    /// Prints one line of the closing <c>--summary</c> block that was assembled from pieces rather
    /// than from one string, so that the book titles in it are colored as titles instead of being
    /// pattern-matched like prose. Same as <see cref="AnnounceSummary"/> in every other respect.
    /// </summary>
    /// <param name="segments">The pieces of the finished summary line, in print order.</param>
    public void AnnounceSummarySegments(IReadOnlyList<SummarySegment> segments)
    {
        lock (_lock)
        {
            var spans = SummaryHighlighter.HighlightSegments(segments);
            _logFile?.Write(ConsoleColors.PlainText(spans));
            ClearBar();
            WriteSpans(spans);
        }
    }

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
    /// Redraws the active file's progress block - but only when it actually differs from what is
    /// already on screen. The visible block is quantized (integer percent, whole bar cells, a
    /// chapter count), so it changes far less often than the timer ticks; skipping the
    /// erase-and-redraw for an unchanged block keeps the bar from flickering on every tick while
    /// nothing is moving.
    /// </summary>
    private void Render()
    {
        lock (_lock)
        {
            if (_slot is not { } slot)
                return;

            var width = SafeWindowWidth() - 1;
            var block = BuildBlock(slot, width, _color);
            // Below a dozen columns nothing is truncated and the block is left to wrap, which
            // ClearBar - it erases exactly BlockLines lines - then cannot fully undo. Deliberate: a
            // bar cut to ten columns says nothing at all, and a terminal that narrow is not a case
            // worth carrying variable-height erase logic for.
            if (width > 10)
                block = [.. block.Select(l => ConsoleColors.PlainText(l).Length > width
                    ? ConsoleColors.Truncate(l, width)
                    : l)];
            var text = string.Join('\n', block.Select(ConsoleColors.PlainText));

            // Nothing to do when the identical block is already drawn. The comparison runs on the
            // plain text, which is the whole reason colors are applied at write time: it stays a
            // comparison of what the user actually sees. When the bar was erased by an interleaved
            // log/summary line, _barDrawn is false, so this never wrongly skips the redraw needed
            // to put the bar back.
            if (_barDrawn && text == _lastLine)
                return;

            ClearBar();
            foreach (var line in block)
                WriteSpans(line);
            _barDrawn = true;
            _lastLine = text;
        }
    }

    /// <summary>
    /// Builds the whole progress block for a single active file as the plain text it renders as,
    /// its lines joined by newlines. Internal for unit testing: the per-tick redraw is skipped only
    /// when this exact string is unchanged (see <see cref="Render"/>), so the tests assert that the
    /// percent number and chapter display both take part in it and therefore always trigger a
    /// redraw when they change.
    /// </summary>
    /// <param name="slot">The tracker and label of the file to draw.</param>
    /// <param name="width">The console width the bar is drawn to fill.</param>
    /// <param name="color">Whether the console this is drawn for renders color; see
    /// <see cref="AddBarSpans"/> for the one thing it changes about the characters.</param>
    internal static string BuildLine(
        (WorkTracker Tracker, string Label) slot, int width = DefaultWidth, bool color = true)
        => string.Join('\n', BuildBlock(slot, width, color).Select(ConsoleColors.PlainText));

    /// <summary>How many console lines one file's progress block occupies. Named rather than
    /// spelled as a literal because <see cref="ClearBar"/> has to erase exactly as many as
    /// <see cref="Render"/> wrote, and a mismatch leaves the terminal scrolling debris. Internal
    /// for the unit test that holds <see cref="BuildBlock"/> to exactly this many lines.</summary>
    internal const int BlockLines = 2;

    /// <summary>Everything the bar line spends on something other than bar cells: the space in
    /// front of it, its two brackets, the five columns of " 100%" and the space closing the
    /// line.</summary>
    private const int BarLineOverhead = 9;

    /// <summary>The width assumed where none is known - the tests, and a console that does not
    /// report one (see <see cref="SafeWindowWidth"/>).</summary>
    internal const int DefaultWidth = 120;

    /// <summary>
    /// Builds one file's progress block: the bar with its percentage on the first line, everything
    /// else on the second. Two lines rather than one because the bar is now drawn as wide as the
    /// console, which leaves no room beside it for the phase, the chapter state, the timer and a
    /// file name.
    /// </summary>
    /// <param name="slot">The tracker and label of the file to draw.</param>
    /// <param name="width">The console width the bar is drawn to fill.</param>
    /// <param name="color">Whether the console this is drawn for renders color; see
    /// <see cref="AddBarSpans"/> for the one thing it changes about the characters.</param>
    internal static List<List<ColoredSpan>> BuildBlock(
        (WorkTracker Tracker, string Label) slot, int width = DefaultWidth, bool color = true)
        => [BuildBarSpans(slot.Tracker, width, color), BuildStatusSpans(slot)];

    /// <summary>
    /// Builds the bar line as its colored sections: one space, the bracketed bar, the percentage,
    /// one space. Internal for unit testing, which guards which section gets which color.
    /// </summary>
    /// <param name="tracker">The file's work tracker.</param>
    /// <param name="width">The console width the line is drawn to fill.</param>
    /// <param name="color">Whether the console this is drawn for renders color; see
    /// <see cref="AddBarSpans"/> for the one thing it changes about the characters.</param>
    internal static List<ColoredSpan> BuildBarSpans(
        WorkTracker tracker, int width = DefaultWidth, bool color = true)
    {
        // Four cells is not a useful bar, it is the point below which the arithmetic would start
        // producing negative widths; a console this narrow is left with a line that overruns it, as
        // Render's own truncation note describes.
        var barWidth = Math.Max(4, width - BarLineOverhead);
        var fraction = tracker.Fraction;
        var filled = (int)Math.Round(fraction * barWidth);
        // Both the fill character and the percentage read off the same stretches, so a bar showing
        // work outside them and a percentage counting it are not expressible.
        var highlights = HighlightSpans(tracker);
        var position = fraction * tracker.PhaseTotalBytes;
        var percent = (int)Math.Floor(WorkFraction(highlights, position, fraction) * 100);
        // A phase that does not work through the file in order gets a position marker instead of a
        // fill, and a count of what it has looked at instead of a percentage - see
        // WorkTracker.LocationsExplored for why a bar would be a lie there.
        var explored = tracker.LocationsExplored;
        var bar = explored is null
            ? BuildFill(filled, barWidth, highlights, tracker.PhaseTotalBytes, position)
            : PositionMarker(filled, barWidth);

        var spans = new List<ColoredSpan>(7)
        {
            new(" ", null),
            new("[", Palette.Structure),
        };
        AddBarSpans(spans, bar, tracker, color);
        spans.Add(new("]", Palette.Structure));
        // Same width as " 100%", so a phase turning into an ordinary one does not shuffle the
        // bar's right edge by a column.
        spans.Add(new(explored is { } count ? $" {count,4}" : $" {percent,3}%", Palette.Progress));
        spans.Add(new(" ", null));
        return spans;
    }

    /// <summary>
    /// Builds the status line as its colored sections: the phase, the chapter state, the elapsed
    /// timer and the file name. Internal for unit testing.
    /// </summary>
    /// <param name="slot">The tracker and label of the file to draw a line for.</param>
    internal static List<ColoredSpan> BuildStatusSpans((WorkTracker Tracker, string Label) slot)
    {
        // The final write has no chapter count of its own to show - the chapters were all decided
        // by the time it runs, so a count there would be a number nothing can change any more.
        var finishing = slot.Tracker.PhaseName == PhaseNames.Finish;

        // Indented by the same single space the bar line opens with, so the two lines of the block
        // start on one column rather than the lower one hanging a step to the left of the bar.
        var spans = new List<ColoredSpan>(12) { new(" ", null) };
        if (slot.Tracker.PhaseLabel is { Length: > 0 } phaseLabel)
        {
            spans.Add(new(phaseLabel, Palette.Phase));
            spans.Add(Separator);
        }
        if (!finishing)
        {
            AddChapterSpans(spans, slot.Tracker);
            spans.Add(Separator);
        }
        spans.Add(new(FormatElapsedTimer(slot.Tracker.Elapsed), Palette.Progress));
        spans.Add(Separator);
        spans.Add(new(slot.Label, Palette.Label));
        return spans;
    }

    /// <summary>
    /// The stretches the bar marks out, merged and in file order, or empty for a phase that reads
    /// the book end to end and marks nothing. The union of both highlights, because both say "this
    /// is a piece of the book" - <see cref="WorkTracker.RegionSpan"/> is normally one of
    /// <see cref="WorkTracker.PhaseSpans"/>, and where a phase sets only the one it is still a
    /// piece.
    /// </summary>
    /// <param name="tracker">The file's work tracker.</param>
    /// <remarks>
    /// Merged rather than concatenated because <see cref="WorkFraction"/> divides by their summed
    /// length: two stretches counted twice would inflate the denominator and leave a phase that
    /// finished its work reading short of 100 %. The same merged list drives
    /// <see cref="BuildFill"/>, so the cells drawn as done and the percentage saying how many are
    /// done cannot disagree. Empty on a phase with no total, matching
    /// <see cref="AddBarSpans"/>'s own bail-out so the characters and the colors agree there too.
    /// </remarks>
    private static List<(long From, long To)> HighlightSpans(WorkTracker tracker)
    {
        if (tracker.PhaseTotalBytes <= 0)
            return [];
        var raw = new List<(long From, long To)>(tracker.PhaseSpans ?? []);
        if (tracker.RegionSpan is { } working)
            raw.Add(working);
        if (raw.Count == 0)
            return [];

        raw.Sort((a, b) => a.From.CompareTo(b.From));
        var merged = new List<(long From, long To)> { raw[0] };
        foreach (var span in raw.Skip(1))
        {
            var last = merged[^1];
            if (span.From <= last.To)
                merged[^1] = (last.From, Math.Max(last.To, span.To));
            else
                merged.Add(span);
        }
        return merged;
    }

    /// <summary>
    /// What the percentage states: how much of the work this phase actually has to do is behind the
    /// bar's fill. For a phase that reads the book end to end that is the fill itself; for one
    /// working a handful of gaps it is progress through those gaps, not through the file they sit
    /// in.
    /// </summary>
    /// <param name="highlights">The phase's stretches, from <see cref="HighlightSpans"/>.</param>
    /// <param name="position">Where the reading head is, in the progress bytes the bar is drawn in.</param>
    /// <param name="fraction">The whole-file fraction, returned as-is for a phase marking nothing.</param>
    /// <remarks>
    /// This does <em>not</em> reinstate the per-phase compressed timeline that build 411 removed,
    /// and the difference is the whole point of that change: the bar is still a map of the file, so
    /// the fill still says where in the book the reading head is. Only the number beside it changed
    /// meaning, from "how far into the file" - which on a phase reading two gaps out of nine hours
    /// was a figure about the file rather than about the work - to "how much of the work is done".
    /// The two answer different questions and are now both on screen.
    /// </remarks>
    private static double WorkFraction(
        List<(long From, long To)> highlights, double position, double fraction)
    {
        if (highlights.Count == 0)
            return fraction;
        double covered = 0, work = 0;
        foreach (var (from, to) in highlights)
        {
            // Defensively ordered: a zero-length or inverted stretch contributes nothing rather
            // than throwing, since a gap far too short to fill a cell is a case the bar already has.
            var end = Math.Max(from, to);
            work += end - from;
            covered += Math.Min(Math.Max(position, from), end) - from;
        }
        return work > 0 ? Math.Clamp(covered / work, 0, 1) : fraction;
    }

    /// <summary>
    /// The bar's cells for an ordinary filling bar: "#" for work done, "-" for everything else.
    /// </summary>
    /// <param name="filled">How many cells the reading head has passed.</param>
    /// <param name="barWidth">The bar's width in cells.</param>
    /// <param name="highlights">The phase's stretches, from <see cref="HighlightSpans"/>.</param>
    /// <param name="total">The phase's total, i.e. what the whole bar stands for.</param>
    /// <param name="position">Where the reading head is, in the progress bytes the bar is drawn in.</param>
    /// <remarks>
    /// <para>
    /// Where a phase marks out stretches, only cells inside one of them are ever drawn as done -
    /// the audio between two gaps is not work this phase did, it is audio it skipped, and filling
    /// it in claimed otherwise. The distinction is carried by the character and not only by the
    /// color, so it survives a terminal with no color, <c>--no-color</c>, and a log or screenshot
    /// where the color is gone: "[##----####---##--]" reads the same in black and white.
    /// </para>
    /// <para>
    /// A stretch the head has gone past is drawn done to its last cell rather than to wherever the
    /// fill rounded. <see cref="SpanCells"/> rounds a stretch <em>outwards</em> so that even a very
    /// short one shows, while the fill rounds to nearest, so the two can differ by a cell - and the
    /// place that shows is the end of a phase, where a stretch the percentage has already counted
    /// as finished would sit there with a cell still empty beside a bar reading 100 %.
    /// </para>
    /// </remarks>
    private static string BuildFill(
        int filled, int barWidth, List<(long From, long To)> highlights, long total, double position)
    {
        if (highlights.Count == 0)
            return new string('#', filled).PadRight(barWidth, '-');

        var cells = new char[barWidth];
        Array.Fill(cells, '-');
        foreach (var span in highlights)
        {
            var (from, to) = SpanCells(span, total, barWidth);
            var end = position >= span.To ? to : Math.Min(to, filled);
            for (var i = from; i < end; i++)
                cells[i] = '#';
        }
        return new string(cells);
    }

    /// <summary>
    /// Which cells of the bar a stretch of the file covers.
    /// </summary>
    /// <param name="span">The stretch, in the progress bytes the bar is drawn in.</param>
    /// <param name="total">The phase's total, i.e. what the whole bar stands for.</param>
    /// <param name="barWidth">The bar's width in cells.</param>
    /// <remarks>
    /// Rounded outwards - floor at the start, ceiling at the end - and then held to at least one
    /// cell, so a gap far too short to fill a cell still shows up. Overstating a stretch by a cell
    /// costs nothing; drawing nothing at all would leave a bar jumping about with no explanation on
    /// the line beside it.
    /// </remarks>
    private static (int From, int To) SpanCells(
        (long FromBytes, long ToBytes) span, long total, int barWidth)
    {
        var from = Math.Clamp((int)Math.Floor((double)span.FromBytes / total * barWidth), 0, barWidth - 1);
        var to = Math.Clamp((int)Math.Ceiling((double)span.ToBytes / total * barWidth), from + 1, barWidth);
        return (from, to);
    }

    /// <summary>
    /// Appends the bar's cells, split wherever the highlighting changes: the stretches this phase
    /// covers in <see cref="Palette.Planned"/>, the one it is working right now in
    /// <see cref="Palette.Working"/>, everything else in <see cref="Palette.Bar"/>.
    /// </summary>
    /// <param name="spans">The line being built, appended to in place.</param>
    /// <param name="bar">The bar's cells as drawn.</param>
    /// <param name="tracker">The file's work tracker.</param>
    /// <param name="color">Whether the console this is drawn for renders color. Where it does not,
    /// a marked cell still waiting to be read is drawn <c>~</c> rather than <c>-</c>: the fill
    /// already survives a colorless console by being a character, and without this the other half
    /// of the statement - which stretches the pass is going to read at all - does not, leaving a
    /// gap Scan's bar indistinguishable from a stalled whole-file one. Only that cell changes: a
    /// read cell is <c>#</c> whatever its color, and audio outside every stretch stays <c>-</c>,
    /// which is what keeps <c>~</c> readable as "marked out, not yet read".</param>
    /// <remarks>
    /// Colored cell by cell and then run-length encoded rather than sliced at each stretch's edges,
    /// because the stretches are neither guaranteed disjoint once rounded out to whole cells nor
    /// guaranteed to arrive in bar order - two gaps a few seconds apart share a cell on a narrow
    /// console. Painting is idempotent where slicing would produce overlapping spans.
    /// <para>
    /// The <c>~</c> substitution reads the same per-cell color array, deliberately: it is the
    /// definition of "would be colored" rather than a second calculation of it, so the two cannot
    /// answer differently for a cell. Rounding is the reason it matters - <see cref="SpanCells"/>
    /// rounds a stretch outwards, so a set of spans re-derived here would agree with the colors
    /// almost always and not quite everywhere.
    /// </para>
    /// </remarks>
    private static void AddBarSpans(
        List<ColoredSpan> spans, string bar, WorkTracker tracker, bool color)
    {
        var total = tracker.PhaseTotalBytes;
        var planned = tracker.PhaseSpans;
        var working = tracker.RegionSpan;
        if (total <= 0 || (planned is null or { Count: 0 } && working is null))
        {
            spans.Add(new(bar, Palette.Bar));
            return;
        }

        var colors = new ConsoleColor[bar.Length];
        Array.Fill(colors, Palette.Bar);
        foreach (var span in planned ?? [])
            Paint(colors, SpanCells(span, total, bar.Length), Palette.Planned);
        if (working is { } current)
            Paint(colors, SpanCells(current, total, bar.Length), Palette.Working);

        var cells = bar.ToCharArray();
        if (!color)
            for (var i = 0; i < cells.Length; i++)
                if (cells[i] == '-' && colors[i] != Palette.Bar)
                    cells[i] = '~';

        var start = 0;
        for (var i = 1; i <= cells.Length; i++)
            if (i == cells.Length || colors[i] != colors[start])
            {
                spans.Add(new(new string(cells, start, i - start), colors[start]));
                start = i;
            }
    }

    /// <summary>Colors one run of bar cells.</summary>
    /// <param name="colors">The bar's per-cell colors, written in place.</param>
    /// <param name="cells">The half-open cell range to color.</param>
    /// <param name="color">The color to give them.</param>
    private static void Paint(ConsoleColor[] colors, (int From, int To) cells, ConsoleColor color)
    {
        for (var i = cells.From; i < cells.To; i++)
            colors[i] = color;
    }

    /// <summary>
    /// The bar of a phase that has a position but no progress: an empty track with one marker on
    /// it, sitting where the fill would have ended so the two read the same way round.
    /// </summary>
    /// <param name="filled">How many cells an ordinary bar would have filled.</param>
    /// <param name="barWidth">The bar's width in cells.</param>
    private static string PositionMarker(int filled, int barWidth)
    {
        // The last filled cell, i.e. one back from the count - and cell 0 for a position at the very
        // start of the file, where an ordinary bar would have filled nothing at all.
        var at = Math.Clamp(filled - 1, 0, barWidth - 1);
        return new string('-', at) + 'X' + new string('-', barWidth - at - 1);
    }

    /// <summary>
    /// Appends the bar's chapter section: "----" until anything at all is found (nothing can
    /// change during Analyze anyway); then how far each of the file's chapter sequences has got,
    /// followed by one bracket holding the count of still-missing earlier chapters - the ones
    /// Scan would have to chase - and the count of extra marks found (under <c>--verify</c>,
    /// confirmed rather than found), as e.g. "ch 6(-2+1)". Each
    /// count is split off into its own span so the numbers alone carry their colors while the
    /// brackets stay structural. An extra mark found before the first chapter shows as
    /// "ch 0(+1)": the zero is worth printing there because the bracket next to it is not empty.
    /// <para>
    /// A book whose numbering restarts shows one number per part, comma-separated and in part
    /// order - "ch 11,15,4(+1)" for a file on part 3's chapter 4. No single number could say
    /// where such a run stands, and the last part's alone reads as the book having gone
    /// backwards. The missing count stays one total across every part, which is what it counts.
    /// Commas are unambiguous here because this tool never groups digits (see
    /// <see cref="ABChapterize.Cli.NumberCulture"/>), so a comma can only ever be a separator.
    /// </para>
    /// </summary>
    /// <param name="spans">The line being built, appended to in place.</param>
    /// <param name="tracker">The file's work tracker.</param>
    private static void AddChapterSpans(List<ColoredSpan> spans, WorkTracker tracker)
    {
        var highest = tracker.HighestChapters;
        var missing = tracker.MissingChapters;
        var extra = tracker.ExtraMarks;

        // --ignore-chapter-numbers is the one mode where chapters land among the named marks, so
        // it is the one mode where the extra count alone would understate the yield by everything
        // the run is actually finding - it keeps the plain total instead.
        if (highest.Count == 0 && tracker.NamedMarks > extra)
        {
            spans.Add(new($"mk {tracker.NamedMarks}", Palette.Chapters));
            return;
        }
        if (highest.Count == 0 && extra == 0)
        {
            spans.Add(new("----", Palette.NoChapters));
            return;
        }

        spans.Add(new($"ch {(highest.Count > 0 ? string.Join(",", highest) : "0")}",
                      Palette.Chapters));
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
    /// Erases the currently drawn progress block (if any), leaving the cursor on its first line
    /// ready for the next redraw or for an interleaved log/summary line. Also drops the
    /// <see cref="_lastLine"/> cache so <see cref="Render"/> treats the bar as gone and redraws it,
    /// rather than skipping on a stale content match.
    /// </summary>
    private void ClearBar()
    {
        if (!_interactive || !_barDrawn)
            return;
        var blank = new string(' ', Math.Max(0, SafeWindowWidth() - 1));
        Console.SetCursorPosition(0, Math.Max(0, Console.CursorTop - BlockLines));
        for (var i = 0; i < BlockLines; i++)
            Console.WriteLine(blank);
        Console.SetCursorPosition(0, Math.Max(0, Console.CursorTop - BlockLines));
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
        try { return Console.WindowWidth; } catch { return DefaultWidth; }
    }

    /// <summary>Stops the refresh timer and restores the cursor hidden for the interactive run.
    /// The color reset is a safety net for a run torn down mid-line (Ctrl+C during a redraw), so
    /// the shell prompt never inherits a bar section's color.</summary>
    public void Dispose()
    {
        // Drop the slot before stopping the timer. Timer.Dispose does not wait for a callback
        // already in flight, and after a Ctrl+C this runs with a file still active, so a late tick
        // could draw one more bar underneath the abort message. A callback already inside the lock
        // finishes first; one arriving after this finds no slot and returns.
        lock (_lock)
            _slot = null;
        _timer?.Dispose();
        if (_interactive)
            TrySetCursorVisible(true);
        if (_color)
            try { Console.ResetColor(); } catch { /* cosmetic only */ }
    }
}
