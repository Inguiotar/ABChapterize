// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using ABChapterize.Vad;
using static ABChapterize.Detection.DetectionTuning;

namespace ABChapterize.Detection;

/// <summary>One stretch of audible non-speech: sound the VAD did not call speech and
/// silencedetect did not call silence, which on an audiobook is music.</summary>
/// <param name="StartSeconds">Where the audible non-speech begins.</param>
/// <param name="EndSeconds">Where it ends.</param>
/// <param name="AnnouncementSeconds">Where VAD hears speech again after this jingle - at a chapter
/// transition, the announcement itself. This, not <see cref="EndSeconds"/>, is what a probe window
/// crossing the jingle has to arrive at: the two differ whenever a hush sits between the music and
/// the voice, or the music resumes after an interior dip the census had to cut.</param>
/// <param name="BridgedBlips">How many VAD "speech" transients shorter than
/// <see cref="TransientSpeechFloorSeconds"/> lie inside this jingle, i.e. how often Silero picked a
/// vocal- or rhythm-like moment out of the music. Zero for a clean instrumental sting.</param>
internal readonly record struct Jingle(
    double StartSeconds, double EndSeconds, double AnnouncementSeconds, int BridgedBlips)
{
    /// <summary>How long the stretch runs, the figure <see cref="JingleCensus"/> reports on.</summary>
    internal double LengthSeconds => EndSeconds - StartSeconds;

    /// <summary>How far back the music reaches from the speech behind it - the quantity a mark
    /// placed at an announcement has to look back over, which is not the same as
    /// <see cref="LengthSeconds"/> wherever a hush separates the two.</summary>
    internal double ReachSeconds => AnnouncementSeconds - StartSeconds;
}

/// <summary>
/// Counts and measures a file's jingles from Pass 1's two raw signals, for the --verbose log and
/// the --debug listing under it. Purely diagnostic: nothing in detection or placement reads a
/// census back, so an entry carries more than the log prints - where the speech behind the jingle
/// resumes, and how many VAD transients had to be bridged to see it as one stretch.
/// <para>
/// A jingle here is a stretch of at least <see cref="MinJingleObservationSeconds"/> that VAD did
/// not hear speech in and silencedetect did not call silence, with a vocal transient shorter than
/// <see cref="TransientSpeechFloorSeconds"/> bridged rather than ending it - the same reading of
/// "that blip is the music, not a speaker" that --mark-before-jingle's walk uses, so the two agree
/// about where a jingle stops.
/// </para>
/// <para>
/// Deliberately measured from the speech segments themselves rather than from
/// <see cref="JingleGeometry.ComputeNonSpeechRegions"/>'s output, which is not the same question:
/// those regions are Pass 2's candidate list, merged at a wider gap and then filtered by longest
/// <em>contiguous</em> run, so a jingle a transient splits into two 1.5 s halves is dropped from
/// them entirely - correct for "is this worth a probe", wrong for "how long is this book's music".
/// Consequently the census and the region count need not agree, and neither is a subset of the
/// other.
/// </para>
/// <para>
/// <b>Why the narrower floor is also the more accurate one</b> (measured 2026-08-08 by replaying
/// both readings over the fourteen-book corpus's own Pass 1 signals, parsed out of the 2026-08-07
/// debug logs in <c>L:\Temp</c>). A chapter announcement is a <em>short</em> VAD segment - "Kapitel
/// eins." runs 0.6-0.9 s - so the regions' 1.0 s merge bridges straight over it and the jingle
/// appears to run on to the next narration, seconds past where the music stopped. Four marks pin
/// this down: Raumschiff Erde's chapter 1 (mark 0:05:07.28, announcement 307.48-308.41), chapter 2
/// (2355.07, 2355.48-2356.32) and chapter 4 (5551.38, 5551.58-5552.25), and Die Maahks' chapter 5
/// (6305.40, 6305.56-6306.20) - in every one the census ends the jingle within 0.2 s of the mark,
/// where the region-based reading ran 0.6-2.3 s past it. The corpus-wide count moves both ways
/// (BARDIOC 29 to 32, Das Mutantenkorps 50 to 62, Die Cyber-Brutzellen 39 to 37, Wintersmith 16 to
/// 14), and on the five books with no chapter music at all the spurious tally drops - The Forever
/// War and I Shall Wear Midnight from 1 to 0, Mort and Paula Monti from 3 to 1.
/// </para>
/// </summary>
internal static class JingleCensus
{
    /// <summary>
    /// Every audible stretch of at least <see cref="MinJingleObservationSeconds"/> between two
    /// genuine VAD speech segments, in file order.
    /// <para>
    /// The silence floor is load-bearing and is not an approximation worth "fixing": silences below
    /// <see cref="MinStoredSilenceSeconds"/> were never recorded, so a beat of quiet inside a music
    /// sting does not split one jingle into two, which is what keeps the measured lengths
    /// comparable to the reach <see cref="ReachSeconds"/> derives from them.
    /// </para>
    /// <para>
    /// What keeps narration out is that cut, not the speech segments: a passage of short words
    /// separated by breath pauses bridges into one span exactly as a transient-broken jingle does,
    /// but its pauses are stored silences, so what survives the cut is the individual words -
    /// nowhere near <see cref="MinJingleObservationSeconds"/> apiece.
    /// </para>
    /// </summary>
    /// <param name="speech">The raw VAD speech segments in file order; empty when the pre-pass did
    /// not run, which yields an empty census.</param>
    /// <param name="silences">Every silence Pass 1 stored, down to
    /// <see cref="MinStoredSilenceSeconds"/> - the whole list, not Pass 2's --min-silence-length
    /// subset, since a half-second hush is still not music.</param>
    internal static List<Jingle> Measure(List<SpeechSegment> speech, List<Silence> silences)
    {
        var jingles = new List<Jingle>();
        // Sorted defensively (the scan emits them in order) because the walk below leans on it, and
        // scanned with a carried index rather than re-filtered per span: a long book brings tens of
        // thousands of speech segments and thousands of silences to this, and the quadratic version
        // of the same loop is the one thing here that could be felt.
        var ordered = silences.OrderBy(s => s.StartSeconds).ToList();
        var next = 0;
        foreach (var span in NonSpeechSpans(speech))
        {
            while (next < ordered.Count && ordered[next].EndSeconds <= span.Start)
                next++;
            AddJingles(jingles, span, AudibleParts(span, ordered, next));
        }
        return jingles;
    }

