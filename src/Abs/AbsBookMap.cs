// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Errors;
using ABChapterize.Processing;

namespace ABChapterize.Abs;

/// <summary>
/// One line of a book mapping file: which local file it is about, and which book on the server
/// that file is a copy of.
/// </summary>
/// <param name="FileName">The local file the entry names, exactly as written - a bare file name,
/// with or without its extension.</param>
/// <param name="Book">The book, as a selector: <c>item:ID</c>, or a title. Null where the entry
/// says this file has no book on the server at all.</param>
/// <param name="Origin">Path of the mapping file the entry was read from, which a parse error
/// names in full.</param>
public sealed record AbsBookMapping(string FileName, AbsSelector? Book, string Origin)
{
    /// <summary>How a match note names the file this entry came from - its bare name, the folder
    /// it sits in being the one the file being processed sits in too.</summary>
    public string Where => Path.GetFileName(Origin);
}

/// <summary>
/// The <c>--abs-map</c> mapping file: what to do when nothing about a local file names the book it
/// belongs to, and the user has to say it outright.
/// </summary>
/// <remarks>
/// <para>
/// <b>An entry is a clue, not a bypass.</b> What it supplies is the one thing
/// <see cref="AbsItemMatcher"/> could not work out for itself - which book this is - and it is
/// handed to the same search as the most trustworthy clue there is, ahead of the tags. Everything
/// downstream is unchanged, the <see cref="AbsItemMatcher.SameRecordingSeconds"/> test included: a
/// hand-written line is still a name, and a name is still not evidence that two things are the same
/// recording. A typo that pairs a book with one part of a split recording would put a whole book's
/// marks past that part's end, which is exactly as destructive whether the wrong pairing came from
/// an album tag or from a line in a file.
/// </para>
/// <para>
/// <b>The right-hand side is an ordinary <see cref="AbsSelector"/></b> rather than a grammar of its
/// own, so <c>item:ID</c> and a bare title both work and a title keeps its own colon. Only those two
/// kinds are accepted: <c>library:</c>, <c>series:</c>, <c>collection:</c> and <c>all</c> name sets
/// of books, and what an entry has to name is one.
/// </para>
/// <para>
/// <b><c>=</c> separates, not <c>:</c></b>, which the sibling <c>--custom-file</c> uses - here the
/// right-hand side is itself allowed to contain a colon, twice over (the <c>item:</c> prefix, and a
/// library title's own punctuation), so a colon could not delimit anything. The <em>first</em>
/// <c>=</c> is the separator; a file name that contains one can be quoted to say so.
/// </para>
/// <para>
/// <b>A later entry wins over an earlier one</b>, whether the two are in the same file or in two
/// files. That is one rule rather than two: mapping files layer the way options do - outermost
/// folder, then inner folders, then the command line - so a nearer entry has to win, and refusing a
/// repeat within a single file would make the same two lines mean different things depending on
/// whether somebody had split them across two folders.
/// </para>
/// <para>
/// Notes: what an item id really looks like on a current server and why nothing here checks, and
/// the sibling option whose separator this one could not borrow.
/// <include file='../../notes/Abs/AbsBookMap.xml' path='doc/member[@name="AbsBookMap"]/*' />
/// </para>
/// </remarks>
public static class AbsBookMap
{
    /// <summary>How an entry says this file has no book on the server. An empty right-hand side
    /// means the same thing.</summary>
    public const string NoBook = "none";

