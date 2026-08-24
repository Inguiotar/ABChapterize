// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Audio;
using ABChapterize.Errors;
using ABChapterize.Processing;

namespace ABChapterize.Abs;

/// <summary>
/// Turns the selectors typed on the command line into the books a run will work on, and fetches the
/// per-book detail once one is about to be.
/// </summary>
/// <remarks>
/// <para>
/// Every listing it reads is cached for the lifetime of the run. A command naming three titles
/// would otherwise pull the whole library down three times, and a library listing is the one
/// request here whose cost grows with the size of the server.
/// </para>
/// <para>
/// Caching is also what makes the selection stable: a scan finishing mid-run cannot make the
/// second selector see a different library from the first, so the set of books a run reports at the
/// start is the set it processes.
/// </para>
/// </remarks>
public sealed class AbsCatalog
{
    private readonly AbsSession _session;

    /// <summary>Where selection notes go, or null when nothing is listening.</summary>
    private readonly Action<string>? _log;

    /// <summary>The server's book libraries, fetched once.</summary>
    private IReadOnlyList<AbsWire.Library>? _libraries;

    /// <summary>Each library's books, by library id, fetched once per library.</summary>
    private readonly Dictionary<string, IReadOnlyList<AbsBook>> _books = new(StringComparer.Ordinal);

    /// <summary>Every collection on the server, fetched once.</summary>
    private IReadOnlyList<AbsWire.Collection>? _collections;

    /// <summary>
    /// How many items one listing request asks for. Audiobookshelf accepts limit=0 for "everything",
    /// but a paged loop costs one extra request on a small library and keeps working if that ever
    /// changes, so the loop is what runs.
    /// </summary>
    private const int PageSize = 500;

    /// <summary>Creates a catalogue over an open session.</summary>
    /// <param name="session">The authenticated session to ask.</param>
    /// <param name="log">Sink for selection notes, or null.</param>
    public AbsCatalog(AbsSession session, Action<string>? log = null)
    {
        _session = session;
        _log = log;
    }

    /// <summary>
    /// Resolves every selector and returns their books, each book once.
    /// </summary>
    /// <param name="selectors">The selectors, in the order they were typed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The selected books, in a stable order.</returns>
    /// <exception cref="AppError">Thrown when a selector matches nothing - a typo that quietly
    /// selected part of what was asked for would be worse than a stopped run.</exception>
    public async Task<IReadOnlyList<AbsBook>> SelectAsync(
        IReadOnlyList<AbsSelector> selectors, CancellationToken ct)
    {
        var picked = new Dictionary<string, AbsBook>(StringComparer.Ordinal);
        foreach (var selector in selectors)
        {
            var found = await ResolveAsync(selector, ct);
            if (found.Count == 0)
                throw new AppError($"Selector \"{selector.Raw}\" matched no book on {_session.Describe}.");
            _log?.Invoke($"{selector.Raw}: {found.Count} book(s)");
            foreach (var book in found)
                picked.TryAdd(book.ItemId, book);
        }
        // Natural order over the item folder, so a numbered series lists as 1, 2, ... 10 rather than
        // 1, 10, 2 - the same ordering a folder of the same books on disk would be processed in.
        return [.. picked.Values
            .OrderBy(b => b.RelativePath, NaturalPathComparer.Instance)
            .ThenBy(b => b.Title, NaturalPathComparer.Instance)];
    }

    /// <summary>Resolves one selector.</summary>
    /// <param name="selector">The selector to resolve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The books it names, possibly none.</returns>
    private async Task<IReadOnlyList<AbsBook>> ResolveAsync(AbsSelector selector, CancellationToken ct)
        => selector.Kind switch
        {
            AbsSelectorKind.All => await EveryBookAsync(ct),
            AbsSelectorKind.Library => await LibraryBooksAsync(selector, ct),
            AbsSelectorKind.Series => await SeriesBooksAsync(selector, ct),
            AbsSelectorKind.Collection => await CollectionBooksAsync(selector, ct),
            AbsSelectorKind.Item => [await ItemAsync(selector.Value, ct)],
            _ => await TitleBooksAsync(selector, ct),
        };

    /// <summary>Every book of every book library on the server.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyList<AbsBook>> EveryBookAsync(CancellationToken ct)
    {
        var all = new List<AbsBook>();
        foreach (var library in await LibrariesAsync(ct))
            all.AddRange(await BooksOfAsync(library.Id, ct));
        return all;
    }