    /// <summary>
    /// How far back this file's music may reach from an announcement: the longest reach any jingle
    /// in the census actually showed, plus <see cref="PhraseMarginSeconds"/>. This is the figure the
    /// two places that ask that question read - Pass 3's anchor lookback and
    /// <see cref="PreciseMarkRefiner"/>'s --mark-before-jingle verification span - in place of the
    /// blind 50 s that --max-jingle-length used to hand them.
    /// <para>
    /// Measured rather than assumed, so it is tighter than that on every book of the corpus bar one:
    /// the longest jingles run 6.5 s (Rendezvous With Rama) to 33.7 s (BARDIOC). The exception is
    /// "Paula Monti" at 67.5 s, a book with a single census entry which is almost certainly a
    /// musical intro rather than a chapter jingle - if a mark there ever moves for want of a shorter
    /// lookback, that entry is the first thing to look at.
    /// </para>
    /// <para>
    /// On a book with no jingles at all this collapses to the margin, which is correct: there is no
    /// music to look back over, and every consumer of it is asking about music.
    /// </para>
    /// </summary>
    /// <param name="jingles">The file's census, empty where VAD found no jingles.</param>
    internal static double ReachSeconds(IReadOnlyList<Jingle> jingles)
        => (jingles.Count == 0 ? 0 : jingles.Max(j => j.ReachSeconds)) + PhraseMarginSeconds;

    /// <summary>Renders a census as the one --verbose line it is worth: how many jingles the file
    /// has and how long they run. The spread matters as much as the count - a book whose jingles
    /// are all within a second of each other plays one sting, while a wide spread means the music
    /// varies per chapter and the file's reach has to cover the longest.</summary>
    /// <param name="jingles">What <see cref="Measure"/> found.</param>
    internal static string Describe(List<Jingle> jingles)
        => $"{jingles.Count} jingle(s) of >= {MinJingleObservationSeconds:0.#} s found" +
           (jingles.Count > 0
               ? $" - shortest {jingles.Min(j => j.LengthSeconds):0.00} s, " +
                 $"longest {jingles.Max(j => j.LengthSeconds):0.00} s, " +
                 $"average {jingles.Average(j => j.LengthSeconds):0.00} s"
               : "");

