// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ABChapterize.Concurrency;
using ABChapterize.Detection;
using ABChapterize.Language;
using ABChapterize.Transcription;

namespace ABChapterize.Cli;

/// <summary>
/// Exception thrown for any command line syntax or validation error.
/// The message describes the problem; the caller prints it together with the usage info.
/// </summary>
public sealed class CliError : Exception
{
    /// <summary>Creates a new command line error with the given description.</summary>
    /// <param name="message">Human readable description of the problem.</param>
    public CliError(string message) : base(message) { }
}

/// <summary>
/// Parsed and validated command line options of the abchapterize tool.
/// Use <see cref="Parse"/> to create an instance from raw arguments.
/// </summary>
public sealed class CliOptions
{
    /// <summary>Recursively descend into subdirectories (--recurse / -r).</summary>
    public bool Recurse { get; private set; }

    /// <summary>Keep the original file as "*.bak" (--backup / -b).</summary>
    public bool Backup { get; private set; }

    /// <summary>Restore "*.&lt;ext&gt;.bak" backup files to their original names (--revert / -R).</summary>
    public bool Revert { get; private set; }

    /// <summary>
    /// Lists every file that would be processed, then exits without loading a Whisper model,
    /// invoking ffmpeg or touching any file (--no-op / -O). Requires --filter - the whole
    /// point is checking that a --filter regexp or extension list actually matches the
    /// intended files before committing to a real run. Combinable only with --recurse,
    /// --filter and the output options, the same restriction <see cref="Revert"/> has.
    /// </summary>
    public bool NoOp { get; private set; }

    /// <summary>
    /// Ignore (and overwrite) the batch progress file a previous, interrupted run may have left
    /// in a directory given on the command line (--ignore-progress), processing every selected
    /// file again instead of resuming where that run stopped. See
    /// <see cref="ABChapterize.Processing.BatchProgress"/> for the checkpointing itself. Unrelated
    /// to the ".missing-marks" auto-resume of an individual file, which is governed by --force.
    /// </summary>
    public bool IgnoreProgress { get; private set; }

    /// <summary>
    /// Two-letter ISO 639-1 language hint for Whisper, or "auto" (--lang / -l, default "auto").
    /// With "auto", <see cref="ChapterDetector"/> detects each file's language from a short
    /// clip via Whisper's own language detector instead of assuming a fixed language for the
    /// whole run; see <see cref="AutoLanguage"/>.
    /// </summary>
    public string Language { get; private set; } = "auto";

    /// <summary>True when <see cref="Language"/> is "auto" - the default. See <see cref="ResolveProfile"/>.</summary>
    public bool AutoLanguage => Language == "auto";

    /// <summary>Raw chapter phrase or "/regexp/" as given on the command line (--chapter-phrase / -c); the default is localized by --lang.</summary>
    public string ChapterPhrase { get; private set; } = "chapter";

    /// <summary>
    /// Raw prologue phrase or "/regexp/" (--prologue-phrase / -p); the default is localized by
    /// --lang. Empty switches prologue detection off for the run - the one way to opt out, since
    /// there is no separate flag and no number could ever be missing without it.
    /// </summary>
    public string ProloguePhrase { get; private set; } = "prologue";

    /// <summary>Title written for a detected prologue (--prologue-title / -P, default "Prologue",
    /// localized by --lang).</summary>
    public string PrologueTitle { get; private set; } = "Prologue";

    /// <summary>Raw epilogue phrase or "/regexp/" (--epilogue-phrase / -g); empty switches epilogue
    /// detection off, as <see cref="ProloguePhrase"/> does for the prologue.</summary>
    public string EpiloguePhrase { get; private set; } = "epilogue";

    /// <summary>Title written for a detected epilogue (--epilogue-title / -G, default "Epilogue",
    /// localized by --lang).</summary>
    public string EpilogueTitle { get; private set; } = "Epilogue";

    /// <summary>
    /// The <c>--custom</c> / <c>--custom-file</c> phrase-to-title mappings, in the order given.
    /// Each becomes a <see cref="NamedPhrase"/> that may match anywhere in a file and as often as it
    /// occurs, so unlike the prologue and epilogue these are capped per file - see
    /// <see cref="DetectionTuning.MaxCustomMarksPerFile"/>. Never localized: a phrase the user
    /// wrote out is meant exactly as written, in whatever language they wrote it.
    /// </summary>
    public IReadOnlyList<CustomMapping> CustomMappings => _customMappings;

    private readonly List<CustomMapping> _customMappings = [];

    /// <summary>
    /// Whether the chapter numbers heard in an announcement are reasoned about
    /// (<c>--ignore-chapter-numbers</c>). The announcements themselves are still detected and still
    /// become marks either way; what this drops is everything built on the numbers forming a
    /// sequence: no gap is ever found or filled, so passes 2.5 and 3 never run, no file is ever
    /// tagged ".missing-marks", and every option that states an expectation about the numbers is
    /// rejected outright rather than silently ignored. A parsed number still reaches the mark's
    /// title, it just no longer has to agree with its neighbours - which is the point, for a book
    /// that restarts its count per part or numbers nothing at all.
    /// </summary>
    public bool IgnoreChapterNumbers { get; private set; }

    /// <summary>Whisper model selector (--model / -m): tiny, base, small, medium, turbo or large,
    /// or <c>custom:&lt;path&gt;</c> for a GGML file of the user's own (see
    /// <see cref="ParseModelSelector"/>).</summary>
    public string Model { get; private set; } = "turbo";

    /// <summary>
    /// Whisper model selector used for pass 3 (gap filling) only (--pass3-model / -M); defaults
    /// to <see cref="Model"/> when not given, so pass 3 uses the same model as pass 2 unless the
    /// user asks for a different one. A lighter model can speed pass 3 up ("I'll likely fix the
    /// stragglers by hand anyway"), a heavier one ("large") can make one last, best-effort attempt
    /// at the chapters the main model missed. The pass-3 model is loaded (and downloaded) lazily,
    /// only when a file actually reaches pass 3. Takes a <c>custom:&lt;path&gt;</c> selector just
    /// like <see cref="Model"/>.
    /// </summary>
    public string Pass3Model { get; private set; } = "turbo";

    /// <summary>
    /// True when <see cref="Pass3Model"/> is strictly more capable than <see cref="Model"/> - the
    /// "one last, best-effort attempt" direction of --pass3-model rather than the "get the
    /// stragglers over with quickly" one. Gates pass 2.5 (see <c>ChapterDetector</c>'s
    /// <c>RunPass25Async</c>), which is only ever worth its extra probes when the model doing them
    /// can actually hear something the pass-2 model could not; with an equal or lighter pass-3
    /// model it would just re-probe the same audio to the same conclusion, more slowly.
    /// <para>
    /// Ranking is by model size (see <see cref="ModelCatalog.ApproximateSizeBytes"/>), which
    /// reproduces <see cref="ModelNames"/>'s own order for the built-in models and is the only
    /// thing there is to go on for a <c>custom:</c> file. Settled once during
    /// <see cref="Parse"/> rather than re-derived per gap, so a custom file's size is read from
    /// disk exactly once.
    /// </para>
    /// </summary>
    public bool Pass3ModelIsUpgrade { get; private set; }

    /// <summary>
    /// Forces the CPU backend for Whisper instead of the fastest available hardware
    /// acceleration (--cpu-only / -C; see <see cref="WhisperTranscriber"/>). The Silero VAD
    /// pre-pass already always runs on CPU regardless of this option - the ONNX Runtime
    /// package this tool references has no GPU-capable execution provider to begin with - so
    /// this only changes Whisper's own backend selection. Useful to leave a GPU free for other
    /// work, or to sidestep a flaky/unsupported GPU backend.
    /// </summary>
    public bool CpuOnly { get; private set; }

    /// <summary>Discard pre-existing chapter markings instead of skipping the file (--force / -f).</summary>
    public bool Force { get; private set; }

    /// <summary>
    /// Maximum plausible number of pre-existing chapter markings (--max-chapters / -x).
    /// Files exceeding it get their markings discarded as bogus. Null when not specified.
    /// </summary>
    public int? MaxChapters { get; private set; }

    /// <summary>
    /// Highest chapter number considered plausible for this book (--max-chapter-number / -N).
    /// Null (the default) means no cap. Any chapter phrase whose parsed number exceeds it is
    /// discarded the moment it is found, before it can become a mark or widen the expected
    /// chapter sequence - a Whisper mishearing turning "chapter ten" into "chapter 510" otherwise
    /// leaves a 500-chapter "gap" for Pass 3 to hunt through and a file tagged with a
    /// ".missing-marks" suffix listing all of them. Unrelated to <see cref="MaxChapters"/>, which
    /// counts a file's <i>pre-existing markings</i> rather than the numbers detection itself reads
    /// out of the audio.
    /// </summary>
    public int? MaxChapterNumber { get; private set; }

    /// <summary>
    /// Transcribes everything after the last chapter found, all the way to the end of the file,
    /// looking for further chapters (--trailing-scan / -L; off by default).
    /// <para>
    /// This closes the one hole the ordinary Pass 3 tail structurally cannot:
    /// <see cref="ABChapterize.Detection.GapPlanning.FindGaps"/> spots a missing chapter by
    /// finding a hole in the number sequence, which needs a known chapter on each side of it. A
    /// chapter missing <em>after</em> the last one found has nothing above it to compare against,
    /// so nothing notices it is gone and the file is written out looking complete.
    /// </para>
    /// <para>
    /// Off by default because the cost is paid on every file, every run, whether or not anything is
    /// wrong: the region is a whole chapter long in a healthy book, and with no expected numbers to
    /// satisfy nothing can stop it early - it always runs to the end of the file. Measured on this
    /// project's own hardware, the default turbo model transcribes at roughly 5.7x real time, so a
    /// 30-minute final chapter costs about five minutes per file; heavier models are slower still.
    /// Worth it when a book's last chapter genuinely matters, wasteful as a blanket default - the
    /// same reasoning that keeps pass 2.5 opt-in.
    /// </para>
    /// <para>
    /// Does nothing when no chapter was found at all (there is no "last chapter" to scan from, and
    /// transcribing an entire book on spec is not what this flag is for), nor after an
    /// --early-abort or --expected-start-chapter abort, which mean the file is being given up on
    /// rather than gap-filled.
    /// </para>
    /// </summary>
    public bool TrailingScan { get; private set; }

