// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using Xunit;
using ABChapterize.Cli;
using ABChapterize.Detection;
using ABChapterize.Language;
using ABChapterize.Transcription;

namespace ABChapterize.Tests;

/// <summary>
/// Tests for the whitespace normalization <see cref="PhraseMatching.FindPhraseMatches"/> applies
/// before any phrase regex runs.
/// </summary>
/// <remarks>
/// Every segment Whisper returns begins with a space, and the flattening used to add a separator of
/// its own on top - so each segment join carried two spaces where a phrase writes one. The built-in
/// phrases are all single words and never noticed; a user's multi-word --chapter-phrase could not
/// match across a segment boundary at all. The strings below are verbatim from the run that found
/// it: "Paula Monti.m4b" with <c>--chapter-phrase "[fr]/(?:premi|1).re partie.? chapitre/"</c>,
/// whose Scan heard chapter 19 correctly, split it over two segments, and dropped it (2026-08-08).
/// The leading spaces in the fixtures are the point of the test - do not tidy them away.
/// </remarks>
public sealed class PhraseWhitespaceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public PhraseWhitespaceTests()
    {
        _dir = Directory.CreateTempSubdirectory("abc-phrase-ws-").FullName;
        _file = Path.Combine(_dir, "book.mp3");
        File.WriteAllBytes(_file, new byte[16]);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>The French profile carrying the multi-word phrase from the case on record.</summary>
    private LanguageProfile FrenchProfile()
        => CliOptions.Parse(
            ["--chapter-phrase", "[fr]/(?:premi|1).re partie.? chapitre/", _file])!.ResolveProfile("fr");

    private static List<TranscriptSegment> Segments(params (double Start, string Text)[] parts)
        => [.. parts.Select(p => new TranscriptSegment(p.Start, p.Start + 2, p.Text, 0.9))];

    private List<PhraseMatching.PhraseMatch> Matches(List<TranscriptSegment> segments)
        => [.. PhraseMatching.FindPhraseMatches(segments, FrenchProfile())];

    [Fact]
    public void APhraseSpanningTwoSegments_IsFound()
    {
        // The exact split that lost chapter 19: "Première partie." then "Chapitre 19.", each with
        // the recognizer's own leading space.
        var matches = Matches(Segments(
            (271.8, " Quelle ridicule et insupportable vanité."),
            (273.6, " Première partie."),
            (275.2, " Chapitre 19."),
            (276.9, " La poste restante.")));

        var match = Assert.Single(matches);
        Assert.Equal(19, match.Number);
        // Timed from the segment the phrase starts in, not the one holding the number.
        Assert.Equal(273.6, match.PhraseStartSeconds);
    }

    [Fact]
    public void APhraseInsideOneSegment_IsStillFound()
    {
        // The ordinary shape, which must not regress: normalization trims the leading space, and an
        // unanchored phrase never cared about it either way.
        var match = Assert.Single(Matches(Segments(
            (0, " Première partie, chapitre 18, la sortie."))));
        Assert.Equal(18, match.Number);
    }

    [Fact]
    public void RunsOfWhitespaceInsideASegment_AreCollapsed()
    {
        // Not a shape Whisper is known to produce, but the normalization is stated as "runs of
        // whitespace become one space" and a line break inside a segment must behave like the double
        // space did - i.e. not defeat the phrase.
        var match = Assert.Single(Matches(Segments(
            (0, " Première  partie,\n\tchapitre 21, l'entretien."))));
        Assert.Equal(21, match.Number);
    }

    [Fact]
    public void ABlankSegmentBetweenTwoHalves_DoesNotBreakThePhrase()
    {
        // A segment normalizing to nothing contributes no separator of its own, so the two halves
        // still meet across exactly one space.
        var match = Assert.Single(Matches(Segments(
            (10, " Première partie."),
            (11, "   "),
            (12, " Chapitre 7."))));
        Assert.Equal(7, match.Number);
    }

    [Fact]
    public void NormalizeWhitespace_TrimsAndCollapses()
    {
        Assert.Equal("Chapitre 19.", PhraseMatching.NormalizeWhitespace(" Chapitre 19."));
        Assert.Equal("a b", PhraseMatching.NormalizeWhitespace("  a \t\r\n b  "));
        Assert.Equal("", PhraseMatching.NormalizeWhitespace("   "));
        Assert.Equal("", PhraseMatching.NormalizeWhitespace(null));
    }
}
