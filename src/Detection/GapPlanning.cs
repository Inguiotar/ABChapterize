// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using ABChapterize.Cli;
using static ABChapterize.Detection.DetectionTuning;

namespace ABChapterize.Detection;

/// <summary>Sequence bookkeeping for chapter detection: finding gaps in a detected chapter
/// sequence, the regions a --verify re-detection run needs to re-probe, the silence bands Pass 2.5
/// sweeps a still-open gap with, normalizing a raw detection list, and snapping Pass 2/Pass 3
/// window/chunk borders to word-safe seams.</summary>
internal static class GapPlanning
{
    /// <summary>A time region suspected to contain undetected chapter starts.</summary>
    /// <param name="FromSeconds">Region start.</param>
    /// <param name="ToSeconds">Region end.</param>
    internal readonly record struct GapRegion(double FromSeconds, double ToSeconds);

    /// <summary>One region <see cref="ChapterDetector.DetectCoreAsync"/> runs its own, independent Pass 2 pass
    /// over - the whole file for a fresh <see cref="ChapterDetector.DetectAsync"/> run, or a single gap-scoped
    /// stretch for <see cref="ChapterDetector.DetectGapsAsync"/>. Bounds every aspect of that pass: candidates
    /// are built only from silences/VAD regions starting inside [<paramref name="FromSeconds"/>,
    /// <paramref name="ToSeconds"/>), window ends are clamped to <paramref name="ToSeconds"/>
    /// (see <see cref="PlanWindowEnd"/>), and the running chapter-number state seeds fresh from
    /// <paramref name="LowerNumber"/>/<paramref name="UpperNumber"/> rather than carrying over
    /// from any other region.</summary>
    /// <param name="FromSeconds">Region start; candidates/decodes never precede it.</param>
    /// <param name="ToSeconds">Region end; candidates/decodes never reach past it.</param>
    /// <param name="LowerNumber">The chapter number already confirmed to precede this region, or 0
    /// when nothing precedes it (a from-file-start region). Seeds Pass 2's running "last accepted
    /// number" so a match must still exceed it to be accepted - but, unlike the whole-file case,
    /// never as the seed for the intro-transition exemption (see <see cref="LowerNumber"/>'s use
    /// in <see cref="ChapterDetector.DetectCoreAsync"/>: a match count already primed by <paramref
    /// name="LowerNumber"/> &gt; 0 is not the "chapter 1" case even when this is the very first
    /// match Pass 2 makes in this region).</param>
    /// <param name="UpperNumber">The chapter number already confirmed to follow this region, or
    /// null when nothing does (this region reaches to the file end). A match at or above it is
    /// rejected outright - guarding against a snapped probe window spilling into the next known
    /// chapter's own announcement and displacing it.</param>
    internal readonly record struct DetectionRegion(
        double FromSeconds, double ToSeconds, int LowerNumber, int? UpperNumber);

    /// <summary>The regions and, when the last checkable marking in file order is unconfirmed, the
    /// trailing recovery target <see cref="ChapterDetector.DetectCoreAsync"/> needs - see <see
    /// cref="BuildGapRegions"/>.</summary>
    /// <param name="Regions">One region per run of consecutive unconfirmed markings.</param>
    /// <param name="TrailingFrom">Start of the trailing region (the last confirmed/file-start
    /// point before it), or null when the file's last checkable marking was confirmed.</param>
    /// <param name="TrailingTargets">The expected numbers of the unconfirmed markings in the
    /// trailing run, in file order; empty when <paramref name="TrailingFrom"/> is null. Unlike an
    /// interior region's <see cref="DetectionRegion.UpperNumber"/>-bounded range, these are taken
    /// verbatim from the markings themselves since there is no following confirmed chapter to
    /// derive a contiguous range from.</param>
    internal readonly record struct GapRecoveryPlan(
        List<DetectionRegion> Regions, double? TrailingFrom, List<int> TrailingTargets);

