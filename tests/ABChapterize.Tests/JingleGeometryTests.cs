// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using ABChapterize.Detection;
using ABChapterize.Transcription;
using ABChapterize.Vad;
using Xunit;

namespace ABChapterize.Tests;

/// <summary>
/// Direct, non-integration tests for <see cref="JingleGeometry.ComputeMarkBeforeJingle"/> and its
/// <see cref="JingleGeometry.RetreatPastNonSpeech"/> backward-walk helper - the --mark-before-jingle
/// placement algorithm's five steps in isolation, without the full detection pipeline
/// <see cref="ChapterDetectorTests"/>'s scripted-jingle scenarios exercise them through.
/// </summary>
public class JingleGeometryTests
{
    /// <summary>The "no transcript to corroborate against" window every scenario below that is
    /// about VAD/silencedetect geometry alone passes: with no segments the corroboration check
    /// short-circuits, so the span is immaterial.</summary>
    private static readonly TranscriptWindow NoTranscript = new([], 0, 0);

    /// <summary>The mark lead every scenario about the <em>walk</em> passes, so the position each
    /// asserts on is the one the walk itself reached rather than that position minus a lead. Step
    /// 5's back-off has its own tests below.</summary>
    private const double NoLead = 0;

    // Step 2 (containment): real speech already covers the original mark - an ordinary
    // in-narration pause with no jingle at all - so it is returned unchanged.
    [Fact]
    public void ComputeMarkBeforeJingle_RealSpeechCoversTheMark_ReturnsItUnchanged()
    {
        var result = JingleGeometry.ComputeMarkBeforeJingle(
            100, [], [new(0, 150)], NoTranscript, NoLead);

        Assert.Equal(100, result);
    }

    // Step 1 backs out of the containing silence to its own start, then step 2's adjacency
    // check (not containment - the silence's start sits just past where real speech ends)
    // recognises real narration ending essentially there and returns the original mark.
    [Fact]
    public void ComputeMarkBeforeJingle_MarkInsideASilence_StepsOutToItsStart_ThenFindsAdjacentSpeech()
    {
        var result = JingleGeometry.ComputeMarkBeforeJingle(
            105, [new(100.2, 110)], [new(0, 100), new(112, 200)], NoTranscript, NoLead);

        Assert.Equal(105, result);
    }

    // Step 1 backs out of the containing silence to its own start; with no real speech at or
    // near that point and no other, already-passed silence behind it either, steps 3-4 keep
    // retreating through the jingle (the gap between the silence and the last real narration,
    // both of which VAD sees as one continuous non-speech stretch) to the true leading edge.
    [Fact]
    public void ComputeMarkBeforeJingle_MarkInsideASilence_WithNoSpeechAtItsStart_RetreatsToTheJingleStart()
    {
        var result = JingleGeometry.ComputeMarkBeforeJingle(
            105, [new(100, 110)], [new(0, 80), new(110.5, 200)], NoTranscript, NoLead);

        Assert.Equal(80, result);
    }

    // Steps 3-4: VAD "speech" blips shorter than TransientSpeechFloorSeconds (musical/vocal
    // transients in the jingle's own music) do not stop the retreat - only the first
    // qualifying (>= the floor) segment does.
    [Fact]
    public void ComputeMarkBeforeJingle_IgnoresSubFloorTransients_WhileRetreatingThroughTheMusic()
    {
        var result = JingleGeometry.ComputeMarkBeforeJingle(
            105, [], [new(0, 80), new(90, 90.3), new(95, 95.35), new(110, 200)], NoTranscript, NoLead);

        Assert.Equal(80, result);
    }

    // Confirmed on real audio (chapters 1, 8, 10 and 34 of a test audiobook): a stored silence
    // encountered while retreating through what VAD reports as one unbroken non-speech run is a
    // stop at its own end - the true jingle start, matching LeadingSilence's own anchor rule
    // elsewhere in this file - rather than something to be walked straight through into whatever
    // lies beyond. This is the "two jingles separated by a real silence" shape: retreating past
    // the second jingle's music must land at the silence between the two, not sail on through it
    // into the tail of the first (the previous chapter's outro sting).
    [Fact]
    public void ComputeMarkBeforeJingle_RetreatCrossesAGenuineSilence_StopsAtItsEndRatherThanThroughIt()
    {
        var result = JingleGeometry.ComputeMarkBeforeJingle(
            102.75, [new(95, 100)], [new(0, 95), new(103, 200)], NoTranscript, NoLead);

        Assert.Equal(100, result);
    }

