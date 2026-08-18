// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Text.RegularExpressions;
using ABChapterize.Detection;
using ABChapterize.Language;
using ABChapterize.Language.Phrases;
using ABChapterize.Vad;
using Xunit;

namespace ABChapterize.Tests;

/// <summary>
/// The pause-geometry guard that replaced trusting Whisper's segmentation
/// (<see cref="AnnouncementIsolation"/>), plus the matcher the mark refinement asks
/// (<see cref="AnnouncementMatcher"/>).
/// <para>
/// The measurements in these tests are not invented. Every flank figure is taken from the Analyze
/// speech segments of "Corsa nello spazio" (build 244, 2026-08-05), replayed at the announcement
/// positions its own debug log records - which is what calibrated the thresholds in the first
/// place.
/// </para>
/// </summary>
public class AnnouncementIsolationTests
{
    /// <summary>A speech timeline, as VAD would return it.</summary>
    private static List<SpeechSegment> Speech(params (double Start, double End)[] segments)
        => segments.Select(s => new SpeechSegment(s.Start, s.End)).ToList();

    /// <summary>One wording of a phrase, asking for the given pauses - what a match carries, and the
    /// only thing about it these tests care about. Written out rather than compiled from source
    /// because the point here is the guard, not the syntax that requests it.</summary>
    /// <param name="guards">The pauses its <c>^</c>/<c>$</c> asked for.</param>
    /// <param name="bare">Whether it is a number spoken alone rather than an expression.</param>
    private static PhraseAlternative Wording(IsolationRule guards, bool bare = false)
        => new(0, bare ? null : new Regex("x"), bare ? "^()$" : "x", bare,
            guards.HasFlag(IsolationRule.LeadIn), guards.HasFlag(IsolationRule.LeadOut));

    /// <summary>
    /// A typical chapter of "Corsa nello spazio": narration, a ~3 s pause, the number spoken alone
    /// for two thirds of a second, a ~2 s pause, then the chapter's first sentence. Chapter 59's
    /// real geometry, rounded.
    /// </summary>
    private static List<SpeechSegment> AnnouncementTimeline()
        => Speech((0, 10), (13.0, 13.7), (15.7, 25.0));

    [Fact]
    public void Measure_ReportsTheGapsAroundTheAnnouncement()
    {
        var flanks = AnnouncementIsolation.Measure(13.0, AnnouncementTimeline());
        Assert.NotNull(flanks);
        Assert.Equal(3.0, flanks!.Value.LeadInSeconds, 3);
        Assert.Equal(2.0, flanks.Value.LeadOutSeconds, 3);
        Assert.Equal(13.0, flanks.Value.SpeechStartSeconds, 3);
    }

    /// <summary>
    /// The refinement anchors an onset to where the waveform's sound resumes, while VAD needs a few
    /// frames before it commits - so an onset lands slightly before "its" speech segment, and
    /// occasionally a hair inside the tail of the one before. Both must find the same segment.
    /// </summary>
    [Theory]
    [InlineData(12.6)]
    [InlineData(13.0)]
    [InlineData(13.4)]
    [InlineData(9.95)]
    public void Measure_FindsTheAnnouncementFromEitherSideOfItsSpeechStart(double onset)
    {
        var flanks = AnnouncementIsolation.Measure(onset, AnnouncementTimeline());
        Assert.Equal(13.0, flanks!.Value.SpeechStartSeconds, 3);
    }

    /// <summary>Without a VAD pre-pass there is nothing to measure with, and that must read as
    /// "unknown" rather than "fails" - a run without VAD is exactly as well off as it was before
    /// this check existed.</summary>
    [Fact]
    public void Measure_IsUnavailableWithoutSpeechSegments()
        => Assert.Null(AnnouncementIsolation.Measure(13.0, []));

    [Fact]
    public void Measure_IsUnavailablePastTheLastSpeechSegment()
        => Assert.Null(AnnouncementIsolation.Measure(999, AnnouncementTimeline()));

    /// <summary>An announcement with nothing behind it in the whole file is not thereby suspect -
    /// an epilogue can be the last thing spoken.</summary>
    [Fact]
    public void Measure_TreatsTheEndOfTheFileAsAnUnboundedTrailingPause()
    {
        var flanks = AnnouncementIsolation.Measure(13.0, Speech((0, 10), (13.0, 13.7)));
        Assert.True(double.IsPositiveInfinity(flanks!.Value.LeadOutSeconds));
        Assert.True(AnnouncementIsolation.Satisfies(flanks.Value, IsolationRule.Both));
    }

