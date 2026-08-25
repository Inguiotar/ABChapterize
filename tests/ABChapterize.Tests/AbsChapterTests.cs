// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Abs;
using ABChapterize.Audio;
using ABChapterize.Errors;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for the two ends of the chapter round trip: <see cref="AbsChapterMerge"/>, which decides
/// what marks a fetched book already has, and <see cref="AbsChapterPush"/>, which turns finished
/// marks into what the server stores.
/// </summary>
public sealed class AbsChapterTests
{
    /// <summary>A probe result carrying the given marks and a fixed duration.</summary>
    /// <param name="chapters">What the downloaded file turned out to hold.</param>
    private static MediaInfo Probed(params Chapter[] chapters)
        => new(3600, 1000, chapters.Length, "aac", "LC", ExistingChapterList: chapters);

    /// <summary>An audio file record carrying the given server-side chapter list.</summary>
    /// <param name="chapters">What Audiobookshelf holds for the book.</param>
    private static IReadOnlyList<Chapter> Held(params Chapter[] chapters) => chapters;

    [Fact]
    public void Merge_ServerListWins()
    {
        var (info, note) = AbsChapterMerge.Apply(
            Probed(new Chapter(0, "One"), new Chapter(100, "Two")),
            Held(new Chapter(0, "A"), new Chapter(50, "B"), new Chapter(90, "C")));

        Assert.Equal(3, info.ChapterCount);
        Assert.Equal([0, 50, 90], info.ExistingChapters.Select(c => c.StartSeconds));
        Assert.Contains("Audiobookshelf has 3", note);
    }

    [Fact]
    public void Merge_WithNothingOnTheServer_KeepsWhatTheFileCarries()
    {
        var (info, note) = AbsChapterMerge.Apply(Probed(new Chapter(0, "One")), Held());

        Assert.Equal(1, info.ChapterCount);
        Assert.Equal(0, info.ExistingChapters[0].StartSeconds);
        Assert.Contains("no chapters", note);
    }

    [Fact]
    public void Merge_WithNothingOnEitherSide_SaysNothing()
    {
        var (info, note) = AbsChapterMerge.Apply(Probed(), Held());

        Assert.Equal(0, info.ChapterCount);
        Assert.Equal("", note);
    }

    [Fact]
    public void Merge_WithAnEmptyFile_ReportsWhereTheMarksCameFrom()
    {
        var (info, note) = AbsChapterMerge.Apply(Probed(), Held(new Chapter(0, "A"), new Chapter(60, "B")));

        Assert.Equal(2, info.ChapterCount);
        Assert.Contains("the file carries none", note);
    }

    /// <summary>
    /// Positions only, and within a second: re-muxing a file through another tool moves marks by
    /// fractions, and reporting that as a disagreement would fire the note on books nobody has
    /// touched. Titles differ for reasons that say nothing about whether a book is marked.
    /// </summary>
    [Fact]
    public void Merge_ListsThatAgreeOnPositions_ReportNothing()
    {
        var (_, note) = AbsChapterMerge.Apply(
            Probed(new Chapter(0, "Intro"), new Chapter(100, "Chapter 1")),
            Held(new Chapter(0.4, "Opening"), new Chapter(100.5, "1")));

        Assert.Equal("", note);
    }

    [Fact]
    public void Merge_AMarkMovedFurtherThanThat_IsReported()
    {
        var (_, note) = AbsChapterMerge.Apply(
            Probed(new Chapter(0, "Intro"), new Chapter(100, "Chapter 1")),
            Held(new Chapter(0, "Intro"), new Chapter(140, "Chapter 1")));

        Assert.NotEqual("", note);
    }

    /// <summary>
    /// <see cref="AbsChapterMerge.SameMarks"/> answers a different question from the note above it
    /// - "is there anything left to do" rather than "are these the same marks" - so it keeps the
    /// one-second tolerance on positions and adds the titles the note ignores.
    /// </summary>
    [Fact]
    public void SameMarks_ToleratesADriftedPositionButNotADifferentTitle()
    {
        Chapter[] settled = [new Chapter(0, "Intro"), new Chapter(100, "Chapter 1")];

        Assert.True(AbsChapterMerge.SameMarks(
            settled, [new Chapter(0.4, "Intro"), new Chapter(100.5, "Chapter 1")]));
        Assert.False(AbsChapterMerge.SameMarks(
            settled, [new Chapter(0, "Opening"), new Chapter(100, "Chapter 1")]));
        Assert.False(AbsChapterMerge.SameMarks(settled, [new Chapter(0, "Intro")]));
        Assert.False(AbsChapterMerge.SameMarks(settled, []));
    }

    /// <summary>Two empty lists are the same marks, which is what makes "the server has nothing and
    /// so has the file" a skip rather than an empty write.</summary>
    [Fact]
    public void SameMarks_TwoEmptyListsAgree()
        => Assert.True(AbsChapterMerge.SameMarks([], []));

    [Fact]
    public void Build_DerivesEachEndFromTheNextStart()
    {
        var wire = AbsChapterPush.Build(
            [new Chapter(0, "Intro"), new Chapter(120, "Chapter 1"), new Chapter(600, "Chapter 2")],
            durationSeconds: 1800);

        Assert.Equal([0, 1, 2], wire.Select(c => c.Id));
        Assert.Equal([0d, 120, 600], wire.Select(c => c.Start));
        Assert.Equal([120d, 600, 1800], wire.Select(c => c.End));
        Assert.Equal(["Intro", "Chapter 1", "Chapter 2"], wire.Select(c => c.Title));
    }

    /// <summary>A duration the probe could not establish leaves the last chapter ending at its own
    /// start rather than before it, which Audiobookshelf would refuse.</summary>
    [Fact]
    public void Build_WithNoUsableDuration_NeverEndsBeforeItStarts()
    {
        var wire = AbsChapterPush.Build([new Chapter(0, "Intro"), new Chapter(120, "One")], durationSeconds: 0);

        Assert.Equal(120, wire[^1].Start);
        Assert.Equal(120, wire[^1].End);
    }

    [Fact]
    public async Task PushAsync_RefusesAnEmptyList()
    {
        var book = new AbsBook("id", "Book", null, "folder", 1, 0, 3600);
        var connection = AbsConnection.Resolve("host:9", "key", null, null);
        using var session = new AbsSession(connection, AbsRetryPolicy.None);

        // Refused before a single byte goes anywhere, which is the whole point: the update replaces
        // the list rather than merging into it, so an empty one deletes what the server has.
        var error = await Assert.ThrowsAsync<AppError>(
            () => AbsChapterPush.PushAsync(session, book, [], 3600, CancellationToken.None));
        Assert.Contains("delete", error.Message);
    }
}
