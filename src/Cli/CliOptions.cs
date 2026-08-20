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
using ABChapterize.Errors;
using ABChapterize.Hooks;
using ABChapterize.Language;
using ABChapterize.Language.Phrases;
using ABChapterize.Transcription;
using ABChapterize.Ui;

namespace ABChapterize.Cli;

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
    /// Housekeeping instead of processing (--cleanup): undo the traces a previous run left in the
    /// selected folder(s) - leftover temporary files, ".bak" backups, ".debug.log" logs, batch
    /// progress files and ".missing-marks" name tags. See
    /// <see cref="ABChapterize.Processing.CleanupRunner"/> for exactly what goes and what does not.
    /// <para>
    /// Combines with <see cref="Revert"/>, which flips the backups from "delete" to "restore" and
    /// is the one combination needing no confirmation: no backup is thrown away, and the leftovers
    /// it still removes are none of them anything's only copy. On its own it
    /// requires <see cref="AssumeYes"/> or an interactive answer. Deliberately without a short
    /// form, as is --yes: the single letters belong to options people reach for daily, and neither
    /// of these should be quick to type.
    /// </para>
    /// </summary>
    public bool Cleanup { get; private set; }

    /// <summary>
    /// Answers --cleanup's confirmation prompt in advance (--yes), for a scripted or scheduled
    /// cleanup that has no console to be asked at. Meaningless anywhere else, and rejected there
    /// rather than ignored - an option that reads as "do not ask me anything" must not look like it
    /// covers more than it does.
    /// </summary>
    public bool AssumeYes { get; private set; }

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
    /// to the ".missing-marks" auto-resume of an individual file, which --force and
    /// --ignore-chapter-numbers govern between them.
    /// </summary>
    public bool IgnoreProgress { get; private set; }

    /// <summary>
    /// Shell command run for each file just before it is worked on (--run-before), or null.
    /// </summary>
    /// <remarks>
    /// Deliberately not run for a file this run then leaves alone: it fires after the file has been
    /// probed and the pre-existing-mark policy applied, so a book skipped for already carrying marks
    /// runs neither hook. That ordering is also why the file is probed a second time afterwards -
    /// a command that joins a split book or re-encodes it has changed everything the first probe
    /// established. A non-zero exit skips the file with a warning: the preparation this option
    /// exists for did not happen, and acting on a file in an unknown state is the one thing worse
    /// than not acting on it. See <see cref="ABChapterize.Hooks.CommandTemplate"/> for the
    /// placeholders and their quoting.
    /// </remarks>
    public CommandTemplate? RunBefore { get; private set; }

    /// <summary>
    /// Shell command run for each file once it is finished (--run-after), or null.
    /// </summary>
    /// <remarks>
    /// Runs only where <see cref="RunBefore"/> would have, and additionally not for a file left
    /// carrying a ".missing-marks" tag: such a file is expected back in a later run, so an archiving
    /// or clean-up command must not treat it as done. Its placeholders resolve against the name the
    /// file has by then, which is not the one it arrived under if the run added or dropped a tag.
    /// A non-zero exit is reported and nothing more - the file itself is already written, and there
    /// is nothing left to withhold from it.
    /// </remarks>
    public CommandTemplate? RunAfter { get; private set; }

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

    /// <summary>Raw epilogue phrase or "/regexp/" (--epilogue-phrase / -g), accepted only after the
    /// book's last chapter (see
    /// <see cref="ABChapterize.Detection.ChapterDetector.ResolveEpiloguePlacement"/>); empty switches epilogue
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
    /// How close a named mark (prologue, epilogue, <c>--custom</c>) may come to a chapter mark
    /// before the two are written as one entry (--named-mark-distance / -D, default 10 s; 0 switches
    /// the merging off and lets both stand wherever they fall).
    /// <para>
    /// Two marks a few seconds apart are two entries a player scrubs through, one of which lands the
    /// listener in the last sentence of what came before - a distinction the chapter list is not the
    /// place to make. Which of the two is the useful one is never in question, so the chapter keeps
    /// its position and the named mark keeps only its title, appended in brackets:
    /// "Chapter 10 (Interlude)". See <see cref="ABChapterize.Processing.FileProcessor.BuildChapters"/>.
    /// </para>
    /// </summary>
    public double NamedMarkDistanceSeconds { get; private set; } = 10;

    /// <summary>
    /// Whether the chapter numbers heard in an announcement are reasoned about
    /// (<c>--ignore-chapter-numbers</c>). The announcements themselves are still detected and still
    /// become marks either way; what this drops is everything built on the numbers forming a
    /// sequence: no gap is ever found or filled, so the Re-probe and Scan passes never run, no file is ever
    /// tagged ".missing-marks", and every option that states an expectation about the numbers is
    /// rejected outright rather than silently ignored. A parsed number still reaches the mark's
    /// title, it just no longer has to agree with its neighbours - which is the point, for a book
    /// that restarts its count per part or numbers nothing at all.
    /// </summary>
    public bool IgnoreChapterNumbers { get; private set; }

    /// <summary>
    /// Whisper model selector for the probing passes (--model / -m): tiny, base, small, medium,
    /// turbo or large, or <c>custom:&lt;path&gt;</c> for a GGML file of the user's own (see
    /// <see cref="ParseModelSelector"/>).
    /// <para>
    /// "small" rather than the largest thing that will run, because Probe asks a different question
    /// from Scan and the models are not ranked the same way for it. A probe is a short window - a
    /// jingle plus an announcement, ten to twenty-five seconds - and Whisper pads anything under
    /// <see cref="ABChapterize.Detection.DetectionTuning.WhisperChunkSeconds"/> out to a full mel
    /// chunk. The large models degenerate on that padding: the window comes back as one unpunctuated
    /// run-on segment with the announcement missing from it. Whisper's rankings hold for a long,
    /// well-framed transcription, which is what Scan does and <see cref="UpgradeModel"/> is for; they
    /// do not survive being asked five-second questions. The smaller model is also several times
    /// faster, so the default is not a trade at all.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the window-by-window measurement of a large model against a small one on short probes.
    /// <include file='../../notes/Cli/CliOptions.xml' path='doc/member[@name="Model"]/*' /></remarks>
    public string Model { get; private set; } = "small";

    /// <summary>
    /// Whisper model selector used for Scan (gap filling) only (--upgrade-model / -M). Scan
    /// transcribes long, naturally framed stretches of audio, where a heavier model really is the
    /// better recognizer - so it defaults to "turbo" while <see cref="Model"/> probes with "small",
    /// and that pairing is also what switches Re-probe on (see <see cref="UpgradeModelIsBetter"/>).
    /// The upgrade model is loaded (and downloaded) lazily, on first use - which may well be Probe
    /// rather than Scan, since <see cref="ChapterDetector"/>'s second-opinion path hands it
    /// Probe's unconfirmed marks and implausible chapter numbers as well.
    /// Takes a <c>custom:&lt;path&gt;</c> selector just like <see cref="Model"/>.
    /// <para>
    /// Naming --model alone re-points this at it, so <c>-m large</c> means large throughout rather
    /// than large probing and a lighter gap read, which would silently be a downgrade and switch off
    /// both Re-probe and the shifted re-read. Only the untouched default is the small/turbo pair; see
    /// the defaulting in <see cref="Parse"/>.
    /// </para>
    /// </summary>
    public string UpgradeModel { get; private set; } = "turbo";

    /// <summary>
    /// True when <see cref="UpgradeModel"/> is strictly more capable than <see cref="Model"/> - the
    /// "one last, best-effort attempt" direction of --upgrade-model rather than the "get the
    /// stragglers over with quickly" one. Gates Re-probe (see <c>ChapterDetector</c>'s
    /// <c>RunReprobeAsync</c>), which is only ever worth its extra probes when the model doing them
    /// can actually hear something the probe model could not; with an equal or lighter upgrade
    /// model it would just re-probe the same audio to the same conclusion, more slowly.
    /// <para>
    /// Ranking is by model size (see <see cref="ModelCatalog.ApproximateSizeBytes"/>), which
    /// reproduces <see cref="ModelNames"/>'s own order for the built-in models and is the only
    /// thing there is to go on for a <c>custom:</c> file. Settled once during
    /// <see cref="Parse"/> rather than re-derived per gap, so a custom file's size is read from
    /// disk exactly once.
    /// </para>
    /// </summary>
    public bool UpgradeModelIsBetter { get; private set; }

    /// <summary>
    /// True when <see cref="UpgradeModel"/> is strictly <em>less</em> capable than <see cref="Model"/>,
    /// i.e. the one direction that unambiguously says "get the stragglers over with quickly". Not the
    /// negation of <see cref="UpgradeModelIsBetter"/>: the default, where the two are the same model,
    /// is neither, and the distinction matters because the retries that are worth their time on an
    /// equal upgrade model are not worth it on a deliberately lighter one. Gates Scan's shifted
    /// re-scan (see <c>ChapterDetector</c>'s <c>RescanShiftedAsync</c>).
    /// </summary>
    public bool UpgradeModelIsWorse { get; private set; }

    /// <summary>
    /// Forces the CPU backend for Whisper instead of the fastest available hardware
    /// acceleration (--cpu-only / -C; see <see cref="WhisperTranscriber"/>). The Silero VAD
    /// pre-pass already always runs on CPU regardless of this option - the ONNX Runtime
    /// package this tool references has no GPU-capable execution provider to begin with - so
    /// this only changes Whisper's own backend selection. Useful to leave a GPU free for other
    /// work, or to sidestep a flaky/unsupported GPU backend.
    /// </summary>
    public bool CpuOnly { get; private set; }

    /// <summary>
    /// Which GPU Whisper should run on (--use-gpu), as a case-insensitive substring of the device
    /// name - "gtx", "uhd", "radeon" - or a bare index for the rare machine holding two identical
    /// cards. Null leaves the choice to
    /// <see cref="ABChapterize.Gpu.GpuSelector.Select"/>'s automatic preference.
    /// </summary>
    /// <remarks>
    /// A name rather than an index because an index is not stable: see
    /// <see cref="ABChapterize.Gpu.GpuDevice"/> for the measurement showing the same machine
    /// enumerating its two GPUs in opposite order depending on how the user logged in. Matching
    /// happens against the devices this very process enumerates, so it cannot go stale between
    /// listing and use.
    /// </remarks>
    public string? UseGpu { get; private set; }

    /// <summary>Discard pre-existing chapter marks instead of skipping the file (--force / -f).</summary>
    public bool Force { get; private set; }

    /// <summary>
    /// Maximum plausible number of pre-existing chapter marks (--max-chapters / -x).
    /// Files exceeding it get their marks discarded as bogus. Null when not specified.
    /// </summary>
    public int? MaxChapters { get; private set; }

    /// <summary>
    /// Highest chapter number considered plausible for this book (--max-chapter-number / -N).
    /// Null when the option was not given, which is not the same as no cap: see
    /// <see cref="EffectiveMaxChapterNumber"/>, which then falls back on
    /// <see cref="DefaultChapterCount"/>. Any chapter phrase whose parsed number exceeds it is
    /// discarded the moment it is found, before it can become a mark or widen the expected
    /// chapter sequence - a Whisper mishearing turning "chapter ten" into "chapter 510" otherwise
    /// leaves a 500-chapter "gap" for Scan to hunt through and a file tagged with a
    /// ".missing-marks" suffix listing all of them. Unrelated to <see cref="MaxChapters"/>, which
    /// counts a file's <i>pre-existing marks</i> rather than the numbers detection itself reads
    /// out of the audio.
    /// </summary>
    public int? MaxChapterNumber { get; private set; }

    /// <summary>
    /// Transcribes everything after the last chapter found, all the way to the end of the file,
    /// looking for further chapters. On by default; --no-trailing-scan switches it off.
    /// <para>
    /// This closes the one hole the ordinary Scan tail structurally cannot:
    /// <see cref="ABChapterize.Detection.GapPlanning.FindGaps"/> spots a missing chapter by
    /// finding a hole in the number sequence, which needs a known chapter on each side of it. A
    /// chapter missing <em>after</em> the last one found has nothing above it to compare against,
    /// so nothing notices it is gone and the file is written out looking complete.
    /// </para>
    /// <para>
    /// It was opt-in until 0.11.0, on the grounds that the cost is paid on every file whether or not
    /// anything is wrong - the region is a whole chapter long in a healthy book, and with no expected
    /// invisible from the outside: a real book lost its last five chapters to it and was written out
    /// looking complete, with no missing-number list and no ".missing-marks" tag, because nothing
    /// above the last mark existed to notice they were gone. A run that silently drops the end of a
    /// book is a worse default than a run that takes a few minutes longer, and the price is bounded
    /// and predictable - one final chapter's worth of transcription per file. --no-trailing-scan buys
    /// that time back for a library already known to be sound.
    /// </para>
    /// <para>
    /// Does nothing when no chapter was found at all (there is no "last chapter" to scan from, and
    /// transcribing an entire book on spec is not what this is for), nor after an
    /// --early-abort or --expected-start-chapter abort, which mean the file is being given up on
    /// rather than gap-filled, nor under --ignore-chapter-numbers, which skips Scan - the scan's
    /// own home - entirely. --chapter-count answers the same question far more cheaply where the
    /// number of chapters is known, so the two are worth reading together.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the book that lost its last five chapters with nothing to notice.
    /// <include file='../../notes/Cli/CliOptions.xml' path='doc/member[@name="TrailingScan"]/*' /></remarks>
    public bool TrailingScan { get; private set; } = true;

    /// <summary>
    /// Minutes of a file's play time Probe may probe without finding a single chapter before
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
    /// expectation: Probe's first find, whatever its number, is accepted outright and <see
    /// cref="ABChapterize.Detection.GapPlanning.FindGaps"/> never hunts below it. When set,
    /// <see cref="ABChapterize.Detection.GapPlanning.FindGaps"/> hunts the leading gap down to this
    /// number via Scan, exactly like an interior gap, regardless of which
    /// <see cref="ChapterDetector"/> entry point is running - so a ".missing-marks" resume of a
    /// leading gap keeps re-deriving the gap it was tagged with. If Scan cannot find them either,
    /// the file is tagged ".missing-marks-...", as an unresolved interior gap is. Separately, and
    /// only for a fresh run (the same restriction as <see cref="EarlyAbortMinutes"/>, for the same
    /// reason), the file is aborted and left unchanged if the very first chapter Probe finds is
    /// numbered below this value - almost certainly the wrong file, phrase or language rather than
    /// a gap worth hunting for.
    /// <para>
    /// Null does not always leave the question open: detecting a prologue implies a start at
    /// chapter 1 all by itself, since a book's prologue is in the file that holds its beginning.
    /// See <see cref="ABChapterize.Detection.GapPlanning.ExpectedStartFor"/>, and note that setting
    /// this option is also how that implication is overruled - a split part whose own prologue
    /// precedes chapter 12 is described by <c>-e 12</c>.
    /// </para>
    /// </summary>
    public int? ExpectedStartChapter { get; private set; }

    /// <summary>
    /// How many numbered chapters this book has, exactly (--chapter-count). Null (the default)
    /// means no expectation, which is where a plain run starts from: it accepts whatever the last
    /// number it hears turns out to be.
    /// <para>
    /// This is the cheap answer to the one hole in the detection pipeline that the number sequence
    /// cannot see by itself. A missing chapter is normally spotted as a hole in that sequence, which
    /// needs a known chapter on each side of it - so a chapter missing <em>after</em> the last one
    /// found has nothing above it to compare against. <see cref="TrailingScan"/> covers the same case
    /// by transcribing the whole tail on spec, and pays for it on every file. Told how many chapters
    /// there are, the run knows exactly which numbers are still owed, hunts only those, stops the
    /// moment they turn up, and does nothing at all once the count is reached.
    /// </para>
    /// <para>
    /// Restricted to a single file named on the command line, because it is a statement about one
    /// specific book and would be nonsense applied to every file of a directory. It is the count,
    /// not the highest number: with <see cref="ExpectedStartChapter"/> the two differ, and
    /// <see cref="LastExpectedChapter"/> is where they meet.
    /// </para>
    /// <para>
    /// What it deliberately does <em>not</em> do is end the search early once the count is reached.
    /// A book's numbered chapters are not necessarily the last thing in it - an epilogue, or any
    /// <c>--custom</c> phrase, may well follow - and stopping at the last number would silently cost
    /// those marks.
    /// </para>
    /// </summary>
    public int? ChapterCount { get; private set; }

    /// <summary>
    /// The highest chapter number this book is declared to have: <see cref="ChapterCount"/> counted
    /// from <see cref="ExpectedStartChapter"/> (1 when not given, the same anchor
    /// <see cref="ABChapterize.Detection.GapPlanning.MissingNumbersInGap"/> falls back to). Null
    /// when no count was given. This, rather than the count itself, is what detection reasons in -
    /// both as the cap on a plausible number and as the upper end of the trailing hunt.
    /// </summary>
    public int? LastExpectedChapter
        => ChapterCount is { } count ? (ExpectedStartChapter ?? 1) + count - 1 : null;

    /// <summary>
    /// How many chapters a book is assumed to have at most when nothing says otherwise. Counted
    /// from <see cref="ExpectedStartChapter"/>, so a file holding the second half of a split book
    /// gets the same allowance from wherever its numbering begins.
    /// <para>
    /// A cap is not a nicety. Without one, a single misread number sets the sequence ceiling and
    /// everything under it becomes a gap to hunt: front-matter years read as chapter numbers push the
    /// real chapter 1 below the sequence, split books into parts that do not exist, cost a prologue
    /// its scope and leave ".missing-marks" tags naming numbers no book ever had. 200 is comfortably
    /// past the longest books on record while excluding every year, price and house number a narrator
    /// reads out. A book that really runs longer says so with --max-chapter-number.
    /// </para>
    /// </summary>
    /// <remarks>Notes: the corpus run whose front-matter years became chapters, and the book lengths the cap is set against.
    /// <include file='../../notes/Cli/CliOptions.xml' path='doc/member[@name="DefaultChapterCount"]/*' /></remarks>
    public const int DefaultChapterCount = 200;

    /// <summary>
    /// The highest chapter number a match may carry and still be believed: whichever of
    /// <see cref="MaxChapterNumber"/> and <see cref="LastExpectedChapter"/> was given (never both -
    /// <see cref="Parse"/> rejects the combination as two answers to one question), else
    /// <see cref="DefaultChapterCount"/> chapters from where the numbering is expected to start.
    /// Never null: a run without a cap is what <see cref="DefaultChapterCount"/> exists to prevent.
    /// </summary>
    public int? EffectiveMaxChapterNumber
        => MaxChapterNumber ?? LastExpectedChapter ?? (ExpectedStartChapter ?? 1) + DefaultChapterCount - 1;

    /// <summary>
    /// Check pre-existing chapter marks against the audio instead of trusting them
    /// blindly (--verify / -V): a short window around each mark's own timestamp is probed
    /// with Whisper for the chapter phrase and the expected number
    /// (<see cref="ChapterDetector.VerifyExistingChaptersAsync"/>). Three outcomes, decided by
    /// <see cref="ABChapterize.Processing.FileProcessor.IsWholesaleFailure"/>: marks that all
    /// check out leave the file untouched, as a skip without --force would; some of them
    /// unconfirmed keeps the confirmed ones and gap-recovers only around the failures
    /// (<see cref="ChapterDetector.DetectGapsAsync"/>); and failures outnumbering confirmations -
    /// or nothing confirmed at all - leave the file completely alone with a warning, rather than
    /// discarding a mark set that was probably never one-per-numbered-chapter to begin with.
    /// A file with no checkable number in any mark is skipped as having nothing to verify.
    /// With --max-chapters, a file already over the threshold is still assumed bogus outright
    /// and skips verification entirely - --verify only decides borderline cases, it never
    /// makes a --max-chapters rejection stricter.
    /// </summary>
    public bool Verify { get; private set; }

    /// <summary>
    /// Lets --verify correct a mark instead of only reporting on it (--fix): where a mark's
    /// announcement is confirmed but sits a little away from where the mark is, the mark is
    /// moved onto it and the file rewritten. Requires --verify, and does nothing on its own.
    /// <para>
    /// Only ever a nudge, and deliberately so - see
    /// <see cref="DetectionTuning.VerifyFixMinShiftSeconds"/> and
    /// <see cref="DetectionTuning.VerifyFixMaxShiftSeconds"/> for the two bounds. A mark that
    /// could not be confirmed at all is not "fixed" by this; it goes to the gap recovery --verify
    /// already runs, exactly as before.
    /// </para>
    /// </summary>
    public bool Fix { get; private set; }

    /// <summary>
    /// Maximum number of unconfirmed --verify marks a file may have and still get the
    /// gap-scoped recovery <see cref="ChapterDetector.DetectGapsAsync"/> normally runs around
    /// them (--verify-threshold / -h). Above it the file is left completely alone with a warning
    /// instead, on the reasoning
    /// <see cref="ABChapterize.Processing.FileProcessor.IsWholesaleFailure"/> spells out: marks
    /// that fail in bulk are more likely to mean something other than one numbered chapter each
    /// than to be a book whose every mark drifted. Null (the default) leaves that judgement to the
    /// ratio rule there; requires --verify.
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
    /// Forces the jingle-first shape of Probe on a file that would not qualify for it by itself
    /// (--jingle-first): the music is read end to end first, and the pauses only where the chapter
    /// sequence still has a hole plus the head and tail of the file. See
    /// <see cref="ABChapterize.Detection.JingleFirstScan"/> for what qualifies a file automatically
    /// and for what the automatic gate is protecting.
    /// <para>
    /// A force switch and not an on/off pair, because the two answers are not symmetric: a book that
    /// meets the gate has been measured to gain from the shape, while a book that does not is an
    /// experiment somebody wants to run. If the shape turns out to need switching <em>off</em> on a
    /// qualifying book, that is a defect in the gate rather than an option the user should have to
    /// find.
    /// </para>
    /// <para><b>Experimental.</b></para>
    /// </summary>
    public bool JingleFirst { get; private set; }

    /// <summary>
    /// How far before the announcement's onset a mark is placed, in seconds (--mark-lead, default
    /// <see cref="DetectionTuning.DefaultMarkLeadSeconds"/>).
    /// </summary>
    /// <remarks>
    /// A matter of taste rather than accuracy, which is why it is an option and not a tuning
    /// constant: the refinement pins the onset to a tenth of a second either way, but how much
    /// silence one wants to hear before the narrator starts differs from listener to listener - and
    /// a lead too short can clip a plosive onset outright. Applies under --mark-before-jingle too:
    /// in full for a chapter whose walk finds narration where the jingle would be, and otherwise as
    /// the back-off into the pause the walk came to rest in, clamped at that pause's own start - see
    /// <see cref="ABChapterize.Detection.JingleGeometry.ComputeMarkBeforeJingle"/>.
    /// </remarks>
    public double MarkLeadSeconds { get; private set; } = DetectionTuning.DefaultMarkLeadSeconds;

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
    /// Whether a probe window that heard a chapter number without the word beside it may be read
    /// again through the bundled speech denoiser (--no-denoise switches it off). On by default, and
    /// on a book with ordinary audio it never runs at all: the file has to sound dull enough to
    /// pass <see cref="Audio.AudioFidelity.Threshold"/> before a window is even allowed to ask, and
    /// then a window has to fail in that particular way.
    /// <para>
    /// Kept as an opt-out rather than an opt-in because the failure it repairs is invisible from
    /// outside - a chapter that was never marked, with nothing in the output to say a heading was
    /// heard and discarded - so a user has no way of knowing they should have asked for it. The
    /// switch exists for reproducing an older run and for the case where the extra decode is not
    /// wanted.
    /// </para>
    /// </summary>
    public bool Denoise { get; private set; } = true;

    /// <summary>
    /// Minimum silence duration in seconds that counts as a potential chapter break
    /// (--min-silence-length / -n). Every such silence triggers a Whisper probe, so an
    /// explicit higher value can reduce the number of probes further still. With an explicit
    /// value this is the whole story; with the default "auto" it is where probing starts, and
    /// the threshold moves from there - see <see cref="AutoMinSilence"/> for the adaptation and
    /// <see cref="AdaptiveFloorSeconds"/> for how far down it reaches.
    /// </summary>
    public double MinSilenceSeconds { get; private set; } = 1.5;

    /// <summary>
    /// Level below which Analyze's scan counts audio as silence, in dBFS (--noise-floor). Always
    /// the level actually scanned with; see <see cref="AutoNoiseFloor"/> for the default, where
    /// this holds the starting point a per-file measurement may move.
    /// </summary>
    public double NoiseFloorDb { get; private set; } = DetectionTuning.DefaultSilenceNoiseDb;

    /// <summary>
    /// True (the default) unless --noise-floor was given an explicit level: each file's own
    /// levels are measured before Analyze and <see cref="NoiseFloorDb"/> is moved only where the
    /// default would fall outside that master's gap between room tone and narration - see
    /// <see cref="ABChapterize.Detection.SilenceThresholdProbe"/>, which also explains why the
    /// answer for an ordinary book is the default unchanged. "auto" can also be given explicitly
    /// for clarity.
    /// </summary>
    public bool AutoNoiseFloor { get; private set; } = true;

    /// <summary>
    /// True (the default) unless --min-silence-length was given an explicit numeric value:
    /// <see cref="MinSilenceSeconds"/> is where probing <em>starts</em>, and
    /// <see cref="ChapterDetector"/> then moves the threshold after each chapter mark - up, to skip
    /// silences this book's own breaks say are too short to be one, or back down as far as
    /// <see cref="AdaptiveFloorSeconds"/> when they turn out shorter than the starting demand.
    /// "auto" can also be given explicitly for clarity.
    /// </summary>
    public bool AutoMinSilence { get; private set; } = true;

    /// <summary>
    /// How short a pause this run may end up treating as a chapter break. With an explicit
    /// --min-silence-length that is the value given and nothing shorter is ever probed; with
    /// "auto" the threshold may settle below <see cref="MinSilenceSeconds"/>, down to
    /// <see cref="DetectionTuning.AdaptiveSilenceFloorSeconds"/>, and what it reaches below the
    /// starting demand is swept for separately rather than probed inline - see
    /// <see cref="ChapterDetector.SweepAdaptiveSubFloorAsync"/> for why the two cannot be the same
    /// list. <see cref="Math.Min(double, double)"/> rather than the constant outright so a starting
    /// demand already below the floor stays the binding one.
    /// </summary>
    public double AdaptiveFloorSeconds
        => AutoMinSilence
            ? Math.Min(MinSilenceSeconds, DetectionTuning.AdaptiveSilenceFloorSeconds)
            : MinSilenceSeconds;

    /// <summary>
    /// Whether a long enough silence is by itself a reason to probe - true for every value of
    /// <see cref="MinSilenceSeconds"/> except 0, which says to probe only where the
    /// voice-activity pre-pass found a jingle. For a book whose chapters all open with one, that
    /// removes the hundreds of ordinary in-narration pauses each of which otherwise costs a Whisper
    /// probe; for a book whose chapters do not, it removes the only way of finding them, which is
    /// why it is off by default.
    /// <para>
    /// Only <em>probing</em> is affected. The silence scan itself still runs and still keeps
    /// everything down to <see cref="StoredSilenceFloorSeconds"/>: window seams, transcript
    /// timestamps, jingle anchors and mark refinement all read that list, and switching it off
    /// would degrade every mark rather than merely finding fewer of them.
    /// </para>
    /// </summary>
    public bool ProbeSilences => MinSilenceSeconds > 0;

    /// <summary>
    /// The shortest silence Analyze retains - normally <see cref="DetectionTuning.MinStoredSilenceSeconds"/>,
    /// or <see cref="MinSilenceSeconds"/> where the user asked for something shorter still. Never
    /// follows <see cref="MinSilenceSeconds"/> down to 0: a scan with no minimum length reports
    /// every sample under the threshold as its own silence, and the list this floor governs is one
    /// mark placement depends on rather than one probing does.
    /// </summary>
    public double StoredSilenceFloorSeconds
        => ProbeSilences
            ? Math.Min(MinSilenceSeconds, DetectionTuning.MinStoredSilenceSeconds)
            : DetectionTuning.MinStoredSilenceSeconds;

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
    /// Whether output is colorized (--color). This covers the progress bar and the closing
    /// --summary block; log lines, per-file summaries and the banner stay plain, and a --log-file
    /// receives plain text regardless.
    /// </summary>
    public ColorMode Color { get; private set; } = ColorMode.Auto;

    /// <summary>
    /// Path of the file the log stream is written to (--log-file / -o), or null for no log file.
    /// Asking for one turns logging on by itself - there would be nothing to write otherwise - and
    /// sends the whole stream to the file rather than the console, so the console keeps its
    /// progress bar and per-file summaries alone. <see cref="VerboseTranscripts"/> still decides
    /// how much detail the stream carries.
    /// </summary>
    public string? LogFilePath { get; private set; }

    /// <summary>
    /// Write a per-file troubleshooting log next to each processed file (--debug): everything the
    /// ordinary log stream carries, plus the raw material behind it - the full silence list, the VAD
    /// pre-pass's speech segments and non-speech regions, and every Whisper transcript segment by
    /// segment, including the mark-refinement probes nothing else ever shows.
    /// <para>
    /// Deliberately absent from --help and from the README's option tables, and documented at length
    /// in the manual and the sources only. It is the option one is <em>told</em> to use when a mark
    /// comes out wrong, not one to pick off a list: it writes a file per audiobook, sizeable on a
    /// long one, and its output is meaningful only to someone reading this code.
    /// </para>
    /// </summary>
    public bool Debug { get; private set; }

    /// <summary>True when log lines are produced at all, for whichever destination
    /// (<see cref="Verbose"/>, <see cref="LogFilePath"/> or <see cref="Debug"/>) asked for them.
    /// Call sites that only build a log message when someone is listening test this rather than
    /// <see cref="Verbose"/>.</summary>
    public bool LoggingEnabled => Verbose || LogFilePath != null || Debug;

    /// <summary>Print a run summary with file counts, timings and the per-file listings of
    /// <see cref="ABChapterize.Processing.RunOutcomes"/> at the end (--summary / -s).</summary>
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
    /// Worker threads for the voice-activity pre-pass (--vad-threads), or null for "auto".
    /// Read through <see cref="EffectiveVadThreads"/>, which resolves what "auto" means.
    /// </summary>
    public int? VadThreads { get; private set; }

    /// <summary>
    /// CPU threads for Whisper transcription (--whisper-threads), or null for "auto".
    /// Read through <see cref="EffectiveWhisperThreads"/>, which resolves what "auto" means.
    /// </summary>
    public int? WhisperThreads { get; private set; }

    /// <summary>
    /// How many blocks of audio the voice-activity pre-pass classifies at once (see
    /// <see cref="ABChapterize.Vad.SileroVadDetector"/>): <see cref="VadThreads"/>, or one per
    /// physical CPU core.
    /// </summary>
    public int EffectiveVadThreads => VadThreads ?? ProcessorTopology.PhysicalCoreCount;

    /// <summary>
    /// How many CPU threads Whisper transcription is given: <see cref="WhisperThreads"/>, or one per
    /// physical CPU core.
    /// </summary>
    public int EffectiveWhisperThreads => WhisperThreads ?? ProcessorTopology.PhysicalCoreCount;

    /// <summary>Word used to build chapter titles; the chapter number is appended (--chapter-title / -t, default "Chapter", localized by --lang).</summary>
    public string Title { get; private set; } = "Chapter";

    /// <summary>
    /// Word used to build the part prefix of a file whose chapter numbering restarts partway
    /// through (--part-title, default "Part", localized by --lang). Only ever written for such a
    /// file; see <see cref="LanguageProfile.ChapterTitleFor"/>.
    /// </summary>
    public string PartTitle { get; private set; } = "Part";

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
    /// The profile resolved at parse time: for an explicit --lang, this is used for every file;
    /// with <see cref="AutoLanguage"/>, it is the English fallback profile used only when a
    /// file's own detection is inconclusive or skipped - see <see cref="ResolveProfile"/>, which
    /// <see cref="ChapterDetector"/> calls per file instead when auto-detecting.
    /// </summary>
    public LanguageProfile DefaultProfile { get; private set; } = null!;

    /// <summary>
    /// The accepted --model/--upgrade-model selectors, in ascending order of transcription quality -
    /// an order <see cref="UpgradeModelIsBetter"/> reads directly. Taken from
    /// <see cref="ModelCatalog.BuiltInNames"/> rather than restated here: a name this accepts but
    /// the catalog does not know turns a malformed command line into an operational error hours
    /// into a run, and one the catalog knows but this rejects makes a bundled model unselectable.
    /// </summary>
    private static readonly string[] ModelNames = ModelCatalog.BuiltInNames;

    /// <summary>Maps every short option letter to its long option name.</summary>
    private static readonly Dictionary<char, string> ShortOptions = new()
    {
        ['r'] = "--recurse", ['b'] = "--backup", ['f'] = "--force", ['j'] = "--mark-before-jingle",
        ['Q'] = "--quick-marks", ['k'] = "--mark-lead",
        ['q'] = "--quiet", ['v'] = "--verbose", ['T'] = "--verbose-transcripts", ['s'] = "--summary",
        ['l'] = "--lang", ['c'] = "--chapter-phrase", ['m'] = "--model", ['M'] = "--upgrade-model",
        ['x'] = "--max-chapters", ['N'] = "--max-chapter-number",
        // -L still maps to the removed --trailing-scan rather than to --no-trailing-scan, so a
        // script carrying it gets the migration error instead of silently doing the opposite of
        // what it asked for. The letter is free to be reused once that error is dropped.
        ['a'] = "--early-abort", ['e'] = "--expected-start-chapter", ['L'] = "--trailing-scan",
        ['F'] = "--filter",
        ['n'] = "--min-silence-length", ['t'] = "--chapter-title", ['i'] = "--intro-title",
        ['p'] = "--prologue-phrase", ['P'] = "--prologue-title",
        ['g'] = "--epilogue-phrase", ['G'] = "--epilogue-title",
        ['u'] = "--custom", ['U'] = "--custom-file", ['D'] = "--named-mark-distance",
        ['R'] = "--revert", ['B'] = "--no-bar", ['d'] = "--dry-run",
        ['E'] = "--export", ['I'] = "--import", ['S'] = "--simple-metadata",
        ['V'] = "--verify", ['h'] = "--verify-threshold", ['C'] = "--cpu-only", ['O'] = "--no-op",
        ['o'] = "--log-file",
    };

    // Tracks which value options were given explicitly, for semantic validation and
    // for applying the --lang-dependent defaults only when the user did not choose.
    private bool _langSet, _modelSet, _upgradeModelSet, _maxSet, _maxChapterNumberSet, _minSilenceSet, _earlyAbortSet, _expectedStartSet, _markLeadSet, _chapterCountSet, _noiseFloorSet, _namedMarkDistanceSet;

    // What --set: changed, already applied to the constants themselves by the time this instance
    // exists (see TuningOverrides). Kept only to be reported and fingerprinted: a run under
    // different tuning is a different command, so it must not resume one recorded under the
    // defaults, and a debug log that does not say which numbers it ran with is unreadable later.
    private IReadOnlyList<string> _tuningOverrides = [];

    /// <summary>The <c>--set:</c> overrides this run applied, "Class.Constant=value" each, in the
    /// order given; empty when the run uses the tuning it was built with. Named for what it holds
    /// rather than after <see cref="Cli.TuningOverrides"/>, which it would otherwise shadow inside
    /// this class.</summary>
    public IReadOnlyList<string> TuningChanges => _tuningOverrides;

    // The title options, each holding what was given for every language, per language, or nothing at
    // all when the option was not given - in which case the language's own default applies. Null
    // therefore also stands in for the "was it set" flags above, there being nothing left worth
    // tracking separately.
    private LocalizedOption? _titleSpec, _partTitleSpec, _introSpec;
    private LocalizedOption? _prologueTitleSpec, _epilogueTitleSpec;

    // The phrase options. A phrase is a list of alternatives rather than one value per language,
    // which is why these are not LocalizedOptions: naming French does not replace what every other
    // language listens for, it adds to it.
    private PhraseSpec? _phraseSpec, _prologuePhraseSpec, _epiloguePhraseSpec;

    /// <summary>
    /// True when <c>--chapter-phrase none</c> was given for any language - the bare-number wording
    /// of <see cref="LanguageProfile.BareNumberAnnouncements"/>. Asked of the whole spec rather than
    /// of the language this run happens to resolve to, because a mixed-language batch may only reach
    /// the language that named it on its two-hundredth file, and a rule that fires there and not on
    /// file one is worse than one that fires on the command line.
    /// </summary>
    private bool UsesBareNumberPhrase
        => _phraseSpec is { } spec &&
           spec.Bodies.Any(p => p.Equals(PhraseCompiler.BareNumberWord, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when any option was given that only means something for a run that actually detects
    /// or writes chapters - i.e. anything beyond file selection (--recurse/--filter) and the
    /// output/logging options. <see cref="Revert"/> and <see cref="NoOp"/> do neither, so both
    /// reject all of these instead of silently ignoring them; sharing one list is what keeps the
    /// two checks, and the promise their error messages make, from drifting apart as options are
    /// added.
    /// </summary>
    private bool AnyProcessingOptionGiven
        => Backup || Force || CpuOnly || MarkBeforeJingle || QuickMarks || !TrailingScan || !Denoise || DryRun
           || JingleFirst
           || Export || Import || SimpleMetadata || Verify || Fix || IgnoreProgress
           || UseGpu != null || VadThreads != null || WhisperThreads != null
           || _langSet || _modelSet || _upgradeModelSet || _maxSet || _maxChapterNumberSet
           || _minSilenceSet || _earlyAbortSet || _markLeadSet || _expectedStartSet
           || _chapterCountSet || _noiseFloorSet || _namedMarkDistanceSet
           || _phraseSpec != null || _titleSpec != null || _partTitleSpec != null || _introSpec != null
           || _prologuePhraseSpec != null || _prologueTitleSpec != null
           || _epiloguePhraseSpec != null || _epilogueTitleSpec != null
           || _customMappings.Count > 0 || IgnoreChapterNumbers
           || RunBefore != null || RunAfter != null;

    /// <summary>
    /// Short hash of every option that changes what a run does to a file - the language and
    /// phrase, the models, the detection tuning and safety nets, the file selection, the output
    /// mode. The list below is an allowlist, so an option is exempt by not being named in it:
    /// everything that only changes what the run <i>looks like</i> (--quiet, --verbose,
    /// --verbose-transcripts, --log-file, --debug, --no-bar, --color, --summary) or how fast it gets there
    /// (--vad-threads, --whisper-threads, --cpu-only, --use-gpu) stays out, so adding or dropping
    /// one of those on an interrupted run's command line still resumes it.
    /// <para>
    /// The hardware options are the interesting case: a run on a different device - or with a
    /// different thread count, which changes the order floating-point reductions accumulate in - may
    /// transcribe with slightly different results, so the marks are not bit-identical in
    /// principle. That is treated as noise, not as a different command - someone who moves a
    /// stalled batch to another machine, or falls back to --cpu-only after a driver failure, wants
    /// the remaining files done, not the finished ones redone.
    /// </para>
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
                $"lang={Language}", $"phrase={ChapterPhrase}", $"title={Title}", $"parttitle={PartTitle}",
                $"intro={IntroTitle}",
                $"prologue={ProloguePhrase}/{PrologueTitle}", $"epilogue={EpiloguePhrase}/{EpilogueTitle}",
                $"nameddistance={NamedMarkDistanceSeconds}",
                $"ignorenumbers={IgnoreChapterNumbers}",
                $"custom={string.Join('|', _customMappings.Select(m => $"{m.Tag}:{m.Phrase}=>{m.Title}"))}",
                $"model={Model}", $"upgrade={UpgradeModel}",
                $"maxchapters={MaxChapters}", $"maxnumber={MaxChapterNumber}",
                $"earlyabort={EarlyAbortMinutes}", $"expectedstart={ExpectedStartChapter}",
                $"chaptercount={ChapterCount}",
                $"trailingscan={TrailingScan}", $"verify={Verify}/{Fix}", $"verifythreshold={VerifyFailThreshold}",
                $"jingle={MarkBeforeJingle}", $"quickmarks={QuickMarks}", $"marklead={MarkLeadSeconds}",
                $"jinglefirst={JingleFirst}",
                $"denoise={Denoise}",
                $"minsilence={MinSilenceSeconds}/{AutoMinSilence}",
                $"noisefloor={NoiseFloorDb}/{AutoNoiseFloor}",
                $"filter={FilterRegex?.ToString()}", $"extensions={string.Join(',', EffectiveExtensions)}",
                $"import={Import}", $"export={Export}", $"simple={SimpleMetadata}",
                $"runbefore={RunBefore?.Raw}", $"runafter={RunAfter?.Raw}",
                $"set={string.Join('|', _tuningOverrides)}",
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
        // Before anything reads args: a --config file's options are the same options, so they are
        // spliced in and then parsed by the code below like any others (see ConfigFile.Expand).
        args = ConfigFile.Expand(args);
        // And before the instance exists, because its own defaults are read out of the very
        // constants --set: writes (see TuningOverrides, which also restores them first).
        var overrides = TuningOverrides.Apply(args);
        var o = new CliOptions { _tuningOverrides = overrides };
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
                // Already applied above, before this instance existed. Recognized here only so it
                // is subject to the ordering rule and is not reported as unknown.
                if (arg.StartsWith(TuningOverrides.Option, StringComparison.Ordinal))
                    continue;
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
                    // Checked like the long form's, although every letter in ShortOptions maps to a
                    // name one of the two switches handles: a letter added to that table without a
                    // handler would otherwise be accepted and then do nothing at all.
                    if (!o.TryApplyValueOption(longName, () => NextParam($"-{c}")))
                        throw new CliError($"Unknown option: -{c}");
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
            throw new CliError("--revert can only be combined with --cleanup, --recurse, --filter and the output options.");

        if (o.Cleanup && o.AnyProcessingOptionGiven)
            throw new CliError("--cleanup can only be combined with --revert, --yes, --recurse, --filter and the output options.");

        // --yes reads as a blanket "stop asking me things", so letting it stand where nothing asks
        // anything would leave the user believing they had covered a prompt they had not.
        if (o.AssumeYes && !o.Cleanup)
            throw new CliError("--yes answers --cleanup's confirmation prompt and has no meaning without it.");

        if (o.NoOp && o.FilterRegex == null && o.FilterExtensions == null)
            throw new CliError("--no-op requires --filter - its purpose is checking that a filter actually matches the intended files.");

        if (o.NoOp && (o.Revert || o.Cleanup || o.AnyProcessingOptionGiven))
            throw new CliError("--no-op can only be combined with --recurse, --filter and the output options.");

        if (o.Import && o.Export)
            throw new CliError("--import and --export cannot be combined.");

        if (o.UseGpu != null && o.CpuOnly)
            throw new CliError("--use-gpu and --cpu-only contradict each other: one picks a GPU, the other refuses to use any.");

        // The title options belong here for a slightly different reason than the rest: they are not
        // detection settings, but an imported mark carries the title the sidecar wrote for it and no
        // intro mark is ever prepended, so naming one is just as much an expectation this run cannot
        // meet. Rejecting beats silently ignoring, same as for --ignore-chapter-numbers below.
        if (o.Import && (o._langSet || o._phraseSpec != null || o._prologuePhraseSpec != null || o._epiloguePhraseSpec != null || o._customMappings.Count > 0 || o.IgnoreChapterNumbers || o._modelSet || o._upgradeModelSet || o._minSilenceSet || o._noiseFloorSet || o._markLeadSet || o._earlyAbortSet || o._expectedStartSet || o._maxChapterNumberSet || o._chapterCountSet || o._namedMarkDistanceSet || o.MarkBeforeJingle || o.JingleFirst || o.QuickMarks || !o.TrailingScan || !o.Denoise || o.Verify || o._titleSpec != null || o._partTitleSpec != null || o._introSpec != null || o._prologueTitleSpec != null || o._epilogueTitleSpec != null))
            throw new CliError(
                "--import skips detection entirely, so --lang, --chapter-phrase, --prologue-phrase, " +
                "--epilogue-phrase, --custom, --custom-file, --ignore-chapter-numbers, --model, --upgrade-model, " +
                "--mark-before-jingle, --jingle-first, --quick-marks, --mark-lead, --min-silence-length, " +
                "--noise-floor, --early-abort, " +
                "--expected-start-chapter, --max-chapter-number, --chapter-count, --no-trailing-scan, " +
                "--no-denoise, --verify, " +
                "--named-mark-distance, " +
                "--chapter-title, --part-title, --intro-title, --prologue-title and --epilogue-title " +
                "have no effect and cannot be combined with it.");

        // --ignore-chapter-numbers removes the chapter-number sequence detection is otherwise built
        // around, and with it every option that reasons in those numbers. Rejecting them outright
        // beats silently ignoring them: each one names an expectation about numbers this run will
        // never form an opinion about, so accepting it would promise something that cannot happen.
        // --chapter-phrase and --chapter-title stay legal - the phrase is still what is listened for
        // title word is still what the mark is called.
        // A bare number is recognized as an announcement only by being in sequence - see
        // PhraseMatching's FindBareNumbers - and --ignore-chapter-numbers is exactly the switch that
        // takes that check away, leaving every number spoken alone anywhere in the book a chapter
        // mark. Rejected rather than merely inadvisable.
        if (o.IgnoreChapterNumbers && o.UsesBareNumberPhrase)
            throw new CliError(
                "--chapter-phrase none recognizes an announcement by its number being in sequence, " +
                "so --ignore-chapter-numbers - which forms no opinion about the numbers - would " +
                "leave nothing to tell an announcement from any other number spoken alone.");

        // --no-trailing-scan is deliberately not in this list, although the scan it switches off is
        // indeed one of the things --ignore-chapter-numbers makes impossible. Every option here names
        // an expectation about the numbers that the run cannot meet, which is what makes rejecting
        // them more useful than ignoring them; --no-trailing-scan names no expectation at all, it only
        // declines work - and declining work that was never going to happen is not a contradiction.
        if (o.IgnoreChapterNumbers && (o._upgradeModelSet || o._expectedStartSet ||
                                       o._maxChapterNumberSet || o._chapterCountSet || o.Verify))
            throw new CliError(
                "--ignore-chapter-numbers forms no opinion about chapter numbers, so --upgrade-model, " +
                "--expected-start-chapter, --max-chapter-number, --chapter-count and --verify " +
                "have nothing to act on and cannot be combined with it.");

        // Its own entry rather than a sixth name in the list above, because what it names is not an
        // expectation about the numbers but a dependence on there being a sequence at all: the
        // jingle-first shape skips the pauses between two chapters whose numbers are consecutive, and
        // with no numbers no two chapters ever are, so every pause in the book would be probed after
        // the jingles had been - the ordinary Probe plus a wasted pass over the music.
        if (o.IgnoreChapterNumbers && o.JingleFirst)
            throw new CliError(
                "--jingle-first defers a book's pauses to wherever its chapter sequence still has " +
                "a hole, which --ignore-chapter-numbers leaves it no way to know - the two cannot " +
                "be combined.");

        if (o.MaxChapterNumber is { } cap && o.ExpectedStartChapter is { } start && cap < start)
            throw new CliError(
                $"--max-chapter-number ({cap}) is below --expected-start-chapter ({start}): " +
                "no chapter could ever be accepted.");

        // Both name the highest number this book may have, so accepting both would mean deciding
        // which of two contradictory answers to believe. --chapter-count is the stronger statement
        // anyway: it also says the numbers up to it are all expected to be there.
        if (o.ChapterCount != null && o.MaxChapterNumber != null)
            throw new CliError(
                "--chapter-count and --max-chapter-number both cap the chapter numbers this book " +
                "may have; give one or the other. --chapter-count also hunts for the chapters " +
                "below the cap that are missing.");

        if (o.Force && o.Verify)
            throw new CliError(
                "--force and --verify cannot be combined: --force always discards pre-existing " +
                "chapter marks, while --verify decides that based on whether they check out.");

        if (o.VerifyFailThreshold != null && !o.Verify)
            throw new CliError("--verify-threshold requires --verify.");

        if (o.Fix && !o.Verify)
            throw new CliError("--fix corrects what --verify checked and requires it.");

        if (o.SimpleMetadata && !o.Export && !o.Import)
            throw new CliError("--simple-metadata requires --export or --import.");

        o.Language = o.Language.ToLowerInvariant();
        if (o.Language != "auto" && !Regex.IsMatch(o.Language, "^[a-z]{2}$"))
            throw new CliError($"Invalid language code \"{o.Language}\": expected a two-letter code like \"en\", or \"auto\".");

        // Both selectors were validated where they were parsed. Naming --model without --upgrade-model
        // re-points Scan at the chosen model, so `-m large` means large throughout rather than large
        // probing and a lighter Scan - which would read as a deliberate downgrade and switch off both
        // Re-probe and the shifted re-read. Only the untouched default keeps the small/turbo pair, and
        // with it the upgrade that puts Re-probe on by default.
        if (!o._upgradeModelSet && o._modelSet)
            o.UpgradeModel = o.Model;
        o.UpgradeModelIsBetter = ModelCatalog.ApproximateSizeBytes(o.UpgradeModel)
                                > ModelCatalog.ApproximateSizeBytes(o.Model);
        o.UpgradeModelIsWorse = ModelCatalog.ApproximateSizeBytes(o.UpgradeModel)
                                  < ModelCatalog.ApproximateSizeBytes(o.Model);

        // Every language's own entry is checked rather than only the one this run resolves to: a
        // spec with an empty entry for a language nobody feeds it today is a broken spec either
        // way, and finding that out mid-batch on file two hundred helps nobody.
        if (o._phraseSpec is { } phrases
                ? phrases.Entries.Count == 0 || phrases.Bodies.Any(p => p.Length == 0)
                : o.ChapterPhrase.Length == 0)
            throw new CliError("The chapter phrase must not be empty.");

        // An empty prologue/epilogue phrase is how those are switched off, so only their titles
        // are required to be non-empty - a mark would otherwise be written with no title at all.
        if (o._prologueTitleSpec is { } prologues && prologues.Values.Any(t => t.Length == 0))
            throw new CliError("The prologue title must not be empty (use --prologue-phrase \"\" to switch prologue detection off).");
        if (o._epilogueTitleSpec is { } epilogues && epilogues.Values.Any(t => t.Length == 0))
            throw new CliError("The epilogue title must not be empty (use --epilogue-phrase \"\" to switch epilogue detection off).");

        o.ResolveTargets(targetArgs);

        // A statement about one book cannot be made about a whole folder of them, and silently
        // applying "this book has 20 chapters" to every file of a library would tag most of them as
        // incomplete. Checked after the targets are resolved, so that naming a directory is caught
        // here rather than becoming a run that hunts for chapters no file has.
        if (o.ChapterCount != null && (o._targets.Count != 1 || o._targets[0].IsDirectory))
            throw new CliError(
                "--chapter-count states how many chapters one particular book has, so it takes " +
                "exactly one file - not a directory, and not several files.");

        // With an explicit --lang this localizes the chapter phrase, title word and intro title
        // (unless given explicitly) for every file; with auto-detection it is only the English
        // fallback, and ChapterDetector resolves a fresh profile per file - see ResolveProfile.
        var fallbackLanguage = o.AutoLanguage ? "en" : o.Language;
        o.DefaultProfile = o.ResolveProfile(fallbackLanguage);
        // Where an option was given, its own text stands - the whole spec, not this one language's
        // share of it, because the fingerprint and the debug log both have to tell two specs apart
        // that happen to agree on the fallback language. Where it was not, the fallback language's
        // localized default fills in.
        o.ChapterPhrase = o._phraseSpec?.Raw ?? o.DefaultProfile.ChapterPhrase;
        o.Title = o._titleSpec?.Raw ?? o.DefaultProfile.Title;
        o.PartTitle = o._partTitleSpec?.Raw ?? o.DefaultProfile.PartTitle;
        o.IntroTitle = o._introSpec?.Raw ?? o.DefaultProfile.IntroTitle;
        // The named phrases keep only their compiled form in the profile, so the raw strings the
        // fingerprint and any user-facing echo read are localized here rather than copied back.
        var language = LanguageRegistry.For(fallbackLanguage);
        o.ProloguePhrase = o._prologuePhraseSpec?.Raw ?? language.ProloguePhrase;
        o.PrologueTitle = o._prologueTitleSpec?.Raw ?? language.PrologueTitle;
        o.EpiloguePhrase = o._epiloguePhraseSpec?.Raw ?? language.EpiloguePhrase;
        o.EpilogueTitle = o._epilogueTitleSpec?.Raw ?? language.EpilogueTitle;

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
            case "--cleanup": Cleanup = true; return true;
            case "--yes": AssumeYes = true; return true;
            case "--no-op": NoOp = true; return true;
            case "--cpu-only": CpuOnly = true; return true;
            case "--force": Force = true; return true;
            case "--mark-before-jingle": MarkBeforeJingle = true; return true;
            case "--jingle-first": JingleFirst = true; return true;
            case "--quick-marks": QuickMarks = true; return true;
            case "--no-trailing-scan": TrailingScan = false; return true;
            case "--no-denoise": Denoise = false; return true;
            // Inverted in 0.11.0. Named rather than left to "Unknown option" so a script carrying it
            // - or the -L it still maps from - is told the scan it asked for is now what it gets
            // anyway, instead of only that the option is gone.
            case "--trailing-scan":
                throw new CliError(
                    "--trailing-scan was inverted: the trailing scan is now on by default, so the " +
                    "option is no longer needed. Use --no-trailing-scan to switch it off.");
            case "--quiet": Quiet = true; return true;
            case "--verbose": Verbose = true; return true;
            case "--verbose-transcripts": VerboseTranscripts = Verbose = true; return true;
            // Undocumented on purpose (see CliOptions.Debug); it has no short form for the same
            // reason - the single letters belong to options people choose for themselves.
            case "--debug": Debug = true; return true;
            case "--no-bar": NoBar = true; return true;
            case "--summary": Summary = true; return true;
            case "--dry-run": DryRun = true; return true;
            case "--export": Export = true; return true;
            case "--import": Import = true; return true;
            case "--simple-metadata": SimpleMetadata = true; return true;
            case "--verify": Verify = true; return true;
            case "--fix": Fix = true; return true;
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
            // The phrase options accumulate rather than overwrite: repeating one is defined as
            // writing its values as a single semicolon-separated list, since a phrase is a list of
            // alternatives and a second --chapter-phrase is another way to say "or this".
            case "--chapter-phrase":
                ChapterPhrase = PhraseSpec.Join(_phraseSpec?.Raw, nextParam());
                _phraseSpec = PhraseSpec.Parse(ChapterPhrase, name);
                return true;
            case "--use-gpu": UseGpu = ParseUseGpu(nextParam()); return true;
            case "--model": Model = ParseModelSelector("--model", nextParam()); _modelSet = true; return true;
            // --pass3-model is the pre-0.12.1 spelling, still accepted silently for the same
            // reason --title is: nothing about the option was wrong except its name. That name
            // pointed at one of the five steps the model is actually used by - the gap scan - and
            // stopped meaning anything at all once the passes were named rather than numbered.
            // The spelling typed is what `name` records, so an error about a bad value still
            // quotes whichever one the user wrote.
            case "--upgrade-model":
            case "--pass3-model": UpgradeModel = ParseModelSelector(name, nextParam()); _upgradeModelSet = true; return true;
            case "--max-chapters": MaxChapters = ParseNonNegativeInt("--max-chapters", nextParam()); _maxSet = true; return true;
            case "--max-chapter-number": MaxChapterNumber = ParseMaxChapterNumber(nextParam()); _maxChapterNumberSet = true; return true;
            case "--early-abort": EarlyAbortMinutes = ParseEarlyAbort(nextParam()); _earlyAbortSet = true; return true;
            case "--expected-start-chapter": ExpectedStartChapter = ParseExpectedStartChapter(nextParam()); _expectedStartSet = true; return true;
            case "--chapter-count": ChapterCount = ParseChapterCount(nextParam()); _chapterCountSet = true; return true;
            case "--verify-threshold": VerifyFailThreshold = ParseNonNegativeInt("--verify-threshold", nextParam()); return true;
            // --title is the pre-0.11.0 spelling. Still accepted and no longer documented: every
            // other option naming a part of a book says which part (--chapter-phrase,
            // --intro-title, --prologue-title), and a bare --title read like the book's own title
            // rather than the word put in front of a chapter number. Silent rather than
            // deprecated-with-a-warning because nothing about it was wrong - only its name - and
            // "name" is what the spec records, so an error about a bad value still quotes
            // whichever spelling was typed.
            case "--chapter-title":
            case "--title": Title = nextParam(); _titleSpec = new(Title, name); return true;
            case "--part-title": PartTitle = nextParam(); _partTitleSpec = new(PartTitle, name); return true;
            case "--intro-title": IntroTitle = nextParam(); _introSpec = new(IntroTitle, name); return true;
            case "--prologue-phrase":
                ProloguePhrase = PhraseSpec.Join(_prologuePhraseSpec?.Raw, nextParam());
                _prologuePhraseSpec = PhraseSpec.Parse(ProloguePhrase, name);
                return true;
            case "--prologue-title": PrologueTitle = nextParam(); _prologueTitleSpec = new(PrologueTitle, name); return true;
            case "--epilogue-phrase":
                EpiloguePhrase = PhraseSpec.Join(_epiloguePhraseSpec?.Raw, nextParam());
                _epiloguePhraseSpec = PhraseSpec.Parse(EpiloguePhrase, name);
                return true;
            case "--epilogue-title": EpilogueTitle = nextParam(); _epilogueTitleSpec = new(EpilogueTitle, name); return true;
            case "--named-mark-distance": NamedMarkDistanceSeconds = ParseNamedMarkDistance(nextParam()); _namedMarkDistanceSet = true; return true;
            case "--custom": _customMappings.AddRange(CustomMappingParser.ParseSpec(nextParam())); return true;
            case "--custom-file": _customMappings.AddRange(CustomMappingParser.ParseFile(nextParam())); return true;
            // Already applied: ConfigFile.Expand read the file and put its options in front of this
            // command line before parsing began. Accepted here only so that the option is known -
            // which is what subjects it to RejectTrailingOption and keeps --help answerable when the
            // file is broken. Its parameter is consumed and discarded.
            case ConfigFile.Option: nextParam(); return true;
            case "--filter": ParseFilter(nextParam()); return true;
            case "--min-silence-length": (MinSilenceSeconds, AutoMinSilence) = ParseMinSilence(nextParam()); _minSilenceSet = true; return true;
            case "--noise-floor": (NoiseFloorDb, AutoNoiseFloor) = ParseNoiseFloor(nextParam()); _noiseFloorSet = true; return true;
            case "--mark-lead": MarkLeadSeconds = ParseMarkLead(nextParam()); _markLeadSet = true; return true;
            case "--vad-threads": VadThreads = ParseThreadCount("--vad-threads", nextParam()); return true;
            case "--whisper-threads": WhisperThreads = ParseThreadCount("--whisper-threads", nextParam()); return true;
            // Removed in 0.10.0 and 0.12.0. Kept as named cases rather than left to "Unknown
            // option" so a script carrying one is told what replaced it instead of only that it is
            // gone.
            case "--max-jingle-length":
                throw new CliError(
                    "--max-jingle-length was removed: every probe window is now cut to its own " +
                    "candidate, and how far a book's music reaches is measured from the file " +
                    "itself. The voice-activity pre-pass that finds the jingles always runs.");
            case "--jobs":
                throw new CliError(
                    "--jobs was removed: files are no longer processed several at a time, so that the " +
                    "whole machine goes into each one. Use --vad-threads and --whisper-threads to " +
                    "control how many threads that is.");
            case "--run-before": RunBefore = CommandTemplate.Parse(nextParam(), name); return true;
            case "--run-after": RunAfter = CommandTemplate.Parse(nextParam(), name); return true;
            case "--log-file": LogFilePath = ParseLogFilePath(nextParam()); return true;
            case "--color": Color = ParseColorMode(nextParam()); return true;
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
    /// Parses the --min-silence-length parameter into a positive number of seconds, 0 (no
    /// silence-triggered probing at all - see <see cref="ProbeSilences"/>), or "auto". "auto"
    /// resolves to the 1.5 s floor plus <see cref="AutoMinSilence"/> set, telling
    /// <see cref="ChapterDetector"/> to self-tighten the threshold as chapters are found.
    /// </summary>
    private static (double Seconds, bool Auto) ParseMinSilence(string value)
    {
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return (1.5, true);
        if (!NumberCulture.TryParseDecimal(value, out var s) || (s != 0 && (s < 0.1 || s > 60)))
            throw new CliError($"Invalid --min-silence-length value \"{value}\": expected 0, seconds between 0.1 and 60, or \"auto\".");
        return (s, false);
    }

    /// <summary>
    /// Parses the --noise-floor parameter into a level in dBFS, or "auto". Bounded well inside
    /// the digital range rather than at it: 0 dBFS is full scale, so a threshold anywhere near it
    /// calls the entire book silent, and one below -90 is under the noise of 16-bit audio and calls
    /// none of it silent.
    /// </summary>
    private static (double Db, bool Auto) ParseNoiseFloor(string value)
    {
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return (DetectionTuning.DefaultSilenceNoiseDb, true);
        if (!NumberCulture.TryParseDecimal(value, out var db) || db < -90 || db > -5)
            throw new CliError(
                $"Invalid --noise-floor value \"{value}\": expected a level in dBFS between -90 " +
                "and -5 (negative, 0 being full scale), or \"auto\".");
        return (db, false);
    }

    /// <summary>Parses the --color parameter into a <see cref="ColorMode"/>.</summary>
    /// <param name="value">The raw parameter.</param>
    private static ColorMode ParseColorMode(string value) => value.ToLowerInvariant() switch
    {
        "auto" => ColorMode.Auto,
        "always" => ColorMode.Always,
        "never" => ColorMode.Never,
        _ => throw new CliError($"Invalid --color value \"{value}\": expected \"auto\", \"always\" or \"never\"."),
    };

    /// <summary>Parses a thread-count parameter into a positive count, or null for "auto".</summary>
    /// <param name="option">Long name of the option being parsed, for the error message.</param>
    /// <param name="value">The raw parameter.</param>
    private static int? ParseThreadCount(string option, string value)
    {
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!int.TryParse(value, out var n) || n < 1)
            throw new CliError($"Invalid {option} value \"{value}\": expected a positive number or \"auto\".");
        return n;
    }

    /// <summary>
    /// Validates a --model/--upgrade-model selector: one of the catalog's names, or
    /// <c>custom:&lt;path&gt;</c> naming a GGML file of the user's own (a fine-tune, a quantized
    /// build, or a model the catalog does not carry).
    /// <para>
    /// A custom path is expanded and made absolute here, for two reasons: two spellings of the same
    /// file must compare equal, because that string comparison is what decides whether Scan needs
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

    /// <summary>
    /// Parses the --mark-lead parameter: seconds between 0 and 10.
    /// </summary>
    /// <param name="value">The raw parameter.</param>
    /// <remarks>
    /// 0 is allowed and means "mark exactly at the onset" - a legitimate choice for someone who
    /// wants no lead-in at all, and the refinement still places it to the same accuracy. The upper
    /// bound only rules out values that would land in the previous chapter's narration; anything
    /// beyond a couple of seconds is already better served by --mark-before-jingle.
    /// </remarks>
    private static double ParseMarkLead(string value)
    {
        if (!NumberCulture.TryParseDecimal(value, out var s) || s < 0 || s > 10)
            throw new CliError($"Invalid --mark-lead value \"{value}\": expected seconds between 0 and 10.");
        return s;
    }

    /// <summary>
    /// Validates the --use-gpu parameter, which is otherwise taken as typed: the device list it
    /// has to match is not known here, so a genuine match failure can only be reported later, by
    /// <see cref="ABChapterize.Gpu.GpuSelector"/>, where the real names are available to print
    /// alongside the complaint.
    /// </summary>
    /// <param name="value">The raw parameter.</param>
    private static string ParseUseGpu(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            throw new CliError("Invalid --use-gpu value: expected part of a GPU name, e.g. --use-gpu gtx (see --list-gpus).");
        return trimmed;
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

    /// <summary>Parses the --named-mark-distance parameter into a number of seconds, or 0 for "let
    /// every mark stand where it is". Capped at 600 because a value past that is a typo, and one
    /// large enough to swallow whole chapters would quietly turn a book's marks into a single
    /// entry.</summary>
    /// <param name="value">The raw parameter.</param>
    private static double ParseNamedMarkDistance(string value)
    {
        if (!NumberCulture.TryParseDecimal(value, out var s) || s < 0 || s > 600)
            throw new CliError(
                $"Invalid --named-mark-distance value \"{value}\": expected 0 or seconds between 0 and 600.");
        return s;
    }

    /// <summary>Parses the --expected-start-chapter parameter into a chapter number of 1 or higher.</summary>
    private static int ParseExpectedStartChapter(string value)
    {
        if (!int.TryParse(value, out var n) || n < 1)
            throw new CliError($"Invalid --expected-start-chapter value \"{value}\": expected a chapter number of 1 or higher.");
        return n;
    }

    /// <summary>Parses the --chapter-count parameter into a count of 1 or higher. Zero is rejected
    /// rather than read as "no chapters at all": a book with none is one this tool has nothing to
    /// do to, and the value would only ever be a typo for a real count.</summary>
    private static int ParseChapterCount(string value)
    {
        if (!int.TryParse(value, out var n) || n < 1)
            throw new CliError($"Invalid --chapter-count value \"{value}\": expected a chapter count of 1 or higher.");
        return n;
    }

    /// <summary>
    /// Resolves the chapter phrase, title word and intro title for the given language: an
    /// explicit --chapter-phrase/--chapter-title/--part-title/--intro-title always wins; otherwise the localized
    /// default for <paramref name="language"/> is used (English defaults for languages without
    /// an entry in <see cref="LanguageRegistry"/>). Called once at parse time for an explicit
    /// --lang (building <see cref="DefaultProfile"/>), and once per file by
    /// <see cref="ChapterDetector"/> when <see cref="AutoLanguage"/> is active.
    /// </summary>
    /// <param name="language">Two-letter language code (not "auto") to resolve defaults for.</param>
    public LanguageProfile ResolveProfile(string language)
    {
        var defaults = LanguageRegistry.For(language);
        var alternatives = Alternatives(_phraseSpec, language, defaults.ChapterPhrase, "--chapter-phrase");
        var title = _titleSpec?.For(language) ?? defaults.ChapterTitle;
        var partTitle = _partTitleSpec?.For(language) ?? defaults.PartTitle;
        var intro = _introSpec?.For(language) ?? defaults.IntroTitle;
        var pattern = PhraseCompiler.Compile(
            alternatives, language, PhraseKind.Chapter, "chapter phrase");
        // This language's own share of the spec rather than the whole of it: the profile is what a
        // per-file debug line reports, and a batch's other languages are not what that file heard.
        // The fingerprint reads CliOptions.ChapterPhrase, which does keep the whole spec.
        return new LanguageProfile(
            language, pattern.Source, pattern,
            title, partTitle, intro, ResolveNamedPhrases(language, defaults));
    }

    /// <summary>
    /// The wordings one phrase option resolves to for a language: what the user wrote, with
    /// <c>default</c> expanded into this tool's own - which is also what an option that says
    /// nothing about this language falls back to whole. The built-in default is itself parsed as a
    /// spec, so a language may bring several wordings of its own without the two syntaxes differing.
    /// </summary>
    /// <param name="spec">The option's value, or null when it was not given.</param>
    /// <param name="builtIn">This tool's own phrase for the language.</param>
    /// <param name="language">Two-letter language code being resolved for.</param>
    /// <param name="option">Long option name, for error messages.</param>
    private static IReadOnlyList<string> Alternatives(
        PhraseSpec? spec, string language, string builtIn, string option)
    {
        IReadOnlyList<string> Defaults() => PhraseSpec.Parse(builtIn, option).Entries
            .Where(e => e.Language == null ||
                        string.Equals(e.Language, language, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Body)
            .ToList();
        return spec?.For(language, Defaults) ?? Defaults();
    }

    /// <summary>
    /// Builds every non-numbered phrase for one language: the prologue and epilogue - each dropped
    /// entirely when its phrase resolves to the empty string, the opt-out spelling, since neither
    /// has a flag of its own and a mark nobody wants is better never detected than detected and
    /// discarded - followed by the <c>--custom</c> mappings in the order they were given. The custom
    /// ones are never localized and never dropped: they were written out by hand, in whatever
    /// language the user meant them in.
    /// <para>
    /// The prologue and epilogue are the same machinery with different values, which is what the
    /// <c>--custom</c> hints expose (see <see cref="SpecTag"/>): a mapping written
    /// <c>[before-first-chapter,once]/^vorwort/</c> resolves to exactly the phrase the built-in
    /// prologue is - the tag supplying the scope and the single-mark rule, the phrase's own
    /// <c>^</c> the pause in front. Untagged mappings keep the defaults they have always had -
    /// anywhere in the file, every occurrence marked, no pause required in front of it.
    /// </para>
    /// </summary>
    /// <param name="language">The language being resolved for, which decides both which share of a
    /// per-language option applies and which <c>--custom</c> mappings are in play at all.</param>
    /// <param name="defaults">The language's own defaults, for whichever option was not given.</param>
    private IReadOnlyList<NamedPhrase> ResolveNamedPhrases(string language, ILanguage defaults)
    {
        var named = new List<NamedPhrase>();
        Add(NamedPhrase.PrologueKind,
            Alternatives(_prologuePhraseSpec, language, defaults.ProloguePhrase, "--prologue-phrase"),
            _prologueTitleSpec?.For(language) ?? defaults.PrologueTitle,
            NamedPhraseScope.BeforeFirstChapter, requiresLeadIn: true);
        Add(NamedPhrase.EpilogueKind,
            Alternatives(_epiloguePhraseSpec, language, defaults.EpiloguePhrase, "--epilogue-phrase"),
            _epilogueTitleSpec?.For(language) ?? defaults.EpilogueTitle,
            NamedPhraseScope.AfterFirstChapter, requiresLeadIn: true);
        for (var i = 0; i < _customMappings.Count; i++)
        {
            // A mapping tagged for another language is left out entirely rather than compiled and
            // never matched: it would cost a regexp pass over every probe window of a book it has
            // nothing to say about. Numbered per its position in the option, so the kind a log line
            // names still points at the mapping the user wrote, whatever the file's language.
            if (_customMappings[i].Language is { } code &&
                !string.Equals(code, language, StringComparison.OrdinalIgnoreCase))
                continue;
            var mapping = _customMappings[i];
            var kind = $"{NamedPhrase.CustomKindPrefix}{i + 1}";
            // An untagged mapping is a tag that asks for nothing, so the two paths differ in what
            // was written rather than in what is built. A mapping's phrase is one alternative: the
            // semicolon that separates alternatives elsewhere already separates mappings here.
            var tag = mapping.Tag ?? new SpecTag(null);
            AddPhrase(kind, [mapping.Phrase], mapping.Title,
                (pattern, title) => tag.ToPhrase(kind, pattern, title));
        }
        return named;

        void Add(string kind, IReadOnlyList<string> phrase, string markTitle, NamedPhraseScope scope,
            bool repeatable = false, bool requiresLeadIn = false)
            => AddPhrase(kind, phrase, markTitle,
                (pattern, title) =>
                    new NamedPhrase(kind, pattern, title, scope, repeatable, requiresLeadIn));

        void AddPhrase(
            string kind, IReadOnlyList<string> phrase, string markTitle,
            Func<PhrasePattern, TitleTemplate, NamedPhrase> build)
        {
            var pattern = PhraseCompiler.Compile(phrase, language, PhraseKind.Named, $"{kind} phrase");
            if (pattern.Alternatives.Count == 0)
                return;
            var title = new TitleTemplate(markTitle, $"{kind} title");
            ValidateTitleGroupRefs(kind, pattern, title);
            named.Add(build(pattern, title));
        }
    }

    /// <summary>
    /// Rejects a title that references a capturing group its phrase does not have. Caught here, at
    /// parse time, rather than left to <see cref="Match.Result"/>: that would throw mid-book, after
    /// possibly hours of transcription, with an exception message naming neither the option nor the
    /// mapping at fault.
    /// </summary>
    /// <param name="kind">The phrase kind, for the error message.</param>
    /// <param name="pattern">The phrase's compiled wordings, supplying the groups that do exist.</param>
    /// <param name="title">The title template to check.</param>
    /// <exception cref="CliError">Thrown for a reference to a group that does not exist.</exception>
    private static void ValidateTitleGroupRefs(string kind, PhrasePattern pattern, TitleTemplate title)
    {
        var groups = pattern.GroupNames.ToList();
        foreach (var group in title.ReferencedGroups.Where(g => !groups.Contains(g)))
            throw new CliError(
                $"The {kind} title \"{title.Raw}\" references the capturing group \"{group}\", " +
                $"which the phrase \"{pattern.Source}\" does not have. " +
                "Write \"$$\" for a literal dollar sign.");
    }

    /// <summary>The title-template syntax as <see cref="UsageText"/> prints it. Constants because
    /// that text is an interpolated raw string literal, in which a brace of its own would have to be
    /// escaped past legibility.</summary>
    private const string GroupReference = "\"${name}\"";

    /// <inheritdoc cref="GroupReference"/>
    private const string NumberReference = "\"${number}\"";

    /// <inheritdoc cref="GroupReference"/>
    private const string Conversions = "$roman{}, $digits{}, $upper{}, $lower{} or $capital{}";

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
          abchapterize --cleanup [--revert] [--yes] [--recurse] [--filter <f>] <file-or-directory>...
          abchapterize -O|--no-op --filter <f> [--recurse] <file-or-directory>...
          abchapterize --help | -?

        Any number of files and directories may be given, mixed freely; they are processed in
        the order listed, and a file named twice (or already covered by a listed directory) is
        still only processed once.

        Options (must precede the file/directory arguments):

              --config <path>       Read options from a file: one option per line, written
                                    exactly as you would type it, with everything after the
                                    option name taken as its argument - so a phrase or a
                                    mapping needs no quoting. One layer of surrounding
                                    double quotes is stripped if you leave it on, which is
                                    also how an empty argument is written (""). Blank lines
                                    and lines starting with "#" are ignored, and a config
                                    file may name another with --config of its own (paths
                                    then relative to the file naming it). May be given more
                                    than once. An option you type on the command line always
                                    wins over the same option in a file, wherever --config
                                    stands; options that are meant to be repeated, such as
                                    --custom and --chapter-phrase, accumulate instead.

        File selection:
          -r, --recurse             Recursively descend into subdirectories (directories only).
          -F, --filter <filter>     Only process matching files. Either "/regexp/" - matched
                                    case-insensitively against the whole path of each file -
                                    or a comma-separated list of permissible file extensions,
                                    e.g. "mp3,m4b". One filter of each kind may be given;
                                    they also select which backups --revert restores - where
                                    the regexp is matched against the backup's own path, the
                                    one still ending in ".bak", so do not anchor it at the
                                    audio extension.
          -f, --force               Discard pre-existing chapter marks. Without --force, files
                                    that already have chapter marks are skipped.
          -x, --max-chapters <n>    If a file has more than <n> pre-existing chapter marks,
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
                                    ("2.", "2nd", "2e") and Roman numerals ("XIII") work in every
                                    language. For these
                                    languages, --lang also localizes the defaults of
                                    --chapter-phrase, --prologue-phrase, --epilogue-phrase,
                                    --chapter-title, --part-title, --intro-title, --prologue-title
                                    and --epilogue-title (per-file with "auto").
          -m, --model <name>        Whisper model used to find the chapters: tiny, base, small,
                                    medium, turbo or large (default: small), or "custom:<path>" for
                                    a GGML model file of your own, e.g.
                                    -m custom:~/models/my-finetune.bin. A custom file is used as it
                                    is: never downloaded, never checked against a known checksum,
                                    and only ranked against the built-in models by its size (which
                                    is what decides Re-probe, see --upgrade-model). Bigger is not
                                    better here: this model listens to short windows a few seconds
                                    long, and the large models are markedly worse at those than
                                    "small" is - they tend to return the window as one run-on
                                    sentence with the announcement missing from it. Scan is where
                                    a heavy model earns its keep; see --upgrade-model.
          -M, --upgrade-model <name>
                                    Whisper model for the steps worth asking a better
                                    recognizer; same choices as --model (default: turbo, or
                                    whatever --model says if you set that and not this).
                                    Chiefly the Scan pass, which transcribes long stretches of
                                    audio, where the heavier models really are the better
                                    recognizers. Use a lighter model to speed the scanning up,
                                    or "large" for one last
                                    best-effort attempt at the chapters the main model missed. A
                                    bigger model than --model's also enables Re-probe, a quick
                                    re-probe of the gap with it before Scan transcribes the region
                                    in full - which the default pairing does. Downloaded and loaded
                                    lazily, only if and when a file actually needs it - which
                                    besides the Re-probe and Scan passes includes Probe's own second
                                    opinions: a mark it could not pin down, a chapter number that
                                    cannot be right, and an announcement a window lost.
          -j, --mark-before-jingle  A short jingle may precede the chapter phrase; anchor the
                                    mark to it instead of the default fixed offset. A silence
                                    scan and a
                                    voice-activity (VAD) pre-pass run over the whole
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
                                    be. Without this option, the mark is always placed
                                    --mark-lead seconds before the chapter phrase, no matter
                                    what precedes it.
              --jingle-first        [EXPERIMENTAL] Read this book's music first and its pauses
                                    afterwards. Probe normally walks both together, in one
                                    sweep through the file; with this it probes every jingle
                                    first, then looks at the pauses only where the chapter
                                    sequence still has a hole, plus before the first chapter
                                    found and after the last - which is where a prologue and
                                    an epilogue are. On a book that announces every chapter
                                    after a music sting, the pauses in between can only
                                    confirm what the music already said, and skipping them
                                    saves a great deal of time. This shape is chosen by
                                    itself for a file with at least one jingle per hour of
                                    play time, unless one of your own --custom mappings may
                                    be announced between two chapters - which is the one
                                    thing it would stop looking for. Give this option to use
                                    it anyway, on a file that qualifies for neither reason.
                                    --verbose says which shape a file ran under.
          -k, --mark-lead <seconds> How far before the announcement a mark is placed (default
                                    0.35). Purely a matter of taste: marks are located to the
                                    same accuracy whatever this is, it only decides how much
                                    lead-in you hear before the narrator starts. Raise it for
                                    a longer run-up, lower it to land closer to the first
                                    word - though below about 0.2 the opening consonant of the
                                    announcement can be clipped. 0 marks the onset itself.
                                    Applies under --mark-before-jingle too: in full where a
                                    chapter has no jingle, and as a back-off into the pause in
                                    front of the jingle where there is one, capped at that
                                    pause's own length.
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
          -n, --min-silence-length <seconds|auto>
                                    The shortest pause probed as a potential chapter
                                    break (default: "auto", which starts at 1.5). This
                                    governs probing alone: Analyze's scan keeps shorter
                                    silences regardless, and marks are placed and refined
                                    against them. With "auto" (the default), starting from
                                    the second chapter mark found (the silence before the
                                    first mark is usually the intro/title silence and often
                                    longer, so it is not used to tighten), the probing
                                    threshold sits at 75% of the length of the shortest
                                    silence a mark has fallen into so far (set once, then
                                    only ever lowered), and a sequence gap re-probes
                                    everything skipped since the last mark rather than
                                    resetting the threshold - fewer Whisper probes without
                                    a fixed guess. Where that figure comes out below the
                                    1.5 probing started at - a narrator whose chapter
                                    breaks are simply shorter than the default assumes -
                                    the gaps left in the numbering are swept for the pauses
                                    in between, down to 0.8. An explicit numeric value
                                    disables all of that and probes every silence at or
                                    above it instead - useful if the breaks are known to
                                    vary a lot, or for troubleshooting. 0 says not to probe
                                    silences at all, leaving only the jingles the
                                    voice-activity pre-pass finds - a large saving on a
                                    book whose every chapter opens with one, and a way to
                                    miss every chapter that does not.
              --noise-floor <dBFS|auto>
                                    How quiet audio has to be to count as a pause, in dBFS
                                    (default: auto; 0 is full scale, so this is negative).
                                    With "auto", each file's own levels are sampled before
                                    the silence scan and the threshold is only moved where
                                    the usual -35 would fall outside that recording's gap
                                    between room tone and speech - which on an ordinary
                                    audiobook it does not, so nothing changes. It matters
                                    on an unusual master: one with audible hiss never drops
                                    below -35 at all, so no pause is ever found and no
                                    chapter with it, while one mastered very quietly puts
                                    the narration itself under -35, so every gap between
                                    two words looks like a chapter break. An explicit level
                                    fixes the threshold for the whole run.

        Phrases & titles:
          -c, --chapter-phrase <p>  Word/phrase that identifies a chapter start (default:
                                    "/(?:^chapter ()|^() chapter|^chapter)/", localized by --lang).
                                    Matching is always case-insensitive. A value is a list of
                                    alternatives separated by semicolons, each of them one of:
                                      word        the word with the number in front or behind
                                      /regexp/    a regular expression
                                      none        the number spoken alone, with no phrase at all
                                      default     this tool's own phrase for the language
                                    e.g. --chapter-phrase "/se[ck]tion ()/;partie;default"
                                    Inside a regexp, "()" stands for a chapter number in any
                                    notation the language has - digits, ordinals, Roman numerals,
                                    spoken words - and captures it. A leading "^" asks for the
                                    announcement to be set off in front - by a real pause, or by the
                                    recognizer writing it as a segment of its own - and a trailing
                                    "$" for a pause behind it; neither is an anchor.
                                    An alternative may be restricted to one language by a leading
                                    "[xx]" tag, e.g. --chapter-phrase "[fr]/chapitre ()/;section".
                                    Untagged alternatives apply to every language; a language the
                                    value says nothing about keeps its own built-in phrase.
                                    Repeating the option adds alternatives. The same syntax works
                                    for --prologue-phrase, --epilogue-phrase and --custom; the
                                    title options take the "[xx]" tag but hold one value each.
                                    "none" is [EXPERIMENTAL] and cannot be combined with
                                    --ignore-chapter-numbers, the chapter sequence being the only
                                    thing that tells such an announcement from a year or a price.
                                    Give it only to books that really are announced this way: on a
                                    book that names its chapters, adding "none" buys nothing and
                                    lets a year in a timetable, a number in dialogue or one in the
                                    closing pages become a chapter.
                                    See doc/manual.md for the whole syntax, with examples.
          -p, --prologue-phrase <p> Word/phrase that identifies a prologue (default: /prolog/,
                                    localized by --lang). Takes the same alternatives, tags and
                                    guards as --chapter-phrase, but parses no number: a match becomes one
                                    untitled-by-number mark carrying --prologue-title. Only
                                    accepted before the first chapter has been found, so a later
                                    mention in the prose cannot produce a second mark; if the
                                    phrase turns up more than once before then, the last
                                    occurrence wins (front matter often lists what is coming
                                    before the narrator announces it). Pass an empty string to
                                    switch prologue detection off.
          -g, --epilogue-phrase <p> Same for the epilogue (default: /epilog/, localized by
                                    --lang), mirrored: only accepted once at least one chapter
                                    has been found, and only kept when it follows the book's
                                    last one - a match between two chapters is prose, or an
                                    inner part ending, and is dropped. Use --custom for a
                                    section there. Pass an empty string to switch it off.
          -u, --custom <mappings>   Extra phrase-to-title mappings, "phrase:title" pairs separated
                                    by semicolons, e.g.
                                      --custom "zwischenspiel:Zwischenspiel;/zeit[- ]?tafel/:Zeittafel"
                                    A phrase is a word or a "/regexp/" and parses no number; a
                                    match anywhere in the file becomes a mark titled after the
                                    colon, as often as the phrase occurs (up to
                                    {DetectionTuning.MaxCustomMarksPerFile} marks per file,
                                    after which the rest are dropped with a note). Only the first colon
                                    delimits, so a title may contain more of them; a "/regexp/"
                                    phrase ends at its closing slash instead, so a colon inside it
                                    is just a colon. Write "\;" for a semicolon inside a regexp.
                                    A title may write out what the phrase captured: {GroupReference}
                                    for a named group, {NumberReference} for the chapter number in
                                    digits, and {Conversions}
                                    to convert one ("$$" writes a literal dollar sign).
                                    Repeat the option to add further
                                    mappings. Never localized - a phrase is taken exactly as
                                    written - but a mapping may open with a "[...]" tag holding a
                                    comma-separated list of a "xx" language code, restricting it
                                    to files that resolve to that language, and any of these
                                    keywords, restricting how it behaves:
                                      before-first-chapter  only before the first chapter found
                                      after-first-chapter   only once a chapter has been found
                                      after-last-chapter    only after the book's last chapter
                                      once                  at most one mark, the last one wins
                                      max=<n>               at most <n> marks, the first ones win
                                    To require a real pause in front of a match, write "^" at the
                                    start of the phrase rather than asking for it in the tag.
                                    e.g. --custom "[de,before-first-chapter,once]/^vorwort/:Vorwort",
                                    which is exactly what the built-in prologue is. The three
                                    positions also have the short forms "before-first",
                                    "after-first" and "after-last". Untagged mappings apply to
                                    every file, anywhere in it, as often as the phrase occurs.
                                    A bracket run counts as a tag only when at least one token in
                                    it is recognized, so a phrase like "[Musik]" - Whisper writes
                                    such tags into its transcripts - is still matched as written;
                                    a typo beside a good keyword is an error rather than phrase
                                    text. See doc/manual.md for the details.
          -U, --custom-file <path>  Read --custom mappings from a text file, one per line. Blank
                                    lines and lines starting with "#" are ignored, and semicolons
                                    need no escaping here since line breaks separate the mappings.
          -D, --named-mark-distance <seconds>
                                    How close a prologue, epilogue or --custom mark may come to a
                                    chapter mark before the two are written as one entry
                                    (default: 10). The chapter keeps its position; the named mark
                                    contributes its title in brackets, e.g.
                                    "Chapter 10 (Interlude)". Pass 0 to write every mark
                                    separately however close together they fall.
          -t, --chapter-title <word>
                                    Word used for chapter titles; the chapter number is appended
                                    (default: Chapter, localized by --lang).
              --part-title <word>   Word used for the part prefix of a file whose chapter numbering
                                    restarts partway through, e.g. "Part 2 - Chapter 1"
                                    (default: Part, localized by --lang). A file holding a single
                                    chapter sequence - every ordinary book - never uses it.
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
                                    All four accept the per-language "[xx]" syntax described
                                    under --chapter-phrase.

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
                                    none - whatever Probe finds first is accepted outright). If
                                    the first chapter found is numbered below <n>, the file is
                                    aborted and left unchanged; if numbered above <n>, the
                                    numbers in between are hunted via Scan like any other gap,
                                    and the file is tagged with a ".missing-marks-..." suffix if
                                    any are still unresolved afterward. The abort is what only
                                    applies to a fresh, from-scratch detection run, the same
                                    restriction as --early-abort; the hunt for the leading
                                    numbers is not restricted that way, so a --verify recovery
                                    or a ".missing-marks" resume keeps looking for them.
              --no-trailing-scan    Do not transcribe the audio after the last chapter found
                                    (default: the scan runs). Scan spots a missing chapter as a
                                    hole in the number sequence, which needs a known chapter on
                                    either side of it - so a chapter missing after the last one
                                    found is the one case nothing else can notice, and the file
                                    would be written out looking complete, with no missing-number
                                    list and no ".missing-marks" tag. The trailing scan closes that
                                    hole, at the price of transcribing a final chapter's worth of
                                    audio on every file, whether or not anything is wrong: there
                                    are no expected numbers to satisfy here, so it can never stop
                                    early. Switch it off for a library you already know is sound,
                                    or use --chapter-count instead, which answers the same question
                                    for far less time. Nothing is scanned anyway when no chapter
                                    was found at all, after an --early-abort or
                                    --expected-start-chapter abort, or under
                                    --ignore-chapter-numbers, which does away with Scan
                                    altogether.
              --no-denoise          Do not re-read a garbled announcement through the built-in
                                    speech denoiser (default: it may). On a dull-sounding
                                    recording the recognizer sometimes writes a chapter's number
                                    but loses the word beside it - "1. The Long Road" where the
                                    narrator said "Chapter one, The Long Road" - and the chapter
                                    is then missed with nothing in the output to show for it.
                                    Where that happens, and only there, the window is read once
                                    more through a denoiser first. It costs one extra decode on
                                    the few windows that fail this way, never moves a mark that
                                    was already found, and does not run at all on a book whose
                                    audio is clear enough not to need it.
          -N, --max-chapter-number <n>
                                    Highest chapter number this book plausibly has (default: 200,
                                    counted from --expected-start-chapter). A detected chapter
                                    numbered above <n> is discarded on the spot as a mishearing.
                                    Raise it for a book that really runs longer - everything above
                                    the cap is dropped silently. Lower it to roughly the real count
                                    when you know it: the default already throws away a misheard
                                    "chapter 510", but a misheard "chapter 150" in a
                                    twelve-chapter book sits under it, becomes a mark of its own
                                    and pushes the real chapters behind it out of the sequence. Not
                                    to be confused with --max-chapters, which counts a file's
                                    pre-existing marks rather than the numbers heard in the
                                    audio.
              --chapter-count <n>   How many numbered chapters this book has, exactly (default:
                                    none - whatever the last number heard turns out to be). Takes
                                    exactly one file, never a directory: it is a statement about
                                    one particular book. A chapter missing after the last one
                                    found is the one thing nothing else here can notice - a gap is
                                    spotted as a hole in the number sequence, and a hole at the
                                    very end has nothing above it to compare against. Told the
                                    count, the run knows which numbers are still owed and hunts
                                    only those, stopping the moment they turn up. The trailing
                                    scan answers the same question by transcribing the whole tail
                                    on spec, so giving a count switches that scan off. Any chapter
                                    numbered above the count is discarded as a mishearing, so this
                                    replaces --max-chapter-number rather than
                                    combining with it. Reaching the count does not end the
                                    search: an epilogue or a --custom phrase may still follow.
                                    Counted from --expected-start-chapter where that is given.
              --ignore-chapter-numbers
                                    Detect chapter announcements as usual, but form no opinion about
                                    the numbers in them. Every announcement heard becomes a mark
                                    where it is heard, keeping whatever number was spoken in its
                                    title, and no sequence gap is ever found or filled: passes 2.5
                                    and 3 never run and no file is tagged ".missing-marks". For
                                    books that restart their count per part, or number nothing at
                                    all. Cannot be combined with --upgrade-model,
                                    --expected-start-chapter, --max-chapter-number,
                                    --chapter-count or --verify.
          -V, --verify              Check pre-existing chapter marks against the audio
                                    instead of trusting them blindly: a short window around
                                    each mark is probed for the chapter phrase and the
                                    expected number. Marks that all check out are left
                                    alone; where some fail, the confirmed ones are kept and
                                    only the stretches around the failures are redetected;
                                    where nearly all fail, the file is left untouched with a
                                    warning, since marks that fail in bulk usually mean
                                    something other than one numbered chapter each. A file
                                    already rejected by --max-chapters skips verification and
                                    stays bogus. Cannot be combined with --force or --import.
              --fix                 Requires --verify. Where a mark's announcement is confirmed
                                    but the mark sits a little away from it, move the mark
                                    onto it and rewrite the file, instead of only reporting that
                                    it checked out. Only a nudge: a mark already within a
                                    quarter of a second is left alone (rewriting a whole audiobook
                                    for that is not worth it), and one more than 30 seconds from
                                    its announcement is left alone too and reported - a mark that
                                    far out is not one that drifted but one that means something
                                    else, and dragging it onto the nearest matching phrase would
                                    destroy information rather than correct it. Marks that
                                    could not be confirmed at all are not affected; those still go
                                    to --verify's usual gap recovery. Marks are placed by
                                    re-transcribing the audio at them, which is a shade less exact
                                    than a full detection run - it has no silence scan to anchor
                                    against - so a chapter that matters to the last tenth of a
                                    second still wants --force without --verify.
          -h, --verify-threshold <n>
                                    Requires --verify. Sets the "nearly all failed" line
                                    explicitly: more than <n> failures leaves the file
                                    untouched with a warning instead of recovering the
                                    stretches around them. Without this option the line is
                                    drawn where the failures start to outnumber the confirmed
                                    ones.

        Output & review:
          -d, --dry-run             Run detection but write nothing; print the chapters that
                                    would be written (timestamps, numbers, titles) instead.
          -E, --export              Also write detected chapters to a sidecar file next to
                                    the audio file (<file>.chapters.ffmeta by default, or
                                    <file>.chapters.txt with --simple-metadata), for manual
                                    review or correction. Combinable with --dry-run. Written
                                    for a file detection completed normally - not for one
                                    left with an unresolved gap, a resumed ".missing-marks"
                                    file, or a --verify --fix rewrite, all of which change
                                    the file's name as they write it.
          -I, --import              Skip Whisper detection; write chapters from a previously
                                    exported sidecar file instead. Since nothing is detected,
                                    the detection options have no effect and are rejected:
                                    --lang, --chapter-phrase, --prologue-phrase,
                                    --epilogue-phrase, --custom, --custom-file,
                                    --ignore-chapter-numbers, --model, --upgrade-model,
                                    --mark-before-jingle, --jingle-first, --quick-marks,
                                    --mark-lead,
                                    --min-silence-length, --noise-floor, --early-abort,
                                    --expected-start-chapter, --max-chapter-number,
                                    --chapter-count, --no-trailing-scan, --no-denoise, --verify,
                                    --named-mark-distance, --chapter-title, --part-title,
                                    --intro-title, --prologue-title and --epilogue-title.
                                    Also mutually exclusive with --export, --revert,
                                    --cleanup and --no-op.
          -S, --simple-metadata     Use a plain "H:MM:SS.fff  Title" sidecar format instead
                                    of FFMETADATA for --export/--import. Requires one of them.

        File & backup management:
          -b, --backup              Keep the original file with the added suffix ".bak". A .bak
                                    left by an earlier run is kept as it is, not replaced.
          -R, --revert              Restore backups: for every supported audio file with an
                                    added ".bak" suffix, delete the corresponding original and
                                    rename the .bak file back. Combinable with --cleanup,
                                    --recurse, --filter and the output options, but nothing else.
              --cleanup             Housekeeping instead of processing: undo the traces earlier
                                    runs left in the selected folder(s), printing a line for
                                    every change. Leftover temporary files are deleted (an
                                    original left parked by an interrupted write is put back
                                    instead), ".debug.log" logs and the progress files of
                                    interrupted batch runs are deleted, files tagged
                                    ".missing-marks-..." are renamed back, and ".bak" backups
                                    are deleted - but only where the file they back up is
                                    sitting next to them and runs the same length, so this can
                                    never throw away the only copy of anything. Add --revert to
                                    restore the backups over their files instead of deleting
                                    them. Nothing is touched before you have seen the list and
                                    confirmed it; --yes confirms in advance, and --revert needs
                                    no confirmation at all, since no backup is then deleted (the
                                    leftovers above still are). Combinable with
                                    --revert, --yes, --recurse, --filter and the output options,
                                    but nothing else.
              --yes                 Answer --cleanup's confirmation prompt with "yes" in
                                    advance, for a scripted cleanup with no console to ask at.
                                    Required there, since a cleanup that cannot ask and was not
                                    told refuses to run. Not needed with --cleanup --revert,
                                    which deletes no backup.
          -O, --no-op               List every file --filter (and --recurse) would select, then
                                    exit without loading a Whisper model, invoking ffmpeg or
                                    touching any file. A quick way to check that a --filter
                                    regexp or extension list actually matches the intended files
                                    before a real run. Requires --filter; combinable with
                                    --recurse and the output options, but nothing else.
              --run-before <cmd>    Run a shell command for each file just before it is worked
                                    on, and only for a file this run actually works on: a
                                    file skipped (e.g. for already carrying marks) runs
                                    neither hook. The file is re-probed afterwards, so a
                                    command that rewrites it is accounted for; a non-zero
                                    exit skips the file with a warning.
              --run-after <cmd>     Run a shell command for each file once it is finished.
                                    Skipped under the same conditions as --run-before, and
                                    additionally for a file left tagged ".missing-marks",
                                    which a later run is expected to pick up again.
                                    Both take "$..." placeholders naming parts of the file's
                                    path - "$1" its name, "$0" its name without the
                                    extension, "$99" its whole path, "$-1" its folder - and
                                    quote them for the shell as needed. Under --dry-run the
                                    command is printed rather than run. See doc/manual.md for
                                    the whole placeholder syntax, with examples.
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
                                    --force and --ignore-chapter-numbers govern between them.

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
          -B, --no-bar              Do not display the progress bar; per-file summary lines are
                                    printed in the same timestamped format as --verbose logs.
              --color <mode>        Colorize the progress bar and the --summary block (nothing
                                    else - log lines and per-file result lines always stay
                                    plain, as does a --log-file's copy of anything): "auto"
                                    (default), "always" or "never". "auto" switches color off
                                    when the output is redirected, when NO_COLOR is set, and on
                                    Unix unless TERM names a 16-color terminal such as
                                    "xterm-256color". Use "always" for a terminal it misjudges,
                                    such as Git Bash on Windows, a CI log, or a modern terminal
                                    still calling itself plain "xterm".
          -s, --summary             Print a summary at the end: file counts, total and average
                                    processing time, the confidence spread across every mark
                                    written, the shortest silence and longest jingle seen before
                                    a chapter, and how much audio Whisper was fed (with its
                                    transcription speed) - then a list of every file skipped
                                    and why, of every file no chapters were found in, of every
                                    file still missing chapter marks, and of every file carrying
                                    marks read below 0.50 confidence, which are the ones worth a
                                    manual check.

        Performance:
          -C, --cpu-only            Force Whisper onto the CPU backend instead of the fastest
                                    available hardware acceleration. The Silero VAD pre-pass
                                    already always runs on CPU regardless of this option, so it
                                    only affects Whisper. Useful to leave a GPU free for other
                                    work, or to sidestep a flaky/unsupported GPU backend.
              --use-gpu <name>      Run Whisper on the GPU whose name contains <name>, matched
                                    case-insensitively, e.g. "--use-gpu gtx" or "--use-gpu uhd".
                                    See --list-gpus for the names on this machine. A number is
                                    read as a device index if the machine has one, which is only
                                    needed for two identical cards. Without this option a single
                                    discrete GPU is preferred automatically, so it is normally
                                    needed only to force the integrated one, or to choose among
                                    several discrete cards. The chosen GPU is named in the
                                    startup line either way. Vulkan only; the CUDA backend keeps
                                    its own device 0.
              --vad-threads <n|auto>
                                    Threads for the voice-activity pre-pass of Analyze (default:
                                    auto - one per physical CPU core). Each thread holds about
                                    11 minutes of decoded audio while it works, so more of them
                                    also means more memory. "1" runs the pre-pass as a single
                                    uninterrupted stream.
              --whisper-threads <n|auto>
                                    Threads for Whisper transcription (default: auto - one per
                                    physical CPU core). Mostly a CPU-backend concern; on a GPU
                                    backend the recognition itself runs on the GPU.

        Info:
          -?, --help                Show this help.
              --version             Show version information.
              --list-gpus           List this machine's Vulkan GPUs with their names, as
                                    --use-gpu matches them, then exit.

        Short options without parameters may be collapsed, e.g. "-rb" equals "-r -b".

        {FfmpegNote}
        """;
}
