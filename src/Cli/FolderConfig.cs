// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Reflection;
using ABChapterize.Errors;

namespace ABChapterize.Cli;

/// <summary>
/// Per-folder settings: a <c>.abchapterize-config</c> holding options and a
/// <c>.abchapterize-custom</c> holding <c>--custom</c> mappings, picked up automatically for the
/// files in that folder. A shelf of German science fiction and a shelf of English fantasy want
/// different phrases, and saying so once per shelf beats saying it on every command line.
/// </summary>
/// <remarks>
/// Layered outermost folder first, so a sub-folder's settings win over its parent's and the
/// command line wins over all of them - the same precedence <see cref="ConfigFile"/> gives an
/// explicit <c>--config</c>, and implemented the same way: the folders' options are simply put in
/// front of the command line and the whole thing re-parsed, so there is nothing here that knows
/// what an option means.
/// <include file='../../notes/Cli/FolderConfig.xml' path='doc/member[@name="FolderConfig"]/*' />
/// </remarks>
internal static class FolderConfig
{
    /// <summary>Name of the per-folder options file.</summary>
    internal const string ConfigName = ".abchapterize-config";

    /// <summary>Name of the per-folder <c>--custom</c> mapping file.</summary>
    internal const string CustomName = ".abchapterize-custom";

    /// <summary>
    /// The only settings a per-folder file may change: everything whose whole effect is on how one
    /// book is read. Anything else is refused.
    /// </summary>
    /// <remarks>
    /// An allow-list rather than a list of what is forbidden, so it fails closed: an option added
    /// later is refused in a per-folder file until somebody decides it belongs here, which is the
    /// safe way round for a list nobody will remember to maintain. The line is drawn at what a
    /// folder can honestly change - a run holds one Whisper model in memory, writes to one log,
    /// works in one mode and selected its files before any folder was read, so a folder asking for
    /// a second model or a different <c>--filter</c> could only be obeyed by discarding what the run
    /// is; a folder asking for a different chapter phrase is asking for exactly what this is for.
    /// <para>
    /// Names <em>properties</em>, not options, and the guard compares resolved values - so
    /// <c>-m turbo</c> and <c>--model turbo</c> are caught alike, and a new spelling of an existing
    /// setting needs no change here. The derived properties are listed alongside the ones they are
    /// derived from (<see cref="CliOptions.DefaultProfile"/> follows <c>--lang</c>,
    /// <see cref="CliOptions.EffectiveMaxChapterNumber"/> follows <c>--max-chapter-number</c> and
    /// <c>--chapter-count</c>), because they change when their inputs do and would otherwise trip
    /// the guard on a perfectly legal file.
    /// </para>
    /// </remarks>
    internal static readonly HashSet<string> PerFile =
    [
        // Language, and the profile that follows from it.
        "Language", "AutoLanguage", "DefaultProfile",
        // What is listened for, and what the marks are called.
        "ChapterPhrase", "Title", "PartTitle", "IntroTitle",
        "ProloguePhrase", "PrologueTitle", "EpiloguePhrase", "EpilogueTitle", "CustomMappings",
        // Where a mark lands.
        "MarkLeadSeconds", "PreciseMark", "QuickMarks", "MarkBeforeJingle",
        "NamedMarkDistanceSeconds",
        // Where the tool looks, and how hard.
        "MinSilenceSeconds", "AutoMinSilence", "AdaptiveFloorSeconds", "StoredSilenceFloorSeconds",
        "ProbeSilences", "NoiseFloorDb", "AutoNoiseFloor", "JingleFirst", "Denoise",
        "TrailingScan", "EarlyAbortMinutes",
        // What the chapter numbering is expected to look like.
        "ExpectedStartChapter", "LastExpectedChapter", "ChapterCount",
        "MaxChapterNumber", "EffectiveMaxChapterNumber",
        // --ignore-chapter-numbers is deliberately NOT here, though it reads like detection:
        // FileProcessor also consults it to decide whether a ".missing-marks" file may be
        // resumed, which happens before any of this and under the run's own options. Allowing
        // it per folder would honour half of it, which is worse than refusing it outright.
        // Not settings: the command line itself, which necessarily differs once a folder's
        // options have been put in front of it, and the fingerprint derived from it. Note the
        // run's own fingerprint is what BatchProgress records, not this one - so a per-folder
        // file edited between two halves of an interrupted batch does not by itself make the
        // finished files be done again. Documented as a limitation rather than fixed: the
        // alternative is a per-file checkpoint identity, a much larger change than the hole.
        "RawArguments", "RunFingerprint",
    ];