    /// <summary>
    /// Determines the regions to fully transcribe: between every pair of consecutive detected
    /// chapters whose numbers are not consecutive, and - only when <paramref
    /// name="expectedStartChapter"/> is given - before the first chapter when its number is
    /// greater than that expectation. Without it, whatever number Pass 2 found first is trusted
    /// outright and no leading gap is ever raised, even when that number is not 1: a plain
    /// from-scratch run has no way to know whether a low first number is really the book's start
    /// or just Pass 2 missing an earlier chapter, and guessing "1" wrongly assumed the latter for
    /// every book, including legitimate split-book parts. Internal for unit testing.
    /// </summary>
    /// <param name="chapters">The currently known chapters, in chronological order.</param>
    /// <param name="duration">Total play time (currently unused; kept for a symmetric,
    /// self-explanatory signature alongside <see cref="BuildGapRegions"/>).</param>
    /// <param name="expectedStartChapter">The chapter number the book is expected to start at
    /// (--expected-start-chapter), or null to disable the leading-gap search entirely - see
    /// <see cref="CliOptions.ExpectedStartChapter"/>.</param>
    internal static List<GapRegion> FindGaps(List<DetectedChapter> chapters, double duration, int? expectedStartChapter = null)
    {
        var gaps = new List<GapRegion>();
        if (chapters.Count == 0)
            return gaps;
        if (expectedStartChapter is { } expected && !chapters[0].NumberUnverified &&
            chapters[0].Number > expected && chapters[0].TimeSeconds > MinLeadingGapSeconds)
            gaps.Add(new GapRegion(0, chapters[0].TimeSeconds));
        for (var i = 1; i < chapters.Count; i++)
        {
            // Skipping the entry rather than filtering it out of the list keeps its neighbours from
            // being paired across it: an unverified number is still a real position in the book, it
            // just may not be the far end of a hole (see DetectedChapter.NumberUnverified).
            if (chapters[i].NumberUnverified)
                continue;
            if (chapters[i].Number > chapters[i - 1].Number + 1)
                gaps.Add(new GapRegion(chapters[i - 1].TimeSeconds, chapters[i].TimeSeconds));
        }
        return gaps;
    }

    /// <summary>
    /// The chapter numbers a gap is expected to recover: every number strictly between the
    /// detected chapters bounding it, or <paramref name="expectedStartChapter"/> (1 when null) up
    /// to the first detected number for a leading gap (whose start is 0, before any chapter). The
    /// bounding chapters are located by their exact timestamps, which <see cref="FindGaps"/>
    /// copied verbatim into the gap, so the float match is exact. Pass 3 uses this to stop
    /// transcribing a gap the moment all of them are found. Internal for unit testing.
    /// </summary>
    /// <param name="chapters">The currently known chapters, in chronological order.</param>
    /// <param name="gap">A gap produced by <see cref="FindGaps"/> over these chapters.</param>
    /// <param name="expectedStartChapter">The same value <see cref="FindGaps"/> was called with;
    /// only ever relevant for a leading gap, which <see cref="FindGaps"/> raises exclusively when
    /// this is set in the first place, so leaving it null here whenever <see cref="FindGaps"/>
    /// found no leading gap at all is always safe.</param>
    internal static List<int> MissingNumbersInGap(List<DetectedChapter> chapters, GapRegion gap, int? expectedStartChapter = null)
    {
        var upper = chapters.First(c => c.TimeSeconds == gap.ToSeconds).Number;
        // A leading gap starts at 0 with no chapter there; FirstOrDefault yields a default
        // DetectedChapter (Number 0), the signal to fall back to expectedStartChapter - 1 (or 0,
        // when null) so the expected set becomes expectedStartChapter..upper-1 as intended.
        var boundChapter = chapters.FirstOrDefault(c => c.TimeSeconds == gap.FromSeconds);
        var lower = boundChapter.Number != 0 ? boundChapter.Number : (expectedStartChapter ?? 1) - 1;
        var missing = new List<int>();
        for (var n = lower + 1; n < upper; n++)
            missing.Add(n);
        return missing;
    }