    /// <summary>
    /// Reads a mapping file: one entry per line, blank lines and <c>#</c> comment lines skipped.
    /// </summary>
    /// <param name="path">Path of the mapping file.</param>
    /// <returns>Its entries, in the order they were written.</returns>
    /// <exception cref="CliError">Thrown when the file cannot be read, holds no entry at all, or
    /// holds a malformed one - named with its line number.</exception>
    public static IReadOnlyList<AbsBookMapping> ParseFile(string path)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            // NotSupportedException among the rest so that a path shape the platform refuses is
            // reported as the command line error it is, the way --custom-file reports its own,
            // rather than escaping with a type name in front of it.
            throw new CliError($"Cannot read --abs-map \"{path}\": {ex.Message}");
        }

        var mappings = new List<AbsBookMapping>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            mappings.Add(ParseOne(line, $"--abs-map \"{path}\", line {i + 1}", path));
        }
        if (mappings.Count == 0)
            throw new CliError($"--abs-map \"{path}\" holds no mapping at all.");
        return mappings;
    }

    /// <summary>
    /// The entry that names a local file, or null where none does.
    /// </summary>
    /// <param name="mappings">Every entry the run and the file's folders contributed, in reading
    /// order: outermost folder first, the command line last.</param>
    /// <param name="localPath">Path of the local audio file.</param>
    /// <returns>The entry, or null when this file is not mapped and the ordinary clues decide.</returns>
    /// <remarks>
    /// Searched from the back, which is what makes a later entry win; see the type remarks.
    /// </remarks>
    public static AbsBookMapping? Find(IReadOnlyList<AbsBookMapping> mappings, string localPath)
    {
        for (var i = mappings.Count - 1; i >= 0; i--)
            if (Names(mappings[i], localPath))
                return mappings[i];
        return null;
    }

    /// <summary>
    /// Whether an entry is about this file.
    /// </summary>
    /// <param name="entry">The mapping entry.</param>
    /// <param name="localPath">Path of the local audio file.</param>
    /// <remarks>
    /// Four spellings are accepted for one reason each. With and without the extension, because an
    /// entry is typed by hand and the extension carries no information here. And against the
    /// <c>.missing-marks</c>-stripped name as well as the real one, because this tool renames the
    /// files it cannot finish: an entry that stopped working the moment a run parked its book under
    /// <c>Mort.missing-marks-7-8.m4b</c> would fail in precisely the situation it was written for -
    /// the same reason <see cref="AbsItemMatcher"/> strips the tag off its own file-name clue.
    /// <para>
    /// Case-insensitive, and exact otherwise. The normalization the rest of the matching uses is
    /// there to let two spellings of a <em>title</em> meet; a file name is something the user
    /// copied off their own disk, and matching it loosely would let one entry claim a neighbouring
    /// file.
    /// </para>
    /// </remarks>
    private static bool Names(AbsBookMapping entry, string localPath)
    {
        var actual = Path.GetFileName(localPath);
        var stripped = Path.GetFileName(MissingMarksTag.StripFrom(localPath));
        return Same(entry.FileName, actual)
               || Same(entry.FileName, stripped)
               || Same(entry.FileName, Path.GetFileNameWithoutExtension(actual))
               || Same(entry.FileName, Path.GetFileNameWithoutExtension(stripped));
    }

    /// <summary>Whether two file names are the same name.</summary>
    /// <param name="a">The name an entry wrote.</param>
    /// <param name="b">The name on disk.</param>
    private static bool Same(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses one <c>file name = book</c> entry.</summary>
    /// <param name="text">The line, already trimmed.</param>
    /// <param name="where">How an error message names this line.</param>
    /// <param name="origin">Path of the file it came from.</param>
    /// <exception cref="CliError">Thrown for a missing separator, an empty file name, an
    /// unterminated quoted one, or a selector naming something other than one book.</exception>
    private static AbsBookMapping ParseOne(string text, string where, string origin)
    {
        var separator = FindSeparator(text, where);
        var name = Unquote(text[..separator].Trim());
        var book = text[(separator + 1)..].Trim();

        if (name.Length == 0)
            throw new CliError($"{where}: the file name before the \"=\" must not be empty.");
        // An empty right-hand side and the word "none" say the same thing, an entry being just as
        // likely to be written by deleting a book as by typing a word. A book genuinely called
        // "none" is still reachable, spelled "title:none".
        if (book.Length == 0 || book.Equals(NoBook, StringComparison.OrdinalIgnoreCase))
            return new AbsBookMapping(name, null, origin);

        var selector = AbsSelector.Parse(book);
        if (selector.Kind is not (AbsSelectorKind.Item or AbsSelectorKind.Title))
            throw new CliError(
                $"{where}: \"{book}\" names a set of books, and an entry has to name one. "
                + "Write \"item:ID\", or the book's title, or \"none\" for a file the server has no "
                + "book for.");
        return new AbsBookMapping(name, selector, origin);
    }

    /// <summary>
    /// The index of the <c>=</c> separating the file name from the book: after the closing quote
    /// for a quoted name, the first one otherwise.
    /// </summary>
    /// <param name="text">The line, already trimmed.</param>
    /// <param name="where">How an error message names this line.</param>
    /// <exception cref="CliError">Thrown when there is no separator, or a quoted name never
    /// closes.</exception>
    private static int FindSeparator(string text, string where)
    {
        var from = 0;
        if (text.StartsWith('"'))
        {
            var closing = text.IndexOf('"', 1);
            if (closing < 0)
                throw new CliError($"{where}: the quoted file name has no closing \".");
            from = closing + 1;
        }
        return text.IndexOf('=', from) is var equals and >= 0
            ? equals
            : throw new CliError(
                $"{where}: expected \"file name = book\", but there is no \"=\" to separate the two.");
    }

    /// <summary>Strips one surrounding layer of quotes, which is how a file name containing the
    /// separator is written.</summary>
    /// <param name="name">The file name as typed.</param>
    private static string Unquote(string name)
        => name.Length >= 2 && name.StartsWith('"') && name.EndsWith('"')
            ? name[1..^1]
            : name;
}
