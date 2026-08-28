// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using ABChapterize.Cli;
using ABChapterize.Errors;
using ABChapterize.Formatting;

namespace ABChapterize.Ui;

/// <summary>
/// The <c>--debug</c> destination: one troubleshooting log per processed audiobook, written beside
/// it. Where <c>--log-file</c> collects a whole run for someone reading what happened,
/// this collects one file for someone reconstructing why a mark landed where it did - which is why
/// it is per file and not per run, and why it opens with the settings and the media the run was
/// working from.
/// <para>
/// The header matters more than it looks. A debug log arrives detached from the command that
/// produced it, and nearly every "why did it do that" turns out to hinge on a setting: which model,
/// which language, which phrase regexp, which silence threshold. Recording the resolved
/// values - after <c>--lang</c>'s localized defaults have been applied, not the raw command line -
/// is what makes the rest of the file interpretable.
/// </para>
/// <para>
/// The I/O itself is <see cref="LogFile"/>'s, with a flush per line so the tail of a run that ended
/// badly survives - but opened with <c>append: false</c>, unlike a <c>--log-file</c>. A debug log
/// holds one run: it is read by searching it (for a timestamp, a chapter number, a phrase Whisper
/// returned), and every one of those searches gets harder when a file holds several runs, because a
/// hit no longer says which run it came from. Re-running a book after a change and diffing the two
/// logs - the standard way of proving a change moved nothing - stops working entirely once the
/// second log is the first one with more text on the end. The previous run's log is therefore
/// overwritten, so anything worth keeping across a re-run has to be copied aside first.
/// </para>
/// </summary>
public sealed class DebugLog : IDisposable
{
    private readonly LogFile _file;

    /// <summary>Where this log is opened, and where <see cref="Dispose"/> is to move it - see
    /// <see cref="FollowTo"/>.</summary>
    private readonly string _path;
    private string? _moveTo;

    /// <summary>Takes ownership of an opened log file; use <see cref="Open"/>.</summary>
    /// <param name="file">The opened destination.</param>
    /// <param name="path">Where it was opened.</param>
    private DebugLog(LogFile file, string path) => (_file, _path) = (file, path);

    /// <summary>
    /// The debug log's path for an audiobook: its own name plus a suffix, so the two stay together
    /// when files are moved around and a folder full of books yields one log each. Derived from the
    /// path as it was when processing started - a file later renamed with a
    /// <c>.missing-marks-...</c> tag keeps the log under its original name, which is also the name
    /// every line inside it refers to. <see cref="FollowTo"/> is how the log gets back to the
    /// audiobook when that tag is dropped again.
    /// </summary>
    /// <param name="file">Path of the audio file.</param>
    public static string PathFor(string file) => file + Suffix;

    /// <summary>The suffix <see cref="PathFor"/> appends, named separately because
    /// <c>--cleanup</c> has to recognize these logs without having an audio file to derive the
    /// name from.</summary>
    public const string Suffix = ".debug.log";

    /// <summary>
    /// Asks this log to move itself next to the audiobook's new name once it is closed, which is
    /// what a file shedding its <c>.missing-marks</c> tag calls for: the tag was never part of the
    /// book's name, and the log's whole reason for sitting beside it is that the two are found
    /// together. Deferred to <see cref="Dispose"/> because the writer still holds the file open,
    /// and because the rename this follows happens while detection's last lines are still being
    /// written.
    /// </summary>
    /// <param name="file">The audio file's new path.</param>
    public void FollowTo(string file) => _moveTo = PathFor(file);