    /// <summary>Chapter 20 of the same book, the tightest genuine trailing pause measured there at
    /// 0.99 s, against a threshold set well below it.</summary>
    [Fact]
    public void Satisfies_AcceptsTheTightestRealChapter()
    {
        var flanks = AnnouncementIsolation.Measure(13.0, Speech((0, 10), (13.0, 13.7), (14.69, 25)));
        Assert.True(AnnouncementIsolation.Satisfies(flanks!.Value, IsolationRule.Both));
    }

    /// <summary>
    /// The false epilogue this guard exists for: <c>/epilogo/</c> matching inside Italian
    /// "riepilogo" in "I dettagli vengono cancellati completamente dal riepilogo", mid-sentence at
    /// 13:35:16 of "Corsa nello spazio". 0.64 s of lead-in on a 3.59 s stretch of continuous
    /// speech - and being non-repeatable, that match replaced the book's real epilogue mark.
    /// </summary>
    [Fact]
    public void Satisfies_RejectsAMidSentenceMatch()
    {
        var flanks = AnnouncementIsolation.Measure(16.41, Speech((0, 15.77), (16.41, 20.0), (20.73, 30)));
        Assert.Equal(0.64, flanks!.Value.LeadInSeconds, 2);
        Assert.False(AnnouncementIsolation.Satisfies(flanks.Value, IsolationRule.LeadIn));
        Assert.False(AnnouncementIsolation.Satisfies(flanks.Value, IsolationRule.Both));
        Assert.True(AnnouncementIsolation.Satisfies(flanks.Value, IsolationRule.None));
    }

    /// <summary>
    /// Why the prologue and epilogue are asked for a leading pause only. Gruelfin's "Zeittafel"
    /// leaves 0.16 s behind it, genuine: a heading word is routinely run straight into the text
    /// that follows it, unlike a number spoken alone.
    /// </summary>
    [Fact]
    public void Satisfies_LeadInIgnoresAHeadingRunIntoItsText()
    {
        var flanks = AnnouncementIsolation.Measure(13.0, Speech((0, 10), (13.0, 13.7), (13.86, 30)));
        Assert.True(AnnouncementIsolation.Satisfies(flanks!.Value, IsolationRule.LeadIn));
        Assert.False(AnnouncementIsolation.Satisfies(flanks.Value, IsolationRule.Both));
    }

    /// <summary>
    /// The lead-in threshold sits between the tightest genuine announcement the corpus has (1.56 s,
    /// "The Forever War"'s epilogue) and the false positive it exists to reject (0.64 s, the
    /// "riepilogo" match), and low in that window rather than midway - a chapter dropped for want of
    /// a pause is lost outright, while the false positives on record are nowhere near the line.
    /// </summary>
    [Theory]
    [InlineData(1.56, true)]
    [InlineData(0.90, true)]
    [InlineData(0.84, false)]
    [InlineData(0.64, false)]
    public void Satisfies_LeadInSitsBetweenTheTightestRealMarkAndTheFalseOne(double leadIn, bool expected)
    {
        var flanks = AnnouncementIsolation.Measure(13.0, Speech((0, 13.0 - leadIn), (13.0, 13.7), (16, 30)));
        Assert.Equal(expected, AnnouncementIsolation.Satisfies(flanks!.Value, IsolationRule.LeadIn));
    }

    /// <summary>
    /// The check a numbered chapter is placed under: whatever its own wording asked for with
    /// <c>^</c>/<c>$</c>, and both flanks regardless once a gap hunt's wider reading is in play.
    /// A phrase-based announcement whose wording asks for nothing is checked not at all.
    /// </summary>
    [Fact]
    public void ForChapter_TakesTheWordingsGuardsAndTheWiderReadingsOwn()
    {
        var phrase = new PhraseMatching.PhraseMatch(3, 10, 11, 0.9);
        var heading = phrase with { Wording = Wording(IsolationRule.LeadIn) };
        var bare = phrase with { Wording = Wording(IsolationRule.Both, bare: true) };
        // A wording that asks for nothing is never checked, whatever the pass is doing.
        Assert.Equal(IsolationRule.None, AnnouncementIsolation.ForChapter(phrase, 10, false).Rule);
        Assert.Equal(IsolationRule.None, AnnouncementIsolation.ForChapter(phrase, 10, true).Rule);
        // A "^" carries into every pass, a gap hunt or not.
        Assert.Equal(IsolationRule.LeadIn, AnnouncementIsolation.ForChapter(heading, 10, false).Rule);
        Assert.Equal(IsolationRule.LeadIn, AnnouncementIsolation.ForChapter(heading, 10, true).Rule);
        // A number spoken alone is written "/^()$/", so it asks for both flanks by itself.
        Assert.Equal(IsolationRule.Both, AnnouncementIsolation.ForChapter(bare, 10, false).Rule);
        Assert.Equal(IsolationRule.Both, AnnouncementIsolation.ForChapter(bare, 10, true).Rule);
        // ... and a wide-reading pass adds them even to a bare wording that asked for neither.
        var unguarded = phrase with { Wording = Wording(IsolationRule.None, bare: true) };
        Assert.Equal(IsolationRule.None, AnnouncementIsolation.ForChapter(unguarded, 10, false).Rule);
        Assert.Equal(IsolationRule.Both, AnnouncementIsolation.ForChapter(unguarded, 10, true).Rule);
    }