    /// <summary>One stretch of non-speech between two genuine speech segments, with the transients
    /// bridged to build it. A class rather than a record because bridging grows a span in place,
    /// both its end and its blip list.</summary>
    /// <param name="start">Where VAD stopped hearing speech.</param>
    /// <param name="end">Where it heard speech again - the announcement, at a chapter transition.</param>
    private sealed class NonSpeechSpan(double start, double end)
    {
        /// <summary>Where VAD stopped hearing speech.</summary>
        internal double Start { get; } = start;

        /// <summary>Where it heard speech again, moved forward by each bridged transient.</summary>
        internal double End { get; set; } = end;

        /// <summary>The bridged transients inside, in file order.</summary>
        internal List<SpeechSegment> Blips { get; } = [];
    }

    /// <summary>
    /// The gaps between consecutive speech segments, with any gap whose separating "speech" is
    /// shorter than <see cref="TransientSpeechFloorSeconds"/> merged into the one before it - the
    /// transient counting toward the length, since a vocal-like blip Silero picked out of a music
    /// sting is part of the sting.
    /// <para>
    /// Non-speech before the first segment and after the last is left out, as it is for
    /// <see cref="JingleGeometry.ComputeNonSpeechRegions"/>: a jingle is flanked by narration on
    /// both sides, whereas a file's head and tail hold publisher idents, credits and dead air that
    /// would skew every figure this census reports.
    /// </para>
    /// </summary>
    /// <param name="speech">The raw VAD speech segments in file order.</param>
    private static List<NonSpeechSpan> NonSpeechSpans(List<SpeechSegment> speech)
    {
        var spans = new List<NonSpeechSpan>();
        for (var i = 1; i < speech.Count; i++)
        {
            var start = speech[i - 1].EndSeconds;
            var end = speech[i].StartSeconds;
            // start minus the previous span's end is the length of speech[i - 1], the segment
            // between the two gaps.
            if (spans.Count > 0 && start - spans[^1].End < TransientSpeechFloorSeconds)
            {
                spans[^1].Blips.Add(speech[i - 1]);
                spans[^1].End = end;
            }
            else
            {
                spans.Add(new NonSpeechSpan(start, end));
            }
        }
        return spans;
    }

    /// <summary>
    /// What is left of one span once the silences overlapping it are cut out, in file order.
    /// </summary>
    /// <param name="span">The span to cut.</param>
    /// <param name="ordered">The stored silences, ordered by start.</param>
    /// <param name="next">Index of the first silence that can still overlap this span.</param>
    private static List<(double Start, double End)> AudibleParts(
        NonSpeechSpan span, List<Silence> ordered, int next)
    {
        var parts = new List<(double Start, double End)>();
        // Silences are clipped to the span, and overlapping ones collapse into one, because the
        // cursor only ever moves forward.
        var cursor = span.Start;
        for (var i = next; i < ordered.Count && ordered[i].StartSeconds < span.End; i++)
        {
            var until = Math.Min(ordered[i].StartSeconds, span.End);
            if (until > cursor)
                parts.Add((cursor, until));
            cursor = Math.Max(cursor, ordered[i].EndSeconds);
        }
        if (span.End > cursor)
            parts.Add((cursor, span.End));
        return parts;
    }

    /// <summary>Turns one span's audible parts into census entries, keeping those long enough to be
    /// a jingle and measuring each one's reach and bridged transients.</summary>
    /// <param name="jingles">The census being built.</param>
    /// <param name="span">The span the parts came from.</param>
    /// <param name="parts">Its audible parts, in file order.</param>
    private static void AddJingles(
        List<Jingle> jingles, NonSpeechSpan span, List<(double Start, double End)> parts)
    {
        foreach (var (start, end) in parts)
            if (end - start >= MinJingleObservationSeconds)
                jingles.Add(new Jingle(
                    start, end, span.End,
                    span.Blips.Count(b => b.StartSeconds >= start && b.StartSeconds < end)));
    }
}
