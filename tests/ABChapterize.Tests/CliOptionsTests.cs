// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Cli;
using ABChapterize.Language;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="CliOptions.Parse"/>: option syntax (long, short, collapsed),
/// per-language defaults, --filter handling, the chapter phrase regex, and all
/// semantic validation rules. Target paths point into a per-test temp directory.
/// </summary>
public sealed class CliOptionsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    /// <summary>Creates a temp directory with one supported audio file to parse against.</summary>
    public CliOptionsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"abchapterize-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "book.m4b");
        File.WriteAllText(_file, "x");
    }

    /// <summary>Removes the temp directory.</summary>
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Parses the given options followed by the temp audio file as target.</summary>
    private CliOptions? ParseFile(params string[] options)
        => CliOptions.Parse([.. options, _file]);

    /// <summary>Parses the given options followed by the temp directory as target.</summary>
    private CliOptions? ParseDir(params string[] options)
        => CliOptions.Parse([.. options, _dir]);

    [Fact]
    public void Defaults_AreAutoLanguageWithEnglishFallback()
    {
        var o = ParseFile()!;
        Assert.Equal("auto", o.Language);
        Assert.True(o.AutoLanguage);
        // The parse-time DefaultProfile/ChapterPhrase/Title/IntroTitle are the English
        // fallback used when a file's own auto-detection is inconclusive or skipped;
        // ChapterDetector resolves a fresh profile per file when actually detecting.
        Assert.Equal("/chapter/", o.ChapterPhrase);
        Assert.Equal("Chapter", o.Title);
        Assert.Equal("Intro", o.IntroTitle);
        Assert.Equal("turbo", o.Model);
        Assert.Equal(1.5, o.MinSilenceSeconds);
        Assert.True(o.AutoMinSilence);
        Assert.Equal(45, o.MaxJingleSeconds);
        Assert.True(o.AutoMaxJingle);
        Assert.Equal(60, o.EarlyAbortMinutes);
        // Jingle-aware probing (the VAD pre-pass) runs by default now, even without
        // --mark-before-jingle - only --max-jingle-length 0 turns it off.
        Assert.True(o.RunVadPrePass);
        Assert.Equal([new CliOptions.Target(_file, IsDirectory: false)], o.Targets);
        // Mark refinement is on by default; --quick-marks is the opt-out, so it starts false
        // while PreciseMark itself starts true (asserted separately).
        Assert.True(o.PreciseMark);
        Assert.False(o.Recurse | o.Backup | o.Revert | o.NoOp | o.CpuOnly | o.Force | o.MarkBeforeJingle | o.QuickMarks | o.TrailingScan | o.Quiet | o.Verbose
                     | o.NoBar | o.Summary | o.DryRun | o.Export | o.Import | o.SimpleMetadata | o.Verify);
        Assert.Null(o.Jobs);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData("Auto")]
    public void Lang_Auto_IsAcceptedExplicitly(string value)
    {
        var o = ParseFile("--lang", value)!;
        Assert.Equal("auto", o.Language);
        Assert.True(o.AutoLanguage);
    }

    [Fact]
    public void Lang_Auto_WithExplicitOverrides_StillWinsOverEnglishFallback()
    {
        var o = ParseFile("--lang", "auto", "-c", "Teil", "-t", "Teil", "-i", "Anfang")!;
        Assert.Equal("Teil", o.ChapterPhrase);
        Assert.Equal("Teil", o.Title);
        Assert.Equal("Anfang", o.IntroTitle);
    }

    [Fact]
    public void ResolveProfile_LocalizesForTheGivenLanguage()
    {
        var o = ParseFile()!; // auto, no overrides
        var profile = o.ResolveProfile("de");
        Assert.Equal("de", profile.Language);
        Assert.Equal("/kapitel/", profile.ChapterPhrase);
        Assert.Equal("Kapitel", profile.Title);
        Assert.Equal("Intro", profile.IntroTitle);
    }

    [Fact]
    public void ResolveProfile_KeepsExplicitOverrides_RegardlessOfLanguage()
    {
        var o = ParseFile("-c", "Teil", "-t", "Teil", "-i", "Anfang")!;
        var profile = o.ResolveProfile("de");
        Assert.Equal("Teil", profile.ChapterPhrase);
        Assert.Equal("Teil", profile.Title);
        Assert.Equal("Anfang", profile.IntroTitle);
    }

    [Fact]
    public void NamedPhrases_DefaultToPrologueAndEpilogue()
    {
        var profile = ParseFile()!.DefaultProfile;
        Assert.Equal(["prologue", "epilogue"], profile.NamedPhrases.Select(p => p.Kind));
        Assert.Equal(["Prologue", "Epilogue"], profile.NamedPhrases.Select(p => p.Title));
        Assert.Equal(
            [NamedPhraseScope.BeforeFirstChapter, NamedPhraseScope.AfterFirstChapter],
            profile.NamedPhrases.Select(p => p.Scope));
        Assert.Matches(profile.NamedPhrases[0].Regex, "and now, the Prologue.");
    }

    [Theory]
    [InlineData("de", "Prolog", "Epilog")]
    [InlineData("fr", "Prologue", "Épilogue")]
    [InlineData("es", "Prólogo", "Epílogo")]
    [InlineData("nl", "Proloog", "Epiloog")]
    [InlineData("pl", "Prolog", "Epilog")]
    public void NamedPhrases_AreLocalized(string lang, string prologue, string epilogue)
    {
        var profile = ParseFile("--lang", lang)!.DefaultProfile;
        Assert.Equal([prologue, epilogue], profile.NamedPhrases.Select(p => p.Title));
    }

    [Fact]
    public void NamedPhrases_HonourExplicitOverrides_RegardlessOfLanguage()
    {
        var profile = ParseFile("-p", "Vorspiel", "-P", "Vorspiel", "-g", "/nach(spiel|wort)/", "-G", "Nachspiel")!
            .ResolveProfile("de");
        Assert.Equal(["Vorspiel", "Nachspiel"], profile.NamedPhrases.Select(p => p.Title));
        Assert.Matches(profile.NamedPhrases[0].Regex, "Vorspiel");
        // The "/regexp/" spelling --chapter-phrase accepts works for the named phrases too.
        Assert.Matches(profile.NamedPhrases[1].Regex, "Nachwort");
    }

    [Theory]
    [InlineData("--prologue-phrase", "epilogue")]
    [InlineData("--epilogue-phrase", "prologue")]
    public void NamedPhrases_AreDroppedEntirely_WhenTheirPhraseIsEmpty(string opt, string survivor)
    {
        var profile = ParseFile(opt, "")!.DefaultProfile;
        Assert.Equal([survivor], profile.NamedPhrases.Select(p => p.Kind));
    }

    [Fact]
    public void NamedPhrases_BothEmpty_LeavesNoNamedDetectionAtAll()
        => Assert.Empty(ParseFile("-p", "", "-g", "")!.DefaultProfile.NamedPhrases);

    [Theory]
    [InlineData("--prologue-title")]
    [InlineData("--epilogue-title")]
    public void NamedMarkTitle_IsRejected_WhenEmpty(string opt)
        => Assert.Throws<CliError>(() => ParseFile(opt, ""));

    [Theory]
    [InlineData("--prologue-phrase", "Prolog")]
    [InlineData("--epilogue-phrase", "Epilog")]
    public void NamedPhrase_IsRejected_WithImport(string opt, string value)
        => Assert.Throws<CliError>(() => ParseFile("--import", opt, value));

    [Fact]
    public void Custom_BecomesRepeatableNamedPhrases_AfterPrologueAndEpilogue()
    {
        var profile = ParseFile("--custom", "zwischenspiel:Zwischenspiel;/zeit[- ]?tafel/:Zeittafel")!
            .ResolveProfile("en");

        Assert.Equal(
            ["prologue", "epilogue", "custom 1", "custom 2"],
            profile.NamedPhrases.Select(p => p.Kind));
        var custom = profile.NamedPhrases.Where(p => p.Repeatable).ToList();
        Assert.All(custom, p => Assert.Equal(NamedPhraseScope.Anywhere, p.Scope));
        Assert.Equal(["Zwischenspiel", "Zeittafel"], custom.Select(p => p.Title));
        Assert.Matches(custom[1].Regex, "die Zeit-Tafel");
    }

    [Fact]
    public void Custom_IsNotLocalized_ByLang()
    {
        // A phrase the user wrote out means exactly what it says, whatever --lang is set to.
        var profile = ParseFile("--lang", "de", "-u", "interlude:Interlude")!.ResolveProfile("de");

        Assert.Equal("Interlude", profile.NamedPhrases.Single(p => p.Repeatable).Title);
    }

    [Fact]
    public void Custom_AccumulatesAcrossRepeatedOptions()
    {
        var options = ParseFile("--custom", "a:A", "--custom", "b:B;c:C")!;

        Assert.Equal(["A", "B", "C"], options.CustomMappings.Select(m => m.Title));
    }

    [Fact]
    public void Custom_ReadsMappingsFromAFile()
    {
        var path = Path.Combine(_dir, "mappings.txt");
        File.WriteAllLines(path, ["# comment", "zwischenspiel:Zwischenspiel"]);

        Assert.Equal(
            [new CustomMapping("zwischenspiel", "Zwischenspiel")],
            ParseFile("--custom-file", path)!.CustomMappings);
    }

    [Fact]
    public void Custom_TitleMayReferenceACapturingGroup()
    {
        var phrase = ParseFile("--custom", "/(interlude|intermezzo)/:The $1")!
            .ResolveProfile("en").NamedPhrases.Single(p => p.Repeatable);

        Assert.Equal("The intermezzo", phrase.ResolveTitle(phrase.Regex.Match("an intermezzo now")));
    }

    [Fact]
    public void Custom_TitleReferencingAMissingGroup_IsRejected()
        => Assert.Throws<CliError>(() => ParseFile("--custom", "/(interlude)/:Part $2"));

    [Fact]
    public void Custom_TitleKeepsAnOrdinaryDollarSign()
    {
        // No capturing group in the phrase, so "$5" is a price, not a substitution.
        var phrase = ParseFile("--custom", "bargain:Only $5")!
            .ResolveProfile("en").NamedPhrases.Single(p => p.Repeatable);

        Assert.Equal("Only $5", phrase.ResolveTitle(phrase.Regex.Match("a bargain")));
    }

    [Fact]
    public void IgnoreChapterNumbers_IsOffByDefault()
        => Assert.False(ParseFile()!.IgnoreChapterNumbers);

    [Fact]
    public void IgnoreChapterNumbers_SwitchesNumberReasoningOff()
        => Assert.True(ParseFile("--ignore-chapter-numbers")!.IgnoreChapterNumbers);

    [Theory]
    [InlineData("--pass3-model", "large")]
    [InlineData("--expected-start-chapter", "3")]
    [InlineData("--max-chapter-number", "40")]
    public void IgnoreChapterNumbers_RejectsANumberBasedOption(string opt, string value)
        => Assert.Throws<CliError>(() => ParseFile("--ignore-chapter-numbers", opt, value));

    [Theory]
    [InlineData("--trailing-scan")]
    [InlineData("--verify")]
    [InlineData("--import")]
    public void IgnoreChapterNumbers_RejectsANumberBasedFlag(string flag)
        => Assert.Throws<CliError>(() => ParseFile("--ignore-chapter-numbers", flag));

    [Theory]
    [InlineData("--chapter-phrase", "part")]
    [InlineData("--title", "Part")]
    public void IgnoreChapterNumbers_StillTakesTheChapterPhraseOptions(string opt, string value)
        => Assert.True(ParseFile("--ignore-chapter-numbers", opt, value)!.IgnoreChapterNumbers);

    [Fact]
    public void IgnoreChapterNumbers_IsAccepted_WithEveryNamedPhraseSwitchedOff()
    {
        var options = ParseFile(
            "--ignore-chapter-numbers", "--prologue-phrase", "", "--epilogue-phrase", "")!;

        Assert.True(options.IgnoreChapterNumbers);
        Assert.Empty(options.DefaultProfile.NamedPhrases);
    }

    [Fact]
    public void RunFingerprint_ChangesWithTheCustomMappings()
        => Assert.NotEqual(
            ParseFile("--custom", "a:A")!.RunFingerprint,
            ParseFile("--custom", "b:B")!.RunFingerprint);

    [Theory]
    [InlineData("--jobs", "1")]
    [InlineData("-J", "4")]
    public void Jobs_IsParsed_LongAndShort(string opt, string value)
    {
        Assert.Equal(int.Parse(value), ParseFile(opt, value)!.Jobs);
    }

    [Fact]
    public void Jobs_Auto_ParsesAsNull()
    {
        Assert.Null(ParseFile("--jobs", "auto")!.Jobs);
        Assert.Null(ParseFile("--jobs", "AUTO")!.Jobs);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("many")]
    public void InvalidJobs_AreRejected(string value)
    {
        Assert.Throws<CliError>(() => ParseFile("--jobs", value));
    }

    [Fact]
    public void JobsWithRevert_IsAnError()
    {
        Assert.Throws<CliError>(() => ParseDir("--revert", "--jobs", "2"));
    }

    [Fact]
    public void Export_IsParsed_LongAndShort()
    {
        Assert.True(ParseFile("--export")!.Export);
        Assert.True(ParseFile("-E")!.Export);
    }

    [Fact]
    public void Import_IsParsed_LongAndShort()
    {
        Assert.True(ParseFile("--import")!.Import);
        Assert.True(ParseFile("-I")!.Import);
    }

    [Fact]
    public void SimpleMetadata_IsParsed_LongAndShort()
    {
        Assert.True(ParseFile("--export", "--simple-metadata")!.SimpleMetadata);
        Assert.True(ParseFile("-ES")!.SimpleMetadata);
    }

    [Fact]
    public void ExportAndImport_Together_IsAnError()
    {
        Assert.Throws<CliError>(() => ParseFile("--export", "--import"));
    }

    [Fact]
    public void ImportWithRevert_IsAnError()
    {
        Assert.Throws<CliError>(() => ParseDir("--import", "--revert"));
    }

    [Theory]
    [InlineData("--lang", "de")]
    [InlineData("--chapter-phrase", "part")]
    [InlineData("--model", "small")]
    [InlineData("--pass3-model", "large")]
    [InlineData("--mark-before-jingle")]
    [InlineData("--quick-marks")]
    [InlineData("--trailing-scan")]
    [InlineData("--max-jingle-length", "30")]
    [InlineData("--min-silence-length", "2")]
    [InlineData("--early-abort", "30")]
    [InlineData("--expected-start-chapter", "5")]
    [InlineData("--max-chapter-number", "50")]
    [InlineData("--verify")]
    public void ImportWithDetectionOptions_IsAnError(params string[] extra)
    {
        Assert.Throws<CliError>(() => ParseFile([.. new[] { "--import" }, .. extra]));
    }

    [Fact]
    public void Pass3Model_DefaultsToTheMainModel()
    {
        Assert.Equal("turbo", ParseFile()!.Pass3Model);
        Assert.Equal("small", ParseFile("--model", "small")!.Pass3Model);
    }

    [Fact]
    public void Pass3Model_IsParsed_LongShortAndCaseNormalized()
    {
        Assert.Equal("large", ParseFile("--pass3-model", "large")!.Pass3Model);
        Assert.Equal("large", ParseFile("-M", "LARGE")!.Pass3Model);
        // Independent of the main model, which keeps its own value.
        var o = ParseFile("--model", "tiny", "--pass3-model", "large")!;
        Assert.Equal("tiny", o.Model);
        Assert.Equal("large", o.Pass3Model);
    }

    [Fact]
    public void InvalidPass3Model_IsRejected()
    {
        Assert.Throws<CliError>(() => ParseFile("--pass3-model", "gigantic"));
    }

    [Theory]
    // Strictly better than the pass-2 model: pass 2.5's gap re-probe is worth its transcriptions.
    [InlineData("tiny", "large", true)]
    [InlineData("base", "small", true)]
    [InlineData("medium", "turbo", true)]
    [InlineData("turbo", "large", true)]
    // Equal or lighter: a re-probe would only reach the same conclusion, more slowly.
    [InlineData("large", "large", false)]
    [InlineData("large", "turbo", false)]
    [InlineData("turbo", "medium", false)]
    [InlineData("small", "tiny", false)]
    public void Pass3ModelIsUpgrade_RanksTheModelsByQuality(string model, string pass3Model, bool expected)
    {
        var o = ParseFile("--model", model, "--pass3-model", pass3Model)!;
        Assert.Equal(expected, o.Pass3ModelIsUpgrade);
    }

    [Fact]
    public void Pass3ModelIsUpgrade_IsFalse_WhenNoPass3ModelWasGivenAtAll()
    {
        // The default mirrors --model, so there is nothing to upgrade to and pass 2.5 stays off.
        Assert.False(ParseFile("--model", "tiny")!.Pass3ModelIsUpgrade);
        Assert.False(ParseFile()!.Pass3ModelIsUpgrade);
    }

    [Fact]
    public void Verify_IsParsed_LongAndShort()
    {
        Assert.True(ParseFile("--verify")!.Verify);
        Assert.True(ParseFile("-V")!.Verify);
    }

    [Fact]
    public void VerifyWithForce_IsAnError()
    {
        Assert.Throws<CliError>(() => ParseFile("--verify", "--force"));
    }

    [Fact]
    public void QuickMarks_IsParsed_LongAndShort()
    {
        Assert.True(ParseFile("--quick-marks")!.QuickMarks);
        Assert.True(ParseFile("-Q")!.QuickMarks);
    }

    [Fact]
    public void TrailingScan_IsParsed_LongAndShort()
    {
        Assert.True(ParseFile("--trailing-scan")!.TrailingScan);
        Assert.True(ParseFile("-L")!.TrailingScan);
    }

    // Mark refinement is the default: PreciseMark is simply the inverse of the opt-out flag,
    // so it holds for a bare run and stops holding exactly when --quick-marks is given.
    [Fact]
    public void PreciseMark_IsOnByDefault_AndOffOnlyWithQuickMarks()
    {
        Assert.True(ParseFile()!.PreciseMark);
        Assert.False(ParseFile()!.QuickMarks);
        Assert.False(ParseFile("--quick-marks")!.PreciseMark);
    }

    // -q stays --quiet; --quick-marks deliberately took the capital -Q instead, so an existing
    // -q never silently changes meaning.
    [Fact]
    public void LowercaseQ_IsStillQuiet_NotQuickMarks()
    {
        var o = ParseFile("-q")!;
        Assert.True(o.Quiet);
        Assert.False(o.QuickMarks);
        Assert.True(o.PreciseMark);
    }

    [Fact]
    public void QuickMarksWithMarkBeforeJingle_IsAllowed()
    {
        // --mark-before-jingle walks back from whatever mark default-mode placement produced,
        // refined or not, so the two compose even though combining them is discouraged.
        var o = ParseFile("--quick-marks", "--mark-before-jingle")!;
        Assert.True(o.QuickMarks);
        Assert.False(o.PreciseMark);
        Assert.True(o.MarkBeforeJingle);
    }

    [Fact]
    public void Verify_ComposesWithMaxChapters()
    {
        var o = ParseFile("--verify", "--max-chapters", "10")!;
        Assert.True(o.Verify);
        Assert.Equal(10, o.MaxChapters);
    }

    [Fact]
    public void VerifyThreshold_IsParsed_LongAndShort_AndDefaultsToNull()
    {
        Assert.Null(ParseFile("--verify")!.VerifyFailThreshold);
        Assert.Equal(3, ParseFile("--verify", "--verify-threshold", "3")!.VerifyFailThreshold);
        Assert.Equal(3, ParseFile("--verify", "-h", "3")!.VerifyFailThreshold);
    }

    [Fact]
    public void VerifyThreshold_WithoutVerify_IsAnError()
    {
        var ex = Assert.Throws<CliError>(() => ParseFile("--verify-threshold", "3"));
        Assert.Contains("requires --verify", ex.Message);
    }

    [Fact]
    public void InvalidVerifyThreshold_IsRejected()
    {
        Assert.Throws<CliError>(() => ParseFile("--verify", "--verify-threshold", "-1"));
        Assert.Throws<CliError>(() => ParseFile("--verify", "--verify-threshold", "many"));
    }

    [Fact]
    public void ImportWithMaxJingleLength_IsStillAnError_EvenWithoutMarkBeforeJingle()
    {
        Assert.Throws<CliError>(() => ParseFile("--import", "--max-jingle-length", "30"));
    }

    [Fact]
    public void Import_WithForceAndMaxChapters_IsAllowed()
    {
        var o = ParseFile("--import", "--force", "--max-chapters", "5")!;
        Assert.True(o.Import && o.Force);
        Assert.Equal(5, o.MaxChapters);
    }

    [Fact]
    public void SimpleMetadata_WithoutExportOrImport_IsAnError()
    {
        Assert.Throws<CliError>(() => ParseFile("--simple-metadata"));
    }

    [Fact]
    public void Export_ComposesWithDryRun()
    {
        var o = ParseFile("--export", "--dry-run")!;
        Assert.True(o.Export && o.DryRun);
    }

    [Fact]
    public void DryRun_IsParsed_LongAndShort()
    {
        Assert.True(ParseFile("--dry-run")!.DryRun);
        Assert.True(ParseFile("-d")!.DryRun);
    }

    [Fact]
    public void DryRun_WithRevert_IsAnError()
    {
        Assert.Throws<CliError>(() => ParseDir("--revert", "--dry-run"));
    }

    [Fact]
    public void Lang_LocalizesPhraseTitleAndIntro()
    {
        var o = ParseFile("--lang", "tr")!;
        Assert.Equal("/b[öo]l[üu]m/", o.ChapterPhrase);
        Assert.Equal("Bölüm", o.Title);
        Assert.Equal("Giriş", o.IntroTitle);
        // The point of the regexp default: a transcript that lost the dotted vowels still matches.
        Assert.Matches(o.PhraseRegex, "bolum 3");
        Assert.Matches(o.PhraseRegex, "Bölüm 3");
    }

    /// <summary>
    /// Guards the two rules <see cref="ABChapterize.Language.ILanguage"/> states about the
    /// built-in phrases: no capturing group (which would be read as "the user is capturing the
    /// chapter number here"), and no empty phrase (which is the prologue/epilogue opt-out
    /// spelling and must never be a language's default).
    /// </summary>
    [Fact]
    public void EveryRegisteredLanguage_HasUsableDefaultPhrases()
    {
        var o = ParseFile()!;
        foreach (var language in LanguageRegistry.Languages)
        {
            var profile = o.ResolveProfile(language.Code);
            Assert.False(profile.PhraseHasNumberGroup, language.Code);
            Assert.NotEmpty(profile.Title);
            Assert.NotEmpty(profile.IntroTitle);
            Assert.Equal(2, profile.NamedPhrases.Count);
            Assert.All(profile.NamedPhrases, p => Assert.NotEmpty(p.Title));
        }
    }

    /// <summary>
    /// Each language's own chapter/prologue/epilogue phrase must actually match the words a
    /// narrator says in that language - including the spelling variants the regexps exist for.
    /// </summary>
    [Theory]
    [InlineData("en", "chapter one", "prologue", "epilog")]
    [InlineData("de", "Kapitel eins", "Prolog", "Epilog")]
    [InlineData("fr", "chapitre un", "prologue", "epilogue")]
    [InlineData("es", "capitulo uno", "prólogo", "epilogo")]
    [InlineData("it", "capitolo uno", "prologo", "epilogo")]
    [InlineData("nl", "hoofdstuk een", "proloog", "epiloog")]
    [InlineData("tr", "Birinci Bölüm", "prolog", "epilog")]
    [InlineData("pt", "capítulo um", "prologo", "epílogo")]
    [InlineData("pl", "rozdziału pierwszego", "prolog", "epilog")]
    [InlineData("sv", "Första kapitlet", "prolog", "epilog")]
    [InlineData("da", "kapitel et", "prolog", "epilog")]
    public void DefaultPhrases_MatchTheirLanguagesAnnouncements(
        string code, string chapter, string prologue, string epilogue)
    {
        var profile = ParseFile()!.ResolveProfile(code);
        Assert.Matches(profile.PhraseRegex, chapter);
        Assert.Matches(profile.NamedPhrases[0].Regex, prologue);
        Assert.Matches(profile.NamedPhrases[1].Regex, epilogue);
    }

    [Theory]
    [InlineData("en", "Intro")]
    [InlineData("de", "Intro")]
    [InlineData("fr", "Introduction")]
    [InlineData("es", "Introducción")]
    [InlineData("it", "Introduzione")]
    [InlineData("nl", "Intro")]
    [InlineData("tr", "Giriş")]
    [InlineData("pt", "Introdução")]
    [InlineData("pl", "Wstęp")]
    [InlineData("sv", "Introduktion")]
    [InlineData("da", "Introduktion")]
    [InlineData("cs", "Intro")] // no dedicated language support: English-ish defaults
    public void IntroTitle_Default_IsLocalized(string lang, string expected)
    {
        Assert.Equal(expected, ParseFile("--lang", lang)!.IntroTitle);
    }

    [Fact]
    public void ExplicitPhraseAndTitle_WinOverLocalization()
    {
        var o = ParseFile("-l", "de", "-c", "Teil", "-t", "Teil", "-i", "Anfang")!;
        Assert.Equal("Teil", o.ChapterPhrase);
        Assert.Equal("Teil", o.Title);
        Assert.Equal("Anfang", o.IntroTitle);
    }

    [Fact]
    public void LanguageAndModel_AreCaseNormalized()
    {
        var o = ParseFile("--lang", "DE", "--model", "TURBO")!;
        Assert.Equal("de", o.Language);
        Assert.Equal("turbo", o.Model);
    }

    [Fact]
    public void CollapsedShortFlags_AllApply()
    {
        var o = ParseDir("-rbfjqvs")!;
        Assert.True(o.Recurse && o.Backup && o.Force && o.MarkBeforeJingle && o.Quiet && o.Verbose && o.Summary);
    }

    [Fact]
    public void ShortValueOption_AsLastCollapsedLetter_TakesParameter()
    {
        var o = ParseFile("-bl", "fr")!;
        Assert.True(o.Backup);
        Assert.Equal("fr", o.Language);
    }

    [Fact]
    public void ShortValueOption_NotLastInCollapsedGroup_IsAnError()
    {
        var ex = Assert.Throws<CliError>(() => ParseFile("-lb", "fr"));
        Assert.Contains("cannot be collapsed", ex.Message);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-?")]
    [InlineData("/?")]
    public void HelpRequests_ReturnNull(string arg)
    {
        Assert.Null(CliOptions.Parse([arg]));
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("-z")]
    public void UnknownOptions_AreRejected(string arg)
    {
        var ex = Assert.Throws<CliError>(() => ParseFile(arg));
        Assert.Contains("Unknown option", ex.Message);
    }

    [Fact]
    public void MissingParameter_IsAnError()
    {
        var ex = Assert.Throws<CliError>(() => CliOptions.Parse(["--lang"]));
        Assert.Contains("requires a parameter", ex.Message);
    }

    [Fact]
    public void TargetMustBeLastArgument()
    {
        var ex = Assert.Throws<CliError>(() => CliOptions.Parse([_file, "--backup"]));
        Assert.Contains("must precede the file/directory arguments", ex.Message);
    }

    [Fact]
    public void SeveralTargets_AreKeptInOrder_FilesAndDirectoriesMixed()
    {
        var second = Path.Combine(_dir, "second.m4b");
        File.WriteAllText(second, "x");
        var o = CliOptions.Parse(["--backup", second, _dir, _file])!;
        Assert.Equal(
            [
                new CliOptions.Target(second, IsDirectory: false),
                new CliOptions.Target(_dir, IsDirectory: true),
                new CliOptions.Target(_file, IsDirectory: false),
            ],
            o.Targets);
    }

    [Fact]
    public void RepeatedTarget_IsListedOnce()
    {
        var o = CliOptions.Parse([_file, Path.Combine(_dir, ".", "book.m4b")])!;
        Assert.Single(o.Targets);
    }

    [Fact]
    public void Recurse_WithSeveralFilesButNoDirectory_IsAnError()
    {
        var second = Path.Combine(_dir, "second.m4b");
        File.WriteAllText(second, "x");
        var ex = Assert.Throws<CliError>(() => CliOptions.Parse(["--recurse", _file, second]));
        Assert.Contains("--recurse", ex.Message);
    }

    [Fact]
    public void Recurse_IsAccepted_WhenAtLeastOneTargetIsADirectory()
    {
        var o = CliOptions.Parse(["--recurse", _file, _dir])!;
        Assert.True(o.Recurse);
    }

    [Fact]
    public void RunFingerprint_IgnoresPresentationOnlyOptions()
    {
        Assert.Equal(ParseFile()!.RunFingerprint,
            ParseFile("--quiet", "--verbose", "--no-bar", "--summary", "--jobs", "3", "--cpu-only")!.RunFingerprint);
    }

    [Fact]
    public void RunFingerprint_ChangesWithAnOptionThatChangesTheResult()
    {
        Assert.NotEqual(ParseFile()!.RunFingerprint, ParseFile("--lang", "de")!.RunFingerprint);
        Assert.NotEqual(ParseFile()!.RunFingerprint, ParseFile("--force")!.RunFingerprint);
    }

    [Fact]
    public void MissingTarget_IsAnError()
    {
        Assert.Throws<CliError>(() => CliOptions.Parse(["--backup"]));
    }

    [Fact]
    public void NonexistentTarget_IsAnError()
    {
        Assert.Throws<CliError>(() => CliOptions.Parse([Path.Combine(_dir, "missing.m4b")]));
    }

    [Fact]
    public void UnsupportedFileExtension_IsAnError()
    {
        var txt = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(txt, "x");
        var ex = Assert.Throws<CliError>(() => CliOptions.Parse([txt]));
        Assert.Contains("Unsupported file type", ex.Message);
    }

    [Fact]
    public void Recurse_OnSingleFile_IsAnError()
    {
        Assert.Throws<CliError>(() => ParseFile("--recurse"));
    }

    [Theory]
    [InlineData("xx1")]
    [InlineData("e")]
    [InlineData("deu")]
    public void InvalidLanguageCodes_AreRejected(string lang)
    {
        Assert.Throws<CliError>(() => ParseFile("--lang", lang));
    }

    [Fact]
    public void InvalidModel_IsRejected()
    {
        Assert.Throws<CliError>(() => ParseFile("--model", "gigantic"));
    }

    [Fact]
    public void FilterExtensionList_IsNormalized()
    {
        var o = ParseDir("--filter", "MP3, .m4b,mp3")!;
        Assert.Equal([".mp3", ".m4b"], o.FilterExtensions!);
        Assert.Equal([".mp3", ".m4b"], o.EffectiveExtensions);
    }

    [Fact]
    public void FilterExtensionList_UnsupportedExtension_IsAnError()
    {
        var ex = Assert.Throws<CliError>(() => ParseDir("--filter", "mp3,wav"));
        Assert.Contains(".wav", ex.Message);
    }

    [Fact]
    public void FilterRegexAndExtensionList_CanBeCombined()
    {
        var o = ParseDir("-F", "/part\\d+/", "-F", "mp3")!;
        Assert.NotNull(o.FilterRegex);
        Assert.Matches(o.FilterRegex!, @"C:\audio\PART7.mp3");
        Assert.Equal([".mp3"], o.FilterExtensions!);
    }

    [Fact]
    public void SecondFilterOfSameKind_IsAnError()
    {
        Assert.Throws<CliError>(() => ParseDir("-F", "mp3", "-F", "m4b"));
        Assert.Throws<CliError>(() => ParseDir("-F", "/a/", "-F", "/b/"));
    }

    [Fact]
    public void InvalidFilterRegex_IsAnError()
    {
        Assert.Throws<CliError>(() => ParseDir("--filter", "/(unclosed/"));
    }

    [Fact]
    public void WithoutFilter_AllSupportedExtensions_AreEffective()
    {
        var o = ParseDir()!;
        Assert.Null(o.FilterExtensions);
        Assert.Equal(CliOptions.SupportedExtensions, o.EffectiveExtensions);
    }

    [Fact]
    public void Revert_WithIncompatibleOptions_IsAnError()
    {
        Assert.Throws<CliError>(() => ParseDir("--revert", "--force"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--lang", "de"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--backup"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--verify"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--pass3-model", "large"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--mark-before-jingle"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--quick-marks"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--trailing-scan"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--early-abort", "30"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--expected-start-chapter", "5"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--max-chapter-number", "50"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--cpu-only"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--max-chapters", "50"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--title", "Teil"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--intro-title", "Vorwort"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--min-silence-length", "2"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--max-jingle-length", "20"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--chapter-phrase", "Teil"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--model", "large"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--export"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--simple-metadata"));
    }

    [Fact]
    public void Revert_WithRecurseAndFilter_IsAllowed()
    {
        var o = ParseDir("--revert", "--recurse", "--filter", "m4b")!;
        Assert.True(o.Revert && o.Recurse);
    }

    [Fact]
    public void Revert_WithOutputOptions_IsAllowed()
    {
        var o = ParseDir("--revert", "--quiet", "--summary", "--verbose", "--no-bar")!;
        Assert.True(o.Revert && o.Quiet && o.Summary);
    }

    [Fact]
    public void NoOp_IsParsed_LongAndShort()
    {
        Assert.True(ParseDir("--no-op", "--filter", "m4b")!.NoOp);
        Assert.True(ParseDir("-O", "--filter", "m4b")!.NoOp);
    }

    [Fact]
    public void NoOp_WithoutFilter_IsAnError()
    {
        var ex = Assert.Throws<CliError>(() => ParseDir("--no-op"));
        Assert.Contains("requires --filter", ex.Message);
    }

    [Fact]
    public void NoOp_WithExtensionOrRegexFilter_IsAllowed()
    {
        Assert.True(ParseDir("--no-op", "--filter", "m4b")!.NoOp);
        Assert.True(ParseDir("--no-op", "--filter", "/book/")!.NoOp);
    }

    [Fact]
    public void NoOp_WithIncompatibleOptions_IsAnError()
    {
        Assert.Throws<CliError>(() => ParseDir("--no-op", "--filter", "m4b", "--revert"));
        Assert.Throws<CliError>(() => ParseDir("--no-op", "--filter", "m4b", "--force"));
        Assert.Throws<CliError>(() => ParseDir("--no-op", "--filter", "m4b", "--lang", "de"));
        Assert.Throws<CliError>(() => ParseDir("--no-op", "--filter", "m4b", "--dry-run"));
        Assert.Throws<CliError>(() => ParseDir("--no-op", "--filter", "m4b", "--jobs", "2"));
        Assert.Throws<CliError>(() => ParseDir("--no-op", "--filter", "m4b", "--early-abort", "30"));
        Assert.Throws<CliError>(() => ParseDir("--no-op", "--filter", "m4b", "--expected-start-chapter", "5"));
        Assert.Throws<CliError>(() => ParseDir("--no-op", "--filter", "m4b", "--max-chapter-number", "50"));
        Assert.Throws<CliError>(() => ParseDir("--no-op", "--filter", "m4b", "--cpu-only"));
        Assert.Throws<CliError>(() => ParseDir("--no-op", "--filter", "m4b", "--backup"));
        Assert.Throws<CliError>(() => ParseDir("--no-op", "--filter", "m4b", "--verify"));
        Assert.Throws<CliError>(() => ParseDir("--no-op", "--filter", "m4b", "--export"));
        Assert.Throws<CliError>(() => ParseDir("--no-op", "--filter", "m4b", "--trailing-scan"));
        Assert.Throws<CliError>(() => ParseDir("--no-op", "--filter", "m4b", "--quick-marks"));
    }

    [Fact]
    public void NoOp_WithRecurseAndOutputOptions_IsAllowed()
    {
        var o = ParseDir("--no-op", "--filter", "m4b", "--recurse", "--quiet", "--summary",
            "--verbose", "--no-bar")!;
        Assert.True(o.NoOp && o.Recurse && o.Quiet && o.Summary);
    }

    [Fact]
    public void CpuOnly_IsParsed_LongAndShort()
    {
        Assert.True(ParseFile("--cpu-only")!.CpuOnly);
        Assert.True(ParseFile("-C")!.CpuOnly);
    }

    [Fact]
    public void EarlyAbort_DefaultsTo60Minutes_WithoutBeingGiven()
    {
        Assert.Equal(60, ParseFile()!.EarlyAbortMinutes);
    }

    [Fact]
    public void EarlyAbort_IsParsed_LongAndShort()
    {
        Assert.Equal(90, ParseFile("--early-abort", "90")!.EarlyAbortMinutes);
        Assert.Equal(90, ParseFile("-a", "90")!.EarlyAbortMinutes);
    }

    [Fact]
    public void EarlyAbort_Zero_DisablesIt()
    {
        Assert.Equal(0, ParseFile("--early-abort", "0")!.EarlyAbortMinutes);
    }

    [Fact]
    public void MaxJingleLength_NoLongerRequiresMarkBeforeJingle()
    {
        var o = ParseFile("--max-jingle-length", "30")!;
        Assert.Equal(30, o.MaxJingleSeconds);
        Assert.False(o.MarkBeforeJingle);
    }

    [Fact]
    public void JingleParameters_AreParsed()
    {
        var o = ParseFile("--mark-before-jingle", "--max-jingle-length", "30.5")!;
        Assert.True(o.MarkBeforeJingle);
        Assert.Equal(30.5, o.MaxJingleSeconds);
    }

    [Fact]
    public void MaxJingleLength_ZeroIsAccepted()
    {
        var o = ParseFile("--max-jingle-length", "0")!;
        Assert.Equal(0, o.MaxJingleSeconds);
    }

    [Theory]
    // Without --mark-before-jingle, RunVadPrePass tracks whether MaxJingleSeconds > 0: only
    // the (default-off) --mark-before-jingle plus --max-jingle-length 0 combination keeps it
    // running for the jingle-anchor placement despite no jingle being expected.
    [InlineData(0, false, false)]
    [InlineData(45, false, true)]
    [InlineData(0, true, true)]
    [InlineData(45, true, true)]
    public void RunVadPrePass_ReflectsMarkBeforeJingleAndMaxJingleLength(
        double maxJingleSeconds, bool markBeforeJingle, bool expectedVad)
    {
        var args = new List<string> { "--max-jingle-length", maxJingleSeconds.ToString() };
        if (markBeforeJingle)
            args.Add("--mark-before-jingle");
        var o = ParseFile([.. args])!;
        Assert.Equal(expectedVad, o.RunVadPrePass);
    }

    [Theory]
    [InlineData("0.5")]
    [InlineData("-3")]
    [InlineData("601")]
    [InlineData("abc")]
    public void InvalidJingleLengths_AreRejected(string value)
    {
        Assert.Throws<CliError>(() => ParseFile("--mark-before-jingle", "--max-jingle-length", value));
    }

    [Theory]
    [InlineData("0.05")]
    [InlineData("61")]
    [InlineData("abc")]
    public void InvalidMinSilenceLengths_AreRejected(string value)
    {
        Assert.Throws<CliError>(() => ParseFile("--min-silence-length", value));
    }

    [Fact]
    public void MinSilenceLength_Auto_SetsFloorAndFlag()
    {
        var o = ParseFile("--min-silence-length", "AUTO")!;
        Assert.True(o.AutoMinSilence);
        Assert.Equal(1.5, o.MinSilenceSeconds);
    }

    [Fact]
    public void MinSilenceLength_ExplicitValue_LeavesAutoFlagUnset()
    {
        var o = ParseFile("--min-silence-length", "2.5")!;
        Assert.False(o.AutoMinSilence);
        Assert.Equal(2.5, o.MinSilenceSeconds);
    }

    [Fact]
    public void DecimalOptions_AcceptACommaAsWellAsAPointAsTheDecimalSeparator()
    {
        // Typed by a person on whatever keyboard/locale they have, so both notations mean the
        // same thing here - see NumberCulture. (Output, by contrast, is always "." regardless.)
        Assert.Equal(2.5, ParseFile("--min-silence-length", "2,5")!.MinSilenceSeconds);
        Assert.Equal(2.5, ParseFile("--min-silence-length", "2.5")!.MinSilenceSeconds);
        Assert.Equal(12.5, ParseFile("--max-jingle-length", "12,5")!.MaxJingleSeconds);
        Assert.Equal(12.5, ParseFile("--max-jingle-length", "12.5")!.MaxJingleSeconds);
        Assert.Equal(1.5, ParseFile("--early-abort", "1,5")!.EarlyAbortMinutes);
        Assert.Equal(1.5, ParseFile("--early-abort", "1.5")!.EarlyAbortMinutes);
    }

    [Fact]
    public void DecimalOptions_StillRejectGarbage_WithEitherSeparator()
    {
        // The comma is read strictly as a decimal point, so nothing that was invalid before
        // becomes valid by writing it with one.
        Assert.Throws<CliError>(() => ParseFile("--min-silence-length", "zwei,fünf"));
        Assert.Throws<CliError>(() => ParseFile("--min-silence-length", "2,5,3"));
        Assert.Throws<CliError>(() => ParseFile("--min-silence-length", "0,05")); // below the 0.1 floor
    }

    [Fact]
    public void InvalidMaxChapters_IsRejected()
    {
        Assert.Throws<CliError>(() => ParseFile("--max-chapters", "-1"));
        Assert.Throws<CliError>(() => ParseFile("--max-chapters", "many"));
    }

    [Fact]
    public void InvalidEarlyAbort_IsRejected()
    {
        Assert.Throws<CliError>(() => ParseFile("--early-abort", "-1"));
        Assert.Throws<CliError>(() => ParseFile("--early-abort", "1441"));
        Assert.Throws<CliError>(() => ParseFile("--early-abort", "soon"));
    }

    [Fact]
    public void ExpectedStartChapter_DefaultsToNull_WithoutBeingGiven()
    {
        Assert.Null(ParseFile()!.ExpectedStartChapter);
    }

    [Fact]
    public void ExpectedStartChapter_IsParsed_LongAndShort()
    {
        Assert.Equal(15, ParseFile("--expected-start-chapter", "15")!.ExpectedStartChapter);
        Assert.Equal(15, ParseFile("-e", "15")!.ExpectedStartChapter);
    }

    [Fact]
    public void InvalidExpectedStartChapter_IsRejected()
    {
        Assert.Throws<CliError>(() => ParseFile("--expected-start-chapter", "0"));
        Assert.Throws<CliError>(() => ParseFile("--expected-start-chapter", "-1"));
        Assert.Throws<CliError>(() => ParseFile("--expected-start-chapter", "many"));
    }

    [Fact]
    public void MaxChapterNumber_DefaultsToNull_WithoutBeingGiven()
    {
        Assert.Null(ParseFile()!.MaxChapterNumber);
    }

    [Fact]
    public void MaxChapterNumber_IsParsed_LongAndShort()
    {
        Assert.Equal(120, ParseFile("--max-chapter-number", "120")!.MaxChapterNumber);
        Assert.Equal(120, ParseFile("-N", "120")!.MaxChapterNumber);
    }

    [Fact]
    public void InvalidMaxChapterNumber_IsRejected()
    {
        Assert.Throws<CliError>(() => ParseFile("--max-chapter-number", "0"));
        Assert.Throws<CliError>(() => ParseFile("--max-chapter-number", "-1"));
        Assert.Throws<CliError>(() => ParseFile("--max-chapter-number", "lots"));
    }

    [Fact]
    public void MaxChapterNumber_BelowExpectedStartChapter_IsRejected()
    {
        Assert.Throws<CliError>(() =>
            ParseFile("--expected-start-chapter", "20", "--max-chapter-number", "10"));
        // Equal is fine: a single-chapter part.
        Assert.NotNull(ParseFile("--expected-start-chapter", "20", "--max-chapter-number", "20"));
    }

    [Fact]
    public void LiteralChapterPhrase_IsEscapedAndCaseInsensitive()
    {
        var o = ParseFile("-c", "part (a)")!;
        Assert.False(o.PhraseHasNumberGroup);
        Assert.Matches(o.PhraseRegex, "PART (A) two");
        Assert.DoesNotMatch(o.PhraseRegex, "part a");
    }

    [Fact]
    public void RegexChapterPhrase_WithCaptureGroup_IsDetected()
    {
        var o = ParseFile("-c", @"/chapter (\d+)/")!;
        Assert.True(o.PhraseHasNumberGroup);
        var m = o.PhraseRegex.Match("Chapter 12 begins");
        Assert.True(m.Success);
        Assert.Equal("12", m.Groups[1].Value);
    }

    [Fact]
    public void RegexChapterPhrase_WithoutGroup_HasNoNumberGroup()
    {
        var o = ParseFile("-c", @"/chapter/")!;
        Assert.False(o.PhraseHasNumberGroup);
    }

    [Fact]
    public void InvalidChapterPhraseRegex_IsAnError()
    {
        Assert.Throws<CliError>(() => ParseFile("-c", "/(unclosed/"));
    }

    [Fact]
    public void EmptyChapterPhrase_IsAnError()
    {
        Assert.Throws<CliError>(() => ParseFile("-c", ""));
    }
}