    // Confirmed on real audio (chapter 5): the old, undirected adjacency check let a VAD blip
    // that only *starts* after the point under test - here, effectively the chapter
    // announcement's own trailing word, spoken just after the mark - masquerade as "preceding"
    // speech merely because its end fell close enough. The fix requires the blip to also start
    // at or before that point; with it excluded, the walk correctly keeps retreating past the
    // jingle to the real preceding narration instead of stopping dead just inside it.
    [Fact]
    public void ComputeMarkBeforeJingle_ABlipStartingAfterTheMark_NeverCountsAsPrecedingSpeech()
    {
        var result = JingleGeometry.ComputeMarkBeforeJingle(
            100, [], [new(50, 70), new(100.2, 101.0)], NoTranscript, NoLead);

        Assert.Equal(70, result);
    }

    // Confirmed on real audio (chapters 8 and 9): a musical/vocal transient inside jingle music
    // can run well past TransientSpeechFloorSeconds - clearing the duration floor even though it
    // carries no real words. When the transcript actually covers that stretch and transcribes
    // nothing there (Whisper does not silently skip genuine narration), the blip is recognised
    // as music and walked straight through, exactly like a too-short one - landing the mark on
    // the real narration that precedes it instead.
    // Transcript segment spans below are deliberately kept short relative to their text
    // (comfortably above MinPlausibleSpeechCharsPerSecond) - see the pace-based rejection test
    // further down for the case where that pace is exactly what disqualifies a segment.
    [Fact]
    public void ComputeMarkBeforeJingle_AnUncorroboratedBlip_IsTreatedAsMusicAndWalkedThrough()
    {
        var transcript = new TranscriptWindow(
        [
            new(0, 4, "Vorheriges Kapitel Text.", 1.0),
            new(60, 90, " ", 1.0), // covers the blip below but transcribes nothing over it
        ], 0, 90);

        var result = JingleGeometry.ComputeMarkBeforeJingle(
            105, [], [new(0, 50), new(70, 71.0), new(110, 200)], transcript, NoLead);

        Assert.Equal(50, result);
    }

    // The corroboration check is not a blanket rejection: a blip the transcript actually covers
    // with real, plausibly-paced words is genuine trailing narration, not music, and stops the
    // retreat right there - same shape as the previous test, but with the blip transcribed this
    // time.
    [Fact]
    public void ComputeMarkBeforeJingle_ACorroboratedBlip_StopsTheRetreatThere()
    {
        var transcript = new TranscriptWindow(
        [
            new(0, 4, "Vorheriges Kapitel Text.", 1.0),
            new(69.5, 71.5, "Ein Wort hier.", 1.0),
        ], 0, 90);

        var result = JingleGeometry.ComputeMarkBeforeJingle(
            105, [], [new(0, 50), new(70, 71.0), new(110, 200)], transcript, NoLead);

        Assert.Equal(71.0, result);
    }

    // Confirmed on real audio (chapters 8 and 10): a transcript segment can cover a blip with
    // real, non-blank text and still fail to corroborate it, when Whisper smeared genuine
    // narration together with a reverb-drenched or musically-stretched announcement into one
    // abnormally long segment - "Kapitel 8" spanning 35 s here mirrors the real case. Such a
    // segment's average pace falls far below any plausible spoken rate, so the blip it covers is
    // rejected exactly like an untranscribed one and walked through as music.
    [Fact]
    public void ComputeMarkBeforeJingle_ABlipCorroboratedOnlyByAnImplausiblyPacedSegment_IsWalkedThrough()
    {
        var transcript = new TranscriptWindow(
        [
            new(0, 4, "Vorheriges Kapitel Text.", 1.0),
            new(55, 90, "Kapitel 8", 1.0),
        ], 0, 90);

        var result = JingleGeometry.ComputeMarkBeforeJingle(
            105, [], [new(0, 50), new(70, 71.0), new(110, 200)], transcript, NoLead);

        Assert.Equal(50, result);
    }

