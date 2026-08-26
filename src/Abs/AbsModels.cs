// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Text.Json.Serialization;

namespace ABChapterize.Abs;

/// <summary>
/// The shapes this tool reads out of Audiobookshelf responses, and the one shape it writes back.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately partial: every class here names only the fields ABS mode actually uses, and the
/// deserializer is told to ignore the rest. An item response carries some forty members - progress,
/// tags, cover paths, ereader devices - and mirroring them would turn every upstream addition into
/// a merge conflict without buying anything. The cost of the choice is that a field this tool needs
/// later has to be added here first, which is the right way round.
/// </para>
/// <para>
/// Mutable properties with public setters rather than records, because that is what
/// <c>System.Text.Json</c> populates without a constructor-matching ceremony for optional members.
/// Nothing outside <see cref="AbsSession"/> and <see cref="AbsCatalog"/> ever sees one: the
/// catalogue converts them into <see cref="AbsBook"/> and <see cref="AbsBookFile"/> immediately, so
/// the rest of the tool never handles a half-populated wire object.
/// </para>
/// </remarks>
internal static class AbsWire
{
    /// <summary>Response of <c>GET /api/libraries</c>.</summary>
    internal sealed class Libraries
    {
        /// <summary>The libraries this account can see. Named for the wire field explicitly,
        /// a property being unable to share its name with the type enclosing it.</summary>
        [JsonPropertyName("libraries")]
        public List<Library> All { get; set; } = [];
    }

    /// <summary>One library.</summary>
    internal sealed class Library
    {
        /// <summary>Server-assigned identifier, used to address the library in later requests.</summary>
        public string Id { get; set; } = "";

        /// <summary>Display name, which is what a <c>library:</c> selector matches against.</summary>
        public string Name { get; set; } = "";

        /// <summary>"book" or "podcast"; ABS mode has nothing to offer a podcast library.</summary>
        public string MediaType { get; set; } = "";
    }

    /// <summary>Response of <c>GET /api/libraries/{id}/items</c>, one page of it.</summary>
    internal sealed class ItemPage
    {
        /// <summary>The items on this page.</summary>
        public List<Item> Results { get; set; } = [];

        /// <summary>How many items the library holds in total, which is how the paging loop knows
        /// when to stop.</summary>
        public int Total { get; set; }
    }

    /// <summary>One library item - a book, which may hold any number of audio files.</summary>
    internal sealed class Item
    {
        /// <summary>Server-assigned identifier; what <c>item:</c> selectors name and what the
        /// chapter update is addressed to.</summary>
        public string Id { get; set; } = "";

        /// <summary>Path on the *server*, shown in listings so a book can be told from a namesake.</summary>
        public string RelPath { get; set; } = "";

        /// <summary>When the item joined its library, in milliseconds since the Unix epoch; what
        /// <c>--newer-than</c> judges a book by. Zero when the server sent none, which is read as
        /// "no date on record" rather than as 1970 - see <see cref="AbsBook.AddedUtc"/>. Present in
        /// every shape this tool asks for, minified library listings and series-embedded books
        /// included (confirmed against 2.36.0, 2026-08-26).</summary>
        public long AddedAt { get; set; }

        /// <summary>The media payload; absent on a malformed or podcast item.</summary>
        public Media? Media { get; set; }
    }

    /// <summary>An item's media: its metadata, its audio files and its chapter list.</summary>
    internal sealed class Media
    {
        /// <summary>Title, author and the rest of the bibliographic data.</summary>
        public Metadata? Metadata { get; set; }

        /// <summary>The audio files, present only in a full (non-minified) response.</summary>
        public List<AudioFile>? AudioFiles { get; set; }

        /// <summary>
        /// The chapter list ABS keeps in its own database, present only in a full response. This is
        /// the list the web player shows and the one <c>POST /api/items/{id}/chapters</c> replaces -
        /// it is not necessarily what is embedded in the audio file.
        /// </summary>
        public List<Chapter>? Chapters { get; set; }

        /// <summary>Number of audio files, which a minified response carries in place of the list
        /// itself - and which is what ABS mode screens single-file books by.</summary>
        public int NumAudioFiles { get; set; }

        /// <summary>Number of chapters, the minified counterpart of <see cref="Chapters"/>.</summary>
        public int NumChapters { get; set; }

        /// <summary>Total play time in seconds across every audio file.</summary>
        public double Duration { get; set; }
    }

    /// <summary>A book's bibliographic metadata.</summary>
    internal sealed class Metadata
    {
        /// <summary>The book title, which is what a bare or <c>title:</c> selector matches.</summary>
        public string? Title { get; set; }

        /// <summary>
        /// Author, shown in listings to tell two books of the same name apart. Present in a
        /// minified response only; a full one carries <see cref="Authors"/> instead.
        /// </summary>
        public string? AuthorName { get; set; }

        /// <summary>
        /// The authors as a full response spells them. Both forms are read because both shapes
        /// reach this tool: the library listing is asked for minified, while an item fetched by id
        /// - and the books embedded in a series or a collection - arrive in full.
        /// </summary>
        public List<NamedEntity>? Authors { get; set; }

    }

