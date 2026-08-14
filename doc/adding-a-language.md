# Adding a language to ABChapterize

So your audiobooks are in a language ABChapterize does not speak yet, and you
would like to fix that. Good news: it is a self-contained job. You will touch
two or three files, all of them in `src\Language\`, and you need to know
nothing whatsoever about how the rest of the tool works — no audio, no
Whisper, no ffmpeg, no threading. If you have written code in *any*
curly-brace language, you can do this.

This guide assumes you know what a regular expression is, roughly. Everything
else is spelled out.

**Contents**

1. [What a "language" consists of](#1-what-a-language-consists-of)
2. [Step 1 — the language class](#2-step-1--the-language-class)
3. [Step 2 — register it](#3-step-2--register-it)
4. [Step 3 — the number parser](#4-step-3--the-number-parser)
5. [Step 4 — tests](#5-step-4--tests)
6. [Step 5 — documentation](#6-step-5--documentation)
7. [Building and running](#7-building-and-running)
8. [Checklist](#8-checklist)

---

## 1. What a "language" consists of

ABChapterize finds chapters by listening for the narrator saying something
like *"Chapter seventeen"*. To do that in your language it needs two things
from you:

1. **Words.** What does a chapter announcement sound like? What should the
   marks be called? Half a dozen short strings. This is 90 % of the value and
   maybe fifteen minutes of work.
2. **Numbers.** How is "seventeen" spelled out in your language? This is the
   larger job, and it is optional in the sense that the tool still works
   without it — announcements with *digits* ("Kapitel 17") and *Roman
   numerals* ("Kapitel XVII") are understood in
   every language, always. Only spelled-out numbers need a parser.

You can ship step 1 alone and add step 2 later. A pull request with just the
words is genuinely useful.

Everything lives here:

```
src\Language\
    ILanguage.cs              <- the interface you implement (read this first)
    LanguageRegistry.cs       <- the one list of all languages (add one line)
    Languages\
        EnglishLanguage.cs    <- copy one of these
        GermanLanguage.cs
        ...
    Parsers\
        INumberWordParser.cs  <- the number interface
        EnglishNumberParser.cs
        ...
```

Nothing outside `src\Language\` knows how many languages exist or what they
are called. That is deliberate, and it is why this is a small job.

---

## 2. Step 1 — the language class

Say you are adding Czech (`cs`). Copy an existing file that is structurally
close to your language — `GermanLanguage.cs` is the simplest one — to
`src\Language\Languages\CzechLanguage.cs`, and edit it:

```csharp
// ABChapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

using ABChapterize.Language.Parsers;

namespace ABChapterize.Language.Languages;

/// <summary>Czech.</summary>
public sealed class CzechLanguage : ILanguage
{
    /// <inheritdoc/>
    public string Code => "cs";

    /// <inheritdoc/>
    public string ChapterPhrase => "/(?:^kapitol ()|^kapitol)/";

    /// <inheritdoc/>
    public string ChapterTitle => "Kapitola";

    /// <inheritdoc/>
    public string PartTitle => "Část";

    /// <inheritdoc/>
    public string IntroTitle => "Úvod";

    /// <inheritdoc/>
    public string ProloguePhrase => "/prolog/";

    /// <inheritdoc/>
    public string PrologueTitle => "Prolog";

    /// <inheritdoc/>
    public string EpiloguePhrase => "/epilog/";

    /// <inheritdoc/>
    public string EpilogueTitle => "Epilog";