    // A blip lying entirely outside the window Whisper was actually asked to transcribe has
    // nothing to corroborate against - that window does not necessarily reach as far back as the
    // retreat walk does - so it falls back to trusting VAD's duration alone, exactly as when no
    // transcript is available at all.
    [Fact]
    public void ComputeMarkBeforeJingle_ABlipOutsideTheTranscribedWindow_FallsBackToTrustingVad()
    {
        var transcript = new TranscriptWindow([new(200, 210, "Ganz woanders.", 1.0)], 200, 210);

        var result = JingleGeometry.ComputeMarkBeforeJingle(
            105, [], [new(0, 50), new(70, 71.0), new(110, 200)], transcript, NoLead);

        Assert.Equal(71.0, result);
    }

    // The counterpart, and the reason the window's span is carried alongside its segments rather
    // than derived from them (confirmed on real audio 2026-07-31, two books, two chapters): a probe
    // that opens exactly on the jingle hears nothing until the announcement, so its transcript is a
    // single segment whose start TrimLeadingNonSpeech then advances to the announcement itself.
    // Every music transient in the jingle then lies before the earliest segment timestamp while
    // sitting squarely inside the decoded audio - "not covered" by the segments, "covered and
    // silent" by the window. Judged against the window, the blip at 70-71 is recognised as music
    // and walked through to the real narration at 50; judged against the segments it was trusted
    // on duration alone, stranding the mark 20 s inside the music.
    [Fact]
    public void ComputeMarkBeforeJingle_ABlipBeforeTheFirstSegmentButInsideTheWindow_IsStillJudgedMusic()
    {
        var transcript = new TranscriptWindow([new(90, 94, "Kapitel 8", 1.0)], 50, 110);

        var result = JingleGeometry.ComputeMarkBeforeJingle(
            105, [], [new(0, 50), new(70, 71.0), new(110, 200)], transcript, NoLead);

        Assert.Equal(50, result);
    }

    // Step 5: the retreat runs out of VAD data before ever finding real preceding speech (a
    // jingle sitting at the very start of the file, before there was any narration) - the
    // reached position is backed off by the flat JingleWalkFallbackLeadSeconds lead instead.
    [Fact]
    public void ComputeMarkBeforeJingle_NoPrecedingSpeechAtAll_BacksOffByTheFlatLead()
    {
        var result = JingleGeometry.ComputeMarkBeforeJingle(
            3.0, [], [new(10, 20)], NoTranscript, NoLead);

        Assert.Equal(2.5, result);
    }

    // Step 5's flat lead must never push the mark negative.
    [Fact]
    public void ComputeMarkBeforeJingle_NoPrecedingSpeechAtAll_NeverGoesNegative()
    {
        var result = JingleGeometry.ComputeMarkBeforeJingle(
            0.3, [], [], NoTranscript, NoLead);

        Assert.Equal(0, result);
    }

    // Step 2's cross-detector adjacency tolerance (JingleWalkAdjacencyToleranceSeconds) absorbs
    // silencedetect/VAD boundary jitter right up to its own limit, but not a hair beyond it -
    // past the limit, the walk instead retreats all the way to where the speech actually ends.
    [Theory]
    [InlineData(101.5, 101.5)] // exactly at the tolerance: still "real speech right here"
    [InlineData(101.51, 100)] // just past it: falls through to the retreat, landing at 100
    public void ComputeMarkBeforeJingle_AdjacencyToleranceCoversJitter_ButNotBeyondIt(
        double originalMark, double expected)
    {
        var result = JingleGeometry.ComputeMarkBeforeJingle(
            originalMark, [], [new(0, 100)], NoTranscript, NoLead);

        Assert.Equal(expected, result);
    }

