// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Abs;
using ABChapterize.Audio;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for <see cref="AbsChapterPull"/>: what a local file may take from the book it was matched
/// to, and the one case where the match is right and the marks are still wrong.
/// </summary>
public sealed class AbsChapterPullTests
{
    /// <summary>A book of the given play time; the rest is beside the point here.</summary>
    /// <param name="seconds">Play time as the library reports it.</param>
    /// <param name="audioFiles">How many files the item holds.</param>
    private static AbsBook Book(double seconds, int audioFiles = 1)
        => new("id", "DW35 - Wintersmith", null, "folder", audioFiles, 0, seconds);

    /// <summary>A probe result of the given play time, carrying the given marks.</summary>
    /// <param name="seconds">Play time of the local file.</param>
    /// <param name="chapters">The marks it carries.</param>
    private static MediaInfo Probed(double seconds, params Chapter[] chapters)
        => new(seconds, 1000, chapters.Length, "aac", "LC", ExistingChapterList: chapters);

    private static readonly Chapter[] ServerList =
        [new Chapter(0, "One"), new Chapter(1800, "Two")];

    [Fact]
    public void ABookOfTheSameLength_HandsOverItsChapters()
    {
        var pull = AbsChapterPull.Decide(
            new AbsMatch(Book(36000), "matched by album tag"), ServerList, Probed(36000));

        Assert.NotNull(pull.Book);
        Assert.Equal(2, pull.FromServer.Count);
        Assert.Equal("matched by album tag", pull.Note);
    }

    /// <summary>
    /// Two encodes of one book differ by an encoder's padding and a trimmed tail, which is well
    /// inside the tolerance and must not stop a pull.
    /// </summary>
    [Fact]
    public void ASecondOrTwoOfDifference_IsStillTheSameRecording()
    {
        var pull = AbsChapterPull.Decide(
            new AbsMatch(Book(36000), "matched by album tag"), ServerList, Probed(36002.5));

        Assert.NotNull(pull.Book);
        Assert.Equal(2, pull.FromServer.Count);
    }

    /// <summary>
    /// The case this class exists for. Every one of a split book's parts carries the same album tag
    /// as the whole, so the matcher recognizes each of them - and the item's chapter list describes
    /// the concatenated timeline, so writing it into a five-minute part would put nearly every mark
    /// past the file's own end.
    /// </summary>
    [Fact]
    public void OnePartOfASplitBook_TakesNothingAndIsNotPushedToEither()
    {
        var pull = AbsChapterPull.Decide(
            new AbsMatch(Book(36000, audioFiles: 135), "matched by album tag"),
            ServerList,
            Probed(300));

        // The book goes with the chapters, not just the chapters: a file whose timeline is not this
        // book's must not have marks sent to it either, and leaving the book here is exactly what
        // would let --abs-push do that.
        Assert.Null(pull.Book);
        Assert.Empty(pull.FromServer);
        Assert.Contains("not the same recording", pull.Note);
    }

    /// <summary>An abridgement, or a different edition of the same title: recognized by name, and
    /// hours apart.</summary>
    [Fact]
    public void ADifferentEditionOfTheSameTitle_IsRefusedToo()
    {
        var pull = AbsChapterPull.Decide(
            new AbsMatch(Book(36000), "matched by title tag"), ServerList, Probed(21600));

        Assert.Null(pull.Book);
        Assert.Contains("600 min", pull.Note);
        Assert.Contains("360 min", pull.Note);
    }

    /// <summary>
    /// The file count is deliberately not the test. A book joined into one file locally but kept as
    /// several on the server is the case the sister project produces, and its chapter list is
    /// exactly the right one for that file - which is why the play time decides and the file count
    /// does not.
    /// </summary>
    [Fact]
    public void ASplitBookJoinedLocally_IsAccepted()
    {
        var pull = AbsChapterPull.Decide(
            new AbsMatch(Book(36000, audioFiles: 19), "matched by folder name"),
            ServerList,
            Probed(36000));

        Assert.NotNull(pull.Book);
        Assert.Equal(2, pull.FromServer.Count);
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
    public void TheFilesOwnMarksAreCarriedThroughEvenWhenThePullIsRefused()
    {
        var own = new Chapter(0, "Only");

        var pull = AbsChapterPull.Decide(
            new AbsMatch(Book(36000), "matched by album tag"), ServerList, Probed(300, own));

        Assert.Null(pull.Book);
        Assert.Equal([own], pull.FromFile);
    }
}
