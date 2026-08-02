// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Ui;

namespace ABChapterize.Processing;

/// <summary>
/// The run's roster of per-file outcomes worth naming at the end: which files were skipped and for
/// what reason, and which were left with chapter marks still missing. Where
/// <see cref="RunStatistics"/> accumulates measurements, this accumulates names - the two listings
/// <c>--summary</c> closes a run with.
/// </summary>
/// <remarks>
/// The point of both listings is a batch of two hundred audiobooks, where the per-file result lines
/// have long scrolled away (and under <c>--quiet</c> were never printed at all): the two questions
/// left at the end are "which ones did you not do" and "which ones are not finished", and neither
/// should require reading a log back. Filled one file at a time by <see cref="FileProcessor"/>, so
/// nothing here is synchronized.
/// </remarks>
internal sealed class RunOutcomes
{
    /// <summary>Files skipped without detection running, each with the short reason its result
    /// line gave (the "(use --force to redo)" hint and the like left off - in a list of two
    /// hundred it is noise, and it is the same hint on every entry).</summary>
    private readonly List<(string Name, string Reason)> _skipped = [];

    /// <summary>Files written with an incomplete chapter sequence, each with the chapter numbers
    /// still missing from it.</summary>
    private readonly List<(string Name, IReadOnlyList<int> MissingNumbers)> _missingMarks = [];

    /// <summary>How many files the run skipped. Taken from the listing itself rather than from a
    /// counter of its own, so the number <c>--summary</c>'s first line quotes and the entries
    /// printed under it cannot drift apart.</summary>
    internal int SkippedCount => _skipped.Count;

    /// <summary>Records one skipped file for the closing listing.</summary>
    /// <param name="name">The file's bare name.</param>
    /// <param name="reason">Why it was skipped, as a sentence fragment following the name.</param>
    internal void RecordSkipped(string name, string reason) => _skipped.Add((name, reason));

    /// <summary>Records one file left with an incomplete chapter sequence.</summary>
    /// <param name="name">The bare name the file carries once the run is done - i.e. the
    /// <c>.missing-marks</c>-tagged one it was renamed to, since that is what the listing's reader
    /// will find in the folder. Under <c>--dry-run</c>, where nothing is renamed, its own.</param>
    /// <param name="missingNumbers">The chapter numbers still missing.</param>
    internal void RecordMissingMarks(string name, IReadOnlyList<int> missingNumbers)
        => _missingMarks.Add((name, missingNumbers));

    /// <summary>
    /// Builds both closing listings, skipped files first, as lines pre-split into their pieces so
    /// that the file names are colored as titles rather than pattern-matched as prose. Either
    /// listing is left out entirely when nothing fell into it.
    /// </summary>
    internal List<List<SummarySegment>> FormatListings()
    {
        var lines = new List<List<SummarySegment>>();
        AppendBlock(lines, $"Skipped {_skipped.Count} file(s):", _skipped);
        AppendBlock(lines, $"Still missing chapter marks in {_missingMarks.Count} file(s):",
            [.. _missingMarks.Select(f => (f.Name, DescribeMissing(f.MissingNumbers)))]);
        return lines;
    }

    /// <summary>Appends one heading plus its indented "&lt;name&gt;: &lt;note&gt;" entries, or
    /// nothing at all when there are no entries.</summary>
    /// <param name="lines">The listing lines built so far, appended to.</param>
    /// <param name="heading">The block's heading line.</param>
    /// <param name="entries">The files to list and the note to print after each one.</param>
    private static void AppendBlock(
        List<List<SummarySegment>> lines, string heading, IReadOnlyList<(string Name, string Note)> entries)
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
    }

    /// <summary>The note following an unfinished book's name: how many marks it is still short of,
    /// and - through the same cut-off <see cref="FileProcessor.MissingMarksPath"/> applies to the
    /// file name it announces - which chapters those are.</summary>
    /// <param name="missingNumbers">The chapter numbers still missing.</param>
    private static string DescribeMissing(IReadOnlyList<int> missingNumbers)
        => $"{missingNumbers.Count} mark(s) missing " +
           $"(chapter {FileProcessor.FormatMissingList(missingNumbers)})";
}