    /// <summary>
    /// Performs the deferred <see cref="FollowTo"/> move, replacing any log already at the
    /// destination. That one was written by the earlier run which left the <c>.missing-marks</c>
    /// tag behind - i.e. by the run this one supersedes - so keeping it would reintroduce exactly
    /// the multi-run file the truncating open exists to avoid.
    /// </summary>
    /// <exception cref="AppError">The move failed - the destination is open in an editor, say.
    /// Reported rather than swallowed: a run asked to keep a record does not get to decide
    /// quietly that it kept a differently-named one.</exception>
    private void MoveToFollowedPath()
    {
        if (_moveTo is not { } target || string.Equals(_path, target, StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            File.Move(_path, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new AppError($"Cannot move the debug log \"{_path}\" to \"{target}\": {ex.Message}");
        }
    }

    /// <summary>
    /// Creates one file's debug log - truncating any log left by an earlier run, see this class's
    /// remarks - and writes its header: the run's own banner, the file being processed, what ffprobe
    /// found in it, and the settings in force.
    /// </summary>
    /// <param name="file">Path of the audio file the log belongs to.</param>
    /// <param name="options">The run's validated options, already localized by <c>--lang</c>.</param>
    /// <param name="info">The file's probe result.</param>
    /// <exception cref="AppError">Thrown when the log cannot be written, so a run asked to keep a
    /// record fails at once rather than discovering it kept none - the same bargain
    /// <see cref="LogFile"/> makes.</exception>
    public static DebugLog Open(string file, CliOptions options, MediaInfo info)
    {
        var path = PathFor(file);
        var log = new DebugLog(LogFile.Open(path, append: false), path);
        log.Write($"file: {file}");
        log.Write($"media: duration {TimeFormat.Hms(info.DurationSeconds)}, {info.SizeBytes} bytes, " +
                  $"codec {info.AudioCodec}" + (info.AudioProfile.Length > 0 ? $" ({info.AudioProfile})" : "") +
                  $", {info.ChapterCount} existing chapter mark(s)" +
                  (info.InputDecoder is { } decoder ? $", decoder {decoder}" : ""));
        foreach (var line in DescribeSettings(options))
            log.Write($"setting: {line}");
        return log;
    }

    /// <summary>Appends one timestamped message.</summary>
    /// <param name="message">The message, without timestamp.</param>
    public void Write(string message) => _file.Write(message);

    /// <summary>Writes the closing line, releases the file and, when
    /// <see cref="FollowTo"/> asked for it, moves it next to the audiobook's new name.</summary>
    public void Dispose()
    {
        _file.Dispose();
        MoveToFollowedPath();
    }

    /// <summary>How the <c>--min-silence-length</c> line reads: the probing floor in force, and in
    /// automatic mode the range the adaptive threshold may sweep through.</summary>
    /// <param name="o">The run's validated options.</param>
    private static string DescribeMinSilence(CliOptions o)
        => !o.ProbeSilences ? "0 (jingles only)"
            : o.AutoMinSilence
                ? $"auto (from {o.MinSilenceSeconds:0.##} s, sweeping to {o.AdaptiveFloorSeconds:0.##} s)"
            : $"{o.MinSilenceSeconds:0.##} s";

    /// <summary>
    /// The settings the header records: every option whose value changes what detection does, as
    /// resolved rather than as typed. Presentation-only options (<c>--quiet</c>, <c>--color</c>, the
    /// bar) are left out - they cannot explain a mark, and a header nobody finishes reading is a
    /// header nobody reads.
    /// </summary>
    /// <param name="o">The run's validated options.</param>
    private static IEnumerable<string> DescribeSettings(CliOptions o)
    {
        yield return $"model {o.Model}, upgrade-model {o.UpgradeModel}, lang {o.Language}";
        yield return $"chapter-phrase {o.ChapterPhrase}, title \"{o.Title}\", " +
                     $"part-title \"{o.PartTitle}\", intro-title \"{o.IntroTitle}\"";
        yield return $"prologue {o.ProloguePhrase} -> \"{o.PrologueTitle}\", " +
                     $"epilogue {o.EpiloguePhrase} -> \"{o.EpilogueTitle}\"";
        // The language tag is echoed with the mapping rather than dropped: this list is what a
        // reader checks a missing custom mark against, and "why did it never match" is answered by
        // the tag as often as by the phrase.
        foreach (var mapping in o.CustomMappings)
            yield return $"custom {(mapping.Language is { } code ? $"[{code}] " : "")}" +
                         $"{mapping.Phrase} -> \"{mapping.Title}\"";
        yield return "named-mark-distance " +
                     (o.NamedMarkDistanceSeconds > 0 ? $"{o.NamedMarkDistanceSeconds:0.##} s" : "off");
        yield return $"noise-floor {(o.AutoNoiseFloor ? "auto" : $"{o.NoiseFloorDb:0.#} dBFS")}";
        yield return $"min-silence-length {DescribeMinSilence(o)}, " +
                     $"mark-lead {o.MarkLeadSeconds:0.##} s";
        yield return $"mark-refinement {(o.PreciseMark ? "on" : "off (--quick-marks)")}, " +
                     $"mark-before-jingle {(o.MarkBeforeJingle ? "on" : "off")}, " +
                     $"denoise-rescue {(o.Denoise ? "on" : "off (--no-denoise)")}";
        yield return $"early-abort {(o.EarlyAbortMinutes > 0 ? $"{o.EarlyAbortMinutes:0.#} min" : "off")}, " +
                     $"expected-start-chapter {o.ExpectedStartChapter?.ToString() ?? "-"}, " +
                     // The effective cap rather than the option, because there is always one now
                     // and a log reading "-" would send the next person hunting for why a chapter
                     // was discarded (see CliOptions.DefaultChapterCount).
                     $"max-chapter-number {o.EffectiveMaxChapterNumber?.ToString() ?? "-"}" +
                     $"{(o.MaxChapterNumber == null && o.ChapterCount == null ? " (default)" : "")}, " +
                     $"chapter-count {o.ChapterCount?.ToString() ?? "-"}, " +
                     $"trailing-scan {(o.TrailingScan ? "on" : "off")}";
        yield return $"ignore-chapter-numbers {(o.IgnoreChapterNumbers ? "on" : "off")}, " +
                     $"verify {(o.Verify ? (o.Fix ? "on (--fix)" : "on") : "off")}" +
                     (o.VerifyFailThreshold is { } threshold ? $" over {threshold}" : "") + ", " +
                     $"force {(o.Force ? "on" : "off")}, " +
                     // The effective ceiling, flagged where it was inferred rather than typed, so a
                     // log that shows a file's marks being discarded also shows what decided it.
                     $"max-chapters {o.EffectiveMaxChapters?.ToString() ?? "-"}" +
                     (o.MaxChapters == null && o.EffectiveMaxChapters != null ? " (implied)" : "") +
                     ", " +
                     $"dry-run {(o.DryRun ? "on" : "off")}";
        // Last because they are the rarest, and first among equals when they are there: a
        // --run-before may hand detection a different file from the one named on the command
        // line (joining a split book, re-encoding it), so a header that does not mention the
        // hook cannot explain the audio its own probe line goes on to describe.
        // Which server a book came from, and as whom - never the key or the password, which
        // AbsConnection.Describe is written not to render. An ABS run probes a temporary copy in a
        // folder named after a guid, so without this the log does not say what book it is about.
        if (o.AbsServer is { } server)
            yield return $"audiobookshelf {server.Describe}, {DescribeAbsModes(o)}";
        if (o.RunBefore is { } before)
            yield return $"run-before {before.Raw}";
        if (o.RunAfter is { } after)
            yield return $"run-after {after.Raw}";
        // Dead last, and one line each rather than a joined list, because this is the setting a
        // reader most needs to see and least expects: every measurement quoted anywhere in the
        // project assumes the tuning this build was calibrated with, and a log that ran under
        // other numbers explains nothing until you know which.
        foreach (var change in o.TuningChanges)
            yield return $"set {change}";
    }

    /// <summary>
    /// Which of the Audiobookshelf modes this run is in, for the header line above.
    /// </summary>
    /// <param name="o">The run's validated options.</param>
    /// <remarks>
    /// A list rather than a chain of alternatives, because <c>--abs-pull</c> and <c>--abs-push</c>
    /// are the one pair that combine and a chain reports such a run as whichever of the two it
    /// tested first. Every mode is named for the same reason the server is: a debug log arrives
    /// detached from the command that produced it, and which direction the marks were travelling
    /// is not something the rest of the file says.
    /// </remarks>
    private static string DescribeAbsModes(CliOptions o)
    {
        var modes = new List<string>();
        if (o.Abs)
            modes.Add("ABS mode");
        if (o.AbsPushOnly)
            modes.Add("abs-push-only");
        if (o.AbsPullOnly)
            modes.Add("abs-pull-only");
        if (o.AbsPull)
            modes.Add("abs-pull");
        if (o.AbsPush)
            modes.Add("abs-push");
        // Never empty in practice - a server is only resolved once a mode asked for one (see
        // CliOptions.Parse) - but a header that silently said nothing would be the one thing this
        // line exists to prevent.
        return modes.Count > 0 ? string.Join(" ", modes) : "no mode";
    }
}
