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
  announcements, not just gaps in the audio, by first probing short candidate
  windows at silences and jingles and only falling back to transcribing a full
  suspicious region when that isn't enough — keeping the time-consuming
  Whisper work to a minimum.
- **Writes marks in place, safely** — chapters are written by stream-copy
  remuxing into a temporary file that is verified before it atomically replaces
  the original. Your audiobook cannot be lost, even without `--backup`.
- **Jingle-aware probing, by default** — Pass 2's probe window is sized to
  catch a jingle (a music sting) before the announcement, and a bundled
  voice-activity model (Silero VAD) finds jingles even when they abut speech
  with no silence on either side, which a plain amplitude scan would miss
  entirely. `--mark-before-jingle` (experimental) anchors the written mark to
  the jingle/silence itself instead of the default fixed offset before the
  phrase.
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
  `.m4b` is recommended: `.m4a` and `.m4b` are identical containers, but
  players may pick their exact behavior based on which extension they see, and
  chapters in `.mp3`/`.opus` files are honored by comparatively few players.
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

# The publisher plays a jingle before each chapter announcement — jingle-aware
# probing (and the VAD pre-pass) run by default, no flag needed:
abchapterize hoerbuch.m4b

# ...want the mark anchored to the jingle/silence itself instead of the
# default fixed offset before the phrase? (experimental):
abchapterize --mark-before-jingle hoerbuch.m4b

# Redo files that already have (wrong) chapter marks:
abchapterize --force badly-marked.m4b

# Not sure which files in a big collection have good marks and which don't?
# Check each existing mark against the audio; only the bad ones get redone:
abchapterize --recurse --verify "D:\Audiobooks"

# Not sure a --filter regexp actually matches the files you mean? List them
# without touching Whisper, ffmpeg or any file:
abchapterize --no-op --filter "/brandon sanderson/" --recurse "D:\Audiobooks"

# See what would be detected without writing anything:
abchapterize --dry-run "My Audiobook.m4b"

# Retry only the files a previous run couldn't fully chapterize (tagged
# "<name>.missing-marks-<n>-<n>-....<ext>" - see the manual's troubleshooting
# section): running over them again resumes automatically, re-probing only
# the still-tagged gap(s) - no extra flag needed, and the rest of a big
# collection is left untouched either way:
abchapterize --recurse --filter "/\.missing-marks-/" "D:\Audiobooks"

# ...or discard that partial work and start such a file over from scratch:
abchapterize --recurse --force --filter "/\.missing-marks-/" "D:\Audiobooks"

# Batch run: quiet, but with a summary at the end:
abchapterize -rqs "D:\Audiobooks"

# Detect, write, and also save a sidecar for manual review/correction:
abchapterize --export "My Audiobook.m4b"

# ...fix a chapter title in My Audiobook.m4b.chapters.ffmeta, then apply it
# without re-running Whisper:
abchapterize --import --force "My Audiobook.m4b"