    /// <summary>
    /// The silence-length bands Pass 2.5 sweeps a still-open gap with, longest first: one band per
    /// <see cref="DetectionTuning.SubFloorSweepBandCount"/>, each
    /// <see cref="DetectionTuning.SubFloorSweepBandSeconds"/> wide, the first ending exactly at
    /// <paramref name="floorSeconds"/> so no silence the run's own demand admitted is swept again.
    /// <para>
    /// "The run's demand" rather than "everything Pass 2 could have reached": under
    /// <c>--min-silence-length auto</c> the threshold can talk itself down to
    /// <see cref="DetectionTuning.AdaptiveSilenceFloorSeconds"/>, so on a book whose breaks are
    /// short some of these bands were within Pass 2's reach after all. Sweeping them anyway is not
    /// waste - Pass 2.5 probes on the <c>--pass3-model</c> recognizer, so a second look at the same
    /// audio is a different reading and the reason the pass exists at all.
    /// </para>
    /// <para>
    /// Anchoring the bands to the effective <c>--min-silence-length</c> rather than to fixed
    /// absolute lengths is what makes them mean the same thing on every run: the sweeps ask "how
    /// far below what this run demanded is a break still plausible", a question whose answer moves
    /// with the demand. A book run at <c>-n 3</c> gets bands under 3 s, not under 1.5 s.
    /// </para>
    /// <para>
    /// Bands below <paramref name="storedFloorSeconds"/> are dropped rather than returned empty:
    /// Pass 1 never kept silences that short, so sweeping them would report "nothing found" about
    /// audio nothing ever looked at. Internal for unit testing.
    /// </para>
    /// </summary>
    /// <param name="floorSeconds">The run's --min-silence-length, i.e. the shortest silence it
    /// opened by demanding.</param>
    /// <param name="storedFloorSeconds">The shortest silence Pass 1 retained at all
    /// (<see cref="DetectionTuning.MinStoredSilenceSeconds"/>, or the floor when that is lower).</param>
    /// <returns>The bands as half-open [min, max) intervals, longest first; empty when the floor
    /// already sits at or below what Pass 1 stored.</returns>
    internal static List<(double MinSeconds, double MaxSeconds)> SubFloorSweepBands(
        double floorSeconds, double storedFloorSeconds)
    {
        var bands = new List<(double, double)>();
        for (var i = 0; i < SubFloorSweepBandCount; i++)
        {
            // Both bounds measured from the floor rather than the band above, so a band's own width
            // never accumulates rounding: at the default floor the last band's minimum is exactly
            // 1.0, not 1.0000000000000002, and a real 1.00 s silence still falls inside it.
            var max = floorSeconds - i * SubFloorSweepBandSeconds;
            var min = floorSeconds - (i + 1) * SubFloorSweepBandSeconds;
            if (min < storedFloorSeconds)
                break;
            bands.Add((min, max));
        }
        return bands;
    }

