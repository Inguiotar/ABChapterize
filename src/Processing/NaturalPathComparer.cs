// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace ABChapterize.Processing;

/// <summary>
/// Orders file paths the way a person expects a file listing to be ordered: like an ordinal
/// case-insensitive comparison, except that a run of digits counts as one number rather than as
/// individual characters, so "Track 2.mp3" precedes "Track 10.mp3" instead of following it.
/// <para>
/// This matters beyond cosmetics. Split audiobooks are numbered per file, so ordinal order
/// processes - and reports - part 10 before part 2, and a batch interrupted halfway then resumes
/// against that same scrambled order. Whole-number comparison is the only ordering under which
/// "the run got as far as part 9" means what it sounds like.
/// </para>
/// <para>
/// Digit runs are compared without ever converting them to a number, so a 40-digit run in a file
/// name cannot overflow anything; equal-value runs that differ only in leading zeros ("02" vs "2")
/// are ordered by zero count, and paths that are equal under all of the above fall back to an
/// ordinal comparison. Both tie-breakers exist so the ordering is total: two distinct paths never
/// compare equal, which would otherwise let the sort return them in an unpredictable order.
/// </para>
/// </summary>
public sealed class NaturalPathComparer : IComparer<string>
{
    /// <summary>The shared instance; the comparer holds no state.</summary>
    public static readonly NaturalPathComparer Instance = new();

    private NaturalPathComparer() { }

    /// <summary>Compares two paths in natural order.</summary>
    /// <param name="x">First path.</param>
    /// <param name="y">Second path.</param>
    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var xRun = DigitRunLength(x, i);
                var yRun = DigitRunLength(y, j);
                var byNumber = CompareDigitRuns(x.AsSpan(i, xRun), y.AsSpan(j, yRun));
                if (byNumber != 0) return byNumber;
                i += xRun;
                j += yRun;
                continue;
            }
            var byChar = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (byChar != 0) return byChar;
            i++;
            j++;
        }

        // One path is a prefix of the other (in the natural-order sense): the shorter one wins.
        if (i < x.Length) return 1;
        if (j < y.Length) return -1;
        return string.CompareOrdinal(x, y);
    }

    /// <summary>Length of the digit run starting at <paramref name="start"/>.</summary>
    /// <param name="s">The string to scan.</param>
    /// <param name="start">Index of the run's first digit.</param>
    private static int DigitRunLength(string s, int start)
    {
        var end = start;
        while (end < s.Length && char.IsAsciiDigit(s[end]))
            end++;
        return end - start;
    }

    /// <summary>
    /// Compares two digit runs as whole numbers. Leading zeros are ignored for the value, so
    /// "007" and "7" are the same chapter; when the values match, the run with fewer leading zeros
    /// sorts first, purely so the result is deterministic.
    /// </summary>
    /// <param name="x">First digit run.</param>
    /// <param name="y">Second digit run.</param>
    private static int CompareDigitRuns(ReadOnlySpan<char> x, ReadOnlySpan<char> y)
    {
        var xTrimmed = x.TrimStart('0');
        var yTrimmed = y.TrimStart('0');
        if (xTrimmed.Length != yTrimmed.Length)
            return xTrimmed.Length - yTrimmed.Length;
        var byDigits = xTrimmed.SequenceCompareTo(yTrimmed);
        return byDigits != 0 ? byDigits : x.Length - y.Length;
    }
}
