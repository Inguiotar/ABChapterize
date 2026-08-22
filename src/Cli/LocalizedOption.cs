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
    /// it - or null and the entry unchanged when there is none. The phrase and title options'
    /// share of <see cref="SpecTag.Take"/>, which is the whole rule; what is added here is that a
    /// <c>--custom</c> hint has no meaning outside <c>--custom</c> and is refused rather than
    /// silently ignored.
    /// </summary>
    /// <param name="entry">One entry of a spec, already trimmed.</param>
    /// <param name="rest">The entry with any tag removed, trimmed.</param>
    /// <param name="option">Long option name, for error messages.</param>
    /// <returns>The lower-cased language code, or null when the entry carries no tag.</returns>
    /// <exception cref="CliError">Thrown when the tag carries a <c>--custom</c> hint, or is itself
    /// malformed.</exception>
    internal static string? TakeLanguageTag(string entry, out string rest, string option = "spec")
    {
        if (SpecTag.Take(entry, out rest, option) is not { } tag)
            return null;
        if (tag.HasHints)
            throw new CliError(
                $"{option}: the keywords in \"{entry}\" only mean something on a --custom mapping. " +
                "A tag here names a language and nothing else.");
        return tag.Language;
    }
}

/// <summary>
/// One <em>title</em> option's value, which may be written once for every language or once per
/// language: <c>--chapter-title "[fr]Chapitre;[en]Section"</c>. What it exists for is a batch run
/// over a mixed-language library, where <c>--lang auto</c> resolves a different language per file
/// and a single word can only ever be right for some of them.
/// <para>
/// A title holds one value per language, which is what separates this from
/// <see cref="ABChapterize.Language.Phrases.PhraseSpec"/>: a phrase is a list of wordings and an
/// untagged one adds to every language's list, whereas a mark can only be called one thing. So here
/// an untagged entry is the <em>fallback</em> for the languages the value does not name, only one
/// may be given, and a value with no tag anywhere is taken whole, semicolons and all - a title
/// containing a semicolon is a title, not two titles.
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
        if (!entries.Any(e => SpecSyntax.TakeLanguageTag(e, out _, option) != null))
        {
            _fallback = raw;
            return;
        }

        foreach (var entry in entries.Where(e => e.Length > 0))
        {
            if (SpecSyntax.TakeLanguageTag(entry, out var value, option) is not { } code)
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
    /// <param name="language">Language code (never "auto" - resolve that first).</param>
    internal string? For(string language) => _byLanguage.GetValueOrDefault(language) ?? _fallback;
}