    /// <summary>
    /// The second way a <c>^</c> is satisfied: the recognizer set the announcement off as a segment
    /// of its own. "I Shall Wear Midnight" chapter 9 is the case - the one mark of the sixteen-book
    /// corpus with under 0.85 s in front of it (0.64 s), and transcribed as
    /// "Chapter 9 The Duchess and the Cook" in a segment of its own, so it passes on that.
    /// </summary>
    [Fact]
    public void ForChapter_TakesASegmentStartForTheLeadIn()
    {
        var match = new PhraseMatching.PhraseMatch(
            9, 10, 11, 0.9, Wording: Wording(IsolationRule.Both));
        Assert.Equal(IsolationRule.Both, AnnouncementIsolation.ForChapter(match, 10, false).Rule);
        Assert.Equal(
            IsolationRule.LeadOut,
            AnnouncementIsolation.ForChapter(match with { OpensSegment = true }, 10, false).Rule);
        // A wording that asked for the lead-in alone is then not checked at all.
        var heading = match with { Wording = Wording(IsolationRule.LeadIn), OpensSegment = true };
        Assert.Equal(IsolationRule.None, AnnouncementIsolation.ForChapter(heading, 10, false).Rule);
    }

    /// <summary>
    /// A number spoken alone is excepted, and that exception is the whole calibration of that
    /// wording: its claim to being an announcement <em>is</em> the pause around it, and the mode
    /// exists because Whisper's segmentation cannot be trusted to say where one is.
    /// </summary>
    [Fact]
    public void ForChapter_DoesNotTakeASegmentStartForABareNumber()
    {
        var bare = new PhraseMatching.PhraseMatch(
            9, 10, 11, 0.9, Wording: Wording(IsolationRule.Both, bare: true), OpensSegment: true);
        Assert.Equal(IsolationRule.Both, AnnouncementIsolation.ForChapter(bare, 10, false).Rule);
        Assert.Equal(IsolationRule.Both, AnnouncementIsolation.ForChapter(bare, 10, true).Rule);
    }

    /// <summary>
    /// What a wide-reading match falls back on when neither model could confirm its announcement.
    /// One that opened its segment is a match the narrow reading would have taken anyway, so its
    /// segment start is an honest position to measure at; one found further in has no such position
    /// and is dropped instead of guessed at.
    /// </summary>
    [Fact]
    public void ForChapter_OnlyOffersAFallbackForANumberSpokenAlone()
    {
        var opening = new PhraseMatching.PhraseMatch(
            3, 10, 11, 0.9, Wording: Wording(IsolationRule.None, bare: true));
        var buried = opening with { SpokenAlone = false };
        Assert.Equal(10, AnnouncementIsolation.ForChapter(opening, 10, true).FallbackPosition);
        Assert.Null(AnnouncementIsolation.ForChapter(buried, 10, true).FallbackPosition);
    }

    [Fact]
    public void Describe_NamesTheMeasurementAndTheThreshold()
    {
        var flanks = new AnnouncementFlanks(0.64, 0.73, 16.41);
        Assert.Equal("0.64 s before, 0.73 s after; need 0.85/0.3",
            AnnouncementIsolation.Describe(flanks, IsolationRule.Both));
        Assert.Equal("0.64 s before, 0.73 s after; need 0.85 before",
            AnnouncementIsolation.Describe(flanks, IsolationRule.LeadIn));
    }

