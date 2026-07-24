// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Reflection;
using System.Text.RegularExpressions;

namespace ABChapterize;

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

    /// <summary>Whisper model selector (--model / -m): tiny, base, small, medium, turbo or large.</summary>
    public string Model { get; private set; } = "turbo";

    /// <summary>
    /// Whisper model selector used for pass 3 (gap filling) only (--pass3-model / -M); defaults
    /// to <see cref="Model"/> when not given, so pass 3 uses the same model as pass 2 unless the
    /// user asks for a different one. A lighter model can speed pass 3 up ("I'll likely fix the
    /// stragglers by hand anyway"), a heavier one ("large") can make one last, best-effort attempt
    /// at the chapters the main model missed. The pass-3 model is loaded (and downloaded) lazily,
    /// only when a file actually reaches pass 3.
    /// </summary>
    public string Pass3Model { get; private set; } = "turbo";

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
    /// Anchors the chapter mark to a jingle/pause preceding the announcement instead of the
    /// default fixed offset (--mark-before-jingle / -j): 0.5 s before a leading silence, or -
    /// absent one - at a silence-less jingle's own VAD-detected start. This is the exact
    /// placement rule this tool's original "--jingle" mode used, preserved unchanged; only its
    /// name and default-off/on status changed, since the VAD pre-pass and widened jingle
    /// probing this placement relies on now run unconditionally (see
    /// <see cref="RunVadPrePass"/>). Without this option, see <see cref="ChapterDetector"/>'s
    /// <c>DefaultMarkLeadSeconds</c> for the placement used instead.
    /// <para><b>Experimental.</b></para>
    /// </summary>
    public bool MarkBeforeJingle { get; private set; }

    /// <summary>
    /// Verifies (and if necessary, corrects) every default-mode mark - i.e. one not produced by
    /// <see cref="MarkBeforeJingle"/> - by directly re-transcribing the audio at the mark instead
    /// of trusting the VAD/duration heuristics that produced it (--precise-mark / -p): if the
    /// chapter phrase is the first thing heard there, the mark is left alone (the common case,
    /// and the only extra cost paid for a chapter that was already right). If not - typically
    /// because the mark landed on a jingle's own spurious VAD "speech" blip rather than the real
    /// announcement - each subsequent VAD speech-segment start after the mark is checked the same
    /// way in turn until one succeeds and the next one after it fails again; only that
    /// success-then-fail pattern confirms the phrase truly begins at the earlier candidate, rather
    /// than at some other unrelated false positive further inside the jingle. If that forward
    /// search finds nothing, the same check runs backward through VAD speech-segment starts before
    /// the mark instead, for the rarer opposite failure - the mark landing generously past the
    /// true announcement rather than short of it. A chapter whose phrase can never be confirmed
    /// either way keeps its original mark rather than guessing. Substantially more expensive than
    /// the default algorithm alone - most of all for chapters preceded by a jingle with several
    /// spurious VAD blips, since each one needs its own extra transcription - so this is off by
    /// default; see <see cref="ChapterDetector"/> for the mechanics. Cannot be combined with
    /// <see cref="MarkBeforeJingle"/>, which replaces default-mode placement with its own,
    /// unrelated jingle/silence anchor - there is nothing here left for this to correct.
    /// <para><b>Experimental.</b></para>
    /// </summary>
    public bool PreciseMark { get; private set; }

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
    /// necessarily representative) - resizes the probe window to 1.25x the longest jingle
    /// actually observed so far plus margin (both up and down as the observed maximum changes),
    /// capped at the original ceiling. Chapters with no jingle (or an ultra-short one) are
    /// excluded from that, since some audiobooks only play the jingle for some chapters and
    /// such a chapter says nothing about how long the window needs to be for one that does have
    /// a full jingle. "auto" can also be given explicitly for clarity.
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

    /// <summary>Print a run summary with file counts and timings at the end (--summary / -s).</summary>
    public bool Summary { get; private set; }

    /// <summary>
    /// Run full detection but write nothing (--dry-run / -d): the chapters that would be
    /// written are printed (timestamps, numbers and titles) instead. Lets a result be
    /// reviewed before trusting it with a real file, without needing --backup/--revert.
    /// </summary>
    public bool DryRun { get; private set; }

    /// <summary>
    /// Write detected chapters to a sidecar file alongside the output (--export / -e), in
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

    /// <summary>The file or directory to process (last command line argument).</summary>
    public string TargetPath { get; private set; } = "";

    /// <summary>True when the target path refers to a directory, false when it refers to a file.</summary>
    public bool TargetIsDirectory { get; private set; }

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

    private static readonly string[] ModelNames = ["tiny", "base", "small", "medium", "turbo", "large"];

    /// <summary>Maps every short option letter to its long option name.</summary>
    private static readonly Dictionary<char, string> ShortOptions = new()
    {
        ['r'] = "--recurse", ['b'] = "--backup", ['f'] = "--force", ['j'] = "--mark-before-jingle",
        ['p'] = "--precise-mark",
        ['q'] = "--quiet", ['v'] = "--verbose", ['T'] = "--verbose-transcripts", ['s'] = "--summary",
        ['l'] = "--lang", ['c'] = "--chapter-phrase", ['m'] = "--model", ['M'] = "--pass3-model",
        ['x'] = "--max-chapters", ['F'] = "--filter", ['X'] = "--max-jingle-length",
        ['n'] = "--min-silence-length", ['t'] = "--title", ['i'] = "--intro-title",
        ['R'] = "--revert", ['B'] = "--no-bar", ['d'] = "--dry-run",
        ['E'] = "--export", ['I'] = "--import", ['S'] = "--simple-metadata",
        ['J'] = "--jobs", ['V'] = "--verify", ['C'] = "--cpu-only", ['O'] = "--no-op",
    };

    // Tracks which value options were given explicitly, for semantic validation and
    // for applying the --lang-dependent defaults only when the user did not choose.
    private bool _langSet, _phraseSet, _modelSet, _pass3ModelSet, _maxSet, _titleSet, _introSet, _jingleLenSet, _minSilenceSet;

    /// <summary>
    /// File extensions of the container formats that ffmpeg can both read and write chapter
    /// marks for (verified empirically: mp4/ipod, ID3v2 mp3, Ogg Opus, Matroska). Notably
    /// absent: .ogg (Vorbis) and .flac - ffmpeg's muxers silently drop chapters for those.
    /// </summary>
    public static readonly string[] SupportedExtensions = [".m4a", ".m4b", ".mp3", ".opus", ".mka"];

    /// <summary>Human-readable list of the supported extensions, e.g. ".m4a/.m4b/.mp3/.opus/.mka".</summary>
    public static string SupportedExtensionsText => string.Join("/", SupportedExtensions);

    /// <summary>
    /// Per-language defaults for the chapter phrase, the title word and the intro chapter
    /// title, applied when --lang is given but --chapter-phrase/--title/--intro-title are
    /// not. "abchapterize -l de buch.m4b" thus looks for "Kapitel" and writes "Intro",
    /// "Kapitel 1", "Kapitel 2", ... without further options. Languages without an entry
    /// keep the English-ish defaults ("chapter", "Chapter", "Intro").
    /// </summary>
    private static readonly Dictionary<string, (string Phrase, string Title, string Intro)> LanguageDefaults = new()
    {
        ["en"] = ("chapter", "Chapter", "Intro"),
        ["de"] = ("Kapitel", "Kapitel", "Intro"),
        ["fr"] = ("chapitre", "Chapitre", "Introduction"),
        ["es"] = ("capítulo", "Capítulo", "Introducción"),
        ["it"] = ("capitolo", "Capitolo", "Introduzione"),
        ["nl"] = ("hoofdstuk", "Hoofdstuk", "Intro"),
        ["tr"] = ("bölüm", "Bölüm", "Giriş"),
        ["pt"] = ("capítulo", "Capítulo", "Introdução"),
        ["pl"] = ("rozdział", "Rozdział", "Wstęp"),
        ["sv"] = ("kapitel", "Kapitel", "Introduktion"),
        ["da"] = ("kapitel", "Kapitel", "Introduktion"),
    };

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

        string NextParam(string optName)
        {
            if (i + 1 >= args.Length)
                throw new CliError($"Option {optName} requires a parameter.");
            return args[++i];
        }

        for (; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--help" or "-?" or "/?")
                return null;

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (!o.TryApplyFlag(arg) && !o.TryApplyValueOption(arg, () => NextParam(arg)))
                    throw new CliError($"Unknown option: {arg}");
            }
            else if (arg.StartsWith('-') && arg.Length > 1)
            {
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
                // First non-option argument must be the last argument (the target path).
                if (i != args.Length - 1)
                    throw new CliError("The file/directory must be the last argument; options must precede it.");
                o.TargetPath = arg;
            }
        }

        if (o.TargetPath.Length == 0)
            throw new CliError("No file or directory specified.");

        // Semantic validation.
        if (o.Revert && (o.Backup || o.Force || o.MarkBeforeJingle || o.PreciseMark || o.DryRun || o._langSet || o._phraseSet || o._modelSet
                         || o._pass3ModelSet || o._maxSet || o._titleSet || o._introSet || o._jingleLenSet || o._minSilenceSet
                         || o.Export || o.Import || o.SimpleMetadata || o.Jobs != null || o.Verify))
            throw new CliError("--revert can only be combined with --recurse and --filter.");

        if (o.NoOp && o.FilterRegex == null && o.FilterExtensions == null)
            throw new CliError("--no-op requires --filter - its purpose is checking that a filter actually matches the intended files.");

        if (o.NoOp && (o.Revert || o.Backup || o.Force || o.MarkBeforeJingle || o.PreciseMark || o.DryRun || o._langSet || o._phraseSet || o._modelSet
                       || o._pass3ModelSet || o._maxSet || o._titleSet || o._introSet || o._jingleLenSet || o._minSilenceSet
                       || o.Export || o.Import || o.SimpleMetadata || o.Jobs != null || o.Verify))
            throw new CliError("--no-op can only be combined with --recurse, --filter and the output options.");

        if (o.Import && o.Export)
            throw new CliError("--import and --export cannot be combined.");

        if (o.Import && (o._langSet || o._phraseSet || o._modelSet || o._pass3ModelSet || o._jingleLenSet || o._minSilenceSet || o.MarkBeforeJingle || o.PreciseMark || o.Verify))
            throw new CliError(
                "--import skips detection entirely, so --lang, --chapter-phrase, --model, --pass3-model, " +
                "--mark-before-jingle, --precise-mark, --max-jingle-length, --min-silence-length and --verify have no effect and cannot be combined with it.");

        if (o.Force && o.Verify)
            throw new CliError(
                "--force and --verify cannot be combined: --force always discards pre-existing " +
                "chapter markings, while --verify decides that based on whether they check out.");

        if (o.MarkBeforeJingle && o.PreciseMark)
            throw new CliError(
                "--mark-before-jingle and --precise-mark cannot be combined: --precise-mark only " +
                "corrects the default (non -j) mark placement, which --mark-before-jingle replaces " +
                "with its own, unrelated jingle/silence anchor.");

        if (o.SimpleMetadata && !o.Export && !o.Import)
            throw new CliError("--simple-metadata requires --export or --import.");

        o.Language = o.Language.ToLowerInvariant();
        if (o.Language != "auto" && !Regex.IsMatch(o.Language, "^[a-z]{2}$"))
            throw new CliError($"Invalid language code \"{o.Language}\": expected a two-letter code like \"en\", or \"auto\".");

        if (!ModelNames.Contains(o.Model.ToLowerInvariant()))
            throw new CliError($"Invalid model \"{o.Model}\": expected one of {string.Join(", ", ModelNames)}.");
        o.Model = o.Model.ToLowerInvariant();

        // The pass-3 model defaults to the main model, so leaving --pass3-model off means pass 3
        // uses the same model as pass 2 - the previous, single-model behavior.
        if (!o._pass3ModelSet)
            o.Pass3Model = o.Model;
        else if (!ModelNames.Contains(o.Pass3Model.ToLowerInvariant()))
            throw new CliError($"Invalid pass-3 model \"{o.Pass3Model}\": expected one of {string.Join(", ", ModelNames)}.");
        o.Pass3Model = o.Pass3Model.ToLowerInvariant();

        if (o.ChapterPhrase.Length == 0)
            throw new CliError("The chapter phrase must not be empty.");

        if (File.Exists(o.TargetPath))
        {
            o.TargetIsDirectory = false;
            if (o.Recurse)
                throw new CliError("--recurse can only be used with a directory, not with a single file.");
            if (!o.Revert)
            {
                var ext = Path.GetExtension(o.TargetPath).ToLowerInvariant();
                if (!SupportedExtensions.Contains(ext))
                    throw new CliError($"Unsupported file type \"{ext}\": only {SupportedExtensionsText} are supported.");
            }
        }
        else if (Directory.Exists(o.TargetPath))
        {
            o.TargetIsDirectory = true;
        }
        else
        {
            throw new CliError($"File or directory not found: {o.TargetPath}");
        }

        // Resolve the run's default profile: for an explicit --lang this localizes the
        // chapter phrase, title word and intro title unless given explicitly (used for
        // every file); with auto-detection this is just the English fallback profile,
        // and ChapterDetector resolves a fresh one per file instead - see ResolveProfile.
        o.DefaultProfile = o.ResolveProfile(o.AutoLanguage ? "en" : o.Language);
        o.ChapterPhrase = o.DefaultProfile.ChapterPhrase;
        o.Title = o.DefaultProfile.Title;
        o.IntroTitle = o.DefaultProfile.IntroTitle;
        o.PhraseRegex = o.DefaultProfile.PhraseRegex;
        o.PhraseHasNumberGroup = o.DefaultProfile.PhraseHasNumberGroup;
        return o;
    }

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
            case "--precise-mark": PreciseMark = true; return true;
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
            case "--model": Model = nextParam(); _modelSet = true; return true;
            case "--pass3-model": Pass3Model = nextParam(); _pass3ModelSet = true; return true;
            case "--max-chapters": MaxChapters = ParseMax(nextParam()); _maxSet = true; return true;
            case "--title": Title = nextParam(); _titleSet = true; return true;
            case "--intro-title": IntroTitle = nextParam(); _introSet = true; return true;
            case "--filter": ParseFilter(nextParam()); return true;
            case "--max-jingle-length": (MaxJingleSeconds, AutoMaxJingle) = ParseJingleLength(nextParam()); _jingleLenSet = true; return true;
            case "--min-silence-length": (MinSilenceSeconds, AutoMinSilence) = ParseMinSilence(nextParam()); _minSilenceSet = true; return true;
            case "--jobs": Jobs = ParseJobs(nextParam()); return true;
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
        if (!double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var s) ||
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
        if (!double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var s) || s < 0.1 || s > 60)
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

    /// <summary>Parses the --max-chapters parameter into a positive integer.</summary>
    private static int ParseMax(string value)
    {
        if (!int.TryParse(value, out var n) || n < 0)
            throw new CliError($"Invalid --max-chapters value \"{value}\": expected a non-negative number.");
        return n;
    }

    /// <summary>
    /// Resolves the chapter phrase, title word and intro title for the given language: an
    /// explicit --chapter-phrase/--title/--intro-title always wins; otherwise the localized
    /// default for <paramref name="language"/> is used (English-ish defaults for languages
    /// without a dedicated entry in <see cref="LanguageDefaults"/>). Called once at parse time
    /// for an explicit --lang (building <see cref="DefaultProfile"/>), and once per file by
    /// <see cref="ChapterDetector"/> when <see cref="AutoLanguage"/> is active.
    /// </summary>
    /// <param name="language">Two-letter language code (not "auto") to resolve defaults for.</param>
    public LanguageProfile ResolveProfile(string language)
    {
        LanguageDefaults.TryGetValue(language, out var defaults);
        var phrase = _phraseSet ? ChapterPhrase : defaults.Phrase ?? "chapter";
        var title = _titleSet ? Title : defaults.Title ?? "Chapter";
        var intro = _introSet ? IntroTitle : defaults.Intro ?? "Intro";
        var (regex, hasGroup) = CompilePhraseRegex(phrase);
        return new LanguageProfile(language, phrase, regex, hasGroup, title, intro);
    }

    /// <summary>
    /// Compiles a chapter phrase into its matching regular expression. A phrase enclosed in
    /// slashes is compiled as-is (case-insensitive); anything else is escaped literally.
    /// </summary>
    /// <param name="chapterPhrase">The raw phrase or "/regexp/".</param>
    /// <exception cref="CliError">Thrown for an invalid regexp.</exception>
    private static (Regex Regex, bool HasNumberGroup) CompilePhraseRegex(string chapterPhrase)
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
                throw new CliError($"Invalid chapter phrase regexp: {ex.Message}");
            }
            return (regex, regex.GetGroupNumbers().Length > 1);
        }

        var pattern = Regex.Escape(chapterPhrase);
        return (new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), false);
    }

    /// <summary>OS-specific note about where ffmpeg/ffprobe are searched (part of the usage info).</summary>
    private static string FfmpegNote => OperatingSystem.IsWindows()
        ? """
          ffmpeg/ffprobe are required. They are searched in %FFMPEG_DIR%\bin (highest priority,
          FFMPEG_DIR points to ffmpeg's base directory), PATH, an "ffmpeg" folder in the current
          directory, next to abchapterize.exe or in the user profile, and common Program Files
          locations.
          """
        : """
          ffmpeg/ffprobe are required. They are searched in $FFMPEG_DIR (highest priority,
          FFMPEG_DIR points to ffmpeg's base directory), PATH, ./ffmpeg, ~/ffmpeg, /usr/bin,
          /usr/local/bin, /opt/ffmpeg, /snap/bin, ~/bin and ~/.local/bin.
          Install e.g. with: sudo apt install ffmpeg
          """;

    /// <summary>Comprehensive usage info printed on --help or on any command line error.</summary>
    public static string UsageText => $"""
        abchapterize {Version} - mark chapter starts in audiobooks using Whisper speech recognition
        Copyright (c) 2026 Jan O. Gretza - MIT license - written with Claude (Anthropic)
        Supported formats: {SupportedExtensionsText} (formats whose containers can hold chapter marks)

        Usage:
          abchapterize [options] <file-or-directory>
          abchapterize -R|--revert [--recurse] [--filter <f>] <file-or-directory>
          abchapterize -O|--no-op --filter <f> [--recurse] <file-or-directory>
          abchapterize --help | -?

        Options (must precede the file/directory argument):
          -r, --recurse             Recursively descend into subdirectories (directories only).
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
          -l, --lang <code|auto>    Two-letter language hint for Whisper, or "auto" (the
                                    default): each file's language is detected from a short
                                    clip and used for that file, falling back to "en" when
                                    the detection is inconclusive. Chapter numbers
                                    transcribed as words - cardinals and ordinals, before or
                                    after the phrase ("chapter two", "Erstes Kapitel") - are
                                    understood in
                                    {string.Join(", ", NumberWordParser.SupportedLanguages)}; digits
                                    ("2.", "2nd", "2e") work in every language. For these
                                    languages, --lang also localizes the defaults of
                                    --chapter-phrase, --title and --intro-title (per-file
                                    with "auto").
          -c, --chapter-phrase <p>  Word/phrase that identifies a chapter start (default:
                                    chapter, localized by --lang).
                                    Enclose in slashes to use a regexp, e.g. "/chapter (\d+)/".
                                    The regexp may contain one capturing group "(\d+)" in place of
                                    the chapter number; otherwise the number is expected to follow
                                    the phrase. Matching is always case-insensitive.
          -m, --model <name>        Whisper model: tiny, base, small, medium, turbo or large
                                    (default: turbo).
          -M, --pass3-model <name>  Whisper model for pass 3 (gap filling) only; same choices as
                                    --model (default: whatever --model is). Use a lighter model to
                                    speed pass 3 up, or "large" for one last best-effort attempt at
                                    the chapters the main model missed. Loaded and downloaded lazily,
                                    only when a file actually reaches pass 3.
          -C, --cpu-only            Force Whisper onto the CPU backend instead of the fastest
                                    available hardware acceleration. The Silero VAD pre-pass
                                    already always runs on CPU regardless of this option, so it
                                    only affects Whisper. Useful to leave a GPU free for other
                                    work, or to sidestep a flaky/unsupported GPU backend.
          -F, --filter <filter>     Only process matching files. Either "/regexp/" - matched
                                    case-insensitively against the whole path of each file -
                                    or a comma-separated list of permissible file extensions,
                                    e.g. "mp3,m4b". One filter of each kind may be given;
                                    they also select which backups --revert restores.
          -f, --force               Discard pre-existing chapter markings. Without --force, files
                                    that already have chapter markings are skipped.
          -x, --max-chapters <n>    If a file has more than <n> pre-existing chapter markings,
                                    they are considered bogus and are discarded.
          -V, --verify              Check pre-existing chapter markings against the audio
                                    instead of trusting them blindly: a short window around
                                    each marking is probed for the chapter phrase and the
                                    expected number. Markings that all check out are left
                                    alone; if any fails, they are discarded and the file goes
                                    through full detection, same as --force would. A file
                                    already rejected by --max-chapters skips verification and
                                    stays bogus. Cannot be combined with --force or --import.
          -j, --mark-before-jingle  [EXPERIMENTAL] A short jingle may precede the chapter
                                    phrase; anchor the mark to it instead of the default fixed
                                    offset (see --max-jingle-length below). A silence scan and
                                    a voice-activity (VAD) pre-pass already run over the whole
                                    file regardless of this option, so jingles are found
                                    whether or not they are preceded by a silence: when a
                                    silence precedes the jingle, the chapter mark is placed 0.5
                                    seconds before it; when the jingle abuts speech with no
                                    silence (or is itself the only thing separating chapters),
                                    the mark is placed at the start of the jingle instead.
                                    Without this option, the mark is always placed 0.25 seconds
                                    before the chapter phrase, no matter what precedes it.
          -p, --precise-mark        [EXPERIMENTAL] Verify every mark placed without
                                    --mark-before-jingle by re-transcribing the audio right at
                                    it: if the chapter phrase is heard there, the mark is left
                                    alone (the common case - no extra cost beyond that one
                                    check). Otherwise, further candidate positions are checked
                                    the same way until the real onset is confirmed and the mark
                                    is corrected to it; a mark that can never be confirmed this
                                    way is left as originally placed. Substantially slower than
                                    without this option, since it costs one or more extra
                                    Whisper transcriptions per chapter, most of all for ones
                                    preceded by a jingle with several false-positive candidates.
                                    Cannot be combined with --mark-before-jingle.
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
          -q, --quiet               Suppress per-file output; warnings and errors are still shown.
          -v, --verbose             Print processing details as timestamped log lines. Probe,
                                    gap and verify lines stop at their "<length>@<time>" header;
                                    use -T to also see the transcribed segments.
          -T, --verbose-transcripts Like --verbose, but also dumps every Whisper transcript's
                                    segments (to see exactly what the recognizer heard). Implies
                                    --verbose.
          -B, --no-bar              Do not display progress bars; per-file summary lines are
                                    printed in the same timestamped format as --verbose logs.
          -s, --summary             Print a summary at the end: file counts, total and average
                                    processing time.
          -d, --dry-run             Run detection but write nothing; print the chapters that
                                    would be written (timestamps, numbers, titles) instead.
          -E, --export              Also write detected chapters to a sidecar file next to
                                    the audio file (<file>.chapters.ffmeta by default, or
                                    <file>.chapters.txt with --simple-metadata), for manual
                                    review or correction. Combinable with --dry-run.
          -I, --import              Skip Whisper detection; write chapters from a previously
                                    exported sidecar file instead. Cannot be combined with
                                    --lang, --chapter-phrase, --model, --mark-before-jingle,
                                    --max-jingle-length, --min-silence-length or --revert.
          -S, --simple-metadata     Use a plain "H:MM:SS.fff  Title" sidecar format instead
                                    of FFMETADATA for --export/--import. Requires one of them.
          -J, --jobs <n|auto>       Number of files processed concurrently (default: auto -
                                    adjusted between 1 and a hardware-derived ceiling based on
                                    live CPU load). "1" forces strictly sequential processing.
          -t, --title <word>        Word used for chapter titles; the chapter number is appended
                                    (default: Chapter, localized by --lang).
          -i, --intro-title <word>  Title of the chapter mark covering the audio before the
                                    first detected chapter, e.g. a prelude (default: Intro,
                                    localized by --lang, e.g. "Giriş" with --lang tr).
          -?, --help                Show this help.
              --version             Show version information.

        Short options without parameters may be collapsed, e.g. "-rb" equals "-r -b".

        {FfmpegNote}
        """;
}
