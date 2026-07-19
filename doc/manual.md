# ABChapterize manual

This is the complete reference for ABChapterize. For a quick start, see the
[README](../README.md).

Contents:

1. [What ABChapterize does](#1-what-abchapterize-does)
2. [Supported file formats](#2-supported-file-formats)
3. [How detection works](#3-how-detection-works)
4. [How chapters are written — file safety](#4-how-chapters-are-written--file-safety)
5. [What is kept and what is stripped](#5-what-is-kept-and-what-is-stripped)
6. [Command line reference](#6-command-line-reference)
7. [Languages and number recognition](#7-languages-and-number-recognition)
8. [Whisper models](#8-whisper-models)
9. [GPU acceleration](#9-gpu-acceleration)
10. [ffmpeg: requirements and discovery](#10-ffmpeg-requirements-and-discovery)
11. [xHE-AAC (USAC) files](#11-xhe-aac-usac-files)
12. [Output, progress and logging](#12-output-progress-and-logging)
13. [Exit codes](#13-exit-codes)
14. [Troubleshooting](#14-troubleshooting)

---

## 1. What ABChapterize does

ABChapterize scans audiobook files for the narrator's actual chapter
announcements ("Chapter Seven", "Kapitel 12", "chapitre premier", …) using
[Whisper](https://github.com/ggerganov/whisper.cpp) speech recognition, and
writes matching chapter marks into the file's metadata.

The audio itself is never re-encoded or altered in any way. Chapters are
written by *remuxing*: the compressed audio (and cover art) is copied
bit-for-bit into a fresh container that carries the new chapter list. The
result is verified before it replaces the original file, so an audiobook
cannot be lost — see [section 4](#4-how-chapters-are-written--file-safety).

Basic usage:

```
abchapterize [options] <file-or-directory>
```

When a directory is given, every supported audio file directly inside it is
processed (with `--recurse`, subdirectories too). Files that already have
chapter marks are skipped unless `--force` or `--max-chapters` says otherwise.

## 2. Supported file formats

| Extension | Container | Chapter format |
| --- | --- | --- |
| `.m4a`, `.m4b` | MP4 / iTunes audiobook | MP4 chapter atoms |
| `.mp3` | MPEG audio | ID3v2 `CHAP` frames |
| `.opus` | Ogg Opus | Vorbis-comment chapter tags |
| `.mka` | Matroska audio | Matroska chapter edition |

The set is determined by what ffmpeg can both read *and write* chapter marks
for; each of these formats has been verified to round-trip chapters through
the exact remux command ABChapterize uses.

Notably absent:

- **`.ogg` (Vorbis) and `.flac`** — ffmpeg's muxers for these containers
  silently drop chapter marks, so writing them is impossible with ffmpeg as
  the backend. Files with these extensions are not processed.
- Everything that is not an audio container (video files etc.).

Files with unsupported extensions are simply skipped during directory scans;
naming one directly as the target is an error.

## 3. How detection works

Detection runs in up to three passes per file.

### Pass 1 — silence scan

ffmpeg's `silencedetect` filter finds every silence of at least
`--min-silence-length` seconds (default 1.5) below −35 dBFS, in one quick
decode pass over the whole file. Chapter announcements in audiobooks
practically always follow such a pause.

The scan is guarded: if it ends prematurely (e.g. because of a damaged file),
the file is aborted with an error instead of silently reporting "no chapters".

### Pass 2 — probing

A short window of audio is transcribed with Whisper:

- at the very beginning of the file, and
- after the end of every detected silence.

The window is 12 seconds normally, or `--max-jingle-length` + 5 seconds with
`--jingle`. Each transcript is matched against the chapter phrase
(see `--chapter-phrase`), and the chapter number is parsed from digits or from
number words (see [section 7](#7-languages-and-number-recognition)).

Rules applied to the matches:

- Without `--jingle`, the phrase must start within 5 seconds after the
  silence — announcements come right after the pause; anything later is
  narration ("…as we learned in chapter three…") and is ignored.
- Only one chapter is accepted per probe window.
- The chapter mark is placed at the end of the silence (i.e. where the
  announcement starts). For a match at the very beginning of the file, the
  phrase position itself is used.
- With `--jingle`, the mark is placed 0.5 seconds *before* the jingle: the
  mark is anchored at the latest silence ending before the phrase, so the
  chapter starts where the jingle starts, not where the announcement starts.
- Duplicate detections of the same chapter number keep the earliest position;
  out-of-order regressions (typically in-text mentions like "as seen in
  chapter three") are dropped.
- Whisper's own confidence (average token probability) for the segment the
  chapter number came from travels with the detection. Marks below 0.5 are
  flagged as low-confidence instead of being silently trusted — see
  [section 12](#12-output-progress-and-logging).

### Pass 3 — gap filling (only when needed)

If the detected chapter numbers have sequence gaps (…7, 9…), or the first
detected chapter is not chapter 1 even though it starts more than 30 seconds
into the file, the regions where the missing chapters must be hiding are
transcribed *completely* (in 10-minute chunks with 10-second overlap, so no
phrase is cut in half). This catches announcements that were not preceded by
a long-enough silence.

If a gap *between* detected chapters still remains after pass 3, the file is
left **unchanged** and a warning is printed — a partially wrong chapter list
is worse than none. A first chapter number above 1 that cannot be pushed down
further is tolerated, though: some books simply start mid-series (which is
also why a first chapter within the first 30 seconds is taken as-is), and the
intro chapter covers the leading audio either way.

### The intro chapter

Audiobooks usually do not start with chapter one — there is a title
announcement, credits, a prologue. Many players (and the MP4 muxer itself)
force the first chapter mark to 0:00, which would move "Chapter 1" to the
very beginning and misplace it.

Therefore, when the first detected chapter starts later than one second into
the file, a synthetic intro chapter (default title: "Intro", localized by
`--lang`; customizable with `--intro-title`) is prepended at 0:00. The first
real chapter keeps its exact detected position.

## 4. How chapters are written — file safety

ABChapterize is designed so that the original audio cannot be lost, even
without `--backup`, even on a crash or power failure mid-write:

1. The chapter list is written to a temporary FFMETADATA file.
2. ffmpeg remuxes the original into a temporary file next to it
   (`<name>.<ext>.abchapterize.tmp<ext>`, e.g. `book.m4b.abchapterize.tmp.m4b`),
   stream-copying the audio and cover art — no re-encoding, no quality loss.
3. The temporary file is **verified** with ffprobe: its duration must match
   the original (within 2 seconds) and it must contain exactly the expected
   number of chapters. If verification fails, the original is untouched.
4. Only then is the original replaced:
   - with `--backup`: the original is renamed to `<name>.<ext>.bak`, then the
     new file takes its place (with rollback if that rename fails);
   - without `--backup`: the original is parked as `<name>.abchapterize.orig`,
     the new file takes its place, and only then is the parked original
     deleted (again with rollback on failure).

Temporary files (`*.abchapterize.*`) are cleaned up afterwards and are always
excluded from directory scans. If you ever find one lying around after a
power failure, the original file next to it is intact; just delete the stray
temporary file.

`abchapterize -R <target>` (`--revert`) undoes a `--backup` run: for every
supported audio file with an added `.bak` suffix, the current file is deleted
and the backup renamed back. `--revert` can be combined with `--recurse`, with
`--filter` (the filter then selects which backups are restored) and with the
output options (`--quiet`, `--summary`), but with no detection or safety
options.

## 5. What is kept and what is stripped

Chapter writing remuxes the file with ffmpeg, mapping streams explicitly.
**Read this if your files carry more than audio and a cover image.**

Kept:

- **All audio streams**, bit-for-bit (stream copy, no re-encoding).
- **All video streams**, which in audio files means **embedded cover art**
  (verified to survive for mp3; MP4 cover art is stored as metadata and is
  also kept).
- **Global metadata/tags**: title, artist, album, year, genre, comments and
  all other container-level tags.
- Per-stream metadata of the kept streams (e.g. the audio stream's language
  tag).

Replaced:

- **The chapter list.** Pre-existing chapter marks do not survive — that is
  the point of the tool. (Without `--force`/`--max-chapters`, files with
  existing chapters are skipped entirely and remain untouched.)

Stripped:

- **Subtitle streams** (e.g. lyrics or transcript tracks).
- **Data and timed-text streams**, including QuickTime-style chapter *text
  tracks* in MP4 files. This is deliberate: such a track duplicates the
  chapter information and would clash with the newly written chapter marks.
- **Attachments** (Matroska attachment streams other than cover art).

Audiobook files virtually never carry these extra streams, but if yours do:
run with `--backup` so the original is kept, or add the streams back with
ffmpeg afterwards.

## 6. Command line reference

```
abchapterize [options] <file-or-directory>
abchapterize -R|--revert [--recurse] [--filter <f>] <file-or-directory>
abchapterize --help | -?
abchapterize --version
```

Options must precede the file/directory argument, which must come last.
Short options that take no parameter may be collapsed: `-rb` = `-r -b`.
Short options that take a parameter (`-l`, `-c`, `-m`, `-x`, `-F`, `-X`,
`-n`, `-t`, `-i`) cannot be collapsed with others.

### Target selection

`<file-or-directory>` (required, last argument)
: A single audio file, or a directory whose supported audio files are
  processed. Naming a file with an unsupported extension is an error.

`-r`, `--recurse`
: Descend into subdirectories. Only valid with a directory target.

`-F`, `--filter <filter>`
: Only process matching files. Two forms, and one of each kind may be given
  (both must then match):

  - `"/regexp/"` — a regular expression between slashes, matched
    case-insensitively against the **whole path** of each candidate file.
    Example: `--filter "/brandon sanderson/"`.
  - `"ext1,ext2"` — a comma-separated list of extensions (with or without
    dots), e.g. `--filter mp3,m4b`. Only supported extensions are allowed.

  The filter also applies to `--revert` (it selects which backups are
  restored) and to directory scans in general. A single file named directly
  as the target is *also* subject to the filter.

### Detection behaviour

`-l`, `--lang <code|auto>`
: Two-letter ISO 639-1 language hint for Whisper, or `auto` (the default).
  With `auto`, each file's language is detected once from a short clip right
  after the silence scan (Whisper's own language detector, no separate model)
  and used for the rest of that file, falling back to `en` when the
  detection is inconclusive - see
  [Auto language detection](#auto-language-detection) below. An explicit
  two-letter code pins the whole run to one language instead, skipping
  detection entirely. Either way, for the languages listed in
  [section 7](#7-languages-and-number-recognition), the resolved language
  enables number-word parsing and localizes the defaults of
  `--chapter-phrase`, `--title` and `--intro-title` (per file with `auto`).
  `abchapterize --lang de buch.m4b` finds "Kapitel eins" and writes
  "Kapitel 1" without further options; so does plain `abchapterize buch.m4b`,
  via auto-detection.

`-c`, `--chapter-phrase <p>`
: The word or phrase that announces a chapter (default: `chapter`, localized
  by `--lang`). Matching is always case-insensitive. Two forms:

  - A literal word/phrase: `--chapter-phrase Teil`. The chapter number is
    expected directly after it ("Teil sieben") or, failing that, directly
    before it ("Siebter Teil") — see section 7.
  - A regular expression between slashes: `--chapter-phrase "/part (\d+)/"`.
    If the regexp contains a capturing group, the group must capture the
    chapter number as digits; without a group, the number is parsed from the
    surrounding words as with a literal phrase.

`-m`, `--model <name>`
: Whisper model: `tiny`, `base`, `small`, `medium`, `turbo` (default) or
  `large`. `tiny` and `base` are not recommended for real audiobooks; see
  [section 8](#8-whisper-models).

`-n`, `--min-silence-length <seconds>`
: Minimum silence duration (0.1–60, default: 1.5) that counts as a potential
  chapter break. Every such silence triggers a Whisper probe, so this is the
  main speed knob: if your audiobook has generous pauses at chapter breaks,
  `-n 2.5` can cut the number of probes dramatically. If chapters go missing,
  lower it again.

`-j`, `--jingle`
: Declare that a jingle (music sting) may precede each chapter announcement.
  Probe windows are widened to `--max-jingle-length` + 5 seconds, and chapter
  marks are placed 0.5 seconds *before* the jingle — where the chapter really
  starts — instead of at the announcement.

`-X`, `--max-jingle-length <seconds>`
: Longest expected jingle (1–600, default: 45). Requires `--jingle`. Lower
  values shrink the probe windows and speed up detection.

### Auto language detection

With `--lang auto` (the default - no `--lang` needed at all), each file's
language is detected independently, so a directory containing audiobooks in
several languages is processed correctly in one run without per-file options.

Mechanically, this happens once per file, right after the silence scan
(pass 1) and before any transcription: the samples already decoded for the
very first probe window (the start of the file) are also handed to Whisper's
own language detector (`WhisperProcessor.DetectLanguageWithProbability`),
which returns a language code and Whisper's own probability for it - no
extra decode, and no separate model. The resolved language is then fixed for
the rest of that file via `ChangeLanguage`, rather than re-detected per
probe, which would be both slower and could disagree with itself partway
through a file.

- At or above a probability of 0.5, the detected language is used, and
  `--chapter-phrase`, `--title` and `--intro-title` are localized for it
  (see [section 7](#7-languages-and-number-recognition)) exactly as an
  explicit `--lang <code>` would, but resolved individually per file.
- Below 0.5, or when the clip at the start of the file is too short to
  probe (well under half a second of audio), the detection is treated as
  inconclusive and the run falls back to `en` for that file - the same
  0.5 cutoff used for flagging low-confidence chapter marks (see
  [section 12](#12-output-progress-and-logging)), since Whisper itself is,
  on average, more unsure than sure below it.
- An explicit `--chapter-phrase`, `--title` or `--intro-title` always wins
  over the localized default, regardless of the detected language.

The outcome is shown in the per-file result line, `--dry-run` listing and
`--verbose` log:

```
Whisper model "turbo" loaded (Vulkan backend, auto language detection), 2 file(s) to process.
buch.m4b: 12 chapter(s) written (1-12) + intro, language: de (p=1.00)
book.m4b: 8 chapter(s) written (1-8) + intro, language: en (p=0.98)
```

`--verbose` additionally logs the detection as it happens:

```
[14:02:11] buch.m4b: language auto-detected: de (p=1.00)
```

or, when the run fell back to English:

```
[14:03:40] book.m4b: language auto-detection inconclusive (tr p=0.31); falling back to en
```

Pin a fixed language with `--lang <code>` to skip per-file detection
entirely - useful for a single-language collection (marginally faster, one
less moving part) or when detection is unreliable for a particular
recording (heavy accents, background music during the announcement).

### Handling of pre-existing chapters

Without any of these options, files that already have chapter marks are
skipped (reported as "skipped").

`-f`, `--force`
: Process such files anyway; their existing marks are discarded and replaced
  by the detection result.

`-x`, `--max-chapters <n>`
: Sanity threshold: a file with more than `<n>` pre-existing marks has them
  considered bogus (some publishers write a "chapter" every few minutes) and
  discarded, even without `--force`. Files at or below the threshold are
  still skipped unless `--force` is also given.

### Safety and undo

`-b`, `--backup`
: Keep the original file as `<name>.<ext>.bak` next to the modified file.
  If a `.bak` file already exists, the file is aborted with an error rather
  than overwriting the backup.

`-R`, `--revert`
: Restore backups instead of processing: for every supported audio file with
  an added `.bak` suffix under the target, the current file is deleted and
  the backup renamed back. Combinable with `--recurse`, `--filter` and the
  output options (`--quiet` and `--summary` take effect; `--verbose` and
  `--no-bar` are accepted but change nothing here). All detection and safety
  options are rejected. When a single audio file is given as the target, its
  `.bak` neighbour is restored.

### Titles

`-t`, `--title <word>`
: Word used to build chapter titles; the chapter number is appended
  (default: `Chapter`, localized by `--lang` — e.g. `Kapitel` with
  `--lang de`). "Chapter 1", "Chapter 2", …

`-i`, `--intro-title <word>`
: Title of the synthetic intro chapter covering the audio before the first
  detected chapter (default: `Intro`, localized by `--lang` — see the table
  in [section 7](#7-languages-and-number-recognition)). See
  [The intro chapter](#the-intro-chapter).

### Output

`-q`, `--quiet`
: Suppress per-file output and progress bars; warnings and errors are still
  shown.

`-v`, `--verbose`
: Print processing details and **all Whisper transcriptions** as timestamped
  log lines — the best way to see what the recognizer actually heard. See
  [section 12](#12-output-progress-and-logging).

`-B`, `--no-bar`
: Never display progress bars; per-file results are printed as timestamped
  log lines. Useful for CI jobs and log files. (Progress bars are also
  disabled automatically when the output is redirected.)

`-s`, `--summary`
: Print a summary at the end of the run: files encountered / processed /
  skipped, warnings, total and average processing time, and — when at least
  one chapter mark was written — the min/max/average Whisper confidence
  across those marks (not every probe attempted, only the ones that produced
  a mark).

`-d`, `--dry-run`
: Run full detection but write nothing. Instead of the usual "N chapter(s)
  written" line, the file's result shows every chapter that *would* be
  written, with its exact timestamp and title:

  ```
  My Audiobook.m4b: DRY RUN - would write 23 chapter(s) (1-23) + intro:
    0:00:00.00  Intro
    0:01:23.45  Chapter 1
    0:15:42.10  Chapter 2
    ...
  ```

  Everything else about the run is unaffected — pre-existing chapter
  handling (`--force`/`--max-chapters`), low-confidence flagging and
  `--summary` stats all behave exactly as in a real run; only the final
  ffmpeg remux is skipped, so the file is guaranteed untouched. Cannot be
  combined with `--revert` (there is nothing to preview when reverting).

### Chapter export/import

A rare misdetection (a mis-heard chapter number, a title that needs
tweaking) doesn't have to mean re-running Whisper on the whole file. Export
the detected chapters to a sidecar file, hand-correct it in a text editor,
then import it back — the corrected chapters are written directly, without
touching Whisper at all.

`-e`, `--export`
: In addition to writing chapters into the audio file as usual, also save
  them to a sidecar file next to it: `<file>.chapters.ffmeta` by default, or
  `<file>.chapters.txt` with `--simple-metadata`. Composes with normal
  detection — it is not a separate mode — and works together with
  `--dry-run`, so `abchapterize --dry-run --export book.m4b` previews the
  result *and* saves it for review without touching the audio file.

`-I`, `--import`
: Skip Whisper detection entirely and write the chapters found in the
  sidecar file instead (looked up next to the audio file, same naming as
  `--export`). If no sidecar file is found, the file is skipped with a
  message suggesting `--export`. Because there is nothing to detect,
  `--import` cannot be combined with any detection option — `--lang`,
  `--chapter-phrase`, `--model`, `--jingle`, `--max-jingle-length`,
  `--min-silence-length` — nor with `--revert`. Pre-existing chapter
  handling (`--force`/`--max-chapters`), `--backup`, `--dry-run` and
  `--summary` all behave the same as in a normal run; imported chapters
  have no Whisper confidence, so they never trigger low-confidence
  warnings and are not counted in `--summary`'s confidence stats.

`-S`, `--simple-metadata`
: Switch both `--export` and `--import` from the default FFMETADATA sidecar
  to a plain-text format: one `H:MM:SS.fff  Title` line per chapter (the
  de facto format used by tools like m4b-tool and `mp4chaps -e`), easier to
  hand-edit at the cost of a small custom parser on import. Requires
  `--export` or `--import`.

  The default FFMETADATA format is ffmpeg's own chapter metadata document
  (`;FFMETADATA1` header, one `[CHAPTER]` section per chapter with
  `TIMEBASE`/`START`/`END`/`title` keys — see
  [section 5](#5-what-is-kept-and-what-is-stripped)); characters `=`, `;`,
  `#`, `\` and literal newlines in titles are backslash-escaped, matching
  ffmpeg's own convention, so the file can also be fed straight to
  `ffmpeg -i metadata.txt -map_metadata` by hand if needed.

  Example FFMETADATA sidecar for a two-chapter file:

  ```
  ;FFMETADATA1
  [CHAPTER]
  TIMEBASE=1/1000
  START=0
  END=83450
  title=Intro
  [CHAPTER]
  TIMEBASE=1/1000
  START=83450
  END=942100
  title=Chapter 1
  ```

  The equivalent `--simple-metadata` sidecar:

  ```
  0:00:00.000  Intro
  0:01:23.450  Chapter 1
  ```

  In the plain-text format, blank lines and lines starting with `;` or `#`
  are ignored, so notes can be added freely between chapters.

### Parallel file processing

`-J`, `--jobs <n|auto>`
: Number of files processed concurrently (default: `auto`). With `auto`, the
  ceiling depends on the Whisper backend in use and is then adjusted live
  between 1 and that ceiling based on measured CPU load (sampled every
  250 ms, using `GetSystemTimes` on Windows and `/proc/stat` on Linux — no
  extra runtime dependency on either platform): above 85% CPU usage the
  limit is lowered by one; below 55% it is raised by one, at most one step
  per sample to avoid flapping.

  - **GPU backends (CUDA, Vulkan):** capped at 1 concurrent file. Running
    several Whisper inferences on the same GPU context at once is not
    something whisper.cpp guarantees is safe, and each concurrent instance
    would duplicate the model into VRAM, risking exhaustion. An explicit
    `-J`/`--jobs` value always overrides this cap if you want to force it
    anyway (verified to work correctly on a Vulkan GPU in testing, but at
    your own risk regarding VRAM).
  - **CPU backend:** ceiling is `Environment.ProcessorCount / 4`, clamped to
    1-4; each concurrent instance is given a matching share of threads so
    the total stays close to the core count instead of oversubscribing it.
  - **`--import`:** since there is no Whisper involved (just `ffprobe` and a
    direct metadata write), the ceiling is `Environment.ProcessorCount`,
    clamped to 1-8.

  `-J 1` forces strictly sequential processing, matching earlier versions'
  behavior. The number actually used is also clamped to the number of files
  being processed, so a single-file run is always sequential regardless of
  `--jobs`. Not valid with `--revert` (there is nothing to parallelize when
  restoring backups).

  With more than one file in flight, the progress display shows one bar per
  active file (see [section 12](#12-output-progress-and-logging)), and
  `--verbose` logs each automatic adjustment:

  ```
  [14:32:07] concurrency: CPU 50%, adjusted parallel file limit 1 -> 2
  ```

### Miscellaneous

`-?`, `--help`
: Show the usage information.

`--version`
: Show the version number.

## 7. Languages and number recognition

By default (`--lang auto`), the language used below is detected per file -
see [Auto language detection](#auto-language-detection). An explicit
`--lang <code>` pins the whole run to one language instead.

The chapter number in an announcement is recognized in two ways:

- **Digits** work in *every* language: "Chapter 12", "Kapitel 12.",
  "2nd", "2e", …
- **Numbers transcribed as words** — Whisper often writes numbers out
  ("Chapter twenty-one") — are parsed for these languages:

| Language | Code | Example cardinal | Example ordinal |
| --- | --- | --- | --- |
| English | `en` | chapter twenty-one | twenty-first chapter |
| German | `de` | Kapitel einundzwanzig | Einundzwanzigstes Kapitel |
| French | `fr` | chapitre vingt et un | premier chapitre |
| Spanish | `es` | capítulo veintiuno | primer capítulo |
| Italian | `it` | capitolo ventuno | primo capitolo |
| Dutch | `nl` | hoofdstuk eenentwintig | eenentwintigste hoofdstuk |
| Turkish | `tr` | bölüm yirmi bir | yirmi birinci bölüm |
| Portuguese | `pt` | capítulo vinte e um | primeiro capítulo |
| Polish | `pl` | rozdział dwadzieścia jeden | rozdział dwudziesty pierwszy |
| Swedish | `sv` | kapitel tjugoett | tjugoförsta kapitlet |
| Danish | `da` | kapitel enogtyve | enogtyvende kapitel |

All numbers from 0 to 999 are understood, as cardinals and as ordinals, and
the number may come **after** the phrase ("Chapter Seven") or **before** it
("Erstes Kapitel", "2. Kapitel", "chapitre premier", "Birinci Bölüm").
The parsers are exhaustively unit-tested against independent reference
spellers for every cardinal number 0–999 in every language. Word ordinals
are exhaustively tested 1–999 too, except for Spanish, Portuguese and
Danish, which only recognize a common, non-compound set of word ordinals
(1st-10th for Spanish/Portuguese, 1st-20th for Danish) since their compound
ordinals beyond that are rare in chapter announcements and, for Danish,
irregular enough (halvtredsindstyvende for 50th) to not be worth the risk —
digit ordinals ("21.", "50.") always work regardless of language.

For these languages, `--lang` also localizes the defaults of
`--chapter-phrase`, `--title` and `--intro-title`:

| `--lang` | Default phrase | Default title word | Default intro title |
| --- | --- | --- | --- |
| `en` | chapter | Chapter | Intro |
| `de` | Kapitel | Kapitel | Intro |
| `fr` | chapitre | Chapitre | Introduction |
| `es` | capítulo | Capítulo | Introducción |
| `it` | capitolo | Capitolo | Introduzione |
| `nl` | hoofdstuk | Hoofdstuk | Intro |
| `tr` | bölüm | Bölüm | Giriş |
| `pt` | capítulo | Capítulo | Introdução |
| `pl` | rozdział | Rozdział | Wstęp |
| `sv` | kapitel | Kapitel | Introduktion |
| `da` | kapitel | Kapitel | Introduktion |

Other languages work too: give `--lang` for transcription and a
`--chapter-phrase` (plain or regexp); announcements with digit numbers are
then fully supported, e.g. `--lang cs --chapter-phrase kapitola`.

## 8. Whisper models

| Selector | Model file | Download size | Notes |
| --- | --- | --- | --- |
| `tiny` | ggml-tiny.bin | ~75 MB | fastest; not suitable for real audiobooks |
| `base` | ggml-base.bin | ~140 MB | still error-prone; not recommended |
| `small` | ggml-small.bin | ~465 MB | smallest model with dependable results |
| `medium` | ggml-medium.bin | ~1.5 GB | |
| `turbo` | ggml-large-v3-turbo.bin | ~1.6 GB | **default** — near-large accuracy, much faster |
| `large` | ggml-large-v3.bin | ~3.1 GB | most accurate, slowest |

A word of warning about the small end of the scale: chapter detection hinges
on the recognizer catching one short, isolated phrase per chapter — there is
no surrounding context to recover from a misheard word, and a single missed
announcement leaves a sequence gap or a mismarked chapter. `tiny` mishears
or drops chapter announcements far too often for that to be reliable; its
support exists mostly for completeness — quick experiments, toy examples,
or extremely constrained machines. `base` fares somewhat better but is
still error-prone, especially for non-English audio. For real audiobooks,
use `small` or bigger; the default `turbo` is the best choice on almost
any hardware that can run it.

Models live in the `models` folder next to the executable. A missing model is
downloaded automatically on first use from the
[ggerganov/whisper.cpp](https://huggingface.co/ggerganov/whisper.cpp/tree/main)
repository on Hugging Face, with a progress display; partial downloads never
count as installed. If the download fails (offline machine, write-protected
folder), the error message contains step-by-step instructions for installing
the model manually.

## 9. GPU acceleration

The native Whisper runtime is selected automatically at start: **CUDA**
(NVIDIA) when available, then **Vulkan** (any modern GPU, including inside
WSL2), then CPU. The chosen backend is shown in the startup line:

```
Whisper model "turbo" loaded (Vulkan backend), 3 file(s) to process.
```

The `runtimes` folder next to the executable contains these native libraries
and must be kept — without it, nothing works.

## 10. ffmpeg: requirements and discovery

ABChapterize needs `ffmpeg` and `ffprobe` (any reasonably recent version) as
external programs; they do all decoding, silence scanning and chapter
writing. Search order:

1. **`FFMPEG_DIR` environment variable** (highest priority — an explicit
   choice overrides everything): both `%FFMPEG_DIR%\bin` and `%FFMPEG_DIR%`
   itself are checked, so pointing at an unpacked release folder just works.
2. `PATH`.
3. An `ffmpeg` folder (or `ffmpeg\bin`) next to the current directory, next
   to the executable, or in the user profile.
4. OS-typical locations: Program Files (Windows); `/usr/bin`,
   `/usr/local/bin`, `/opt/ffmpeg`, `/snap/bin`, `~/bin` and `~/.local/bin`
   (Linux).

Both executables must be found in the *same* directory. On Linux, install
with `sudo apt install ffmpeg` or your distribution's equivalent; on Windows,
download a build from [ffmpeg.org](https://ffmpeg.org/download.html) and
unpack it into one of the searched locations.

## 11. xHE-AAC (USAC) files

Some recent audiobooks (notably from certain store apps) use the xHE-AAC
(USAC) profile of AAC. ffmpeg's native AAC decoder cannot handle this profile
reliably; the Fraunhofer `libfdk_aac` decoder can, but it is
license-restricted ("nonfree") and therefore **not included in any official
ffmpeg download or distribution package**.

ABChapterize detects xHE-AAC files (even with an ffmpeg build that cannot probe
them at all) and:

- if the installed ffmpeg has `libfdk_aac`, transparently decodes with it and
  processes the file normally;
- otherwise skips the file with a warning explaining the situation.

To process such files, build ffmpeg with `--enable-libfdk-aac
--enable-nonfree` (on Windows e.g. via the
[media-autobuild suite](https://github.com/m-ab-s/media-autobuild_suite))
and point `FFMPEG_DIR` at the result. Since the audio is only stream-copied
when writing chapters, the nonfree build is needed for *reading* the audio;
the written file keeps the original xHE-AAC stream untouched.

## 12. Output, progress and logging

**Normal mode** shows a live progress bar per file (phase, percentage,
chapters found so far) that is replaced by a one-line result when the file is
done:

```
My Audiobook.m4b: 23 chapter(s) written (1-23) + intro
```

With more than one file processed concurrently (see
[`--jobs`](#parallel-file-processing)), one bar is shown per file currently
in flight, stacked in a block capped to the terminal's height (a terminal
resize is picked up automatically on the next refresh); each file's bar is
replaced by its own one-line result as soon as it finishes, while the other
active bars keep redrawing below it.

Progress bars are only drawn on an interactive console; when the output is
redirected (pipe, log file), they are suppressed automatically.

**`--quiet`** drops the per-file lines too; only warnings and errors appear.
Combine with `--summary` for a batch run that prints totals at the end.

**`--no-bar`** keeps the per-file result lines but prints them in the
timestamped log format instead of drawing bars — the right choice for CI
logs.

**`--verbose`** additionally logs, with a `[HH:mm:ss]` timestamp and the file
name, everything the pipeline does:

- probe result (duration, codec/profile, existing chapter marks),
- the silence count of pass 1,
- **every Whisper transcription** with segment timings and confidence
  (`p=0.87`) — both the probe windows and the full-transcription chunks of
  pass 3,
- every accepted chapter detection with the exact mark position and
  confidence, flagged `LOW CONFIDENCE` below 0.5,
- the regions transcribed in pass 3.

This is the primary diagnosis tool: it shows verbatim what the recognizer
heard, so you can see *why* an announcement was missed and adjust
`--chapter-phrase`, `--min-silence-length` or the model.

**Confidence flagging** — every chapter mark carries Whisper's own confidence
(the average token probability of the segment the number was parsed from).
When a written mark's confidence is below 0.5, the file's result line notes
it (`N low-confidence mark(s) (chapter ...; see --verbose)`) even without
`--verbose` — a nudge to spot-check that chapter rather than silently
trusting a mark Whisper itself was unsure about. With `--summary`, the
min/max/average confidence across all marks written in the run is printed
too.

Warnings (unresolved gaps, skipped xHE-AAC files, low-confidence marks) are
always shown, even with `--quiet`, and never abort the rest of a batch run.

## 13. Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Success. Files skipped or finished with warnings still count as success. |
| 1 | Fatal error (a file could not be processed; the run stops). |
| 2 | Command line usage error. |
| 130 | Aborted with Ctrl+C. |

Ctrl+C is handled gracefully: child processes are terminated and temporary
files are cleaned up on the way out.

## 14. Troubleshooting

**"No chapter phrases found"** — run with `--verbose` and read what Whisper
actually transcribed. Typical causes: the announcements use a different word
(fix with `--chapter-phrase`), the language is wrong (with `--lang auto`,
check the "language used" note in the result line - the auto-detection may
have picked the wrong language or fallen back to `en`; pin it with an
explicit `--lang` if so), the pauses are shorter than `--min-silence-length`
(lower `-n`), or a jingle sits between the pause and the announcement (add
`--jingle`).

**Chapters found but some are missing** — if the missing ones are announced
without a preceding pause, pass 3 usually catches them automatically. If a
gap remains, the file is left unchanged (see the warning); try a lower
`--min-silence-length` or a better model.

**A "chapter" was detected that isn't one** — in-text mentions are filtered
by the ordering heuristics, but a phrase like "chapter twelve" right after a
long pause can fool the tool. Use `--backup`, inspect the result, and
`--revert` if needed; a regexp phrase (`-c "/^\s*chapter (\d+)/"`) can help
with stubborn cases.

**It's slow** — see the speed knobs: `--min-silence-length` (fewer probes),
`--max-jingle-length` (smaller probe windows), a smaller `--model`. Check
that the startup line reports a GPU backend, not CPU.

**Model download fails** — the error message includes manual installation
steps; see [section 8](#8-whisper-models).

**ffmpeg not found** — see [section 10](#10-ffmpeg-requirements-and-discovery);
the quickest fix is setting `FFMPEG_DIR`.

**File skipped as xHE-AAC** — see [section 11](#11-xhe-aac-usac-files).