    /// <summary>The option a reader would recognize a guarded property by, where the two are not
    /// obviously the same word. Only for the error message.</summary>
    private static readonly Dictionary<string, string> OptionNames = new()
    {
        ["Model"] = "--model", ["UpgradeModel"] = "--upgrade-model",
        ["CpuOnly"] = "--cpu-only", ["UseGpu"] = "--use-gpu",
        ["VadThreads"] = "--vad-threads", ["WhisperThreads"] = "--whisper-threads",
        ["EffectiveVadThreads"] = "--vad-threads", ["EffectiveWhisperThreads"] = "--whisper-threads",
        ["Recurse"] = "--recurse", ["FilterRegex"] = "--filter", ["FilterExtensions"] = "--filter",
        ["EffectiveExtensions"] = "--filter",
        ["MaxChapters"] = "--max-chapters", ["Force"] = "--force", ["Backup"] = "--backup",
        ["Verify"] = "--verify", ["Fix"] = "--fix", ["VerifyFailThreshold"] = "--verify-threshold",
        ["Import"] = "--import", ["Export"] = "--export", ["SimpleMetadata"] = "--simple-metadata",
        ["DryRun"] = "--dry-run", ["Revert"] = "--revert", ["Cleanup"] = "--cleanup",
        ["NoOp"] = "--no-op", ["AssumeYes"] = "--yes", ["IgnoreProgress"] = "--ignore-progress",
        ["Quiet"] = "--quiet", ["Verbose"] = "--verbose",
        ["VerboseTranscripts"] = "--verbose-transcripts",
        ["Summary"] = "--summary", ["NoBar"] = "--no-bar", ["Color"] = "--color",
        ["LogFilePath"] = "--log-file", ["Debug"] = "--debug",
        ["RunBefore"] = "--run-before", ["RunAfter"] = "--run-after",
        ["TuningChanges"] = "--set:",
        ["UpgradeModelIsBetter"] = "--upgrade-model", ["UpgradeModelIsWorse"] = "--upgrade-model",
        ["Targets"] = "the file arguments",
        ["Abs"] = "--abs", ["UsesAbs"] = "--abs", ["AbsPushOnly"] = "--abs-push-only",
        ["AbsPush"] = "--abs-push",
        ["AbsServer"] = "--abs-url", ["AbsTemp"] = "--abs-temp",
        ["AbsSelectors"] = "the book selectors",
    };

    /// <summary>
    /// The folders whose settings apply to <paramref name="file"/>, outermost first: from the
    /// target that brought the file in, down to the folder holding it.
    /// </summary>
    /// <remarks>
    /// A parent is only consulted when the run reached the file <em>through</em> it, which is what
    /// makes the scope the user's own: without <c>--recurse</c> a target directory yields only its
    /// own files, so the chain is that one folder, and a file named directly on the command line
    /// gets its own folder and nothing above it. Nothing outside what was asked for is ever read -
    /// a stray config file two levels up in someone's library cannot reach a run that did not
    /// descend from there.
    /// </remarks>
    /// <param name="file">Absolute path of the audio file.</param>
    /// <param name="targetRoot">The target the file was found through: the directory given on the
    /// command line, or the file itself when it was named directly.</param>
    internal static List<string> FoldersFor(string file, string targetRoot)
    {
        var own = Path.GetDirectoryName(Path.GetFullPath(file));
        if (own is null)
            return [];
        var root = Directory.Exists(targetRoot)
            ? Path.GetFullPath(targetRoot)
            : Path.GetDirectoryName(Path.GetFullPath(targetRoot)) ?? own;

        var chain = new List<string>();
        for (var dir = own; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            chain.Add(dir);
            if (CliOptions.PathComparer.Equals(CliOptions.NormalizePath(dir),
                                               CliOptions.NormalizePath(root)))
                break;
        }
        // The walk started at the file and stops at the root, so it is inside-out; and if the file
        // turned out not to be under the root at all (a symlinked target, say), fall back to its
        // own folder rather than to every folder up to the drive.
        if (!chain.Any(d => CliOptions.PathComparer.Equals(CliOptions.NormalizePath(d),
                                                           CliOptions.NormalizePath(root))))
            return [own];
        chain.Reverse();
        return chain;
    }