    /// <summary>
    /// Minutes of a file's play time Pass 2 may probe without finding a single chapter before
    /// giving up on it outright (--early-abort / -a, default 60; 0 disables the feature
    /// entirely). Active by default - it guards against burning a whole-book transcription on a
    /// file that plainly will not yield any chapters (wrong --chapter-phrase, wrong --lang, or a
    /// book that announces chapters differently). Only applies to a fresh, from-scratch run: a
    /// --verify gap recovery or a ".missing-marks" resume always seeds at least one confirmed
    /// chapter, so <see cref="ChapterDetector"/> never aborts those early. An aborted file is left
    /// unchanged, exactly as if a full scan had found no chapter phrases.
    /// </summary>
    public double EarlyAbortMinutes { get; private set; } = 60;

    /// <summary>
    /// The chapter number this book is expected to start at (--expected-start-chapter / -e), for
    /// a split-book part that does not begin at chapter 1. Null (the default) means no
    /// expectation: Pass 2's first find, whatever its number, is accepted outright and <see
    /// cref="ABChapterize.Detection.GapPlanning.FindGaps"/> never hunts below it. When set,
    /// <see cref="ABChapterize.Detection.GapPlanning.FindGaps"/> hunts the leading gap down to this
    /// number via Pass 3, exactly like an interior gap, regardless of which
    /// <see cref="ChapterDetector"/> entry point is running - so a ".missing-marks" resume of a
    /// leading gap keeps re-deriving the gap it was tagged with. If Pass 3 cannot find them either,
    /// the file is tagged ".missing-marks-...", as an unresolved interior gap is. Separately, and
    /// only for a fresh run (the same restriction as <see cref="EarlyAbortMinutes"/>, for the same
    /// reason), the file is aborted and left unchanged if the very first chapter Pass 2 finds is
    /// numbered below this value - almost certainly the wrong file, phrase or language rather than
    /// a gap worth hunting for.
    /// </summary>
    public int? ExpectedStartChapter { get; private set; }

    /// <summary>
    /// Check pre-existing chapter markings against the audio instead of trusting them
    /// blindly (--verify / -V): a short window around each marking's own timestamp is probed
    /// with Whisper for the chapter phrase and the expected number. Markings that all check
    /// out leave the file untouched, same as today without --force; if any fails, the
    /// markings are discarded and the file goes through full detection, same as --force would.
    /// With --max-chapters, a file already over the threshold is still assumed bogus outright
    /// and skips verification entirely - --verify only decides borderline cases, it never
    /// makes a --max-chapters rejection stricter.
    /// </summary>
    public bool Verify { get; private set; }

    /// <summary>
    /// Maximum number of unconfirmed --verify markings a file may have and still get the
    /// gap-scoped recovery <see cref="ChapterDetector.DetectGapsAsync"/> normally runs around
    /// them (--verify-threshold / -h). Above it, the confirmed markings are no longer trusted
    /// either - the file's whole marking set is treated as bogus and discarded outright, the
    /// same full, from-scratch detection a --verify run with nothing confirmed at all already
    /// falls back to. Guards against trusting a couple of lucky survivors as gap-recovery
    /// anchors in a file whose markings are otherwise mostly wrong. Null (the default) never
    /// overrides the plain "at least one confirmed marking" rule; requires --verify.
    /// </summary>
    public int? VerifyFailThreshold { get; private set; }

    /// <summary>
    /// Anchors the chapter mark to a jingle preceding the announcement instead of the default
    /// fixed offset (--mark-before-jingle / -j): starting from whatever mark default-mode
    /// placement (normally already corrected by <see cref="PreciseMark"/>) computed for the
    /// phrase, walks backward through any leading silence and then the jingle's own music to the
    /// previous chapter's actual trailing narration - or, where two jingles play back to back
    /// with an audible break between them, to the second one's start rather than in front of the
    /// first - and marks right there; see
    /// <see cref="JingleGeometry.ComputeMarkBeforeJingle"/> for the mechanics. Building on top of
    /// default-mode placement instead of replacing it is what makes this compatible with
    /// <see cref="PreciseMark"/>, unlike the original "--jingle" mode it descends from. Since
    /// <see cref="PreciseMark"/> is on unless <see cref="QuickMarks"/> asks otherwise, the walk
    /// normally starts from a confirmed announcement onset, which is what makes its result
    /// trustworthy on the VAD/silence heuristics alone; only without that confirmation is the
    /// walked result itself re-checked - see <see cref="PreciseMarkRefiner"/>'s
    /// <c>VerifyMarkBeforeJingleAsync</c>. Without this option, see
    /// <see cref="DetectionTuning.DefaultMarkLeadSeconds"/> for the placement used instead.
    /// </summary>
    public bool MarkBeforeJingle { get; private set; }

    /// <summary>
    /// Opts out of the mark refinement that normally runs on every mark (--quick-marks / -Q),
    /// trading placement accuracy for speed: probing alone decides where each mark goes, with no
    /// re-transcription to confirm it. Marks placed this way are usually usable, but a mark can
    /// land after the chapter phrase rather than before it - even together with <see
    /// cref="MarkBeforeJingle"/>, whose backward walk can only be as good as the mark it starts
    /// from (see <see cref="JingleGeometry.RetreatPastNonSpeech"/>'s known-limitation note).
    /// For the refinement this switches off, see <see cref="PreciseMark"/>.
    /// <para><b>Experimental.</b></para>
    /// </summary>
    public bool QuickMarks { get; private set; }

    /// <summary>
    /// Whether marks are verified (and if necessary corrected) by directly re-transcribing the
    /// audio at the mark instead of trusting the VAD/duration heuristics that produced it - the
    /// default, and simply the inverse of <see cref="QuickMarks"/>. The CLI expresses this as an
    /// opt-out while the detection engine reads it as a capability, so that the two never have to
    /// reason about a double negative.
    /// <para>
    /// If the chapter phrase is the first thing heard at the mark, it is left alone - the common
    /// case, and the only cost paid for a chapter that was already right. If not (typically because
    /// the mark landed on a jingle's spurious VAD "speech" blip rather than the announcement), each
    /// subsequent VAD speech-segment start after the mark is checked in turn until one succeeds and
    /// the next fails again; only that success-then-fail pattern confirms the phrase truly begins at
    /// the earlier candidate rather than at another false positive deeper inside the jingle. If the
    /// forward search finds nothing, the same check runs backward through the speech-segment starts
    /// before the mark, for the rarer opposite failure of a mark landing generously past the true
    /// announcement. A chapter whose phrase is never confirmed keeps its original mark.
    /// </para>
    /// <para>
    /// Costs one or more extra Whisper transcriptions per chapter - most of all where a jingle
    /// produces several spurious VAD blips, since each needs its own - which is what
    /// <see cref="QuickMarks"/> exists to skip; see <see cref="ChapterDetector"/> for the mechanics.
    /// Combines with <see cref="MarkBeforeJingle"/>, which walks the refined mark one step further
    /// back to the jingle's start - and whose walked result is then verified in turn.
    /// </para>
    /// </summary>
    public bool PreciseMark => !QuickMarks;

    /// <summary>
    /// Maximum expected jingle duration in seconds (--max-jingle-length / -X, default 45), or 0
    /// to say no jingle is expected at all. Above 0, the probe window after each silence spans
    /// this duration plus a flat 5-second margin for the chapter phrase itself, and VAD
    /// non-speech regions can add extra probe candidates for silence-less jingles; at 0,
    /// neither happens - Pass 2 falls back to its original fixed probe window, exactly as if
    /// jingle support did not exist. This is always the ceiling used until a real jingle length
    /// has been observed; see <see cref="AutoMaxJingle"/> for the default self-tightening
    /// behavior applied on top of it during probing.
    /// </summary>
    public double MaxJingleSeconds { get; private set; } = 45;

    /// <summary>
    /// True (the default) unless --max-jingle-length was given an explicit numeric value
    /// (including 0, which also disables jingle probing/VAD entirely - see
    /// <see cref="RunVadPrePass"/>): <see cref="ChapterDetector"/> starts probing with the
    /// <see cref="MaxJingleSeconds"/> ceiling, then - from the second jingle mark found (the
    /// same reasoning as <see cref="AutoMinSilence"/>: the gap before the first mark is not
    /// necessarily representative) - resizes the probe window to 1.25x the longest jingle observed
    /// so far plus margin, both up and down as that maximum changes, capped at the original
    /// ceiling. Chapters with no jingle (or an ultra-short one) are excluded: some audiobooks play
    /// the jingle only for some chapters, and such a chapter says nothing about the window a full
    /// jingle needs. "auto" can also be given explicitly for clarity.
    /// </summary>
    public bool AutoMaxJingle { get; private set; } = true;

    /// <summary>
    /// True whenever the Silero VAD pre-pass should run over a file: either
    /// <see cref="MarkBeforeJingle"/> needs its jingle/VAD-region anchor, or
    /// <see cref="MaxJingleSeconds"/> is above 0 and Pass 2 may need to widen its probe window
    /// or add VAD-region candidates for a possible jingle. False only when neither applies -
    /// <see cref="MarkBeforeJingle"/> is off and <see cref="MaxJingleSeconds"/> is exactly 0 -
    /// which reproduces this tool's original, pre-jingle-support behavior exactly.
    /// </summary>
    public bool RunVadPrePass => MarkBeforeJingle || MaxJingleSeconds > 0;

    /// <summary>
    /// Minimum silence duration in seconds that counts as a potential chapter break
    /// (--min-silence-length / -n). Every such silence triggers a Whisper probe, so an
    /// explicit higher value can reduce the number of probes further still. This is always
    /// the silence scan's floor (1.5 by default); see <see cref="AutoMinSilence"/> for the
    /// default self-tightening behavior applied on top of it during probing.
    /// </summary>
    public double MinSilenceSeconds { get; private set; } = 1.5;

