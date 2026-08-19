// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Cli;
using ABChapterize.Language;

namespace ABChapterize.Detection;

/// <summary>
/// Decides whether a file's Probe reads its longest pauses first. The counterpart of
/// <see cref="JingleFirstScan"/> for a book with no music to read instead, and the two are mutually
/// exclusive by construction: this shape is offered only to a file that one turned down.
/// <para>
/// <b>What it is for.</b> A book announces its chapters after its longest pauses, and the pauses it
/// does not announce anything after outnumber them by one to two orders of magnitude. Read in file
/// order, a chapter break and the thousand sentence gaps around it cost the same; read longest
/// first, the chapters arrive in the first few dozen windows and the rest of the list can be judged
/// on what they said rather than probed on the chance that they say something.
/// </para>
/// <para>
/// <b>It is a plan, not a first half.</b> Reading the long pauses decides nothing - it decides only
/// which candidates are worth reading at all. Probing itself is still the one forward walk it has
/// always been (<see cref="RegionProber"/>), which is what keeps the chapter sequence, the restart
/// tracking, the gap re-probe and <c>--early-abort</c> working exactly as they do on any other book.
/// The saving is that the walk passes over every candidate lying between two readings that carry
/// consecutive chapter numbers.
/// </para>
/// <para>
/// <b>Why passing those over is safe</b> - the same argument the jingle-first shape rests on:
/// between two consecutive chapter numbers there is no number left for a third chapter to carry, a
/// prologue's scope has closed there and an epilogue's has not opened. The one thing that
/// <em>could</em> be announced between two consecutive chapters is a <c>--custom</c> mapping the
/// user placed there, which is why one rules this shape out exactly as it rules out reading the
/// music first. And a stretch closed on a misread number is still reopened by the sequence gap
/// re-probe, which this shape keeps.
/// </para>
/// <para>
/// Experimental (0.12.1), with no corpus run behind it yet - only a replay of the frozen build-341
/// logs, which says where a book's chapter-bearing pauses rank in its own list and nothing about
/// what a window turns out to read. Two things can still move a mark on a real book: a stretch
/// closed on a misread number, and the transcript overlap cache, which a walk that visits candidates
/// out of order fills differently - so a window may be served fresh here where it was served from
/// cache before, or the reverse.
/// </para>
/// </summary>
/// <remarks>Notes: where the corpus's chapter pauses rank, and what the shape costs on the books it cannot help.
/// <include file='../../notes/Detection/DescendingSilenceScan.xml' path='doc/member[@name="DescendingSilenceScan"]/*' /></remarks>
internal static class DescendingSilenceScan
{
    /// <summary>What <see cref="Decide"/> made of one file: whether Probe reads its longest pauses
    /// first, and the sentence <c>--verbose</c> prints about it.</summary>
    /// <param name="Run">Whether to run the descending shape.</param>
    /// <param name="Note">What to log, or null when there is nothing worth saying.</param>
    internal readonly record struct Verdict(bool Run, string? Note);

    /// <summary>
    /// Whether this file's Probe is to read its longest pauses first, and why.
    /// <para>
    /// Answered per file like the jingle-first verdict and for the same reasons - the language whose
    /// <c>--custom</c> mappings are in play belongs to the file, not to the run.
    /// </para>
    /// </summary>
    /// <param name="options">The run's options.</param>
    /// <param name="profile">The file's resolved language profile, supplying the named phrases.</param>
    /// <param name="freshRun">Whether this is a fresh detection over one whole-file region rather
    /// than a --verify or resume recovery. Those probe bounded gaps already, where there is no long
    /// run of settled chapters whose pauses could be skipped.</param>
    /// <param name="jingleFirst">Whether the music-first shape has this file. It gets first refusal:
    /// where a book's structure is in its music, reading the music is the cheaper question, and
    /// running both would frame the same audio two ways for one job.</param>
    internal static Verdict Decide(
        CliOptions options, LanguageProfile profile, bool freshRun, bool jingleFirst)
    {
        // Without chapter numbers there is no sequence, so "the chapters either side are
        // consecutive" - the whole argument for skipping the pauses in between - cannot be made
        // about anything, and every stretch of the file would be unsettled.
        if (!freshRun || jingleFirst || options.IgnoreChapterNumbers)
            return new Verdict(false, null);
        // An explicit --min-silence-length is the user naming the pauses worth probing, and it is
        // also the setting that switches off the adaptive arithmetic this walk stops against
        // (RegionProber.ProposeGatherFloor). Without a stop rule the walk reads the whole candidate
        // list out of file order, which is every cost of the shape and none of its saving.
        if (!options.AutoMinSilence)
            return new Verdict(false, null);
        if (JingleFirstScan.BetweenChapters(profile) is not { } mapping)
            return new Verdict(true, "reading the longest pauses first");
        return new Verdict(false,
            $"{mapping.Kind} (\"{mapping.Pattern.Source}\") may be announced between chapters - " +
            "reading the file in one sweep instead");
    }