    // The four two-jingle chapters of the German test audiobook, at their real measured
    // geometry (whole-file VAD + silencedetect, 2026-07-26). Each has the previous chapter's
    // outro sting, a silence, then this chapter's own jingle - which Silero VAD reports as one
    // undifferentiated non-speech run, so only silencedetect's separator distinguishes them.
    // Stopping at that separator puts the mark at the second jingle's start; walking through it
    // (as the retreat used to) overshot by 9-37 s, into the previous chapter's sting.
    [Theory]
    // mark, narration, separator, pre-announcement hush, announcement, expected
    [InlineData(179.75, 142.240, 143.360, 162.837, 166.171, 186.462, 187.565, 187.840, 188.448, 166.171)]
    [InlineData(12185.606, 12157.312, 12159.072, 12166.079, 12167.989, 12184.212, 12185.654, 12185.856, 12187.0, 12167.989)]
    [InlineData(15657.158, 15629.888, 15630.848, 15635.989, 15639.271, 15655.663, 15657.359, 15657.408, 15658.5, 15639.271)]
    [InlineData(49954.790, 49918.560, 49920.096, 49929.554, 49931.249, 49952.451, 49954.899, 49955.040, 49956.192, 49931.249)]
    public void ComputeMarkBeforeJingle_TwoJinglesSeparatedByASilence_MarksTheSecondOnesStart(
        double originalMark, double narrationStart, double narrationEnd,
        double separatorStart, double separatorEnd, double hushStart, double hushEnd,
        double announcementStart, double announcementEnd, double expected)
    {
        var result = JingleGeometry.ComputeMarkBeforeJingle(
            originalMark,
            [new(separatorStart, separatorEnd), new(hushStart, hushEnd)],
            [new(narrationStart, narrationEnd), new(announcementStart, announcementEnd)],
            NoTranscript, NoLead);

        Assert.Equal(expected, result);
    }

    // The counterpart safety case, at the real geometry of one of the same book's correctly
    // marked chapters: a single jingle whose music never dips below silencedetect's threshold,
    // so the only silences in the traversed stretch are the leading hush (whose end is the
    // jingle start - the right answer) and the pre-announcement hush that step 1 exits. Stopping
    // unconditionally at silences must therefore leave chapters of this shape exactly where they
    // already were; measured against all ten known-good chapters, none of them has a silence
    // inside its jingle at all.
    [Fact]
    public void ComputeMarkBeforeJingle_ASilenceFreeJingle_IsUnaffectedByTheSilenceStop()
    {
        var result = JingleGeometry.ComputeMarkBeforeJingle(
            9966.376,
            [new(9941.758, 9945.162), new(9965.418, 9966.626)],
            [new(9940.800, 9941.824), new(9966.784, 9967.520)],
            NoTranscript, NoLead);

        Assert.Equal(9945.162, result);
    }

