// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Cli;
using ABChapterize.Detection;
using ABChapterize.Transcription;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="PhraseMatching.PrecededByPunctuation"/>, the third way a <c>^</c> can be
/// satisfied.
/// </summary>
/// <remarks>
/// The strings are the shapes measured across the Perry Rhodan Silber-Edition library
/// (159 books, 2026-08-28), where 11 books announce a chapter as "&lt;Schauplatz&gt;. Kapitel N"
/// and three lost a chapter because neither of the first two routes could see it. The histogram
/// that fixes what counts and what must not is in
/// <c>notes\Detection\PhraseMatching.xml</c>: mid-segment announcements follow a comma 152 times,
/// a full stop 31, a hyphen 6 - and a letter or digit 94 times, which is narration and the group
/// this predicate exists to keep out.
/// </remarks>
public sealed class PhrasePunctuationTests
{
    /// <summary>The three real losses, verbatim from the runs that dropped them.</summary>
    [Theory]
    [InlineData("Milchstraße, Kapitel 14")]              // 113 - Der Loower und das Auge
    [InlineData("Milchstraße, Kapitel 23")]              // 133 - Die Ewigen Diener
    [InlineData("Die Sol, Kapitel 25")]                  // 96 - Die Gravo-Katastrophe
    [InlineData("Aboltsystem. Kapitel 16")]              // 106 - Laire, recovered only by a re-read
    public void PrecededByPunctuation_AcceptsTheBannerShape(string text)
        => Assert.True(PhraseMatching.PrecededByPunctuation(text, AnnouncementAt(text)));

    /// <summary>
    /// Narration must stay out, and it is the whitespace requirement rather than the character
    /// class that keeps most of it out. "Der erste Kapitel 5" is the corpus's own example of the
    /// 94-strong group following a letter.
    /// </summary>
    [Theory]
    [InlineData("Der erste Kapitel 5")]                  // a word in front - narration
    [InlineData("Zwischenkapitel 5")]                    // no boundary at all, mid-word
    [InlineData("Er las 3.Kapitel 5")]                   // punctuation but no space: still glued on
    public void PrecededByPunctuation_RejectsNarration(string text)
        => Assert.False(PhraseMatching.PrecededByPunctuation(text, AnnouncementAt(text)));

    /// <summary>Where the chapter word starts, found rather than counted out by hand - an index
    /// written into the fixture is one more thing that can be wrong about the fixture.</summary>
    /// <param name="text">The transcript text under test.</param>
    private static int AnnouncementAt(string text)
    {
        var i = text.IndexOf("apitel", StringComparison.OrdinalIgnoreCase);
        Assert.True(i > 0, $"fixture has no chapter word: {text}");
        return i - 1;
    }

    /// <summary>
    /// Nothing in front is not punctuation in front. A match at the very start of the window text
    /// opens its segment and is answered by that route instead.
    /// </summary>
    [Fact]
    public void PrecededByPunctuation_RejectsTheStartOfTheText()
    {
        Assert.False(PhraseMatching.PrecededByPunctuation("Kapitel 14 und so weiter", 0));
        Assert.False(PhraseMatching.PrecededByPunctuation("   Kapitel 14", 3));
    }

    /// <summary>
    /// One predicate rather than a hand-kept list, so the marks nobody thought to enumerate work
    /// too: sentence enders, clause separators, dashes, and a closing quote on its own account -
    /// <c>?"</c> ends a sentence whichever of the two is tested.
    /// </summary>
    [Theory]
    [InlineData(".")]
    [InlineData(",")]
    [InlineData(";")]
    [InlineData(":")]
    [InlineData("!")]
    [InlineData("?")]
    [InlineData("-")]
    [InlineData("—")]                               // em dash
    [InlineData("…")]                               // ellipsis
    [InlineData("»")]                               // closing German quote
    [InlineData("\"")]
    public void PrecededByPunctuation_AcceptsAnyPunctuationMark(string mark)
    {
        var text = $"Wort{mark} Kapitel 14";
        Assert.True(PhraseMatching.PrecededByPunctuation(text, text.IndexOf("Kapitel", StringComparison.Ordinal)));
    }

    /// <summary>An index past the end must not throw; nothing in the pipeline produces one, but the
    /// predicate is handed a raw match offset and clamping is cheaper than trusting.</summary>
    [Fact]
    public void PrecededByPunctuation_ToleratesAnIndexPastTheEnd()
        => Assert.True(PhraseMatching.PrecededByPunctuation("Wort, ", 99));

    /// <summary>
    /// The wiring, which is the seam that can rot silently: the finder has to set the flag on the
    /// match it hands the guard, or the predicate above is exercised by nothing. The German segment
    /// is the shape "Der Loower und das Auge" lost chapter 14 to - banner and number in one segment,
    /// so <c>OpensSegment</c> is false and only punctuation is left to answer the <c>^</c>.
    /// </summary>
    [Fact]
    public void FindPhraseMatches_FlagsAnAnnouncementBehindPunctuation()
    {
        var banner = Match(" Milchstraße, Kapitel 14");
        Assert.False(banner.OpensSegment);
        Assert.True(banner.FollowsPunctuation);

        var plain = Match(" Kapitel 14. In diesem verdammten Leben");
        Assert.True(plain.OpensSegment);

        // Narration: the chapter word follows a word, so neither route may fire.
        var narration = Match(" Er las das dritte Kapitel 14 mal");
        Assert.False(narration.OpensSegment);
        Assert.False(narration.FollowsPunctuation);
    }

    /// <summary>The first chapter reading of a one-segment window, through the real finder.</summary>
    /// <param name="text">The segment text, with the leading space the recognizer always writes.</param>
    private static PhraseMatching.PhraseMatch Match(string text)
    {
        var profile = CliOptions.Parse(["--lang", "de", "."])!.ResolveProfile("de");
        var segments = new List<TranscriptSegment> { new(0, 3, text, 0.9) };
        return Assert.Single(PhraseMatching.FindPhraseMatches(segments, profile));
    }
}
