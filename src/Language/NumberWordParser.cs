// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using System.Globalization;
using System.Text.RegularExpressions;
using ABChapterize.Language.Parsers;

namespace ABChapterize.Language;

/// <summary>
/// Extracts chapter numbers from transcribed text. Understands plain digits in any language,
/// digit ordinals ("2nd", "2e", "2ème"), Roman numerals (see <see cref="RomanNumerals"/>), and
/// spoken number words (0-999, cardinal and
/// ordinal) via the per-language parsers in <see cref="ABChapterize.Language.Parsers"/>; unknown language codes fall
/// back to the English parser. Numbers can be extracted after the chapter phrase
/// ("Chapter Seven") or before it ("Erstes Kapitel", "2. Kapitel", "Birinci Bölüm").
/// </summary>
public static class NumberWordParser
{
    /// <summary>
    /// One tokenized word, plus whether the transcript wrote a period directly after it - the
    /// single piece of punctuation this parser cannot afford to discard, because it is what
    /// separates a one-letter Roman numeral from an ordinary word (see
    /// <see cref="TryParseRoman"/>).
    /// </summary>
    /// <param name="Text">The word with its surrounding punctuation stripped.</param>
    /// <param name="TrailingPeriod">Whether a "." directly followed it before stripping.</param>
    private readonly record struct Token(string Text, bool TrailingPeriod);

    /// <summary>
    /// Matches a digit ordinal: 1-3 digits plus the ordinal suffix of any registered
    /// language's parser ("2nd", "1er", "2e", "2ème", "2de", "2ste", "5'inci", "3º",
    /// "21:a"). Assembled from each parser's own <see cref="INumberWordParser.DigitOrdinalSuffixPattern"/>
    /// rather than hardcoded here, so a language brings its own suffixes (and any
    /// separator it needs, like Turkish's optional apostrophe or Swedish's mandatory
    /// colon) simply by declaring them.
    /// </summary>
    private static readonly Regex DigitOrdinalRegex = BuildDigitOrdinalRegex();

