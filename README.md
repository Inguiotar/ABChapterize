# Chapterize

**Correct chapter marks for your audiobooks — by actually listening to them.**

Chapterize scans audiobook files (`.m4a`, `.m4b`, `.mp3`, `.opus`, `.mka`) for
spoken chapter announcements ("Chapter Seven", "Kapitel 12", …) using
[Whisper](https://github.com/ggerganov/whisper.cpp) speech recognition and
writes proper chapter marks directly into the file.
No splitting, no sidecar files, no server — the audio itself stays untouched,
only the chapter metadata is rewritten.

If you have ever bought an audiobook whose chapter marks were missing, misplaced,
or pure fantasy (looking at you, Audible), this tool is for you.

## Highlights

- **Finds chapters by listening** — it detects the narrator's actual chapter
  announcements, not just gaps in the audio.
- **Writes marks in place, safely** — chapters are written by stream-copy
  remuxing into a temporary file that is verified before it atomically replaces
  the original. Your audiobook cannot be lost, even without `--backup`.
- **Jingle-aware** — if your audiobook plays a jingle before each announcement,
  the mark is placed *before* the jingle, where the chapter really starts.
- **Self-healing** — when the detected chapter numbers have gaps (e.g. chapter 12
  was announced without a pause before it), the suspicious regions are
  transcribed in full to find the missing ones.
- **Zero setup for models** — the Whisper model downloads itself on first use.
- **GPU accelerated** — uses CUDA or Vulkan when available, falls back to CPU.
- **Seven languages** of number recognition out of the box — English, German,
  French, Spanish, Italian, Dutch and Turkish. Whisper likes to write numbers
  out as words ("twenty-one", "einundzwanzig", "vingt et un", "veintiuno",
  "ventuno", "eenentwintig", "yirmi bir"), and Chapterize understands them
  all; other languages work with a custom phrase/regexp. Ordinal
  announcements are understood too, before or after the phrase — "Erstes
  Kapitel", "2. Kapitel", "chapitre premier", "Birinci Bölüm" — and `--lang`
  localizes the chapter phrase and title defaults, so `--lang de` alone finds
  and writes "Kapitel".
- **All chapter-capable audio formats** — MP4 audiobooks (`.m4a`/`.m4b`), MP3,
  Opus and Matroska audio (`.mka`). (`.ogg` and `.flac` are out, through no
  fault of their own: ffmpeg cannot write chapter marks into those containers.)
- **Windows and Linux**, single self-contained executable.

## Getting started

### 1. Get ffmpeg

Chapterize uses `ffmpeg`/`ffprobe` for audio decoding and chapter writing.
If you don't have it yet:

- **Windows:** download a build from [ffmpeg.org](https://ffmpeg.org/download.html)
  (e.g. the gyan.dev "essentials" zip) and unpack it. Chapterize finds it
  automatically in `PATH`, in an `ffmpeg` folder next to the exe or in your user
  profile, in Program Files, or wherever `FFMPEG_DIR` points.
- **Linux:** `sudo apt install ffmpeg` (or your distribution's equivalent).

### 2. Get Chapterize

Download the archive for your platform from the
[Releases](../../releases) page and unpack it anywhere you like.
Keep the `runtimes` folder next to the executable — it contains the native
Whisper libraries.

### 3. Run it

```
chapterize "My Audiobook.m4b"
```

That's it. On the first run, the speech model is downloaded automatically
(about 1.6 GB for the default model — one time only, with a progress display).
Then the audiobook is scanned and the chapter marks are written:

```
Whisper model "turbo" loaded (Vulkan backend), 1 file(s) to process.
My Audiobook.m4b: 23 chapter(s) written (1-23) + intro
```

Want to be extra careful on the first try? Use `--backup` — the original file
is kept as `My Audiobook.m4b.bak`, and `chapterize --revert` restores it if you
don't like the result.

## Everyday examples

```sh
# A whole audiobook collection, subfolders included, keeping backups:
chapterize --recurse --backup "D:\Audiobooks"

# German audiobook ("Kapitel eins", "Erstes Kapitel", ...) - the chapter
# phrase and title default to "Kapitel" automatically with --lang de:
chapterize --lang de buch.m4b

# The publisher plays a jingle before each chapter announcement:
chapterize --jingle hoerbuch.m4b

# Redo files that already have (wrong) chapter marks:
chapterize --force badly-marked.m4b

# Batch run: quiet, but with a summary at the end:
chapterize -rqs "D:\Audiobooks"
```

## Options

Run `chapterize --help` for a quick reference, or see the
[manual](doc/manual.md) for the full story — including exactly
[what is kept and what is stripped](doc/manual.md#5-what-is-kept-and-what-is-stripped)
when chapters are written. The most useful knobs:

| Option | What it does |
| --- | --- |
| `-r`, `--recurse` | Descend into subdirectories. |
| `-b`, `--backup` | Keep the original file as `*.bak`. |
| `--revert` | Restore all `*.bak` backups (undo). |
| `-l`, `--lang <code>` | Language hint for Whisper (default: `en`). Numbers transcribed as words — cardinal and ordinal, before or after the phrase — are understood in `en`, `de`, `fr`, `es`, `it`, `nl`, `tr`; digits (`12`, `2nd`, `2e`) in every language. Also localizes the defaults of `--chapter-phrase` and `--title`. |
| `-c`, `--chapter-phrase <p>` | Word or `/regexp/` announcing a chapter (default: `chapter`, localized by `--lang`). |
| `-m`, `--model <name>` | Whisper model: `tiny`, `base`, `small`, `medium`, `turbo` (default), `large`. |
| `-F`, `--filter <f>` | Only process matching files: `/regexp/` (against the whole path) or an extension list like `mp3,m4b`. |
| `-f`, `--force` | Redo files that already have chapter marks. |
| `-x`, `--max-chapters <n>` | Treat more than `<n>` pre-existing marks as bogus and discard them. |
| `-j`, `--jingle` | A jingle precedes announcements; marks go before the jingle. |
| `-X`, `--max-jingle-length <s>` | Longest expected jingle in seconds (default: 45). |
| `-n`, `--min-silence-length <s>` | Silence duration that counts as a potential chapter break (default: 1.5). |
| `-t`, `--title <word>` | Word for generated chapter titles (default: `Chapter`, localized by `--lang`). |
| `-i`, `--intro-title <word>` | Title for the intro mark before the first chapter (default: the title word plus `0`, e.g. `Chapter 0`). |
| `-q`, `--quiet` / `-s`, `--summary` | Less per-file output / totals at the end. |
| `-v`, `--verbose` | Log all transcriptions and processing details. |
| `--no-bar` | No progress bar; per-file results as log lines. |

Short options without parameters can be collapsed (`-rb` = `-r -b`).

## How it works

1. **Pass 1 — silence scan:** ffmpeg finds every silence longer than
   `--min-silence-length` (default 1.5 s below −35 dBFS) in one quick pass.
2. **Pass 2 — probing:** a short stretch of audio after each silence is
   transcribed with Whisper and matched against the chapter phrase. The chapter
   number is parsed from digits or from numbers written out as words (0-999,
   cardinals and ordinals alike), whether it follows the phrase ("Chapter
   Seven") or precedes
   it ("Erstes Kapitel", "2. Kapitel", "Birinci Bölüm").
3. **Pass 3 — gap filling (only if needed):** if the chapter numbers found so
   far have sequence gaps, the regions where the missing chapters must be
   hiding are transcribed completely. If a gap still remains, the file is left
   unchanged and a warning is printed.

A synthetic "Chapter 0" intro mark covers everything before the first detected
chapter (audiobooks usually start with title/credits), so the first real
chapter keeps its exact position.

## Tuning tips

- **Speed:** if your audiobook has generous pauses at chapter breaks, raise the
  silence threshold, e.g. `-n 2.5` — far fewer Whisper probes, much faster run.
  If chapters go missing, lower it again.
- **Jingles:** if you know the jingle is short, say so: `-j -X 15` shrinks the
  probe window and speeds things up.
- **Accuracy vs. speed:** `--model turbo` (default) is a good balance. `tiny`
  is much faster but mishears more; `large` is the most accurate and slowest.
- **Unusual announcements:** `--chapter-phrase` accepts a regexp between
  slashes, e.g. `-c "/part (\d+)/"` — a capturing group is used as the chapter
  number directly.
- **Diagnosis:** run with `--verbose` to see all Whisper transcriptions and
  processing details as log lines — what the recognizer actually heard.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet publish -c Release                 # Windows build -> bin/publish/win-x64
dotnet publish -c Release -r linux-x64    # Linux build   -> bin/publish/linux-x64
dotnet test tests/Chapterize.Tests        # run the unit tests
```

## License

[MIT](LICENSE). The bundled native Whisper libraries come from
[Whisper.net](https://github.com/sandrohanea/whisper.net) /
[whisper.cpp](https://github.com/ggerganov/whisper.cpp) (MIT), and the speech
models are OpenAI's [Whisper](https://github.com/openai/whisper) models (MIT).
ffmpeg is used as an external program and is not part of this project.