    /// <inheritdoc/>
    public INumberWordParser NumberParser { get; } = new CzechNumberParser();
}
```

That is the whole class. A few notes on filling it in.

### The three phrases

`ChapterPhrase`, `ProloguePhrase` and `EpiloguePhrase` are exactly what a user
could type after `--chapter-phrase` on the command line: either a plain word,
or a regular expression wrapped in slashes. Use the `/regexp/` form — every
built-in language does, for consistency.

The chapter phrase has one more thing in it. Every built-in language spells it
`/(?:^WORD ()|^() WORD|^WORD)/`: three alternatives of one phrase, where `()`
stands for a chapter number in your language's own notation and captures it.
The first alternative takes the number that follows the word directly
("Kapitola 12"), the second the number that precedes it ("První kapitola"), and
the third is the bare word, where the number is read off whatever stands around
it. Copy the shape and change the word; the parts that vary by language are the
word itself and the number grammar in step 3.

Keep all three, in that order. The order matters where two of them read the
same words differently: on "Der erste Kapitel 5" the second alternative claims
"erste Kapitel" — it starts earlier, and the leftmost match wins — and would
make that chapter 1. Three transcripts in the reference corpus read exactly
like that, none because the narrator said it, all because a short probe window
opened with a word Whisper invented. What saves them is that a reading the
chapter sequence cannot accept is put aside and the next one tried, so
"Kapitel 5" gets its turn; the ordering is what decides which reading is
preferred when both would fit.

The `^` on all three says the announcement must be set off from what precedes
it — by a pause, or by the recognizer writing it as a transcript segment of its
own. Keep it. It is what stops your chapter word becoming a mark every time it
turns up in ordinary narration, and on the second alternative it is also what
lets an ordinal-first announcement be recognized by its own segment start.

If your word needs a `(?:...)` for a stem change, as Swedish and Danish do with
`kapit(?:el|let)`, write it and think no further: the alternation is multiplied
out for you, so those three alternatives become six, one per spelling. That is
deliberate — each alternative has to be a single expression with no choice left
in it, because the one that finds an announcement is also the one its position is
confirmed against later.

Two rules about how they are matched, both of which save you work:

- **Case is ignored.** Never write `[Kk]`.
- **It is a substring match, not a whole-word match.** The pattern only has to
  occur *somewhere* in the transcribed line. So a pattern that stops at the
  stem automatically covers every ending your language glues on: Polish gets
  "rozdział", "rozdziale" and "rozdziału" out of the single pattern
  `rozdzia[łl]`, and English `prolog` finds "prologue" for free.

What *does* need a pattern:

| Situation | Example | Pattern |
| --- | --- | --- |
| Whisper drops an accent | "capitulo" for "capítulo" | `cap[íi]tulo` |
| Whisper drops a diacritic | "bolum" for "bölüm" | `b[öo]l[üu]m` |
| The stem itself changes | Swedish "kapitel" / "kapitlet" | `kapit(?:el\|let)` |
| Two accepted spellings | "prolog" / "prologue" | just `prolog` — substring |

Write any alternation of your own as `(?:a|b)`, **not** `(a|b)`. A plain
`(...)` is a *capturing* group, and ABChapterize reads one in a phrase as "the
chapter number is here" — which is exactly what the `()` above is, and what a
second one would collide with. A unit test
(`EveryRegisteredLanguage_HasUsableDefaultPhrases`) will fail if you slip.

Do not try to cover a *different word* your language might use for a chapter
("part", "book", "section"). Being greedy here costs accuracy: a phrase that
matches ordinary prose puts marks in the middle of scenes. Pick the one word a
narrator actually says, and let users reach for `--chapter-phrase` for the
rest.

The exception, should your language have one: if two *different* words are
genuinely interchangeable as the thing narrators announce — not "one common
one and one a publisher occasionally uses", but two you would be equally
unsurprised to hear — then the default has to cover both, or half the
audiobooks in that language get nothing. Write it as one alternation,
`/(?:^(?:woord|kapittel) ()|^(?:woord|kapittel))/`, and the earlier note applies: `(?:...)`, never
`(...)`. Test it against prose before you commit to it. The bar is high on
purpose, and no built-in language has cleared it so far: all eleven get by on
a single word, and the only alternations among them (Swedish and Danish
`kapit(?:el|let)`) are two endings of one word rather than two words.

For the prologue and the epilogue, prefer your language's Latin-derived form
("Prolog", "Prólogo", …) over a native near-synonym. Words like German
"Vorwort" or Turkish "Önsöz" mean *foreword* — front matter *about* the book —
whereas a prologue is part of the story, and that is what gets announced.

### The four titles

`ChapterTitle` is the word marks are named after: with `Kapitola`, chapters
come out as "Kapitola 1", "Kapitola 2", …. Write it the way it should appear
in a player — properly capitalized, with all its accents.

`PartTitle` is only used for a book whose chapters count from one again in
every part, where the marks come out as "Část 2 - Kapitola 1". Pick the word
for a *structural division of a book*, not a synonym of the chapter word —
Turkish uses "Bölüm" for a chapter and "Kısım" here, and reusing one for both
would produce "Bölüm 2 - Bölüm 1". Two rules follow from how it is read back
off a written title: it must be a word the language does not routinely use at
the start of a chapter heading, and if it happens to be the beginning of a
longer word (Swedish "Del" is also the start of "Delen"), that is fine — a
prefix only counts when a non-letter follows it.

`IntroTitle` names the mark covering whatever comes before the first chapter
(a publisher's announcement, a title read aloud). "Intro" is fine if your
language borrows it; otherwise use the natural word.

`PrologueTitle` and `EpilogueTitle` name those two marks. They are usually the
phrase with a capital letter.

### `NumberParser`

Point it at your parser from step 3. If you are not writing one yet, use
English for the moment — digits and Roman numerals still work, spelled-out
numbers just will not:

```csharp
public INumberWordParser NumberParser { get; } = new EnglishNumberParser();
```

One thing you do *not* have to worry about: your language's number words are
tried before the Roman-numeral reading, so a word that happens to be spelled
like one is safe. French "dix" is ten, even though `DIX` is also a perfectly
good Roman 509.

(Do please come back and replace it. Many audiobooks spell their numbers out.)

---

## 3. Step 2 — register it

Open `src\Language\LanguageRegistry.cs` and add one line to the `All` array:

```csharp
    private static readonly ILanguage[] All =
    [
        new EnglishLanguage(),
        new GermanLanguage(),
        ...
        new DanishLanguage(),
        new CzechLanguage(),      // <- yours
    ];
