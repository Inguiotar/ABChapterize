# Changelog

All notable, user-visible changes to ABChapterize are recorded here — what changed
for you, not how it was built. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and version numbers follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.9.1] — unreleased

### Added

- **Chapter numbers announced as Roman numerals are now recognized**, in every
  language, alongside digits and spelled-out words. This is not about books that
  print their chapters that way — it is about Whisper, which transcribes a spoken
  "chapter thirteen" as `CHAPTER XIII` whenever it settles on a book-heading style,
  and may do that for some chapters of a book and not others. Until now those
  chapters were heard and then silently discarded for want of a readable number,
  which typically showed up as one stubborn chapter missing from an otherwise clean
  run. Only the standard spelling counts (`IIII` is not 4), and a one-letter numeral
  needs the period a heading gives it (`Chapter V.`), so an English "chapter I
  wrote" is not read as chapter 1. Where a word could be either, the language's own
  number words win: French "dix" is ten, not 509.

- **Spanish, Portuguese and Danish now understand compound spelled-out
  ordinals.** "Capítulo vigésimo primero", "capítulo vigésimo primeiro" and
  "Enogtyvende kapitel" are read as chapter 21; previously these three
  languages recognized only the short list of simple ordinals (1st–10th, and
  1st–20th for Danish) and a chapter announced this way went unnoticed. Both
  genders, the fused Spanish spelling ("decimoctavo"), the European and
  Brazilian Portuguese variants, and both the formal and everyday Danish tens
  ("halvtredsindstyvende" and "halvtredsende") are accepted. Spanish and
  Portuguese now cover 1st–199th, Danish 1st–100th.

- **An announcement whose number cannot be read no longer passes in silence.**
  When the chapter phrase is heard but no number can be made of what follows it,
  `--verbose` now says so and quotes what was actually transcribed there — and
  the spot is re-transcribed once more from a differently framed window, which is
  often all it takes, since the wording a recognizer produces depends on where the
  window around it starts. In-book mentions of the word "chapter" are not
  reported; only a stretch of audio that yielded no chapter at all is.

- **A chapter number that cannot be right is now questioned before it is
  believed.** A German "chapter nineteen" heard as chapter 90 used to be accepted
  at face value, leaving 70 chapters "missing" and sending the gap re-probe after
  47 candidate spots for nothing. Now, when a number would either leave more than
  three chapters missing at once or falls at or below the chapters already found,
  the spot is read again: first with `--pass3-model` when it names a better model
  than the probing one, then from two differently framed windows around the
  announcement. A re-reading is adopted only if it actually continues the chapter
  sequence, so a genuine jump in a book's numbering is left alone. `--verbose`
  reports each attempt and what came of it. A number below the sequence used to be
  discarded unheard; now it is discarded only once re-reading has failed to make
  sense of it — which turns some of those into the chapter they always were.

- **The progress bar now counts the extra marks it finds**, alongside the
  chapters: `ch 5(-1+1)` means chapter 5 is marked, one chapter below it is
  still outstanding, and one prologue, epilogue or `--custom` mark has been
  found. Extra marks turning up before the first chapter show as `ch 0(+1)`
  rather than waiting for a chapter to have something to hang off.

- **The progress bar and the `--summary` block are now in color** where the
  terminal supports it. In the bar: the fill and the file name in white,
  separators and brackets in dark grey, the percentage and the timer in cyan, the
  phase in a darker cyan, and the chapter count in dark green — grey while it is
  still `----`, with the bracketed count of missing chapters in dark red and
  the count of extra marks in the chapter count's own green. In
  the summary: prose in white, brackets in dark grey, and every measured value in
  cyan together with its unit, so `1.52 s` and `3.7%` each read as one figure.
  Nothing else is ever colored and a `--log-file` always receives plain text, so
  a logged or piped run looks exactly as it did before. **`--color`** takes
  `auto` (the default), `always` or `never`; `auto` stays quiet when the output
  is redirected, when `NO_COLOR` is set, and on Unix unless `TERM` names a
  16-color terminal. `--color always` overrides it for the
  terminals it misjudges — Git Bash on Windows, CI logs, and anything modern
  still calling itself plain `xterm`.

- **Pick the GPU by name.** `--use-gpu gtx` runs Whisper on the GPU whose name
  contains "gtx", matched case-insensitively against any part of it, and
  `--list-gpus` prints the names your machine reports:

  ```
  > abchapterize --list-gpus
  Vulkan GPUs on this machine:
    0: Intel(R) UHD Graphics 630 (integrated)
    1: NVIDIA GeForce GTX 1070 (discrete)
  ```

  A request that matches no GPU, or more than one, stops the run and lists what
  is available rather than quietly using a different card. Names are matched
  instead of numbers on purpose: the numbering is the driver's, and on one test
  machine it came out in opposite order depending on whether the user was
  sitting at the desktop or connected remotely.

