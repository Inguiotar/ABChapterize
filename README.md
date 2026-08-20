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
- **Jingle-aware probing, by default** — a bundled voice-activity model (Silero
  VAD) finds jingles (music stings) even when they abut speech with no silence
  on either side, which a plain amplitude scan would miss entirely, and Probe
  gives each one a probe of its own aimed at where the announcement behind it
  will be. `--mark-before-jingle` anchors the written mark to
  the jingle/silence itself instead of the default fixed offset before the
  phrase.
- **Self-healing** — when the detected chapter numbers have gaps (e.g. chapter 12
  was announced without a pause before it), the stretch the missing chapter has to
  be in is gone over again: first by listening more closely and at shorter pauses
  than the run was willing to probe, and only then by transcribing it in full.
- **Zero setup for models** — the Whisper model is downloaded automatically on
  first use, and checked against pinned SHA-256 and SHA3-256 digests before
  it is used — a compromised or tampered-with download is rejected rather
  than silently loaded.
- **GPU accelerated** — uses CUDA or Vulkan when available, falls back to CPU.
  On a laptop or desktop with both an integrated and a discrete GPU, the
  discrete one is picked automatically (left alone, the Vulkan runtime may
  well grab the integrated one, which is dramatically slower), and the GPU in
  use is named in the startup line. Override with
  `--use-gpu <name>`; `--list-gpus` shows the names.
- **One file at a time, with the whole machine behind it** — the scan that opens
  every run spreads a single book across all your cores, which takes most of that
  pass off the clock without changing a single thing it finds. Thread counts
  default to your physical core count; `--vad-threads` and `--whisper-threads`
  override them.
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
  automatically in `PATH`, in an `ffmpeg` folder in the current directory, next to
  the exe or in your user profile, in Program Files, or wherever `FFMPEG_DIR` points — at the unpacked
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
(about 465 MB for the default model — one time only, with a progress display;
the 1.6 GB upgrade model follows later, and only if a file actually needs it).
Then the audiobook is scanned and the chapter marks are written:

```
Whisper model "small" loaded (Vulkan backend on NVIDIA GeForce GTX 1070, auto language detection), upgrade model "turbo" (loaded on first use), 1 file(s) to process.
My Audiobook.m4b: 24 mark(s) written (23 chapter(s) 1-23, 1 named), language: en (p=1.00)
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

# Happy with the results? Sweep up the backups, logs and name tags the runs
# left behind (it lists what it would do and waits for a "yes"):
abchapterize --cleanup --recurse "D:\Audiobooks"

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

# Run your own commands around each file - here: normalize it first, then file
# the backup away afterwards. "$99" is the file's whole path, "$1" its name;
# names with spaces are quoted for the shell for you:
abchapterize --recurse --backup \
             --run-before "abnormalize $99" \
             --run-after "mv $99.bak ~/archive/$1" \
             ~/audiobooks

# Files are processed one at a time, each with the whole machine. Leave a few
# cores free for whatever else the machine is doing:
abchapterize --recurse --vad-threads 6 --whisper-threads 6 "D:\Audiobooks"
```

## Options