```

That is the entire wiring. The `--lang` help text, the supported-language
list, the number-parser lookup and the localized defaults all read from this
array; none of them needs touching.

---

## 4. Step 3 — the number parser

This is the part that takes an evening. Your job: turn the words Whisper wrote
("sedmnáct", "dvacet jedna") into the integers 17 and 21, for every number
from 0 to 999, as **cardinals** ("chapter twenty-one") and as **ordinals**
("the twenty-first chapter") — audiobooks use both.

Ordinals are allowed to stop short of 999 if your language spells them as
words of their own rather than deriving them from the cardinals. Spanish
("vigésimo" has nothing to do with "veinte") stops at 199 and Danish at 100
for exactly that reason: the words above are ones no narrator would say. Go as
far as the language stays regular, say where you stopped in the class doc
comment, and let the digit ordinals cover the rest.

Create `src\Language\Parsers\CzechNumberParser.cs` implementing
`INumberWordParser`. Read `INumberWordParser.cs` first; it is short and states
the contract precisely. Then pick the existing parser whose language works
most like yours and adapt it:

| Your language builds numbers like… | Start from |
| --- | --- |
| separate words, "twenty one" | `EnglishNumberParser.cs` |
| one compound word, units first, "einundzwanzig" | `GermanNumberParser.cs`, `DutchNumberParser.cs` |
| one compound word, tens first, "tjugoett" | `SwedishNumberParser.cs` |
| separate words with a connector, "vinte e um" | `PortugueseNumberParser.cs` |
| separate words, heavily inflected ordinals | `PolishNumberParser.cs` |

Three things to get right, all of which the existing parsers demonstrate:

- **`consumed`.** Report how many of the tokens you actually used. Callers
  matching a number that has to end exactly at the chapter phrase rely on it;
  get it wrong and "he said three. Chapter" becomes chapter 3.
- **Be forgiving.** Whisper writes what it hears. Normalize away accents,
  hyphens and alternate spellings rather than demanding the dictionary form —
  see the `Normalize` method most parsers have.
- **`DigitOrdinalSuffixPattern`.** If your language writes digit ordinals with
  a suffix ("1st", "1:a", "5'inci"), give the regex fragment for it, including
  any separator. If it just writes "17." — a number and a period — return an
  empty string; that case is already handled for every language.

There is a fourth member, `NumberWordPattern`: a regex fragment matching one
spoken number of your language, which is what the `()` of a phrase expands to.
Build it from the very tables you parse with, using the helpers in
`NumberWordPatterns.cs` — `TokenRun` for a language that writes a number as
separate words, `Alt` and `AnyOf` for one that compounds it — so that adding a
spelling to a table adds it to the pattern in the same edit. It is allowed, and
meant, to be a *superset* of what your parser accepts: whatever it captures is
handed straight back to `TryParse`, which is the authority on the value. Being
generous costs nothing; missing a spelling costs a chapter. `NumberWordPatternTests`
checks the direction that matters, replaying every spelling the reference
spellers produce for 0-999 through it.

---

## 5. Step 4 — tests

Run the suite from the repository root:

```
dotnet test tests\ABChapterize.Tests
```

Some tests will already cover your work the moment you register the language:
they walk `LanguageRegistry.Languages` and check that every language's phrases
compile, carry no capturing group, and have non-empty titles.

Two you should extend by hand:

- `SupportedLanguages_ListsAllParsers` in `NumberWordParserTests.cs` — add
  your code to the expected list.
- `DefaultPhrases_MatchTheirLanguagesAnnouncements` in `CliOptionsTests.cs` —
  add one `[InlineData]` row with a realistic chapter announcement, a prologue
  and an epilogue as Whisper would transcribe them. If your phrase has
  spelling variants, write the *awkward* spelling here; that is the whole
  point of the row.

If you wrote a number parser, add it to the exhaustive round-trip tests as
well: `ExhaustiveNumberWordTests.cs` for cardinals, `ExhaustiveOrdinalTests.cs`
for ordinals, and `NumberWordPatternTests.cs` for the `()` expansion. They spell
out every number 0–999 with an **independent** reference speller in
`Spellers.cs` and feed it back through your parser — the grammar is deliberately
written twice, so a misunderstanding of it cannot agree with itself. Copy the
reference speller closest to your language, then add your language to those
three files.

Finally, try it on a real book:

```
abchapterize --lang cs --dry-run kniha.m4b
```

`--dry-run` detects everything and writes nothing, so it is safe to run
repeatedly. Add `--verbose` to watch each announcement being recognized.

---

## 6. Step 5 — documentation

Two tables in `doc\manual.md`, section 7 ("Languages and number recognition"):

- the parsed-languages table (language, code, an example cardinal and ordinal
  announcement) — only if you wrote a number parser;
- the two defaults tables (chapter phrase / title / intro title, and the
  prologue/epilogue phrases and titles).

If your language is the first to need some trick — a script issue, a
transcription quirk worth warning about — say so in a sentence there.

Add a line to `CHANGELOG.md` under the unreleased version, written for
someone using the tool:

```markdown
- **Czech is now understood**: chapter announcements, spoken numbers 0–999 and
  the localized `--lang cs` defaults.
