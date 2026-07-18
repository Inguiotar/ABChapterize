using System.Text.RegularExpressions;

namespace Chapterize;

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
/// Parsed and validated command line options of the chapterize tool.
/// Use <see cref="Parse"/> to create an instance from raw arguments.
/// </summary>
public sealed class CliOptions
{
    /// <summary>Recursively descend into subdirectories (--recurse / -r).</summary>
    public bool Recurse { get; private set; }

    /// <summary>Keep the original file as "*.bak" (--backup / -b).</summary>
    public bool Backup { get; private set; }

    /// <summary>Restore "*.&lt;ext&gt;.bak" backup files to their original names (--revert).</summary>
    public bool Revert { get; private set; }

    /// <summary>Two-letter ISO 639-1 language hint for Whisper (--lang / -l, default "en").</summary>
    public string Language { get; private set; } = "en";

    /// <summary>Raw chapter phrase or "/regexp/" as given on the command line (--chapter-phrase / -c); the default is localized by --lang.</summary>
    public string ChapterPhrase { get; private set; } = "chapter";

    /// <summary>Whisper model selector (--model / -m): tiny, base, small, medium, turbo or large.</summary>
    public string Model { get; private set; } = "turbo";

    /// <summary>Discard pre-existing chapter markings instead of skipping the file (--force / -f).</summary>
    public bool Force { get; private set; }

    /// <summary>
    /// Maximum plausible number of pre-existing chapter markings (--max-chapters / -x).
    /// Files exceeding it get their markings discarded as bogus. Null when not specified.
    /// </summary>
    public int? MaxChapters { get; private set; }

    /// <summary>A jingle may precede the chapter phrase; mark chapters 0.5 s before it (--jingle / -j).</summary>
    public bool Jingle { get; private set; }

    /// <summary>
    /// Maximum expected jingle duration in seconds (--max-jingle-length / -X, default 45).
    /// With --jingle, the probe window after each silence spans this duration plus a flat
    /// 5-second margin for the chapter phrase itself.
    /// </summary>
    public double MaxJingleSeconds { get; private set; } = 45;

    /// <summary>
    /// Minimum silence duration in seconds that counts as a potential chapter break
    /// (--min-silence-length / -n, default 1.5). Every such silence triggers a Whisper probe,
    /// so higher values can drastically reduce the number of probes.
    /// </summary>
    public double MinSilenceSeconds { get; private set; } = 1.5;

    /// <summary>Suppress per-file output; warnings and errors are still shown (--quiet / -q).</summary>
    public bool Quiet { get; private set; }

    /// <summary>Print a run summary with file counts and timings at the end (--summary / -s).</summary>
    public bool Summary { get; private set; }

    /// <summary>Word used to build chapter titles; the chapter number is appended (--title / -t, default "Chapter", localized by --lang).</summary>
    public string Title { get; private set; } = "Chapter";

    /// <summary>
    /// Title of the synthetic chapter covering the audio before the first detected chapter
    /// (--intro-title / -i). Audiobooks usually start with a prelude, so the first detected
    /// chapter must not be moved to 0:00; instead this intro chapter is prepended at 0:00
    /// when the first chapter starts later. Defaults to the title word followed by "0".
    /// </summary>
    public string IntroTitle { get; private set; } = "";

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

    private static readonly string[] ModelNames = ["tiny", "base", "small", "medium", "turbo", "large"];

    /// <summary>
    /// File extensions of the container formats that ffmpeg can both read and write chapter
    /// marks for (verified empirically: mp4/ipod, ID3v2 mp3, Ogg Opus, Matroska). Notably
    /// absent: .ogg (Vorbis) and .flac - ffmpeg's muxers silently drop chapters for those.
    /// </summary>
    public static readonly string[] SupportedExtensions = [".m4a", ".m4b", ".mp3", ".opus", ".mka"];

    /// <summary>Human-readable list of the supported extensions, e.g. ".m4a/.m4b/.mp3/.opus/.mka".</summary>
    public static string SupportedExtensionsText => string.Join("/", SupportedExtensions);