    /// <summary>
    /// True (the default) unless --min-silence-length was given an explicit numeric value:
    /// the silence scan (Pass 1) still uses the 1.5 s floor, but <see cref="ChapterDetector"/>
    /// self-tightens the probing threshold after each chapter mark instead of probing every
    /// silence found. "auto" can also be given explicitly for clarity.
    /// </summary>
    public bool AutoMinSilence { get; private set; } = true;

    /// <summary>Suppress per-file output; warnings and errors are still shown (--quiet / -q).</summary>
    public bool Quiet { get; private set; }

    /// <summary>
    /// Print processing details as log lines (--verbose / -v). Probe/gap/verify lines are logged
    /// up to and including their "&lt;length&gt;@&lt;timestamp&gt;" header; the transcribed segments
    /// themselves are only dumped when <see cref="VerboseTranscripts"/> is also set. Implied by
    /// <see cref="VerboseTranscripts"/>.
    /// </summary>
    public bool Verbose { get; private set; }

    /// <summary>
    /// Like <see cref="Verbose"/>, but also dumps every Whisper transcript's segments after its
    /// header line (--verbose-transcripts / -T) - what plain --verbose did before this flag
    /// existed. Setting it implies <see cref="Verbose"/>.
    /// </summary>
    public bool VerboseTranscripts { get; private set; }

    /// <summary>Suppress the progress bar; per-file summaries use the log-line format (--no-bar / -B).</summary>
    public bool NoBar { get; private set; }

    /// <summary>
    /// Path of the file the log stream is written to (--log-file / -o), or null for no log file.
    /// Asking for one turns logging on by itself - there would be nothing to write otherwise - and
    /// sends the whole stream to the file rather than the console, so the console keeps its
    /// progress bar and per-file summaries alone. <see cref="VerboseTranscripts"/> still decides
    /// how much detail the stream carries.
    /// </summary>
    public string? LogFilePath { get; private set; }

    /// <summary>True when log lines are produced at all, for whichever destination
    /// (<see cref="Verbose"/> or <see cref="LogFilePath"/>) asked for them. Call sites that only
    /// build a log message when someone is listening test this rather than
    /// <see cref="Verbose"/>.</summary>
    public bool LoggingEnabled => Verbose || LogFilePath != null;

    /// <summary>Print a run summary with file counts and timings at the end (--summary / -s).</summary>
    public bool Summary { get; private set; }

    /// <summary>
    /// Run full detection but write nothing (--dry-run / -d): the chapters that would be
    /// written are printed (timestamps, numbers and titles) instead. Lets a result be
    /// reviewed before trusting it with a real file, without needing --backup/--revert.
    /// </summary>
    public bool DryRun { get; private set; }

    /// <summary>
    /// Write detected chapters to a sidecar file alongside the output (--export / -E), in
    /// addition to writing them into the audio file. Composes with normal detection (and
    /// with --dry-run, which still saves the sidecar even though the audio file is left
    /// untouched). Format is FFMETADATA unless --simple-metadata is given.
    /// </summary>
    public bool Export { get; private set; }

    /// <summary>
    /// Skip Whisper detection entirely and write chapters from a previously exported
    /// sidecar file instead (--import / -I). Lets a rare misdetection be hand-corrected in
    /// the sidecar and re-applied without re-transcribing the whole file.
    /// </summary>
    public bool Import { get; private set; }

    /// <summary>
    /// Use the plain-text "H:MM:SS.fff  Title" sidecar format instead of FFMETADATA for
    /// both --export and --import (--simple-metadata / -S).
    /// </summary>
    public bool SimpleMetadata { get; private set; }

    /// <summary>
    /// Maximum number of files processed concurrently (--jobs / -J), or null for the
    /// default: automatically adjusted between 1 and a hardware-derived ceiling based on
    /// live CPU load (see <see cref="ConcurrencyMonitor"/>). "1" disables concurrency
    /// entirely - useful for CI logs or troubleshooting.
    /// </summary>
    public int? Jobs { get; private set; }

    /// <summary>Word used to build chapter titles; the chapter number is appended (--title / -t, default "Chapter", localized by --lang).</summary>
    public string Title { get; private set; } = "Chapter";

    /// <summary>
    /// Title of the synthetic chapter covering the audio before the first detected chapter
    /// (--intro-title / -i). Audiobooks usually start with a prelude, so the first detected
    /// chapter must not be moved to 0:00; instead this intro chapter is prepended at 0:00
    /// when the first chapter starts later. Defaults to "Intro", localized by --lang.
    /// </summary>
    public string IntroTitle { get; private set; } = "Intro";

    /// <summary>
    /// Regular expression from --filter "/regexp/", matched case-insensitively against the
    /// whole file path of each candidate file. Null when no regexp filter is active.
    /// </summary>
    public Regex? FilterRegex { get; private set; }

    /// <summary>
    /// Extensions (with leading dots, lower-case) from --filter "ext1,ext2" that restrict
    /// which of the supported file types are processed. Null when no extension filter is active.
    /// </summary>
    public string[]? FilterExtensions { get; private set; }

    /// <summary>The file extensions to process: --filter's list, or all supported ones.</summary>
    public string[] EffectiveExtensions => FilterExtensions ?? SupportedExtensions;

    /// <summary>
    /// One file or directory named at the end of the command line.
    /// </summary>
    /// <param name="Path">The path exactly as given, so console output echoes what was typed.</param>
    /// <param name="IsDirectory">True for a directory, false for a single file.</param>
    public readonly record struct Target(string Path, bool IsDirectory);

    /// <summary>
    /// The files and directories to process, in command line order and with duplicates
    /// removed. Files and directories may be mixed freely; a path covered by an earlier
    /// directory is still only processed once (see the file enumeration in
    /// <see cref="ABChapterize.Processing.FileProcessor"/>).
    /// </summary>
    public IReadOnlyList<Target> Targets => _targets;

    private readonly List<Target> _targets = [];

    /// <summary>
    /// Compiled case-insensitive regular expression used to find the chapter phrase in transcribed text.
    /// Built from <see cref="ChapterPhrase"/>.
    /// </summary>
    public Regex PhraseRegex { get; private set; } = null!;

    /// <summary>
    /// True when <see cref="PhraseRegex"/> contains an explicit capturing group for the chapter number;
    /// false when the number is expected to immediately follow the matched phrase.
    /// </summary>
    public bool PhraseHasNumberGroup { get; private set; }

    /// <summary>
    /// The profile resolved at parse time: for an explicit --lang, this is used for every file;
    /// with <see cref="AutoLanguage"/>, it is the English fallback profile used only when a
    /// file's own detection is inconclusive or skipped - see <see cref="ResolveProfile"/>, which
    /// <see cref="ChapterDetector"/> calls per file instead when auto-detecting.
    /// </summary>
    public LanguageProfile DefaultProfile { get; private set; } = null!;

    /// <summary>
    /// The accepted --model/--pass3-model selectors, in ascending order of transcription quality -
    /// an order <see cref="Pass3ModelIsUpgrade"/> reads directly, so keep any new entry in its
    /// rightful place rather than appending it. "turbo" (large-v3-turbo) sits just below "large":
    /// a distilled large that trades a little accuracy for a lot of speed, still clearly ahead of
    /// "medium".
    /// </summary>
    private static readonly string[] ModelNames = ["tiny", "base", "small", "medium", "turbo", "large"];

    /// <summary>Maps every short option letter to its long option name.</summary>
    private static readonly Dictionary<char, string> ShortOptions = new()
    {
        ['r'] = "--recurse", ['b'] = "--backup", ['f'] = "--force", ['j'] = "--mark-before-jingle",
        ['Q'] = "--quick-marks",
        ['q'] = "--quiet", ['v'] = "--verbose", ['T'] = "--verbose-transcripts", ['s'] = "--summary",
        ['l'] = "--lang", ['c'] = "--chapter-phrase", ['m'] = "--model", ['M'] = "--pass3-model",
        ['x'] = "--max-chapters", ['N'] = "--max-chapter-number",
        ['a'] = "--early-abort", ['e'] = "--expected-start-chapter", ['L'] = "--trailing-scan",
        ['F'] = "--filter", ['X'] = "--max-jingle-length",
        ['n'] = "--min-silence-length", ['t'] = "--title", ['i'] = "--intro-title",
        ['p'] = "--prologue-phrase", ['P'] = "--prologue-title",
        ['g'] = "--epilogue-phrase", ['G'] = "--epilogue-title",
        ['u'] = "--custom", ['U'] = "--custom-file",
        ['R'] = "--revert", ['B'] = "--no-bar", ['d'] = "--dry-run",
        ['E'] = "--export", ['I'] = "--import", ['S'] = "--simple-metadata",
        ['J'] = "--jobs", ['V'] = "--verify", ['h'] = "--verify-threshold", ['C'] = "--cpu-only", ['O'] = "--no-op",
        ['o'] = "--log-file",
    };

    // Tracks which value options were given explicitly, for semantic validation and
    // for applying the --lang-dependent defaults only when the user did not choose.
    private bool _langSet, _phraseSet, _modelSet, _pass3ModelSet, _maxSet, _maxChapterNumberSet, _titleSet, _introSet, _jingleLenSet, _minSilenceSet, _earlyAbortSet, _expectedStartSet;
    private bool _prologuePhraseSet, _prologueTitleSet, _epiloguePhraseSet, _epilogueTitleSet;

    /// <summary>
    /// True when any option was given that only means something for a run that actually detects
    /// or writes chapters - i.e. anything beyond file selection (--recurse/--filter) and the
    /// output/logging options. <see cref="Revert"/> and <see cref="NoOp"/> do neither, so both
    /// reject all of these instead of silently ignoring them; sharing one list is what keeps the
    /// two checks, and the promise their error messages make, from drifting apart as options are
    /// added.
    /// </summary>
    private bool AnyProcessingOptionGiven
        => Backup || Force || CpuOnly || MarkBeforeJingle || QuickMarks || TrailingScan || DryRun
           || Export || Import || SimpleMetadata || Verify || IgnoreProgress || Jobs != null
           || _langSet || _phraseSet || _modelSet || _pass3ModelSet || _maxSet || _maxChapterNumberSet
           || _titleSet || _introSet || _jingleLenSet || _minSilenceSet || _earlyAbortSet
           || _expectedStartSet || _prologuePhraseSet || _prologueTitleSet
           || _epiloguePhraseSet || _epilogueTitleSet
           || _customMappings.Count > 0 || IgnoreChapterNumbers;