    /// <summary>
    /// Groups a --verify run's marking outcomes into the regions <see cref="ChapterDetector.DetectGapsAsync"/>
    /// re-probes: one <see cref="DetectionRegion"/> per run of consecutive unconfirmed markings
    /// (a single unconfirmed marking is its own run of one), bounded below by the nearest
    /// preceding marking and above by the nearest following one - confirmed or not, since an
    /// unparseable-title marking (<see cref="VerifyMarkingOutcome.ExpectedNumber"/> null) carries
    /// no boundary information and is skipped entirely rather than breaking a run. A run reaching
    /// the last checkable marking has no following bound; it becomes the trailing target instead
    /// of a region with a null <see cref="DetectionRegion.UpperNumber"/> precisely because there
    /// is no generic mechanism (unlike <see cref="FindGaps"/>'s interior gaps, safety-netted by
    /// the existing Pass 3 tail regardless of how <c>chapters</c> was seeded) that would otherwise
    /// notice a still-missing trailing chapter - nothing bounds it from above to compare against.
    /// Internal for unit testing.
    /// </summary>
    /// <param name="markings">A --verify run's per-marking outcomes, in file order (see
    /// <see cref="VerifyResult.Markings"/>).</param>
    /// <param name="duration">Total play time; both a trailing region's and the trailing target's
    /// own upper bound.</param>
    internal static GapRecoveryPlan BuildGapRegions(IReadOnlyList<VerifyMarkingOutcome> markings, double duration)
    {
        var checkable = markings.Where(m => m.ExpectedNumber is not null)
            .OrderBy(m => m.StartSeconds).ToList();
        var regions = new List<DetectionRegion>();
        double? trailingFrom = null;
        var trailingTargets = new List<int>();
        for (var i = 0; i < checkable.Count; i++)
        {
            if (checkable[i].Confirmed)
                continue;
            // Not the start of a run when the previous checkable marking is itself unconfirmed -
            // this index was already folded into that earlier run below.
            if (i > 0 && !checkable[i - 1].Confirmed)
                continue;

            var runEnd = i;
            while (runEnd + 1 < checkable.Count && !checkable[runEnd + 1].Confirmed)
                runEnd++;

            var isTrailing = runEnd + 1 >= checkable.Count;
            var from = i > 0 ? checkable[i - 1].StartSeconds : 0.0;
            var lower = i > 0 ? checkable[i - 1].ExpectedNumber!.Value : 0;
            var to = isTrailing ? duration : checkable[runEnd + 1].StartSeconds;
            var upper = isTrailing ? (int?)null : checkable[runEnd + 1].ExpectedNumber!.Value;
            // The trailing run also gets an ordinary Pass 2 region (cheap silence/jingle probing
            // may well find it, exactly like an interior gap); trailingFrom/trailingTargets exist
            // purely so DetectCoreAsync can still add a Pass 3 fallback for whatever that probing
            // does not find, since - see the remarks above - nothing else would notice.
            regions.Add(new DetectionRegion(from, to, lower, upper));
            if (isTrailing)
            {
                trailingFrom = from;
                for (var k = i; k <= runEnd; k++)
                    trailingTargets.Add(checkable[k].ExpectedNumber!.Value);
            }
            i = runEnd;
        }
        return new GapRecoveryPlan(regions, trailingFrom, trailingTargets);
    }

    /// <summary>
    /// Sorts detections chronologically and reduces them to the largest subset whose numbers still
    /// ascend with time: duplicates of one number (the earliest survives) and out-of-order
    /// regressions - typically in-text mentions like "as seen in chapter three" - fall out.
    /// Internal for unit testing.
    /// <para>
    /// <em>Largest</em> subset rather than a greedy left-to-right scan, and the difference is not
    /// academic. A greedy scan keeps whatever it meets first and measures the rest of the file
    /// against it, so one number misheard <em>upwards</em> takes the whole remainder of the book
    /// with it. On "Die Cyber-Brutzellen" (2026-08-01) chapter 14's announcement at 7:01:30 was read
    /// as 40, and chapters 15 through 29 - every one of them detected, refined and correctly placed
    /// - then failed "greater than 40" one after another and were discarded: a 17 h audiobook came
    /// out marked as far as 6:21 and no further. Fifteen mutually corroborating chapters losing to a
    /// single outlier is the wrong way round. The longest strictly ascending subsequence drops the
    /// outlier and keeps the fifteen, which bounds what any one mishearing can cost at its own mark
    /// - and <see cref="ChapterDetector.RepairSequenceOutliersAsync"/> then tries to win that back too.
    /// </para>
    /// <para>
    /// Ties are settled in two steps. Between two equally long readings, the one whose last chapter
    /// number is lower wins - it claims fewer chapters exist, and chapter numbers ascend one at a
    /// time, so the denser reading is the likelier one. Between two of <em>those</em>, the earlier
    /// wins, which is what preserves the "of two detections of one chapter, keep the earlier" rule:
    /// overlapping probe windows hearing one announcement yield equally long subsequences, and the
    /// earlier detection is the one that saw the announcement rather than its tail. Confidence is
    /// deliberately not part of the objective - a re-hearing reported more confidently is no
    /// evidence of a better <em>position</em>, and letting it win would move marks that nothing has
    /// shown to be wrong.
    /// </para>
    /// </summary>
    /// <param name="found">The raw detections, in any order.</param>
    internal static List<DetectedChapter> Normalize(List<DetectedChapter> found)
        => NormalizeWithOutliers(found).Kept;

