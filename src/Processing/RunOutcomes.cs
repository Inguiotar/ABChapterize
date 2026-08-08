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
    private readonly List<(string Name, IReadOnlyList<int> Numbers, bool BareNumbers)> _lowConfidence = [];

    /// <summary>How many files the run skipped. Taken from the listing itself rather than from a
    /// counter of its own, so the number <c>--summary</c>'s first line quotes and the entries
    /// printed under it cannot drift apart.</summary>
    internal int SkippedCount => _skipped.Count;

    /// <summary>How many processed files came out with no chapters, counted from the listing for
    /// the same reason <see cref="SkippedCount"/> is.</summary>
    internal int NoChaptersCount => _noChapters.Count;

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
    /// <param name="numbers">The chapter numbers read below the threshold.</param>
    /// <param name="bareNumbers">Whether this file was read in <c>--chapter-phrase none</c> mode,
    /// which is what earns the block its footnote.</param>
    internal void RecordLowConfidence(string name, IReadOnlyList<int> numbers, bool bareNumbers)
        => _lowConfidence.Add((name, numbers, bareNumbers));

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
            [.. _lowConfidence.Select(f => (f.Name, DescribeLowConfidence(f.Numbers)))],
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
    /// <param name="numbers">The chapter numbers read below the threshold.</param>
    private static string DescribeLowConfidence(IReadOnlyList<int> numbers)
        => $"{numbers.Count} mark(s) (chapter {MissingMarksTag.FormatList(numbers)})";

    /// <summary>
    /// The line closing the low-confidence block when any of the files in it were read in
    /// <c>--chapter-phrase none</c> mode, and null otherwise.
    /// </summary>
    /// <remarks>
    /// The confidence is Whisper's mean token probability for the segment the number was read from,
    /// and in bare-number mode that segment is frequently the number and nothing else - a single
    /// token, whose probability is far more volatile than a phrase's whole sentence. Measured on
    /// "Corsa nello spazio" (Italian, 65 chapters, 2026-08-08): its marks have the highest median
    /// confidence of the fourteen-book corpus at 0.89, and simultaneously the only three marks below
    /// 0.5 anywhere in it - 0.44, 0.42 and 0.29, each from a segment reading exactly "44", "55" and
    /// "58" while the prose either side of them scored 0.81 to 0.94. All three were correctly placed,
    /// landing in -91 to -95 dBFS silence. So the tail is fatter without the marks being worse, which
    /// is precisely what a reader deciding where to spend an evening needs told.
    /// </remarks>
    private string? BareNumberFootnote()
        => _lowConfidence.Any(f => f.BareNumbers)
            ? "(A bare number is often a segment of one token, so its confidence swings much wider " +
              "than a phrase's - a low value there is weaker evidence of a bad mark.)"
            : null;
}