    /// <summary>
    /// Short hash of every option that changes what a run does to a file - the language and
    /// phrase, the models, the detection tuning and safety nets, the file selection, the output
    /// mode. Options that only change what the run <i>looks like</i> (--quiet, --verbose,
    /// --log-file, --no-bar, --summary, --jobs, --cpu-only) are deliberately left out, so adding one of those
    /// to an interrupted run's command line still resumes it.
    /// <para>
    /// <see cref="ABChapterize.Processing.BatchProgress"/> stores this alongside the files a run
    /// finished and refuses to resume progress recorded under a different fingerprint: those files
    /// were processed by a different command, so counting them as done would silently leave the
    /// new one's work undone. Derived from the resolved values rather than the raw arguments, so
    /// "-rb" and "--recurse --backup" agree.
    /// </para>
    /// </summary>
    public string RunFingerprint
    {
        get
        {
            var relevant = string.Join('\n', [
                $"recurse={Recurse}", $"backup={Backup}", $"force={Force}",
                $"lang={Language}", $"phrase={ChapterPhrase}", $"title={Title}", $"intro={IntroTitle}",
                $"prologue={ProloguePhrase}/{PrologueTitle}", $"epilogue={EpiloguePhrase}/{EpilogueTitle}",
                $"ignorenumbers={IgnoreChapterNumbers}",
                $"custom={string.Join('|', _customMappings.Select(m => $"{m.Phrase}=>{m.Title}"))}",
                $"model={Model}", $"pass3={Pass3Model}",
                $"maxchapters={MaxChapters}", $"maxnumber={MaxChapterNumber}",
                $"earlyabort={EarlyAbortMinutes}", $"expectedstart={ExpectedStartChapter}",
                $"trailingscan={TrailingScan}", $"verify={Verify}", $"verifythreshold={VerifyFailThreshold}",
                $"jingle={MarkBeforeJingle}", $"quickmarks={QuickMarks}",
                $"maxjingle={MaxJingleSeconds}/{AutoMaxJingle}", $"minsilence={MinSilenceSeconds}/{AutoMinSilence}",
                $"filter={FilterRegex?.ToString()}", $"extensions={string.Join(',', EffectiveExtensions)}",
                $"import={Import}", $"export={Export}", $"simple={SimpleMetadata}",
            ]);
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(relevant)))[..16];
        }
    }

    /// <summary>
    /// File extensions of the container formats that ffmpeg can both read and write chapter
    /// marks for (verified empirically: mp4/ipod, ID3v2 mp3, Ogg Opus, Matroska). Notably
    /// absent: .ogg (Vorbis) and .flac - ffmpeg's muxers silently drop chapters for those.
    /// </summary>
    public static readonly string[] SupportedExtensions = [".m4a", ".m4b", ".mp3", ".opus", ".mka"];

    /// <summary>Human-readable list of the supported extensions, e.g. ".m4a/.m4b/.mp3/.opus/.mka".</summary>
    public static string SupportedExtensionsText => string.Join("/", SupportedExtensions);

    /// <summary>Platform-specific name of this executable, for user-facing messages.</summary>
    public static string ExeName => OperatingSystem.IsWindows() ? "abchapterize.exe" : "abchapterize";

    /// <summary>Informational version of this build (from the csproj Version property).</summary>
    public static string Version => typeof(CliOptions).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>
    /// Auto-incrementing build counter baked into the assembly by the IncrementBuildNumber
    /// MSBuild target (see the csproj and BuildNumber.txt); null only if that target never ran
    /// (e.g. a host that loads these sources without going through a normal build). Shown only
    /// by --version, not in <see cref="Version"/> or <see cref="UsageText"/>.
    /// </summary>
    public static string? BuildNumber => GetAssemblyMetadata("BuildNumber");

    /// <summary>UTC timestamp of the build that produced this assembly, set by the same
    /// MSBuild target as <see cref="BuildNumber"/>; null under the same circumstances.</summary>
    public static string? BuildTimestamp => GetAssemblyMetadata("BuildTimestamp");

    /// <summary>Reads a value written into the assembly via [AssemblyMetadata(key, value)].</summary>
    private static string? GetAssemblyMetadata(string key) => typeof(CliOptions).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == key)?.Value;

    /// <summary>
    /// Parses and validates the raw command line arguments.
    /// </summary>
    /// <param name="args">Arguments as passed to Main.</param>
    /// <returns>A fully validated options instance, or null when --help / -? was requested.</returns>
    /// <exception cref="CliError">Thrown on any syntax or validation error.</exception>
    public static CliOptions? Parse(string[] args)
    {
        var o = new CliOptions();
        var i = 0;
        var targetArgs = new List<string>();

        string NextParam(string optName)
        {
            if (i + 1 >= args.Length)
                throw new CliError($"Option {optName} requires a parameter.");
            return args[++i];
        }

        // Every option must precede the target paths; once the first bare argument has been
        // seen, everything left on the command line is a target.
        void RejectTrailingOption(string optName)
        {
            if (targetArgs.Count > 0)
                throw new CliError(
                    $"Option {optName} must precede the file/directory arguments, which end the command line.");
        }

        for (; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--help" or "-?" or "/?")
                return null;

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                RejectTrailingOption(arg);
                if (!o.TryApplyFlag(arg) && !o.TryApplyValueOption(arg, () => NextParam(arg)))
                    throw new CliError($"Unknown option: {arg}");
            }
            else if (arg.StartsWith('-') && arg.Length > 1)
            {
                RejectTrailingOption(arg);
                // Short options; flags without parameters may be collapsed (e.g. -rb).
                var letters = arg[1..];
                for (var k = 0; k < letters.Length; k++)
                {
                    var c = letters[k];
                    if (c == '?')
                        return null;
                    if (!ShortOptions.TryGetValue(c, out var longName))
                        throw new CliError($"Unknown option: -{c}");
                    if (o.TryApplyFlag(longName))
                        continue;
                    if (k != letters.Length - 1)
                        throw new CliError($"Option -{c} takes a parameter and cannot be collapsed with other options ({arg}).");
                    o.TryApplyValueOption(longName, () => NextParam($"-{c}"));
                }
            }
            else
            {
                targetArgs.Add(arg);
            }
        }

        if (targetArgs.Count == 0)
            throw new CliError("No file or directory specified.");

        // Semantic validation.
        if (o.Revert && o.AnyProcessingOptionGiven)
            throw new CliError("--revert can only be combined with --recurse, --filter and the output options.");

        if (o.NoOp && o.FilterRegex == null && o.FilterExtensions == null)
            throw new CliError("--no-op requires --filter - its purpose is checking that a filter actually matches the intended files.");

        if (o.NoOp && (o.Revert || o.AnyProcessingOptionGiven))
            throw new CliError("--no-op can only be combined with --recurse, --filter and the output options.");

        if (o.Import && o.Export)
            throw new CliError("--import and --export cannot be combined.");

        if (o.Import && (o._langSet || o._phraseSet || o._prologuePhraseSet || o._epiloguePhraseSet || o._customMappings.Count > 0 || o.IgnoreChapterNumbers || o._modelSet || o._pass3ModelSet || o._jingleLenSet || o._minSilenceSet || o._earlyAbortSet || o._expectedStartSet || o._maxChapterNumberSet || o.MarkBeforeJingle || o.QuickMarks || o.TrailingScan || o.Verify))
            throw new CliError(
                "--import skips detection entirely, so --lang, --chapter-phrase, --prologue-phrase, " +
                "--epilogue-phrase, --custom, --custom-file, --ignore-chapter-numbers, --model, --pass3-model, " +
                "--mark-before-jingle, --quick-marks, --max-jingle-length, --min-silence-length, --early-abort, " +
                "--expected-start-chapter, --max-chapter-number, --trailing-scan and --verify " +
                "have no effect and cannot be combined with it.");

        // --ignore-chapter-numbers removes the chapter-number sequence detection is otherwise built
        // around, and with it every option that reasons in those numbers. Rejecting them outright
        // beats silently ignoring them: each one names an expectation about numbers this run will
        // never form an opinion about, so accepting it would promise something that cannot happen.
        // --chapter-phrase and --title stay legal - the phrase is still what is listened for and the
        // title word is still what the mark is called.
        if (o.IgnoreChapterNumbers && (o._pass3ModelSet || o._expectedStartSet ||
                                       o._maxChapterNumberSet || o.TrailingScan || o.Verify))
            throw new CliError(
                "--ignore-chapter-numbers forms no opinion about chapter numbers, so --pass3-model, " +
                "--expected-start-chapter, --max-chapter-number, --trailing-scan and --verify have " +
                "nothing to act on and cannot be combined with it.");

        if (o.MaxChapterNumber is { } cap && o.ExpectedStartChapter is { } start && cap < start)
            throw new CliError(
                $"--max-chapter-number ({cap}) is below --expected-start-chapter ({start}): " +
                "no chapter could ever be accepted.");

        if (o.Force && o.Verify)
            throw new CliError(
                "--force and --verify cannot be combined: --force always discards pre-existing " +
                "chapter markings, while --verify decides that based on whether they check out.");

        if (o.VerifyFailThreshold != null && !o.Verify)
            throw new CliError("--verify-threshold requires --verify.");

        if (o.SimpleMetadata && !o.Export && !o.Import)
            throw new CliError("--simple-metadata requires --export or --import.");

        o.Language = o.Language.ToLowerInvariant();
        if (o.Language != "auto" && !Regex.IsMatch(o.Language, "^[a-z]{2}$"))
            throw new CliError($"Invalid language code \"{o.Language}\": expected a two-letter code like \"en\", or \"auto\".");

        // Both selectors were validated where they were parsed. Defaulting the pass-3 model to the
        // main one also leaves Pass3ModelIsUpgrade false, so pass 2.5 stays off unless
        // --pass3-model actually names a heavier model.
        if (!o._pass3ModelSet)
            o.Pass3Model = o.Model;
        o.Pass3ModelIsUpgrade = ModelCatalog.ApproximateSizeBytes(o.Pass3Model)
                                > ModelCatalog.ApproximateSizeBytes(o.Model);

        if (o.ChapterPhrase.Length == 0)
            throw new CliError("The chapter phrase must not be empty.");

        // An empty prologue/epilogue phrase is how those are switched off, so only their titles
        // are required to be non-empty - a mark would otherwise be written with no title at all.
        if (o._prologueTitleSet && o.PrologueTitle.Length == 0)
            throw new CliError("The prologue title must not be empty (use --prologue-phrase \"\" to switch prologue detection off).");
        if (o._epilogueTitleSet && o.EpilogueTitle.Length == 0)
            throw new CliError("The epilogue title must not be empty (use --epilogue-phrase \"\" to switch epilogue detection off).");

        o.ResolveTargets(targetArgs);

        // With an explicit --lang this localizes the chapter phrase, title word and intro title
        // (unless given explicitly) for every file; with auto-detection it is only the English
        // fallback, and ChapterDetector resolves a fresh profile per file - see ResolveProfile.
        var fallbackLanguage = o.AutoLanguage ? "en" : o.Language;
        o.DefaultProfile = o.ResolveProfile(fallbackLanguage);
        o.ChapterPhrase = o.DefaultProfile.ChapterPhrase;
        o.Title = o.DefaultProfile.Title;
        o.IntroTitle = o.DefaultProfile.IntroTitle;
        o.PhraseRegex = o.DefaultProfile.PhraseRegex;
        o.PhraseHasNumberGroup = o.DefaultProfile.PhraseHasNumberGroup;
        // The named phrases keep only their compiled form in the profile, so the raw strings the
        // fingerprint and any user-facing echo read are localized here rather than copied back.
        var language = LanguageRegistry.For(fallbackLanguage);
        if (!o._prologuePhraseSet)
            o.ProloguePhrase = language.ProloguePhrase;
        if (!o._prologueTitleSet)
            o.PrologueTitle = language.PrologueTitle;
        if (!o._epiloguePhraseSet)
            o.EpiloguePhrase = language.EpiloguePhrase;
        if (!o._epilogueTitleSet)
            o.EpilogueTitle = language.EpilogueTitle;

        return o;
    }

    /// <summary>
    /// String comparer for whole paths, matching how the file system itself compares them:
    /// case-insensitively on Windows, case-sensitively elsewhere. Used to recognize the same
    /// target given twice, and by the file enumeration to keep an overlapping directory from
    /// yielding the same file more than once.
    /// </summary>
    public static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Validates the paths given at the end of the command line and fills <see cref="Targets"/>
    /// with them, dropping repetitions of a path already listed. Each must exist; a plain file
    /// must additionally have a supported extension (except under --revert, which is given the
    /// original name rather than the ".bak" one).
    /// </summary>
    /// <param name="targetArgs">The bare arguments collected during parsing, in order.</param>
    /// <exception cref="CliError">Thrown when a path does not exist, names an unsupported file
    /// type, or when --recurse was given without a single directory to descend into.</exception>
    private void ResolveTargets(List<string> targetArgs)
    {
        var seen = new HashSet<string>(PathComparer);
        foreach (var path in targetArgs)
        {
            if (!seen.Add(NormalizePath(path)))
                continue;
            if (File.Exists(path))
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (!Revert && !SupportedExtensions.Contains(ext))
                    throw new CliError(
                        $"Unsupported file type \"{ext}\" ({path}): only {SupportedExtensionsText} are supported.");
                _targets.Add(new Target(path, IsDirectory: false));
            }
            else if (Directory.Exists(path))
            {
                _targets.Add(new Target(path, IsDirectory: true));
            }
            else
            {
                throw new CliError($"File or directory not found: {path}");
            }
        }

        if (Recurse && !_targets.Any(t => t.IsDirectory))
            throw new CliError("--recurse can only be used with a directory, and none was given.");
    }

    /// <summary>Reduces a path to the absolute, separator-normalized form two spellings of the
    /// same target share, so they can be recognized as one.</summary>
    /// <param name="path">The path as given on the command line.</param>
    public static string NormalizePath(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// Applies a parameterless flag option given by its long name.
    /// </summary>
    /// <param name="name">Long option name, e.g. "--recurse".</param>
    /// <returns>True when <paramref name="name"/> is a known flag; false otherwise.</returns>
    private bool TryApplyFlag(string name)
    {
        switch (name)
        {
            case "--recurse": Recurse = true; return true;
            case "--backup": Backup = true; return true;
            case "--revert": Revert = true; return true;
            case "--no-op": NoOp = true; return true;
            case "--cpu-only": CpuOnly = true; return true;
            case "--force": Force = true; return true;
            case "--mark-before-jingle": MarkBeforeJingle = true; return true;
            case "--quick-marks": QuickMarks = true; return true;
            case "--trailing-scan": TrailingScan = true; return true;
            case "--quiet": Quiet = true; return true;
            case "--verbose": Verbose = true; return true;
            case "--verbose-transcripts": VerboseTranscripts = Verbose = true; return true;
            case "--no-bar": NoBar = true; return true;
            case "--summary": Summary = true; return true;
            case "--dry-run": DryRun = true; return true;
            case "--export": Export = true; return true;
            case "--import": Import = true; return true;
            case "--simple-metadata": SimpleMetadata = true; return true;
            case "--verify": Verify = true; return true;
            case "--ignore-progress": IgnoreProgress = true; return true;
            case "--ignore-chapter-numbers": IgnoreChapterNumbers = true; return true;
            default: return false;
        }
    }

    /// <summary>
    /// Applies an option that takes a parameter, given by its long name.
    /// </summary>
    /// <param name="name">Long option name, e.g. "--lang".</param>
    /// <param name="nextParam">Supplies the option's parameter; only invoked for known options.</param>
    /// <returns>True when <paramref name="name"/> is a known value option; false otherwise.</returns>
    /// <exception cref="CliError">Thrown when the parameter is missing or invalid.</exception>
    private bool TryApplyValueOption(string name, Func<string> nextParam)
    {
        switch (name)
        {
            case "--lang": Language = nextParam(); _langSet = true; return true;
            case "--chapter-phrase": ChapterPhrase = nextParam(); _phraseSet = true; return true;
            case "--model": Model = ParseModelSelector("--model", nextParam()); _modelSet = true; return true;
            case "--pass3-model": Pass3Model = ParseModelSelector("--pass3-model", nextParam()); _pass3ModelSet = true; return true;
            case "--max-chapters": MaxChapters = ParseNonNegativeInt("--max-chapters", nextParam()); _maxSet = true; return true;
            case "--max-chapter-number": MaxChapterNumber = ParseMaxChapterNumber(nextParam()); _maxChapterNumberSet = true; return true;
            case "--early-abort": EarlyAbortMinutes = ParseEarlyAbort(nextParam()); _earlyAbortSet = true; return true;
            case "--expected-start-chapter": ExpectedStartChapter = ParseExpectedStartChapter(nextParam()); _expectedStartSet = true; return true;
            case "--verify-threshold": VerifyFailThreshold = ParseNonNegativeInt("--verify-threshold", nextParam()); return true;
            case "--title": Title = nextParam(); _titleSet = true; return true;
            case "--intro-title": IntroTitle = nextParam(); _introSet = true; return true;
            case "--prologue-phrase": ProloguePhrase = nextParam(); _prologuePhraseSet = true; return true;
            case "--prologue-title": PrologueTitle = nextParam(); _prologueTitleSet = true; return true;
            case "--epilogue-phrase": EpiloguePhrase = nextParam(); _epiloguePhraseSet = true; return true;
            case "--epilogue-title": EpilogueTitle = nextParam(); _epilogueTitleSet = true; return true;
            case "--custom": _customMappings.AddRange(CustomMappingParser.ParseSpec(nextParam())); return true;
            case "--custom-file": _customMappings.AddRange(CustomMappingParser.ParseFile(nextParam())); return true;
            case "--filter": ParseFilter(nextParam()); return true;
            case "--max-jingle-length": (MaxJingleSeconds, AutoMaxJingle) = ParseJingleLength(nextParam()); _jingleLenSet = true; return true;
            case "--min-silence-length": (MinSilenceSeconds, AutoMinSilence) = ParseMinSilence(nextParam()); _minSilenceSet = true; return true;
            case "--jobs": Jobs = ParseJobs(nextParam()); return true;
            case "--log-file": LogFilePath = ParseLogFilePath(nextParam()); return true;
            default: return false;
        }
    }

    /// <summary>
    /// Parses a --filter parameter: either "/regexp/" (matched against the whole file path)
    /// or a comma-separated list of permissible file extensions like "mp3,m4b".
    /// </summary>
    /// <param name="value">The raw --filter parameter.</param>
    /// <exception cref="CliError">Thrown for an invalid regexp, an unsupported extension,
    /// or when a filter of the same kind was already given.</exception>
    private void ParseFilter(string value)
    {
        if (value.Length > 2 && value.StartsWith('/') && value.EndsWith('/'))
        {
            if (FilterRegex != null)
                throw new CliError("Only one --filter regexp can be given.");
            try
            {
                FilterRegex = new Regex(value[1..^1], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch (ArgumentException ex)
            {
                throw new CliError($"Invalid --filter regexp: {ex.Message}");
            }
            return;
        }

        if (FilterExtensions != null)
            throw new CliError("Only one --filter extension list can be given.");
        var extensions = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(e => (e.StartsWith('.') ? e : "." + e).ToLowerInvariant())
            .Distinct()
            .ToArray();
        if (extensions.Length == 0)
            throw new CliError("The --filter extension list must not be empty.");
        var unsupported = extensions.Where(e => !SupportedExtensions.Contains(e)).ToList();
        if (unsupported.Count > 0)
            throw new CliError(
                $"Unsupported extension(s) in --filter: {string.Join(", ", unsupported)} " +
                $"(supported: {SupportedExtensionsText}).");
        FilterExtensions = extensions;
    }

    /// <summary>
    /// Parses the --max-jingle-length parameter into 0 (no jingle expected - see
    /// <see cref="RunVadPrePass"/>), a number of seconds between 1 and 600, or "auto". "auto"
    /// resolves to the 45 s default ceiling plus <see cref="AutoMaxJingle"/> set, telling
    /// <see cref="ChapterDetector"/> to self-tighten the probe window as real jingle lengths
    /// are observed.
    /// </summary>
    private static (double Seconds, bool Auto) ParseJingleLength(string value)
    {
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return (45, true);
        if (!NumberCulture.TryParseDecimal(value, out var s) ||
            (s != 0 && (s < 1 || s > 600)))
            throw new CliError($"Invalid --max-jingle-length value \"{value}\": expected 0, seconds between 1 and 600, or \"auto\".");
        return (s, false);
    }

    /// <summary>
    /// Parses the --min-silence-length parameter into a positive number of seconds, or "auto".
    /// "auto" resolves to the 1.5 s floor plus <see cref="AutoMinSilence"/> set, telling
    /// <see cref="ChapterDetector"/> to self-tighten the threshold as chapters are found.
    /// </summary>
    private static (double Seconds, bool Auto) ParseMinSilence(string value)
    {
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return (1.5, true);
        if (!NumberCulture.TryParseDecimal(value, out var s) || s < 0.1 || s > 60)
            throw new CliError($"Invalid --min-silence-length value \"{value}\": expected seconds between 0.1 and 60, or \"auto\".");
        return (s, false);
    }

    /// <summary>Parses the --jobs parameter into a positive job count, or "auto".</summary>
    private static int? ParseJobs(string value)
    {
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!int.TryParse(value, out var n) || n < 1)
            throw new CliError($"Invalid --jobs value \"{value}\": expected a positive number or \"auto\".");
        return n;
    }

    /// <summary>
    /// Validates a --model/--pass3-model selector: one of the catalog's names, or
    /// <c>custom:&lt;path&gt;</c> naming a GGML file of the user's own (a fine-tune, a quantized
    /// build, or a model the catalog does not carry).
    /// <para>
    /// A custom path is expanded and made absolute here, for two reasons: two spellings of the same
    /// file must compare equal, because that string comparison is what decides whether pass 3 needs
    /// a second model loaded at all; and a leading <c>~</c> reaches this unexpanded on Windows,
    /// where the shell does not do it - the one place the tool has to do a shell's job to make the
    /// documented syntax work as typed.
    /// </para>
    /// </summary>
    /// <param name="optName">Long option name, for the error messages.</param>
    /// <param name="value">The raw parameter.</param>
    /// <exception cref="CliError">Thrown for an unknown model name, an empty custom path, or a
    /// custom path that does not name an existing file.</exception>
    private static string ParseModelSelector(string optName, string value)
    {
        if (!value.StartsWith(ModelCatalog.CustomPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = value.ToLowerInvariant();
            return ModelNames.Contains(name)
                ? name
                : throw new CliError($"Invalid model \"{value}\" for {optName}: expected one of " +
                                     $"{string.Join(", ", ModelNames)}, or \"{ModelCatalog.CustomPrefix}<path>\" " +
                                     "to use a GGML model file of your own.");
        }

        var path = ExpandHomeDirectory(value[ModelCatalog.CustomPrefix.Length..].Trim());
        if (path.Length == 0)
            throw new CliError($"{optName} \"{value}\" names no file: expected \"{ModelCatalog.CustomPrefix}<path>\".");
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            { throw new CliError($"{optName}: \"{path}\" is not a usable file path ({ex.Message})"); }
        if (!File.Exists(full))
            throw new CliError($"{optName}: the model file \"{full}\" does not exist.");
        return ModelCatalog.CustomPrefix + full;
    }

    /// <summary>Replaces a leading <c>~</c> with the user's home directory, leaving every other
    /// path untouched.</summary>
    /// <param name="path">The path as typed.</param>
    private static string ExpandHomeDirectory(string path)
    {
        if (path is not ['~', var second, ..] || (second != '/' && second != '\\'))
            return path == "~" ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : path;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
    }

    /// <summary>
    /// Validates a --log-file path. The file itself is only opened once the run starts, but a
    /// misspelled directory is caught here: a run that logs is usually one nobody watches, and
    /// noticing hours later that there is no log defeats the purpose of asking for one.
    /// </summary>
    /// <param name="value">The raw --log-file parameter.</param>
    /// <exception cref="CliError">Thrown for an empty path, a path naming an existing directory,
    /// or one whose parent directory does not exist.</exception>
    private static string ParseLogFilePath(string value)
    {
        if (value.Trim().Length == 0)
            throw new CliError("--log-file requires a file name.");
        if (Directory.Exists(value))
            throw new CliError($"--log-file \"{value}\" is a directory, not a file.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(value));
        if (directory != null && !Directory.Exists(directory))
            throw new CliError($"--log-file directory does not exist: {directory}");
        return value;
    }

    /// <summary>Parses a non-negative integer parameter, shared by --max-chapters and
    /// --verify-threshold.</summary>
    /// <param name="optName">Long option name, for the error message.</param>
    /// <param name="value">The raw parameter.</param>
    private static int ParseNonNegativeInt(string optName, string value)
    {
        if (!int.TryParse(value, out var n) || n < 0)
            throw new CliError($"Invalid {optName} value \"{value}\": expected a non-negative number.");
        return n;
    }

    /// <summary>Parses the --early-abort parameter into 0 (disables the feature) or a number of
    /// minutes between 0 and 1440 (24 hours).</summary>
    private static double ParseEarlyAbort(string value)
    {
        if (!NumberCulture.TryParseDecimal(value, out var n) || n < 0 || n > 1440)
            throw new CliError($"Invalid --early-abort value \"{value}\": expected 0 (disabled) or minutes between 0 and 1440.");
        return n;
    }

    /// <summary>Parses the --max-chapter-number parameter into a chapter number of 1 or higher.
    /// Zero is rejected rather than treated as "disabled": a cap of 0 would discard every chapter
    /// there is, which is never what anyone means.</summary>
    private static int ParseMaxChapterNumber(string value)
    {
        if (!int.TryParse(value, out var n) || n < 1)
            throw new CliError($"Invalid --max-chapter-number value \"{value}\": expected a chapter number of 1 or higher.");
        return n;
    }

    /// <summary>Parses the --expected-start-chapter parameter into a chapter number of 1 or higher.</summary>
    private static int ParseExpectedStartChapter(string value)
    {
        if (!int.TryParse(value, out var n) || n < 1)
            throw new CliError($"Invalid --expected-start-chapter value \"{value}\": expected a chapter number of 1 or higher.");
        return n;
    }

    /// <summary>
    /// Resolves the chapter phrase, title word and intro title for the given language: an
    /// explicit --chapter-phrase/--title/--intro-title always wins; otherwise the localized
    /// default for <paramref name="language"/> is used (English defaults for languages without
    /// an entry in <see cref="LanguageRegistry"/>). Called once at parse time for an explicit
    /// --lang (building <see cref="DefaultProfile"/>), and once per file by
    /// <see cref="ChapterDetector"/> when <see cref="AutoLanguage"/> is active.
    /// </summary>
    /// <param name="language">Two-letter language code (not "auto") to resolve defaults for.</param>
    public LanguageProfile ResolveProfile(string language)
    {
        var defaults = LanguageRegistry.For(language);
        var phrase = _phraseSet ? ChapterPhrase : defaults.ChapterPhrase;
        var title = _titleSet ? Title : defaults.ChapterTitle;
        var intro = _introSet ? IntroTitle : defaults.IntroTitle;
        var (regex, hasGroup) = CompilePhraseRegex(phrase);
        return new LanguageProfile(
            language, phrase, regex, hasGroup, title, intro, ResolveNamedPhrases(defaults));
    }

    /// <summary>
    /// Builds every non-numbered phrase for one language: the prologue and epilogue - each dropped
    /// entirely when its phrase resolves to the empty string, the opt-out spelling, since neither
    /// has a flag of its own and a mark nobody wants is better never detected than detected and
    /// discarded - followed by the <c>--custom</c> mappings in the order they were given. The custom
    /// ones are never localized and never dropped: they were written out by hand, in whatever
    /// language the user meant them in.
    /// </summary>
    /// <param name="defaults">The language's own defaults, for whichever option was not given.</param>
    private IReadOnlyList<NamedPhrase> ResolveNamedPhrases(ILanguage defaults)
    {
        var named = new List<NamedPhrase>();
        Add("prologue", _prologuePhraseSet ? ProloguePhrase : defaults.ProloguePhrase,
            _prologueTitleSet ? PrologueTitle : defaults.PrologueTitle, NamedPhraseScope.BeforeFirstChapter);
        Add("epilogue", _epiloguePhraseSet ? EpiloguePhrase : defaults.EpiloguePhrase,
            _epilogueTitleSet ? EpilogueTitle : defaults.EpilogueTitle, NamedPhraseScope.AfterFirstChapter);
        for (var i = 0; i < _customMappings.Count; i++)
            Add($"custom {i + 1}", _customMappings[i].Phrase, _customMappings[i].Title,
                NamedPhraseScope.Anywhere, repeatable: true);
        return named;

        void Add(string kind, string phrase, string markTitle, NamedPhraseScope scope,
            bool repeatable = false)
        {
            if (phrase.Length == 0)
                return;
            var regex = CompilePhraseRegex(phrase, kind).Regex;
            ValidateTitleGroupRefs(kind, phrase, markTitle, regex);
            named.Add(new NamedPhrase(kind, regex, markTitle, scope, repeatable));
        }
    }

    /// <summary>
    /// Rejects a title that references a capturing group its phrase does not have. Caught here, at
    /// parse time, rather than left to <see cref="Match.Result"/>: that would throw mid-book, after
    /// possibly hours of transcription, with an exception message naming neither the option nor the
    /// mapping at fault.
    /// </summary>
    /// <param name="kind">The phrase kind, for the error message.</param>
    /// <param name="phrase">The raw phrase, quoted in the error message so a long
    /// <c>--custom</c> list points at the mapping that is wrong.</param>
    /// <param name="title">The raw title template to check.</param>
    /// <param name="regex">The phrase's compiled expression, supplying the groups that do exist.</param>
    /// <exception cref="CliError">Thrown for a reference to a group that does not exist.</exception>
    private static void ValidateTitleGroupRefs(string kind, string phrase, string title, Regex regex)
    {
        // A phrase without capturing groups substitutes nothing at all, so "$5" in its title is a
        // price rather than a reference - the same rule NamedPhrase.ResolveTitle applies.
        if (regex.GetGroupNumbers().Length <= 1)
            return;

        foreach (var group in NamedPhrase.ReferencedGroups(title))
        {
            var exists = int.TryParse(group, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? regex.GetGroupNumbers().Contains(number)
                : regex.GetGroupNames().Contains(group);
            if (!exists)
                throw new CliError(
                    $"The {kind} title \"{title}\" references the capturing group \"{group}\", " +
                    $"which the phrase \"{phrase}\" does not have. " +
                    "Write \"$$\" for a literal dollar sign.");
        }
    }

    /// <summary>
    /// Compiles a chapter phrase into its matching regular expression. A phrase enclosed in
    /// slashes is compiled as-is (case-insensitive); anything else is escaped literally.
    /// </summary>
    /// <param name="chapterPhrase">The raw phrase or "/regexp/".</param>
    /// <param name="kind">What the phrase names, for the error message: "chapter" (the default),
    /// "prologue" or "epilogue". The named phrases share this compiler so all three accept the
    /// same "/regexp/" spelling; only <see cref="LanguageProfile.PhraseHasNumberGroup"/> is
    /// meaningless for them, since they parse no number.</param>
    /// <exception cref="CliError">Thrown for an invalid regexp.</exception>
    private static (Regex Regex, bool HasNumberGroup) CompilePhraseRegex(
        string chapterPhrase, string kind = "chapter")
    {
        if (chapterPhrase.Length > 2 && chapterPhrase.StartsWith('/') && chapterPhrase.EndsWith('/'))
        {
            Regex regex;
            try
            {
                regex = new Regex(chapterPhrase[1..^1], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch (ArgumentException ex)
            {
                throw new CliError($"Invalid {kind} phrase regexp: {ex.Message}");
            }
            return (regex, regex.GetGroupNumbers().Length > 1);
        }

        var pattern = Regex.Escape(chapterPhrase);
        return (new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), false);
    }

    /// <summary>OS-specific note about where ffmpeg/ffprobe are searched (part of the usage info).</summary>
    private static string FfmpegNote => OperatingSystem.IsWindows()
        ? """
          ffmpeg/ffprobe are required. They are searched in %FFMPEG_DIR%\bin and %FFMPEG_DIR%
          itself (highest priority; point FFMPEG_DIR at either), PATH, an "ffmpeg" folder in the current
          directory, next to abchapterize.exe or in the user profile, and common Program Files
          locations.
          """
        : """
          ffmpeg/ffprobe are required. They are searched in $FFMPEG_DIR and $FFMPEG_DIR/bin
          (highest priority; point FFMPEG_DIR at either), PATH, ./ffmpeg, ~/ffmpeg, /usr/bin,
          /usr/local/bin, /opt/ffmpeg, /snap/bin, ~/bin and ~/.local/bin.
          Install e.g. with: sudo apt install ffmpeg
          """;

    /// <summary>Comprehensive usage info printed on --help / -?. A command line error prints
    /// just a pointer to --help instead - see <see cref="Program.Main"/> - since this has grown
    /// too long to usefully repeat after every syntax mistake.</summary>
    public static string UsageText => $"""
        abchapterize {Version} - mark chapter starts in audiobooks using Whisper speech recognition
        Copyright (c) 2026 Jan O. Gretza - MIT license - written with Claude (Anthropic)
        Supported formats: {SupportedExtensionsText} (formats whose containers can hold chapter marks)

        Usage:
          abchapterize [options] <file-or-directory>...
          abchapterize -R|--revert [--recurse] [--filter <f>] <file-or-directory>...
          abchapterize -O|--no-op --filter <f> [--recurse] <file-or-directory>...
          abchapterize --help | -?

        Any number of files and directories may be given, mixed freely; they are processed in
        the order listed, and a file named twice (or already covered by a listed directory) is
        still only processed once.

        Options (must precede the file/directory arguments):

        File selection:
          -r, --recurse             Recursively descend into subdirectories (directories only).
          -F, --filter <filter>     Only process matching files. Either "/regexp/" - matched
                                    case-insensitively against the whole path of each file -
                                    or a comma-separated list of permissible file extensions,
                                    e.g. "mp3,m4b". One filter of each kind may be given;
                                    they also select which backups --revert restores.
          -f, --force               Discard pre-existing chapter markings. Without --force, files
                                    that already have chapter markings are skipped.
          -x, --max-chapters <n>    If a file has more than <n> pre-existing chapter markings,
                                    they are considered bogus and are discarded.

        Detection tuning:
          -l, --lang <code|auto>    Two-letter language hint for Whisper, or "auto" (the
                                    default): each file's language is detected from a short
                                    clip and used for that file, falling back to "en" when
                                    the detection is inconclusive. Chapter numbers
                                    transcribed as words - cardinals and ordinals, before or
                                    after the phrase ("chapter two", "Erstes Kapitel") - are
                                    understood in
                                    {string.Join(", ", LanguageRegistry.SupportedCodes)}; digits
                                    ("2.", "2nd", "2e") work in every language. For these
                                    languages, --lang also localizes the defaults of
                                    --chapter-phrase, --prologue-phrase, --epilogue-phrase,
                                    --title, --intro-title, --prologue-title and
                                    --epilogue-title (per-file with "auto").
          -c, --chapter-phrase <p>  Word/phrase that identifies a chapter start (default:
                                    /chapter/, localized by --lang).
                                    Enclose in slashes to use a regexp, e.g. "/chapter (\d+)/".
                                    The regexp may contain one capturing group "(\d+)" in place of
                                    the chapter number; otherwise the number is expected to follow
                                    the phrase. Matching is always case-insensitive.
          -p, --prologue-phrase <p> Word/phrase that identifies a prologue (default: /prolog/,
                                    localized by --lang). Accepts a "/regexp/" like
                                    --chapter-phrase, but parses no number: a match becomes one
                                    untitled-by-number mark carrying --prologue-title. Only
                                    accepted before the first chapter has been found, so a later
                                    mention in the prose cannot produce a second mark; if the
                                    phrase turns up more than once before then, the last
                                    occurrence wins (front matter often lists what is coming
                                    before the narrator announces it). Pass an empty string to
                                    switch prologue detection off.
          -g, --epilogue-phrase <p> Same for the epilogue (default: /epilog/, localized by
                                    --lang), mirrored: only accepted once at least one chapter
                                    has been found. Pass an empty string to switch it off.
          -u, --custom <mappings>   Extra phrase-to-title mappings, "phrase:title" pairs separated
                                    by semicolons, e.g.
                                      --custom "zwischenspiel:Zwischenspiel;/zeit[- ]?tafel/:Zeittafel"
                                    A phrase is a word or a "/regexp/" and parses no number; a
                                    match anywhere in the file becomes a mark titled after the
                                    colon, as often as the phrase occurs (up to
                                    {DetectionTuning.MaxCustomMarksPerFile} marks per file, after
                                    which the rest are dropped with a note). Only the first colon
                                    delimits, so a title may contain more of them; a "/regexp/"
                                    phrase ends at its closing slash instead, so a colon inside it
                                    is just a colon. Write "\;" for a semicolon inside a regexp.
                                    A title may reference the phrase's capturing groups by number
                                    ($1, $2) or by name, in .NET's own substitution syntax ("$$"
                                    writes a literal dollar sign). Repeat the option to add further
                                    mappings. Never localized - taken exactly as written.
          -U, --custom-file <path>  Read --custom mappings from a text file, one per line. Blank
                                    lines and lines starting with "#" are ignored, and semicolons
                                    need no escaping here since line breaks separate the mappings.
              --ignore-chapter-numbers
                                    Detect chapter announcements as usual, but form no opinion about
                                    the numbers in them. Every announcement heard becomes a mark
                                    where it is heard, keeping whatever number was spoken in its
                                    title, and no sequence gap is ever found or filled: passes 2.5
                                    and 3 never run and no file is tagged ".missing-marks". For
                                    books that restart their count per part, or number nothing at
                                    all. Cannot be combined with --pass3-model,
                                    --expected-start-chapter, --max-chapter-number, --trailing-scan
                                    or --verify.
          -m, --model <name>        Whisper model: tiny, base, small, medium, turbo or large
                                    (default: turbo), or "custom:<path>" for a GGML model file of
                                    your own, e.g. -m custom:~/models/my-finetune.bin. A custom
                                    file is used as it is: never downloaded, never checked against
                                    a known checksum, and only ranked against the built-in models
                                    by its size (which is what decides pass 2.5, see
                                    --pass3-model).
          -M, --pass3-model <name>  Whisper model for pass 3 (gap filling) only; same choices as
                                    --model (default: whatever --model is). Use a lighter model to
                                    speed pass 3 up, or "large" for one last best-effort attempt at
                                    the chapters the main model missed. A bigger model than
                                    --model's also enables pass 2.5, a quick re-probe of the gap
                                    with it before pass 3 transcribes the region in full. Loaded
                                    and downloaded lazily, only when a file actually reaches pass
                                    2.5 or pass 3.
          -C, --cpu-only            Force Whisper onto the CPU backend instead of the fastest
                                    available hardware acceleration. The Silero VAD pre-pass
                                    already always runs on CPU regardless of this option, so it
                                    only affects Whisper. Useful to leave a GPU free for other
                                    work, or to sidestep a flaky/unsupported GPU backend.
          -j, --mark-before-jingle  A short jingle may precede the chapter phrase; anchor the
                                    mark to it instead of the default fixed offset (see
                                    --max-jingle-length below). A silence scan and a
                                    voice-activity (VAD) pre-pass already run over the whole
                                    file regardless of this option, so jingles are found
                                    whether or not they are preceded by a silence: starting
                                    from wherever the mark would otherwise be placed, this
                                    walks backward through any leading silence and then the
                                    jingle's own music to the previous chapter's actual
                                    trailing narration, and marks right there. Two jingles
                                    playing back to back with an audible break between them
                                    stop the walk at that break, so the mark lands at the
                                    second jingle's start rather than in front of the first.
                                    When VAD finds no jingle - an ordinary in-narration
                                    pause - the mark is left exactly where it would otherwise
                                    be. Without this option, the mark is always placed 0.25
                                    seconds before the chapter phrase, no matter what
                                    precedes it.
          -Q, --quick-marks         [EXPERIMENTAL] Skip the refinement that normally verifies
                                    every mark, and take probing's own placement as final.
                                    Normally each mark is checked by re-transcribing the audio
                                    right at it: if the chapter phrase is heard there the mark
                                    stands (the common case - no cost beyond that one check),
                                    otherwise further candidate positions are checked the same
                                    way until the real onset is confirmed and the mark is
                                    corrected to it. Skipping all that is markedly faster,
                                    since the checks cost one or more extra Whisper
                                    transcriptions per chapter - most of all for chapters
                                    preceded by a jingle with several false-positive
                                    candidates - but the marks it leaves behind, while usually
                                    usable, may sit after the chapter phrase instead of before
                                    it. That can happen even together with
                                    --mark-before-jingle, whose backward walk can only be as
                                    good as the mark it starts from.
          -X, --max-jingle-length <seconds|auto>
                                    Maximum expected jingle duration (default, and ceiling with
                                    "auto": 45), or 0 if no jingle is expected at all. Above 0,
                                    audio is probed for this duration plus 5 seconds (for the
                                    phrase itself) after each silence; at 0, probing uses its
                                    original fixed window instead, and the VAD pre-pass is
                                    skipped entirely unless --mark-before-jingle still needs it.
                                    With "auto" (the default), starting from the second jingle
                                    mark found (the first is not necessarily representative),
                                    the probe window resizes to 1.25x the longest jingle
                                    actually observed so far plus margin, capped at the ceiling
                                    - narrower once a book's real jingle length is known, wider
                                    again if a longer one turns up. An explicit numeric value
                                    disables this and keeps the window fixed at that value
                                    throughout - useful if the jingle length is known and
                                    consistent, or for troubleshooting.
          -n, --min-silence-length <seconds|auto>
                                    Minimum silence duration that counts as a potential
                                    chapter break; the silence scan always uses this as its
                                    floor (default, and floor with "auto": 1.5). With "auto"
                                    (the default), starting from the second chapter mark
                                    found (the silence before the first mark is usually the
                                    intro/title silence and often longer, so it is not used
                                    to tighten), the probing threshold sits at 75% of the
                                    length of the shortest silence a mark has fallen into so
                                    far (raised once, then only ever lowered), and a sequence
                                    gap re-probes everything skipped since the last mark
                                    rather than resetting the threshold - fewer Whisper
                                    probes without a fixed guess. An explicit numeric value
                                    disables this and probes every such silence instead -
                                    useful if the breaks are known to vary a lot, or for
                                    troubleshooting.

        Detection safety nets:
          -a, --early-abort <minutes>
                                    Abort a file's detection outright, leaving it unchanged as
                                    if no chapters were found, once this many minutes of play
                                    time have been probed without a single chapter (default:
                                    60; 0 disables this and always probes the whole file). Only
                                    applies to a fresh, from-scratch detection run - never to a
                                    --verify gap recovery or a ".missing-marks" resume, which
                                    already have a confirmed chapter to build on.
          -e, --expected-start-chapter <n>
                                    The chapter number this book is expected to start at, for a
                                    split-book part that does not begin at chapter 1 (default:
                                    none - whatever Pass 2 finds first is accepted outright). If
                                    the first chapter found is numbered below <n>, the file is
                                    aborted and left unchanged; if numbered above <n>, the
                                    numbers in between are hunted via Pass 3 like any other gap,
                                    and the file is tagged with a ".missing-marks-..." suffix if
                                    any are still unresolved afterward. Only applies to a fresh,
                                    from-scratch detection run, same restriction as --early-abort.
          -L, --trailing-scan       Transcribe everything after the last chapter found, through to
                                    the end of the file, looking for further chapters (default:
                                    off). Pass 3 spots a missing chapter as a hole in the number
                                    sequence, which needs a known chapter on either side of it - so
                                    a chapter missing after the last one found is the one case
                                    nothing can notice, and the file is written out looking
                                    complete. This closes that hole, at the price of transcribing a
                                    whole final chapter's worth of audio on every file, whether or
                                    not anything is wrong: there are no expected numbers to satisfy
                                    here, so the scan can never stop early. Reach for it when a
                                    book's last chapter matters more than the run time. Does
                                    nothing when no chapter was found at all, or after an
                                    --early-abort or --expected-start-chapter abort.
          -N, --max-chapter-number <n>
                                    Highest chapter number this book plausibly has (default: none).
                                    A detected chapter numbered above <n> is discarded on the spot
                                    as a mishearing - without it, a single "chapter 510" heard in a
                                    twelve-chapter book leaves a 500-chapter hole for pass 3 to
                                    hunt through and a file tagged as missing all of them. Not to
                                    be confused with --max-chapters, which counts a file's
                                    pre-existing markings rather than the numbers heard in the
                                    audio.
          -V, --verify              Check pre-existing chapter markings against the audio
                                    instead of trusting them blindly: a short window around
                                    each marking is probed for the chapter phrase and the
                                    expected number. Markings that all check out are left
                                    alone; if any fails, they are discarded and the file goes
                                    through full detection, same as --force would. A file
                                    already rejected by --max-chapters skips verification and
                                    stays bogus. Cannot be combined with --force or --import.
          -h, --verify-threshold <n>
                                    Requires --verify. If more than <n> markings fail
                                    verification, the ones that did pass are no longer trusted
                                    as gap-recovery anchors either - the whole marking set is
                                    discarded and the file goes through full detection, the
                                    same fallback used when --verify confirms nothing at all.
                                    Without this option, even a single confirmed marking is
                                    enough to keep the rest as a gap-scoped recovery instead.

        Chapter titles:
          -t, --title <word>        Word used for chapter titles; the chapter number is appended
                                    (default: Chapter, localized by --lang).
          -i, --intro-title <word>  Title of the chapter mark covering the audio before the
                                    first detected mark, e.g. a prelude (default: Intro,
                                    localized by --lang, e.g. "Giriş" with --lang tr).
          -P, --prologue-title <word>
                                    Title written for a detected prologue (default: Prologue,
                                    localized by --lang, e.g. "Prolog" with --lang de).
          -G, --epilogue-title <word>
                                    Title written for a detected epilogue (default: Epilogue,
                                    localized by --lang).
                                    A --custom mark's title comes from its own mapping instead.

        Output & review:
          -d, --dry-run             Run detection but write nothing; print the chapters that
                                    would be written (timestamps, numbers, titles) instead.
          -E, --export              Also write detected chapters to a sidecar file next to
                                    the audio file (<file>.chapters.ffmeta by default, or
                                    <file>.chapters.txt with --simple-metadata), for manual
                                    review or correction. Combinable with --dry-run.
          -I, --import              Skip Whisper detection; write chapters from a previously
                                    exported sidecar file instead. Since nothing is detected,
                                    the detection options have no effect and are rejected:
                                    --lang, --chapter-phrase, --prologue-phrase,
                                    --epilogue-phrase, --model, --pass3-model,
                                    --mark-before-jingle, --quick-marks, --max-jingle-length,
                                    --min-silence-length, --early-abort,
                                    --expected-start-chapter, --max-chapter-number,
                                    --trailing-scan and --verify. Also mutually exclusive with
                                    --export, --revert and --no-op.
          -S, --simple-metadata     Use a plain "H:MM:SS.fff  Title" sidecar format instead
                                    of FFMETADATA for --export/--import. Requires one of them.

        File & backup management:
          -b, --backup              Keep the original file with the added suffix ".bak".
          -R, --revert              Restore backups: for every supported audio file with an
                                    added ".bak" suffix, delete the corresponding original and
                                    rename the .bak file back. Combinable with --recurse,
                                    --filter and the output options, but nothing else.
          -O, --no-op               List every file --filter (and --recurse) would select, then
                                    exit without loading a Whisper model, invoking ffmpeg or
                                    touching any file. A quick way to check that a --filter
                                    regexp or extension list actually matches the intended files
                                    before a real run. Requires --filter; combinable with
                                    --recurse and the output options, but nothing else.
              --ignore-progress     Start every listed directory over instead of resuming it.
                                    While a directory is being processed, the files finished so
                                    far are recorded in an ".abchapterize-progress" file inside
                                    it, which is deleted again as soon as that directory is
                                    done; a run cut short by Ctrl+C, a crash or a power loss
                                    therefore picks up where it left off when the same command
                                    is run again. Progress recorded under different options is
                                    discarded automatically, so this is only needed to redo
                                    files the very same command already finished. Unrelated to
                                    the ".missing-marks" resume of an individual file, which
                                    --force governs.

        Logging & display:
          -q, --quiet               Suppress per-file output; warnings and errors are still shown.
          -v, --verbose             Print processing details as timestamped log lines. Probe,
                                    gap and verify lines stop at their "<length>@<time>" header;
                                    use -T to also see the transcribed segments.
          -T, --verbose-transcripts Like --verbose, but also dumps every Whisper transcript's
                                    segments (to see exactly what the recognizer heard). Implies
                                    --verbose.
          -o, --log-file <path>     Write the log to a file instead of the console. Logging is
                                    switched on by this, so --verbose is not needed as well (add
                                    -T for the transcripts); the console keeps just its progress
                                    bar and per-file summaries, which the file receives too. An
                                    existing file is appended to, never overwritten.
          -B, --no-bar              Do not display progress bars; per-file summary lines are
                                    printed in the same timestamped format as --verbose logs.
          -s, --summary             Print a summary at the end: file counts, total and average
                                    processing time.

        Performance:
          -J, --jobs <n|auto>       Number of files processed concurrently (default: auto -
                                    adjusted between 1 and a hardware-derived ceiling based on
                                    live CPU load). "1" forces strictly sequential processing.

        Info:
          -?, --help                Show this help.
              --version             Show version information.

        Short options without parameters may be collapsed, e.g. "-rb" equals "-r -b".

        {FfmpegNote}
        """;
}
