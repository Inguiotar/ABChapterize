// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ABChapterize.Errors;

namespace ABChapterize.Language.Phrases;

/// <summary>
/// The title a matched phrase writes, with its references to the phrase's capturing groups
/// resolved: <c>${name}</c> for the text a group captured, <c>${number}</c> for the chapter number
/// in digits however it was spoken, and <c>$roman{}</c>, <c>$upper{}</c>, <c>$lower{}</c> and
/// <c>$capital{}</c> for the conversions worth having ("Interlude XIV", "PART TWO").
/// <para>
/// References by index are gone: <c>$1</c> is refused with a message naming the fix rather than
/// silently becoming literal text, because a title that used to substitute and now does not is the
/// kind of change nobody notices until the file is written. <c>$$</c> is still a dollar sign, and
/// a group that did not take part in the match resolves to nothing - alternatives are one phrase
/// written several ways, and only one of them matched.
/// </para>
/// </summary>
public sealed partial class TitleTemplate
{
    /// <summary>One piece of a parsed template: either literal text or a group reference.</summary>
    /// <param name="Literal">The text to emit, for a literal piece.</param>
    /// <param name="Group">The group to substitute, for a reference.</param>
    /// <param name="Function">The conversion to apply; empty for a plain <c>${name}</c>.</param>
    private readonly record struct Piece(string Literal, string? Group = null, string Function = "");

    private readonly List<Piece> _pieces;

    /// <summary>The finished title of a template that references nothing, i.e. the literal pieces
    /// joined - which differs from <see cref="Raw"/> wherever a <c>$$</c> was written.</summary>
    private string? _plain;

    /// <summary>The template exactly as it was written - what a run fingerprint and the debug log
    /// report, and what a <c>--verify</c> comparison of two option sets works on.</summary>
    public string Raw { get; }

    /// <summary>Whether this template references any group at all. A template that does not is its
    /// own answer, which keeps a title holding an ordinary dollar amount out of the substitution
    /// machinery entirely.</summary>
    internal bool HasReferences => _pieces.Any(p => p.Group != null);

    /// <summary>Parses a title template.</summary>
    /// <param name="raw">The template as written.</param>
    /// <param name="what">How to name this title in an error message.</param>
    /// <exception cref="CliError">Thrown for an index reference, an unknown conversion, or a
    /// reference naming no group. An <em>unterminated</em> <c>${</c> is not an error: with no
    /// closing brace there is no reference to recognize, so it falls through to literal text like
    /// any other dollar sign.</exception>
    public TitleTemplate(string raw, string what)
    {
        Raw = raw;
        _pieces = Parse(raw, what);
    }

    /// <summary>
    /// The title to write for one match. Group references are resolved against
    /// <paramref name="match"/>, which must come from the alternative that produced the mark.
    /// </summary>
    /// <param name="match">The match this mark was produced from, or null when there is none - a
    /// bare-number announcement has no regex behind it, so every reference resolves to nothing.</param>
    /// <param name="language">Two-letter language code, for reading a spoken number back out of a
    /// captured group.</param>
    public string Resolve(Match? match, string language)
    {
        // Not Raw: a template without references still had its "$$" read, and handing back what was
        // typed would put both dollars in the title.
        if (!HasReferences)
            return _plain ??= string.Concat(_pieces.Select(p => p.Literal));
        var title = new StringBuilder();
        foreach (var piece in _pieces)
            title.Append(piece.Group == null
                ? piece.Literal
                : Convert(Captured(match, piece.Group), piece.Function, language));
        return title.ToString().Trim();
    }

    /// <summary>What one group captured, or "" when it did not take part in the match.</summary>
    /// <param name="match">The match, or null when the announcement had no regex behind it.</param>
    /// <param name="group">The group name.</param>
    private static string Captured(Match? match, string group)
        => match?.Groups[group] is { Success: true } captured ? captured.Value : "";

    /// <summary>
    /// Applies one conversion. <c>number</c> is not a conversion but a group whose plain form is
    /// already its digits, which is why <see cref="Parse"/> gives it the <c>digits</c> function
    /// rather than leaving it as written text: "XIII", "thirteen" and "13." are one chapter, and a
    /// title reading "Interlude thirteen" beside "Chapter 13" would be this tool disagreeing with
    /// itself.
    /// </summary>
    /// <param name="text">What the group captured.</param>
    /// <param name="function">The conversion name, or "" for none.</param>
    /// <param name="language">Two-letter language code, for reading a spoken number.</param>
    private static string Convert(string text, string function, string language) => function switch
    {
        "" => text,
        "upper" => text.ToUpperInvariant(),
        "lower" => text.ToLowerInvariant(),
        "capital" => Capitalize(text),
        // A group that holds no number keeps its text: the conversion is a request, and refusing
        // the whole mark over a title that cannot be spelled as a number would be out of proportion.
        "digits" => Number(text, language) is { } n ? n.ToString(CultureInfo.InvariantCulture) : text,
        _ => Number(text, language) is { } value and > 0 ? RomanNumerals.Format(value) : text,
    };

    /// <summary>The number a captured group holds, or null when it holds none.</summary>
    /// <param name="text">What the group captured.</param>
    /// <param name="language">Two-letter language code steering number-word parsing.</param>
    private static int? Number(string text, string language)
        => NumberWordParser.TryExtractNumber(text, language, out var number) ? number : null;