    /// <summary>A phrase matcher is the compiled phrase, unchanged - the implicit conversion exists
    /// so the dozens of call sites that had one keep working.</summary>
    [Fact]
    public void PhraseMatcher_IsThePhrase()
    {
        AnnouncementMatcher matcher = Compile("/kapitel/", "de");
        Assert.True(matcher.Matches("Kapitel 17"));
        Assert.False(matcher.Matches("Es war vorbei."));
        Assert.False(matcher.Matches(null));
    }

    /// <summary>
    /// The matcher that makes refinement possible at all under <c>--chapter-phrase none</c>, where
    /// the phrase regex is one that never matches anything. It accepts any number spoken alone
    /// rather than one particular reading, because a probe series writes the same announcement as
    /// digits, as words and as a Roman numeral at different moments - and because leaving
    /// <em>which</em> number to <see cref="RefinedNumberVote"/> is what keeps the number refinable.
    /// </summary>
    [Theory]
    [InlineData("45.", true)]
    [InlineData("Quarantacinque.", true)]
    [InlineData("XLV.", true)]
    [InlineData("45. Zhang Mingoua lanciò un'occhiata.", true)]
    [InlineData("ha trovato un'astronave aliena. 3. Il presidente.", true)]
    [InlineData("Il presidente Amanda Santeros picchiettava la sua penna.", false)]
    [InlineData("1000 km sopra le macchinazioni di Washington.", false)]
    [InlineData("", false)]
    public void BareNumberMatcher_AcceptsAnyNumberSpokenAlone(string text, bool expected)
        => Assert.Equal(expected, Matcher(strict: true).Matches(text));

    /// <summary>
    /// The pairing that keeps an over-eager matcher away from an unguarded mark. Italian "un",
    /// "una" and "uno" all parse as 1, so the permissive reading answers "found" on ordinary prose -
    /// harmless where <see cref="AnnouncementIsolation"/> vets the onset afterwards, and a way to
    /// drag a Probe mark onto a word where nothing does.
    /// </summary>
    [Fact]
    public void BareNumberMatcher_OnlyGoesPermissiveForAGuardedMatch()
    {
        const string prose = "Una teoria. Un lavoro di merda.";
        Assert.False(Matcher(strict: true).Matches(prose));
        Assert.True(Matcher(strict: false).Matches(prose));

        // …and the case the permissive reading exists for: no period after the heading number.
        const string noPeriod = "45 Zangmingoa lanciò un'occhiata alla lettura della data.";
        Assert.False(Matcher(strict: true).Matches(noPeriod));
        Assert.True(Matcher(strict: false).Matches(noPeriod));
    }

    /// <summary>
    /// The four marks that landed seconds late on "Corsa nello spazio" (build 249, 2026-08-06),
    /// each one the onset walk climbing off the announcement onto the first number in the
    /// chapter's own opening sentence. Nothing here is contrived: two of the four are a date line,
    /// one is a distance and one is the word "tre". Each case is the probe text the run's debug log
    /// records at the position the mark was wrongly corrected to, with the sequence bounds that
    /// pass was actually holding.
    /// </summary>
    [Fact]
    public void BareNumberMatcher_IgnoresANumberTheSequenceCannotHold()
    {
        // Chapter 1 opens "1. 9 febbraio 2066." - the walk settled on the year, 3.9 s late.
        Reject("9 Feb. 2066.", strict: true, new NumberBounds(0), detected: 1);
        Reject("2066. Da 10 km di distanza.", strict: true, new NumberBounds(0), detected: 1);
        // Chapter 20 opens "20. 27 luglio 2067." - both the year and its fragments matched.
        Reject("2067.", strict: false, new NumberBounds(19, 22), detected: 20);
        Reject("27 luglio 2067.", strict: false, new NumberBounds(19, 22), detected: 20);
        // Chapter 4's first words are "mille chilometri", which Whisper writes as "1000 km".
        Reject("1000 km sopra le macchinazioni di Washington.",
            strict: false, new NumberBounds(3, 5), detected: 4);
        // Chapter 11's are "Tre di loro", and Italian "tre" parses as 3.
        Reject("Tre di loro non erano mai stati su.",
            strict: false, new NumberBounds(10, 12), detected: 11);
    }

