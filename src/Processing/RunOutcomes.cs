// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Detection;
using ABChapterize.Ui;
using static ABChapterize.Detection.DetectionTuning;

namespace ABChapterize.Processing;

/// <summary>
/// The run's roster of per-file outcomes worth naming at the end: which files were skipped and for
/// what reason, which came out of detection with no chapters at all, which were left with chapter
/// marks still missing, and which were finished but carry marks Whisper was unsure of. Where
/// <see cref="RunStatistics"/> accumulates measurements, this accumulates names - the four listings
/// <c>--summary</c> closes a run with.
/// </summary>
/// <remarks>
/// The point of the listings is a batch of two hundred audiobooks, where the per-file result lines
/// have long scrolled away (and under <c>--quiet</c> were never printed at all): the questions left
/// at the end are "which ones did you not do", "which ones came back empty-handed", "which ones
/// are not finished" and "which ones should I check by hand", and none should require reading a log
/// back. Filled one file at a time by <see cref="FileProcessor"/>, so nothing here is synchronized.
/// </remarks>
internal sealed class RunOutcomes
{
    /// <summary>Files skipped without detection running, each with the short reason its result
    /// line gave (the "(use --force to redo)" hint and the like left off - in a list of two
    /// hundred it is noise, and it is the same hint on every entry).</summary>
    private readonly List<(string Name, string Reason)> _skipped = [];

    /// <summary>Files detection ran on and found nothing in, each with the reason its result line
    /// gave (early abort, first chapter below --expected-start-chapter, or no phrase at all).</summary>
    private readonly List<(string Name, string Reason)> _noChapters = [];

    /// <summary>Files written with an incomplete chapter sequence, each with the chapter numbers
    /// still missing from it.</summary>
    private readonly List<(string Name, IReadOnlyList<int> MissingNumbers)> _missingMarks = [];

    /// <summary>Files carrying at least one mark whose chapter number was read from a transcript
    /// segment Whisper scored below <see cref="DetectionTuning.LowConfidenceThreshold"/>, each with
    /// those numbers and whether the file was read in <c>--chapter-phrase none</c> mode.</summary>
    private readonly List<(string Name, IReadOnlyList<DetectedChapter> Chapters, int SequenceCount,
        bool BareNumbers)> _lowConfidence = [];

    /// <summary>How many files the run skipped. Taken from the listing itself rather than from a
    /// counter of its own, so the number <c>--summary</c>'s first line quotes and the entries
    /// printed under it cannot drift apart.</summary>
    internal int SkippedCount => _skipped.Count;

    /// <summary>How many processed files came out with no chapters, counted from the listing for
    /// the same reason <see cref="SkippedCount"/> is.</summary>
    internal int NoChaptersCount => _noChapters.Count;

    /// <summary>How many files the run has left with chapter marks still missing, counted from the
    /// listing for the same reason <see cref="SkippedCount"/> is.</summary>
    internal int MissingMarksCount => _missingMarks.Count;

    /// <summary>Records one skipped file for the closing listing.</summary>
    /// <param name="name">The file's bare name.</param>
    /// <param name="reason">Why it was skipped, as a sentence fragment following the name.</param>
    internal void RecordSkipped(string name, string reason) => _skipped.Add((name, reason));

    /// <summary>Records one file detection left unchanged for want of any chapter at all.</summary>
    /// <param name="name">The file's bare name.</param>
    /// <param name="reason">Why nothing was written, as a sentence fragment following the name -
    /// the same wording the file's own result line used, so the two cannot describe the outcome
    /// differently.</param>
    internal void RecordNoChapters(string name, string reason) => _noChapters.Add((name, reason));

    /// <summary>Records one file left with an incomplete chapter sequence.</summary>
    /// <param name="name">The bare name the file carries once the run is done - i.e. the
    /// <c>.missing-marks</c>-tagged one it was renamed to, since that is what the listing's reader
    /// will find in the folder. Under <c>--dry-run</c>, where nothing is renamed, its own.</param>
    /// <param name="missingNumbers">The chapter numbers still missing.</param>
    internal void RecordMissingMarks(string name, IReadOnlyList<int> missingNumbers)
        => _missingMarks.Add((name, missingNumbers));

    /// <summary>Records one file whose written marks include at least one low-confidence chapter
    /// number.</summary>
    /// <param name="name">The bare name the file carries once the run is done, for the same reason
    /// <see cref="RecordMissingMarks"/> takes that one.</param>
    /// <param name="chapters">The chapters read below the threshold, in file order.</param>
    /// <param name="sequenceCount">How many chapter sequences the file holds, which is what
    /// decides whether the listing has to name parts - see <see cref="NameChapters"/>.</param>
    /// <param name="bareNumbers">Whether this file was read in <c>--chapter-phrase none</c> mode,
    /// which is what earns the block its footnote.</param>
    internal void RecordLowConfidence(
        string name, IReadOnlyList<DetectedChapter> chapters, int sequenceCount, bool bareNumbers)
        => _lowConfidence.Add((name, chapters, sequenceCount, bareNumbers));

    /// <summary>
    /// Builds the closing listings, in order of how much of the file's work got done - not started,
    /// started and empty-handed, finished but incomplete, finished but worth a look - as lines
    /// pre-split into their pieces so that the file names are colored as titles rather than
    /// pattern-matched as prose. Any listing is left out entirely when nothing fell into it.
    /// </summary>
    internal List<List<SummarySegment>> FormatListings()
    {
        var lines = new List<List<SummarySegment>>();
        AppendBlock(lines, $"Skipped {_skipped.Count} file(s):", _skipped);
        AppendBlock(lines, $"No chapters found in {_noChapters.Count} file(s):", _noChapters);
        AppendBlock(lines, $"Still missing chapter marks in {_missingMarks.Count} file(s):",
            [.. _missingMarks.Select(f => (f.Name, DescribeMissing(f.MissingNumbers)))]);
        AppendBlock(lines,
            $"Low-confidence chapter marks in {_lowConfidence.Count} file(s) " +
            $"(below p={LowConfidenceThreshold:0.00}, worth a manual check):",
            [.. _lowConfidence.Select(f => (f.Name, DescribeLowConfidence(f.Chapters, f.SequenceCount)))],
            BareNumberFootnote());
        return lines;
    }

