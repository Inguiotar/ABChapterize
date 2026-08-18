// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using ABChapterize.Cli;
using ABChapterize.Language;
using static ABChapterize.Detection.DetectionTuning;

namespace ABChapterize.Detection;

/// <summary>Sequence bookkeeping for chapter detection: finding gaps in a detected chapter
/// sequence, the regions a --verify re-detection run needs to re-probe, the silence bands Re-probe
/// sweeps a still-open gap with, normalizing a raw detection list, and snapping Probe/Scan
/// window/chunk borders to word-safe seams.</summary>
internal static class GapPlanning
{
    /// <summary>A time region suspected to contain undetected chapter starts.</summary>
    /// <param name="FromSeconds">Region start.</param>
    /// <param name="ToSeconds">Region end.</param>
    /// <param name="Sequence">Which chapter sequence the missing numbers belong to (see
    /// <see cref="DetectedChapter.Sequence"/>); 0 for every gap of an ordinary book. A gap never
    /// straddles a restart - <see cref="FindGaps"/> raises one across a part boundary only for the
    /// head of the <em>new</em> part - so a single sequence always answers for the whole of it.</param>
    internal readonly record struct GapRegion(double FromSeconds, double ToSeconds, int Sequence = 0);

    /// <summary>
    /// The number a chapter sequence is expected to begin at, or null when nothing may be assumed.
    /// <para>
    /// The file's own first sequence keeps the rule <see cref="FindGaps"/> has always applied: only
    /// <c>--expected-start-chapter</c> (or, through <see cref="ExpectedStartFor"/>, a detected
    /// prologue) may say a chapter is missing below the first one found, because a low first number
    /// is ambiguous - a split-book part legitimately starts at chapter 12.
    /// </para>
    /// <para>
    /// Every sequence after a restart is unambiguous, and answers 1. Counting from 1 again is what
    /// a restart <em>is</em> - it is the evidence <see cref="RegionProber"/> requires before opening
    /// a new sequence at all - so a part whose lowest detected number is 3 is a part missing its
    /// first two chapters, not a part that begins at 3.
    /// </para>
    /// </summary>
    /// <param name="sequence">The 0-based sequence in question.</param>
    /// <param name="expectedStartChapter">--expected-start-chapter, or null for no expectation.</param>
    internal static int? StartOfSequence(int sequence, int? expectedStartChapter)
        => sequence == 0 ? expectedStartChapter : 1;

    /// <summary>
    /// Splits a chapter list into its sequences, ascending by sequence and, within each, by time -
    /// one group holding everything for the ordinary book. The unit almost every rule below works
    /// in: "does the numbering ascend", "which numbers are missing" and "what does this hole hold"
    /// are all questions about one part of a book, and asking them of a list spanning a restart
    /// answers about a numbering that jumps backwards.
    /// </summary>
    /// <param name="chapters">The chapters to split, in any order.</param>
    internal static List<List<DetectedChapter>> BySequence(IEnumerable<DetectedChapter> chapters)
        => [.. chapters
            .GroupBy(c => c.Sequence)
            .OrderBy(g => g.Key)
            .Select(g => g.OrderBy(c => c.TimeSeconds).ToList())];

    /// <summary>One region <see cref="ChapterDetector.DetectCoreAsync"/> runs its own, independent Probe pass
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
    /// when nothing precedes it (a from-file-start region). Seeds <see cref="RegionProber"/>'s
    /// running "last accepted number", which does two jobs: a match must exceed it to be accepted at
    /// all, and it supplies the lower half of <see cref="RegionProber.SequenceBounds"/> until this
    /// region has a chapter of its own. A non-zero value also tells the --min-silence-length auto
    /// tightening (<c>RegionProber</c>'s <c>TightenThreshold</c>) that this region's very first mark
    /// already has a chapter in front of it, so its anchor silence is a real inter-chapter break
    /// rather than the front-matter transition a whole-file region has to skip.</param>
    /// <param name="UpperNumber">The chapter number already confirmed to follow this region, or
    /// null when nothing does (this region reaches to the file end). A match at or above it is
    /// rejected outright - guarding against a snapped probe window spilling into the next known
    /// chapter's own announcement and displacing it.</param>
    /// <param name="Sequence">Which of the file's chapter sequences this region lies in (see
    /// <see cref="DetectedChapter.Sequence"/>); 0 for every region of an ordinary book. Both
    /// numbers above are that sequence's own, so a recovery region inside part 2 hunts part 2's
    /// numbering, and anything it finds is stamped with this rather than falling back into part 1.
    /// A region never straddles a part boundary: every one of them is derived from a gap, and
    /// <see cref="FindGaps"/> opens none across a restart.</param>
    internal readonly record struct DetectionRegion(
        double FromSeconds, double ToSeconds, int LowerNumber, int? UpperNumber, int Sequence = 0);