Run `abchapterize --help` for a quick reference, or see the
[manual](doc/manual.md) for the full story — including exactly
[what is kept and what is stripped](doc/manual.md#5-what-is-kept-and-what-is-stripped)
when chapters are written. Grouped below exactly as `--help` groups them:

| Option | What it does |
| --- | --- |
| `--config <path>` | Read options from a file, one per line, written as you would type them. Anything you also type on the command line wins. |
| (none) | A `.abchapterize-config` in a folder applies to the books in it, and a `.abchapterize-custom` beside it supplies `--custom` mappings. Sub-folders and the command line win over it. |

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
| `-l`, `--lang <code\|auto>` | Language hint for Whisper, or `auto` (the default): each file's language is detected from short samples taken inside the book — up to five, from different places, until one is confident or they can be voted on — and used for that file, falling back to `en` only when they cannot agree. Numbers transcribed as words — cardinal and ordinal, before or after the phrase — are understood in `en`, `de`, `fr`, `es`, `it`, `nl`, `tr`, `pt`, `pl`, `sv`, `da`; digits (`12`, `2nd`, `2e`) and Roman numerals (`XIII`) in every language. Also localizes the defaults of `--chapter-phrase`, `--prologue-phrase`, `--epilogue-phrase`, `--chapter-title`, `--part-title`, `--intro-title`, `--prologue-title` and `--epilogue-title` (per-file with `auto`). |
| `-m`, `--model <name>` | Whisper model used to find the chapters: `tiny`, `base`, `small` (default), `medium`, `turbo`, `large`, or `custom:<path>` for a GGML model file of your own (used as-is: not downloaded, not checksum-verified, ranked against the built-in models by file size). Bigger is not better here — this model listens to short windows a few seconds long, and the large ones are markedly worse at those (see [Tuning tips](#tuning-tips)). `tiny`/`base` are not recommended for real audiobooks either. |
| `-M`, `--upgrade-model <name>` | Whisper model for Scan (gap filling) only; same choices as `--model`, `custom:<path>` included (default: `turbo`, or whatever `--model` says if you set that and not this). Scan transcribes long stretches of audio, where the heavier models really are the better recognizers. Lighter to speed Scan up, or `large` for one last attempt at the gaps. A model heavier than `--model`'s also enables Re-probe — which the default pairing does. Downloaded and loaded lazily, the first time a file actually needs it — Re-probe, Scan, or one of the second opinions Probe asks a heavier recognizer for. |
| `-j`, `--mark-before-jingle` | Walk the mark backward from the default placement, back through the jingle's own music, to the end of the previous chapter's actual narration — or to the start of the last jingle, where several play back to back — instead of the default fixed offset before the phrase (see [How it works](#how-it-works)). Where the walk ends on a pause, the mark backs into it by `-k` seconds; a chapter with no jingle at all keeps its ordinary placement. Best left alongside the default refinement: with `-Q` the walk starts from raw default placement, which occasionally overshoots the announcement and leaves the mark after it. |
| `--jingle-first` | **Experimental.** Read this book's music first and its pauses afterwards, instead of walking both together in one sweep: every jingle in order, then the pauses — but only where the chapter numbering still has a hole, before the first chapter found and after the last. The pauses between two chapters whose numbers already run consecutively are skipped, and nothing else can be announced there. A file with at least one jingle per hour takes this shape by itself unless one of your `--custom` mappings may fall between two chapters; this asks for it regardless. Cannot combine with `--ignore-chapter-numbers`. See [How it works](#how-it-works). |
| `-Q`, `--quick-marks` | **Experimental.** Skip the refinement that normally re-transcribes the audio at every mark to confirm the phrase is really there (see [How it works](#how-it-works)). Faster — saves a handful of transcriptions per chapter — but marks, while usually usable, may end up after the chapter phrase rather than before it, even together with `-j`. |
| `-k`, `--mark-lead <seconds>` | How far in front of the announcement a mark is placed (default 0.35). Purely a matter of taste — marks are located just as precisely whatever this is, it only decides how much lead-in you hear before the narrator starts. Below roughly 0.2 the announcement's opening consonant starts to get clipped; `0` marks the onset itself. Applies with `-j` too: in full where a chapter has no jingle, and as a back-off into the pause before the jingle where there is one (capped at that pause's length). |
| `-n`, `--min-silence-length <s\|auto>` | The shortest pause probed as a potential chapter break (default: `auto`, which starts at 1.5 s). Probing only — Analyze's scan keeps shorter silences regardless, and marks are placed and refined against them. With `auto` (the default), the probing threshold retunes itself as chapters are found at a pause, and any gap left in the numbering is swept for the shorter pauses in between, whether or not the threshold itself ever came down (see [How it works](#how-it-works)); an explicit value probes every silence at or above it instead. `0` switches silence-triggered probing off altogether, leaving only the jingles the voice-activity pre-pass finds — a large saving on a book whose every chapter opens with one, and a way to miss every chapter that does not. The silence scan itself keeps running either way, so marks land exactly where they otherwise would. |
| `--noise-floor <dBFS\|auto>` | How quiet audio has to be to count as a pause — the other half of `--min-silence-length`'s question (default: `auto`). Normally −35 dBFS, which sits comfortably between the room tone and the narration of any ordinary master. `auto` samples each file first and moves the threshold only where −35 would not: a recording with audible hiss never drops below it, so no pause and no chapter is ever found, and a very quietly mastered one puts the narration itself under it, so every gap between two words looks like a chapter break. |

**Phrases & titles**

| Option | What it does |
| --- | --- |
| `-c`, `--chapter-phrase <p>` | What announces a chapter (default: `/(?:^chapter ()\|^() chapter\|^chapter)/`, localized by `--lang`). A list of alternatives separated by `;`, each of them a word, a `/regexp/`, `none` (**experimental** — the number spoken alone, with no phrase at all; only for books really announced that way, since on a book that names its chapters it adds years, dialogue and closing-page numbers as chapters and buys nothing) or `default` (this tool's own phrase). Inside a regexp, `()` stands for a chapter number in any notation the language has and captures it; a leading `^` or trailing `$` asks for a pause before or after the announcement. An alternative may be restricted to one language with a `[xx]` tag, for a mixed batch. The same syntax serves `--prologue-phrase`, `--epilogue-phrase` and `--custom`. See [the manual](doc/manual.md#the-phrase-syntax) for all of it. |
| `-p`, `--prologue-phrase <p>` | Word or `/regexp/` announcing a prologue (default: `/prolog/`, localized by `--lang`). Only accepted before the first numbered chapter, at most once per file; an empty value switches prologue detection off. |
| `-g`, `--epilogue-phrase <p>` | Word or `/regexp/` announcing an epilogue (default: `/epilog/`, localized by `--lang`). Only accepted after the book's last numbered chapter, at most once per file; a match anywhere else is dropped (use `--custom` for a section between chapters). An empty value switches epilogue detection off. |
| `-u`, `--custom <mappings>` | Extra `phrase:title` mappings separated by `;`, e.g. `--custom "zwischenspiel:Zwischenspiel;/zeit[- ]?tafel/:Zeittafel"`. A phrase is a word or a `/regexp/`, parses no number, and is matched at any point in the file as often as it occurs (up to 100 marks per file), but must be announced just as a chapter phrase must. Titles may write out what the phrase captured — `${name}`, `${number}` for the chapter number in digits, `$roman{}`, `$upper{}` and so on. Repeatable; never localized, though a mapping may open with a `[...]` tag holding a `xx` language code and/or hints saying what kind of thing it names — `before-first-chapter`, `after-first-chapter`, `after-last-chapter`, `once`, `max=<n>` — which is the same machinery the built-in prologue and epilogue are. Write `^` at the start of the phrase to require a real pause in front of a match. See the manual. |
| `-U`, `--custom-file <path>` | Read `--custom` mappings from a text file, one per line (blank and `#` lines ignored). |
| `-D`, `--named-mark-distance <seconds>` | How close a named mark (prologue, epilogue, `--custom`) may come to a chapter mark before the two become one entry (default: 10). The chapter keeps its position, the named mark contributes its title in brackets — "Chapter 10 (Interlude)". `0` writes every mark separately. |
| `-t`, `--chapter-title <word>` | Word for generated chapter titles (default: `Chapter`, localized by `--lang`). |
| `--part-title <word>` | Word for the part prefix of a file whose chapter numbering restarts partway through — "Part 2 - Chapter 1" (default: `Part`, localized by `--lang`). A book with a single chapter sequence never uses it; see the manual for how a restart is recognized. |
| `-i`, `--intro-title <word>` | Title for the intro mark before the first detected mark (default: `Intro`, localized by `--lang`). |
| `-P`, `--prologue-title <word>` | Title for the prologue mark (default: `Prologue`, localized by `--lang`). |
| `-G`, `--epilogue-title <word>` | Title for the epilogue mark (default: `Epilogue`, localized by `--lang`). |

**Detection safety nets**

| Option | What it does |
| --- | --- |
| `-a`, `--early-abort <minutes>` | Always on (default: 60; `0` disables it). Give up on a file, unchanged, once this many minutes of play time have been probed with no chapter found — avoids transcribing a whole book that plainly isn't going to yield any. Only applies to a fresh detection run. |
| `-e`, `--expected-start-chapter <n>` | For a split-book part that doesn't start at chapter 1: the number this file is expected to start at. Without it (the default), whatever number Probe finds first is trusted outright and nothing below it is ever searched for — unless a prologue is detected, which implies a start at chapter 1 by itself. With it, a first chapter found *below* `<n>` aborts the file outright, unchanged; a first chapter found *above* `<n>` has Scan search for the missing numbers down to `<n>`, tagging the file `.missing-marks-…` if it still can't find them all. Only applies to a fresh detection run. |
| `--no-trailing-scan` | Skip the transcription of everything after the last chapter found (default: it runs). A missing chapter is normally spotted as a hole in the number sequence, which needs a known chapter on either side of it — so one missing *after* the last chapter found is the one case nothing else notices, and the file would come out looking complete, with nothing reported missing and no `.missing-marks` tag. Closing that hole costs a final chapter's worth of transcription on every file, since there are no expected numbers to satisfy and the scan can never stop early. Turn it off for a library you already know is sound, or give `--chapter-count` instead. Suppressed automatically when `--chapter-count` is given. |
| `--no-denoise` | Do not re-read a garbled announcement through the built-in speech denoiser (default: it may). On a dull-sounding recording the recognizer sometimes writes a chapter's number but loses the word beside it — "1. The Long Road" where the narrator said "Chapter one, The Long Road" — and the chapter is missed with nothing in the output to show for it. Where that happens, and only there, the window is read once more through a denoiser first. Costs one extra pass over the few windows that fail this way, never moves a mark that was already found, and does not run at all on a book whose audio is clear enough not to need it. |
| `--chapter-count <n>` | How many numbered chapters this book has, exactly (default: no expectation). Takes one file, never a folder. The informed version of the trailing scan, and it replaces it: knowing the count, the run knows which numbers are still owed after the last one found, hunts only those, and transcribes nothing at all when the count is already reached. Doubles as `--max-chapter-number` (a higher number is discarded as a mishearing), so the two cannot be combined. Reaching the count does not end the search — an epilogue or a `--custom` phrase may still follow. |
| `-N`, `--max-chapter-number <n>` | Highest chapter number this book plausibly has (default: **200**, counted from `--expected-start-chapter`). A detected chapter numbered above `<n>` is discarded as a mishearing — without a cap, one misheard "chapter 510" in a twelve-chapter book turns everything in between into a gap to hunt for. Raise it for a book that really runs longer; lower it to roughly the real count when you know it. Not to be confused with `--max-chapters` above, which counts *pre-existing* marks. |
| `--ignore-chapter-numbers` | Detect chapter announcements as usual but form no opinion about their numbers: the spoken number still reaches the title, nothing checks the sequence. The Re-probe and Scan passes never run and nothing is ever tagged `.missing-marks`. Cannot combine with `--upgrade-model`, `--expected-start-chapter`, `--max-chapter-number`, `--chapter-count` or `--verify`, nor with `--chapter-phrase none`, which recognizes an announcement by its number being in sequence. |
| `-V`, `--verify` | Check pre-existing chapter marks against the audio instead of trusting them blindly (or requiring `--force`). The check assumes each mark is titled with a chapter number that the narrator announces right there: marks that check out are kept, and only the stretch(es) around a mark that doesn't are redetected. So this is **not** a read-only report. Where the failures outnumber the confirmations, the file is skipped with a warning and left exactly as it was — which is what happens to retailer marks that lump several book chapters into one entry, since those cannot pass the check. Cannot combine with `--force` or `--import`. |
| `--fix` | Requires `--verify`. Lets it correct a mark instead of only reporting on it: a mark whose announcement is confirmed but which sits a little away from it is moved onto it and the file rewritten. Only a nudge — a mark already within a quarter of a second is left alone, and so is one more than 30 seconds out, which is not a mark that drifted but one that means something else. Unconfirmed marks are untouched and still go to the usual gap recovery. |
| `-h`, `--verify-threshold <n>` | Requires `--verify`. Draws the "failed wholesale" line by hand: more than `<n>` failed marks leaves the file untouched with a warning, however many others were confirmed. By default that line sits where the failures begin to outnumber the confirmations. |

**Output & review**

| Option | What it does |
| --- | --- |
| `-d`, `--dry-run` | Detect chapters but write nothing; print what would be written. |
| `-E`, `--export` | Also save detected chapters to a sidecar file (`<file>.chapters.ffmeta`, or `<file>.chapters.txt` with `--simple-metadata`) for manual review or correction. Combinable with `--dry-run`. Written for a file detection completed normally — not for one left with an unresolved gap, a resumed `.missing-marks` file, or a `--verify --fix` rewrite. |
| `-I`, `--import` | Skip Whisper entirely and write chapters from a previously exported sidecar file instead — for reapplying a hand-corrected result. |
| `-S`, `--simple-metadata` | Use a plain `H:MM:SS.fff  Title` sidecar format instead of FFMETADATA for `--export`/`--import`. |

**File & backup management**

| Option | What it does |
| --- | --- |
| `-b`, `--backup` | Keep the original file as `*.bak`. A `*.bak` from an earlier run is kept as it is, never overwritten. |
| `-R`, `--revert` | Restore all `*.bak` backups (undo). |
| `--cleanup` | Housekeeping instead of processing: undo the traces earlier runs left in the folder, printing a line per change. Leftover temporary files, `.debug.log` logs and interrupted runs' progress files are deleted, `.missing-marks-...` name tags are taken off, and `*.bak` backups are deleted — but only where the file they back up is next to them and runs the same length, so it can never throw away the only copy of anything. Add `--revert` to restore the backups instead of deleting them. Shows you the list and waits for a "yes" first. |
| `--yes` | Answer `--cleanup`'s prompt in advance — required for a scripted cleanup, which has no console to be asked at. |
| `-O`, `--no-op` | List every file `--filter` (and `--recurse`) would select, then exit without loading Whisper, invoking ffmpeg or touching any file — a quick way to check a `--filter` regexp or extension list before a real run. Requires `--filter`; combinable only with `--recurse` and the output options. |
| `--run-before <cmd>` | Run a shell command for each file just before it is worked on — and only for a file the run actually works on, so a skipped one runs neither hook. `$1` is the file name, `$0` it without its extension, `$99` its whole path, `$-1` its folder; they are quoted for the shell as needed. A non-zero exit skips the file. Under `--dry-run` the command is printed rather than run. See [the manual](doc/manual.md#running-your-own-commands-around-each-file) for the whole placeholder syntax. |
| `--run-after <cmd>` | Run a shell command for each file once it is finished. Same placeholders and the same rules, plus one more: it does not run for a file left tagged `.missing-marks-...`, which a later run is expected to pick up again. |
| `--ignore-progress` | Start every listed folder over instead of resuming it. While a folder is being processed, the files finished so far are recorded in an `.abchapterize-progress` file inside it, which is deleted again as soon as that folder is done — so an interrupted run resumes by itself when the same command is run again. Progress recorded under different options is discarded automatically, so this is only needed to redo files the very same command already finished. |

**Logging & display**

| Option | What it does |
| --- | --- |
| `-q`, `--quiet` | Suppress the per-file lines; warnings and errors are still shown. |
| `-v`, `--verbose` | Log processing details, each probe/chunk as a `<length>@<time>` header. |
| `-T`, `--verbose-transcripts` | Like `--verbose`, but also dump every Whisper transcript's segments. Implies `--verbose`. |
| `-o`, `--log-file <path>` | Write the log to a file instead of the console — switches logging on by itself (add `-T` for the transcripts). The console keeps its progress bar and result lines, which the file gets too. Appends to an existing file. |
| `-B`, `--no-bar` | No progress bar; per-file results as log lines. |
| `--color <mode>` | Colorize the progress bar, the file name on a per-file result line, and the `--summary` block: `auto` (default), `always` or `never`. Nothing else is ever colored, and a `--log-file` always gets plain text. |
| `-s`, `--summary` | Totals at the end of the run: file counts, times, and confidence, silence/jingle, Whisper-audio and transcription-speed statistics — followed by a list of every file that was skipped and why, of every file no chapters were found in, of every file left with chapter marks still missing, and of every file carrying marks read below a Whisper confidence of 0.50, which are the ones worth checking by hand. |

**Performance**

| Option | What it does |
| --- | --- |
| `-C`, `--cpu-only` | Force Whisper onto the CPU backend instead of the fastest available hardware acceleration. The Silero VAD pre-pass already always runs on CPU regardless of this option, so it only affects Whisper. |
| `--use-gpu <name>` | Run Whisper on the GPU whose name contains `<name>`, case-insensitively — `--use-gpu gtx`, `--use-gpu uhd`. Only needed to override the automatic preference for a single discrete GPU, or to choose between several discrete ones. A request matching no GPU, or more than one, is an error listing the real names. Vulkan only. See [picking a GPU](doc/manual.md#picking-a-gpu-on-a-multi-gpu-machine). |
| `--vad-threads <n\|auto>` | Threads for the voice-activity pre-pass of Analyze (default: `auto` — one per physical CPU core). Each thread holds about 11 minutes of decoded audio while it works, so more of them also means more memory. `1` runs the pre-pass as a single uninterrupted stream. |
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

- **`Analyze`** — ffmpeg finds every pause quieter than
   `--noise-floor` (normally −35 dBFS) in one quick pass, keeping the short ones
   as well as the long — marks are placed and refined against the whole list.
   `--min-silence-length` (default, and starting point with `auto`: 1.5 s) then
   decides which of them are probed.
- **The VAD pre-pass**, part of `Analyze` and always run: a bundled voice-activity model
   ([Silero VAD](https://github.com/snakers4/silero-vad)) scans the whole file
   for speech vs. non-speech. A jingle is music, which reads as non-speech to
   a speech detector just like silence does — so this catches jingles the
   amplitude-only silence scan misses entirely: one that abuts the narration
   with no detectable gap on either side. Every jingle it finds becomes a
   probe candidate of its own; a pause that merely leads into one is then left
   alone, since the jingle hears the same announcement from a better place.
- **`Probe`** — a short stretch of audio is transcribed with Whisper at
   each candidate and matched against the chapter phrase. Where the window opens
   and how far it runs follows from what made the place a candidate: after a
   pause the announcement comes within seconds, behind a jingle it is the first
   speech after the music, and a jingle with a sound *inside* it may be hiding
   the announcement in there, so the window covers the music too. The chapter
   number is parsed from digits, Roman numerals, or numbers written out as
   words (0-999,
   cardinals and ordinals alike), whether it follows the phrase ("Chapter
   Seven") or precedes
   it ("Erstes Kapitel", "2. Kapitel", "Birinci Bölüm"). By default
   (`--min-silence-length auto`), starting from the second mark found (the
   silence before the first mark is usually the intro/title silence, often
   longer than the breaks between chapters, so it's not used to tighten),
   the probing threshold sits at 75% of the *shortest* chapter-break
   silence observed so far — set once, then only ever lowered — so
   shorter in-chapter pauses stop being probed once real inter-chapter
   breaks are established. Only chapters found at a
   pause teach it: the hush in front of a jingle measures the run-up to the
   music rather than the break between two chapters. Where that measurement comes
   out *below* the 1.5 s probing started at — a narrator whose chapter breaks
   are simply shorter than the default assumes — a gap in the numbering reaches
   down to it, and once Probe is done the gaps still open are swept for those
   short pauses as well, down to 0.8 s. That sweep runs off the gap rather than
   off the measurement, so a book of jingles — which measures nothing — gets it
   too. An explicit `--min-silence-length` value disables all of this and probes
   every silence at or above it instead, and nothing below it.
   A sequence gap sends the whole stretch between the two chapters back for a
   second look: every pause and every jingle in it, including the ones the first
   look ruled out and the ones too short to have been probed at all, each read in
   a slightly different framing rather than a wider
   one — and since that retry knows both ends of the
   hole it's filling, only the numbers actually missing from it can be right
   there. A number that can't be right — one leaving
   more than three chapters missing at once, one below the chapters
   already found, or one outside the hole being searched — is read again before
   it's believed: with `--upgrade-model` if
   that's the better model, then from two differently framed windows. The new
   reading only counts if it fits the sequence there, so a book that genuinely
   skips numbers keeps its own. An announcement heard with *no* readable number
   at all goes down the same road, with the same rule deciding what may be
   believed — which is the only thing that can rescue one sitting past the last
   chapter found, where there's no gap for a later pass to notice. A stretch
   that yields nothing while VAD did
   hear someone speak inside its jingle is read once more from a shorter
   window, through a heavier `--upgrade-model` where one is named: Whisper reads
   audio in 30-second chunks, and a lone announcement can drop out of a window
   that crosses one.
- **`J-probe` and `S-probe` (experimental)** — a book that announces every
   chapter after a jingle is probed in two halves instead of one sweep — every
   jingle first, then the pauses, and only those that can still be carrying
   something: anywhere the numbering has a hole, everything before the first
   chapter found, everything after the last. The pauses between two chapters
   whose numbers already run consecutively are skipped, since nothing else can
   be announced there — no number is left for a chapter, and a prologue and an
   epilogue both belong elsewhere. On a book of that shape those pauses number
   in the thousands and can only confirm what the music already said. A file
   takes this shape by itself with at least one jingle per hour, unless a
   `--custom` mapping of yours may fall between two chapters; `--jingle-first`
   asks for it regardless.
- **`SD-probe` (experimental)** — a book with *no* chapter music has the same
   problem from the other side: it announces its chapters after its pauses, and
   it has a hundred pauses that announce nothing for every one that does. So its
   pauses are skimmed once through longest first, purely to find out roughly
   where the chapters are, and `SC-probe` behind it then reads the file in order as
   always but passes over every pause lying between two chapters whose numbers
   already run consecutively. Nothing read during the skim is read again. The
   skim stops as soon as the pauses get too short to be this book's chapter
   breaks, and on a file that announces nothing it gives up having spent no more
   than `--early-abort` would have. It decides only which pauses are worth
   reading — every chapter is still recognised, numbered and placed by the pass
   behind it, reading the file in order.
- **Mark refinement** (skip with `--quick-marks`/`-Q`) — for
   the mark that still lands on the wrong spot — usually a jingle whose
   music briefly fools the voice-activity detector into sounding like speech —
   every mark is double-checked by re-transcribing short, isolated clips of
   the stretch the phrase was heard in, closing in on the announcement a few
   checks at a time until one hears the phrase first; its own
   beginning is then measured to within a tenth of a second and the mark set
   just ahead of it. A mark none of those clips can confirm is searched for once
   more through a heavier `--upgrade-model`, where one is named, before it is left
   at its estimated position. Costs a handful of extra transcriptions
   per chapter, which is what `--quick-marks` trades away for speed. Those
   clips are also the best look at the chapter *number* anything in the run
   gets — the same announcement can read as "chapter forty" from a 45-second
   window and "chapter fourteen" from every window under seven — so when they
   agree with each other, disagree with the number in hand, and offer one the
   sequence can hold, the mark is filed under theirs. That costs nothing extra;
   the clips were transcribed either way.
- **Sequence reconciliation** — chapter numbers rise through a book, so when
   Probe is done and one mark's number contradicts the marks around it, that
   mark gives way — not the rest of the book. Before it's given up, its
   neighbours get a say: between a chapter 13 and a chapter 15 there's exactly
   one number the mark can carry, so it's renumbered outright; where they leave
   several, the audio is read once more and held to that range. Running it here
   rather than during Probe is the point — the chapters that settle the
   question are often found long after the misreading.
- **`Re-probe`** — a cheap second opinion, only with a heavier `--upgrade-model`:
   most gaps aren't unprobeable audio, just a number the probe model misread
   with the announcement sitting right there in the window. So before Scan
   commits to transcribing whole regions, the same quick probes are simply run
   again over the gap with the bigger model, which can close it without
   transcribing the region at all. Not a free bet, though: the probing cost
   grows with how many candidate silences the gap holds, so on a dense region it
   can approach the cost of the full transcription it's trying to avoid — and if
   it finds nothing, Scan still runs afterward. A gap that survives that gets
   one more try aimed at the other explanation, that nothing ever probed there:
   the pauses just short of `--min-silence-length` are swept a tenth of a second
   at a time, longest first, down to half a second under the setting, stopping as
   soon as the missing chapters turn up — which is what saves a book whose
   narrator's chapter break happens to land right on the limit. Only ever happens
   when `--upgrade-model` names a *better* model than Probe's, which by default it
   does: `small` probes, `turbo` gets the second opinion.
- **`Scan`** — only if needed: if the chapter numbers found so
   far have sequence gaps, the regions where the missing chapters must be
   hiding are transcribed completely, in roughly 10-minute chunks. Scan
   can use a different model than Probe (`--upgrade-model`). A chapter missing
   after the *last* one found leaves no such gap — nothing above it to notice
   its absence — which is what the trailing scan below is for.
- **`Re-scan`** — a gap that survives being transcribed end
   to end is a misreading, not unread audio — every second of it was read. The
   likeliest misreading is framing: Whisper decodes in 30-second windows, and an
   announcement landing on a window border can drop out of the transcript
   entirely while the sentences either side of it come through perfectly, so the
   text reads as though nothing were missing. Each remaining gap is therefore
   read once more with every decode shifted by half a window, putting whatever
   sat on a border as far from one as it can get. Runs unless `--upgrade-model`
   names a *lighter* model than `--model` — the one setting that says outright
   the stragglers aren't worth more time. The trailing scan below is read once
   only either way: it already runs on every file, and reading audio nothing
   suspects twice would double that standing cost. If a gap still
   remains after all that, the chapters that *were* found are written and the
   file is renamed
   with a `.missing-marks-…` tag listing the still-missing numbers, rather than
   discarded. Running the tool again over such a file resumes it
   automatically: the committed chapters are trusted as-is, and only the
   still-tagged gap(s) get their own Probe/Scan, exactly as after a failed
   `--verify`. The file is renamed back to its original name once every
   chapter is found, or re-tagged with the (possibly shorter) remaining list
   otherwise; `--force` bypasses this and redoes the whole file from scratch.
   More than ten missing chapters are left out of the tag (just
   `.missing-marks`), and such a file is not resumed automatically — a gap that
   wide wants a look by hand first, usually starting with
   `--max-chapter-number`. However it is eventually redone, any run that ends
   with a complete chapter sequence takes the tag back off (and, with `--debug`,
   brings the log beside it along, replacing the earlier run's).

- **The trailing scan**, the `Scan` pass's last job: everything after the last chapter found is transcribed
   too, on spec, because that is the one place a missing chapter leaves no trace.
   A gap is spotted as a hole in the numbering, and a hole at the very end has
   nothing above it to be a hole *in* — so without this the file is written out
   looking complete. It costs a final chapter's worth of transcription on every
   file and can never stop early, having no expected numbers to satisfy;
   `--no-trailing-scan` buys that time back for a library already known to be
   sound, and `--chapter-count` replaces it with a search that knows what it is
   looking for. Skipped when no chapter was found at all, and after an
   `--early-abort` or `--expected-start-chapter` abort.
`--custom "phrase:Title;..."` adds marks for anything else a book announces —
an interlude, a timeline, a cast list. Such a phrase may be a `/regexp/` whose
capturing groups the title writes out (`${name}`, `${number}`), and is matched
at any point in the file and as often as it occurs. It must be announced, though, exactly as a
chapter phrase must: a passing mention in the narration gets no mark.

`--ignore-chapter-numbers` keeps chapter detection running but stops it
reasoning about the numbers — no sequence, no gaps, no missing chapters, with
whatever number was spoken still going into the title. For books whose numbering
the tool cannot make sense of — an omnibus of several novels, or one that
announces "Chapter" and simply reads on. A book that merely restarts its count in
every part needs no option: that is recognized by itself, and its chapters are
titled "Part 2 - Chapter 1" (see `--part-title`).

Alongside the numbered chapters, a "Prologue" and an "Epilogue" mark are
detected the same way (localized by `--lang`, renamed with `--prologue-title`
and `--epilogue-title`, re-worded with `--prologue-phrase` and
`--epilogue-phrase`, switched off with an empty phrase). A prologue only counts
before the first numbered chapter and an epilogue only after the *last* one, at
most one of each per file. For a section between two chapters — an omnibus whose
inner parts each end with one, say — use `--custom`, which is bound by no
position.

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
- **Jingles:** Probe costs one probe per jingle whatever its length, and every
  window is cut to its own candidate, so a book of long jingles is no slower
  than a book of short ones. On a book whose every chapter opens with music,
  `-n 0` is the real saving: it drops the ordinary pauses as candidates and
  leaves only the jingles.
- **Accuracy vs. speed:** the default pairing is `-m small -M turbo`, and it is
  not the compromise it looks like. The model named by `--model` listens to
  short windows a few seconds long, and the large models are *worse* at those
  than `small` is: they tend to hand back the whole window as one run-on
  sentence with the announcement missing from it, which on one French audiobook
  cost six of twenty-five chapters. `--upgrade-model` handles the long
  transcriptions, where the usual ranking does hold, so `turbo` is kept in
  reserve for the chapters `small` could not resolve — and is loaded only if
  that happens. The small model is also several times faster, so this is not a
  trade at all.
- **When to reach for something heavier:** `-M large` for one last, best-effort
  attempt at a book that keeps losing chapters. Raising `--model` itself is
  worth trying on a book whose narrator is hard to make out, but check the result
  rather than assuming it improved. Going *below* `small` is not advisable for
  real audiobooks: detection stands or falls with catching a single short phrase,
  and `tiny` in particular mishears or drops chapter announcements so often
  that it is supported mostly for completeness (quick experiments, toy examples).
- **Memory:** a model needs somewhat more memory to run than its download size —
  whisper.cpp's own
  [figures](https://github.com/ggml-org/whisper.cpp#memory-usage) are ~852 MB
  for the default `small`, ~2.1 GB for `medium` and ~3.9 GB for `large`, with
  `turbo` landing around 2.2 GB (loaded alongside it only once Re-probe or Scan
  actually runs). (Figures quoted for OpenAI's Python
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
  slashes, e.g. `-c "/part ()/"`, where `()` stands for the chapter number in
  any notation the language has. Several alternatives may be given at once, and
  a `^` or `$` asks for a pause around the announcement — see
  [the manual](doc/manual.md#the-phrase-syntax).
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
(MIT, Copyright (c) 2020-present Silero Team), and the bundled speech denoiser is
[LavaSR](https://github.com/ysharma3501/LavaSR)'s (Apache-2.0) in the ONNX
conversion by [LavaSR-ONNX](https://github.com/Topping1/LavaSR-ONNX). Both of
those run on [ONNX Runtime](https://github.com/microsoft/onnxruntime) (MIT,
Copyright (c) Microsoft Corporation) — see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the full notices.
ffmpeg is used as an external program and is not part of this project.