    /// <summary>
    /// The option tokens the folders contribute, outermost folder first - a
    /// <c>.abchapterize-config</c>'s own lines, and a <c>--custom-file</c> for a
    /// <c>.abchapterize-custom</c>.
    /// </summary>
    /// <param name="folders">The chain from <see cref="FoldersFor"/>.</param>
    /// <exception cref="CliError">Thrown for an unreadable or malformed file, named with its
    /// line.</exception>
    internal static List<string> TokensFor(IEnumerable<string> folders)
    {
        var tokens = new List<string>();
        foreach (var folder in folders)
        {
            var config = Path.Combine(folder, ConfigName);
            if (File.Exists(config))
            {
                string[] lines;
                try
                {
                    lines = File.ReadAllLines(config);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                               or ArgumentException or NotSupportedException)
                {
                    throw new CliError($"Cannot read \"{config}\": {ex.Message}");
                }
                for (var n = 0; n < lines.Length; n++)
                    if (ConfigFile.LineTokens(lines[n], $"\"{config}\", line {n + 1}") is { } line)
                        tokens.AddRange(line);
            }

            var custom = Path.Combine(folder, CustomName);
            if (File.Exists(custom))
                tokens.AddRange(["--custom-file", custom]);
        }
        return tokens;
    }

    /// <summary>
    /// The options one file is to be detected with: the run's own, with any
    /// <c>.abchapterize-config</c> and <c>.abchapterize-custom</c> along its folder chain layered
    /// underneath.
    /// </summary>
    /// <remarks>
    /// Returns the run's own instance unchanged - not a copy - when no folder in the chain carries
    /// either file, which is the overwhelmingly common case and keeps a run that uses none of this
    /// exactly as it was.
    /// </remarks>
    /// <param name="run">The options the command line resolved to.</param>
    /// <param name="file">Absolute path of the audio file.</param>
    /// <param name="targetRoot">The target the file was found through; see
    /// <see cref="FoldersFor"/>.</param>
    /// <exception cref="AppError">Thrown for an unreadable or malformed per-folder file, or one
    /// that tried to change a setting outside <see cref="PerFile"/>.</exception>
    internal static CliOptions ResolveForFile(CliOptions run, string file, string targetRoot)
    {
        var folders = FoldersFor(file, targetRoot);
        List<string> tokens;
        try
        {
            tokens = TokensFor(folders);
        }
        catch (CliError ex)
        {
            // TokensFor and ConfigFile.LineTokens report a bad file as a command line error, which
            // is right when --config named it. Reached from here it is not: nothing is wrong with
            // the command line, a file on disk is unreadable or malformed, and the usage text a
            // CliError prints would only be in the way.
            throw new AppError(ex.Message);
        }
        if (tokens.Count == 0)
            return run;
        // The folders' options go in front of the command line, so the command line still wins -
        // and the targets, which end that array, still end this one.
        var perFile = CliOptions.Parse([.. tokens, .. run.RawArguments])
                      ?? throw new AppError($"Could not resolve the settings for \"{file}\".");
        RejectRunWideDifferences(run, perFile, folders);
        return perFile;
    }

    /// <summary>
    /// Refuses a per-folder file that changed something belonging to the run as a whole.
    /// </summary>
    /// <param name="run">The options the command line resolved to.</param>
    /// <param name="perFile">The same with the folders' settings layered under it.</param>
    /// <param name="folders">The chain those settings came from, for the error message.</param>
    /// <exception cref="AppError">Thrown naming the option that may not be set per folder. An error
    /// rather than a warning: a run that quietly ignored half of a folder's settings would be
    /// indistinguishable from one that honoured them.</exception>
    internal static void RejectRunWideDifferences(
        CliOptions run, CliOptions perFile, IEnumerable<string> folders)
    {
        foreach (var p in typeof(CliOptions).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (PerFile.Contains(p.Name) || p.GetIndexParameters().Length > 0)
                continue;
            if (Same(p.GetValue(run), p.GetValue(perFile)))
                continue;
            var option = OptionNames.TryGetValue(p.Name, out var named) ? named : p.Name;
            var files = folders.Select(f => Path.Combine(f, ConfigName)).Where(File.Exists).ToList();
            var where = files.Count switch
            {
                0 => ConfigName,
                1 => files[0],
                _ => "one of " + string.Join(", ", files),
            };
            throw new AppError(
                $"{option} belongs to the whole run and cannot be set per folder, " +
                $"but {where} sets it. " +
                "A per-folder file may change how a book is read, not what the run is.");
        }
    }

    /// <summary>Value equality that also handles the list-valued properties.</summary>
    /// <param name="a">The run's value.</param>
    /// <param name="b">The per-file value.</param>
    private static bool Same(object? a, object? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        if (a is System.Collections.IEnumerable ea and not string &&
            b is System.Collections.IEnumerable eb and not string)
            return ea.Cast<object>().Select(x => x?.ToString())
                .SequenceEqual(eb.Cast<object>().Select(x => x?.ToString()));
        return a.ToString() == b.ToString();
    }
}