    /// <summary>The regions and, when the last checkable mark in file order is unconfirmed, the
    /// trailing recovery target <see cref="ChapterDetector.DetectCoreAsync"/> needs - see <see
    /// cref="BuildGapRegions"/>.</summary>
    /// <param name="Regions">One region per run of consecutive unconfirmed marks.</param>
    /// <param name="TrailingFrom">Start of the trailing region (the last confirmed/file-start
    /// point before it), or null when the file's last checkable mark was confirmed.</param>
    /// <param name="TrailingTargets">The expected numbers of the unconfirmed marks in the
    /// trailing run, in file order; empty when <paramref name="TrailingFrom"/> is null. Unlike an
    /// interior region's <see cref="DetectionRegion.UpperNumber"/>-bounded range, these are taken
    /// verbatim from the marks themselves since there is no following confirmed chapter to
    /// derive a contiguous range from.</param>
    internal readonly record struct GapRecoveryPlan(
        List<DetectionRegion> Regions, double? TrailingFrom, List<int> TrailingTargets);

    /// <summary>
    /// The chapter number this file's sequence is expected to start at, which is what opens a
    /// leading gap at all (see <see cref="FindGaps"/>): <c>--expected-start-chapter</c> when the
    /// user named one, else 1 once a prologue has been detected, else null - no expectation, and
    /// whatever number Probe found first is the book's start.
    /// <para>
    /// A prologue is the one piece of evidence detection can produce for itself that this file
    /// holds the beginning of a book. <see cref="FindGaps"/> refuses to assume "1" precisely because
    /// a low first number is ambiguous - a split-book part legitimately starts at chapter 12, and
    /// this tool is pointed at such files routinely - but a part that starts at chapter 12 does not
    /// carry the book's prologue. So the mark that resolves the ambiguity is the same mark whose
    /// absence leaves it open, and the assumption is only made where it has been earned.
    /// </para>
    /// <para>
    /// Not applied to <c>--expected-start-chapter</c>'s <em>abort</em> half
    /// (<see cref="RegionProber.IsBelowExpectedStart"/>, fed from
    /// <see cref="ProbeContext.ExpectedStartChapter"/>): that gives up on a whole file, and a book
    /// numbering its opening section "chapter 0" would then be abandoned over a prologue rather than
    /// over anything the user asked for. What this value earns is the right to hunt the chapters
    /// under the first one found, never the right to stop.
    /// </para>
    /// </summary>
    /// <param name="options">The run's options, for the explicit expectation and for
    /// <see cref="CliOptions.IgnoreChapterNumbers"/>, under which there is no sequence to start.</param>
    /// <param name="named">The named marks known so far - the prologue among them, if it was
    /// found.</param>
    internal static int? ExpectedStartFor(CliOptions options, IReadOnlyList<DetectedMark> named)
        => options.ExpectedStartChapter ??
           (!options.IgnoreChapterNumbers && named.Any(m => m.Kind == NamedPhrase.PrologueKind)
               ? 1
               : null);