    /// <summary>Appends one heading plus its indented "&lt;name&gt;: &lt;note&gt;" entries, or
    /// nothing at all when there are no entries.</summary>
    /// <param name="lines">The listing lines built so far, appended to.</param>
    /// <param name="heading">The block's heading line.</param>
    /// <param name="entries">The files to list and the note to print after each one.</param>
    /// <param name="footnote">A single line closing the block, or null for none. Indented with the
    /// entries rather than the heading, since it qualifies them.</param>
    private static void AppendBlock(
        List<List<SummarySegment>> lines, string heading,
        IReadOnlyList<(string Name, string Note)> entries, string? footnote = null)
    {
        if (entries.Count == 0)
            return;
        lines.Add([SummarySegment.Prose(heading)]);
        foreach (var (name, note) in entries)
            lines.Add([
                SummarySegment.Prose("  "),
                SummarySegment.Title(name),
                SummarySegment.Prose($": {note}"),
            ]);
        if (footnote != null)
            lines.Add([SummarySegment.Prose($"  {footnote}")]);
    }

    /// <summary>The note following an unfinished book's name: how many marks it is still short of,
    /// and - through the same cut-off <see cref="MissingMarksTag.PathFor"/> applies to the
    /// file name it announces - which chapters those are.</summary>
    /// <param name="missingNumbers">The chapter numbers still missing.</param>
    private static string DescribeMissing(IReadOnlyList<int> missingNumbers)
        => $"{missingNumbers.Count} mark(s) missing " +
           $"(chapter {MissingMarksTag.FormatList(missingNumbers)})";

    /// <summary>The note following the name of a book with marks worth checking: how many, and
    /// which chapters. Through <see cref="MissingMarksTag.FormatList"/> like the missing-marks note,
    /// so a book that came back unsure about forty of its chapters takes one line rather than
    /// forty numbers.</summary>
    /// <param name="chapters">The chapters read below the threshold.</param>
    /// <param name="sequenceCount">How many chapter sequences the file holds.</param>
    private static string DescribeLowConfidence(IReadOnlyList<DetectedChapter> chapters, int sequenceCount)
        => $"{chapters.Count} mark(s) ({NameChapters(chapters, sequenceCount)})";

    /// <summary>
    /// Names a set of chapter marks for a listing: "chapter 4, 9, 17" for an ordinary book, and
    /// "part 1 chapter 4, 9; part 2 chapter 3" for one whose numbering restarts, where a bare
    /// number would not say which part's chapter 4 was meant.
    /// </summary>
    /// <remarks>
    /// The <see cref="MissingMarksTag.MaxNamedNumbers"/> cut-off is applied to the whole set
    /// before the grouping rather than per part, so a book in five parts cannot name five times as
    /// many chapters as an ordinary one and defeat the point of having a cut-off. Parts are
    /// numbered from one, as they are in the mark titles this listing sends a reader to look at.
    /// The word is English like the rest of the summary prose, not the localized
    /// <c>--part-title</c> that goes into the file itself.
    /// </remarks>
    /// <param name="chapters">The chapters to name, in file order.</param>
    /// <param name="sequenceCount">How many chapter sequences the file holds. The trigger is the
    /// book rather than the listed chapters: marks that all happen to fall in one part of a
    /// three-part book still need saying which part, and asking the subset would not.</param>
    internal static string NameChapters(IReadOnlyList<DetectedChapter> chapters, int sequenceCount)
    {
        var shown = chapters.Count <= MissingMarksTag.MaxNamedNumbers
            ? chapters
            : [.. chapters.Take(MissingMarksTag.MaxNamedNumbers)];
        var more = chapters.Count - shown.Count;
        var tail = more > 0 ? $" and {more} more" : "";
        if (sequenceCount <= 1)
            return $"chapter {string.Join(", ", shown.Select(c => c.Number))}{tail}";
        var parts = shown.GroupBy(c => c.Sequence).OrderBy(g => g.Key);
        return string.Join("; ", parts.Select(g =>
            $"part {g.Key + 1} chapter {string.Join(", ", g.Select(c => c.Number))}")) + tail;
    }

    /// <summary>
    /// The line closing the low-confidence block when any of the files in it were read in
    /// <c>--chapter-phrase none</c> mode, and null otherwise.
    /// </summary>
    /// <remarks>
    /// The confidence is Whisper's mean token probability for the segment the number was read from,
    /// and in bare-number mode that segment is frequently the number and nothing else - a single
    /// token, whose probability is far more volatile than a phrase's whole sentence. So the tail is
    /// fatter without the marks being worse, which is precisely what a reader deciding where to
    /// spend an evening needs told.
    /// <include file='../../notes/Processing/RunOutcomes.xml' path='doc/member[@name="BareNumberFootnote"]/*' />
    /// </remarks>
    private string? BareNumberFootnote()
        => _lowConfidence.Any(f => f.BareNumbers)
            ? "(A bare number is often a segment of one token, so its confidence swings much wider " +
              "than a phrase's - a low value there is weaker evidence of a bad mark.)"
            : null;
}
