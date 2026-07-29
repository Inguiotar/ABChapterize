// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Cli;
using ABChapterize.Detection;
using ABChapterize.Language;
using ABChapterize.Transcription;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="PhraseMatching.FindUnnumberedAnnouncements"/> - the "heard the phrase,
/// could not read a number" signal. It exists because of the 2026-07-30 "I Shall Wear Midnight"
/// case, where a chapter was lost silently: the phrase matched every time and the number, written
/// as a Roman numeral, was unreadable, so the match was discarded without a trace.
/// </summary>
/// <remarks>
/// The unreadable numbers here are non-canonical Roman numerals ("XIIII"), which no notation the
/// tool understands can read and which a recognizer plausibly produces. Using a form that is
/// unreadable <em>by construction</em> keeps these tests failing on purpose no matter how far
/// number parsing is extended later.
/// </remarks>
public sealed class UnnumberedAnnouncementTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public UnnumberedAnnouncementTests()
    {
        _dir = Directory.CreateTempSubdirectory("abc-unnumbered-").FullName;
        _file = Path.Combine(_dir, "book.mp3");
        File.WriteAllBytes(_file, new byte[16]);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private LanguageProfile Profile(string code) => CliOptions.Parse([_file])!.ResolveProfile(code);

    private static List<TranscriptSegment> Segments(params (double Start, string Text)[] parts)
        => [.. parts.Select(p => new TranscriptSegment(p.Start, p.Start + 3, p.Text, 1.0))];

    private List<PhraseMatching.UnnumberedAnnouncement> Found(
        List<TranscriptSegment> segments, string code = "en")
        => [.. PhraseMatching.FindUnnumberedAnnouncements(segments, Profile(code))];

    /// <summary>The shape of the case that motivated the feature: phrase present, number not
    /// readable, previously discarded without a trace.</summary>
    [Fact]
    public void UnreadableNumber_IsReported()
    {
        var found = Found(Segments((12.5, " CHAPTER XIIII. THE SHAKING OF THE SHEETS")));

        var one = Assert.Single(found);
        Assert.Equal(12.5, one.PhraseStartSeconds);
        Assert.Contains("SHAKING", one.Text);
    }

    /// <summary>A readable announcement is not one with an unreadable number, whichever of the three
    /// notations it uses - otherwise the caller would log a line per chapter.</summary>
    [Theory]
    [InlineData(" Chapter 13. The Shaking of the Sheets")]
    [InlineData(" Chapter Thirteen. The Shaking of the Sheets")]
    [InlineData(" CHAPTER XIII. THE SHAKING OF THE SHEETS")]
    public void ReadableNumber_IsNotReported(string text)
        => Assert.Empty(Found(Segments((12.5, text))));

    /// <summary>Reports the timestamp of the segment the phrase sits in, not the window start, so a
    /// log line points at the announcement itself.</summary>
    [Fact]
    public void Timestamp_ComesFromThePhrasesOwnSegment()
    {
        var found = Found(Segments(
            (0.0, " and so the year turned."),
            (7.25, " Chapter thirteen. All was well."),
            (30.0, " CHAPTER XIIII. THE SHAKING OF THE SHEETS")));

        var one = Assert.Single(found);
        Assert.Equal(30.0, one.PhraseStartSeconds);
    }

    /// <summary>An in-text mention has no number either, so it lands here too. Keeping it is
    /// deliberate: the caller suppresses these by reporting only when a window yielded no mark at
    /// all, which is a cheaper rule than telling a heading from a sentence here.</summary>
    [Fact]
    public void InTextMention_IsReportedToo()
        => Assert.Single(Found(Segments((5.0, " The next chapter was harder than the last."))));

    /// <summary>Every phrase in the window is reported, not just the first - a retry window can
    /// straddle two announcements, and hiding the second would hide the one that mattered.</summary>
    [Fact]
    public void EveryUnreadablePhraseInTheWindow_IsReported()
    {
        var found = Found(Segments(
            (5.0, " CHAPTER XIIII. THE SHAKING OF THE SHEETS"),
            (40.0, " CHAPTER XVIIII. THE DUCHESS")));

        Assert.Equal(2, found.Count);
        Assert.Equal([5.0, 40.0], found.Select(f => f.PhraseStartSeconds));
    }

    /// <summary>The snippet is bounded so a log line cannot wrap a terminal when the phrase sits in
    /// the middle of a long sentence.</summary>
    [Fact]
    public void LongSegment_IsTruncated()
    {
        var found = Found(Segments((5.0, " chapter " + new string('x', 200))));

        var one = Assert.Single(found);
        Assert.True(one.Text.Length <= 61, $"snippet was {one.Text.Length} chars: {one.Text}");
        Assert.EndsWith("…", one.Text);
    }

    /// <summary>Works off the resolved profile, so a non-English run reports its own phrase.</summary>
    [Fact]
    public void OtherLanguages_UseTheirOwnPhrase()
    {
        Assert.Single(Found(Segments((5.0, " KAPITEL XIIII. Das Rütteln der Laken")), "de"));
        Assert.Empty(Found(Segments((5.0, " Kapitel dreizehn.")), "de"));
    }

    [Fact]
    public void EmptyTranscript_ReportsNothing()
        => Assert.Empty(Found([]));
}