    /// <summary>The books of the one library a <c>library:</c> selector names.</summary>
    /// <param name="selector">The selector, whose value is the library name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="AppError">Thrown when no library matches, or when several do - naming a
    /// library is naming one, so an ambiguous name is a question rather than a set.</exception>
    private async Task<IReadOnlyList<AbsBook>> LibraryBooksAsync(AbsSelector selector, CancellationToken ct)
    {
        var libraries = await LibrariesAsync(ct);
        var match = PickOne(libraries, l => l.Name, selector, "library", () => NameList(libraries, l => l.Name));
        return await BooksOfAsync(match.Id, ct);
    }

    /// <summary>The books of the one series a <c>series:</c> selector names, searched across every
    /// library.</summary>
    /// <param name="selector">The selector, whose value is the series name.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<IReadOnlyList<AbsBook>> SeriesBooksAsync(AbsSelector selector, CancellationToken ct)
    {
        var series = new List<AbsWire.Series>();
        foreach (var library in await LibrariesAsync(ct))
            // Paged rather than asked for in one go: the series endpoint reads limit=0 as "none"
            // where the item endpoint reads it as "all", so a library measured that way came back
            // empty and every series: selector said it matched nothing (found 2026-08-24 against
            // Audiobookshelf 2.36.0).
            for (var page = 0; ; page++)
            {
                var response = await _session.GetAsync<AbsWire.SeriesPage>(
                    $"/api/libraries/{Uri.EscapeDataString(library.Id)}/series"
                    + $"?limit={PageSize}&page={page}", ct);
                series.AddRange(response.Results);
                if (response.Results.Count < PageSize)
                    break;
            }

        var match = PickOne(series, s => s.Name, selector, "series", () => NameList(series, s => s.Name));
        return Books(match.Books);
    }

    /// <summary>The books of the one collection a <c>collection:</c> selector names.</summary>
    /// <param name="selector">The selector, whose value is the collection name.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<IReadOnlyList<AbsBook>> CollectionBooksAsync(AbsSelector selector, CancellationToken ct)
    {
        _collections ??= (await _session.GetAsync<AbsWire.Collections>("/api/collections", ct)).All;
        if (_collections.Count == 0)
            throw new AppError($"There are no collections on {_session.Describe}.");
        var match = PickOne(_collections, c => c.Name, selector, "collection",
            () => NameList(_collections, c => c.Name));
        return Books(match.Books);
    }

    /// <summary>The books whose title a bare or <c>title:</c> selector matches, across every
    /// library.</summary>
    /// <param name="selector">The selector, whose value is the title.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// A title selector deliberately yields every book it matches rather than insisting on one.
    /// Library titles carry their series and their number ("Silber Edition 087: Das Spiel des
    /// Laren"), so a partial title is the natural way to name a run of them, and the alternative -
    /// refusing anything ambiguous - would make the selector useless for exactly the case it is
    /// most wanted for. An exact title still wins outright, so naming one book gets one book.
    /// </remarks>
    private async Task<IReadOnlyList<AbsBook>> TitleBooksAsync(AbsSelector selector, CancellationToken ct)
    {
        var books = await EveryBookAsync(ct);
        var exact = books.Where(b => AbsSelector.MatchesExactly(b.Title, selector.Value)).ToList();
        return exact.Count > 0 ? exact : [.. books.Where(b => AbsSelector.Matches(b.Title, selector.Value))];
    }

    /// <summary>Fetches one item by its identifier.</summary>
    /// <param name="itemId">The library item identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="AppError">Thrown when the item does not exist or holds no media.</exception>
    private async Task<AbsBook> ItemAsync(string itemId, CancellationToken ct)
        => AbsBook.From(await _session.GetAsync<AbsWire.Item>($"/api/items/{Uri.EscapeDataString(itemId)}", ct))
           ?? throw new AppError($"Audiobookshelf item {itemId} holds no media.");

    /// <summary>
    /// Fetches the audio file and chapter list of one book, which is what a run needs once it has
    /// decided to work on it.
    /// </summary>
    /// <param name="book">The book to look at.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Its single audio file and Audiobookshelf's own chapter list.</returns>
    /// <exception cref="AppError">Thrown when the item turns out not to hold exactly one audio
    /// file after all - the selection screened on a count the server had already sent, and this is
    /// the point at which the files themselves are seen.</exception>
    public async Task<AbsBookFile> LoadFileAsync(AbsBook book, CancellationToken ct)
    {
        var item = await _session.GetAsync<AbsWire.Item>(
            $"/api/items/{Uri.EscapeDataString(book.ItemId)}", ct);
        var files = item.Media?.AudioFiles ?? [];
        if (files.Count != 1)
            throw new AppError(
                $"\"{book.Title}\" holds {files.Count} audio file(s); ABS mode works on books that are one file.");
        var metadata = files[0].Metadata
                       ?? throw new AppError($"Audiobookshelf named no file for \"{book.Title}\".");

        // Sorted rather than trusted: everything downstream - the merge, the skip decision,
        // --verify - reads this as a chapter list in play order, and ABS stores what it was given.
        var chapters = (item.Media?.Chapters ?? [])
            .OrderBy(c => c.Start)
            .Select(c => new Chapter(c.Start, c.Title))
            .ToList();
        return new AbsBookFile(files[0].Ino, metadata.Filename, metadata.Size, chapters);
    }