    /// <summary>
    /// The other half, and the reason the filter is the sequence rather than the number itself:
    /// Whisper's notation for one announcement fluctuates between probes, so every reading of the
    /// right number has to survive. All four texts are from the same run's logs.
    /// </summary>
    [Fact]
    public void BareNumberMatcher_KeepsEveryNotationOfTheAnnouncement()
    {
        Accept("45.", strict: true, new NumberBounds(44, 46), detected: 45);
        Accept("Quarantacinque.", strict: true, new NumberBounds(44, 46), detected: 45);
        Accept("XLV.", strict: true, new NumberBounds(44, 46), detected: 45);
        // Glued to the chapter's first sentence, which is why the unit is a sentence.
        Accept("45. Zhang Mingoua lanciò un'occhiata.", strict: true, new NumberBounds(44, 46), 45);
        // And the announcement of chapter 20 itself, in the same breath as the date that fooled it.
        Accept("20. 27 luglio 2067", strict: false, new NumberBounds(19, 22), detected: 20);
    }

    /// <summary>
    /// A number the bounds reject still refines when it is the one the detecting window read -
    /// <see cref="NumberCheck.AdmitsAsAnnouncement"/>'s second half. Without it a mark whose number
    /// <see cref="SuspectNumberMender"/> could not mend would fail every probe and keep its
    /// unrefined default-mode position, which is the failure bare-number mode started out with.
    /// </summary>
    [Fact]
    public void BareNumberMatcher_StillTakesTheNumberTheWindowRead()
    {
        var check = new NumberCheck(90, Profile(bareNumbers: true), new NumberBounds(18, 20));
        Assert.False(check.Bounds.Admits(90));
        Assert.True(check.AdmitsAsAnnouncement(90));
        Assert.True(check.AdmitsAsAnnouncement(19));
        Assert.False(check.AdmitsAsAnnouncement(2066));
    }

    /// <summary>
    /// A sentence whose number the bounds reject must not end the scan: the announcement can sit in
    /// a later one, which is exactly the shape a refinement probe with a lead-in produces.
    /// </summary>
    [Fact]
    public void BareNumberMatcher_KeepsScanningPastARejectedSentence()
        => Accept("Tre di loro non erano mai stati su. 11.",
            strict: false, new NumberBounds(10, 12), detected: 11);

    /// <summary>Asserts the matcher takes <paramref name="text"/> under those bounds.</summary>
    /// <param name="text">The probe transcript text.</param>
    /// <param name="strict">Whether this is a Probe match rather than a gap hunt's.</param>
    /// <param name="bounds">The sequence bounds the pass was holding.</param>
    /// <param name="detected">The number the detecting window read.</param>
    private static void Accept(string text, bool strict, NumberBounds bounds, int detected)
        => Assert.True(Matcher(strict, bounds, detected).Matches(text), text);

    /// <summary>Asserts the matcher rejects <paramref name="text"/> under those bounds, and that it
    /// would have taken it unbounded - so the test fails if the text stops being a number at
    /// all.</summary>
    /// <param name="text">The probe transcript text.</param>
    /// <param name="strict">Whether this is a Probe match rather than a gap hunt's.</param>
    /// <param name="bounds">The sequence bounds the pass was holding.</param>
    /// <param name="detected">The number the detecting window read.</param>
    private static void Reject(string text, bool strict, NumberBounds bounds, int detected)
    {
        Assert.True(Matcher(strict).Matches(text), $"unbounded matcher should still take: {text}");
        Assert.False(Matcher(strict, bounds, detected).Matches(text), text);
    }

    /// <summary>A bare-number matcher for the reading a Probe match respectively a gap hunt's
    /// match would have been found under, holding any number at all - which is what the readings
    /// alone are worth testing against.</summary>
    /// <param name="strict">Whether this is a Probe match rather than a gap hunt's.</param>
    private static AnnouncementMatcher Matcher(bool strict)
        => AnnouncementMatcher.ForPattern(
            Profile(bareNumbers: true).ChapterPattern, "it", Reading(strict), _ => true);

    /// <summary>The same matcher as the refinement actually builds it: held to what the chapter
    /// sequence can hold at this mark.</summary>
    /// <param name="strict">Whether this is a Probe match rather than a gap hunt's.</param>
    /// <param name="bounds">The sequence bounds the pass was holding.</param>
    /// <param name="detected">The number the detecting window read.</param>
    private static AnnouncementMatcher Matcher(bool strict, NumberBounds bounds, int detected)
        => AnnouncementMatcher.ForPattern(
            Profile(bareNumbers: true).ChapterPattern, "it", Reading(strict),
            new NumberCheck(detected, Profile(bareNumbers: true), bounds).AdmitsAsAnnouncement);