    /// <summary>
    /// Per-language defaults for the chapter phrase and the title word, applied when --lang
    /// is given but --chapter-phrase/--title are not. "chapterize -l de buch.m4b" thus looks
    /// for "Kapitel" and writes "Kapitel 1", "Kapitel 2", ... without further options.
    /// </summary>
    private static readonly Dictionary<string, (string Phrase, string Title)> LanguageDefaults = new()
    {
        ["en"] = ("chapter", "Chapter"),
        ["de"] = ("Kapitel", "Kapitel"),
        ["fr"] = ("chapitre", "Chapitre"),
        ["es"] = ("capítulo", "Capítulo"),
        ["it"] = ("capitolo", "Capitolo"),
        ["nl"] = ("hoofdstuk", "Hoofdstuk"),
        ["tr"] = ("bölüm", "Bölüm"),
    };

    /// <summary>Platform-specific name of this executable, for user-facing messages.</summary>
    public static string ExeName => OperatingSystem.IsWindows() ? "chapterize.exe" : "chapterize";

    /// <summary>
    /// Parses and validates the raw command line arguments.
    /// </summary>
    /// <param name="args">Arguments as passed to Main.</param>
    /// <returns>A fully validated options instance, or null when --help / -? was requested.</returns>
    /// <exception cref="CliError">Thrown on any syntax or validation error.</exception>
    public static CliOptions? Parse(string[] args)
    {
        var o = new CliOptions();
        bool langSet = false, phraseSet = false, modelSet = false, maxSet = false, titleSet = false, introSet = false;
        var jingleLenSet = false;
        var minSilenceSet = false;
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
                switch (arg)
                {
                    case "--recurse": o.Recurse = true; break;
                    case "--backup": o.Backup = true; break;
                    case "--revert": o.Revert = true; break;
                    case "--force": o.Force = true; break;
                    case "--jingle": o.Jingle = true; break;
                    case "--quiet": o.Quiet = true; break;
                    case "--summary": o.Summary = true; break;
                    case "--lang": o.Language = NextParam(arg); langSet = true; break;
                    case "--chapter-phrase": o.ChapterPhrase = NextParam(arg); phraseSet = true; break;
                    case "--model": o.Model = NextParam(arg); modelSet = true; break;
                    case "--max-chapters": o.MaxChapters = ParseMax(NextParam(arg)); maxSet = true; break;
                    case "--title": o.Title = NextParam(arg); titleSet = true; break;
                    case "--intro-title": o.IntroTitle = NextParam(arg); introSet = true; break;
                    case "--filter": o.ParseFilter(NextParam(arg)); break;
                    case "--max-jingle-length": o.MaxJingleSeconds = ParseJingleLength(NextParam(arg)); jingleLenSet = true; break;
                    case "--min-silence-length": o.MinSilenceSeconds = ParseMinSilence(NextParam(arg)); minSilenceSet = true; break;
                    default: throw new CliError($"Unknown option: {arg}");
                }
            }
            else if (arg.StartsWith('-') && arg.Length > 1)
            {
                // Short options; flags without parameters may be collapsed (e.g. -rb).
                var letters = arg[1..];
                for (var k = 0; k < letters.Length; k++)
                {
                    var c = letters[k];
                    var isLast = k == letters.Length - 1;
                    switch (c)
                    {
                        case 'r': o.Recurse = true; break;
                        case 'b': o.Backup = true; break;
                        case 'f': o.Force = true; break;
                        case 'j': o.Jingle = true; break;
                        case 'q': o.Quiet = true; break;
                        case 's': o.Summary = true; break;
                        case '?': return null;
                        case 'l':
                        case 'c':
                        case 'm':
                        case 'x':
                        case 'F':
                        case 'X':
                        case 'n':
                        case 't':
                        case 'i':
                            if (!isLast)
                                throw new CliError($"Option -{c} takes a parameter and cannot be collapsed with other options ({arg}).");
                            switch (c)
                            {
                                case 'l': o.Language = NextParam($"-{c}"); langSet = true; break;
                                case 'c': o.ChapterPhrase = NextParam($"-{c}"); phraseSet = true; break;
                                case 'm': o.Model = NextParam($"-{c}"); modelSet = true; break;
                                case 'x': o.MaxChapters = ParseMax(NextParam($"-{c}")); maxSet = true; break;
                                case 'F': o.ParseFilter(NextParam($"-{c}")); break;
                                case 'X': o.MaxJingleSeconds = ParseJingleLength(NextParam($"-{c}")); jingleLenSet = true; break;
                                case 'n': o.MinSilenceSeconds = ParseMinSilence(NextParam($"-{c}")); minSilenceSet = true; break;
                                case 't': o.Title = NextParam($"-{c}"); titleSet = true; break;
                                case 'i': o.IntroTitle = NextParam($"-{c}"); introSet = true; break;
                            }
                            break;
                        default: throw new CliError($"Unknown option: -{c}");
                    }
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
        if (o.Revert && (o.Backup || o.Force || o.Jingle || langSet || phraseSet || modelSet || maxSet || titleSet || introSet || jingleLenSet || minSilenceSet))
            throw new CliError("--revert can only be combined with --recurse and --filter.");

        if (jingleLenSet && !o.Jingle)
            throw new CliError("--max-jingle-length requires --jingle.");

        if (!Regex.IsMatch(o.Language, "^[a-zA-Z]{2}$"))
            throw new CliError($"Invalid language code \"{o.Language}\": expected a two-letter code like \"en\".");
        o.Language = o.Language.ToLowerInvariant();

        if (!ModelNames.Contains(o.Model.ToLowerInvariant()))
            throw new CliError($"Invalid model \"{o.Model}\": expected one of {string.Join(", ", ModelNames)}.");
        o.Model = o.Model.ToLowerInvariant();

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

        // Localize the chapter phrase and title word from --lang unless given explicitly;
        // the intro title then follows the (possibly localized) title word.
        if (LanguageDefaults.TryGetValue(o.Language, out var defaults))
        {
            if (!phraseSet)
                o.ChapterPhrase = defaults.Phrase;
            if (!titleSet)
                o.Title = defaults.Title;
        }
        if (!introSet)
            o.IntroTitle = $"{o.Title} 0";

        o.BuildPhraseRegex();
        return o;
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

    /// <summary>Parses the --max-jingle-length parameter into a positive number of seconds.</summary>
    private static double ParseJingleLength(string value)
    {
        if (!double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var s) || s <= 0 || s > 600)
            throw new CliError($"Invalid --max-jingle-length value \"{value}\": expected seconds between 1 and 600.");
        return s;
    }

    /// <summary>Parses the --min-silence-length parameter into a positive number of seconds.</summary>
    private static double ParseMinSilence(string value)
    {
        if (!double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var s) || s < 0.1 || s > 60)
            throw new CliError($"Invalid --min-silence-length value \"{value}\": expected seconds between 0.1 and 60.");
        return s;
    }

