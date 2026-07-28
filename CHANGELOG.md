# Changelog

All notable, user-visible changes to ABChapterize are recorded here — what changed
for you, not how it was built. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and version numbers follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.9.1] — unreleased

### Added

- **Marks for anything else the narrator announces.** `--custom` takes
  `phrase:title` mappings, several of them separated by semicolons:

  ```
  abchapterize --custom "zwischenspiel:Zwischenspiel;/zeit[- ]?tafel/:Zeittafel" book.m4b
  ```

  A phrase is a plain word or a `/regexp/`, exactly as for `--chapter-phrase`,
  and no chapter number is parsed or expected. Unlike the prologue and epilogue,
  a custom phrase is matched anywhere in the file and as often as it occurs, so
  a book with an interlude between every chapter gets a mark for each of them.
  Titles can pull text out of the phrase's own capturing groups with `$1`, `$2`
  or a group name. Only the first colon separates phrase from title, so a title
  may contain further ones; a `/regexp/` phrase ends at its closing slash
  instead, so a colon inside it is just a colon; and `\;` writes a semicolon
  inside a regexp. `--custom` may be repeated, and `--custom-file` reads the
  same mappings from a text file, one per line. At most 100 such marks are
  written per file — beyond that the rest are dropped and the file's summary
  line says so, so a phrase that matches ordinary prose cannot pepper a whole
  book with marks.
- **`--ignore-chapter-numbers`** keeps detecting chapter announcements but stops
  reasoning about the numbers in them — for books that restart their count in
  every part, bind several novels together, or announce "Chapter" and simply
  read on. Every announcement heard becomes a mark, keeping whatever number was
  spoken in its title (and just the bare title word when none was). Nothing
  checks that the numbers ascend or that any are missing, so no file is ever
  tagged `.missing-marks` and a run finishes right after the probing pass, which
  usually makes it noticeably quicker. The options that only mean something for
  a chapter sequence (`--pass3-model`, `--expected-start-chapter`,
  `--max-chapter-number`, `--trailing-scan`, `--verify`) are rejected alongside
  it rather than silently ignored.
- **Prologues and epilogues get their own marks.** A narrator announcing a
  "prologue" or an "epilogue" now produces a mark titled accordingly, alongside
  the numbered chapters. Both are on by default and localized by `--lang`, in
  all eleven supported languages. A prologue is only accepted while no numbered
  chapter has been found yet and an epilogue only once at least one has, at most
  one of each per file — so the "prologue" mentioned halfway through a plot
  summary is ignored. Reword them with `--prologue-phrase`/`--epilogue-phrase`
  (a plain word or a `/regexp/`, same as `--chapter-phrase`), rename the marks
  with `--prologue-title`/`--epilogue-title`, and switch either off entirely by
  passing an empty phrase, e.g. `--prologue-phrase ""`. These marks do not count
  toward the chapter-number sequence, so they never create or fill a gap.
- **Several files and folders in one command.** The trailing argument is now a
  list: `abchapterize -r "D:\Audiobooks" "E:\More" "one-off.m4b"` works, mixing
  files and folders freely. Duplicates (and files that a listed folder already
  covers) are processed once, in the order the arguments were given.
- **Interrupted batch runs pick up where they left off.** While a folder is
  being processed, a small `.abchapterize-progress` file in that folder records
  which files are already done. If the run is cut short — Ctrl+C, a crash, a
  power loss — simply running the same command again skips those files instead
  of scanning them all over again. Each folder given on the command line keeps
  its own progress file, and each is deleted the moment that folder is finished,
  so a run that completes normally leaves nothing behind. Change the options and
  the stale progress is discarded rather than misapplied; the new
  `--ignore-progress` ignores (and rewrites) it on demand, and `--dry-run`,
  `--no-op` and `--revert` never write one at all.

### Changed

- **The built-in chapter, prologue and epilogue phrases now cover each
  language's spelling variants.** They are regular expressions rather than
  single words, so a transcript that dropped an accent still matches
  ("capitulo" for "capítulo", "bolum" for "bölüm"), and a form the language
  itself changes is found too — Swedish and Danish "kapitlet" alongside
  "kapitel", English "prolog" alongside "prologue". Nothing about using them
  changes: they are still matched case-insensitively anywhere in the line, and
  `--chapter-phrase`/`--prologue-phrase`/`--epilogue-phrase` still override
  them. `--help` and the manual list each language's exact default.
- **A guide for adding a language**, [doc/adding-a-language.md](doc/adding-a-language.md),
  aimed at contributors who have never seen the rest of the codebase.
- **Marks now land within a tenth of a second of the announcement, and get there
  faster.** Precise marking used to accept a mark as soon as the chapter phrase
  was audible from it — but a jingle is exactly what Whisper does not transcribe,
  so a mark sitting several seconds inside one sounded just as convincing, and
  the mark stayed that far early. It now measures where the announcement really
  begins instead of taking the first plausible answer, and does so with a
  handful of checks rather than creeping toward it a tenth of a second at a
  time. Books with a musical sting before each chapter benefit most; marks that
  were already right stay where they are, give or take that tenth of a second.
  `--quick-marks` still skips the whole step.

### Fixed

- **A misheard chapter number no longer costs a genuine chapter its mark.** When
  the final transcription pass went looking for, say, the chapter 2 missing
  between chapters 1 and 3, a "chapter seven" misheard somewhere in that stretch
  was accepted as a find — and chapter 3, now out of sequence behind it, was
  dropped from the results. Only numbers that could actually be missing from the
  stretch being searched are considered.

## [0.9.0] — 2026-07-27

First public release.

- Finds chapter starts by transcribing the narrator's own chapter announcements
  with Whisper and writes real chapter marks into `.m4a`, `.m4b`, `.mp3`,
  `.opus` and `.mka` files, by stream-copy remux — the audio itself is never
  re-encoded.
- Three-pass detection: a silence scan plus a voice-activity (VAD) pre-pass,
  short Whisper probes at the resulting candidates, and full transcription of
  the suspicious regions only where the chapter numbers show a gap.
- Jingle-aware: a music sting before the announcement is expected by default,
  and `--mark-before-jingle` anchors the mark in front of it.
- Mark refinement on by default: every mark is re-checked against the audio and
  corrected if the phrase is not really there (`--quick-marks` opts out).
- Automatic per-file language detection, with spoken chapter numbers understood
  in English, German, French, Spanish, Italian, Dutch, Turkish, Portuguese,
  Polish, Swedish and Danish — cardinals and ordinals, before or after the
  phrase. `--lang` also localizes the chapter phrase and title defaults.
- Whisper models download themselves on first use and are verified against
  pinned SHA-256/SHA3-256 digests.
- CUDA and Vulkan acceleration with automatic CPU fallback; multiple files
  processed concurrently, throttled to live CPU load.
- Safety nets for the awkward cases: `--verify` checks existing marks against
  the audio, `--early-abort` gives up on hopeless files, `--max-chapter-number`
  rejects misheard numbers, and a file whose gaps could not be closed is tagged
  `.missing-marks-…` and resumed automatically on the next run.
- Review and repair tooling: `--dry-run`, `--export`/`--import` sidecars,
  `--backup`/`--revert`, `--no-op`.
- Single self-contained executable for Windows and Linux.
