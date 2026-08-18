// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Text.RegularExpressions;
using Xunit;
using ABChapterize.Cli;
using ABChapterize.Errors;
using ABChapterize.Language;
using ABChapterize.Language.Phrases;
using ABChapterize.Detection;

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
        Assert.Equal("/(?:^chapter ()|^() chapter|^chapter)/", o.ChapterPhrase);
        Assert.Equal("Chapter", o.Title);
        Assert.Equal("Intro", o.IntroTitle);
        // The probing model and the pass-3 model are deliberately different by default: short probe
        // windows are what "small" is better at, long transcriptions what "turbo" is better at.
        Assert.Equal("small", o.Model);
        Assert.Equal("turbo", o.Pass3Model);
        Assert.True(o.Pass3ModelIsUpgrade);
        // The trailing scan is on unless --no-trailing-scan says otherwise.
        Assert.True(o.TrailingScan);
        Assert.Equal(1.5, o.MinSilenceSeconds);
        Assert.True(o.AutoMinSilence);
        Assert.Equal(60, o.EarlyAbortMinutes);
        Assert.Equal([new CliOptions.Target(_file, IsDirectory: false)], o.Targets);
        // Mark refinement is on by default; --quick-marks is the opt-out, so it starts false
        // while PreciseMark itself starts true (asserted separately).
        Assert.True(o.PreciseMark);
        Assert.False(o.Recurse | o.Backup | o.Revert | o.NoOp | o.CpuOnly | o.Force | o.MarkBeforeJingle | o.QuickMarks | o.Quiet | o.Verbose
                     | o.NoBar | o.Summary | o.DryRun | o.Export | o.Import | o.SimpleMetadata | o.Verify);
        Assert.Null(o.VadThreads);
        Assert.Null(o.WhisperThreads);
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
        Assert.Equal("/(?:^kapitel ()|^() kapitel|^kapitel)/", profile.ChapterPhrase);
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
        Assert.Equal(["Prologue", "Epilogue"], profile.NamedPhrases.Select(p => p.Title.Raw));
        Assert.Equal(
            [NamedPhraseScope.BeforeFirstChapter, NamedPhraseScope.AfterFirstChapter],
            profile.NamedPhrases.Select(p => p.Scope));
        PhraseAssert.Matches(profile.NamedPhrases[0].Pattern, "and now, the Prologue.");
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
        Assert.Equal([prologue, epilogue], profile.NamedPhrases.Select(p => p.Title.Raw));
    }

    [Fact]
    public void NamedPhrases_HonourExplicitOverrides_RegardlessOfLanguage()
    {
        var profile = ParseFile("-p", "Vorspiel", "-P", "Vorspiel", "-g", "/nach(spiel|wort)/", "-G", "Nachspiel")!
            .ResolveProfile("de");
        Assert.Equal(["Vorspiel", "Nachspiel"], profile.NamedPhrases.Select(p => p.Title.Raw));
        PhraseAssert.Matches(profile.NamedPhrases[0].Pattern, "Vorspiel");
        // The "/regexp/" spelling --chapter-phrase accepts works for the named phrases too.
        PhraseAssert.Matches(profile.NamedPhrases[1].Pattern, "Nachwort");
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
        Assert.Equal(["Zwischenspiel", "Zeittafel"], custom.Select(p => p.Title.Raw));
        PhraseAssert.Matches(custom[1].Pattern, "die Zeit-Tafel");
    }

    [Fact]
    public void Custom_IsNotLocalized_ByLang()
    {
        // A phrase the user wrote out means exactly what it says, whatever --lang is set to.
        var profile = ParseFile("--lang", "de", "-u", "interlude:Interlude")!.ResolveProfile("de");

        Assert.Equal("Interlude", profile.NamedPhrases.Single(p => p.Repeatable).Title.Raw);
    }

    [Fact]
    public void PerLanguage_PhraseAndTitle_ResolveIndependentlyPerFile()
    {
        // What the syntax exists for: one batch run over a mixed-language library, where --lang
        // auto resolves a different language per file and one literal phrase can only ever be right
        // for some of them.
        var options = ParseFile(
            "--chapter-phrase", "[fr]/(?:premi|1).re partie.? chapitre/;[en]section",
            "--chapter-title", "[fr]Chapitre;[en]Section")!;

        Assert.Equal("/(?:premi|1).re partie.? chapitre/", options.ResolveProfile("fr").ChapterPhrase);
        Assert.Equal("section", options.ResolveProfile("en").ChapterPhrase);
        Assert.Equal("Chapitre", options.ResolveProfile("fr").Title);
        Assert.Equal("Section", options.ResolveProfile("en").Title);
    }

    [Fact]
    public void ChapterTitle_IsTheDocumentedSpelling()
    {
        var options = ParseFile("--chapter-title", "Section")!;

        Assert.Equal("Section", options.ResolveProfile("en").Title);
    }

    [Fact]
    public void Title_StillWorks_AsTheUndocumentedOldSpelling()
    {
        // --title was renamed to --chapter-title in 0.11.0 and kept working silently: nothing about
        // it was wrong except the name, so a script or a shell history carrying it must not break.
        // It is gone from every doc, which is what the usage-text assertion below pins.
        var options = ParseFile("--title", "Section")!;

        Assert.Equal("Section", options.ResolveProfile("en").Title);
        Assert.Equal(ParseFile("--chapter-title", "Section")!.RunFingerprint, options.RunFingerprint);
    }

    [Fact]
    public void ShortFormT_MeansChapterTitle()
    {
        var options = ParseFile("-t", "Section")!;

        Assert.Equal("Section", options.ResolveProfile("en").Title);
    }

    [Fact]
    public void UsageText_NamesChapterTitleAndNotTheOldSpelling()
    {
        // The half of the rename that cannot be checked by parsing: --title keeps working but must
        // not be advertised anywhere, or the two spellings start propagating side by side again.
        Assert.Contains("--chapter-title", CliOptions.UsageText);
        Assert.DoesNotMatch(@"(?<!-)--title\b", CliOptions.UsageText);
    }

    [Fact]
    public void UsageText_GroupsPhrasesWithTitlesAndKeepsIgnoreChapterNumbersWithTheSafetyNets()
    {
        // The option groups are a documented surface of their own - "where do I look for this" -
        // and the three sections below are the ones whose membership has been deliberately chosen
        // rather than inherited. Pinned by section boundaries so a stray option cannot drift in.
        var text = CliOptions.UsageText;
        string Section(string header, string next)
        {
            var from = text.IndexOf(header, StringComparison.Ordinal);
            Assert.True(from >= 0, $"usage text has no \"{header}\" section");
            var to = text.IndexOf(next, from, StringComparison.Ordinal);
            Assert.True(to > from, $"\"{next}\" does not follow \"{header}\"");
            return text[from..to];
        }

        var phrases = Section("Phrases & titles:", "Detection safety nets:");
        foreach (var option in new[]
                 {
                     "--chapter-phrase", "--prologue-phrase", "--epilogue-phrase", "--custom",
                     "--custom-file", "--chapter-title", "--intro-title", "--prologue-title",
                     "--epilogue-title",
                 })
            Assert.Contains(option, phrases);

        var safetyNets = Section("Detection safety nets:", "Output & review:");
        Assert.Contains("--ignore-chapter-numbers", safetyNets);

        var performance = Section("Performance:", "Info:");
        Assert.Contains("--cpu-only", performance);
        Assert.Contains("--use-gpu", performance);
        Assert.Contains("--whisper-threads", performance);
    }

    [Fact]
    public void PerLanguage_LeavesUnnamedLanguagesOnTheirOwnDefaults()
    {
        // The property that makes the feature additive: naming French does not impose French on the
        // German files in the same run - they get the German defaults, exactly as if the option had
        // never been given.
        var profile = ParseFile("--chapter-phrase", "[fr]chapitre", "--chapter-title", "[fr]Chapitre")!
            .ResolveProfile("de");

        Assert.Equal("/(?:^kapitel ()|^() kapitel|^kapitel)/", profile.ChapterPhrase);
        Assert.Equal("Kapitel", profile.Title);
    }

    [Fact]
    public void PerLanguage_UsesAnUntaggedEntryAsTheFallback()
    {
        var options = ParseFile("--chapter-title", "[fr]Chapitre;Section")!;

        Assert.Equal("Chapitre", options.ResolveProfile("fr").Title);
        Assert.Equal("Section", options.ResolveProfile("de").Title);
    }

    [Fact]
    public void PerLanguage_SplitsAtEverySemicolon_TaggedOrNot()
    {
        // A semicolon always separates alternatives, whether anything carries a tag or not: the
        // phrase is a list, and "or this as well" is what a second entry says.
        var options = ParseFile("--chapter-phrase", "/kapitel/;/abschnitt/")!;

        Assert.Equal("/kapitel/;/abschnitt/", options.ResolveProfile("de").ChapterPhrase);
        PhraseAssert.Matches(options.ResolveProfile("de").ChapterPattern, "kapitel 3");
        PhraseAssert.Matches(options.ResolveProfile("de").ChapterPattern, "abschnitt 3");
    }

    /// <summary>
    /// An untagged alternative applies to every language, tagged ones adding to it - so naming
    /// French does not take the shared wording away from the German files in the same batch.
    /// </summary>
    [Fact]
    public void PerLanguage_AddsTaggedAlternativesToTheUntaggedOnes()
    {
        var options = ParseFile("--chapter-phrase", "[fr]/chapitre/;/kapitel/;[fr]/partie/")!;

        Assert.Equal("/chapitre/;/kapitel/;/partie/", options.ResolveProfile("fr").ChapterPhrase);
        Assert.Equal("/kapitel/", options.ResolveProfile("de").ChapterPhrase);
    }

    /// <summary>Repeating a phrase option is defined as writing its values as one
    /// semicolon-separated list.</summary>
    [Fact]
    public void PerLanguage_RepeatingThePhraseOption_AddsAlternatives()
    {
        var options = ParseFile("-c", "/kapitel/", "-c", "[en]/chapter/")!;

        Assert.Equal("/kapitel/;/chapter/", options.ResolveProfile("en").ChapterPhrase);
        Assert.Equal("/kapitel/", options.ResolveProfile("de").ChapterPhrase);
    }

    /// <summary>"default" pulls this tool's own wording for the language back into the list, so a
    /// value can add to the built-in phrases instead of replacing them.</summary>
    [Fact]
    public void PerLanguage_DefaultPullsInTheBuiltInWording()
    {
        var options = ParseFile("-c", "/abschnitt/;[de]default;[fr]default")!;

        Assert.Equal(
            "/abschnitt/;/(?:^kapitel ()|^() kapitel|^kapitel)/",
            options.ResolveProfile("de").ChapterPhrase);
        Assert.Equal(
            "/abschnitt/;/(?:^chapitre ()|^() chapitre|^chapitre)/",
            options.ResolveProfile("fr").ChapterPhrase);
        // A language the value says nothing extra about keeps only what it was given.
        Assert.Equal("/abschnitt/", options.ResolveProfile("en").ChapterPhrase);
    }

    /// <summary>"default" and "none" combine: a book whose chapters are sometimes announced and
    /// sometimes just numbered.</summary>
    [Fact]
    public void PerLanguage_DefaultCombinesWithNone()
    {
        var profile = ParseFile("-c", "default;none", "--lang", "de")!.DefaultProfile;

        // "default" brings the language's own three wordings, "none" adds a fourth.
        Assert.Equal(4, profile.ChapterPattern.Alternatives.Count);
        Assert.True(profile.BareNumberAnnouncements);
        PhraseAssert.Matches(profile.ChapterPattern, "kapitel 3");
    }

    [Fact]
    public void PerLanguage_KeepsTheEscapedSemicolonInsideATaggedEntry()
    {
        // A semicolon a tagged entry really needs is written "\;", the same escape --custom uses.
        var options = ParseFile("--chapter-phrase", @"[de]/kapitel[\;:]/;[en]/chapter/")!;

        Assert.Equal("/kapitel[;:]/", options.ResolveProfile("de").ChapterPhrase);
        Assert.Equal("/chapter/", options.ResolveProfile("en").ChapterPhrase);
    }

    [Theory]
    [InlineData("[fr]Chapitre;[fr]Chapitre")]   // the same language twice
    [InlineData("[fr]Chapitre;A;B")]            // two entries claiming to be the fallback
    public void PerLanguage_IsRejected_WhenTheSpecContradictsItself(string spec)
        => Assert.Throws<CliError>(() => ParseFile("--chapter-title", spec));

    [Fact]
    public void PerLanguage_ChecksEveryLanguagesOwnEntryForEmptiness()
        => Assert.Throws<CliError>(() => ParseFile("--chapter-phrase", "[fr];[en]chapter"));

    [Fact]
    public void PerLanguage_Custom_AppliesAMappingOnlyToItsOwnLanguage()
    {
        var options = ParseFile("--custom", "[fr]/scène/:Scène;[en]/scene/:Scene;/zeittafel/:Zeittafel")!;

        Assert.Equal(
            ["Scène", "Zeittafel"],
            options.ResolveProfile("fr").NamedPhrases.Where(p => p.Repeatable).Select(p => p.Title.Raw));
        Assert.Equal(
            ["Scene", "Zeittafel"],
            options.ResolveProfile("en").NamedPhrases.Where(p => p.Repeatable).Select(p => p.Title.Raw));
        // A language neither mapping names keeps only the untagged one.
        Assert.Equal(
            ["Zeittafel"],
            options.ResolveProfile("de").NamedPhrases.Where(p => p.Repeatable).Select(p => p.Title.Raw));
    }

    [Fact]
    public void PerLanguage_Custom_KeepsAMappingsOwnNumberInItsKind()
    {
        // The kind a log line names has to point at the mapping the user wrote, whatever the file's
        // language leaves out of the list.
        var profile = ParseFile("--custom", "[fr]/scène/:Scène;/zeittafel/:Zeittafel")!.ResolveProfile("de");

        Assert.Equal(["custom 2"], profile.NamedPhrases.Where(p => p.Repeatable).Select(p => p.Kind));
    }

    [Fact]
    public void PerLanguage_DistinguishesTwoSpecsThatAgreeOnTheFallbackLanguage()
        => Assert.NotEqual(
            ParseFile("--chapter-title", "[en]Section;[fr]Chapitre")!.RunFingerprint,
            ParseFile("--chapter-title", "[en]Section;[de]Kapitel")!.RunFingerprint);

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
    public void Custom_HintsResolveIntoThePhraseTheBuiltInPrologueIs()
    {
        // The point of the hints: the prologue and the epilogue were always this same machinery
        // with different values, and a mapping can now ask for exactly those values - the tag
        // supplying the scope and the single-mark rule, the phrase's own "^" the pause in front.
        //
        // The two carry that pause differently: the mapping on the wording that matched, the
        // prologue on the phrase itself, which is why the fields are asserted apart and the
        // *rule* asserted equal. That they cannot come out differently is why the "heading" hint
        // was removed before 0.12.0 shipped - it was a second spelling of this one demand.
        var tagged = ParseFile("--custom", "[before-first-chapter,once]/^vorwort/:Vorwort")!
            .ResolveProfile("en").NamedPhrases.Single(p => p.IsCustom);
        var prologue = ParseFile("--custom", "x:X")!
            .ResolveProfile("en").NamedPhrases.Single(p => p.Kind == NamedPhrase.PrologueKind);

        Assert.Equal(prologue.Scope, tagged.Scope);
        Assert.Equal(prologue.Repeatable, tagged.Repeatable);
        Assert.Null(tagged.MaxMarks);

        Assert.All(tagged.Pattern.Alternatives, a => Assert.True(a.RequiresLeadIn));
        Assert.False(tagged.RequiresLeadIn);
        Assert.True(prologue.RequiresLeadIn);

        Assert.Equal(IsolationRule.LeadIn, EffectiveRule(tagged));
        Assert.Equal(IsolationRule.LeadIn, EffectiveRule(prologue));
        return;

        // What the placement layer actually judges the mark on, fed each route's own guards.
        static IsolationRule EffectiveRule(NamedPhrase phrase)
        {
            var guards = phrase.Pattern.Alternatives[0].RequiresLeadIn
                ? IsolationRule.LeadIn
                : IsolationRule.None;
            return RegionProber.NamedIsolationFor(
                new PhraseMatching.NamedMatch(phrase, "T", 0, 1, 1.0, "", guards), 0).Rule;
        }
    }

    /// <summary>
    /// The removed hint names itself rather than falling into the unknown-keyword branch, which
    /// would list what is accepted and leave the reader to work out that the replacement is not a
    /// keyword at all.
    /// </summary>
    [Fact]
    public void Custom_TheRemovedHeadingHint_PointsAtTheCaret()
    {
        var error = Assert.Throws<CliError>(
            () => ParseFile("--custom", "[before-first-chapter,heading]/vorwort/:Vorwort"));

        Assert.Contains("heading", error.Message);
        Assert.Contains("^", error.Message);
    }

    [Fact]
    public void Custom_AnUntaggedMappingKeepsItsOldDefaults()
    {
        var phrase = ParseFile("--custom", "zwischenspiel:Zwischenspiel")!
            .ResolveProfile("en").NamedPhrases.Single(p => p.IsCustom);

        Assert.Equal(NamedPhraseScope.Anywhere, phrase.Scope);
        Assert.True(phrase.Repeatable);
        Assert.False(phrase.RequiresLeadIn);
        Assert.Null(phrase.MaxMarks);
    }

    [Fact]
    public void Custom_HintsChangeTheRunFingerprint()
    {
        // A hint changes what the run does to a file, so a batch's recorded progress must not be
        // resumed under a command line that added or dropped one.
        Assert.NotEqual(
            ParseFile("--custom", "zwischenspiel:Zwischenspiel")!.RunFingerprint,
            ParseFile("--custom", "[once]zwischenspiel:Zwischenspiel")!.RunFingerprint);
    }

    /// <summary>Hints mean nothing outside <c>--custom</c>, so the options that read the same tag
    /// for its language half refuse them rather than ignoring them.</summary>
    /// <param name="option">The option to give the tagged value to.</param>
    [Theory]
    [InlineData("--chapter-phrase")]
    [InlineData("--chapter-title")]
    [InlineData("--prologue-phrase")]
    public void ALocalizedOption_RejectsACustomHint(string option)
        => Assert.Throws<CliError>(() => ParseFile(option, "[once]whatever"));

    [Fact]
    public void Custom_TitleMayReferenceACapturingGroup()
    {
        var phrase = ParseFile("--custom", "/(?<kind>interlude|intermezzo)/:The ${kind}")!
            .ResolveProfile("en").NamedPhrases.Single(p => p.Repeatable);

        Assert.Equal("The intermezzo", Resolve(phrase, "an intermezzo now"));
    }

    /// <summary>An unnamed group is the chapter number wherever it appears, so a title asks for it
    /// by that name - and gets it in digits, whatever notation the narrator used.</summary>
    [Theory]
    [InlineData("/interlude ()/", "interlude thirteen", "Interlude 13")]
    [InlineData("/interlude ()/", "interlude XIII.", "Interlude 13")]
    [InlineData("/interlude ()/", "Interlude 13", "Interlude 13")]
    public void Custom_TitleMayReferenceTheNumber(string phrase, string heard, string expected)
        => Assert.Equal(expected, Resolve(
            ParseFile("--custom", $"{phrase}:Interlude ${{number}}")!
                .ResolveProfile("en").NamedPhrases.Single(p => p.Repeatable),
            heard));

    [Fact]
    public void Custom_TitleMayAskForRomanNumeralsAndCase()
    {
        var options = ParseFile(
            "--custom",
            "/interlude ()/:Interlude $roman{number}",
            "--custom",
            "/(?<kind>interlude|intermezzo)/:$upper{kind}\\;$lower{kind}\\;$capital{kind}")!;
        var phrases = options.ResolveProfile("en").NamedPhrases.Where(p => p.Repeatable).ToList();

        Assert.Equal("Interlude XIII", Resolve(phrases[0], "interlude thirteen"));
        Assert.Equal("INTERMEZZO;intermezzo;Intermezzo", Resolve(phrases[1], "an intermezzo now"));
    }

    [Fact]
    public void Custom_TitleReferencingAMissingGroup_IsRejected()
        => Assert.Throws<CliError>(() => ParseFile("--custom", "/(?<a>interlude)/:Part ${b}"));

    /// <summary>Index references were how a title named a group until 0.12.0. They are refused
    /// rather than quietly read as literal text, which is the one outcome nobody would notice.</summary>
    [Fact]
    public void Custom_TitleReferencingAGroupByIndex_IsRejected()
        => Assert.Throws<CliError>(() => ParseFile("--custom", "/(interlude)/:The $1"));

    [Fact]
    public void Custom_TitleWithAnUnknownConversion_IsRejected()
        => Assert.Throws<CliError>(() => ParseFile("--custom", "/(?<a>x)/:The $romen{a}"));

    [Fact]
    public void Custom_TitleKeepsAnOrdinaryDollarSign()
    {
        // "$5" is a price: only "$name{...}" and "${name}" are references, and a bare digit after
        // the dollar is refused outright rather than read either way.
        var phrase = ParseFile("--custom", "bargain:Only $$5")!
            .ResolveProfile("en").NamedPhrases.Single(p => p.Repeatable);

        Assert.Equal("Only $5", Resolve(phrase, "a bargain"));
        // A dollar before an ordinary word is not a reference either, and needs no escape.
        Assert.Equal("Costs $lots", Resolve(
            ParseFile("--custom", "bargain:Costs $lots")!
                .ResolveProfile("en").NamedPhrases.Single(p => p.Repeatable),
            "a bargain"));
    }

    /// <summary>The title one phrase writes for the first place it matches in some text.</summary>
    /// <param name="phrase">The resolved phrase.</param>
    /// <param name="text">The transcript text it should match in.</param>
    private static string Resolve(NamedPhrase phrase, string text)
        => phrase.ResolveTitle(phrase.Pattern.Matches(text).First().Match, "en");

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
    [InlineData("--verify")]
    [InlineData("--import")]
    [InlineData("--chapter-count", "12")]
    public void IgnoreChapterNumbers_RejectsANumberBasedFlag(string flag, string? value = null)
        => Assert.Throws<CliError>(() =>
            ParseFile(value is null ? ["--ignore-chapter-numbers", flag]
                                    : ["--ignore-chapter-numbers", flag, value]));

    [Theory]
    [InlineData("--chapter-phrase", "part")]
    [InlineData("--chapter-title", "Part")]
    public void IgnoreChapterNumbers_StillTakesTheChapterPhraseOptions(string opt, string value)
        => Assert.True(ParseFile("--ignore-chapter-numbers", opt, value)!.IgnoreChapterNumbers);

    [Fact]
    public void JingleFirst_IsOffByDefault()
        => Assert.False(ParseFile()!.JingleFirst);

    [Fact]
    public void JingleFirst_ForcesTheShape()
        => Assert.True(ParseFile("--jingle-first")!.JingleFirst);

    /// <summary>The shape defers a book's pauses to wherever its chapter sequence still has a hole,
    /// which is not a question that can be asked of a run forming no opinion about the numbers.</summary>
    [Fact]
    public void JingleFirst_CannotBeCombinedWithIgnoreChapterNumbers()
        => Assert.Throws<CliError>(() => ParseFile("--jingle-first", "--ignore-chapter-numbers"));

    [Fact]
    public void JingleFirst_ChangesTheRunFingerprint()
        => Assert.NotEqual(ParseFile()!.RunFingerprint, ParseFile("--jingle-first")!.RunFingerprint);

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

    [Fact]
    public void ThreadCounts_AreParsed()
    {
        Assert.Equal(3, ParseFile("--vad-threads", "3")!.VadThreads);
        Assert.Equal(7, ParseFile("--whisper-threads", "7")!.WhisperThreads);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("AUTO")]
    public void ThreadCounts_Auto_ParseAsNull(string value)
    {
        Assert.Null(ParseFile("--vad-threads", value)!.VadThreads);
        Assert.Null(ParseFile("--whisper-threads", value)!.WhisperThreads);
    }

    /// <summary>"auto" is not a value of its own to the rest of the tool: it resolves to the
    /// machine's physical core count, which is what every caller actually reads.</summary>
    [Fact]
    public void ThreadCounts_ResolveAutoToAPlausibleCoreCount()
    {
        var auto = ParseFile("--vad-threads", "auto")!;
        Assert.InRange(auto.EffectiveVadThreads, 1, Environment.ProcessorCount);
        Assert.InRange(auto.EffectiveWhisperThreads, 1, Environment.ProcessorCount);

        var explicitly = ParseFile("--vad-threads", "2", "--whisper-threads", "5")!;
        Assert.Equal(2, explicitly.EffectiveVadThreads);
        Assert.Equal(5, explicitly.EffectiveWhisperThreads);
    }

    [Theory]
    [InlineData("--vad-threads", "0")]
    [InlineData("--vad-threads", "-1")]
    [InlineData("--vad-threads", "many")]
    [InlineData("--whisper-threads", "0")]
    [InlineData("--whisper-threads", "-1")]
    [InlineData("--whisper-threads", "many")]
    public void InvalidThreadCounts_AreRejected(string option, string value)
    {
        Assert.Throws<CliError>(() => ParseFile(option, value));
    }

    [Fact]
    public void ThreadCountsWithRevert_AreAnError()
    {
        Assert.Throws<CliError>(() => ParseDir("--revert", "--vad-threads", "2"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--whisper-threads", "2"));
    }

    /// <summary>--jobs was removed in 0.10.0 but is still recognized, purely so a script that
    /// carries it is told what replaced it rather than only that it is unknown.</summary>
    [Fact]
    public void RemovedJobsOption_IsRejectedByName()
    {
        var error = Assert.Throws<CliError>(() => ParseFile("--jobs", "4"));
        Assert.Contains("--vad-threads", error.Message);
        Assert.Contains("--whisper-threads", error.Message);
        // The short form is gone outright, so the letter is free again.
        Assert.Throws<CliError>(() => ParseFile("-J", "4"));
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
    [InlineData("--no-trailing-scan")]
    [InlineData("--min-silence-length", "2")]
    [InlineData("--mark-lead", "0.5")]
    [InlineData("--early-abort", "30")]
    [InlineData("--expected-start-chapter", "5")]
    [InlineData("--max-chapter-number", "50")]
    [InlineData("--named-mark-distance", "5")]
    [InlineData("--verify")]
    // The titles are not detection settings, but an imported mark carries the sidecar's own title
    // and no intro mark is prepended, so they are equally inert and equally refused.
    [InlineData("--chapter-title", "Chapter")]
    [InlineData("--intro-title", "Intro")]
    [InlineData("--prologue-title", "Prologue")]
    [InlineData("--epilogue-title", "Epilogue")]
    [InlineData("--jingle-first")]
    public void ImportWithDetectionOptions_IsAnError(params string[] extra)
    {
        Assert.Throws<CliError>(() => ParseFile([.. new[] { "--import" }, .. extra]));
    }

    [Theory]
    [InlineData("--import", "--lang", "de")]
    [InlineData("--ignore-chapter-numbers", "--verify")]
    [InlineData("--ignore-chapter-numbers", "--jingle-first")]
    public void AnIncompatibilityMessage_NamesOnlyOptionsTheHelpAlsoLists(params string[] args)
    {
        // Each of these lists of mutually exclusive options exists three times over: the check
        // itself, the error message naming them, and the matching --help entry. Message and help
        // had already drifted apart once - --custom, --custom-file and --ignore-chapter-numbers
        // were missing from --help's --import entry (found 2026-07-31) - which nothing noticed,
        // since both spellings are prose. Anchoring the help text to the message at least keeps
        // the two a user actually reads in agreement.
        var message = Assert.Throws<CliError>(() => ParseFile(args)).Message;
        foreach (Match named in Regex.Matches(message, "--[a-z0-9-]+"))
            Assert.Contains(named.Value, CliOptions.UsageText);
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

    /// <summary>Creates a fake GGML file of the given size in the temp directory and returns the
    /// <c>custom:</c> selector naming it.</summary>
    /// <param name="name">File name to create.</param>
    /// <param name="bytes">Size to give it - what the model ranking goes by.</param>
    private string CustomModel(string name, long bytes)
    {
        var path = Path.Combine(_dir, name);
        using (var f = File.Create(path))
            f.SetLength(bytes);
        return "custom:" + path;
    }

    [Fact]
    public void CustomModel_IsAcceptedForBothModelOptions_AsAnAbsolutePath()
    {
        var selector = CustomModel("my-finetune.bin", 1000);
        var o = ParseFile("--model", selector, "--pass3-model", selector)!;
        Assert.Equal(selector, o.Model);
        Assert.Equal(selector, o.Pass3Model);
    }

    [Fact]
    public void CustomModel_KeepsItsPathsCase()
    {
        // A built-in name is case-normalized; a path must not be, or it would break on Linux.
        var selector = CustomModel("MyFineTune.bin", 1000);
        Assert.Equal(selector, ParseFile("-m", selector)!.Model);
    }

    [Fact]
    public void CustomModel_IsResolvedToAnAbsolutePath()
    {
        // Two spellings of one file must produce one selector: that string comparison is what
        // decides whether pass 3 loads a second model at all.
        var absolute = ParseFile("-m", CustomModel("m.bin", 1000))!.Model;
        var viaDots = ParseFile("-m", $"custom:{Path.Combine(_dir, "sub", "..", "m.bin")}")!.Model;
        Assert.Equal(absolute, viaDots);
    }

    [Fact]
    public void CustomModel_ThatDoesNotExist_IsRejected()
    {
        var ex = Assert.Throws<CliError>(() =>
            ParseFile("--model", "custom:" + Path.Combine(_dir, "nothing-here.bin")));
        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public void CustomModel_WithoutAPath_IsRejected()
    {
        Assert.Throws<CliError>(() => ParseFile("--model", "custom:"));
    }

    [Fact]
    public void CustomModel_RanksAgainstBuiltInModelsBySize()
    {
        // Bigger than "large" (3.1 GB) is not worth writing to disk, so the comparison is made in
        // the other direction: a 1 KB file is lighter than every built-in model, and pass 2.5 is
        // therefore off in one direction and on in the other.
        var tinyCustom = CustomModel("small-custom.bin", 1000);
        Assert.False(ParseFile("--model", "tiny", "--pass3-model", tinyCustom)!.Pass3ModelIsUpgrade);
        Assert.True(ParseFile("--model", tinyCustom, "--pass3-model", "tiny")!.Pass3ModelIsUpgrade);
    }

    [Fact]
    public void TwoCustomModels_RankAgainstEachOtherBySize()
    {
        var smaller = CustomModel("a.bin", 1000);
        var bigger = CustomModel("b.bin", 2000);
        Assert.True(ParseFile("-m", smaller, "-M", bigger)!.Pass3ModelIsUpgrade);
        Assert.False(ParseFile("-m", bigger, "-M", smaller)!.Pass3ModelIsUpgrade);
    }

    [Fact]
    public void CustomModel_TakesPartInTheRunFingerprint()
    {
        // Different weights, different results - resuming across them would be wrong.
        Assert.NotEqual(
            CliOptions.Parse(["-m", CustomModel("a.bin", 1000), _dir])!.RunFingerprint,
            CliOptions.Parse(["-m", CustomModel("b.bin", 1000), _dir])!.RunFingerprint);
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
    public void Pass3Model_FollowsTheModelChosen_UnlessNeitherWasNamed()
    {
        // Naming --model alone re-points pass 3 at it, so there is nothing to upgrade to and pass 2.5
        // stays off - which is what keeps "-m large" meaning large throughout rather than large
        // probing and a quietly lighter pass 3.
        var picked = ParseFile("--model", "tiny")!;
        Assert.Equal("tiny", picked.Pass3Model);
        Assert.False(picked.Pass3ModelIsUpgrade);
        // Naming neither keeps the small/turbo pair, and with it the upgrade that turns pass 2.5 on.
        var bare = ParseFile()!;
        Assert.Equal("turbo", bare.Pass3Model);
        Assert.True(bare.Pass3ModelIsUpgrade);
    }

    [Theory]
    // Strictly lighter than the pass-2 model: the one unambiguous "get the stragglers over with".
    [InlineData("large", "turbo", true)]
    [InlineData("small", "tiny", true)]
    // Equal or better is not a downgrade - and equal is neither direction, which is the whole
    // reason this is not simply the negation of Pass3ModelIsUpgrade.
    [InlineData("large", "large", false)]
    [InlineData("tiny", "tiny", false)]
    [InlineData("base", "small", false)]
    public void Pass3ModelIsDowngrade_IsOnlyTheStrictlyLighterDirection(
        string model, string pass3Model, bool expected)
    {
        var o = ParseFile("--model", model, "--pass3-model", pass3Model)!;
        Assert.Equal(expected, o.Pass3ModelIsDowngrade);
    }

    [Fact]
    public void Pass3ModelIsDowngrade_IsFalse_WhenNoPass3ModelWasGivenAtAll()
    {
        // The default mirrors --model, so pass 3's shifted re-read stays available.
        Assert.False(ParseFile("--model", "large")!.Pass3ModelIsDowngrade);
        Assert.False(ParseFile()!.Pass3ModelIsDowngrade);
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
    public void TrailingScan_IsOnByDefault_AndOffOnlyWithNoTrailingScan()
    {
        Assert.True(ParseFile()!.TrailingScan);
        Assert.False(ParseFile("--no-trailing-scan")!.TrailingScan);
    }

    [Fact]
    public void TrailingScan_RejectsItsOldSpelling_WithAMigrationMessage()
    {
        // Both the long form and the -L it still maps from, so a script carrying either is told the
        // scan it asked for is now the default rather than silently doing the opposite.
        foreach (var spelling in new[] { "--trailing-scan", "-L" })
        {
            var error = Assert.Throws<CliError>(() => ParseFile(spelling));
            Assert.Contains("--no-trailing-scan", error.Message);
        }
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
    public void LogFile_TurnsLoggingOn_WithoutTurningOnConsoleVerbosity()
    {
        var o = ParseFile("--log-file", Path.Combine(_dir, "run.log"))!;
        Assert.Equal(Path.Combine(_dir, "run.log"), o.LogFilePath);
        Assert.True(o.LoggingEnabled);
        Assert.False(o.Verbose);
    }

    [Fact]
    public void LogFile_HasTheShortFormO()
    {
        Assert.Equal(Path.Combine(_dir, "run.log"),
            ParseFile("-o", Path.Combine(_dir, "run.log"))!.LogFilePath);
    }

    [Fact]
    public void LoggingEnabled_IsAlsoTrueForPlainVerbose()
    {
        Assert.True(ParseFile("--verbose")!.LoggingEnabled);
        Assert.False(ParseFile()!.LoggingEnabled);
    }

    [Fact]
    public void LogFile_InAMissingDirectory_IsRejectedAtParseTime()
    {
        // Caught now rather than hours into an unattended run that would have logged nothing.
        var ex = Assert.Throws<CliError>(() =>
            ParseFile("--log-file", Path.Combine(_dir, "nope", "run.log")));
        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public void LogFile_NamingADirectory_IsRejected()
    {
        var ex = Assert.Throws<CliError>(() => ParseFile("--log-file", _dir));
        Assert.Contains("is a directory", ex.Message);
    }

    [Fact]
    public void LogFile_DoesNotChangeTheRunFingerprint()
    {
        // It only changes what the run looks like, so an interrupted run can be resumed with a
        // log file added to the command line.
        Assert.Equal(ParseDir()!.RunFingerprint,
            ParseDir("--log-file", Path.Combine(_dir, "run.log"))!.RunFingerprint);
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
        Assert.Throws<CliError>(() => ParseFile("--import", "--min-silence-length", "3"));
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
        Assert.Equal("/(?:^b[öo]l[üu]m ()|^() b[öo]l[üu]m|^b[öo]l[üu]m)/", o.ChapterPhrase);
        Assert.Equal("Bölüm", o.Title);
        Assert.Equal("Giriş", o.IntroTitle);
        // The point of the regexp default: a transcript that lost the dotted vowels still matches.
        PhraseAssert.Matches(o.DefaultProfile.ChapterPattern, "bolum 3");
        PhraseAssert.Matches(o.DefaultProfile.ChapterPattern, "Bölüm 3");
    }

    /// <summary>
    /// Guards the shape <see cref="ABChapterize.Language.ILanguage.ChapterPhrase"/> states every
    /// built-in chapter phrase has: three wordings - the number behind the word, the number in front
    /// of it, and the bare word - each asking to be set off in front and none behind, and no empty
    /// phrase, which is the prologue/epilogue opt-out spelling and must never be a language's
    /// default. Counted rather than indexed, because a stem alternation is multiplied out and
    /// Swedish and Danish ("kapitel"/"kapitlet") therefore arrive as six.
    /// </summary>
    [Fact]
    public void EveryRegisteredLanguage_HasUsableDefaultPhrases()
    {
        var o = ParseFile()!;
        foreach (var language in LanguageRegistry.Languages)
        {
            var profile = o.ResolveProfile(language.Code);
            var wordings = profile.ChapterPattern.Alternatives;
            Assert.Equal(0, wordings.Count % 3);
            Assert.Equal(wordings.Count / 3 * 2, wordings.Count(a => a.HasNumberGroup));
            Assert.True(wordings[0].HasNumberGroup, language.Code);
            Assert.False(wordings[^1].HasNumberGroup, language.Code);
            Assert.All(profile.ChapterPattern.Alternatives, a =>
            {
                Assert.True(a.RequiresLeadIn, language.Code);
                Assert.False(a.RequiresLeadOut, language.Code);
            });
            Assert.All(profile.NamedPhrases, p => Assert.All(p.Pattern.Alternatives,
                a => Assert.False(a.HasNumberGroup, language.Code)));
            Assert.NotEmpty(profile.Title);
            Assert.NotEmpty(profile.IntroTitle);
            Assert.Equal(2, profile.NamedPhrases.Count);
            Assert.All(profile.NamedPhrases, p => Assert.NotEmpty(p.Title.Raw));
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
        PhraseAssert.Matches(profile.ChapterPattern, chapter);
        PhraseAssert.Matches(profile.NamedPhrases[0].Pattern, prologue);
        PhraseAssert.Matches(profile.NamedPhrases[1].Pattern, epilogue);
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
    public void RunFingerprint_IgnoresOptionsThatDoNotChangeTheMarks()
    {
        Assert.Equal(ParseFile()!.RunFingerprint,
            ParseFile("--quiet", "--verbose", "--no-bar", "--summary",
                "--vad-threads", "3", "--whisper-threads", "3", "--cpu-only")!.RunFingerprint);
        // Separately, because --use-gpu and --cpu-only refuse to be combined: which device the
        // recognizer runs on decides how long a run takes, never where its marks land.
        Assert.Equal(ParseFile()!.RunFingerprint,
            ParseFile("--use-gpu", "gtx")!.RunFingerprint);
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
        Assert.Throws<CliError>(() => ParseDir("--revert", "--no-trailing-scan"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--early-abort", "30"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--expected-start-chapter", "5"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--max-chapter-number", "50"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--cpu-only"));
        // The other half of the same statement: --revert loads no speech model, so it has neither
        // a GPU to refuse nor one to pick.
        Assert.Throws<CliError>(() => ParseDir("--revert", "--use-gpu", "gtx"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--max-chapters", "50"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--chapter-title", "Teil"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--intro-title", "Vorwort"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--min-silence-length", "2"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--min-silence-length", "2"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--chapter-phrase", "Teil"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--model", "large"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--export"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--simple-metadata"));
    }

    /// <summary>
    /// The two hooks run per processed file, so every mode that processes no file rejects them
    /// rather than accepting a command it would never run.
    /// </summary>
    [Fact]
    public void RunHooks_AreRefusedByTheModesThatProcessNothing()
    {
        Assert.Throws<CliError>(() => ParseDir("--revert", "--run-before", "echo $1"));
        Assert.Throws<CliError>(() => ParseDir("--revert", "--run-after", "echo $1"));
        Assert.Throws<CliError>(() => ParseDir("--cleanup", "--yes", "--run-before", "echo $1"));
        Assert.Throws<CliError>(() => ParseDir("--no-op", "--filter", "m4b", "--run-after", "echo $1"));
    }

    /// <summary>
    /// --import writes marks per file exactly as detection does, so unlike the detection options it
    /// has no reason to turn the hooks away.
    /// </summary>
    [Fact]
    public void RunHooks_AreAllowedWithImport()
    {
        var o = ParseFile("--import", "--run-before", "echo $1", "--run-after", "echo $0")!;
        Assert.Equal("echo $1", o.RunBefore!.Raw);
        Assert.Equal("echo $0", o.RunAfter!.Raw);
    }

    /// <summary>
    /// A hook changes what the run does to a file, so a batch resumed with a different one must not
    /// count the files finished without it as done.
    /// </summary>
    [Fact]
    public void RunHooks_TakePartInTheRunFingerprint()
    {
        Assert.NotEqual(ParseFile()!.RunFingerprint, ParseFile("--run-before", "echo $1")!.RunFingerprint);
        Assert.NotEqual(
            ParseFile("--run-after", "echo $1")!.RunFingerprint,
            ParseFile("--run-after", "echo $0")!.RunFingerprint);
        // The two are separate settings, not one: the same command in the other slot is a
        // different run.
        Assert.NotEqual(
            ParseFile("--run-before", "echo $1")!.RunFingerprint,
            ParseFile("--run-after", "echo $1")!.RunFingerprint);
    }

    [Fact]
    public void RunHooks_RejectAnEmptyCommandAndABadPlaceholder()
    {
        Assert.Throws<CliError>(() => ParseFile("--run-before", ""));
        Assert.Throws<CliError>(() => ParseFile("--run-after", "mv $-0 x"));
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
        Assert.Throws<CliError>(() => ParseDir("--no-op", "--filter", "m4b", "--vad-threads", "2"));
        Assert.Throws<CliError>(() => ParseDir("--no-op", "--filter", "m4b", "--whisper-threads", "2"));
        Assert.Throws<CliError>(() => ParseDir("--no-op", "--filter", "m4b", "--early-abort", "30"));
        Assert.Throws<CliError>(() => ParseDir("--no-op", "--filter", "m4b", "--expected-start-chapter", "5"));
        Assert.Throws<CliError>(() => ParseDir("--no-op", "--filter", "m4b", "--max-chapter-number", "50"));
        Assert.Throws<CliError>(() => ParseDir("--no-op", "--filter", "m4b", "--cpu-only"));
        Assert.Throws<CliError>(() => ParseDir("--no-op", "--filter", "m4b", "--use-gpu", "gtx"));
        Assert.Throws<CliError>(() => ParseDir("--no-op", "--filter", "m4b", "--backup"));
        Assert.Throws<CliError>(() => ParseDir("--no-op", "--filter", "m4b", "--verify"));
        Assert.Throws<CliError>(() => ParseDir("--no-op", "--filter", "m4b", "--export"));
        Assert.Throws<CliError>(() => ParseDir("--no-op", "--filter", "m4b", "--no-trailing-scan"));
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

    [Theory]
    [InlineData("-0.1")]
    [InlineData("11")]
    [InlineData("abc")]
    public void InvalidMarkLeads_AreRejected(string value)
    {
        Assert.Throws<CliError>(() => ParseFile("--mark-lead", value));
    }

    [Fact]
    public void MarkLead_DefaultsToTheTuningConstant_AndIsOverridable()
    {
        Assert.Equal(DetectionTuning.DefaultMarkLeadSeconds, ParseFile()!.MarkLeadSeconds);
        Assert.Equal(0.6, ParseFile("--mark-lead", "0.6")!.MarkLeadSeconds);
        Assert.Equal(0.6, ParseFile("-k", "0.6")!.MarkLeadSeconds);
    }

    [Fact]
    public void MarkLead_Zero_IsAccepted()
    {
        // "Mark exactly at the onset" is a legitimate taste, not a mistake - unlike the other
        // duration options, whose zero would mean "no silence/jingle at all" and break their logic.
        Assert.Equal(0, ParseFile("--mark-lead", "0")!.MarkLeadSeconds);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("601")]
    [InlineData("abc")]
    public void InvalidNamedMarkDistances_AreRejected(string value)
    {
        Assert.Throws<CliError>(() => ParseFile("--named-mark-distance", value));
    }

    [Fact]
    public void NamedMarkDistance_DefaultsToTenSeconds_AndIsOverridable()
    {
        Assert.Equal(10, ParseFile()!.NamedMarkDistanceSeconds);
        Assert.Equal(2.5, ParseFile("--named-mark-distance", "2.5")!.NamedMarkDistanceSeconds);
        Assert.Equal(2.5, ParseFile("-D", "2,5")!.NamedMarkDistanceSeconds);
        // Zero is how the merging is switched off, so it is a value rather than a mistake.
        Assert.Equal(0, ParseFile("--named-mark-distance", "0")!.NamedMarkDistanceSeconds);
    }

    [Fact]
    public void NamedMarkDistance_ChangesTheRunFingerprint()
    {
        // It decides which marks a file ends up with, so a resumed batch must not mix two values.
        Assert.NotEqual(
            ParseFile()!.RunFingerprint, ParseFile("--named-mark-distance", "5")!.RunFingerprint);
    }

    [Fact]
    public void MarkLead_ChangesTheRunFingerprint()
    {
        // A resumed run must not mix marks placed with two different leads.
        Assert.NotEqual(ParseFile()!.RunFingerprint, ParseFile("--mark-lead", "0.6")!.RunFingerprint);
    }

    [Fact]
    public void DecimalOptions_AcceptACommaAsWellAsAPointAsTheDecimalSeparator()
    {
        // Typed by a person on whatever keyboard/locale they have, so both notations mean the
        // same thing here - see NumberCulture. (Output, by contrast, is always "." regardless.)
        Assert.Equal(2.5, ParseFile("--min-silence-length", "2,5")!.MinSilenceSeconds);
        Assert.Equal(2.5, ParseFile("--min-silence-length", "2.5")!.MinSilenceSeconds);
        Assert.Equal(1.5, ParseFile("--early-abort", "1,5")!.EarlyAbortMinutes);
        Assert.Equal(1.5, ParseFile("--early-abort", "1.5")!.EarlyAbortMinutes);
        Assert.Equal(0.4, ParseFile("--mark-lead", "0,4")!.MarkLeadSeconds);
        Assert.Equal(0.4, ParseFile("--mark-lead", "0.4")!.MarkLeadSeconds);
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
    public void MinSilenceLengthZero_SwitchesSilenceProbingOff()
    {
        var o = ParseFile("--min-silence-length", "0")!;
        Assert.False(o.ProbeSilences);
        Assert.False(o.AutoMinSilence);
        Assert.True(ParseFile()!.ProbeSilences);
        Assert.True(ParseFile("--min-silence-length", "2")!.ProbeSilences);
    }

    /// <summary>Removed in 0.12.0, and told so rather than reported as an unknown option: a script
    /// carrying it asked for something the tool now measures for itself.</summary>
    [Fact]
    public void MaxJingleLength_IsRejectedWithAMigrationMessage()
    {
        var error = Assert.Throws<CliError>(() => ParseFile("--max-jingle-length", "30"));
        Assert.Contains("was removed", error.Message);
        Assert.Contains("always runs", error.Message);
        // Silence-only probing no longer has anything to be incompatible with.
        Assert.NotNull(ParseFile("--min-silence-length", "0"));
    }

    [Fact]
    public void ChapterPhraseNone_TurnsOnBareNumberAnnouncements()
    {
        var o = ParseFile("--chapter-phrase", "none")!;
        Assert.True(o.DefaultProfile.BareNumberAnnouncements);
        // "none" is shorthand for "/^()$/": one wording, no expression behind it, and both pause
        // guards asked for by the anchors.
        var wording = Assert.Single(o.DefaultProfile.ChapterPattern.Alternatives);
        Assert.True(wording.IsBareNumber);
        Assert.True(wording.RequiresLeadIn);
        Assert.True(wording.RequiresLeadOut);
        // Nothing is matched by pattern, so a caller that only asks the expressions finds nothing
        // rather than throwing.
        PhraseAssert.DoesNotMatch(o.DefaultProfile.ChapterPattern, "chapter seventeen none");
    }

    /// <summary>Per language like any other value: one series announces "Kapitel 17", another just
    /// says "Seventeen".</summary>
    [Fact]
    public void ChapterPhraseNone_WorksPerLanguage()
    {
        var o = ParseFile("--chapter-phrase", "[en]none;[de]/kapitel/")!;
        Assert.True(o.ResolveProfile("en").BareNumberAnnouncements);
        Assert.False(o.ResolveProfile("de").BareNumberAnnouncements);
    }

    /// <summary>"none" names the bare-number mode for the chapter phrase alone; for a prologue,
    /// which parses no number, it is just the word.</summary>
    [Fact]
    public void ProloguePhraseNone_IsTakenLiterally()
    {
        var profile = ParseFile("--prologue-phrase", "none")!.DefaultProfile;
        Assert.False(profile.BareNumberAnnouncements);
        PhraseAssert.Matches(profile.NamedPhrases.First(p => p.Kind == "prologue").Pattern, "and none of it");
    }

    [Fact]
    public void ChapterPhraseNone_WithIgnoreChapterNumbers_IsRejected()
        => Assert.Throws<CliError>(() => ParseFile("--chapter-phrase", "none", "--ignore-chapter-numbers"));

    /// <summary>Neither option is given by default, but a run is never left without a cap: see
    /// <see cref="CliOptions.DefaultChapterCount"/> for what an uncapped run costs.</summary>
    [Fact]
    public void ChapterCount_DefaultsToNull_ButTheCapDoesNot()
    {
        var o = ParseFile()!;
        Assert.Null(o.ChapterCount);
        Assert.Null(o.LastExpectedChapter);
        Assert.Null(o.MaxChapterNumber);
        Assert.Equal(CliOptions.DefaultChapterCount, o.EffectiveMaxChapterNumber);
    }

    /// <summary>The default allowance is counted from where the numbering starts, so a file holding
    /// the tail of a split book is not capped below its own first chapter.</summary>
    [Fact]
    public void TheDefaultCap_IsCountedFromExpectedStartChapter()
        => Assert.Equal(
            CliOptions.DefaultChapterCount + 249,
            ParseFile("--expected-start-chapter", "250")!.EffectiveMaxChapterNumber);

    [Fact]
    public void ChapterCount_IsParsed_AndBecomesTheCap()
    {
        var o = ParseFile("--chapter-count", "20")!;
        Assert.Equal(20, o.ChapterCount);
        Assert.Equal(20, o.LastExpectedChapter);
        Assert.Equal(20, o.EffectiveMaxChapterNumber);
    }

    [Fact]
    public void ChapterCount_IsCountedFromExpectedStartChapter()
    {
        var o = ParseFile("--chapter-count", "3", "--expected-start-chapter", "5")!;
        Assert.Equal(3, o.ChapterCount);
        Assert.Equal(7, o.LastExpectedChapter);
    }

    [Fact]
    public void InvalidChapterCount_IsRejected()
    {
        Assert.Throws<CliError>(() => ParseFile("--chapter-count", "0"));
        Assert.Throws<CliError>(() => ParseFile("--chapter-count", "-1"));
        Assert.Throws<CliError>(() => ParseFile("--chapter-count", "twenty"));
    }

    /// <summary>Both name the highest number the book may have; accepting both would mean picking
    /// which of two contradictory answers to believe.</summary>
    [Fact]
    public void ChapterCount_WithMaxChapterNumber_IsRejected()
        => Assert.Throws<CliError>(() => ParseFile("--chapter-count", "20", "--max-chapter-number", "20"));

    /// <summary>It says how many chapters *this book* has, which is not a thing that can be said
    /// about a folder of them.</summary>
    [Fact]
    public void ChapterCount_NeedsExactlyOneFile()
    {
        Assert.Throws<CliError>(() => CliOptions.Parse(["--chapter-count", "20", _dir]));
        var second = Path.Combine(_dir, "other.m4b");
        File.WriteAllText(second, "x");
        Assert.Throws<CliError>(() => CliOptions.Parse(["--chapter-count", "20", _file, second]));
        Assert.NotNull(ParseFile("--chapter-count", "20"));
    }

    [Fact]
    public void ChapterCount_WithIgnoreChapterNumbers_IsRejected()
        => Assert.Throws<CliError>(() => ParseFile("--chapter-count", "20", "--ignore-chapter-numbers"));

    /// <summary>
    /// A literal chapter phrase is the word with the number on either side of it - "Teil sieben"
    /// and "Siebter Teil" are the same announcement - and the word itself is escaped, so its
    /// punctuation is punctuation rather than syntax.
    /// </summary>
    [Fact]
    public void LiteralChapterPhrase_IsEscapedAndTakesTheNumberBehindTheWord()
    {
        var pattern = ParseFile("-c", "part (a)")!.DefaultProfile.ChapterPattern;
        Assert.Equal(2, pattern.Alternatives.Count);
        PhraseAssert.Matches(pattern, "PART (A) two");
        PhraseAssert.Matches(pattern, "the second part (a) of it");
        // The parentheses are the word's own, not a capturing group.
        PhraseAssert.DoesNotMatch(pattern, "part a two");
    }

    /// <summary>
    /// A literal chapter phrase asks for a pause in front, exactly as a built-in default does: a
    /// chapter word in the middle of a sentence is not an announcement, whoever supplied the word.
    /// Nothing behind it, though - a heading runs straight into its own text. See
    /// <c>PhraseCompiler.BodyOf</c>.
    /// </summary>
    [Fact]
    public void LiteralChapterPhrase_AsksForAPauseInFront()
        => Assert.All(ParseFile("-c", "part")!.DefaultProfile.ChapterPattern.Alternatives,
            a =>
            {
                Assert.True(a.RequiresLeadIn);
                Assert.False(a.RequiresLeadOut);
            });

    /// <summary>A literal named phrase still asks for nothing: a <c>--custom</c> mapping says it
    /// wants a pause with a <c>^</c> of its own, and the built-in prologue and epilogue carry the
    /// demand on the phrase whatever wording the user gives them.</summary>
    [Fact]
    public void LiteralNamedPhrase_AsksForNoPause()
        => Assert.All(ParseFile("-u", "zwischenspiel:Zwischenspiel")!
                .DefaultProfile.NamedPhrases[^1].Pattern.Alternatives,
            a =>
            {
                Assert.False(a.RequiresLeadIn);
                Assert.False(a.RequiresLeadOut);
            });

    /// <summary>An unnamed capturing group is the chapter number - the convention
    /// <c>--chapter-phrase "/part (\d+)/"</c> has always used, now spelled <c>()</c> when the
    /// language's own notation will do.</summary>
    [Fact]
    public void RegexChapterPhrase_WithCaptureGroup_IsTheNumber()
    {
        var pattern = ParseFile("-c", @"/chapter (\d+)/")!.DefaultProfile.ChapterPattern;
        Assert.True(Assert.Single(pattern.Alternatives).HasNumberGroup);
        Assert.Equal("12", PhraseAssert.Captured(pattern, "Chapter 12 begins"));
    }

    /// <summary>The <c>()</c> token takes the language's own number notation, so the same phrase
    /// covers digits, words and Roman numerals without any of them being written out.</summary>
    [Theory]
    [InlineData("Chapter 12 begins", "12")]
    [InlineData("Chapter twelve begins", "twelve")]
    [InlineData("Chapter XII. begins", "XII.")]
    [InlineData("Chapter one hundred and five begins", "one hundred and five")]
    public void RegexChapterPhrase_WithTheNumberToken_CapturesEveryNotation(
        string heard, string expected)
        => Assert.Equal(expected,
            PhraseAssert.Captured(ParseFile("-c", "/chapter ()/")!.DefaultProfile.ChapterPattern, heard));

    [Fact]
    public void RegexChapterPhrase_WithoutGroup_HasNoNumberGroup()
        => Assert.False(
            Assert.Single(ParseFile("-c", @"/chapter/")!.DefaultProfile.ChapterPattern.Alternatives)
                .HasNumberGroup);

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