    /// <summary>
    /// <see cref="Normalize"/>'s full answer: the chapters it keeps, plus the ones it had to drop to
    /// make the sequence ascend. Separate because only <see cref="ChapterDetector.RepairSequenceOutliersAsync"/>
    /// wants the second half, and every other caller reads better without it.
    /// </summary>
    /// <param name="found">The raw detections, in any order.</param>
    /// <returns>The ascending subset in chronological order, and the discarded entries in the same
    /// order.</returns>
    internal static (List<DetectedChapter> Kept, List<DetectedChapter> Dropped) NormalizeWithOutliers(
        List<DetectedChapter> found)
    {
        var ordered = found.OrderBy(c => c.TimeSeconds).ThenBy(c => c.Number).ToList();
        if (ordered.Count == 0)
            return ([], []);

        // Textbook O(n^2) longest-increasing-subsequence DP. The O(n log n) formulation would not
        // pay here - a book has tens of chapters, not millions - and this one reconstructs the
        // actual subsequence without the extra bookkeeping that one needs.
        var runLength = new int[ordered.Count];
        var predecessor = new int[ordered.Count];
        var best = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            runLength[i] = 1;
            predecessor[i] = -1;
            for (var j = 0; j < i; j++)
            {
                // Strictly longer, never merely as long: the first predecessor reaching a given
                // length wins, which is what makes ties resolve toward the earliest entries.
                if (ordered[j].Number < ordered[i].Number && runLength[j] + 1 > runLength[i])
                {
                    runLength[i] = runLength[j] + 1;
                    predecessor[i] = j;
                }
            }
            // Longest wins; between equally long readings the one claiming the fewest chapters
            // exist does, and between two of those the earlier one. Ending the search at the first
            // maximum instead - which is what "earliest wins" naively means - would truncate the
            // sequence at an outlier: [13, 40, 16] has two readings of length two, and the one
            // ending at 40 is the earlier of them.
            if (runLength[i] > runLength[best] ||
                (runLength[i] == runLength[best] && ordered[i].Number < ordered[best].Number))
                best = i;
        }

        var keptIndices = new HashSet<int>();
        for (var i = best; i >= 0; i = predecessor[i])
            keptIndices.Add(i);