    /// <summary>
    /// The order the descent reads a region's candidates in: its start, then its music, then its
    /// pauses longest first.
    /// <para>
    /// The region start leads because a book announcing chapter 1 in its opening seconds has no
    /// pause in front of it to be found by, and because the chapter it finds anchors the numbering
    /// every later reading is judged against. The jingles follow, in file order and ahead of the
    /// stop rule, because there are only ever a handful on a file this shape runs on - the shape is
    /// refused outright to a book with enough music to be read music-first - and letting the stop
    /// rule cut them off would leave that handful unread on a file the ordinary walk reads them on.
    /// </para>
    /// </summary>
    /// <param name="candidates">The region's candidates, in file order.</param>
    /// <returns>Indices into <paramref name="candidates"/>, in reading order.</returns>
    internal static IEnumerable<int> LongestPauseFirst(IReadOnlyList<ProbeCandidate> candidates)
        => Enumerable.Range(0, candidates.Count)
            .OrderBy(i => candidates[i].Silence is null ? (candidates[i].IsJingle ? 1 : 0) : 2)
            .ThenByDescending(i => PauseSecondsOf(candidates[i]) ?? 0)
            .ThenBy(i => candidates[i].Start);

    /// <summary>How long a candidate's pause is, or null where it has none - the region start and
    /// every jingle. A jingle says nothing about how long this book's pauses run, which is why it
    /// can neither move the stop rule nor be stopped by it.</summary>
    /// <param name="candidate">The candidate to measure.</param>
    internal static double? PauseSecondsOf(ProbeCandidate candidate)
        => candidate.Silence is { } silence ? silence.EndSeconds - silence.StartSeconds : null;

    /// <summary>
    /// The stretches the descent settled: between two windows whose readings carry consecutive
    /// chapter numbers there is no number left for a third chapter to carry, so every candidate
    /// strictly inside is passed over. This is the entirety of what the shape saves.
    /// <para>
    /// Bounded by the candidate starts rather than by the announcements inside them, so the two
    /// windows that closed a stretch are themselves still walked - they are where those chapters get
    /// accepted. A misread number can close a stretch that holds a real chapter, which is this
    /// shape's one exposure and the same one the jingle-first shape carries; a hole it opens
    /// elsewhere is still picked up by the sequence gap re-probe, which the walk keeps.
    /// </para>
    /// </summary>
    /// <param name="candidates">The region's candidates, in file order.</param>
    /// <param name="heard">Which candidates the descent read, and the chapter numbers each window
    /// held - the first of them standing for the window, a wide one covering two transitions being
    /// rare enough that pairing on it would only ever close fewer stretches.</param>
    internal static List<(double From, double To)> SettledSpans(
        IReadOnlyList<ProbeCandidate> candidates,
        IReadOnlyList<(int Index, List<int> Numbers)> heard)
    {
        var readings = heard
            .Where(h => h.Numbers.Count > 0)
            .Select(h => (Start: candidates[h.Index].Start, Number: h.Numbers[0]))
            .OrderBy(r => r.Start)
            .ToList();
        var spans = new List<(double From, double To)>();
        for (var i = 1; i < readings.Count; i++)
            if (readings[i].Number == readings[i - 1].Number + 1)
                spans.Add((readings[i - 1].Start, readings[i].Start));
        return spans;
    }
}
