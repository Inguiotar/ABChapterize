// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;

namespace ABChapterize.Abs;

/// <summary>
/// One Audiobookshelf book as ABS mode selects it: enough to list it, to decide whether it can be
/// worked on at all, and to address it - but not yet which file to fetch.
/// </summary>
/// <param name="ItemId">The library item identifier, which the download and the chapter update are
/// both addressed to.</param>
/// <param name="Title">The book title as the library holds it.</param>
/// <param name="Author">The author, or null where the library knows none.</param>
/// <param name="RelativePath">The item folder relative to its library root, which is what tells two
/// books of the same title apart in a listing.</param>
/// <param name="AudioFileCount">How many audio files the item holds. Anything but one is refused;
/// see <see cref="IsSingleFile"/>.</param>
/// <param name="ChapterCount">How many chapters Audiobookshelf currently has for the item - its own
/// list, which need not be what is embedded in the audio.</param>
/// <param name="DurationSeconds">Total play time as the library reports it.</param>
/// <remarks>
/// Split from <see cref="AbsBookFile"/> because selecting a book and fetching one are answered by
/// two different requests: a library listing arrives minified, carrying counts instead of the audio
/// files and the chapter list, and asking for the full item costs a request per book. A run that
/// selects two hundred books and processes three should pay for three, so the detail is fetched
/// when a book is about to be worked on rather than when it is listed.
/// </remarks>
public sealed record AbsBook(
    string ItemId,
    string Title,
    string? Author,
    string RelativePath,
    int AudioFileCount,
    int ChapterCount,
    double DurationSeconds)
{
    /// <summary>
    /// Whether this book is one ABS mode can work on at all: exactly one audio file.
    /// </summary>
    /// <remarks>
    /// A split book is refused rather than handled. Audiobookshelf chapter list addresses the
    /// concatenated timeline of the whole item, which no single-file run can see; detecting each
    /// part separately would restart the chapter-number sequence at every file boundary, and the
    /// sequence is what almost every safeguard in <c>src\Detection\</c> reasons in. Joining a split
    /// book first is a different job from marking one.
    /// </remarks>
    public bool IsSingleFile => AudioFileCount == 1;

    /// <summary>
    /// What <c>--filter</c> regexp is matched against in ABS mode, standing in for the file path it
    /// matches against everywhere else: the item folder and the title, so that either half of what
    /// the user can see in their library is enough to select on.
    /// </summary>
    public string FilterText => RelativePath.Length > 0 ? $"{RelativePath}/{Title}" : Title;

    /// <summary>The book as one line of a listing: title, author and the folder it sits in.</summary>
    public string Describe
        => Title
           + (string.IsNullOrWhiteSpace(Author) ? "" : $" - {Author}")
           + (RelativePath.Length > 0 ? $" [{RelativePath}]" : "");

    /// <summary>
    /// The author line, from whichever of the two shapes the response used.
    /// </summary>
    /// <param name="metadata">The item metadata, or null.</param>
    /// <returns>The authors as one string, or null where the library knows none.</returns>
    /// <remarks>
    /// A minified response flattens the authors into <c>authorName</c> and a full one leaves them
    /// as a list, so a book listed one way and fetched the other would otherwise lose its author
    /// somewhere between selection and processing - which is precisely the kind of difference that
    /// makes a listing and a run disagree about which book they are talking about.
    /// </remarks>
    private static string? AuthorOf(AbsWire.Metadata? metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata?.AuthorName))
            return metadata.AuthorName;
        var names = metadata?.Authors?.Select(a => a.Name).Where(x => x.Length > 0).ToList() ?? [];
        return names.Count > 0 ? string.Join(", ", names) : null;
    }

    /// <summary>Builds a book from a wire item, or null when the item carries no media at all.</summary>
    /// <param name="item">The item as the server sent it, minified or full.</param>
    /// <returns>The book, or null for an item with nothing to work on.</returns>
    internal static AbsBook? From(AbsWire.Item item)
    {
        if (item.Media is not { } media)
            return null;
        // A full response carries the lists and a minified one the counts, and either may be the
        // shape at hand: series and collection endpoints embed their books in full, the library
        // listing is asked for minified. Counting the list where there is one keeps the two
        // agreeing rather than letting a zeroed count in a full response read as an empty book.
        var files = media.AudioFiles?.Count ?? media.NumAudioFiles;
        var chapters = media.Chapters?.Count ?? media.NumChapters;
        return new AbsBook(
            item.Id,
            string.IsNullOrWhiteSpace(media.Metadata?.Title) ? "(untitled)" : media.Metadata.Title,
            AuthorOf(media.Metadata),
            item.RelPath,
            files,
            chapters,
            media.Duration);
    }
}

/// <summary>
/// The audio file of a book, and the chapter list Audiobookshelf holds for it - what a run needs
/// once it has decided to work on the book.
/// </summary>
/// <param name="Ino">The file system inode the download endpoint addresses the file by.</param>
/// <param name="FileName">The file name on the server, reused for the local temporary copy so that
/// every log line and summary names something recognizable.</param>
/// <param name="SizeBytes">Size in bytes, which is the download progress bar total.</param>
/// <param name="Chapters">The chapter list from Audiobookshelf own database, in start order and
/// possibly empty. Held as ordinary <see cref="Chapter"/> values, without their ends, because an
/// end is always the next start and the tool has exactly one chapter type for that reason.</param>
public sealed record AbsBookFile(
    string Ino,
    string FileName,
    long SizeBytes,
    IReadOnlyList<Chapter> Chapters)
{
    /// <summary>The file extension, lower-cased, as the supported-format check reads it.</summary>
    public string Extension => Path.GetExtension(FileName).ToLowerInvariant();
}
