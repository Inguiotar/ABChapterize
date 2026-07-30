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
/// which language, which phrase regexp, whether the VAD pre-pass ran at all. Recording the resolved
/// values - after <c>--lang</c>'s localized defaults have been applied, not the raw command line -
/// is what makes the rest of the file interpretable.
/// </para>
/// <para>
/// The I/O itself is <see cref="LogFile"/>'s, deliberately: append rather than truncate, a flush per
/// line, and a dated header per run. A debug log is written precisely because something is going
/// wrong, so the two failure modes that matter - losing the previous attempt's record, and losing
/// the tail of a run that ended badly - are the ones already ruled out there.
/// </para>
/// </summary>
public sealed class DebugLog : IDisposable
{
    private readonly LogFile _file;

    /// <summary>Takes ownership of an opened log file; use <see cref="Open"/>.</summary>
    /// <param name="file">The opened destination.</param>
    private DebugLog(LogFile file) => _file = file;

    /// <summary>
    /// The debug log's path for an audiobook: its own name plus a suffix, so the two stay together
    /// when files are moved around and a folder full of books yields one log each. Derived from the
    /// path as it was when processing started - a file later renamed with a
    /// <c>.missing-marks-...</c> tag keeps the log under its original name, which is also the name
    /// every line inside it refers to.
    /// </summary>
    /// <param name="file">Path of the audio file.</param>
    public static string PathFor(string file) => file + ".debug.log";

    /// <summary>
    /// Opens (or creates) one file's debug log and writes its header: the run's own banner, the file
    /// being processed, what ffprobe found in it, and the settings in force.
    /// </summary>
    /// <param name="file">Path of the audio file the log belongs to.</param>
    /// <param name="options">The run's validated options, already localized by <c>--lang</c>.</param>
    /// <param name="info">The file's probe result.</param>
    /// <exception cref="AppError">Thrown when the log cannot be written, so a run asked to keep a
    /// record fails at once rather than discovering it kept none - the same bargain
    /// <see cref="LogFile"/> makes.</exception>
    public static DebugLog Open(string file, CliOptions options, MediaInfo info)
    {
        var log = new DebugLog(LogFile.Open(PathFor(file)));
        log.Write($"file: {file}");
        log.Write($"media: duration {TimeFormat.Hms(info.DurationSeconds)}, {info.SizeBytes} bytes, " +
                  $"codec {info.AudioCodec}" + (info.AudioProfile.Length > 0 ? $" ({info.AudioProfile})" : "") +
                  $", {info.ChapterCount} existing chapter marking(s)" +
                  (info.InputDecoder is { } decoder ? $", decoder {decoder}" : ""));
        foreach (var line in DescribeSettings(options))
            log.Write($"setting: {line}");
        return log;
    }

    /// <summary>Appends one timestamped message.</summary>
    /// <param name="message">The message, without timestamp.</param>
    public void Write(string message) => _file.Write(message);

    /// <summary>Writes the closing line and releases the file.</summary>
    public void Dispose() => _file.Dispose();

    /// <summary>
    /// The settings the header records: every option whose value changes what detection does, as
    /// resolved rather than as typed. Presentation-only options (<c>--quiet</c>, <c>--color</c>, the
    /// bar) are left out - they cannot explain a mark, and a header nobody finishes reading is a
    /// header nobody reads.
    /// </summary>
    /// <param name="o">The run's validated options.</param>
    private static IEnumerable<string> DescribeSettings(CliOptions o)
    {
        yield return $"model {o.Model}, pass3-model {o.Pass3Model}, lang {o.Language}";
        yield return $"chapter-phrase {o.ChapterPhrase}, title \"{o.Title}\", intro-title \"{o.IntroTitle}\"";
        yield return $"prologue {o.ProloguePhrase} -> \"{o.PrologueTitle}\", " +
                     $"epilogue {o.EpiloguePhrase} -> \"{o.EpilogueTitle}\"";
        foreach (var mapping in o.CustomMappings)
            yield return $"custom {mapping.Phrase} -> \"{mapping.Title}\"";
        yield return $"min-silence-length {(o.AutoMinSilence ? $"auto (floor {o.MinSilenceSeconds:0.##} s)" : $"{o.MinSilenceSeconds:0.##} s")}, " +
                     $"max-jingle-length {(o.AutoMaxJingle ? $"auto (ceiling {o.MaxJingleSeconds:0.#} s)" : $"{o.MaxJingleSeconds:0.#} s")}, " +
                     $"mark-lead {o.MarkLeadSeconds:0.##} s";
        yield return $"vad-pre-pass {(o.RunVadPrePass ? "on" : "off")}, " +
                     $"mark-refinement {(o.PreciseMark ? "on" : "off (--quick-marks)")}, " +
                     $"mark-before-jingle {(o.MarkBeforeJingle ? "on" : "off")}";
        yield return $"early-abort {(o.EarlyAbortMinutes > 0 ? $"{o.EarlyAbortMinutes:0.#} min" : "off")}, " +
                     $"expected-start-chapter {o.ExpectedStartChapter?.ToString() ?? "-"}, " +
                     $"max-chapter-number {o.MaxChapterNumber?.ToString() ?? "-"}, " +
                     $"trailing-scan {(o.TrailingScan ? "on" : "off")}";
        yield return $"ignore-chapter-numbers {(o.IgnoreChapterNumbers ? "on" : "off")}, " +
                     $"verify {(o.Verify ? "on" : "off")}, force {(o.Force ? "on" : "off")}, " +
                     $"dry-run {(o.DryRun ? "on" : "off")}";
    }
}
