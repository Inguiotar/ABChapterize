// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Text.RegularExpressions;
using ABChapterize.Detection;
using ABChapterize.Language;
using ABChapterize.Vad;
using Xunit;

namespace ABChapterize.Tests;

/// <summary>
/// The pause-geometry guard that replaced trusting Whisper's segmentation
/// (<see cref="AnnouncementIsolation"/>), plus the matcher the mark refinement asks
/// (<see cref="AnnouncementMatcher"/>).
/// <para>
/// The measurements in these tests are not invented. Every flank figure is taken from the Pass 1
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
    /// 0.99 s, against a threshold of 0.5.</summary>
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
    /// Why the prologue and epilogue are asked for a leading pause only. Gruelfin's "Zeittafel" has
    /// 0.16 s behind it and "I Shall Wear Midnight"'s epilogue 0.44 s, both genuine: a heading word
    /// is routinely run straight into the text that follows it, unlike a number spoken alone.
    /// </summary>
    [Theory]
    [InlineData(0.16)]
    [InlineData(0.44)]
    public void Satisfies_LeadInIgnoresAHeadingRunIntoItsText(double trailing)
    {
        var flanks = AnnouncementIsolation.Measure(13.0, Speech((0, 10), (13.0, 13.7), (13.7 + trailing, 30)));
        Assert.True(AnnouncementIsolation.Satisfies(flanks!.Value, IsolationRule.LeadIn));
        Assert.False(AnnouncementIsolation.Satisfies(flanks.Value, IsolationRule.Both));
    }

    /// <summary>The check a numbered chapter is placed under: nothing at all unless the book
    /// announces by bare number <em>and</em> this pass took the wider reading.</summary>
    [Fact]
    public void ForChapter_AsksOnlyWhereTheWiderReadingWasUsed()
    {
        var match = new PhraseMatching.PhraseMatch(3, 10, 11, 0.9);
        IsolationRule Rule(bool bareNumbers, bool wideReading)
            => AnnouncementIsolation.ForChapter(Profile(bareNumbers), match, 10, wideReading).Rule;

        Assert.Equal(IsolationRule.None, Rule(bareNumbers: false, wideReading: false));
        Assert.Equal(IsolationRule.None, Rule(bareNumbers: false, wideReading: true));
        // Pass 2's forward scan: deliberately left cheap, the segment opening standing in for it.
        Assert.Equal(IsolationRule.None, Rule(bareNumbers: true, wideReading: false));
        Assert.Equal(IsolationRule.Both, Rule(bareNumbers: true, wideReading: true));
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
        var profile = Profile(bareNumbers: true);
        var opening = new PhraseMatching.PhraseMatch(3, 10, 11, 0.9);
        var buried = opening with { SpokenAlone = false };
        Assert.Equal(10, AnnouncementIsolation.ForChapter(profile, opening, 10, true).FallbackPosition);
        Assert.Null(AnnouncementIsolation.ForChapter(profile, buried, 10, true).FallbackPosition);
    }

    [Fact]
    public void Describe_NamesTheMeasurementAndTheThreshold()
    {
        var flanks = new AnnouncementFlanks(0.64, 0.73, 16.41);
        Assert.Equal("0.64 s before, 0.73 s after; need 1.0/0.5",
            AnnouncementIsolation.Describe(flanks, IsolationRule.Both));
        Assert.Equal("0.64 s before, 0.73 s after; need 1.0 before",
            AnnouncementIsolation.Describe(flanks, IsolationRule.LeadIn));
    }

    /// <summary>A phrase matcher is the regex, unchanged - the implicit conversion exists so the
    /// dozens of call sites that had one keep working.</summary>
    [Fact]
    public void PhraseMatcher_IsTheRegex()
    {
        AnnouncementMatcher matcher = new Regex("kapitel", RegexOptions.IgnoreCase);
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
    /// drag a Pass 2 mark onto a word where nothing does.
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

    /// <summary>A bare-number matcher for the reading a Pass 2 match respectively a gap hunt's
    /// match would have been found under.</summary>
    private static AnnouncementMatcher Matcher(bool strict)
        => AnnouncementMatcher.ForBareNumbers("it", strict
            ? NumberWordParser.BareNumberReading.SpokenAloneAtSegmentStart
            : NumberWordParser.BareNumberReading.LeadingASentence);

    /// <summary>A language profile for the two flavours, built the way CliOptions builds one.</summary>
    private static LanguageProfile Profile(bool bareNumbers)
        => new("it", bareNumbers ? "none" : "/capitolo/",
            new Regex(bareNumbers ? "(?!)" : "capitolo", RegexOptions.IgnoreCase),
            PhraseHasNumberGroup: false, "Capitolo", "Introduzione", [], bareNumbers);
}
