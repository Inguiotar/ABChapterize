// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Processing;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="NaturalPathComparer"/>: whole-number digit runs, case insensitivity, and
/// the tie-breakers that keep the ordering total.
/// </summary>
public sealed class NaturalPathComparerTests
{
    /// <summary>Sorts the given names with the comparer under test.</summary>
    /// <param name="names">The names to sort.</param>
    private static List<string> Sorted(params string[] names)
        => [.. names.OrderBy(n => n, NaturalPathComparer.Instance)];

    [Fact]
    public void DigitRuns_CompareAsWholeNumbers()
    {
        Assert.Equal(
            ["Track 2.mp3", "Track 9.mp3", "Track 10.mp3", "Track 100.mp3"],
            Sorted("Track 10.mp3", "Track 100.mp3", "Track 2.mp3", "Track 9.mp3"));
    }

    [Fact]
    public void ZeroPaddedAndUnpaddedNumbers_InterleaveByValue()
    {
        Assert.Equal(
            ["Part 03.m4b", "Part 9.m4b", "Part 011.m4b"],
            Sorted("Part 9.m4b", "Part 011.m4b", "Part 03.m4b"));
    }

    [Fact]
    public void EqualValues_AreOrderedByLeadingZeroCount()
    {
        // Same number, so nothing about the value decides it; the point is only that the two do
        // not compare equal, which would leave their order up to the sort's internals.
        // Fed in the wrong order on purpose: OrderBy is stable, so an input already in the
        // expected order would pass even if the comparer called the two equal.
        Assert.Equal(["Part 7.m4b", "Part 007.m4b"], Sorted("Part 007.m4b", "Part 7.m4b"));
    }

    [Fact]
    public void LettersCompareCaseInsensitively()
    {
        Assert.Equal(["alpha.m4b", "Beta.m4b", "gamma.m4b"],
            Sorted("Beta.m4b", "gamma.m4b", "alpha.m4b"));
    }

    [Fact]
    public void NamesEqualApartFromCase_AreOrderedOrdinally_RatherThanEqual()
    {
        Assert.Equal(["Book.m4b", "book.m4b"], Sorted("book.m4b", "Book.m4b"));
        Assert.NotEqual(0, NaturalPathComparer.Instance.Compare("book.m4b", "Book.m4b"));
    }

    [Fact]
    public void AShorterNameThatIsAPrefix_ComesFirst()
    {
        Assert.Equal(["Book 2.m4b", "Book 2.m4b.bak"], Sorted("Book 2.m4b.bak", "Book 2.m4b"));
    }

    [Fact]
    public void ADigitRunTooLongForAnyIntegerType_StillCompares()
    {
        var big = new string('9', 40);
        var bigger = big + "1"; // one digit longer, so numerically greater
        Assert.Equal([$"x{big}.m4b", $"x{bigger}.m4b"], Sorted($"x{bigger}.m4b", $"x{big}.m4b"));
    }

    [Fact]
    public void ANumberedDirectory_OrdersByItsNumberToo()
    {
        Assert.Equal(
            [Path.Combine("Disc 2", "a.m4b"), Path.Combine("Disc 10", "a.m4b")],
            Sorted(Path.Combine("Disc 10", "a.m4b"), Path.Combine("Disc 2", "a.m4b")));
    }

    [Fact]
    public void IdenticalNames_CompareEqual()
    {
        Assert.Equal(0, NaturalPathComparer.Instance.Compare("Book 3.m4b", "Book 3.m4b"));
    }
}
