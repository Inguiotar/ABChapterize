// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Text.RegularExpressions;

namespace ABChapterize.Processing;

/// <summary>
/// The ".missing-marks" file-name tag: how one is written, recognized and taken off again.
/// <para>
/// A to-do note left on a file name is an odd place to keep state, and it earns that place by
/// surviving everything else: the folder can be moved to another machine, the tool upgraded, the
/// run's own log thrown away, and the note is still there in the file listing where a human will
/// see it. What makes it work is that exactly one set of rules writes and reads it - the detection
/// pipeline puts a tag on, an auto-resume takes one off, <c>--cleanup</c> takes one off without
/// re-detecting - so the rules live here rather than in whichever of the three touched it first.
/// </para>
/// </summary>
public static partial class MissingMarksTag
{
    /// <summary>
    /// The most chapter numbers <see cref="PathFor"/> spells out in a file name before falling
    /// back to the unnumbered ".missing-marks" tag. A file missing this many chapters or fewer is
    /// worth naming them all - beyond that the name grows unwieldy (and can hit the platform's
    /// path length limit), and a gap that large is a sign that detection went off the rails rather
    /// than a shortlist worth resuming from.
    /// </summary>
    public const int MaxNamedNumbers = 10;

    /// <summary>
    /// Builds the name a file is renamed to when Scan leaves an unresolved chapter-sequence gap:
    /// the original name with a ".missing-marks-&lt;n&gt;-&lt;n&gt;-..." tag (the still-missing
    /// chapter numbers, "-"-delimited) inserted before the extension, e.g. "Book.m4b" with
    /// chapters 3 and 7 missing becomes "Book.missing-marks-3-7.m4b". Beyond
    /// <see cref="MaxNamedNumbers"/> missing chapters the numbers are left out entirely
    /// ("Book.missing-marks.m4b"), which also takes the file out of <see cref="IsResumable"/>'s
    /// auto-resume scope on purpose: a gap that wide is something to look at by hand, not to hand
    /// straight back to another automatic run. Any such tag already present is replaced rather
    /// than stacked, in either form.
    /// </summary>
    /// <param name="file">Path of the file being renamed.</param>
    /// <param name="missingNumbers">The chapter numbers still missing after Scan.</param>
    public static string PathFor(string file, IReadOnlyList<int> missingNumbers)
    {
        var tag = missingNumbers.Count is > 0 and <= MaxNamedNumbers
            ? $".missing-marks-{string.Join("-", missingNumbers)}"
            : ".missing-marks";
        return Path.Combine(
            Path.GetDirectoryName(file) ?? "",
            StripFromStem(Path.GetFileNameWithoutExtension(file)) + tag + Path.GetExtension(file));
    }

    /// <summary>The file's own original name, with any ".missing-marks-..." tag stripped - what a
    /// resumed file is renamed back to once every previously-missing chapter is found.</summary>
    /// <param name="file">Path of the tagged file.</param>
    public static string StripFrom(string file)
        => Path.Combine(
            Path.GetDirectoryName(file) ?? "",
            StripFromStem(Path.GetFileNameWithoutExtension(file)) + Path.GetExtension(file));

    /// <summary>True when a file name still carries a numbered ".missing-marks-&lt;n&gt;-..." tag
    /// (see <see cref="PathFor"/>) - i.e. a previous run left it with an unresolved
    /// chapter-sequence gap small enough to name, and it is a candidate for the auto-resume
    /// branch. The unnumbered ".missing-marks" form deliberately does not qualify; see
    /// <see cref="PathFor"/>.</summary>
    /// <param name="file">Path of the file being considered.</param>
    public static bool IsResumable(string file)
        => NumberedTagRegex().IsMatch(Path.GetFileNameWithoutExtension(file));

    /// <summary>True when a file name carries a ".missing-marks" tag in either form. Unlike
    /// <see cref="IsResumable"/> this asks "is this file still flagged?" rather than "can a resume
    /// act on it", which is the question both when deciding whether a completed run has a tag to
    /// take off again and when telling the two rename directions apart.</summary>
    /// <param name="file">Path of the file being considered.</param>
    public static bool IsTagged(string file)
        => TagRegex().IsMatch(Path.GetFileNameWithoutExtension(file));

    /// <summary>
    /// Formats still-missing chapter numbers for a summary line, listing at most
    /// <see cref="MaxNamedNumbers"/> of them and summarizing the rest as a count - the same
    /// cut-off <see cref="PathFor"/> applies to the file name, so the message and the name it
    /// announces stay in step.
    /// </summary>
    /// <param name="missingNumbers">The chapter numbers still missing.</param>
    public static string FormatList(IReadOnlyList<int> missingNumbers)
        => missingNumbers.Count <= MaxNamedNumbers
            ? string.Join(", ", missingNumbers)
            : string.Join(", ", missingNumbers.Take(MaxNamedNumbers)) +
              $" and {missingNumbers.Count - MaxNamedNumbers} more";

    /// <summary>Removes a trailing ".missing-marks" tag - with or without its number list - from a
    /// file stem, so re-tagging an already-tagged file replaces the tag instead of appending a
    /// second one.</summary>
    /// <param name="stem">File name without directory or extension.</param>
    private static string StripFromStem(string stem) => TagRegex().Replace(stem, "");

    /// <summary>Matches a trailing ".missing-marks" tag in either form, numbered or bare.</summary>
    [GeneratedRegex(@"\.missing-marks(-[0-9-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TagRegex();

    /// <summary>Matches only the numbered form of the tag, the one an auto-resume can act on.</summary>
    [GeneratedRegex(@"\.missing-marks-[0-9-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedTagRegex();
}