    /// <summary>Which reading a Probe match respectively a gap hunt's match is refined under.</summary>
    /// <param name="strict">Whether this is a Probe match rather than a gap hunt's.</param>
    private static NumberWordParser.BareNumberReading Reading(bool strict)
        => strict
            ? NumberWordParser.BareNumberReading.SpokenAloneAtSegmentStart
            : NumberWordParser.BareNumberReading.LeadingASentence;

    /// <summary>A language profile for the two flavours, built the way CliOptions builds one.</summary>
    private static LanguageProfile Profile(bool bareNumbers)
    {
        var phrase = bareNumbers ? "none" : "/capitolo/";
        return new LanguageProfile(
            "it", phrase, Compile(phrase, "it"), "Capitolo", "Parte", "Introduzione", []);
    }

    /// <summary>Compiles one chapter phrase exactly as the command line layer does.</summary>
    /// <param name="phrase">The phrase as it would be written after --chapter-phrase.</param>
    /// <param name="language">Two-letter language code.</param>
    private static PhrasePattern Compile(string phrase, string language)
        => PhraseCompiler.Compile([phrase], language, PhraseKind.Chapter, "chapter phrase");

    /// <summary>
    /// "De vandrande djäknarne" chapter 6, the geometry the mark-inside-speech guard was built for
    /// (build 339, 2026-08-17). Analyze's own segments around 1:37: the LibriVox boilerplate, then the
    /// reader's credit "Inläsning av Lars Rolander" running 1:37:17.66-1:37:20.00, a 0.55 s pause,
    /// then "Sjätte kapitlet" at 1:37:20.41. Absolute times kept so the figures match the debug log.
    /// </summary>
    private static List<SpeechSegment> ReaderCreditTimeline()
        => Speech((5831.64, 5836.99), (5837.66, 5840.00), (5840.41, 5841.56), (5842.14, 5843.93));

    [Fact]
    public void AMarkInThePauseAfterTheReaderCredit_IsNotInsideSpeech()
    {
        // 1:37:20.06 - the default-mode mark, sitting in the 0.55 s pause. The position the guard
        // restores, and the one the user confirmed by ear.
        Assert.Null(AnnouncementIsolation.DepthInsideSpeech(5840.06, ReaderCreditTimeline()));
    }

    [Fact]
    public void TheRefinedMarkThatLandedInTheReaderCredit_MeasuresItsRealDepth()
    {
        // 1:37:18.66 - what the survival walk actually produced, 1.00 s into the credit. The only
        // two non-zero depths in the whole build-339 corpus were this and chapter 11's 1.42 s.
        var depth = AnnouncementIsolation.DepthInsideSpeech(5838.66, ReaderCreditTimeline());
        Assert.NotNull(depth);
        Assert.Equal(1.00, depth!.Value, 2);
        Assert.True(depth.Value > DetectionTuning.MarkInsideSpeechSeconds);
    }

    [Fact]
    public void AMarkAtASegmentBoundary_ReportsNoSpeechElapsed()
    {
        // Half-open [Start, End): a mark exactly on a speech start has zero speech behind it, which
        // is the honest answer and safely under the threshold either way, while a mark on a segment's
        // end is past that segment entirely. Both edges occur constantly at VAD's 0.1 s resolution,
        // so neither may throw the verdict off.
        Assert.Equal(0.0, AnnouncementIsolation.DepthInsideSpeech(5840.41, ReaderCreditTimeline())!.Value, 4);
        Assert.Null(AnnouncementIsolation.DepthInsideSpeech(5840.00, ReaderCreditTimeline()));
        Assert.Equal(0.01, AnnouncementIsolation.DepthInsideSpeech(5840.42, ReaderCreditTimeline())!.Value, 2);
    }

    [Fact]
    public void WithoutAVadPrePass_DepthIsUnmeasurable()
    {
        // Same contract as Measure: no segments means no verdict, so a run without VAD is exactly as
        // well off as it was before the guard existed rather than having every mark second-guessed.
        Assert.Null(AnnouncementIsolation.DepthInsideSpeech(5838.66, Speech()));
    }

    [Fact]
    public void AMarkBeforeAJingle_IsNotInsideSpeech()
    {
        // Why 441 of 443 corpus marks measure exactly zero: music reads as non-speech, so a mark
        // anchored to a jingle's leading edge is outside every segment even though it is far from
        // silent - which is also why dBFS cannot stand in for this measurement.
        var timeline = Speech((0, 10), (30.0, 45.0));
        Assert.Null(AnnouncementIsolation.DepthInsideSpeech(12.0, timeline));
        Assert.Null(AnnouncementIsolation.DepthInsideSpeech(29.9, timeline));
    }
}
