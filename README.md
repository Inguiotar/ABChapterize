# ABChapterize

**Correct chapter marks for your audiobooks — by actually listening to them.**

ABChapterize scans audiobook files (`.m4a`, `.m4b`, `.mp3`, `.opus`, `.mka`) for
spoken chapter announcements ("Chapter Seven", "Kapitel 12", …) using
[Whisper](https://github.com/ggerganov/whisper.cpp) speech recognition and
writes proper chapter marks directly into the file.
No splitting, no sidecar files, no server — the audio itself stays untouched,
only the chapter metadata is rewritten.

If you have ever bought an audiobook whose chapter marks were missing, misplaced,
or pure fantasy (looking at you, Audible), this tool is for you.

Prebuilt binaries for Windows and Linux are available on the
[Releases](../../releases) page — no build tools required.

## Highlights

- **Finds chapters by listening** — it detects the narrator's actual chapter
  announcements, not just gaps in the audio.
- **Writes marks in place, safely** — chapters are written by stream-copy
  remuxing into a temporary file that is verified before it atomically replaces
  the original. Your audiobook cannot be lost, even without `--backup`.
- **Jingle-aware** — if your audiobook plays a jingle before each announcement,
  the mark is placed *before* the jingle, where the chapter really starts. A
  bundled voice-activity model (Silero VAD) catches jingles even when they
  abut speech with no silence on either side, which a plain amplitude scan
  would miss entirely.
- **Self-healing** — when the detected chapter numbers have gaps (e.g. chapter 12
  was announced without a pause before it), the suspicious regions are
  transcribed in full to find the missing ones.
- **Zero setup for models** — the Whisper model is downloaded automatically on
  first use, and checked against pinned SHA-256 and SHA3-256 digests before
  it is used — a compromised or tampered-with download is rejected rather
  than silently loaded.
- **GPU accelerated** — uses CUDA or Vulkan when available, falls back to CPU.
- **Processes batches in parallel** — multiple files at once, auto-throttled to
  live CPU load (and capped at 1 concurrent file on GPU backends by default,
  for VRAM/context safety); override with `--jobs`.
- **Detects the language automatically** — by default, each file's language
  is detected from a short clip before transcription and used just for that
  file (falling back to English when the detection is inconclusive), so a
  mixed-language collection just works with no `--lang` at all. Pin it to one
  language with `--lang de` if you'd rather skip per-file detection.
- **Eleven languages** of number recognition out of the box — English, German,
  French, Spanish, Italian, Dutch, Turkish, Portuguese, Polish, Swedish and
  Danish. Whisper likes to write numbers out as words ("twenty-one",
  "einundzwanzig", "vingt et un", "veintiuno", "ventuno", "eenentwintig",
  "yirmi bir", "vinte e um", "dwadzieścia jeden", "tjugoett", "enogtyve"),
  and ABChapterize understands them all; other languages work with a custom
  phrase/regexp. Ordinal
  announcements are understood too, before or after the phrase — "Erstes
  Kapitel", "2. Kapitel", "chapitre premier", "Birinci Bölüm" — and `--lang`
  localizes the chapter phrase and title defaults (per-file when
  auto-detecting), so a German audiobook alone finds and writes "Kapitel"
  with no options at all.
- **All chapter-capable audio formats** — MP4 audiobooks (`.m4a`/`.m4b`), MP3,
  Opus and Matroska audio (`.mka`). (`.ogg` and `.flac` are out, through no
  fault of their own: ffmpeg cannot write chapter marks into those containers.)
- **Windows and Linux**, single self-contained executable.

## Getting started

### 1. Get ffmpeg

ABChapterize uses `ffmpeg`/`ffprobe` for audio decoding and chapter writing.
If you don't have it yet:

- **Windows:** download a build from [ffmpeg.org](https://ffmpeg.org/download.html)
  (e.g. the gyan.dev "essentials" zip) and unpack it. ABChapterize finds it
  automatically in `PATH`, in an `ffmpeg` folder next to the exe or in your user
  profile, in Program Files, or wherever `FFMPEG_DIR` points.
- **Linux:** `sudo apt install ffmpeg` (or your distribution's equivalent).

### 2. Get ABChapterize

Download the archive for your platform from the
[Releases](../../releases) page and unpack it anywhere you like.
Keep the `runtimes` folder next to the executable — it contains the native
Whisper libraries.

### 3. Run it

```
abchapterize "My Audiobook.m4b"
```

That's it. On the first run, the speech model is downloaded automatically
(about 1.6 GB for the default model — one time only, with a progress display).
Then the audiobook is scanned and the chapter marks are written:

```
Whisper model "turbo" loaded (Vulkan backend, auto language detection), 1 file(s) to process.
My Audiobook.m4b: 23 chapter(s) written (1-23) + intro, language: en (p=1.00)
```

Want to be extra careful on the first try? Use `--backup` — the original file
is kept as `My Audiobook.m4b.bak`, and `abchapterize --revert` restores it if you
don't like the result.

## Everyday examples

```sh
# A whole audiobook collection, subfolders included, keeping backups:
abchapterize --recurse --backup "D:\Audiobooks"

# A mixed-language collection: no --lang needed, each file's language is
# detected automatically and localizes "Kapitel"/"Chapter"/etc. per file:
abchapterize --recurse "D:\Audiobooks (mixed languages)"

# Pin a fixed language instead of auto-detecting per file (skips detection,
# slightly faster): the chapter phrase and title default to "Kapitel"
# automatically with --lang de:
abchapterize --lang de buch.m4b

# The publisher plays a jingle before each chapter announcement:
abchapterize --jingle hoerbuch.m4b

# Redo files that already have (wrong) chapter marks:
abchapterize --force badly-marked.m4b

# Not sure which files in a big collection have good marks and which don't?
# Check each existing mark against the audio; only the bad ones get redone:
abchapterize --recurse --verify "D:\Audiobooks"

# See what would be detected without writing anything:
abchapterize --dry-run "My Audiobook.m4b"

# Batch run: quiet, but with a summary at the end:
abchapterize -rqs "D:\Audiobooks"

# Detect, write, and also save a sidecar for manual review/correction:
abchapterize --export "My Audiobook.m4b"

# ...fix a chapter title in My Audiobook.m4b.chapters.ffmeta, then apply it
# without re-running Whisper:
abchapterize --import --force "My Audiobook.m4b"

# Process a big batch faster: several files at once, auto-throttled to CPU load:
abchapterize --recurse "D:\Audiobooks"

# Force a fixed number of concurrent files instead of the automatic ceiling:
abchapterize --recurse --jobs 4 "D:\Audiobooks"
```

## Options

Run `abchapterize --help` for a quick reference, or see the
[manual](doc/manual.md) for the full story — including exactly
[what is kept and what is stripped](doc/manual.md#5-what-is-kept-and-what-is-stripped)
when chapters are written. The most useful knobs:

| Option | What it does |
| --- | --- |
| `-r`, `--recurse` | Descend into subdirectories. |
| `-b`, `--backup` | Keep the original file as `*.bak`. |
| `-R`, `--revert` | Restore all `*.bak` backups (undo). |
| `-l`, `--lang <code\|auto>` | Language hint for Whisper, or `auto` (the default): each file's language is detected from a short clip and used for that file, falling back to `en` when the detection is inconclusive. Numbers transcribed as words — cardinal and ordinal, before or after the phrase — are understood in `en`, `de`, `fr`, `es`, `it`, `nl`, `tr`, `pt`, `pl`, `sv`, `da`; digits (`12`, `2nd`, `2e`) in every language. Also localizes the defaults of `--chapter-phrase` and `--title` (per-file with `auto`). |
| `-c`, `--chapter-phrase <p>` | Word or `/regexp/` announcing a chapter (default: `chapter`, localized by `--lang`). |
| `-m`, `--model <name>` | Whisper model: `tiny`, `base`, `small`, `medium`, `turbo` (default), `large`. `tiny`/`base` are not recommended for real audiobooks (see [Tuning tips](#tuning-tips)). |
| `-F`, `--filter <f>` | Only process matching files: `/regexp/` (against the whole path) or an extension list like `mp3,m4b`. |
| `-f`, `--force` | Redo files that already have chapter marks. |
| `-x`, `--max-chapters <n>` | Treat more than `<n>` pre-existing marks as bogus and discard them. |
| `-V`, `--verify` | Check pre-existing chapter marks against the audio instead of trusting them blindly (or requiring `--force`): marks that check out are left alone, marks that don't are discarded and the file goes through full detection. Cannot combine with `--force` or `--import`. |
| `-j`, `--jingle` | A jingle precedes announcements. Marks go 0.5 s before the jingle when a silence precedes it, or at the jingle's own start when it doesn't (found via a bundled voice-activity model — see [How it works](#how-it-works)). |
| `-X`, `--max-jingle-length <s>` | Longest expected jingle in seconds (default: 45). |
| `-n`, `--min-silence-length <s\|auto>` | Silence duration that counts as a potential chapter break; this is always the silence scan's floor (default, and floor with `auto`: 1.5). With `auto` (the default), the probing threshold self-tightens after every mark found (see [How it works](#how-it-works)); an explicit value probes every such silence instead. |
| `-t`, `--title <word>` | Word for generated chapter titles (default: `Chapter`, localized by `--lang`). |
| `-i`, `--intro-title <word>` | Title for the intro mark before the first chapter (default: `Intro`, localized by `--lang`). |
| `-q`, `--quiet` / `-s`, `--summary` | Less per-file output / totals (and confidence stats) at the end. |
| `-v`, `--verbose` | Log all transcriptions and processing details. |
| `-B`, `--no-bar` | No progress bar; per-file results as log lines. |
| `-d`, `--dry-run` | Detect chapters but write nothing; print what would be written. |
| `-e`, `--export` | Also save detected chapters to a sidecar file (`<file>.chapters.ffmeta`, or `<file>.chapters.txt` with `--simple-metadata`) for manual review or correction. Combinable with `--dry-run`. |
| `-I`, `--import` | Skip Whisper entirely and write chapters from a previously exported sidecar file instead — for reapplying a hand-corrected result. |
| `-S`, `--simple-metadata` | Use a plain `H:MM:SS.fff  Title` sidecar format instead of FFMETADATA for `--export`/`--import`. |
| `-J`, `--jobs <n\|auto>` | Number of files processed concurrently (default: `auto` — adjusted between 1 and a hardware-derived ceiling based on live CPU load). `-J 1` forces strictly sequential processing. |

Short options without parameters can be collapsed (`-rb` = `-r -b`).

## How it works

1. **Pass 1 — silence scan:** ffmpeg finds every silence longer than
   `--min-silence-length` (default, and floor with `auto`: 1.5 s below
   −35 dBFS) in one quick pass.
1b. **VAD pre-pass (`--jingle` only):** a bundled voice-activity model
   ([Silero VAD](https://github.com/snakers4/silero-vad)) scans the whole file
   for speech vs. non-speech. A jingle is music, which reads as non-speech to
   a speech detector just like silence does — so this catches jingles the
   amplitude-only silence scan misses entirely: one that abuts the narration
   with no detectable gap on either side. Where a silence leads into the
   jingle, the silence scan already has it covered and VAD adds nothing
   redundant; VAD only contributes new candidates at the silence-less
   transitions, so it doesn't add extra Whisper probes for books that don't
   need it.
2. **Pass 2 — probing:** a short stretch of audio after each silence (and
   after each silence-less jingle VAD found) is
   transcribed with Whisper and matched against the chapter phrase. The chapter
   number is parsed from digits or from numbers written out as words (0-999,
   cardinals and ordinals alike), whether it follows the phrase ("Chapter
   Seven") or precedes
   it ("Erstes Kapitel", "2. Kapitel", "Birinci Bölüm"). Window borders are
   snapped to silence mid-points so no decode ever cuts
   a word in half, and each detection is pinpointed to its own preceding
   silence right away — a confident mark even skips the remaining windows
   that overlap its own. By default
   (`--min-silence-length auto`), starting from the second mark found (the
   silence before the first mark is usually the intro/title silence, often
   longer than the breaks between chapters, so it's not used to tighten),
   the probing threshold sits at 75% of the *shortest* chapter-break
   silence observed so far — raised once, then only ever lowered — so
   shorter in-chapter pauses stop being probed once real inter-chapter
   breaks are established; everything skipped since the last mark is
   re-probed the moment a sequence gap turns up. An explicit
   `--min-silence-length` value disables
   this and probes every silence at or above it instead.
3. **Pass 3 — gap filling (only if needed):** if the chapter numbers found so
   far have sequence gaps, the regions where the missing chapters must be
   hiding are transcribed completely, in chunks whose borders snap to
   silences too (with the transcripts bridged across each seam, so not even
   a phrase interrupted by a pause right at a border can slip through). If a
   gap still remains, the file is left unchanged and a warning is printed.

A synthetic "Intro" mark (localized by `--lang`, customizable with
`--intro-title`) covers everything before the first detected chapter
(audiobooks usually start with title/credits), so the first real chapter
keeps its exact position.

## Tuning tips

- **Speed:** by default (`-n auto`), the silence threshold self-tightens as
  real chapter breaks are found, without having to guess a fixed value. If
  your audiobook's pauses vary too much for that to help, an explicit
  threshold like `-n 2.5` still works — far fewer Whisper probes, much faster
  run, but chapters go missing if it's set too high.
- **Jingles:** if you know the jingle is short, say so: `-j -X 15` shrinks the
  probe window and speeds things up.
- **Accuracy vs. speed:** `--model turbo` (default) is a good balance;
  `large` is the most accurate and slowest. Going smaller than `small` is
  not advisable for real audiobooks: chapter detection stands or falls with
  the recognizer catching a single short phrase, and `tiny` in particular
  mishears or drops chapter announcements so often that it is supported
  mostly for completeness (quick experiments, toy examples).
- **Unusual announcements:** `--chapter-phrase` accepts a regexp between
  slashes, e.g. `-c "/part (\d+)/"` — a capturing group is used as the chapter
  number directly.
- **Diagnosis:** run with `--verbose` to see all Whisper transcriptions and
  processing details as log lines — what the recognizer actually heard, and
  the confidence it had in each transcription. Chapter marks below 50%
  confidence are flagged in the per-file result line (even without
  `--verbose`) as worth a manual spot-check.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet publish -c Release                 # Windows build -> bin/publish/win-x64
dotnet publish -c Release -r linux-x64    # Linux build   -> bin/publish/linux-x64
dotnet test tests/ABChapterize.Tests        # run the unit tests
```

## License

[MIT](LICENSE). The bundled native Whisper libraries come from
[Whisper.net](https://github.com/sandrohanea/whisper.net) /
[whisper.cpp](https://github.com/ggerganov/whisper.cpp) (MIT), and the speech
models are OpenAI's [Whisper](https://github.com/openai/whisper) models (MIT).
The bundled jingle-detection model is [Silero VAD](https://github.com/snakers4/silero-vad)
(MIT, Copyright (c) 2020-present Silero Team) — see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the full notice.
ffmpeg is used as an external program and is not part of this project.