        var kept = new List<DetectedChapter>(keptIndices.Count);
        var dropped = new List<DetectedChapter>(ordered.Count - keptIndices.Count);
        for (var i = 0; i < ordered.Count; i++)
            (keptIndices.Contains(i) ? kept : dropped).Add(ordered[i]);
        return (kept, dropped);
    }

    /// <summary>
    /// Finds the silence that truly precedes a matched phrase, independent of which candidate
    /// silence triggered the probe. A probe window can span the previous chapter's trailing speech,
    /// an unrelated in-text pause long enough to pass --min-silence-length itself, the real
    /// inter-chapter silence, the jingle (with the VAD pre-pass) and finally the phrase - so
    /// trusting the triggering silence would anchor the --mark-before-jingle position and the
    /// --min-silence-length/--max-jingle-length auto mechanisms to the wrong, earlier one. Returns
    /// null when no silence lies between the window start and the phrase, meaning the triggering
    /// silence (ending exactly at windowStart) was the real one after all.
    /// </summary>
    /// <param name="windowStart">Absolute start of the probe window (or of the lookback range)
    /// in seconds.</param>
    /// <param name="phraseAbsSeconds">Absolute phrase start in seconds.</param>
    /// <param name="silences">The silences to search - callers pass the full stored list
    /// (every silence down to <see cref="MinStoredSilenceSeconds"/>).</param>
    internal static Silence? FindRealAnchorSilence(double windowStart, double phraseAbsSeconds, List<Silence> silences)
    {
        var silence = silences.LastOrDefault(s => s.EndSeconds > windowStart && s.EndSeconds <= phraseAbsSeconds);
        return silence == default ? null : silence;
    }

    /// <summary>
    /// Finds where to cut between two adjacent Pass 2 probe windows so the seam never falls
    /// mid-word: the mid-point of the nearest qualifying silence, falling back to a VAD
    /// non-speech region under the same rules when the VAD pre-pass ran and no silence qualifies, and
    /// finally to the border itself (no snap) when neither exists - which almost certainly
    /// means there is no chapter transition near the border to begin with, so a mid-word cut
    /// there is not a real risk. A candidate target's mid-point must lie inside window 2 -
    /// strictly after <paramref name="windowStart"/>, and before <paramref name="windowEnd"/>
    /// (inclusive at planning time, where a seam at window 2's very end just means window 1
    /// swallows it whole; strict at reuse time, so the fresh tail decode is never empty).
    /// <para>
    /// Two callers with different rules, selected via <paramref name="allowBeyondBorder"/>.
    /// <see cref="PlanWindowEnd"/> (true) plans window 1's end before it is decoded, so the seam may
    /// go anywhere within window 2 - window 1's decode is extended or shortened to end exactly
    /// there. The reuse-time call inside a probe (false) runs after window 1 is decoded: everything
    /// left of the seam comes from its cached transcript, which cannot be extended retroactively, so
    /// the target must <em>start</em> at or before the border. One merely straddling the border is
    /// fine (the stretch past it is inside the silence, so no speech is lost), but one entirely
    /// beyond it would leave [border, seam) in neither transcript. The border normally <em>is</em>
    /// window 1's planned seam, which the restricted search re-finds at distance zero; it only
    /// genuinely decides for overlaps that plan did not anticipate (a probe-window resize since).
    /// </para>
    /// </summary>
    /// <param name="windowStart">Start of window 2 (the later window's candidate start).</param>
    /// <param name="border">The unsnapped border - window 1's (planned or decoded) end.</param>
    /// <param name="windowEnd">End of window 2.</param>
    /// <param name="allSilences">Every silence Pass 1 found, down to <see
    /// cref="MinStoredSilenceSeconds"/> - not just the ones at or above --min-silence-length.</param>
    /// <param name="nonSpeechRegions">VAD non-speech regions; empty when the VAD pre-pass did not run.</param>
    /// <param name="jingle">True when the VAD pre-pass ran (VAD non-speech regions are
    /// populated), enabling the VAD region fallback.</param>
    /// <param name="allowBeyondBorder">True at planning time (the border itself moves to the
    /// seam); false at reuse time (the cache ends at the border, see above).</param>
    internal static double FindOverlapSplitPoint(
        double windowStart, double border, double windowEnd,
        List<Silence> allSilences, List<NonSpeechRegion> nonSpeechRegions, bool jingle,
        bool allowBeyondBorder)
    {
        // At planning time a seam exactly at windowEnd is allowed: window 1 then swallows
        // window 2 whole, and window 2 is served entirely from its cache. At reuse time the
        // bound stays strict so the fresh tail decode [seam, windowEnd) can never be empty.
        return FindNearestSeam(border, windowStart, windowEnd,
            upperInclusive: allowBeyondBorder,
            targetStartAtOrBefore: allowBeyondBorder ? null : border,
            allSilences, nonSpeechRegions, jingle) ?? border;
    }

    /// <summary>
    /// The nearest word-safe seam to <paramref name="border"/>: the mid-point of a silence -
    /// or, when the VAD pre-pass ran, of a VAD non-speech region when no silence qualifies - within
    /// (<paramref name="earliestExclusive"/>, <paramref name="latestInclusive"/>], or null when
    /// neither kind of target has its mid-point in that range. No word straddles the mid-point
    /// of a silence, which is what makes it the safest place to cut audio that is transcribed
    /// in separate pieces. The single seam search behind every border decision in the
    /// pipeline: Pass 2's shared-border and stand-alone window-end snaps
    /// (<see cref="PlanWindowEnd"/>), the reuse-time split (both via
    /// <see cref="FindOverlapSplitPoint"/>), and Pass 3's chunk borders
    /// (<see cref="ChapterDetector.TranscribeRegionAsync"/>).
    /// </summary>
    /// <param name="border">The unsnapped border the seam should stay closest to.</param>
    /// <param name="earliestExclusive">Lower bound (exclusive) for the seam.</param>
    /// <param name="latestInclusive">Upper bound for the seam; inclusive when
    /// <paramref name="upperInclusive"/>, exclusive otherwise.</param>
    /// <param name="upperInclusive">Whether a seam exactly at <paramref name="latestInclusive"/>
    /// is acceptable (see <see cref="FindOverlapSplitPoint"/> for the one caller that must
    /// keep the bound strict).</param>
    /// <param name="targetStartAtOrBefore">When set, only targets that <em>start</em> at or
    /// before this position qualify - the reuse-time restriction, where everything left of the
    /// seam must already be covered by a cached transcript.</param>
    /// <param name="allSilences">Every silence Pass 1 stored, down to
    /// <see cref="MinStoredSilenceSeconds"/>.</param>
    /// <param name="nonSpeechRegions">VAD non-speech regions; empty when the VAD pre-pass did not run.</param>
    /// <param name="jingle">True when the VAD pre-pass ran (VAD non-speech regions are
    /// populated), enabling the VAD region fallback.</param>
    internal static double? FindNearestSeam(
        double border, double earliestExclusive, double latestInclusive, bool upperInclusive,
        double? targetStartAtOrBefore,
        List<Silence> allSilences, List<NonSpeechRegion> nonSpeechRegions, bool jingle)
    {
        double? Nearest(IEnumerable<(double Start, double End)> targets) => targets
            .Where(t => targetStartAtOrBefore is not { } cap || t.Start <= cap)
            .Select(t => (double?)((t.Start + t.End) / 2))
            .Where(mid => mid > earliestExclusive &&
                          (upperInclusive ? mid <= latestInclusive : mid < latestInclusive))
            .OrderBy(mid => Math.Abs(mid!.Value - border))
            .FirstOrDefault();

        var seam = Nearest(allSilences.Select(s => (s.StartSeconds, s.EndSeconds)));
        if (seam is null && jingle)
            seam = Nearest(nonSpeechRegions.Select(r => (r.StartSeconds, r.EndSeconds)));
        return seam;
    }

    /// <summary>
    /// Plans a single Pass 2 probe window's end, called right before that window is probed -
    /// on the fly, so the end always reflects the <paramref name="probeSeconds"/> in effect at
    /// that moment (the adaptive --max-jingle-length resizes apply to the very next window,
    /// with no pre-computed bulk plan to go stale). The window naturally spans
    /// <paramref name="probeSeconds"/> from its candidate start (clamped to the file end), but
    /// when the next candidate's window overlaps it, their shared border is snapped to the
    /// nearest silence (or, with the VAD pre-pass, VAD non-speech region) mid-point anywhere within
    /// that next window's natural span - see <see cref="FindOverlapSplitPoint"/> - and this window's
    /// decode ends exactly there, before or beyond its natural end. The next probe's fresh decode
    /// starts at that same seam (its cached-transcript reuse re-finds it as the cache's end), so
    /// consecutive decodes stitch together word-safely at a mid-silence cut, with no dead
    /// (never-transcribed) stretch and no re-decoded overlap. A raw-border joint remains only where
    /// the next window holds no snap target at all - and no silence there means no chapter
    /// transition near the border either, so a mid-word cut costs nothing.
    /// <para>
    /// A window end that does <em>not</em> lie inside the next window (stand-alone windows,
    /// the last window, and a next window fully contained in this one) is snapped too, in a
    /// more limited way: to the nearest seam within <see cref="WindowEndSnapSearchSeconds"/>
    /// <em>after</em> the natural end (extension only), so even an isolated window's decode
    /// stops at a word-safe cut. Without a target in reach it keeps its natural length.
    /// Internal for unit testing.
    /// </para>
    /// </summary>
    /// <param name="start">This window's candidate start.</param>
    /// <param name="nextStart">The next candidate's start, or null for the last window.</param>
    /// <param name="probeSeconds">Current probe window length in seconds.</param>
    /// <param name="durationSeconds">Total play time; window ends are clamped to it.</param>
    /// <param name="allSilences">Every silence Pass 1 found, down to <see
    /// cref="MinStoredSilenceSeconds"/>.</param>
    /// <param name="nonSpeechRegions">VAD non-speech regions; empty when the VAD pre-pass did not run.</param>
    /// <param name="jingle">True when the VAD pre-pass ran (VAD non-speech regions are
    /// populated), enabling the VAD region fallback.</param>
    internal static double PlanWindowEnd(
        double start, double? nextStart, double probeSeconds, double durationSeconds,
        List<Silence> allSilences, List<NonSpeechRegion> nonSpeechRegions, bool jingle)
    {
        var naturalEnd = Math.Min(start + probeSeconds, durationSeconds);
        if (nextStart is { } ns && ns < naturalEnd)
        {
            var nextNaturalEnd = Math.Min(ns + probeSeconds, durationSeconds);
            if (nextNaturalEnd > naturalEnd)
            {
                // Shared border inside the next window: snap it to a seam anywhere in there.
                var seam = FindOverlapSplitPoint(ns, naturalEnd, nextNaturalEnd,
                    allSilences, nonSpeechRegions, jingle, allowBeyondBorder: true);
                return seam > start ? seam : naturalEnd;
            }
            // The next window ends at or before this one's natural end (possible near the
            // file end): no shared border to snap - it will be served wholesale from this
            // window's cached transcript instead; fall through to the stand-alone snap.
        }

        // Stand-alone end: extend to the nearest seam within the short forward search so the
        // decode never stops mid-word (see WindowEndSnapSearchSeconds). Should this reach past
        // the next window's start, the reuse-time split simply re-finds the very same seam as
        // the cache's end - a clean stitch either way.
        return FindNearestSeam(naturalEnd, naturalEnd,
            Math.Min(naturalEnd + WindowEndSnapSearchSeconds, durationSeconds),
            upperInclusive: true, targetStartAtOrBefore: null,
            allSilences, nonSpeechRegions, jingle) ?? naturalEnd;
    }

    /// <summary>
    /// Computes the chapter numbers shown in the progress bar and in detection log lines from a
    /// detection list: the highest chapter number found so far, and which numbers below it are
    /// still undetected (the gaps Pass 3 would have to chase). Runs the input through
    /// <see cref="Normalize"/> first so in-text mentions of earlier chapters (regressions that
    /// Normalize drops anyway) cannot make a genuinely missing chapter look found. Mirrors
    /// <see cref="ChapterDetector.BuildDetectionResult"/>'s leading-gap rule: without
    /// <paramref name="expectedStartChapter"/>, nothing below the lowest number actually found is
    /// "missing" - a split-book part starting at chapter 2 must not report chapter 1 as missing.
    /// The ceiling comes from the corroborated numbers only, so one number nothing could vouch for
    /// cannot declare everything under it missing (see
    /// <see cref="DetectedChapter.NumberUnverified"/>). Internal for unit testing.
    /// </summary>
    /// <param name="found">The chapters detected so far.</param>
    /// <param name="expectedStartChapter">The chapter number the book is expected to start at
    /// (<see cref="CliOptions.ExpectedStartChapter"/>), or null for no expectation.</param>
    internal static (int Highest, List<int> Missing) ChapterProgress(
        IEnumerable<DetectedChapter> found, int? expectedStartChapter = null)
    {
        var kept = Normalize(found.ToList());
        var numbers = kept.Select(c => c.Number).ToHashSet();
        if (numbers.Count == 0)
            return (0, []);
        // Only a corroborated number gets to say how far the book runs. An unverified one still
        // counts as present - it is a mark like any other - but the stretch below it is not
        // reported missing, which is what keeps a spoken year from declaring two thousand chapters
        // lost (see DetectedChapter.NumberUnverified).
        var vouched = kept.Where(c => !c.NumberUnverified).Select(c => c.Number).ToList();
        // Nothing corroborated at all leaves no span to be missing from, so nothing is: the highest
        // number is still reported, since it names a mark that really was written, but a lone
        // uncorroborated 2179 must not answer "how much of this book is still missing?" with 2114.
        // Reachable on a clip or a short file - Pass 2 finding exactly one chapter and that one in
        // doubt - rather than on a whole book.
        if (vouched.Count == 0)
            return (numbers.Max(), []);
        var highest = vouched.Max();
        // Clamped to highest: the first chapter found can transiently be numbered below
        // expectedStartChapter for one call, right before ChapterDetector's own "below
        // expectation" check aborts the run - without the clamp that would make the range
        // below negative-length and throw.
        var lowest = Math.Min(highest, expectedStartChapter ?? numbers.Min());
        var missing = Enumerable.Range(lowest, highest - lowest + 1).Where(n => !numbers.Contains(n)).ToList();
        return (highest, missing);
    }
}