    /// <summary>Upper-cases the first letter and leaves the rest as it is - "an interlude" becomes
    /// "An interlude", not "An Interlude", the string being a title fragment rather than a title.</summary>
    /// <param name="text">What the group captured.</param>
    private static string Capitalize(string text)
        => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>
    /// Splits a template into literal text and references. Anything after a <c>$</c> that is not a
    /// reference, a conversion or another <c>$</c> is literal text, so an ordinary title survives
    /// untouched - the one exception being a digit, which used to be a reference and is refused
    /// rather than quietly re-read.
    /// </summary>
    /// <param name="raw">The template as written.</param>
    /// <param name="what">How to name this title in an error message.</param>
    /// <exception cref="CliError">Thrown for an index reference, an unknown conversion, or a
    /// reference naming no group. An <em>unterminated</em> <c>${</c> is not an error: with no
    /// closing brace there is no reference to recognize, so it falls through to literal text like
    /// any other dollar sign.</exception>
    private static List<Piece> Parse(string raw, string what)
    {
        var pieces = new List<Piece>();
        var literal = new StringBuilder();
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '$')
            {
                literal.Append(raw[i]);
                continue;
            }
            if (i + 1 < raw.Length && raw[i + 1] == '$')
            {
                literal.Append('$');
                i++;
                continue;
            }
            if (i + 1 < raw.Length && char.IsAsciiDigit(raw[i + 1]))
                throw new CliError(
                    $"{what}: \"${raw[i + 1]}\" refers to a capturing group by number, which is no " +
                    "longer supported. Name the group - \"(?<part>...)\" in the phrase, " +
                    "\"${part}\" in the title - or write \"$$\" for a literal dollar sign.");

            if (Reference(raw, i, what) is not { } reference)
            {
                literal.Append('$');
                continue;
            }
            pieces.Add(new Piece(literal.ToString()));
            literal.Clear();
            pieces.Add(reference.Piece);
            i = reference.End;
        }
        pieces.Add(new Piece(literal.ToString()));
        return pieces;
    }

    /// <summary>
    /// Reads one reference starting at a <c>$</c>: <c>${name}</c>, or a conversion name followed by
    /// <c>{name}</c>. Returns null when what follows is neither, which makes it ordinary text.
    /// </summary>
    /// <param name="raw">The whole template.</param>
    /// <param name="start">Index of the <c>$</c>.</param>
    /// <param name="what">How to name this title in an error message.</param>
    /// <exception cref="CliError">Thrown for an unknown conversion, or for a reference naming no
    /// group. An unterminated <c>{</c> simply fails to match and returns null.</exception>
    private static (Piece Piece, int End)? Reference(string raw, int start, string what)
    {
        var m = ReferenceSyntax().Match(raw, start);
        if (!m.Success || m.Index != start)
            return null;

        var function = m.Groups[1].Value.ToLowerInvariant();
        var group = m.Groups[2].Value.Trim();
        // A "$word{...}" whose word is not one of the conversions is a typo worth naming: the shape
        // is unmistakable, and treating it as literal text would put "$romen{n}" in a chapter title.
        if (!Conversions.Contains(function))
            throw new CliError(
                $"{what}: \"${m.Groups[1].Value}{{...}}\" is not a known conversion. Use " +
                $"{string.Join(", ", Conversions.Where(c => c.Length > 0).Select(c => $"$" + c + "{}"))}" +
                ", or \"${name}\" for the text as it was captured.");
        if (group.Length == 0)
            throw new CliError($"{what}: \"{m.Value}\" names no capturing group.");
        // ${number} is the digits of whatever was captured, which is the whole point of the group:
        // the notation a narrator happened to use is not what a chapter title should inherit.
        if (function.Length == 0 && group == PhraseAlternative.NumberGroup)
            function = "digits";
        return (new Piece("", group, function), m.Index + m.Length - 1);
    }

    /// <summary>Every group a template references, for validating it against the phrase's own
    /// groups before a run starts rather than mid-book.</summary>
    internal IEnumerable<string> ReferencedGroups
        => _pieces.Where(p => p.Group != null).Select(p => p.Group!).Distinct();

    /// <summary>
    /// Recognizes titles this template has written before, for the resume and <c>--verify</c>
    /// carry-over: a rewrite of a file's whole mark set must not silently drop a numberless mark,
    /// and its written text is all there is to match on. Every reference becomes a wildcard, since
    /// what it expanded to is exactly what varies from one mark to the next.
    /// <para>
    /// A template that is <em>nothing but</em> a reference therefore compiles to "any non-empty
    /// title", and on a resume it claims every unnumbered mark in the file for its own phrase.
    /// Tolerable rather than tolerated by accident: the marks in such a file were all written by
    /// this tool in the run being resumed, so what is at stake is which phrase a carried-over mark
    /// is attributed to, not whether somebody else's mark is mistaken for one of ours - and a
    /// title with no literal text of its own is a strange thing to ask for to begin with.
    /// </para>
    /// </summary>
    internal Regex Matcher => _matcher ??= BuildMatcher();

    private Regex? _matcher;

    /// <summary>Compiles <see cref="Matcher"/>. Only the literal pieces are escaped - escaping the
    /// whole template first would leave the references unrecognizable, <see cref="Regex.Escape"/>
    /// rewriting both <c>$</c> and <c>{</c>.</summary>
    private Regex BuildMatcher()
    {
        var pattern = new StringBuilder("^");
        foreach (var piece in _pieces)
            pattern.Append(piece.Group == null ? Regex.Escape(piece.Literal) : ".+");
        return new Regex(
            pattern.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>The conversions a template may name, the empty one being a plain <c>${name}</c>.
    /// Also the "expected one of" half of the error a typo produces.</summary>
    private static readonly string[] Conversions =
        ["", "digits", "roman", "upper", "lower", "capital"];

    /// <summary>A reference: an optional conversion name, then the group in braces. The name admits
    /// digits although none of the conversions has one, so that a misspelling is reported rather
    /// than falling through to "a dollar before ordinary text".</summary>
    [GeneratedRegex(@"\$([a-zA-Z][a-zA-Z0-9]*)?\{([^}]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceSyntax();
}