    /// <summary>The server's book libraries, fetched once and remembered.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="AppError">Thrown when the account can see no book library at all.</exception>
    private async Task<IReadOnlyList<AbsWire.Library>> LibrariesAsync(CancellationToken ct)
    {
        if (_libraries != null)
            return _libraries;
        var response = await _session.GetAsync<AbsWire.Libraries>("/api/libraries", ct);
        // Podcast libraries are dropped here rather than refused later: they hold no books, so a
        // "library:" selector naming one is a mistake worth an error about the name, and "all"
        // should walk past them in silence.
        _libraries = [.. response.All.Where(l => l.MediaType is "book" or "")];
        if (_libraries.Count == 0)
            throw new AppError($"{_session.Describe} has no book libraries this account can see.");
        return _libraries;
    }

    /// <summary>One library's books, paged through and remembered.</summary>
    /// <param name="libraryId">The library to list.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<IReadOnlyList<AbsBook>> BooksOfAsync(string libraryId, CancellationToken ct)
    {
        if (_books.TryGetValue(libraryId, out var cached))
            return cached;

        var books = new List<AbsBook>();
        for (var page = 0; ; page++)
        {
            var response = await _session.GetAsync<AbsWire.ItemPage>(
                $"/api/libraries/{Uri.EscapeDataString(libraryId)}/items"
                + $"?limit={PageSize}&page={page}&minified=1", ct);
            books.AddRange(Books(response.Results));
            if (response.Results.Count < PageSize || books.Count >= response.Total)
                break;
        }
        _books[libraryId] = books;
        return books;
    }

    /// <summary>Converts wire items into books, dropping any that carry no media.</summary>
    /// <param name="items">The items as the server sent them, or null.</param>
    private static IReadOnlyList<AbsBook> Books(IEnumerable<AbsWire.Item>? items)
        => items == null ? [] : [.. items.Select(AbsBook.From).OfType<AbsBook>()];

    /// <summary>
    /// What a "no such thing" message offers instead, capped so that a server with two hundred
    /// series does not answer a typo with a screenful.
    /// </summary>
    /// <typeparam name="T">The candidate type.</typeparam>
    /// <param name="candidates">Everything that could have been meant.</param>
    /// <param name="nameOf">How to read a candidate name.</param>
    private static string NameList<T>(IReadOnlyList<T> candidates, Func<T, string> nameOf)
    {
        const int shown = 12;
        var names = candidates.Take(shown).Select(c => $"\"{nameOf(c)}\"");
        return string.Join(", ", names) + (candidates.Count > shown ? $", ... ({candidates.Count} in all)" : "");
    }

    /// <summary>
    /// Picks the one candidate a selector names, preferring an exact name over a partial one.
    /// </summary>
    /// <typeparam name="T">The candidate type - a library, a series or a collection.</typeparam>
    /// <param name="candidates">Everything the selector could mean.</param>
    /// <param name="nameOf">How to read a candidate's name.</param>
    /// <param name="selector">The selector, for its value and its wording in messages.</param>
    /// <param name="kind">What is being picked, for the message.</param>
    /// <param name="listAlternatives">What to offer when nothing matched; called only then, so a
    /// listing is not built for the ordinary case.</param>
    /// <exception cref="AppError">Thrown when nothing matches or when several things do.</exception>
    private static T PickOne<T>(
        IReadOnlyList<T> candidates, Func<T, string> nameOf, AbsSelector selector, string kind,
        Func<string> listAlternatives)
    {
        var exact = candidates.Where(c => AbsSelector.MatchesExactly(nameOf(c), selector.Value)).ToList();
        var matches = exact.Count > 0
            ? exact
            : candidates.Where(c => AbsSelector.Matches(nameOf(c), selector.Value)).ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new AppError(
                $"No {kind} matching \"{selector.Value}\". Available: {listAlternatives()}."),
            _ => throw new AppError(
                $"\"{selector.Value}\" matches {matches.Count} {kind} entries "
                + $"({string.Join(", ", matches.Select(m => $"\"{nameOf(m)}\""))}); name one of them exactly."),
        };
    }
}
