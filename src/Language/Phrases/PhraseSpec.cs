// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Cli;
using ABChapterize.Errors;

namespace ABChapterize.Language.Phrases;

/// <summary>
/// What a phrase option says, before any of it is compiled: a list of alternatives, each optionally
/// restricted to one language by a leading <c>[xx]</c> tag.
/// <para>
/// A language takes every untagged alternative <em>and</em> every one tagged for it, in the order
/// they were written - so <c>"[fr]/a/;/b/;[en]/c/"</c> gives French "a" then "b", English "b" then
/// "c", and everything else "b". A language the value says nothing about at all keeps this tool's
/// own default for it, exactly as if the option had not been given, which is what lets a batch over
/// a mixed library name one language's oddity without imposing it on the rest.
/// </para>
/// <para>
/// The alternatives are not a preference order in the sense of "stop at the first that works":
/// every one of them is searched, and a window may well produce a match from each. The order only
/// decides which alternative claims a stretch of text that two of them match.
/// </para>
/// </summary>
/// <param name="Entries">The alternatives, tags already taken off.</param>
/// <param name="Raw">The option value exactly as written, for the run fingerprint and the debug
/// log - two specs that happen to agree on one language are still different runs.</param>
internal sealed record PhraseSpec(IReadOnlyList<PhraseEntry> Entries, string Raw)
{
    /// <summary>The spelling that pulls this tool's own default for the language back into the
    /// list, so a value can add to the built-in wordings instead of replacing them.</summary>
    internal const string DefaultWord = "default";

    /// <summary>
    /// Splits one option value into its alternatives. Semicolons separate them and <c>\;</c> is a
    /// semicolon inside one, exactly as the <c>--custom</c> syntax has always had it.
    /// </summary>
    /// <param name="raw">The value as written on the command line.</param>
    /// <param name="option">Long option name, for error messages.</param>
    /// <exception cref="CliError">Thrown for a malformed tag.</exception>
    internal static PhraseSpec Parse(string raw, string option)
    {
        var entries = new List<PhraseEntry>();
        foreach (var entry in SpecSyntax.SplitOnUnescapedSemicolons(raw).Select(e => e.Trim()))
        {
            if (entry.Length == 0)
                continue;
            var language = SpecSyntax.TakeLanguageTag(entry, out var body, option);
            entries.Add(new PhraseEntry(language, body.Trim()));
        }
        return new PhraseSpec(entries, raw);
    }

    /// <summary>
    /// The alternatives that apply to one language, with <see cref="DefaultWord"/> expanded into
    /// whatever <paramref name="fallback"/> resolves to.
    /// </summary>
    /// <param name="language">Language code (never "auto" - resolve that first).</param>
    /// <param name="fallback">This tool's own alternatives for the language, consulted both for a
    /// <c>default</c> entry and for a value that says nothing about this language at all.</param>
    internal IReadOnlyList<string> For(string language, Func<IReadOnlyList<string>> fallback)
    {
        // A value with nothing in it at all is the opt-out spelling, not an omission: an empty
        // --prologue-phrase switches prologue detection off, and falling back to the built-in
        // default there would make the option impossible to say no with.
        if (Entries.Count == 0)
            return [];

        var mine = Entries
            .Where(e => e.Language == null ||
                        string.Equals(e.Language, language, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (mine.Count == 0)
            return fallback();

        var resolved = new List<string>();
        foreach (var entry in mine)
        {
            if (entry.Body.Equals(DefaultWord, StringComparison.OrdinalIgnoreCase))
                resolved.AddRange(fallback());
            else
                resolved.Add(entry.Body);
        }
        return resolved;
    }

    /// <summary>Every alternative's body, whatever language it was tagged for - what the per-option
    /// emptiness and combination checks ask about, since those are properties of the value rather
    /// than of one file's language.</summary>
    internal IEnumerable<string> Bodies => Entries.Select(e => e.Body);

    /// <summary>Joins repeated occurrences of one option, which the manual defines as equivalent to
    /// writing them as one semicolon-separated value.</summary>
    /// <param name="first">What the option has collected so far, or null on its first occurrence.</param>
    /// <param name="next">The new value.</param>
    internal static string Join(string? first, string next)
        => string.IsNullOrEmpty(first) ? next : $"{first};{next}";
}

/// <summary>One alternative of a <see cref="PhraseSpec"/>.</summary>
/// <param name="Language">The code it is restricted to, or null when it applies to every
/// language.</param>
/// <param name="Body">The alternative itself: a word, a <c>/regexp/</c>, <c>none</c> or
/// <see cref="PhraseSpec.DefaultWord"/>.</param>
internal readonly record struct PhraseEntry(string? Language, string Body);