    /// <summary>Anything the server names with an id and a name: an author, a series entry.</summary>
    internal sealed class NamedEntity
    {
        /// <summary>The name, which is the only part this tool has a use for.</summary>
        public string Name { get; set; } = "";
    }

    /// <summary>One audio file of an item.</summary>
    internal sealed class AudioFile
    {
        /// <summary>The file system inode, which is how a download request addresses the file.</summary>
        public string Ino { get; set; } = "";

        /// <summary>Name, extension and size.</summary>
        public FileMetadata? Metadata { get; set; }
    }

    /// <summary>An audio file's own file system metadata.</summary>
    internal sealed class FileMetadata
    {
        /// <summary>Bare file name, reused for the local temporary copy so log lines name something
        /// recognizable.</summary>
        public string Filename { get; set; } = "";

        /// <summary>Size in bytes - the download progress bar's total.</summary>
        public long Size { get; set; }
    }

    /// <summary>One chapter, in both directions: this is also what the update request sends.</summary>
    internal sealed class Chapter
    {
        /// <summary>Zero-based position in the list. ABS assigns these itself, but the update
        /// endpoint expects them present and consecutive.</summary>
        public int Id { get; set; }

        /// <summary>Start in seconds from the beginning of the book.</summary>
        public double Start { get; set; }

        /// <summary>End in seconds; the last chapter ends at the book duration.</summary>
        public double End { get; set; }

        /// <summary>Chapter title.</summary>
        public string Title { get; set; } = "";
    }

    /// <summary>The body of <c>POST /api/items/{id}/chapters</c>.</summary>
    internal sealed class ChapterUpdate
    {
        /// <summary>The complete new chapter list; the request replaces rather than merges.</summary>
        public List<Chapter> Chapters { get; set; } = [];
    }

    /// <summary>Response of <c>GET /api/libraries/{id}/series</c>, one page of it.</summary>
    internal sealed class SeriesPage
    {
        /// <summary>The series on this page, each with the books it holds.</summary>
        public List<Series> Results { get; set; } = [];
    }

    /// <summary>One series and its books.</summary>
    internal sealed class Series
    {
        /// <summary>Display name, which is what a <c>series:</c> selector matches.</summary>
        public string Name { get; set; } = "";

        /// <summary>The books, embedded in full rather than by reference.</summary>
        public List<Item>? Books { get; set; }
    }

    /// <summary>Response of <c>GET /api/collections</c>.</summary>
    internal sealed class Collections
    {
        /// <summary>Every collection across every library this account can see. Named for the
        /// wire field explicitly, a property being unable to share its name with the type
        /// enclosing it.</summary>
        [JsonPropertyName("collections")]
        public List<Collection> All { get; set; } = [];
    }

    /// <summary>One collection and its books.</summary>
    internal sealed class Collection
    {
        /// <summary>Display name, which is what a <c>collection:</c> selector matches.</summary>
        public string Name { get; set; } = "";

        /// <summary>The books, embedded in full rather than by reference.</summary>
        public List<Item>? Books { get; set; }
    }

    /// <summary>Response of <c>GET /api/libraries/{id}/search</c>.</summary>
    internal sealed class SearchResult
    {
        /// <summary>The book hits; the response also carries author, series and tag hits, which
        /// ABS mode has no use for.</summary>
        public List<SearchHit> Book { get; set; } = [];
    }

    /// <summary>One search hit, which wraps the item rather than being one.</summary>
    internal sealed class SearchHit
    {
        /// <summary>The item that matched.</summary>
        public Item? LibraryItem { get; set; }
    }

    /// <summary>Response of <c>POST /login</c> and of <c>POST /api/authorize</c>, which agree on
    /// the part this tool reads.</summary>
    internal sealed class Session
    {
        /// <summary>The authenticated account.</summary>
        public User? User { get; set; }
    }

    /// <summary>The authenticated account.</summary>
    internal sealed class User
    {
        /// <summary>Account name, echoed once at connection time so a run says who it is acting as.</summary>
        public string Username { get; set; } = "";

        /// <summary>Account kind - "root", "admin", "user" or "guest".</summary>
        public string Type { get; set; } = "";

        /// <summary>
        /// The long-lived JWT older servers hand out. Kept as a fallback because
        /// <see cref="AccessToken"/> only appeared in 2.26; a server predating it returns this one
        /// and nothing else.
        /// </summary>
        public string? Token { get; set; }

        /// <summary>The access token current servers hand out, preferred over <see cref="Token"/>.</summary>
        public string? AccessToken { get; set; }

        /// <summary>What this account may do, checked up front so a run that cannot write chapters
        /// says so before downloading a gigabyte.</summary>
        public Permissions? Permissions { get; set; }
    }

    /// <summary>The subset of an account permissions this tool depends on.</summary>
    internal sealed class Permissions
    {
        /// <summary>Whether the account may download audio files.</summary>
        public bool Download { get; set; }

        /// <summary>Whether the account may change item metadata, chapters included.</summary>
        public bool Update { get; set; }
    }
}
