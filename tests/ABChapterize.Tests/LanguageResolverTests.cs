// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using ABChapterize.Detection;
using ABChapterize.Vad;
using Xunit;

namespace ABChapterize.Tests;

/// <summary>
/// Direct tests for <see cref="LanguageResolver"/>'s sampling half - where in a book it decides to
/// listen, given VAD speech runs, existing chapter marks, or nothing but a duration. The probe
/// loop and the vote that consume these positions are covered end to end by
/// <see cref="ChapterDetectorTests"/>'s <c>AutoLanguage_*</c> scenarios, which need a scripted
/// recognizer; the position rules need nothing but arithmetic, and are easier to pin down here.
/// </summary>
public class LanguageResolverTests
{
    /// <summary>A 10-hour book, so the anchor fractions land on round, readable numbers.</summary>
    private const double Duration = 36000;

    /// <summary>
    /// Continuous narration for the whole book, as the VAD actually reports it: sentence-sized
    /// segments a second apart, not the long unbroken runs it is tempting to picture. A window
    /// opening on any of these is 30/31 speech, so every one of them qualifies.
    /// </summary>
    private static List<SpeechSegment> RegularSpeech()
        => [.. Enumerable.Range(0, (int)(Duration / 5)).Select(i => new SpeechSegment(i * 5, i * 5 + 4))];

    [Fact]
    public void SpeechPositions_NeverStartsAtTheFileOpening()
    {
        // The bug this class was written for: an audiobook's first seconds are label music far too
        // often for them to be the sample, and they used to be the *only* sample.
        var positions = LanguageResolver.SpeechPositions(RegularSpeech(), Duration).ToList();

        Assert.NotEmpty(positions);
        Assert.All(positions, p => Assert.True(p > 600, $"sampled at {p}, too near the start"));
    }

    [Fact]
    public void SpeechPositions_TakesFiveSpreadSamples_StartingAFifthIn()
    {
        var positions = LanguageResolver.SpeechPositions(RegularSpeech(), Duration).ToList();

        // A speech run starts exactly on every anchor here, so the anchors come through untouched -
        // which is what makes the ordering visible: best guess first, then outward over the middle.
        Assert.Equal([7200, 16200, 25200, 3600, 30600], positions);
    }

    [Fact]
    public void SpeechPositions_SnapsToTheNearestQualifyingWindow_NotTheAnchorItself()
    {
        // One speech-dense stretch only, nowhere near any anchor. Every anchor must still resolve
        // onto it rather than sampling the silence it was aimed at.
        var speech = Enumerable.Range(0, 10).Select(i => new SpeechSegment(9000 + i * 5, 9004 + i * 5));

        var positions = LanguageResolver.SpeechPositions([.. speech], Duration).ToList();

        Assert.NotEmpty(positions);
        Assert.All(positions, p => Assert.InRange(p, 9000, 9050));
    }

    [Fact]
    public void SpeechPositions_SkipsIsolatedSpeech_InAnOtherwiseQuietWindow()
    {
        // A lone shout, a sung line or an announcement over a jingle: real speech, but the window
        // around it is not narration and the detector reads it badly. Sentence-sized segments
        // further away are the better sample despite being individually shorter.
        var speech = new List<SpeechSegment> { new(7200, 7206) }; // 6 s alone, right on anchor one
        speech.AddRange(Enumerable.Range(0, 10).Select(i => new SpeechSegment(12000 + i * 5, 12004 + i * 5)));

        var positions = LanguageResolver.SpeechPositions(speech, Duration).ToList();

        Assert.DoesNotContain(7200, positions);
        Assert.All(positions, p => Assert.InRange(p, 12000, 12050));
    }

    [Fact]
    public void SpeechPositions_FallsBackToTheDensestWindows_WhenNoneReachTheMinimum()
    {
        // A heavily scored book, or a conservative VAD: nothing is half speech, but the VAD still
        // knows where the speech is, which beats guessing at fractions of the duration.
        var speech = new List<SpeechSegment>
        {
            new(1000, 1002),
            new(20000, 20012), new(20020, 20022), // the densest 30 s there is
            new(30000, 30003),
        };

        var positions = LanguageResolver.SpeechPositions(speech, Duration).ToList();

        Assert.Contains(20000, positions);
        Assert.All(positions, p => Assert.Contains(p, new[] { 1000.0, 20000.0, 20020.0, 30000.0 }));
    }

    [Fact]
    public void SpeechPositions_WithNoSpeechAtAll_FallsBackToTheBlindAnchors()
    {
        Assert.Equal(
            LanguageResolver.DurationPositions(Duration),
            LanguageResolver.SpeechPositions([], Duration));
    }

    [Fact]
    public void ExistingMarkPositions_LandInsideTheChapter_PastItsAnnouncement()
    {
        // A mark is a known chapter start, so the announcement and whatever jingle wraps it sit
        // right at it - which is exactly what a language sample must not be taken from.
        var marks = Enumerable.Range(0, 20)
            .Select(i => new Chapter(i * 1800, $"Chapter {i + 1}"))
            .ToList();

        var positions = LanguageResolver.ExistingMarkPositions(marks, Duration).ToList();

        Assert.Equal(5, positions.Count);
        Assert.All(positions, p => Assert.Equal(20, p % 1800));
    }

    [Fact]
    public void ExistingMarkPositions_DropsAnythingPastTheEndOfTheFile()
    {
        // A mark in the last few seconds offsets to a position with no audio behind it.
        var positions = LanguageResolver.ExistingMarkPositions([new Chapter(Duration - 5, "Last")], Duration);

        Assert.Empty(positions);
    }

    [Fact]
    public void ExistingMarkPositions_WithNone_FallBackToTheBlindAnchors()
    {
        Assert.Equal(
            LanguageResolver.DurationPositions(Duration),
            LanguageResolver.ExistingMarkPositions([], Duration));
    }

    [Fact]
    public void DurationPositions_AreFiveDistinctPlaces_AwayFromBothEnds()
    {
        var positions = LanguageResolver.DurationPositions(Duration).ToList();

        Assert.Equal(5, positions.Count);
        Assert.Equal(positions.Count, positions.Distinct().Count());
        // Both ends of an audiobook are where the non-narration material clusters: label music and
        // copyright cards at the front, credits and the retailer outro at the back.
        Assert.All(positions, p => Assert.InRange(p, 0.05 * Duration, 0.9 * Duration));
    }

    [Fact]
    public void DurationPositions_OnAVeryShortFile_StayWithinIt()
    {
        Assert.All(LanguageResolver.DurationPositions(12), p => Assert.InRange(p, 0, 12));
    }
}
