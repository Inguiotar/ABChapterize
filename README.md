# ABChapterize

**Correct chapter marks for your audiobooks — by actually listening to them.**

ABChapterize is a command-line tool that scans audiobook files
(`.m4a`, `.m4b`, `.mp3`, `.opus`, `.mka`) for
spoken chapter announcements ("Chapter Seven", "Kapitel 12", …) using
[Whisper](https://github.com/ggerganov/whisper.cpp) speech recognition and
writes proper chapter marks directly into the file.
No splitting, no sidecar files, no server — the audio itself stays untouched,
only the chapter metadata is rewritten.

If you have ever bought an audiobook whose chapter marks were missing,
misplaced, or pure fantasy (looking at you, Audible), this tool is for you.

Finding the chapters is only half the job. ABChapterize's mission is getting
each mark to land in the *right place* — something most other tools treat as an
afterthought, dropping the mark wherever the nearest silence happened to be, or
a fixed handful of seconds off. Here, a found chapter mark is re-listened to
in order to pin down where the announcement actually begins, jingles are
located with a voice-activity model rather than guessed at from the waveform,
and the mark is placed relative to what the narrator is doing rather than to
the file's byte positions. Skipping ahead should land you on the chapter
announcement (or - if you prefer - on the start of the chapter's lead-in
jingle), not two sentences into the previous chapter's ending.

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
  entirely. `--mark-before-jingle` anchors the written mark to
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
  On a laptop or desktop with both an integrated and a discrete GPU, the
  discrete one is picked automatically (left alone, the Vulkan runtime may
  well grab the integrated one — measured 8.6x slower on a test machine), and
  the GPU in use is named in the startup line. Override with
  `--use-gpu <name>`; `--list-gpus` shows the names.
- **One file at a time, with the whole machine behind it** — the scan that opens
  every run spreads a single book across all your cores: on a 12-core machine an
  8.5-hour audiobook's first pass went from about 200 seconds to about 50, and
  found exactly the same speech. Thread counts default to your physical core
  count; `--vad-threads` and `--whisper-threads` override them.
- **Batches survive being interrupted** — name as many files and folders as you
  like in one command; each folder quietly keeps track of what is already done
  while it is being worked on, so a run stopped by Ctrl+C, a crash or a power
  loss picks up where it left off when you run it again. A run that finishes
  normally cleans up after itself.
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
  with no options at all. Missing your language? Adding one is a contained,
  well-signposted job — see
  [doc/adding-a-language.md](doc/adding-a-language.md).
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
  profile, in Program Files, or wherever `FFMPEG_DIR` points — at the unpacked
  folder or at the binaries themselves, either works.
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
Whisper model "turbo" loaded (Vulkan backend on NVIDIA GeForce GTX 1070, auto language detection), 1 file(s) to process.
My Audiobook.m4b: 23 chapter(s) written (1-23) + intro, language: en (p=1.00)
```

Want to be extra careful on the first try? Use `--backup` — the original file
is kept as `My Audiobook.m4b.bak`, and `abchapterize --revert` restores it if you
don't like the result.

## Everyday examples

```sh
# A whole audiobook collection, subfolders included, keeping backups:
abchapterize --recurse --backup "D:\Audiobooks"

# Several folders and single files in one go, in the order given. If this run
# is interrupted, repeating the command resumes it instead of starting over:
abchapterize --recurse "D:\Audiobooks" "E:\New arrivals" "F:\one-off.m4b"

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
# default fixed offset before the phrase?
abchapterize --mark-before-jingle hoerbuch.m4b

# Redo files that already have (wrong) chapter marks:
abchapterize --force badly-marked.m4b

# Marks that should be one per numbered chapter (say, from an earlier run),
# but you're not sure they all landed right? Check each against the audio;
# only the ones that don't check out get redone:
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

# Files are processed one at a time, each with the whole machine. Leave a few
# cores free for whatever else the machine is doing:
abchapterize --recurse --vad-threads 6 --whisper-threads 6 "D:\Audiobooks"
```

## Options

Run `abchapterize --help` for a quick reference, or see the
[manual](doc/manual.md) for the full story — including exactly
[what is kept and what is stripped](doc/manual.md#5-what-is-kept-and-what-is-stripped)
when chapters are written. Grouped below exactly as `--help` groups them:

**File selection**

| Option | What it does |
| --- | --- |
| `-r`, `--recurse` | Descend into subdirectories. |
| `-F`, `--filter <f>` | Only process matching files: `/regexp/` (against the whole path) or an extension list like `mp3,m4b`. |
| `-f`, `--force` | Redo files that already have chapter marks. |
| `-x`, `--max-chapters <n>` | Treat more than `<n>` pre-existing marks as bogus and discard them. |

**Detection tuning**

| Option | What it does |
| --- | --- |
| `-l`, `--lang <code\|auto>` | Language hint for Whisper, or `auto` (the default): each file's language is detected from a short clip and used for that file, falling back to `en` when the detection is inconclusive. Numbers transcribed as words — cardinal and ordinal, before or after the phrase — are understood in `en`, `de`, `fr`, `es`, `it`, `nl`, `tr`, `pt`, `pl`, `sv`, `da`; digits (`12`, `2nd`, `2e`) and Roman numerals (`XIII`) in every language. Also localizes the defaults of `--chapter-phrase`, `--prologue-phrase`, `--epilogue-phrase`, `--title`, `--intro-title`, `--prologue-title` and `--epilogue-title` (per-file with `auto`). |
| `-c`, `--chapter-phrase <p>` | Word or `/regexp/` announcing a chapter (default: `/chapter/`, localized by `--lang`). |
| `-p`, `--prologue-phrase <p>` | Word or `/regexp/` announcing a prologue (default: `/prolog/`, localized by `--lang`). Only accepted before the first numbered chapter, at most once per file; an empty value switches prologue detection off. |
| `-g`, `--epilogue-phrase <p>` | Word or `/regexp/` announcing an epilogue (default: `/epilog/`, localized by `--lang`). Only accepted after at least one numbered chapter, at most once per file; an empty value switches epilogue detection off. |
| `-u`, `--custom <mappings>` | Extra `phrase:title` mappings separated by `;`, e.g. `--custom "zwischenspiel:Zwischenspiel;/zeit[- ]?tafel/:Zeittafel"`. A phrase is a word or a `/regexp/`, parses no number, and is matched at any point in the file as often as it occurs (up to 100 marks per file), but must be announced just as a chapter phrase must. Titles may reference the phrase's capturing groups as `$1`, `$2` or by name. Repeatable; never localized. |
| `-U`, `--custom-file <path>` | Read `--custom` mappings from a text file, one per line (blank and `#` lines ignored). |
| `--ignore-chapter-numbers` | Detect chapter announcements as usual but form no opinion about their numbers: the spoken number still reaches the title, nothing checks the sequence. Passes 2.5 and 3 never run and nothing is ever tagged `.missing-marks`. Cannot combine with `--pass3-model`, `--expected-start-chapter`, `--max-chapter-number`, `--trailing-scan` or `--verify`. |
| `-m`, `--model <name>` | Whisper model: `tiny`, `base`, `small`, `medium`, `turbo` (default), `large`, or `custom:<path>` for a GGML model file of your own (used as-is: not downloaded, not checksum-verified, ranked against the built-in models by file size). `tiny`/`base` are not recommended for real audiobooks (see [Tuning tips](#tuning-tips)). |
| `-M`, `--pass3-model <name>` | Whisper model for pass 3 (gap filling) only; same choices as `--model`, `custom:<path>` included (default: same as `--model`). Lighter to speed pass 3 up, or `large` for one last attempt at the gaps. Loaded lazily, only if pass 3 runs. |
| `-C`, `--cpu-only` | Force Whisper onto the CPU backend instead of the fastest available hardware acceleration. The Silero VAD pre-pass already always runs on CPU regardless of this option, so it only affects Whisper. |
| `--use-gpu <name>` | Run Whisper on the GPU whose name contains `<name>`, case-insensitively — `--use-gpu gtx`, `--use-gpu uhd`. Only needed to override the automatic preference for a single discrete GPU, or to choose between several discrete ones. A request matching no GPU, or more than one, is an error listing the real names. Vulkan only. See [picking a GPU](doc/manual.md#picking-a-gpu-on-a-multi-gpu-machine). |
| `-j`, `--mark-before-jingle` | Walk the mark backward from the default placement, back through the jingle's own music, to the end of the previous chapter's actual narration — or to the start of the last jingle, where several play back to back — instead of the default fixed offset before the phrase (see [How it works](#how-it-works)). Where the walk ends on a pause, the mark backs into it by `-k` seconds; a chapter with no jingle at all keeps its ordinary placement. Best left alongside the default refinement: with `-Q` the walk starts from raw default placement, which occasionally overshoots the announcement and leaves the mark after it. |
| `-Q`, `--quick-marks` | **Experimental.** Skip the refinement that normally re-transcribes the audio at every mark to confirm the phrase is really there (see [How it works](#how-it-works)). Faster — saves a handful of transcriptions per chapter — but marks, while usually usable, may end up after the chapter phrase rather than before it, even together with `-j`. |
| `-X`, `--max-jingle-length <s\|auto>` | Longest expected jingle in seconds; this is always the probe window's ceiling (default, and ceiling with `auto`: 45), or `0` for "no jingle expected at all" — narrows the probe window back down and skips the VAD pre-pass (unless `-j` still needs it). With `auto` (the default), the probe window self-tightens after every jingle mark found (see [How it works](#how-it-works)); an explicit value keeps the window fixed at it instead. |
| `-k`, `--mark-lead <seconds>` | How far in front of the announcement a mark is placed (default 0.35). Purely a matter of taste — marks are located just as precisely whatever this is, it only decides how much lead-in you hear before the narrator starts. Below roughly 0.2 the announcement's opening consonant starts to get clipped; `0` marks the onset itself. Applies with `-j` too: in full where a chapter has no jingle, and as a back-off into the pause before the jingle where there is one (capped at that pause's length). |
| `-n`, `--min-silence-length <s\|auto>` | Silence duration that counts as a potential chapter break; this is always the silence scan's floor (default, and floor with `auto`: 1.5). With `auto` (the default), the probing threshold self-tightens after every mark found (see [How it works](#how-it-works)); an explicit value probes every such silence instead. |

**Detection safety nets**

| Option | What it does |
| --- | --- |
| `-a`, `--early-abort <minutes>` | Always on (default: 60; `0` disables it). Give up on a file, unchanged, once this many minutes of play time have been probed with no chapter found — avoids transcribing a whole book that plainly isn't going to yield any. Only applies to a fresh detection run. |
| `-e`, `--expected-start-chapter <n>` | For a split-book part that doesn't start at chapter 1: the number this file is expected to start at. Without it (the default), whatever number pass 2 finds first is trusted outright and nothing below it is ever searched for. With it, a first chapter found *below* `<n>` aborts the file outright, unchanged; a first chapter found *above* `<n>` has pass 3 search for the missing numbers down to `<n>`, tagging the file `.missing-marks-…` if it still can't find them all. Only applies to a fresh detection run. |
| `-L`, `--trailing-scan` | Transcribe everything after the last chapter found, through to the end of the file, looking for further chapters (default: off). A missing chapter is normally spotted as a hole in the number sequence, which needs a known chapter on either side of it — so one missing *after* the last chapter found is the one case nothing notices, and the file comes out looking complete. This closes that hole, but there are no expected numbers to satisfy here, so the scan can never stop early: every file pays a full final chapter's worth of transcription, whether or not anything was wrong. |
| `-N`, `--max-chapter-number <n>` | Highest chapter number this book plausibly has (default: no limit). A detected chapter numbered above `<n>` is discarded as a mishearing — without it, one misheard "chapter 510" in a twelve-chapter book turns everything in between into a gap to hunt for. Not to be confused with `--max-chapters` above, which counts *pre-existing* marks. |
| `-V`, `--verify` | Check pre-existing chapter marks against the audio instead of trusting them blindly (or requiring `--force`). The check assumes each mark is titled with a chapter number that the narrator announces right there: marks that check out are kept, only the stretch(es) around a mark that doesn't are redetected, and if every mark fails the file falls back to full detection. So this is **not** a read-only report, and it is the wrong tool for retailer marks that lump several book chapters into one entry — those cannot pass the check and will be replaced. Cannot combine with `--force` or `--import`. |
| `-h`, `--verify-threshold <n>` | Requires `--verify`. If more than `<n>` marks fail verification, the ones that passed are no longer trusted as gap-recovery anchors either — the whole file falls back to full detection, same as when nothing at all is confirmed. |

**Chapter titles**

| Option | What it does |
| --- | --- |
| `-t`, `--title <word>` | Word for generated chapter titles (default: `Chapter`, localized by `--lang`). |
| `-i`, `--intro-title <word>` | Title for the intro mark before the first detected mark (default: `Intro`, localized by `--lang`). |
| `-P`, `--prologue-title <word>` | Title for the prologue mark (default: `Prologue`, localized by `--lang`). |
| `-G`, `--epilogue-title <word>` | Title for the epilogue mark (default: `Epilogue`, localized by `--lang`). |

**Output & review**

| Option | What it does |
| --- | --- |
| `-d`, `--dry-run` | Detect chapters but write nothing; print what would be written. |
| `-E`, `--export` | Also save detected chapters to a sidecar file (`<file>.chapters.ffmeta`, or `<file>.chapters.txt` with `--simple-metadata`) for manual review or correction. Combinable with `--dry-run`. |
| `-I`, `--import` | Skip Whisper entirely and write chapters from a previously exported sidecar file instead — for reapplying a hand-corrected result. |
| `-S`, `--simple-metadata` | Use a plain `H:MM:SS.fff  Title` sidecar format instead of FFMETADATA for `--export`/`--import`. |

**File & backup management**

| Option | What it does |
| --- | --- |
| `-b`, `--backup` | Keep the original file as `*.bak`. |
| `-R`, `--revert` | Restore all `*.bak` backups (undo). |
| `-O`, `--no-op` | List every file `--filter` (and `--recurse`) would select, then exit without loading Whisper, invoking ffmpeg or touching any file — a quick way to check a `--filter` regexp or extension list before a real run. Requires `--filter`; combinable only with `--recurse` and the output options. |
| `--ignore-progress` | Start every listed folder over instead of resuming it. While a folder is being processed, the files finished so far are recorded in an `.abchapterize-progress` file inside it, which is deleted again as soon as that folder is done — so an interrupted run resumes by itself when the same command is run again. Progress recorded under different options is discarded automatically, so this is only needed to redo files the very same command already finished. |

**Logging & display**

| Option | What it does |
| --- | --- |
| `-q`, `--quiet` | Suppress the per-file lines; warnings and errors are still shown. |
| `-v`, `--verbose` | Log processing details, each probe/chunk as a `<length>@<time>` header. |
| `-T`, `--verbose-transcripts` | Like `--verbose`, but also dump every Whisper transcript's segments. Implies `--verbose`. |
| `-o`, `--log-file <path>` | Write the log to a file instead of the console — switches logging on by itself (add `-T` for the transcripts). The console keeps its progress bar and result lines, which the file gets too. Appends to an existing file. |
| `-B`, `--no-bar` | No progress bar; per-file results as log lines. |
| `--color <mode>` | Colorize the progress bar and the `--summary` block: `auto` (default), `always` or `never`. Nothing else is ever colored, and a `--log-file` always gets plain text. |
| `-s`, `--summary` | Totals at the end of the run: file counts, times, and confidence, silence/jingle, Whisper-audio and transcription-speed statistics — followed by a list of every file that was skipped and why, and of every file left with chapter marks still missing. |

**Performance** — files are always processed one at a time, so each gets the whole machine.

| Option | What it does |
| --- | --- |
| `--vad-threads <n\|auto>` | Threads for the voice-activity pre-pass of pass 1 (default: `auto` — one per physical CPU core). Each thread holds about 11 minutes of decoded audio while it works, so more of them also means more memory. `1` runs the pre-pass as a single uninterrupted stream. |
| `--whisper-threads <n\|auto>` | Threads for Whisper transcription (default: `auto` — one per physical CPU core). Mostly a CPU-backend concern; on a GPU backend the recognition itself runs on the GPU. |

**Info**

| Option | What it does |
| --- | --- |
| `-?`, `--help` | Show the usage info, from which these groups are taken. |
| `--version` | Show version and build information. |
| `--list-gpus` | List this machine's Vulkan GPUs as `--use-gpu` matches them, then exit. |

Short options without parameters can be collapsed (`-rb` = `-r -b`). Decimal
values accept either separator (`-n 2.5` = `-n 2,5`); printed numbers always
use `.`, whatever the machine's locale says.

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
   number is parsed from digits, Roman numerals, or numbers written out as
   words (0-999,
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
   A sequence gap puts the window back at the ceiling and retries every
   candidate since the last chapter at that width, including ones already
   probed while it was narrower — and since that retry knows both ends of the
   hole it's filling, only the numbers actually missing from it can be right
   there. A number that can't be right — one leaving
   more than three chapters missing at once, one below the chapters
   already found, or one outside the hole being searched — is read again before
   it's believed: with `--pass3-model` if
   that's the better model, then from two differently framed windows. The new
   reading only counts if it fits the sequence there, so a book that genuinely
   skips numbers keeps its own. An announcement heard with *no* readable number
   at all goes down the same road, with the same rule deciding what may be
   believed — which is the only thing that can rescue one sitting past the last
   chapter found, where there's no gap for a later pass to notice. A stretch
   that yields nothing while VAD did
   hear someone speak inside its jingle is read once more from a shorter
   window: Whisper reads audio in 30-second chunks, and a lone announcement
   can drop out of a window that crosses one.
2b. **Mark refinement (skip with `--quick-marks`/`-Q`):** for
   the mark that still lands on the wrong spot — usually a jingle whose
   music briefly fools the voice-activity detector into sounding like speech —
   every mark is double-checked by re-transcribing short, isolated clips of
   the stretch the phrase was heard in, closing in on the announcement a few
   checks at a time until one hears the phrase first; its own
   beginning is then measured to within a tenth of a second and the mark set
   just ahead of it. A mark none of those clips can confirm is searched for once
   more through a heavier `--pass3-model`, where one is named, before it is left
   at its estimated position. Costs a handful of extra transcriptions
   per chapter, which is what `--quick-marks` trades away for speed. Those
   clips are also the best look at the chapter *number* anything in the run
   gets — the same announcement can read as "chapter forty" from a 45-second
   window and "chapter fourteen" from every window under seven — so when they
   agree with each other, disagree with the number in hand, and offer one the
   sequence can hold, the mark is filed under theirs. That costs nothing extra;
   the clips were transcribed either way.
2b-ii. **Sequence reconciliation:** chapter numbers rise through a book, so when
   pass 2 is done and one mark's number contradicts the marks around it, that
   mark gives way — not the rest of the book. Before it's given up, its
   neighbours get a say: between a chapter 13 and a chapter 15 there's exactly
   one number the mark can carry, so it's renumbered outright; where they leave
   several, the audio is read once more and held to that range. Running it here
   rather than during pass 2 is the point — the chapters that settle the
   question are often found long after the misreading.
2c. **Pass 2.5 — a cheap second opinion (only with a heavier `--pass3-model`):**
   most gaps aren't unprobeable audio, just a number the pass-2 model misread
   with the announcement sitting right there in the window. So before pass 3
   commits to transcribing whole regions, the same quick probes are simply run
   again over the gap with the bigger model, which can close it without
   transcribing the region at all. Not a free bet, though: the probing cost
   grows with how many candidate silences the gap holds, so on a dense region it
   can approach the cost of the full transcription it's trying to avoid — and if
   it finds nothing, pass 3 still runs afterward. A gap that survives that gets
   one more try aimed at the other explanation, that nothing ever probed there:
   the pauses just short of `--min-silence-length` are swept a tenth of a second
   at a time, longest first, down to half a second under the setting, stopping as
   soon as the missing chapters turn up — which is what saves a book whose
   narrator's chapter break happens to land right on the limit. Only ever happens
   when `--pass3-model` names a *better* model than pass 2's, so it stays
   something you opt into.
3. **Pass 3 — gap filling (only if needed):** if the chapter numbers found so
   far have sequence gaps, the regions where the missing chapters must be
   hiding are transcribed completely, in chunks whose borders snap to
   silences too (with the transcripts bridged across each seam, so not even
   a phrase interrupted by a pause right at a border can slip through). Pass 3
   can use a different model than pass 2 (`--pass3-model`). A chapter missing
   after the *last* one found leaves no such gap — nothing above it to notice
   its absence — which is what `--trailing-scan` is for.
3b. **Pass 3.5 — the shifted re-read:** a gap that survives being transcribed end
   to end is a misreading, not unread audio — every second of it was read. The
   likeliest misreading is framing: Whisper decodes in 30-second windows, and an
   announcement landing on a window border can drop out of the transcript
   entirely while the sentences either side of it come through perfectly, so the
   text reads as though nothing were missing. Each remaining gap is therefore
   read once more with every decode shifted by half a window, putting whatever
   sat on a border as far from one as it can get. Runs unless `--pass3-model`
   names a *lighter* model than `--model` — the one setting that says outright
   the stragglers aren't worth more time — and always for `--trailing-scan`,
   where asking for the scan says the opposite. If a gap still
   remains after all that, the chapters that *were* found are written and the
   file is renamed
   with a `.missing-marks-…` tag listing the still-missing numbers, rather than
   discarded. Running the tool again over such a file resumes it
   automatically: the committed chapters are trusted as-is, and only the
   still-tagged gap(s) get their own pass 2/pass 3, exactly as after a failed
   `--verify`. The file is renamed back to its original name once every
   chapter is found, or re-tagged with the (possibly shorter) remaining list
   otherwise; `--force` bypasses this and redoes the whole file from scratch.
   More than ten missing chapters are left out of the tag (just
   `.missing-marks`), and such a file is not resumed automatically — a gap that
   wide wants a look by hand first, usually starting with
   `--max-chapter-number`. However it is eventually redone, any run that ends
   with a complete chapter sequence takes the tag back off (and, with `--debug`,
   brings the log beside it along).

`--custom "phrase:Title;..."` adds marks for anything else a book announces —
an interlude, a timeline, a cast list. Such a phrase may be a `/regexp/` (with
`$1`-style group references in the title), and is matched at any point in the
file and as often as it occurs. It must be announced, though, exactly as a
chapter phrase must: a passing mention in the narration gets no mark.

`--ignore-chapter-numbers` keeps chapter detection running but stops it
reasoning about the numbers — no sequence, no gaps, no missing chapters, with
whatever number was spoken still going into the title. For books that restart
their count per part, or announce chapters without numbering them at all.

Alongside the numbered chapters, a "Prologue" and an "Epilogue" mark are
detected the same way (localized by `--lang`, renamed with `--prologue-title`
and `--epilogue-title`, re-worded with `--prologue-phrase` and
`--epilogue-phrase`, switched off with an empty phrase). A prologue only counts
before the first numbered chapter and an epilogue only after it, at most one of
each per file.

A synthetic "Intro" mark (localized by `--lang`, customizable with
`--intro-title`) covers everything before the first detected mark
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
- **A faster pairing worth trying:** `-m small -M turbo` lets the small,
  quick model do the many short probes and keeps `turbo` in reserve for the
  chapters it couldn't resolve. In testing on English and German audiobooks
  this came out meaningfully faster with the same chapter marks at the end —
  worth a `--dry-run` comparison on your own books before adopting it, since
  it does lean on a smaller model for most of the work.
- **Memory:** a model needs somewhat more memory to run than its download size —
  whisper.cpp's own
  [figures](https://github.com/ggml-org/whisper.cpp#memory-usage) are ~852 MB
  for `small`, ~2.1 GB for `medium` and ~3.9 GB for `large`, with the default
  `turbo` landing around 2.2 GB. (Figures quoted for OpenAI's Python
  implementation are two to three times higher; they do not apply here.) It
  comes out of video memory on a GPU backend and out of system RAM on a CPU
  backend, and only ever one copy of it: files are processed one at a time. The
  voice-activity pre-pass wants some of its own on top, growing with
  `--vad-threads`. Nothing is checked up front; if the memory isn't there you'll
  hear it from the model loader, not from ABChapterize. See
  [memory requirements](doc/manual.md#memory-requirements) for the details.
- **Absurd chapter numbers:** if a run reports a chapter number far beyond
  anything the book has (and then a long list of "missing" ones below it),
  Whisper misheard a number — smaller models do this now and then. Cap it with
  `--max-chapter-number` set to roughly the real chapter count.
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
