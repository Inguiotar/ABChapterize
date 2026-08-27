// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Abs;
using ABChapterize.Audio;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="AbsChapterPull"/>: which of the two chapter lists a matched file may take,
/// and what a match that came back empty-handed leaves behind. Whether the two sides are the same
/// recording is settled a step earlier - see <see cref="AbsItemMatcherTests"/>.
/// </summary>
public sealed class AbsChapterPullTests
{
    /// <summary>A book of the given play time; the rest is beside the point here.</summary>
    /// <param name="seconds">Play time as the library reports it.</param>
    private static AbsBook Book(double seconds)
        => new("id", "DW35 - Wintersmith", null, "folder", 1, 0, seconds);

    /// <summary>A probe result of the given play time, carrying the given marks.</summary>
    /// <param name="seconds">Play time of the local file.</param>
    /// <param name="chapters">The marks it carries.</param>
    private static MediaInfo Probed(double seconds, params Chapter[] chapters)
        => new(seconds, 1000, chapters.Length, "aac", "LC", ExistingChapterList: chapters);

    private static readonly Chapter[] ServerList =
        [new Chapter(0, "One"), new Chapter(1800, "Two")];

    [Fact]
    public void AMatchedBook_HandsOverItsChapters()
    {
        var pull = AbsChapterPull.Decide(
            new AbsMatch(Book(36000), "matched by album tag"), ServerList, Probed(36000));

        Assert.NotNull(pull.Book);
        Assert.Equal(2, pull.FromServer.Count);
        Assert.Equal("matched by album tag", pull.Note);
    }

    [Fact]
    public void NoMatchAtAll_KeepsTheMatchersExplanation()
    {
        var pull = AbsChapterPull.Decide(
            new AbsMatch(null, "no book on the server matches this file"), [], Probed(36000));

        Assert.Null(pull.Book);
        Assert.Empty(pull.FromServer);
        Assert.Equal("no book on the server matches this file", pull.Note);
    }

    /// <summary>The file's own marks are carried through whatever the verdict, that being the list
    /// the reconciliation compares against.</summary>
    [Fact]
    public void TheFilesOwnMarksAreCarriedThroughEvenWhenThereIsNoBook()
    {
        var own = new Chapter(0, "Only");

        var pull = AbsChapterPull.Decide(
            new AbsMatch(null, "not the same recording"), ServerList, Probed(300, own));

        Assert.Null(pull.Book);
        Assert.Equal([own], pull.FromFile);
    }

    /// <summary>
    /// A refused match takes any list read before it with it. The server's chapters belong to the
    /// book, so keeping them after the book has been dropped is precisely how marks from another
    /// timeline would reach a file.
    /// </summary>
    [Fact]
    public void ARefusedMatch_DropsTheServersListWithTheBook()
    {
        var pull = AbsChapterPull.Decide(
            new AbsMatch(null, "not the same recording"), ServerList, Probed(300));

        Assert.Null(pull.Book);
        Assert.Empty(pull.FromServer);
    }
}