    /// <summary>Parses the --max-chapters parameter into a positive integer.</summary>
    private static int ParseMax(string value)
    {
        if (!int.TryParse(value, out var n) || n < 0)
            throw new CliError($"Invalid --max-chapters value \"{value}\": expected a non-negative number.");
        return n;
    }

    /// <summary>
    /// Builds <see cref="PhraseRegex"/> from <see cref="ChapterPhrase"/>. A phrase enclosed in
    /// slashes is compiled as-is (case-insensitive); anything else is escaped literally.
    /// </summary>
    private void BuildPhraseRegex()
    {
        string pattern;
        if (ChapterPhrase.Length > 2 && ChapterPhrase.StartsWith('/') && ChapterPhrase.EndsWith('/'))
        {
            pattern = ChapterPhrase[1..^1];
            try
            {
                PhraseRegex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch (ArgumentException ex)
            {
                throw new CliError($"Invalid chapter phrase regexp: {ex.Message}");
            }
            PhraseHasNumberGroup = PhraseRegex.GetGroupNumbers().Length > 1;
        }
        else
        {
            pattern = Regex.Escape(ChapterPhrase);
            PhraseRegex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            PhraseHasNumberGroup = false;
        }
    }

    /// <summary>OS-specific note about where ffmpeg/ffprobe are searched (part of the usage info).</summary>
    private static string FfmpegNote => OperatingSystem.IsWindows()
        ? """
          ffmpeg/ffprobe are required. They are searched in PATH, .\ffmpeg\bin,
          %USERPROFILE%\ffmpeg\bin, common Program Files locations and finally %FFMPEG_DIR%\bin
          (FFMPEG_DIR points to ffmpeg's base directory).
          """
        : """
          ffmpeg/ffprobe are required. They are searched in PATH, ./ffmpeg, ~/ffmpeg, /usr/bin,
          /usr/local/bin, /opt/ffmpeg, /snap/bin and finally $FFMPEG_DIR (FFMPEG_DIR points to
          ffmpeg's base directory). Install e.g. with: sudo apt install ffmpeg
          """;

    /// <summary>Comprehensive usage info printed on --help or on any command line error.</summary>
    public static string UsageText => $"""
        chapterize - mark chapter starts in audiobooks using Whisper speech recognition
        Supported formats: {SupportedExtensionsText} (formats whose containers can hold chapter marks)

        Usage:
          chapterize [options] <file-or-directory>
          chapterize --revert [--recurse] <file-or-directory>
          chapterize --help | -?

        Options (must precede the file/directory argument):
          -r, --recurse             Recursively descend into subdirectories (directories only).
          -b, --backup              Keep the original file with the added suffix ".bak".
              --revert              Restore backups: for every supported audio file with an
                                    added ".bak" suffix, delete the corresponding original and
                                    rename the .bak file back. Only combinable with --recurse
                                    and --filter.
          -l, --lang <code>         Two-letter language hint for Whisper (default: en).
                                    Spoken chapter numbers - cardinals and ordinals, before or
                                    after the phrase ("chapter two", "Erstes Kapitel") - are
                                    understood in {string.Join(", ", NumberWordParser.SupportedLanguages)}; digits
                                    ("2.", "2nd", "2e") work in every language. For these
                                    languages, --lang also localizes the defaults of
                                    --chapter-phrase and --title (and thus --intro-title).
          -c, --chapter-phrase <p>  Word/phrase that identifies a chapter start (default:
                                    chapter, localized by --lang).
                                    Enclose in slashes to use a regexp, e.g. "/chapter (\d+)/".
                                    The regexp may contain one capturing group "(\d+)" in place of
                                    the chapter number; otherwise the number is expected to follow
                                    the phrase. Matching is always case-insensitive.
          -m, --model <name>        Whisper model: tiny, base, small, medium, turbo or large
                                    (default: turbo).
          -F, --filter <filter>     Only process matching files. Either "/regexp/" - matched
                                    case-insensitively against the whole path of each file -
                                    or a comma-separated list of permissible file extensions,
                                    e.g. "mp3,m4b". One filter of each kind may be given;
                                    they also select which backups --revert restores.
          -f, --force               Discard pre-existing chapter markings. Without --force, files
                                    that already have chapter markings are skipped.
          -x, --max-chapters <n>    If a file has more than <n> pre-existing chapter markings,
                                    they are considered bogus and are discarded.
          -j, --jingle              A short jingle may precede the chapter phrase; chapter marks
                                    are placed 0.5 seconds before the jingle.
          -X, --max-jingle-length <seconds>
                                    Maximum expected jingle duration (default: 45). Audio is
                                    probed for this duration plus 5 seconds (for the phrase
                                    itself) after each silence. Lower values speed up probing.
                                    Requires --jingle.
          -n, --min-silence-length <seconds>
                                    Minimum silence duration that counts as a potential
                                    chapter break (default: 1.5). Every such silence is
                                    probed with Whisper, so higher values can drastically
                                    speed up detection when the breaks are known to be long.
          -q, --quiet               Suppress per-file output; warnings and errors are still shown.
          -s, --summary             Print a summary at the end: file counts, total and average
                                    processing time.
          -t, --title <word>        Word used for chapter titles; the chapter number is appended
                                    (default: Chapter, localized by --lang).
          -i, --intro-title <word>  Title of the chapter mark covering the audio before the
                                    first detected chapter, e.g. a prelude (default: the
                                    --title word followed by "0", e.g. "Chapter 0" or,
                                    with --lang de, "Kapitel 0").
          -?, --help                Show this help.
              --version             Show version information.

        Short options without parameters may be collapsed, e.g. "-rb" equals "-r -b".

        {FfmpegNote}
        """;
}
