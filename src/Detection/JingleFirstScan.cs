// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Cli;
using ABChapterize.Language;
using static ABChapterize.Detection.DetectionTuning;
using static ABChapterize.Detection.GapPlanning;

namespace ABChapterize.Detection;

/// <summary>
/// Decides whether a file's Pass 2 runs jingle-first, and plans the second half of it when it does.
/// <para>
/// The ordinary Pass 2 walks a book's pauses and its music together, in one chronological sweep. On
/// a book that announces every chapter after a music sting, that is thousands of pause windows which
/// can only ever confirm what the music already said - and build 331 of "Die Cyber-Brutzellen" is
/// the recorded proof that they do exactly that: the seven chapters build 339 accepted "at a
/// silence" were accepted "at a jingle" by build 331 at the same millisecond, and the 1256 extra
/// probes between the two runs bought no mark at all. So the jingle-first shape reads the music
/// first, end to end, and only then looks at the pauses - and only where the chapter sequence still
/// has a hole for one to fill, plus the head and the tail of the file, which are where a prologue
/// and an epilogue live.
/// </para>
/// <para>
/// <b>What the gate is really protecting.</b> Skipping the pauses between two consecutive chapters
/// is safe exactly as long as nothing else can be announced there. A numbered chapter cannot be -
/// the numbers either side are consecutive, so there is no number left for it to carry. The
/// prologue's scope closes at the first chapter and the epilogue's opens after the last, so neither
/// can be there either. That leaves the user's own <c>--custom</c> mappings, which name whatever
/// recurring element the user says they do, at whatever position, and one of those is precisely what
/// this shape would stop looking for. Hence the second half of the gate - and hence
/// <see cref="NamedPhraseScope.AfterFirstChapter"/> counting as "between chapters" alongside
/// <see cref="NamedPhraseScope.Anywhere"/>: it does not exclude the middle of the book, only the
/// front matter.
/// </para>
/// <para>
/// Experimental (0.12.1). The corpus evidence for the shape is strong on the books it targets and
/// there is none at all for the books it does not, which is what <c>--jingle-first</c> is for: it
/// forces the shape on so a book outside the gate can be measured without rebuilding the tool.
/// </para>
/// </summary>
internal static class JingleFirstScan
{
    /// <summary>What <see cref="Decide"/> made of one file: whether Pass 2 runs jingle-first, and
    /// the sentence <c>--verbose</c> prints about it.</summary>
    /// <param name="Run">Whether to run the jingle-first shape.</param>
    /// <param name="Note">What to log, or null when there is nothing worth saying - which is the
    /// ordinary case for the ordinary book, where the shape was never in question.</param>
    internal readonly record struct Verdict(bool Run, string? Note);

    /// <summary>
    /// Whether this file's Pass 2 is to run jingle-first, and why.
    /// <para>
    /// Answered per file rather than per run: the census belongs to the file, and so does the
    /// language whose <c>--custom</c> mappings are in play, a mixed-language batch compiling a
    /// different mapping set for each of them.
    /// </para>
    /// <para>
    /// A file that qualifies on its music and is then held back by a mapping says so in the log. It
    /// is the one outcome here that will be asked about: the same run over the same book behaves
    /// differently for a reason that is stated nowhere else, and the option that overrides it is
    /// worth naming where somebody will read it.
    /// </para>
    /// </summary>
    /// <param name="options">The run's options, for the <c>--jingle-first</c> override and for the
    /// mode this shape has nothing to offer.</param>
    /// <param name="jingles">The file's census (<see cref="JingleCensus.Measure"/>), empty when the
    /// VAD pre-pass did not run.</param>
    /// <param name="durationSeconds">The file's play time, which the census is counted against.</param>
    /// <param name="profile">The file's resolved language profile, supplying the named phrases.</param>
    /// <param name="freshRun">Whether this is a fresh detection over one whole-file region rather
    /// than a --verify or resume recovery. Those probe nothing but bounded gaps already, where there
    /// is no long run of settled chapters to skip the pauses of, and where the head and tail
    /// stretches this plans would mean something else entirely. The caller folds the one-region half
    /// of that into this argument rather than leaving it implied, since
    /// <see cref="UnsettledStretches"/> is planned against a single region and would otherwise
    /// silently plan against the first of several.</param>
    internal static Verdict Decide(
        CliOptions options, IReadOnlyList<Jingle> jingles, double durationSeconds,
        LanguageProfile profile, bool freshRun)
    {
        // Without chapter numbers there is no sequence, so "the chapters either side are
        // consecutive" - the whole argument for skipping the pauses in between - cannot be made
        // about anything, and every stretch of the file would be unsettled. --jingle-first is
        // refused outright alongside --ignore-chapter-numbers rather than quietly ignored here.
        if (!freshRun || options.IgnoreChapterNumbers)
            return new Verdict(false, null);
        if (options.JingleFirst)
            return new Verdict(true, "jingle-first Pass 2 (--jingle-first)");

        var music = Earned(jingles, durationSeconds);
        if (music is null)
            return new Verdict(false, null);
        if (BetweenChapters(profile) is not { } mapping)
            return new Verdict(true, $"jingle-first Pass 2: {music}");
        return new Verdict(false,
            $"{music}, but {mapping.Kind} (\"{mapping.Pattern.Source}\") may be announced between " +
            "chapters - keeping the ordinary Pass 2 (--jingle-first overrides this)");
    }