    // The three outliers of a six-book test run (2026-07-31), at their real measured geometry -
    // every other mark of that run was judged near-perfect by ear, so these are the whole of what
    // the two fixes below had to move. Times are the log's own, rounded to 10 ms.
    //
    // The first two are the same defect: a short musical sting inside the jingle, before the
    // announcement and therefore before the transcript's only (trimmed) segment, but well inside
    // the decoded window - trusted as narration on its VAD duration alone until IsGenuineSpeech
    // started judging coverage by the window's span. Both marks stopped at the sting's end,
    // 2.9 s and 6.9 s deep into their jingle's music.
    //
    // The third is the boundary defect: the sting starts 0.01 s *before* silencedetect ends the
    // pre-jingle hush, so retreating past it left the walk a hair inside that hush - which an
    // ends-before-here silence test then refused to see, sending the walk on to the narration and
    // the mark to the hush's start, 2.7 s early.
    [Theory]
    // mark, narration end, hush (start/end, equal when there is none), sting, announcement, window
    // (start/end), expected
    // "Die Dritte Macht" ch. 33: silence-less jingle, sting 2.05 s into it.
    [InlineData(48516.22, 48500.19, 48500.19, 48500.19, 48502.24, 48503.07, 48516.57, 48518.17,
                48500.19, 48561.69, 48500.19)]
    // "Gruelfin" ch. 25: hush, then jingle, sting 6.32 s into the music.
    [InlineData(43490.01, 43478.27, 43478.19, 43481.39, 43487.71, 43488.32, 43490.43, 43491.90,
                43481.39, 43514.69, 43481.39)]
    // "Gruelfin" ch. 14: hush, then a 0.26 s sting straddling its end by 0.25 s.
    [InlineData(24206.61, 24197.53, 24197.44, 24200.10, 24200.09, 24200.35, 24206.91, 24207.74,
                24200.10, 24236.90, 24200.10)]
    public void ComputeMarkBeforeJingle_TheThreeMisplacedMarksOfTheJuly2026Run_LandAtTheJingleStart(
        double originalMark, double narrationEnd, double hushStart, double hushEnd,
        double stingStart, double stingEnd, double announcementStart, double announcementEnd,
        double windowStart, double windowEnd, double expected)
    {
        // What Whisper reported for such a window: one segment, the announcement, its start
        // already advanced onto the phrase by TrimLeadingNonSpeech.
        var transcript = new TranscriptWindow(
            [new(announcementStart, windowEnd, "Kapitel 25", 0.6)], windowStart, windowEnd);

        var result = JingleGeometry.ComputeMarkBeforeJingle(
            originalMark,
            hushEnd > hushStart ? [new(hushStart, hushEnd)] : [],
            [new(narrationEnd - 3, narrationEnd), new(stingStart, stingEnd),
             new(announcementStart, announcementEnd)],
            transcript, NoLead);

        Assert.Equal(expected, result);
    }

    // Step 5, the mark lead. The walk stops at the end of the hush before the jingle (100), and
    // the lead then backs the mark into that hush - the same "a moment of quiet before the thing
    // the mark is for" rule default-mode placement follows, applied to a jingle instead of an
    // announcement. --mark-lead was documented as ignored under --mark-before-jingle; it never was
    // in the no-jingle case (see below), and now it is not here either.
    [Theory]
    [InlineData(0.35, 99.65)]
    [InlineData(0.0, 100.0)]  // --mark-lead 0 keeps the mark exactly at the jingle's start
    [InlineData(2.0, 98.0)]   // the whole 2 s hush, exactly consumed
    public void ComputeMarkBeforeJingle_AWalkStoppingOnAHush_BacksIntoItByTheMarkLead(
        double markLead, double expected)
    {
        var result = JingleGeometry.ComputeMarkBeforeJingle(
            110, [new(98, 100)], [new(0, 98), new(112, 200)], NoTranscript, markLead);

        Assert.Equal(expected, result);
    }

    // ...and never further than the hush itself, whose start is where the previous chapter's
    // narration ends. A lead longer than the hush is spent in full on it and no more, which is the
    // one thing --mark-before-jingle exists to guarantee: no mark inside the old chapter.
    [Fact]
    public void ComputeMarkBeforeJingle_AMarkLeadLongerThanTheHush_StopsAtTheHushStart()
    {
        var result = JingleGeometry.ComputeMarkBeforeJingle(
            110, [new(99.7, 100)], [new(0, 99.7), new(112, 200)], NoTranscript, 5.0);

        Assert.Equal(99.7, result);
    }

    // A walk stopping on *speech* gets no lead at all: the previous chapter's narration runs right
    // up to the jingle here, with no hush to sit in, so backing off would put the mark inside it.
    [Fact]
    public void ComputeMarkBeforeJingle_AWalkStoppingOnNarration_GetsNoLead()
    {
        var result = JingleGeometry.ComputeMarkBeforeJingle(
            110, [], [new(0, 100), new(112, 200)], NoTranscript, 0.35);

        Assert.Equal(100, result);
    }

    // And a phrase with no jingle in front of it never reaches step 5: step 2 hands the original
    // mark straight back, and that mark already carries the lead its own default-mode placement
    // applied. This is the case a book with jingles on only some of its chapters runs into on the
    // rest of them, and the lead has to survive it whatever --mark-before-jingle does elsewhere.
    [Theory]
    [InlineData(0.35)]
    [InlineData(2.0)]
    public void ComputeMarkBeforeJingle_NoJingleAtAll_HandsBackTheAlreadyLedMarkWhateverTheLead(
        double markLead)
    {
        var result = JingleGeometry.ComputeMarkBeforeJingle(
            100, [], [new(0, 150)], NoTranscript, markLead);

        Assert.Equal(100, result);
    }

