// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Text;
using ABChapterize.Errors;

namespace ABChapterize.Cli;

/// <summary>
/// The punctuation the phrase, title and <c>--custom</c> options share: entries separated by
/// semicolons, each optionally opened by a <c>[xx]</c> language tag. Kept in one place because both
/// halves of it are things a regexp may legitimately contain, so the rules for reading them have to
/// be identical wherever they are applied.
/// </summary>
internal static class SpecSyntax
{
    /// <summary>Splits a spec at every semicolon that is not written as <c>\;</c>. Any other
    /// backslash is passed through untouched, so a regexp's own <c>\d</c> or <c>\s</c> survives this
    /// step intact.</summary>
    /// <param name="spec">The raw option value.</param>
    internal static IEnumerable<string> SplitOnUnescapedSemicolons(string spec)
    {
        var entry = new StringBuilder();
        for (var i = 0; i < spec.Length; i++)
        {
            if (spec[i] == '\\' && i + 1 < spec.Length && spec[i + 1] == ';')
            {
                entry.Append(';');
                i++;
            }
            else if (spec[i] == ';')
            {
                yield return entry.ToString();
                entry.Clear();
            }
            else
            {
                entry.Append(spec[i]);
            }
        }
        yield return entry.ToString();
    }

    /// <summary>
    /// Strips a leading <c>[xx]</c> language tag off an entry, returning the code and what follows
    /// it - or null and the entry unchanged when there is none. Exactly two ASCII letters are
    /// required, which is what keeps a phrase legitimately starting with a bracket ("[Kk]apitel")
    /// from being read as a tag: the code is checked for shape only, not against the language
    /// registry, matching <c>--lang</c>, which also accepts a code this tool has no defaults for.
    /// </summary>
    /// <param name="entry">One entry of a spec, already trimmed.</param>
    /// <param name="rest">The entry with any tag removed, trimmed.</param>
    /// <returns>The lower-cased language code, or null when the entry carries no tag.</returns>
    internal static string? TakeLanguageTag(string entry, out string rest)
    {
        rest = entry;
        if (entry.Length < 4 || entry[0] != '[' || entry[3] != ']' ||
            !char.IsAsciiLetter(entry[1]) || !char.IsAsciiLetter(entry[2]))
            return null;
        rest = entry[4..].TrimStart();
        return entry[1..3].ToLowerInvariant();
    }
}

/// <summary>
/// One phrase or title option's value, which may be written once for every language or once per
/// language: <c>--chapter-title "[fr]Chapitre;[en]Section"</c>. What it exists for is a batch run
/// over a mixed-language library, where <c>--lang auto</c> resolves a different language per file
/// and a single literal phrase can only ever be right for some of them.
/// <para>
/// A value with no <c>[xx]</c> tag anywhere in it is taken whole, semicolons and all, rather than
/// split - so every phrase that worked before this existed still means exactly what it did, however
/// much punctuation it carries. Tagging any entry switches the whole value into per-language mode,
/// after which one untagged entry may still be given as the fallback for the languages not named.
/// </para>
/// <para>
/// A language the value says nothing about falls back to this tool's own localized default for that
/// language, exactly as if the option had not been given at all. That is what makes the feature
/// additive: naming French explicitly does not silently impose a French phrase on the German files
/// in the same run.
/// </para>
/// </summary>
internal sealed class LocalizedOption
{
    private readonly Dictionary<string, string> _byLanguage = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _fallback;

    /// <summary>The option value exactly as it was written, for the run fingerprint and the debug
    /// log - which have to distinguish two specs that happen to agree on one language.</summary>
    internal string Raw { get; }

    /// <summary>Every value this option can resolve to, for the per-option emptiness checks.</summary>
    internal IEnumerable<string> Values =>
        _fallback is null ? _byLanguage.Values : _byLanguage.Values.Append(_fallback);

    /// <summary>Parses one option value.</summary>
    /// <param name="raw">The value as written on the command line.</param>
    /// <param name="option">Long option name, for error messages.</param>
    /// <exception cref="CliError">Thrown for a repeated language or a second untagged entry.</exception>
    internal LocalizedOption(string raw, string option)
    {
        Raw = raw;
        var entries = SpecSyntax.SplitOnUnescapedSemicolons(raw).Select(e => e.Trim()).ToList();
        if (!entries.Any(e => SpecSyntax.TakeLanguageTag(e, out _) != null))
        {
            _fallback = raw;
            return;
        }

        foreach (var entry in entries.Where(e => e.Length > 0))
        {
            if (SpecSyntax.TakeLanguageTag(entry, out var value) is not { } code)
            {
                if (_fallback != null)
                    throw new CliError(
                        $"{option}: only one entry may be left without a [xx] language tag - " +
                        "it is the fallback for the languages not named.");
                _fallback = entry;
                continue;
            }
            if (!_byLanguage.TryAdd(code, value))
                throw new CliError($"{option}: the language [{code}] is given more than once.");
        }
    }

    /// <summary>
    /// The value to use for one language, or null when this option says nothing about it - in which
    /// case the caller falls back to the language's built-in default.
    /// </summary>
    /// <param name="language">Two-letter language code (never "auto" - resolve that first).</param>
    internal string? For(string language) => _byLanguage.GetValueOrDefault(language) ?? _fallback;
}
