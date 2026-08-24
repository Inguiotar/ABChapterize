// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Text;
using ABChapterize.Errors;

namespace ABChapterize.Abs;

/// <summary>What kind of thing an ABS mode selector names.</summary>
public enum AbsSelectorKind
{
    /// <summary>A book title, matched loosely - the default for an unprefixed selector.</summary>
    Title,

    /// <summary>A whole library.</summary>
    Library,

    /// <summary>A series, wherever it lives.</summary>
    Series,

    /// <summary>A collection, wherever it lives.</summary>
    Collection,

    /// <summary>One library item, by its server identifier.</summary>
    Item,

    /// <summary>Every book on the server.</summary>
    All,
}

/// <summary>
/// One selector as typed on the command line in ABS mode, where the trailing arguments name books
/// on a server rather than paths on a disk.
/// </summary>
/// <param name="Kind">What the selector names.</param>
/// <param name="Value">The name or identifier, with the prefix stripped; empty for
/// <see cref="AbsSelectorKind.All"/>.</param>
/// <param name="Raw">The selector exactly as typed, for error messages.</param>
public sealed record AbsSelector(AbsSelectorKind Kind, string Value, string Raw)
{
    /// <summary>The prefixes, and what each selects. Aliases share a value.</summary>
    private static readonly Dictionary<string, AbsSelectorKind> Prefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["library"] = AbsSelectorKind.Library,
        ["lib"] = AbsSelectorKind.Library,
        ["series"] = AbsSelectorKind.Series,
        ["collection"] = AbsSelectorKind.Collection,
        ["coll"] = AbsSelectorKind.Collection,
        ["item"] = AbsSelectorKind.Item,
        ["id"] = AbsSelectorKind.Item,
        ["title"] = AbsSelectorKind.Title,
        ["book"] = AbsSelectorKind.Title,
    };

    /// <summary>The three spellings of "everything on the server".</summary>
    private static readonly string[] EverythingWords = ["all", "*", "everything"];

    /// <summary>How the usage text and the error messages spell the grammar.</summary>
    public const string Syntax =
        "library:NAME, series:NAME, collection:NAME, item:ID, title:NAME, or all";

    /// <summary>
    /// Reads one command line selector.
    /// </summary>
    /// <param name="argument">The trailing argument as typed.</param>
    /// <returns>The parsed selector.</returns>
    /// <exception cref="CliError">Thrown for a prefix with nothing after it.</exception>
    /// <remarks>
    /// A colon only introduces a prefix when what stands in front of it is one of
    /// <see cref="Prefixes"/>. That is what lets a title keep its own colon - "Silber Edition 001:
    /// Die Dritte Macht" is a book, not a request for something called "Die Dritte Macht" in a kind
    /// of container named "Silber Edition 001" - and it is why an unknown prefix is read as part of
    /// a title rather than refused.
    /// </remarks>
    public static AbsSelector Parse(string argument)
    {
        var text = argument.Trim();
        if (EverythingWords.Contains(text, StringComparer.OrdinalIgnoreCase))
            return new AbsSelector(AbsSelectorKind.All, "", argument);

        var colon = text.IndexOf(':');
        if (colon > 0 && Prefixes.TryGetValue(text[..colon], out var kind))
        {
            var value = text[(colon + 1)..].Trim();
            if (value.Length == 0)
                throw new CliError($"Selector \"{argument}\" names no {kind.ToString().ToLowerInvariant()}.");
            return new AbsSelector(kind, value, argument);
        }

        if (text.Length == 0)
            throw new CliError($"Empty selector. Expected a book title, or one of: {Syntax}.");
        return new AbsSelector(AbsSelectorKind.Title, text, argument);
    }

    /// <summary>
    /// Reduces a name to the form two spellings of it share, so that matching is about the words
    /// rather than about the punctuation between them.
    /// </summary>
    /// <param name="text">The name as typed, or as the library holds it.</param>
    /// <returns>Lower-case letters and digits, single-spaced.</returns>
    /// <remarks>
    /// Punctuation is dropped rather than kept because library titles carry a great deal of it that
    /// nobody types back - "DW01 - The Colour of Magic" and "Silber Edition 001: Die Dritte Macht"
    /// are both found by their bare title this way. Casing is full Unicode despite
    /// <c>InvariantGlobalization</c>, .NET having moved casing off ICU in 5.0, so a non-Latin title
    /// normalizes as well as a Latin one.
    /// </remarks>
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && builder.Length > 0)
                    builder.Append(' ');
                pendingSpace = false;
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                pendingSpace = true;
            }
        }
        return builder.ToString();
    }

    /// <summary>Whether a candidate name matches what was asked for: the same name, or one
    /// containing it.</summary>
    /// <param name="candidate">The name as the library holds it.</param>
    /// <param name="wanted">The name as typed.</param>
    public static bool Matches(string candidate, string wanted)
    {
        var normalized = Normalize(wanted);
        return normalized.Length > 0
               && Normalize(candidate).Contains(normalized, StringComparison.Ordinal);
    }

    /// <summary>Whether a candidate name is exactly what was asked for, punctuation aside - which
    /// outranks a mere containment when both are on offer.</summary>
    /// <param name="candidate">The name as the library holds it.</param>
    /// <param name="wanted">The name as typed.</param>
    public static bool MatchesExactly(string candidate, string wanted)
        => Normalize(candidate) == Normalize(wanted);
}