- **`--mark-lead <seconds>`** (`-k`) sets how far in front of the announcement a
  mark is placed. Marks are located just as precisely whatever it is; all it
  decides is how much lead-in you hear before the narrator starts, which is a
  matter of taste. `0` marks the measured onset itself, and `--mark-before-jingle`
  ignores it, taking its position from the jingle instead.
- **Marks for anything else the narrator announces.** `--custom` takes
  `phrase:title` mappings, several of them separated by semicolons:

  ```
  abchapterize --custom "zwischenspiel:Zwischenspiel;/zeit[- ]?tafel/:Zeittafel" book.m4b
  ```

  A phrase is a plain word or a `/regexp/`, exactly as for `--chapter-phrase`,
  and no chapter number is parsed or expected. Unlike the prologue and epilogue,
  a custom phrase is matched at any point in the file and as often as it occurs,
  so a book with an interlude between every chapter gets a mark for each of
  them. It still has to be *announced*, judged by the same rules as a chapter
  phrase — a passing mention in the narration is not a mark.
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
- **Your own Whisper model.** `--model custom:<path>` — and `--pass3-model
  custom:<path>` — takes a GGML model file from anywhere on disk instead of one
  of the six built-in selectors: a fine-tune for a narrator or language, a
  quantized build, or just a model kept elsewhere. A leading `~` is expanded to
  your home directory, on Windows too, and the path is checked while the command
  line is parsed, so a typo fails before the run starts rather than hours into
  it. The file is used exactly as it is — never downloaded, never checked
  against a pinned checksum. Where ABChapterize needs to know whether one model
  outclasses another (deciding whether the quick pass 2.5 gap re-probe is worth
  it), it compares their file sizes.
- **`--log-file <path>`** (`-o`) keeps the log in a file instead of on screen.
  Asking for one is enough to switch logging on — `--verbose` is not needed
  alongside it, and `-T` still adds the transcripts. The console keeps its
  progress bar and its result lines, and the file receives those too — per-file
  summaries (including the ones `--quiet` holds back) and the `--summary` block
  — so an unattended run stays watchable while its detail is kept for later. An existing file is appended to rather than
  overwritten, with each run bracketed by a header naming the version and the
  command line; lines are written as they happen, so even a run that is cut
  short leaves its log behind.
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

- **Mark refinement is faster and no longer takes a running start.** It used to
  begin by sampling positions near the mark that the voice-activity detector had
  flagged as speech, and only fall back to searching the stretch the announcement
  was heard in when none of them confirmed. On a 12-hour test book that first step
  earned its keep 3 times out of 26 while spending 8 minutes on it — the search it
  was meant to save then placed all the remaining marks in under 4. It has been
  dropped, so every mark now goes straight to the search, with unchanged accuracy.
  With `--verbose`, refinement announces itself once per mark
  (`refining mark at … - narrowing in on the phrase between … and …`) instead of
  reporting a candidate walk first.
- **The progress bar leads with the percentage**, with the phase moved behind it
  into a separated section of its own — `[####----]  42% | Pass 2 | ch 6 | …`
  rather than `[####----] Pass 2  42% | ch 6 | …`.
- **Marks now sit 0.35 seconds before the announcement instead of 0.25.** Since
  marks became accurate to a tenth of a second, the old lead-in turned out to be
  cutting it too fine: a chapter would occasionally start so close to the first
  word that its opening consonant was clipped — and a hard one, like the "K" of
  "Kapitel", is easy to lose without a listener being able to say whether they
  heard it. Use `--mark-lead` to set it back, or anywhere else you like.
- **A single discrete GPU is now preferred automatically**, and the startup line
  names the GPU in use instead of only the backend:

  ```
  Whisper model "turbo" loaded (Vulkan backend on NVIDIA GeForce GTX 1070), 3 file(s) to process.
  ```

  Previously the Vulkan runtime took whichever GPU it enumerated first, which on
  a machine with an integrated GPU beside a discrete one could be the integrated
  one — 8.6× slower on the test machine, with nothing in the output to say so.
  Machines with one GPU, several discrete GPUs, or none that report themselves
  as discrete keep the runtime's own choice; use `--use-gpu` to decide those
  cases. The device is named either way, which also makes a less obvious case
  visible: a software rasterizer like `llvmpipe` is a real Vulkan device, so a
  container or WSL2 distro without GPU passthrough would report "Vulkan backend"
  while quietly running on the CPU. It now says `on llvmpipe`. A run that steps
  aside for the Vulkan runtime's own `GGML_VK_VISIBLE_DEVICES` variable names
  both the variable and the GPU it leaves in charge, rather than falling back
  to a bare "Vulkan backend" that looks like the naming failed.
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

- **A mark left far from its announcement is now recovered in seconds rather
  than minutes.** When the usual nearby candidates all fail, the fallback used
  to comb the surrounding audio a tenth of a second at a time — which, for a
  mark that had landed half a minute early, meant hundreds of transcriptions
  and twenty minutes of apparent silence on one mark. It now closes in on the
  announcement instead, roughly halving the search each time, and finds the
  same position in a couple of dozen checks. Marks that were already being
  placed correctly are unaffected.