```

---

## 7. Building and running

You need the .NET SDK (version 10 or newer). From the repository root:

```
dotnet build                          # compile
dotnet test tests\ABChapterize.Tests  # run the tests
dotnet publish -c Release             # build the actual executable
```

The publish output lands in `bin\publish\win-x64\` (or `linux-x64\` with
`-r linux-x64`). You do **not** need to edit any project file: every `.cs`
under `src\` is compiled automatically, so a new file in the right folder is
picked up on its own.

Keep the build at zero warnings. Documentation comments are compiler-checked
here, so a `<see cref="..."/>` pointing at something that does not exist is a
warning, not a silent typo.

---

## 8. Checklist

- [ ] `src\Language\Languages\XxxLanguage.cs` written, all nine members filled in
- [ ] Chapter phrase written as `/(?:^WORD ()|^() WORD|^WORD)/`; any other grouping
      `(?:...)`, never `(...)`
- [ ] Titles carry their proper capitalization and accents
- [ ] One line added to `LanguageRegistry.All`
- [ ] `src\Language\Parsers\XxxNumberParser.cs` written (or English borrowed, temporarily)
- [ ] `SupportedLanguages_ListsAllParsers` and
      `DefaultPhrases_MatchTheirLanguagesAnnouncements` extended
- [ ] Round-trip tests, `NumberWordPattern` and reference speller added, if there is a
      number parser
- [ ] `doc\manual.md` section 7 tables updated
- [ ] `CHANGELOG.md` entry added
- [ ] `dotnet test` green, `dotnet build` free of warnings
- [ ] Tried against a real audiobook with `--lang xx --dry-run --verbose`

Thank you — every language added here is one more shelf of audiobooks that
stops being a wall of "Chapter 1" and starts being navigable.