    /// <summary>Builds <see cref="DigitOrdinalRegex"/> from every registered parser's suffix fragment.</summary>
    private static Regex BuildDigitOrdinalRegex()
    {
        var fragments = LanguageRegistry.Languages
            .Select(l => l.NumberParser.DigitOrdinalSuffixPattern)
            .Where(p => p.Length > 0)
            .Distinct();
        var pattern = $@"^(\d{{1,3}})(?:{string.Join('|', fragments)})$";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Tries to extract a number from the beginning of <paramref name="text"/>,
    /// which is the transcribed text immediately following the chapter phrase.
    /// </summary>
    /// <param name="text">Text following the matched chapter phrase.</param>
    /// <param name="language">Two-letter language code steering number-word parsing.</param>
    /// <param name="number">Receives the extracted number on success.</param>
    /// <returns>True when a number could be extracted.</returns>
    public static bool TryExtractNumber(string text, string language, out int number)
    {
        number = 0;
        var tokens = Tokenize(text);
        if (tokens.Count == 0)
            return false;

        // Digits always win, regardless of language ("Chapter 12.").
        if (TryParseDigits(tokens[0].Text, out number))
            return true;

        var parser = LanguageRegistry.For(language).NumberParser;
        if (parser.TryParse(Words(tokens), out number, out _))
            return true;

        // Roman numerals last, and only where the language's own words found nothing: the two
        // notations collide. French "dix" is the word for ten and, read as Roman, the canonical
        // spelling of 509 - the spoken word is what the narrator said, so it has to win.
        return TryParseRoman(tokens[0], out number);
    }

    /// <summary>
    /// Tries to extract a number from the end of <paramref name="text"/>, which is the
    /// transcribed text immediately preceding the chapter phrase — the "Erstes Kapitel" /
    /// "Birinci Bölüm" / "2. Kapitel" announcement order. The number must end exactly at
    /// the phrase, so a number that merely occurs earlier in the sentence does not count.
    /// </summary>
    /// <param name="text">Text preceding the matched chapter phrase.</param>
    /// <param name="language">Two-letter language code steering number-word parsing.</param>
    /// <param name="number">Receives the extracted number on success.</param>
    /// <returns>True when a number could be extracted.</returns>
    public static bool TryExtractNumberBefore(string text, string language, out int number)
    {
        number = 0;
        var tokens = TokenizeTail(text);
        if (tokens.Count == 0)
            return false;

        // Digits directly before the phrase: "2. Kapitel", "3rd chapter".
        if (TryParseDigits(tokens[^1].Text, out number))
            return true;

        // Try every suffix of the token window; accept only a parse that consumes
        // everything up to the phrase ("sagte drei. Kapitel" must not yield 3).
        var parser = LanguageRegistry.For(language).NumberParser;
        var words = Words(tokens);
        for (var start = 0; start < tokens.Count; start++)
        {
            var slice = words.GetRange(start, words.Count - start);
            if (parser.TryParse(slice, out number, out var consumed)
                && start + consumed == tokens.Count)
                return true;
        }

        // "XIII. Kapitel" - and last, for the same collision reason as in TryExtractNumber.
        if (TryParseRoman(tokens[^1], out number))
            return true;

        number = 0;
        return false;
    }

    /// <summary>
    /// Whether <paramref name="text"/> is a number and nothing else - "Seventeen.", "17.",
    /// "einundzwanzig", "XIII." - which is what <c>--chapter-phrase none</c> reads as a chapter
    /// announcement. Every token must be consumed, so "Seventeen men stood there" is not one; that
    /// is the whole difference from <see cref="TryExtractNumber"/>, which only wants the text to
    /// <em>begin</em> with a number because a chapter phrase already established what it is.
    /// </summary>
    /// <param name="text">The text to test, typically one whole transcript segment.</param>
    /// <param name="language">Two-letter language code steering number-word parsing.</param>
    /// <param name="number">Receives the parsed number on success.</param>
    /// <returns>True when the text is nothing but a number.</returns>
    public static bool TryParseWholeText(string text, string language, out int number)
    {
        number = 0;
        // Generous enough for the longest spelled-out number any supported language writes
        // ("neunhundertneunundneunzig" is one token, but "nine hundred and ninety-nine" is four),
        // and still short enough that a sentence cannot be mistaken for one.
        var tokens = Tokenize(text, limit: 8);
        if (tokens.Count == 0)
            return false;
        if (tokens.Count == 1 && TryParseDigits(tokens[0].Text, out number))
            return true;

        var parser = LanguageRegistry.For(language).NumberParser;
        if (parser.TryParse(Words(tokens), out number, out var consumed) && consumed == tokens.Count)
            return true;

        // Roman last, for the collision reason TryExtractNumber explains - and only for a lone
        // token, an isolated announcement being one numeral rather than a sequence of them.
        number = 0;
        return tokens.Count == 1 && TryParseRoman(tokens[0], out number);
    }

    /// <summary>Which readings of a transcript segment count as a bare-number announcement under
    /// <c>--chapter-phrase none</c>; see <see cref="FindBareNumberAnnouncement"/>.</summary>
    public enum BareNumberReading
    {
        /// <summary>
        /// The segment's opening sentence must <em>be</em> the number. What Pass 2's forward scan
        /// uses: it walks the whole book with no upper bound on the sequence and nothing vets its
        /// finds afterwards, so a false accept there displaces real chapters.
        /// <para>
        /// The strictness is measured rather than cautious. Over the 15345 transcript segments one
        /// 18-hour Italian run logged (2026-08-05), this reading finds all 65 chapter numbers plus
        /// five implausible extras (150, 600, 1947, 2059, 2068), every one of them out of sequence.
        /// Merely requiring the segment to <em>start</em> with a number instead doubles the hits to
        /// 1797 and floods the plausible range: Italian "un"/"una"/"uno" all parse as 1, and prose
        /// such as "12 il capitano Fan Castro spostò lo sguardo" or "25 Perfino con un reattore
        /// spento" is then indistinguishable from an announcement at the right sequence position.
        /// </para>
        /// </summary>
        SpokenAloneAtSegmentStart,

        /// <summary>
        /// A sentence that is nothing but the number, but anywhere in the text rather than only at
        /// its start. What the mark refinement asks of its own probes under a
        /// <see cref="SpokenAloneAtSegmentStart"/> match: a refinement probe decodes a few seconds
        /// with a lead-in, so the transcript routinely opens with a fragment of whatever preceded
        /// the announcement and the number lands in the second sentence. Just as strict as
        /// <see cref="SpokenAloneAtSegmentStart"/> about what a number has to look like, which is
        /// what a Pass 2 mark needs: nothing vets where that refinement puts it.
        /// </summary>
        SpokenAloneAnywhere,

        /// <summary>
        /// Any sentence of the segment, and it need only <em>begin</em> with the number. For the
        /// passes that hunt specific missing numbers inside a bounded stretch, where the hole says
        /// which numbers may appear and
        /// <see cref="ABChapterize.Detection.AnnouncementIsolation"/> vets the position afterwards -
        /// so nothing about Whisper's output has to be trusted, neither its segmentation nor its
        /// punctuation.
        /// <para>
        /// The punctuation half is why this cannot just be the reading above applied to every
        /// sentence. Whisper's period after a heading number is not dependable, and specifically not
        /// dependable <em>across machines</em>: the same window at 12:25:23 of the same book came
        /// back as "45. Zhang Mingoua lanciò…" on one GPU and "45 Zangmingoa lanciò…" on another
        /// (2026-08-05; see the cross-machine reproducibility note in the project docs). A rule
        /// hinging on that period quietly finds different chapters on different hardware.
        /// </para>
        /// </summary>
        LeadingASentence,
    }

    /// <summary>A bare-number announcement read out of one transcript segment.</summary>
    /// <param name="Number">The number that was spoken.</param>
    /// <param name="SpokenAlone">Whether <see cref="BareNumberReading.SpokenAloneAtSegmentStart"/> would
    /// have found this one too. The distinction survives into
    /// <see cref="ABChapterize.Detection.PhraseMatching.PhraseMatch"/> because it is what a mark
    /// falls back on when the isolation guard cannot run: a number the strict reading also accepts
    /// stands on its own evidence, while one only the wide reading found stands on the guard alone
    /// and must not be kept without it.</param>
    public readonly record struct BareNumberAnnouncement(int Number, bool SpokenAlone);

    /// <summary>
    /// Reads a <c>--chapter-phrase none</c> announcement out of one transcript segment.
    /// <para>
    /// The unit is a <em>sentence</em>, not the segment, and that is the entire point. Until
    /// 2026-08-05 this asked whether the segment <em>was</em> a number start to finish, on the
    /// reasoning that Whisper ends a segment where the speaking stops and so brackets a number
    /// spoken alone. It does not, reliably: on "Corsa nello spazio" (18 h, 65 chapters, build 244)
    /// it glued the announcement onto the first sentence of the chapter - "45. Zhang Mingoua
    /// lanciò un'occhiata…" - for ten of them, and every one was then discarded despite having been
    /// transcribed correctly, chapter 53 six separate times across two models.
    /// </para>
    /// <para>
    /// The split deliberately requires whitespace after the sentence mark, so "2.179" stays one
    /// token rather than yielding a spurious "2" - a real case from the same book, whose epilogue
    /// is followed by the year 2179.
    /// </para>
    /// <para>
    /// <see cref="BareNumberReading"/> then decides how much of Whisper's own formatting is being
    /// leaned on. Both levels are calibrated against that book's logged transcripts rather than
    /// chosen; see the two values for the counts.
    /// </para>
    /// <para>
    /// <paramref name="admits"/> is the other half, and it is what the mark refinement leans on
    /// rather than the reading. Any number can open a sentence, so a refinement probe reading
    /// "2066." or "Tre di loro" answers "found" as readily as one reading the chapter's own number,
    /// and the onset walk then climbs off the announcement onto the chapter's first sentence.
    /// Every misplaced mark in that book's build-249 re-run was exactly this, and the shapes are
    /// worth knowing because none of them is unusual: chapters 1 and 20 open with a date line
    /// ("1. 9 febbraio 2066.", "20. 27 luglio 2067.") and the walk settled on the year, four
    /// seconds late; chapter 4's first words are "mille chilometri", which Whisper writes as
    /// "1000 km"; chapter 11's are "Tre di loro", and Italian "tre" parses as 3. Filtering by what
    /// the chapter sequence can hold at that point removes all four while keeping every reading of
    /// the announcement itself, notation changes included.
    /// </para>
    /// </summary>
    /// <param name="text">One transcript segment's text.</param>
    /// <param name="language">Two-letter language code steering number-word parsing.</param>
    /// <param name="reading">How much of the segment counts; see <see cref="BareNumberReading"/>.</param>
    /// <param name="admits">Which numbers may be taken for an announcement here, or null for any.
    /// The mark refinement passes the chapter sequence's own view of what can sit at this point
    /// (see <see cref="ABChapterize.Detection.NumberCheck"/>); detection passes nothing, because a
    /// number the sequence rejects is precisely what
    /// <see cref="ABChapterize.Detection.SuspectNumberMender"/> exists to re-read rather than
    /// discard. A rejected sentence does not end the scan - the announcement may be in a later
    /// one.</param>
    /// <returns>The announcement, or null when no sentence in scope carries one.</returns>
    public static BareNumberAnnouncement? FindBareNumberAnnouncement(
        string text, string language, BareNumberReading reading, Func<int, bool>? admits = null)
    {
        var sentences = SentenceBreak.Split(text);
        for (var i = 0; i < sentences.Length; i++)
        {
            // The three readings are nested, so one walk serves all of them - and SpokenAlone can
            // report whether the strictest would also have taken this, without a second pass.
            if (TryParseWholeText(sentences[i], language, out var whole) && Admitted(whole, admits))
                return new BareNumberAnnouncement(whole, SpokenAlone: i == 0);
            if (reading == BareNumberReading.SpokenAloneAtSegmentStart)
                return null;
            if (reading == BareNumberReading.LeadingASentence &&
                TryExtractNumber(sentences[i], language, out var leading) && Admitted(leading, admits))
                return new BareNumberAnnouncement(leading, SpokenAlone: false);
        }
        return null;
    }

    /// <summary>Whether a number just parsed out of a sentence passes the caller's filter; no
    /// filter admits everything.</summary>
    /// <param name="number">The parsed number.</param>
    /// <param name="admits">The filter, or null for "any number".</param>
    private static bool Admitted(int number, Func<int, bool>? admits) => admits?.Invoke(number) ?? true;

    /// <summary>Splits a segment at its sentence boundaries: a sentence-ending punctuation mark
    /// followed by whitespace. The whitespace is what keeps a decimal or dotted number
    /// ("2.179") in one piece - see <see cref="FindBareNumberAnnouncement"/>.</summary>
    private static readonly Regex SentenceBreak =
        new(@"(?<=[.!?…])\s+", RegexOptions.CultureInvariant);

    /// <summary>
    /// Finds an unambiguous Roman numeral anywhere in <paramref name="text"/>, rather than only at
    /// the end nearest a chapter phrase. Written for a pre-existing marking's <em>title</em>
    /// (<see cref="ABChapterize.Detection.MarkingTitleNumber"/>), where the number can sit behind a
    /// heading the phrase-anchored rules do not reach - and only ever reached there once those have
    /// found nothing, since scanning a whole string for something that is also a word is a last
    /// resort by nature. The one-letter guard applies exactly as everywhere else, so a stray "I" or
    /// "C" in a title is not a chapter number unless it is written like a heading.
    /// </summary>
    /// <param name="text">The text to scan.</param>
    /// <param name="number">Receives the parsed number on success.</param>
    /// <returns>True when some token is an unambiguous Roman numeral.</returns>
    public static bool TryExtractRomanAnywhere(string text, out int number)
    {
        foreach (var token in Tokenize(text, int.MaxValue))
            if (TryParseRoman(token, out number))
                return true;
        number = 0;
        return false;
    }

    /// <summary>
    /// Parses a token as a Roman numeral ("XIII"), subject to the one-letter guard below. Like
    /// digits, and unlike spoken words, the notation is the same in every language - but unlike
    /// digits it overlaps with real words, so every caller tries it <em>after</em> the language's
    /// own number-word parser rather than before.
    /// </summary>
    /// <param name="token">The token to parse, with its trailing-period flag.</param>
    /// <param name="number">Receives the parsed number on success.</param>
    private static bool TryParseRoman(Token token, out int number)
    {
        number = 0;
        if (!RomanNumerals.TryParse(token.Text, out var parsed) || !RomanNumeralIsUnambiguous(token))
            return false;
        number = parsed;
        return true;
    }

    /// <summary>
    /// Whether a token that <em>parses</em> as a Roman numeral may be read as one here. Only
    /// one-letter numerals are in doubt, and only they are gated: I, V, X, L, C, D and M are all
    /// ordinary words or initials somewhere ("in that chapter I wrote…", Polish "rozdział i
    /// epilog", where "i" means "and"), and reading one as a number would plant a chapter mark in
    /// the middle of prose. A trailing period resolves it, because that is how a transcript writes
    /// a heading number and not how it writes a pronoun.
    /// <para>
    /// Two letters or more need no such test: <see cref="RomanNumerals.TryParse"/> accepts only
    /// canonical spellings, and almost nothing that is also a word survives that
    /// ("DIM", "LID", "CIVIC", "MILD" are all non-canonical; "MIX" is 1009 and out of range).
    /// </para>
    /// <para>
    /// The rule is what real transcripts do, not a guess: on "I Shall Wear Midnight" (2026-07-30)
    /// Whisper wrote the one-letter cases as "CHAPTER V. THE MOTHER OF TONGUES" and "CHAPTER X.
    /// THE MELTING GIRL" - both with the period - while the multi-letter "CHAPTER VII SONGS IN THE
    /// NIGHT" came without one. The cost of being wrong is asymmetric and points the same way: a
    /// missed one-letter numeral is one chapter pass 3 still gets a shot at, whereas a false
    /// chapter 1 read out of an English pronoun displaces a real mark.
    /// </para>
    /// </summary>
    /// <param name="token">The token that parsed as a Roman numeral.</param>
    private static bool RomanNumeralIsUnambiguous(Token token)
        => token.Text.Length > 1 || token.TrailingPeriod;

    /// <summary>Parses a token that is a plain digit number or a digit ordinal ("2nd", "2e").</summary>
    /// <param name="token">The token text, punctuation already stripped.</param>
    /// <param name="number">Receives the parsed number on success.</param>
    private static bool TryParseDigits(string token, out int number)
    {
        if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out number))
            return true;
        var m = DigitOrdinalRegex.Match(token);
        if (m.Success)
        {
            number = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            return true;
        }
        number = 0;
        return false;
    }

    /// <summary>The bare words of a token list, which is all an <see cref="INumberWordParser"/>
    /// ever needs - the trailing-period flag exists solely for <see cref="TryParseRoman"/>.</summary>
    /// <param name="tokens">The tokens to strip down.</param>
    private static List<string> Words(List<Token> tokens)
        => tokens.ConvertAll(t => t.Text);

    /// <summary>
    /// Splits text into words, stripping surrounding punctuation. Only the first few tokens are
    /// relevant to a number following a phrase, so tokenization stops after five words by default.
    /// </summary>
    /// <param name="text">The text to split.</param>
    /// <param name="limit">How many tokens to keep; <see cref="TryExtractRomanAnywhere"/> lifts it,
    /// having a whole title to scan rather than the words right after a phrase.</param>
    private static List<Token> Tokenize(string text, int limit = 5)
    {
        var tokens = new List<Token>();
        foreach (var raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (MakeToken(raw) is { } token)
                tokens.Add(token);
            if (tokens.Count >= limit)
                break;
        }
        return tokens;
    }

    /// <summary>Splits text into words like <see cref="Tokenize"/>, keeping the LAST five.</summary>
    /// <param name="text">The text preceding the chapter phrase.</param>
    private static List<Token> TokenizeTail(string text)
    {
        var tokens = new List<Token>();
        foreach (var raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (MakeToken(raw) is { } token)
                tokens.Add(token);
            if (tokens.Count > 5)
                tokens.RemoveAt(0);
        }
        return tokens;
    }

    /// <summary>Turns one raw whitespace-delimited word into a <see cref="Token"/>, or null when
    /// nothing but punctuation is left of it.</summary>
    /// <param name="raw">The word as the transcript wrote it, punctuation included.</param>
    private static Token? MakeToken(string raw)
    {
        var text = TrimPunctuation(raw);
        if (text.Length == 0)
            return null;
        // Read off the raw word, and tolerant of what closes around it: a heading number lands as
        // "XIII." mid-line but as "V.\"" or "X.)" inside a quote or bracket, and all of those are
        // the same period as far as the numeral is concerned.
        return new Token(text, raw.TrimEnd(ClosingPunctuation).EndsWith('.'));
    }

    /// <summary>Strips the punctuation Whisper attaches to words.</summary>
    /// <param name="raw">The word as the transcript wrote it.</param>
    private static string TrimPunctuation(string raw) =>
        raw.Trim('.', ',', ':', ';', '!', '?', '"', '\'', '(', ')', '…', '„', '“', '”');

    /// <summary>The punctuation that may close around a word <em>after</em> its own period, and so
    /// has to come off before <see cref="MakeToken"/> can see that period. Deliberately excludes
    /// "." itself, which is the thing being looked for.</summary>
    private static readonly char[] ClosingPunctuation =
        [',', ':', ';', '!', '?', '"', '\'', ')', '…', '“', '”'];
}