# Processing several files at once, auto-throttled to CPU load, is already the
# default for any multi-file run - no flag needed. Force a fixed number of
# concurrent files instead of the automatic ceiling:
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
| `-O`, `--no-op` | List every file `--filter` (and `--recurse`) would select, then exit without loading Whisper, invoking ffmpeg or touching any file — a quick way to check a `--filter` regexp or extension list before a real run. Requires `--filter`; combinable only with `--recurse` and the output options. |
| `-l`, `--lang <code\|auto>` | Language hint for Whisper, or `auto` (the default): each file's language is detected from a short clip and used for that file, falling back to `en` when the detection is inconclusive. Numbers transcribed as words — cardinal and ordinal, before or after the phrase — are understood in `en`, `de`, `fr`, `es`, `it`, `nl`, `tr`, `pt`, `pl`, `sv`, `da`; digits (`12`, `2nd`, `2e`) in every language. Also localizes the defaults of `--chapter-phrase` and `--title` (per-file with `auto`). |
| `-c`, `--chapter-phrase <p>` | Word or `/regexp/` announcing a chapter (default: `chapter`, localized by `--lang`). |
| `-m`, `--model <name>` | Whisper model: `tiny`, `base`, `small`, `medium`, `turbo` (default), `large`. `tiny`/`base` are not recommended for real audiobooks (see [Tuning tips](#tuning-tips)). |
| `-M`, `--pass3-model <name>` | Whisper model for pass 3 (gap filling) only; same choices as `--model` (default: same as `--model`). Lighter to speed pass 3 up, or `large` for one last attempt at the gaps. Loaded lazily, only if pass 3 runs. |
| `-C`, `--cpu-only` | Force Whisper onto the CPU backend instead of the fastest available hardware acceleration. The Silero VAD pre-pass already always runs on CPU regardless of this option, so it only affects Whisper. |
| `-F`, `--filter <f>` | Only process matching files: `/regexp/` (against the whole path) or an extension list like `mp3,m4b`. |
| `-f`, `--force` | Redo files that already have chapter marks. |
| `-x`, `--max-chapters <n>` | Treat more than `<n>` pre-existing marks as bogus and discard them. |
| `-a`, `--early-abort <minutes>` | Always on (default: 60; `0` disables it). Give up on a file, unchanged, once this many minutes of play time have been probed with no chapter found — avoids transcribing a whole book that plainly isn't going to yield any. Only applies to a fresh detection run. |
| `-e`, `--expected-start-chapter <n>` | For a split-book part that doesn't start at chapter 1: the number this file is expected to start at. Without it (the default), whatever number pass 2 finds first is trusted outright and nothing below it is ever searched for. With it, a first chapter found *below* `<n>` aborts the file outright, unchanged; a first chapter found *above* `<n>` has pass 3 search for the missing numbers down to `<n>`, tagging the file `.missing-marks-…` if it still can't find them all. Only applies to a fresh detection run. |
| `-V`, `--verify` | Check pre-existing chapter marks against the audio instead of trusting them blindly (or requiring `--force`): marks that check out are trusted and kept, and only the stretch(es) of the file around any mark that doesn't get redetected. If every mark fails, the file falls back to full detection. Cannot combine with `--force` or `--import`. |
| `-h`, `--verify-threshold <n>` | Requires `--verify`. If more than `<n>` marks fail verification, the ones that passed are no longer trusted as gap-recovery anchors either — the whole file falls back to full detection, same as when nothing at all is confirmed. |
| `-j`, `--mark-before-jingle` | **Experimental.** Walk the mark backward from the default placement, back through the jingle's own music, to the end of the previous chapter's actual narration — or to the start of the last jingle, where several play back to back — instead of the default fixed offset before the phrase (see [How it works](#how-it-works)). Combinable with `-p`, which then supplies the starting point. |
| `-p`, `--precise-mark` | **Experimental.** Double-check every default-placed mark by re-transcribing the audio right at it, correcting it if the phrase isn't actually there (see [How it works](#how-it-works)). Slower — costs one or more extra transcriptions per chapter. Combinable with `-j`, which walks the result further back. |
| `-X`, `--max-jingle-length <s\|auto>` | Longest expected jingle in seconds; this is always the probe window's ceiling (default, and ceiling with `auto`: 45), or `0` for "no jingle expected at all" — narrows the probe window back down and skips the VAD pre-pass (unless `-j` still needs it). With `auto` (the default), the probe window self-tightens after every jingle mark found (see [How it works](#how-it-works)); an explicit value keeps the window fixed at it instead. |
| `-n`, `--min-silence-length <s\|auto>` | Silence duration that counts as a potential chapter break; this is always the silence scan's floor (default, and floor with `auto`: 1.5). With `auto` (the default), the probing threshold self-tightens after every mark found (see [How it works](#how-it-works)); an explicit value probes every such silence instead. |
| `-t`, `--title <word>` | Word for generated chapter titles (default: `Chapter`, localized by `--lang`). |
| `-i`, `--intro-title <word>` | Title for the intro mark before the first chapter (default: `Intro`, localized by `--lang`). |
| `-q`, `--quiet` / `-s`, `--summary` | Less per-file output / totals (confidence, silence/jingle, Whisper-audio and transcription-speed stats) at the end. |
| `-v`, `--verbose` | Log processing details, each probe/chunk as a `<length>@<time>` header. |
| `-T`, `--verbose-transcripts` | Like `--verbose`, but also dump every Whisper transcript's segments. Implies `--verbose`. |
| `-B`, `--no-bar` | No progress bar; per-file results as log lines. |
| `-d`, `--dry-run` | Detect chapters but write nothing; print what would be written. |
| `-E`, `--export` | Also save detected chapters to a sidecar file (`<file>.chapters.ffmeta`, or `<file>.chapters.txt` with `--simple-metadata`) for manual review or correction. Combinable with `--dry-run`. |
| `-I`, `--import` | Skip Whisper entirely and write chapters from a previously exported sidecar file instead — for reapplying a hand-corrected result. |
| `-S`, `--simple-metadata` | Use a plain `H:MM:SS.fff  Title` sidecar format instead of FFMETADATA for `--export`/`--import`. |
| `-J`, `--jobs <n\|auto>` | Number of files processed concurrently (default: `auto` — adjusted between 1 and a hardware-derived ceiling based on live CPU load). `-J 1` forces strictly sequential processing. |

Short options without parameters can be collapsed (`-rb` = `-r -b`).

## How it works

1. **Pass 1 — silence scan:** ffmpeg finds every silence longer than
   `--min-silence-length` (default, and floor with `auto`: 1.5 s below
   −35 dBFS) in one quick pass.
1b. **VAD pre-pass (default; skipped only with `--max-jingle-length 0` and no
   `--mark-before-jingle`):** a bundled voice-activity model
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
   a word in half, and each detection is pinpointed right away — a confident
   mark even skips the remaining windows that overlap its own. By default
   (`--min-silence-length auto`), starting from the second mark found (the
   silence before the first mark is usually the intro/title silence, often
   longer than the breaks between chapters, so it's not used to tighten),
   the probing threshold sits at 75% of the *shortest* chapter-break
   silence observed so far — raised once, then only ever lowered — so
   shorter in-chapter pauses stop being probed once real inter-chapter
   breaks are established; everything skipped since the last mark is
   re-probed the moment a sequence gap turns up. An explicit
   `--min-silence-length` value disables
   this and probes every silence at or above it instead. The jingle probe
   window (`--max-jingle-length` plus 5 seconds for the phrase itself)
   self-tightens the same way by default (`--max-jingle-length auto`):
   starting from the second jingle mark found, it resizes to 1.25x the
   longest jingle actually observed so far, capped at the 45 s ceiling — an
   explicit `--max-jingle-length` value keeps the window fixed at it instead.
2b. **Precise-mark check (only with `--precise-mark`/`-p`, experimental):** for
   the rare mark that still lands on the wrong spot — usually a jingle whose
   music briefly fools the voice-activity detector into sounding like speech —
   every mark is double-checked by re-transcribing a short, isolated clip of
   the audio right at it; if the phrase isn't really there, nearby candidates
   are checked the same way until it's found and the mark is corrected,
   falling back to a wider sweep of the same area on the rare chapter where
   even that doesn't confirm anything. Costs one or more extra transcriptions
   per chapter, so it's off by default.
3. **Pass 3 — gap filling (only if needed):** if the chapter numbers found so
   far have sequence gaps, the regions where the missing chapters must be
   hiding are transcribed completely, in chunks whose borders snap to
   silences too (with the transcripts bridged across each seam, so not even
   a phrase interrupted by a pause right at a border can slip through). Pass 3
   can use a different model than pass 2 (`--pass3-model`). If a gap still
   remains, the chapters that *were* found are written and the file is renamed
   with a `.missing-marks-…` tag listing the still-missing numbers, rather than
   discarded. Running the tool again over such a file resumes it
   automatically: the committed chapters are trusted as-is, and only the
   still-tagged gap(s) get their own pass 2/pass 3, exactly as after a failed
   `--verify`. The file is renamed back to its original name once every
   chapter is found, or re-tagged with the (possibly shorter) remaining list
   otherwise; `--force` bypasses this and redoes the whole file from scratch.

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
- **Jingles:** by default (`-X auto`), the probe window self-tightens as real
  jingle lengths are found, the same way `-n auto` does for silences. If you
  know the jingle is consistently short, an explicit `-X 15` narrows the
  window and speeds things up further; if there's no jingle at all, `-X 0`
  narrows it all the way back down and skips the VAD pre-pass too.
- **Accuracy vs. speed:** `--model turbo` (default) is a good balance;
  `large` is the most accurate and slowest. Going smaller than `small` is
  not advisable for real audiobooks: chapter detection stands or falls with
  the recognizer catching a single short phrase, and `tiny` in particular
  mishears or drops chapter announcements so often that it is supported
  mostly for completeness (quick experiments, toy examples).
- **Unusual announcements:** `--chapter-phrase` accepts a regexp between
  slashes, e.g. `-c "/part (\d+)/"` — a capturing group is used as the chapter
  number directly.
- **Diagnosis:** run with `--verbose` to see processing details as log lines,
  or `--verbose-transcripts` (`-T`) to also see every Whisper transcription —
  what the recognizer actually heard, and the confidence it had in each.
  Chapter marks below 50% confidence are flagged in the per-file result line
  (even without `--verbose`) as worth a manual spot-check.

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