- **`--verbose` now reports mark refinement** instead of going quiet while it
  works. Each mark logs the candidates it is about to check, and — for the rare
  mark whose announcement none of them confirm — the stretch it is about to
  search and where in it the announcement turned out to be.

### Fixed

- **A prologue, epilogue or `--custom` phrase now has to be announced.** These
  were exempt from the checks that tell a real announcement from a mention of
  the same words in passing, so a narrator saying "…and then there is the
  epilogue" could get a mark. They are now judged exactly as a chapter
  announcement is. Only affects runs with `--max-jingle-length 0`; with the
  voice-activity pre-pass on (the default) neither kind was being checked.
- **Numbered files are processed in the order a human would expect.** A folder's
  files used to be sorted character by character, so "Track 10.mp3" came before
  "Track 2.mp3". Digits are now compared as whole numbers, which also fixes the
  order files are reported in and what an interrupted run considers already done.
- **A chapter hidden behind an unusually long jingle is now recovered without the
  slow pass.** With `--max-jingle-length auto` (the default) the probe window
  narrows to fit the jingles seen so far, and a chapter announced later than that
  — a jingle well above the book's usual length, or a narrator pausing before the
  announcement — fell outside the window and was heard by nothing. Only candidates
  the tool had *skipped* were retried when a chapter turned up out of sequence, so
  a window that ran and came back empty was never looked at again and the chapter
  had to wait for the full-transcription pass, if it was found at all. Every
  candidate since the previous chapter is now retried at the full window width,
  and a jingle discovered that way sizes the window for the rest of the file.
  Where there is nothing to retry, `--verbose` now says so instead of moving on
  without comment.
- **Recovering a chapter now teaches the probe window how far it has to reach.** With
  `--max-jingle-length auto` the window narrows to fit the jingles seen so far, and it
  was sized purely from how long each jingle ran. A chapter whose announcement sits far
  from the nearest silence therefore kept being missed even after an identical case had
  just been recovered from a gap, because that recovery taught the window nothing: on one
  15½-hour German audiobook, four chapters were lost the same way and each cost its own
  retry pass. A chapter recovered from a gap now also reports how far into its window the
  announcement actually ended, and the window widens to cover that for the rest of the
  file — so the same shape of chapter is found the first time instead of after the fact.
  One recovery may widen the window by at most 25 %, so a single outlier chapter cannot
  leave every later probe running at the maximum window for hours; an extreme reach is
  granted over several recoveries instead.
- **A chapter recovered from a sequence gap is no longer found several times over.**
  The retry that closes a gap kept going through the candidates behind the recovered
  chapter, and because they cover the same stretch of audio each one found that same
  chapter again — placing, refining and then discarding an identical mark every time,
  for minutes of needless work on a long book (four times over for one chapter of a
  real audiobook). The retry now stops the moment the gap is closed, and an already
  recovered chapter cannot be picked up a second time. Detection then carries on from
  where it was, with the recovered chapters' own silences and jingles folded into the
  two self-tightening settings first.
- **A misheard chapter number no longer costs a genuine chapter its mark.** When
  the final transcription pass went looking for, say, the chapter 2 missing
  between chapters 1 and 3, a "chapter seven" misheard somewhere in that stretch
  was accepted as a find — and chapter 3, now out of sequence behind it, was
  dropped from the results. Only numbers that could actually be missing from the
  stretch being searched are considered.
- **An announcement the recognizer dropped from a long window is now heard on a
  second, shorter look.** Whisper reads audio in 30-second chunks, and a lone word
  spoken inside a jingle — a bare "Prolog", say — can vanish from the transcript
  entirely once the stretch being read crosses that length, while the very same
  audio is transcribed perfectly from a shorter one. When a stretch yields no mark
  at all and yet the voice-activity pre-pass heard someone speak inside its jingle,
  that spot is now read once more from a window short enough to survive, which
  recovers the announcement. Needs the pre-pass, so it does not apply with
  `--max-jingle-length 0`.
- **A mark no longer stops just short of the announcement it belongs to.** Where the
  recognizer timestamped the announcement several seconds later than it was actually
  spoken, mark refinement searched a stretch that began *after* the words, found
  nothing, and left the mark where the first estimate had put it — in one real case
  a second *into* the spoken "Chapter 21", which is exactly where a listener notices.
  The search now reaches far enough behind a suspect timestamp to cover the words it
  was meant to describe.
- **A mark near the end of the stretch it was found in is no longer placed at
  random.** Deciding whether the announcement is still audible from a given point
  means asking the recognizer about the audio after it, and when the mark sat close
  to the end of the transcribed stretch there was barely a second of it left to ask
  about — at which length the answer is a coin flip, and one wrong answer sent the
  search off by half a minute. Refinement now always listens to a few seconds,
  however little is nominally left, which fixed a `--custom` mark landing 30 seconds
  early in a real book.

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