    // RetreatPastNonSpeech itself: starting inside a qualifying segment never moves the
    // position further than necessary - it is returned unchanged.
    [Fact]
    public void RetreatPastNonSpeech_AlreadyInsideAQualifyingSegment_ReturnsFromUnchanged()
    {
        var (position, foundBoundary, _) = JingleGeometry.RetreatPastNonSpeech(
            50, [new(40, 60)], [], NoTranscript, 0.4);

        Assert.Equal(50, position);
        Assert.True(foundBoundary);
    }

    // The chapter-12 shape (2026-07-26): the retreat can land exactly inside a sub-floor musical
    // transient deep in the jingle, with no stored silence anywhere between it and the starting
    // position. The straddling check above must apply the same duration/corroboration gate the
    // other blip-handling branch already has,
    // rather than accepting outright just because the position happens to fall inside some VAD
    // speech segment - otherwise a too-short transient like this one (0.384 s, under the 0.4 s
    // floor) is trusted as the true jingle edge, undershooting the walk by most of the jingle's
    // own length. Skipped over, the retreat continues back to the real narration further behind.
    [Fact]
    public void RetreatPastNonSpeech_StartingInsideASubFloorBlip_IsSkippedRatherThanAcceptedOutright()
    {
        var (position, foundBoundary, _) = JingleGeometry.RetreatPastNonSpeech(
            29.8, [new(0, 20), new(29.6, 29.984)], [], NoTranscript, 0.4);

        Assert.Equal(20, position);
        Assert.True(foundBoundary);
    }

    // Chains backward through several too-short blips before accepting the first one that
    // meets the minimum length.
    [Fact]
    public void RetreatPastNonSpeech_SkipsShortBlips_ChainingBackToTheFirstQualifyingOne()
    {
        var (position, foundBoundary, _) = JingleGeometry.RetreatPastNonSpeech(
            100, [new(0, 80), new(85, 85.2), new(92, 92.1)], [], NoTranscript, 0.4);

        Assert.Equal(80, position);
        Assert.True(foundBoundary);
    }

    // An already-passed stored silence nearer to the starting position than the nearest
    // qualifying speech blip is a stop at its own end, without ever needing to fall back to that
    // more distant blip - the "trailing narration, then leading silence, then jingle" shape.
    [Fact]
    public void RetreatPastNonSpeech_AGenuineSilenceIsCloser_StopsAtItsEnd()
    {
        var (position, foundBoundary, _) = JingleGeometry.RetreatPastNonSpeech(
            70, [new(0, 48.5)], [new(50, 60)], NoTranscript, 0.4);

        Assert.Equal(60, position);
        Assert.True(foundBoundary);
    }

    // A stored silence stops the walk whatever precedes it - no corroboration from nearby speech
    // is asked for, and none is needed: silencedetect does not read jingle music as silence, so
    // the silence marks a real break in the music, and the mark belongs at the start of whatever
    // plays after it. Same setup as the previous test, but with the nearest speech now far beyond
    // JingleWalkAdjacencyToleranceSeconds from the silence - which changes nothing.
    [Fact]
    public void RetreatPastNonSpeech_ASilenceStopsTheWalk_WhateverPrecedesIt()
    {
        var (position, foundBoundary, _) = JingleGeometry.RetreatPastNonSpeech(
            70, [new(0, 40)], [new(50, 60)], NoTranscript, 0.4);

        Assert.Equal(60, position);
        Assert.True(foundBoundary);
    }

    // With several silences behind the starting position, the walk stops at the nearest one and
    // never reaches those further back: it is looking for the start of the music that immediately
    // precedes the announcement, not for the earliest break anywhere in a long non-speech stretch.
    [Fact]
    public void RetreatPastNonSpeech_SeveralSilencesBehind_StopsAtTheNearestOne()
    {
        var (position, foundBoundary, _) = JingleGeometry.RetreatPastNonSpeech(
            100, [new(0, 50)], [new(50, 52), new(95, 97)], NoTranscript, 0.4);

        Assert.Equal(97, position);
        Assert.True(foundBoundary);
    }