    /// <summary>
    /// The automatic half of the gate: enough music that the census describes this book's chapter
    /// structure rather than an intro tune somebody left in. Null when it does not.
    /// <para>
    /// One jingle per hour is a low bar and is meant to be. The books this targets run 20-60 of them
    /// over 10-18 hours, and the books it must not fire on have none at all - the corpus's spurious
    /// tallies are 0 to 1 for a whole file, the measurement that brought them down that far being
    /// recorded on <see cref="JingleCensus"/>. There is no populated band in between to calibrate
    /// against, so a bar drawn anywhere inside it would be a number with nothing behind it.
    /// </para>
    /// </summary>
    /// <param name="jingles">The file's census.</param>
    /// <param name="durationSeconds">The file's play time.</param>
    private static string? Earned(IReadOnlyList<Jingle> jingles, double durationSeconds)
    {
        if (durationSeconds <= 0)
            return null;
        var perHour = jingles.Count / (durationSeconds / 3600);
        return perHour >= JingleFirstMinPerHour
            ? $"{jingles.Count} jingle(s), {perHour:0.0} per hour"
            : null;
    }

    /// <summary>
    /// The first <c>--custom</c> mapping that may legitimately be announced between two chapters, or
    /// null when none can be. See this class's remarks for why one such mapping rules the shape out:
    /// the pauses between two consecutive chapters are the one place it could be heard, and this is
    /// the shape that stops looking there.
    /// </summary>
    /// <param name="profile">The file's resolved language profile, whose mapping list is already cut
    /// to the ones written for this language.</param>
    internal static NamedPhrase? BetweenChapters(LanguageProfile profile)
        => profile.NamedPhrases.FirstOrDefault(
            p => p.IsCustom &&
                 p.Scope is NamedPhraseScope.Anywhere or NamedPhraseScope.AfterFirstChapter);

    /// <summary>
    /// The stretches the jingle half left unsettled, in file order - what the pause half walks.
    /// <para>
    /// A stretch between two chapters is settled when the second continues the first: same sequence,
    /// next number. Everything else is unsettled, which is three shapes at once and deliberately not
    /// three code paths - the head (before the first chapter found, where a prologue lives and where
    /// the chapters below the first one would be), a hole in the numbering, and the tail (after the
    /// last chapter found, where the epilogue lives and where the book may simply run on). A file
    /// whose music yielded no chapter at all is one stretch spanning the whole region, which is the
    /// ordinary Pass 2 in all but name: the right answer for a book whose jingles turned out to carry
    /// no announcements.
    /// </para>
    /// <para>
    /// Each stretch carries the numbers that bracket it, so the pause half is held to them exactly as
    /// a --verify gap region is: <see cref="DetectionRegion.LowerNumber"/> is the chapter below it
    /// and <see cref="DetectionRegion.UpperNumber"/> the one above, which together decide what a
    /// window there may accept, how hard <c>--chapter-phrase none</c> looks at a transcript, and
    /// whether the walk may conclude the numbering restarts. A stretch straddling a part boundary
    /// gets no upper bound at all: its two numbers belong to different sequences and nothing can be
    /// compared across them, so it is read forward exactly as the primary scan reads a book.
    /// </para>
    /// <para>
    /// Bounded by the marks rather than by the announcements behind them, as
    /// <see cref="FindGaps"/> bounds a gap, so the pause half re-reads the pause the chapter below it
    /// was found at. That window costs one probe and can produce nothing: the number it holds is the
    /// one already accepted, which no longer tops the sequence.
    /// </para>
    /// </summary>
    /// <param name="found">The chapters the jingle half accepted, in any order.</param>
    /// <param name="region">The region both halves walk - the whole file, this shape being restricted
    /// to a fresh run.</param>
    internal static List<DetectionRegion> UnsettledStretches(
        IReadOnlyList<DetectedChapter> found, DetectionRegion region)
    {
        var chapters = found.OrderBy(c => c.TimeSeconds).ToList();
        if (chapters.Count == 0)
            return [region];

        var stretches = new List<DetectionRegion>();
        // The head: bounded above by the first chapter found and below by whatever the region was
        // seeded with, which for a fresh run's whole-file region is "nothing known yet".
        Add(region.FromSeconds, chapters[0].TimeSeconds, region.LowerNumber, chapters[0], region.Sequence);
        for (var i = 1; i < chapters.Count; i++)
        {
            var (below, above) = (chapters[i - 1], chapters[i]);
            if (below.Sequence == above.Sequence && above.Number == below.Number + 1)
                continue;
            Add(below.TimeSeconds, above.TimeSeconds, below.Number, above, below.Sequence);
        }
        // The tail, which no chapter closes: the region's own upper bound stands, and for a fresh
        // run that is none at all.
        Add(chapters[^1].TimeSeconds, region.ToSeconds, chapters[^1].Number, null, chapters[^1].Sequence);
        return stretches;

        void Add(double from, double to, int lower, DetectedChapter? above, int sequence)
        {
            // Under a second there is nothing to walk: a pause candidate is held clear of a
            // stretch's last second, so such a stretch has no candidate to offer and only the
            // bookkeeping around it would run. Two chapters that close are ordinary - the announcement
            // of a new part follows the previous one's last chapter by whatever the book allows.
            if (to - from <= 1)
                return;
            stretches.Add(new DetectionRegion(
                from, to, lower,
                above is { } next && next.Sequence == sequence ? next.Number : null,
                sequence));
        }
    }
}
