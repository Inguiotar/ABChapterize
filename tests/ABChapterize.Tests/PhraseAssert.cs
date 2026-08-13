// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Language.Phrases;
using Xunit;

namespace ABChapterize.Tests;

/// <summary>
/// What <c>Assert.Matches</c> is for a <see cref="PhrasePattern"/>. A phrase is a list of wordings
/// rather than one expression, so xunit's own overloads no longer fit it - and a failure message
/// that names the phrase as written is worth more here than the position of a regex match.
/// </summary>
internal static class PhraseAssert
{
    /// <summary>Asserts that some wording of the phrase matches.</summary>
    /// <param name="pattern">The compiled phrase.</param>
    /// <param name="text">The text it should be found in.</param>
    internal static void Matches(PhrasePattern pattern, string text)
        => Assert.True(pattern.IsMatch(text), $"\"{text}\" should match the phrase {pattern.Source}");

    /// <summary>Asserts that no wording of the phrase matches.</summary>
    /// <param name="pattern">The compiled phrase.</param>
    /// <param name="text">The text it should not be found in.</param>
    internal static void DoesNotMatch(PhrasePattern pattern, string text)
        => Assert.False(pattern.IsMatch(text), $"\"{text}\" should not match the phrase {pattern.Source}");

    /// <summary>The chapter number one wording captured, or null when nothing matched or the
    /// wording captures no number - what a title's <c>${number}</c> would be built from.</summary>
    /// <param name="pattern">The compiled phrase.</param>
    /// <param name="text">The text to match against.</param>
    internal static string? Captured(PhrasePattern pattern, string text)
        => pattern.Matches(text).FirstOrDefault() is { Match: not null } hit &&
           hit.Match.Groups[PhraseAlternative.NumberGroup] is { Success: true } number
            ? number.Value
            : null;
}