    // Real-audio replay, chapters 6 and 14 of one German audiobook (2026-08-02). Both marks landed
    // on the previous chapter's closing sentence, twelve and eighteen seconds early, and both had
    // the identical shape: the narrator pauses mid-sentence, so VAD cuts the last words into a
    // second blip which the short-gap merge then swallows into the jingle's region, where
    // ResolveDefaultPhraseOnset took it for the announcement. AdjustJingleRegion cannot trim it -
    // the probe window opens on the jingle and its transcript has no words for audio it never saw.
    //
    // The numbers below are the file's own, measured with tools\vadprobe's wholevad mode, and the
    // transcript is the one that window really produced; the announcement's true position was
    // confirmed by re-transcribing 5.25 s from each expected mark. Replaying them through the real
    // helpers is what makes this a regression test rather than a restatement of the fix.
    [Theory]
    // Chapter 6, reached through the short-window jingle re-read: its window opens at 6891.29, past
    // the blip entirely, and Whisper hands back one 25 s segment.
    [InlineData(6891.29, 6916.29, 6908.416)]
    // Chapter 14, an ordinary probe window opening on the region itself at 18698.144. The reused
    // half of its transcript starts before the window and is sliced off, so the announcement's own
    // segment (18701.40-18713.40 before trimming) is all that reaches the geometry.
    [InlineData(18698.144, 18713.40, 18710.688)]
    public void ResolveDefaultPhraseOnset_BlipEndingTheHush_IsNotTakenForTheAnnouncement(
        double windowStart, double segmentEnd, double expectedOnset)
    {
        List<SpeechSegment> speech =
        [
            new(6883.104, 6886.624), new(6887.744, 6889.472), new(6890.368, 6891.264),
            new(6908.416, 6909.600), new(6910.592, 6911.296),
            new(18693.984, 18695.968), new(18697.024, 18698.144), new(18698.848, 18699.616),
            new(18710.688, 18711.936), new(18712.960, 18713.856),
        ];
        List<Silence> silences =
        [
            new(6886.489, 6887.760), new(6889.206, 6890.372), new(6891.104, 6895.008),
            new(6909.511, 6910.596), new(6911.171, 6912.148),
            new(18695.658, 18697.038), new(18698.019, 18698.843), new(18699.374, 18703.431),
            new(18711.911, 18712.925), new(18713.739, 18715.899),
        ];
        var regions = JingleGeometry.ComputeNonSpeechRegions(speech);

        // What the window's transcript amounts to once the announcement's segment is trimmed of the
        // silence and jingle Whisper timestamped it from - the phrase onset the geometry is handed.
        List<TranscriptSegment> transcript = [new(expectedOnset, segmentEnd, " Kapitel", 1.0)];
        var (anchorSilence, region) = JingleGeometry.ResolveJingleAnchor(
            expectedOnset, segmentEnd, windowStart, silences, regions,
            candidateVadRegion: null, speech, transcript);

        var onset = JingleGeometry.ResolveDefaultPhraseOnset(
            expectedOnset, region, anchorSilence, speech);

        Assert.Equal(expectedOnset, onset, 3);
        Assert.Equal(expectedOnset - 0.35, JingleGeometry.RefineDefaultMark(onset - 0.35, speech, 0.35), 3);
    }

    // When the retreat runs out of speech data entirely, it still reports however far it got
    // (having skipped past any too-short blips along the way) rather than the original
    // starting position - the caller's own fallback (step 5) backs off from that.
    [Fact]
    public void RetreatPastNonSpeech_RunsOutOfData_ReturnsFalseWithTheFurthestPositionReached()
    {
        var (position, foundBoundary, _) = JingleGeometry.RetreatPastNonSpeech(
            10, [new(2, 2.1)], [], NoTranscript, 0.4);

        Assert.Equal(2, position);
        Assert.False(foundBoundary);
    }
}