    /// <summary>
    /// Determines the regions to fully transcribe: between every pair of consecutive detected
    /// chapters whose numbers are not consecutive, and - only when <paramref
    /// name="expectedStartChapter"/> is given - before the first chapter when its number is
    /// greater than that expectation. Without it, whatever number Probe found first is trusted
    /// outright and no leading gap is ever raised, even when that number is not 1: a plain
    /// from-scratch run has no way to know whether a low first number is really the book's start
    /// or just Probe missing an earlier chapter, and guessing "1" wrongly assumed the latter for
    /// every book, including legitimate split-book parts. Internal for unit testing.
    /// <para>
    /// A restart is the one place where consecutive chapters may legitimately run backwards, and it
    /// is not a hole: part 2's chapter 1 follows part 1's chapter 15 with nothing missing between
    /// them, and a gap raised there would send Scan across the whole of part 1 hunting numbers
    /// that were never spoken. What the boundary can still hide is the <em>head</em> of the new
    /// part, so it raises a gap for exactly that - and only when the new part does not already
    /// start at 1 (see <see cref="StartOfSequence"/>).
    /// </para>
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
            gaps.Add(new GapRegion(0, chapters[0].TimeSeconds, chapters[0].Sequence));
        for (var i = 1; i < chapters.Count; i++)
        {
            // Skipping the entry rather than filtering it out of the list keeps its neighbours from
            // being paired across it: an unverified number is still a real position in the book, it
            // just may not be the far end of a hole (see DetectedChapter.NumberUnverified).
            if (chapters[i].NumberUnverified)
                continue;
            var expectedHere = chapters[i].Sequence != chapters[i - 1].Sequence
                ? StartOfSequence(chapters[i].Sequence, expectedStartChapter) ?? chapters[i].Number
                : chapters[i - 1].Number + 1;
            if (chapters[i].Number > expectedHere)
                gaps.Add(new GapRegion(
                    chapters[i - 1].TimeSeconds, chapters[i].TimeSeconds, chapters[i].Sequence));
        }
        return gaps;
    }

    /// <summary>
    /// The chapter numbers a gap is expected to recover: every number strictly between the
    /// detected chapters bounding it, or <paramref name="expectedStartChapter"/> (1 when null) up
    /// to the first detected number for a leading gap (whose start is 0, before any chapter). The
    /// bounding chapters are located by their exact timestamps, which <see cref="FindGaps"/>
    /// copied verbatim into the gap, so the float match is exact. Scan uses this to stop
    /// transcribing a gap the moment all of them are found. Internal for unit testing.
    /// <para>
    /// A gap opened by a restart is bounded below by the previous part's last chapter, which says
    /// nothing at all about how far into the new part its far end sits - so it is treated exactly
    /// like a leading gap, and the range runs from where the new sequence is expected to begin.
    /// </para>
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
        // DetectedChapter (Number 0), the signal to fall back to the sequence's expected start
        // (or 1, when there is no expectation) so the set becomes expectedStart..upper-1.
        var boundChapter = chapters.FirstOrDefault(c => c.TimeSeconds == gap.FromSeconds);
        var lower = boundChapter.Number != 0 && boundChapter.Sequence == gap.Sequence
            ? boundChapter.Number
            : (StartOfSequence(gap.Sequence, expectedStartChapter) ?? 1) - 1;
        var missing = new List<int>();
        for (var n = lower + 1; n < upper; n++)
            missing.Add(n);
        return missing;
    }

    /// <summary>
    /// How many <see cref="DetectionTuning.WhisperChunkSeconds"/> decode windows a stretch of audio
    /// of this length costs to recognize. Rounded up because a partial window is a whole one to the
    /// recognizer, which is exactly why this is the unit a short probe and a long transcription can
    /// be compared in at all.
    /// </summary>
    /// <param name="seconds">Length of the stretch.</param>
    internal static int ChunkWindows(double seconds)
        => (int)Math.Ceiling(seconds / WhisperChunkSeconds);

    /// <summary>
    /// What any recovery pass may spend probing one gap, in decode windows:
    /// <see cref="DetectionTuning.SubFloorSweepBudgetFraction"/> of what transcribing that gap
    /// outright would cost. Every pass that probes a gap on spec shares this one bound, so that
    /// probing stays the cheaper bet even when it finds nothing and the transcription runs anyway -
    /// the shape it exists to prevent being on record from a 56-minute gap that re-probed for 40
    /// minutes and recovered nothing.
    /// </summary>
    /// <param name="gapSeconds">Length of the gap being probed.</param>
    internal static double GapProbeBudget(double gapSeconds)
        => SubFloorSweepBudgetFraction * ChunkWindows(gapSeconds);

    /// <summary>
    /// The silence-length bands Re-probe sweeps a still-open gap with, longest first: one band per
    /// <see cref="DetectionTuning.SubFloorSweepBandCount"/>, each
    /// <see cref="DetectionTuning.SubFloorSweepBandSeconds"/> wide, the first ending exactly at
    /// <paramref name="floorSeconds"/> so no silence the run's own demand admitted is swept again.
    /// <para>
    /// "The run's demand" rather than "everything Probe could have reached": under
    /// <c>--min-silence-length auto</c> the threshold can talk itself down to
    /// <see cref="DetectionTuning.AdaptiveSilenceFloorSeconds"/>, so on a book whose breaks are
    /// short some of these bands were within Probe's reach after all. Sweeping them anyway is not
    /// waste - Re-probe probes on the <c>--upgrade-model</c> recognizer, so a second look at the same
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
    /// Analyze never kept silences that short, so sweeping them would report "nothing found" about
    /// audio nothing ever looked at. Internal for unit testing.
    /// </para>
    /// </summary>
    /// <param name="floorSeconds">The run's --min-silence-length, i.e. the shortest silence it
    /// opened by demanding.</param>
    /// <param name="storedFloorSeconds">The shortest silence Analyze retained at all
    /// (<see cref="DetectionTuning.MinStoredSilenceSeconds"/>, or the floor when that is lower).</param>
    /// <returns>The bands as half-open [min, max) intervals, longest first; empty when the floor
    /// already sits at or below what Analyze stored.</returns>
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
    /// Groups a --verify run's mark outcomes into the regions <see cref="ChapterDetector.DetectGapsAsync"/>
    /// re-probes: one <see cref="DetectionRegion"/> per run of consecutive unconfirmed marks
    /// (a single unconfirmed mark is its own run of one), bounded below by the nearest
    /// preceding mark and above by the nearest following one - confirmed or not, since an
    /// unparseable-title mark (<see cref="ExistingMarkOutcome.ExpectedNumber"/> null) carries
    /// no boundary information and is skipped entirely rather than breaking a run. A run reaching
    /// the last checkable mark has no following bound; it becomes the trailing target instead
    /// of a region with a null <see cref="DetectionRegion.UpperNumber"/> precisely because there
    /// is no generic mechanism (unlike <see cref="FindGaps"/>'s interior gaps, safety-netted by
    /// the existing Scan tail regardless of how <c>chapters</c> was seeded) that would otherwise
    /// notice a still-missing trailing chapter - nothing bounds it from above to compare against.
    /// Internal for unit testing.
    /// </summary>
    /// <param name="outcomes">A --verify run's per-mark outcomes, in file order (see
    /// <see cref="VerifyResult.Outcomes"/>).</param>
    /// <param name="duration">Total play time; both a trailing region's and the trailing target's
    /// own upper bound.</param>
    internal static GapRecoveryPlan BuildGapRegions(IReadOnlyList<ExistingMarkOutcome> outcomes, double duration)
    {
        var checkable = outcomes.Where(m => m.ExpectedNumber is not null)
            .OrderBy(m => m.StartSeconds).ToList();
        var regions = new List<DetectionRegion>();
        double? trailingFrom = null;
        var trailingTargets = new List<int>();
        for (var i = 0; i < checkable.Count; i++)
        {
            if (checkable[i].Confirmed)
                continue;
            // Not the start of a run when the previous checkable mark is itself unconfirmed and
            // belongs to the same part - this index was already folded into that earlier run below.
            // A restart always breaks the run, however many unconfirmed marks meet across it:
            // one region cannot hunt two numberings at once.
            if (i > 0 && !checkable[i - 1].Confirmed && checkable[i - 1].Sequence == checkable[i].Sequence)
                continue;

            var sequence = checkable[i].Sequence;
            var runEnd = i;
            while (runEnd + 1 < checkable.Count && !checkable[runEnd + 1].Confirmed &&
                   checkable[runEnd + 1].Sequence == sequence)
                runEnd++;

            var isTrailing = runEnd + 1 >= checkable.Count;
            var from = i > 0 ? checkable[i - 1].StartSeconds : 0.0;
            var to = isTrailing ? duration : checkable[runEnd + 1].StartSeconds;
            // The audio bounds above take the nearest mark either way, so no stretch is left
            // unprobed; the number bounds may only come from this part, since a mark on the far
            // side of a restart says nothing at all about how this part's numbering runs.
            var lower = i > 0 && checkable[i - 1].Sequence == sequence
                ? checkable[i - 1].ExpectedNumber!.Value
                : 0;
            var upper = isTrailing || checkable[runEnd + 1].Sequence != sequence
                ? (int?)null
                : checkable[runEnd + 1].ExpectedNumber!.Value;
            // The trailing run also gets an ordinary Probe region (cheap silence/jingle probing
            // may well find it, exactly like an interior gap); trailingFrom/trailingTargets exist
            // purely so DetectCoreAsync can still add a Scan fallback for whatever that probing
            // does not find, since - see the remarks above - nothing else would notice.
            regions.Add(new DetectionRegion(from, to, lower, upper, sequence));
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
        // One sequence at a time, because "ascending" only means anything inside a part: run the
        // subsequence search across a restart and it would keep whichever part is longer and throw
        // the other away wholesale, which is the exact failure this feature exists to end.
        if (found.Any(c => c.Sequence != 0))
        {
            var keptAll = new List<DetectedChapter>();
            var droppedAll = new List<DetectedChapter>();
            foreach (var sequence in BySequence(found))
            {
                var (kept, dropped) = NormalizeOneSequence(sequence);
                keptAll.AddRange(kept);
                droppedAll.AddRange(dropped);
            }
            // Sequences are contiguous in time, so this only ever re-sorts within a part - but a
            // caller reading position off the list order must not have to know that.
            return ([.. keptAll.OrderBy(c => c.TimeSeconds)],
                    [.. droppedAll.OrderBy(c => c.TimeSeconds)]);
        }
        return NormalizeOneSequence(found);
    }

    /// <summary>
    /// <see cref="NormalizeWithOutliers"/> over the chapters of a single sequence - the whole job
    /// for an ordinary book, and one part's share of it for a book divided into parts.
    /// </summary>
    /// <param name="found">The raw detections of one sequence, in any order.</param>
    private static (List<DetectedChapter> Kept, List<DetectedChapter> Dropped) NormalizeOneSequence(
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
    /// trusting the triggering silence would anchor the --mark-before-jingle position, the
    /// --min-silence-length auto threshold and the per-file jingle statistics to the wrong, earlier
    /// one. Returns
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
    /// Finds where to cut between two adjacent probe windows so the seam never falls
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
    /// <param name="allSilences">Every silence Analyze found, down to <see
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
    /// pipeline: Probe's shared-border and stand-alone window-end snaps
    /// (<see cref="PlanWindowEnd"/>), the reuse-time split (both via
    /// <see cref="FindOverlapSplitPoint"/>), and Scan's chunk borders
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
    /// <param name="allSilences">Every silence Analyze stored, down to
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
    /// Plans a single probe window's end, called right before that window is probed -
    /// on the fly, so the end always reflects the <paramref name="probeSeconds"/> in effect at
    /// that moment - a candidate's own window length is settled when the candidate is built, and a
    /// plan computed for the whole region up front would only be a plan to go stale. The window
    /// naturally spans
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
    /// <param name="allSilences">Every silence Analyze found, down to <see
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
    /// still undetected (the gaps Scan would have to chase). Runs the input through
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
        if (kept.Count == 0)
            return (0, []);
        var highest = 0;
        var missing = new List<int>();
        // Per part, and the reported "highest" is the last part's: the bar is saying how far into
        // the book this run has got, and a book on part 3's chapter 2 has not gone backwards from
        // part 1's chapter 15.
        foreach (var sequence in BySequence(kept))
            highest = MissingInSequence(sequence, expectedStartChapter, missing);
        return (highest, missing);
    }

    /// <summary>
    /// Adds one sequence's still-undetected chapter numbers to <paramref name="missing"/> and
    /// returns the highest number it reaches - <see cref="ChapterProgress"/>'s per-part body,
    /// split out so the loop over the parts reads as the one new thing it is.
    /// </summary>
    /// <param name="sequence">One sequence's chapters, ascending in time.</param>
    /// <param name="expectedStartChapter">--expected-start-chapter, or null for no expectation.</param>
    /// <param name="missing">Collects the missing numbers; appended to.</param>
    /// <returns>The highest number present in this sequence, corroborated or not.</returns>
    private static int MissingInSequence(
        List<DetectedChapter> sequence, int? expectedStartChapter, List<int> missing)
    {
        var numbers = sequence.Select(c => c.Number).ToHashSet();
        if (numbers.Count == 0)
            return 0;
        // Only a corroborated number gets to say how far the book runs. An unverified one still
        // counts as present - it is a mark like any other - but the stretch below it is not
        // reported missing, which is what keeps a spoken year from declaring two thousand chapters
        // lost (see DetectedChapter.NumberUnverified).
        var vouched = sequence.Where(c => !c.NumberUnverified).Select(c => c.Number).ToList();
        // Nothing corroborated at all leaves no span to be missing from, so nothing is: the highest
        // number is still reported, since it names a mark that really was written, but a lone
        // uncorroborated 2179 must not answer "how much of this book is still missing?" with 2114.
        // Reachable on a clip or a short file - Probe finding exactly one chapter and that one in
        // doubt - rather than on a whole book.
        if (vouched.Count == 0)
            return numbers.Max();
        var highest = vouched.Max();
        // Clamped to highest: the first chapter found can transiently be numbered below
        // expectedStartChapter for one call, right before ChapterDetector's own "below
        // expectation" check aborts the run - without the clamp that would make the range
        // below negative-length and throw.
        var lowest = Math.Min(
            highest, StartOfSequence(sequence[0].Sequence, expectedStartChapter) ?? numbers.Min());
        missing.AddRange(
            Enumerable.Range(lowest, highest - lowest + 1).Where(n => !numbers.Contains(n)));
        return highest;
    }
}
