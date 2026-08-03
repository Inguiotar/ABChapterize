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
   ([picking a GPU](#picking-a-gpu-on-a-multi-gpu-machine))
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
chapter marks are skipped unless `--force`, `--max-chapters` or `--verify`
says otherwise.

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

`.m4b` is the recommended format. `.m4a` and `.m4b` are identical containers —
the extension is purely a naming convention (`.m4b` for audiobooks/podcasts,
`.m4a` for other audio) — but players may choose their exact behavior (e.g.
remembering playback position) based on which one it actually sees, so it is
worth naming audiobooks `.m4b`. `.mp3` and `.opus` chapter support, while
written correctly by ABChapterize, is honored by comparatively few players —
expect inconsistent or missing chapter navigation with those two formats.

Notably absent:

- **`.ogg` (Vorbis) and `.flac`** — ffmpeg's muxers for these containers
  silently drop chapter marks, so writing them is impossible with ffmpeg as
  the backend. Files with these extensions are not processed.
- Everything that is not an audio container (video files etc.).

Files with unsupported extensions are simply skipped during directory scans;
naming one directly as the target is an error.

## 3. How detection works

Detection runs in up to three Whisper-transcription passes per file, plus a
voice-activity (VAD) pre-pass that runs by default (skipped only with
`--max-jingle-length 0` and no `--mark-before-jingle`). This section is an
overview of what each pass does; the machinery that keeps it accurate and
fast — how probe windows are sized and stitched together word-safely, how
each mark is pinpointed to its exact position, the transcript caching and the
self-tuning that cut the number of Whisper calls — is documented in the
source. Only what affects using the tool is covered here.

### Pass 1 — silence scan (and VAD pre-pass)

ffmpeg's `silencedetect` filter finds every silence of at least
`--min-silence-length` seconds (default, and floor with `auto`: 1.5) below
−35 dBFS, in one quick decode pass over the whole file. Chapter announcements
in audiobooks practically always follow such a pause. If the scan ends
prematurely (e.g. because of a damaged file), the file is aborted with an
error instead of silently reporting "no chapters".

`silencedetect` is amplitude-only: a jingle (a short music sting) that abuts
the narration with no detectable gap around it produces no silence at all, so
it never gives pass 2 a candidate near that transition. By default, a
voice-activity detection pre-pass runs over the same decode using a bundled
model — [Silero VAD](https://github.com/snakers4/silero-vad), MIT-licensed,
embedded in the executable (~2.2 MB, no separate download; see
[THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)). Music reads as
non-speech to a speech detector, the same as silence, so a jingle shows up as
a non-speech region flanked by speech even when there is no amplitude gap
around it, and pass 2 gets a candidate at every such jingle that has no
leading silence of its own. This pre-pass is skipped only when
`--max-jingle-length 0` says no jingle is expected at all and
`--mark-before-jingle` is not given either — reproducing this tool's
original, pre-jingle-detection behavior exactly (see the `-X` and `-j`
references below).

### Pass 2 — probing

A short window of audio is transcribed with Whisper at the start of the file,
after the end of every detected silence, and — whenever the VAD pre-pass ran
(the default; see [Pass 1](#pass-1--silence-scan-and-vad-pre-pass)) — at every
silence-less jingle it found too. The window is `--max-jingle-length` + 5
seconds (up to 50 seconds, with the 45 s ceiling, before it self-tightens —
see below), or a fixed 12 seconds when
`--max-jingle-length 0` says no jingle is expected. Each transcript is matched
against the chapter phrase (see `--chapter-phrase`), and the chapter number is
parsed from digits, Roman numerals or number words
(see [section 7](#7-languages-and-number-recognition)).

If the phrase is heard but nothing following it can be read as a number,
`--verbose` reports it and quotes what was transcribed there, and the spot is
read again: with `--pass3-model` first if that names a better model than the
probing one, then from two differently framed windows — the wording a
recognizer produces at a given place depends on where the window around it
begins, so a second framing often reads cleanly. A number found that way is
believed only if it continues the chapter sequence, so a book's own mentions of
the word "chapter" cannot turn into a mark; the recovered chapter is marked
exactly where it was first heard. Only a stretch that yielded no chapter at all
is treated this way, so those mentions stay out of the log as well.

A window that yields nothing at all gets a second look when the VAD pre-pass
contradicts it — that is, when someone was heard speaking inside the jingle the
window covered and the transcript has no words for that spot. Whisper reads
audio in 30-second chunks, and a lone word inside a jingle can drop out of a
window that crosses one while being transcribed cleanly from a shorter window
over the same audio. The spot is therefore read once more from a window short
enough to stay inside a single chunk — through the model `--pass3-model` names
where that is a better one than `--model`, since an announcement quiet enough to
be dropped is also one a larger model is likelier to recover — and `--verbose`
says so, naming the recognizer it used. This needs the
pre-pass, so `--max-jingle-length 0` never does it.

A number that *is* read but cannot plausibly belong where it was heard — one
that would leave more than three chapters missing in one go, or one below the
chapters already found — is not taken at face value either. It is read again:
with `--pass3-model` first if that names a better model than the probing one,
then from two differently framed windows around the announcement. The new
reading is adopted only if it fits the sequence at that point, so a book that
genuinely skips numbers keeps its own numbering; where nothing sensible can be
read, the original number stands (and, if it was below the sequence, is skipped
as before). The mark itself never moves — only the number changes.
`--verbose` reports every attempt and its outcome.

Inside a gap search — a re-reading of the stretch between two chapters already
found, looking for the ones missing between them — "fits the sequence" is much
narrower, because both ends of the hole are known. Only the numbers actually
missing from that stretch can be right there, so anything else is questioned and
re-read as above, and a number at or beyond the chapter that closes the gap is
refused outright rather than displacing it.

Mark placement supplies one more reading of the number, at no extra cost. Unless
`--quick-marks` is in force, pinning down where an announcement begins
(see below) means transcribing it several times over in short windows framed on
the announcement itself, and those windows read a spoken number more reliably
than the long one that found the chapter — the same audio can come back as
"chapter forty" from a 45-second window and "chapter fourteen" from every window
under seven. When those readings agree clearly with one another, disagree with
the number in hand, and offer one that fits the sequence, the mark is recorded
under theirs. `--verbose` reports the correction.

Where a chapter announcement is found, the mark is placed a fixed lead-in
before it — 0.35 seconds by default, `--mark-lead` — no matter what precedes
it: a silence, a jingle, or nothing at all. When a
jingle precedes the announcement, Whisper's own timestamp for it is not always
trusted outright: if the voice-activity pre-pass shows the announcement's own
opening syllable was brief enough to be folded into the jingle's non-speech
stretch, the mark is placed there instead — VAD's boundaries pinpoint it more
reliably than Whisper's own timing in that case. Failing that, if the
timestamp still comes back smeared to before the jingle even starts, the mark
is floored at the jingle's own end instead of landing early, back in the
previous chapter's narration.

One more layer sits on top of that by default, for the case where even this
still lands on the wrong spot — typically a
jingle whose own music briefly resembles speech closely enough to fool the
voice-activity detector, in either direction: short of the true announcement
or generously past it. Every mark placed by default-mode probing — including
the starting point `--mark-before-jingle` would otherwise walk backward
from — is double-checked against the audio itself, by transcribing short,
isolated clips of it: the stretch the chapter phrase was heard in is searched,
closing in on the announcement a few checks at a time rather than combing
through it, and the announcement's own beginning is then
measured to within a tenth of a second. The mark is set `--mark-lead` seconds
ahead of it. Hearing the phrase at the mark is not by itself taken as proof that the
mark is right — a jingle is not transcribed at all, so a mark several seconds
inside one hears the announcement just as clearly as a mark sitting on it. A
mark that cannot be confirmed this way is searched for once more, in full, through
the model `--pass3-model` names where that is a better one than `--model` — a
quietly-spoken announcement inside a jingle can be lost on the smaller model and
plain to the larger. Only when neither hears it is the mark left as originally
placed rather than guessed at. Finally, whatever mark results —
confirmed, corrected, or left as is — is nudged up to 0.15 seconds earlier to
the quietest point in that stretch, but only when doing so is a clear (at
least 6 dB) improvement over the mark's own position; a mark is never moved
later. This keeps a player from starting playback abruptly mid-sound (an
audible "plop") without ever risking eating into the announcement itself.
This costs a handful of extra Whisper transcriptions per chapter on top of pass
2's own probe — a mark that already sits close to its announcement is the
cheapest case, one left seconds away from it the most expensive. `--quick-marks`/`-Q` skips the whole layer when that time matters more
than the last few tenths of a second of accuracy (the machinery is documented
in the source).

`--mark-before-jingle` anchors the mark to the end of the
previous chapter's actual narration instead, by walking backward from
whatever mark default mode (normally already refined as described above)
found: first out of any silence
the mark sits in, then — if no real speech is heard right there — back
through the jingle's own music, until real narration is found and the mark is
placed there; a mark with real narration already right before it is left
unchanged. Where two jingles play back to back — the previous chapter's outro
sting followed by this chapter's own, with an audible break between them —
the walk stops at that break, so the mark lands at the start of the second
jingle rather than in front of the first. When a jingle opens the file with
nothing spoken before it at all, the mark instead backs off by a small fixed
margin. In the rare case that the refinement above could not confirm the
announcement — so the walk started from an unverified position — the walked
result is itself re-transcribed afterward and corrected further back if the
announcement is still audible there. The result is then nudged earlier to a
nearby, clearly quieter point in the same way (see above) (the
machinery for all of this is documented in the source).
In-text mentions ("…as we learned in chapter three…") are rejected by requiring the
announcement to follow a real pause; out-of-order detections and duplicates of
an already-marked chapter are dropped, keeping the earliest position. Each
mark also carries Whisper's own confidence, and marks below 0.5 are flagged
for a spot-check rather than trusted silently (see
[section 12](#12-output-progress-and-logging)).

By default (`--min-silence-length auto`), pass 2 does not probe every silence
from pass 1. As chapters are found it learns how long this book's real
inter-chapter breaks are and stops probing clearly shorter in-chapter pauses,
so far fewer Whisper probes are needed without a fixed guess; should a chapter
later turn up out of sequence, everything skipped since the previous chapter
is re-probed before pass 3 has to step in. Giving `--min-silence-length` an
explicit numeric value disables this and probes every silence at or above it.
See the [`-n` reference](#detection-behaviour) for the knob itself.

By default (`--max-jingle-length auto`), the jingle probe window self-tightens
the same way. Starting from the second jingle mark found (the first is
excluded — the gap before it isn't necessarily representative), the window
resizes to 1.25x the longest jingle actually observed so far plus the
5-second phrase margin, capped at the 45 s ceiling — narrowing once a book's
real jingle length is known, and widening again if a longer one turns up.
Giving `--max-jingle-length` an explicit numeric value (including `0`)
disables this and keeps the window fixed at that value throughout. See the
[`-X` reference](#detection-behaviour) for the knob itself.

A chapter turning up out of sequence puts every candidate since the previous
chapter back in question, not just the ones that were passed over: a window
that was probed while the jingle window sat narrow can end before an unusually
late announcement, so those are re-probed at the full ceiling width too. The
retry stops as soon as the gap is closed. When there is nothing to retry —
every candidate already had the full window and simply yielded no readable
announcement — `--verbose` says so, and the gap goes straight to pass 3.

A chapter recovered that way also reports how far into its window its
announcement ended, and the window widens to cover at least that much for the
rest of the file. This is the one case where the plain distance from the
candidate is trusted rather than the jingle length: the candidate is vouched
for by a chapter nothing else in the run found. It matters for books whose real
chapter breaks are too short to be probed on their own, where the window has to
reach the announcement from the previous candidate — without it, the same shape
of chapter is missed again a few chapters later. One recovery can widen the
window by no more than a quarter of its current width, though: a wide window
makes probes overlap and so costs time on every remaining candidate, and a
single unusual chapter should not decide that for the rest of a long book. A
reach beyond that is granted over several recoveries.

When pass 2 is done, its finds are reconciled into one ascending sequence, since
chapter numbers rise through a book. Where a mark's number contradicts the marks
around it, that mark gives way rather than the rest of the book — a single
mishearing can cost its own mark but never the chapters behind it. And before it
is given up, the surrounding chapters get a say in what it should have been:
between a chapter 13 and a chapter 15 there is exactly one number the mark can
carry, so it is simply renumbered; where the neighbours leave several
possibilities, the audio is read again and held to that range. This is why the
step waits until pass 2 has finished — the chapters that settle the question are
often found long after the misreading. It also runs before the tool works out
which chapters are missing, so the passes below never go hunting for a chapter
that was never missing. `--verbose` reports each repair.

### Pass 2.5 — cheap gap re-probe (only with a heavier `--pass3-model`)

A gap is often not a chapter the probing missed, but a number the pass-2 model
misheard while probing the right spot. So when — and only when —
`--pass3-model` names a *better* model than pass 2's, the gap's regions are
first re-probed exactly as pass 2 probes, but with that better model. When it
finds the missing chapters, pass 3 never has to run for them at all; anything it
does not find falls through to pass 3 immediately afterward.

A gap that survives the re-probe gets one more, differently aimed attempt first.
The other reason an announcement goes unfound is that nothing ever looked at it:
`--min-silence-length` decides which pauses are worth probing, and a narrator
whose chapter break lands just under the setting has every chapter of the book
below it. So the gap is swept for the pauses just short of that setting — a tenth
of a second at a time, longest first, down to half a second under it — and the
sweep stops the moment the missing chapters are accounted for. Where the gap is
long enough that this would end up costing more than transcribing it outright,
the sweep is abandoned and pass 3 takes over. `--verbose` reports each band it
sweeps.

Whether pass 2.5 pays off depends on the gap: the re-probe's cost grows with the
number of candidate silences inside it, not with its length, so a region dense
in candidates can spend about as long probing as pass 3 would have spent
transcribing it outright — and then pass 3 still follows. Expect it to help most
where a gap is long but quiet. With an equal or lighter `--pass3-model` (the
default is equal), this step does not run at all. Marks are placed exactly as in
pass 2.

### Pass 3 — gap filling (only when needed)

If the detected chapter numbers have sequence gaps (…7, 9…), the regions
where the missing chapters must be hiding are transcribed *completely*, in
roughly 10-minute chunks. This catches announcements that were not preceded
by a long-enough silence. Marks found here are placed the same way as in
pass 2. If a chunk still leaves an expected chapter unaccounted for, a
stored silence (or, when the VAD pre-pass ran, a VAD non-speech region)
inside it that the chunk's own transcript skipped over entirely gets a
second, closer look before the chapter is given up as missing — documented
in the source.

A first detected chapter numbered above 1 is, by default, trusted outright:
there is no way to tell a legitimate split-book start from a spot pass 2
simply missed, so guessing "1" and searching for it is never attempted (the
intro chapter covers the leading audio either way — see below).
`--expected-start-chapter` opts a file into that search instead, down to a
specific expected number rather than a blind guess — see
[Detection behaviour](#detection-behaviour) — with the same 10-second grace
period: a first chapter found within 10 seconds of the file start is still
taken as-is, not searched past.

A chapter missing *after* the last one found is the one case none of this can
notice: a gap is a hole in the number sequence, which needs a known chapter on
either side of it, and there is nothing above the last one to compare against.
`--trailing-scan` transcribes that stretch anyway — from the last chapter found
through to the end of the file — at the price of doing so on every file, every
run, whether or not anything is wrong. See
[Detection behaviour](#detection-behaviour).

### Pass 3.5 — the shifted re-read

A gap that survives being transcribed end to end is a different problem from a
gap nothing ever looked at: every second of it *was* read, so what is left to
explain is a misreading. The likeliest one by far is a matter of framing —
Whisper decodes in 30-second windows, and an announcement landing right on a
window border can drop out of the transcript altogether while the sentences on
either side of it come through perfectly, leaving text that reads as though
nothing were missing.

So each still-open gap — or, where pass 3 closed part of one, each remaining
piece of it — is read once more with every decode shifted by 15 seconds, half a
window, which puts whatever sat on a border as far from one as it can get.
This runs unless `--pass3-model` names a *lighter* model than `--model`: that is
the one setting which unambiguously says the stragglers are not worth more time.
For `--trailing-scan` it always runs, since asking for that scan is itself the
statement that they are.

If a gap *between* detected chapters (or, with `--expected-start-chapter`,
before the first one) still remains after pass 3, the chapters that *were*
found are still written, but a warning is printed and the file is
**renamed** to `<name>.missing-marks-<n>-<n>-…<ext>` — the tag listing the
still-missing chapter numbers, `-`-delimited (e.g.
`My Book.missing-marks-3-7.m4b`). This flags the file for attention and
preserves the partial work instead of discarding it, rather than committing a
silently-complete-looking but partially-wrong chapter list.

More than ten missing chapters are not spelled out — the tag is just
`<name>.missing-marks<ext>`, since the full list would make for an unwieldy
(and possibly over-long) file name. Such a file is also *not* picked up
automatically by a later run: a gap that wide usually means detection went
off the rails somewhere, which is worth a look before handing the file back
to another automatic attempt. See
[`--max-chapter-number`](#handling-of-pre-existing-chapters) for the most
common cause.

A later run over a *numbered* tagged file picks it up automatically (unless
`--force` is given): the chapters already committed are trusted outright,
and only the still-tagged gap(s) get their own pass 2 and, if needed, pass 3,
exactly as after a failed `--verify` (see
[`--verify`](#handling-of-pre-existing-chapters)). If that completes the
sequence, the file is renamed back to its original name; if a gap is still
unresolved, it is re-tagged with the (possibly shorter) remaining list.
`--force` bypasses this and redoes the file from scratch instead, discarding
every existing marking including the partial ones.

An unnumbered tagged file is never picked up automatically, so redoing it means
`--force` (or `--verify`, or a `--max-chapters` low enough to condemn its
partial markings). Whichever way it happens, *any* run that ends with a
complete chapter sequence takes the tag off again and gives the file its own
name back — the tag records work still to be done, and there is none left.
With `--debug`, the log written beside the file follows it to that name;
if a log is already sitting there from the run that left the tag, the new one
is appended to it rather than replacing it.

### Prologue and epilogue

Alongside the numbered chapters, every run also listens for a prologue and an
epilogue announcement (default phrases `/prolog/` / `/epilog/`, localized by
`--lang`) and gives each its own mark, titled `--prologue-title` /
`--epilogue-title` rather than "Chapter *n*".

Neither takes part in the chapter number sequence: a prologue between the
intro and chapter 1 does not make chapter 1 look like chapter 2, and no gap
hunt, `--verify` check or ".missing-marks" tag ever concerns itself with
them. At most one of each is written per file.

Because "prologue" and "epilogue" are ordinary words that also occur in
ordinary prose, each is only accepted where it can plausibly be an
announcement: the prologue only *before* the first chapter has been found,
the epilogue only *after* at least one has. Within that window the last
occurrence wins — front matter frequently lists what is coming ("read by …,
contains a prologue") before the narrator actually announces it.

Both are switched off by passing an empty phrase, e.g.
`--prologue-phrase "" --epilogue-phrase ""`.

### Custom marks

`--custom` adds phrases of your own, each with the title its mark is written
under:

```
abchapterize --custom "zwischenspiel:Zwischenspiel;/zeit[- ]?tafel/:Zeittafel" book.m4b
```

A phrase is a plain word or a `/regexp/`, exactly as for `--chapter-phrase`,
and no number is parsed or expected. Unlike the prologue and epilogue, a
custom phrase is accepted at **any point** in the file and **as often as it
occurs** — a book with an interlude between every chapter gets a mark for each
of them.

What it is *not* is a full-text search: a custom phrase has to be **announced**,
and is held to exactly the same standard as a chapter phrase for deciding
whether it was. A narrator mentioning a timeline in passing gets no mark; the
narrator announcing "Zeittafel" after a pause does. Titles may pull text out of the phrase's own capturing groups with
`$1`, `$2` or a group name; write `$$` for a literal dollar sign.

Syntax notes:

- Mappings are separated by `;`, phrase and title by `:`.
- Only the *first* `:` separates, so a title may contain further ones
  ("`time:Time: an interlude`"). A `/regexp/` phrase instead ends at its
  closing slash, so a colon inside it is just a colon.
- Write `\;` for a semicolon inside a regexp.
- `--custom` may be repeated; every use adds to the list.
- `--custom-file <path>` reads mappings from a text file, one per line, with
  blank lines and `#` comment lines ignored. Semicolons need no escaping
  there, since line breaks separate the mappings.
- Custom phrases are never localized by `--lang` — they are taken exactly as
  written.

At most 100 custom marks are written per file. Beyond that the rest are
dropped and the file's summary line says so: a phrase that matches ordinary
prose (`--custom "the:the"`) would otherwise pepper a whole book with marks.

### Detecting chapters without believing their numbers

`--ignore-chapter-numbers` leaves detection working exactly as it normally
does — the chapter phrase is still searched for, every announcement still
becomes a mark, and the marks are still placed with the same silence, jingle
and refinement machinery — but the tool stops forming any opinion about the
numbers it hears.

Whatever number was spoken still ends up in the title (`Chapter 7`), and an
announcement with no number at all is marked too, titled with the bare word
(`Chapter`). Nothing checks that the numbers ascend, that none is missing, or
that the book starts at 1. Consequently no sequence gap is ever found or
filled, so [Pass 2.5](#pass-25--cheap-gap-re-probe-only-with-a-heavier---pass3-model),
[Pass 3](#pass-3--gap-filling-only-when-needed) and
[Pass 3.5](#pass-35--the-shifted-re-read) never run and no file is
ever tagged `.missing-marks`. A run finishes after Pass 2, which usually makes
it a good deal quicker than a normal one.

Use it for books whose numbering the tool cannot make sense of: one that
restarts its count in every part, one made of several novels bound together,
or one that announces "Chapter" and then simply reads on.

The prologue and epilogue keep their usual positional rules — the prologue
before the first chapter heard, the epilogue after it — and `--custom` marks
behave as always. The per-file limit of 100 custom marks does not apply to
chapter announcements.

The options that reason in chapter numbers are rejected rather than silently
ignored: `--pass3-model`, `--expected-start-chapter`, `--max-chapter-number`,
`--trailing-scan` and `--verify`. `--chapter-phrase` and `--title` remain
perfectly useful and are accepted.

### The intro chapter

Audiobooks usually do not start with chapter one — there is a title
announcement, credits, a prologue. Many players (and the MP4 muxer itself)
force the first chapter mark to 0:00, which would move "Chapter 1" to the
very beginning and misplace it.

Therefore, when the first detected mark (chapter or prologue) starts later
than 0:00, a synthetic intro chapter (default title: "Intro", localized by
`--lang`; customizable with `--intro-title`) is prepended at 0:00, and the
first real mark keeps its exact detected position — *unless* nothing precedes
that first announcement but silence, music or a jingle: with no actual
spoken prelude to give its own entry, no intro chapter is inserted, and the
muxer's own start-snapping folds that lead-in into the first real mark
instead, however many minutes long it runs.

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
excluded from directory scans. If a power failure ever leaves one behind,
check which of the two kinds it is before deleting anything:

- `<name>.<ext>.abchapterize.tmp<ext>` is the half-written replacement. The
  audiobook next to it is untouched, so this one can simply be deleted.
- `<name>.<ext>.abchapterize.orig` **is your original**, parked for the moment
  it takes the finished file to move into its place (step 4 above, without
  `--backup`). If `<name>.<ext>` is missing, rename this one back to it; if the
  audiobook is sitting there complete, the parked copy has done its job and can
  go. Either way, look before deleting.

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
abchapterize [options] <file-or-directory>...
abchapterize -R|--revert [--recurse] [--filter <f>] <file-or-directory>...
abchapterize -O|--no-op --filter <f> [--recurse] <file-or-directory>...
abchapterize --help | -?
abchapterize --version
abchapterize --list-gpus
```

Options must precede the file/directory arguments, which must come last.
Short options that take no parameter may be collapsed: `-rb` = `-r -b`.
Short options that take a parameter — every one shown with a `<value>`
placeholder in the reference below, e.g. `-l <code|auto>` — cannot be
collapsed with others; each needs its own `-x value`.

Options taking a decimal number accept either separator — `-n 2.5` and
`-n 2,5` are the same thing — so you can type whatever your keyboard and
habits produce. Numbers the tool *prints* always use `.`, on every machine,
so that logs and reports stay comparable regardless of regional settings.

### Target selection

`<file-or-directory>...` (required, last arguments)
: One or more audio files and/or directories, mixed freely — a directory
  contributes its supported audio files, a file is processed on its own.
  Naming a file with an unsupported extension is an error. Targets are
  processed in the order given, and nothing is processed twice: a path listed
  again, or a file a listed directory already covers, is silently dropped the
  second time around. Within a directory, files are processed in natural order:
  like an alphabetical listing, except that runs of digits count as whole
  numbers, so "Track 2.mp3" comes before "Track 10.mp3".

`-r`, `--recurse`
: Descend into subdirectories. Requires at least one directory target.

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
  `--chapter-phrase`, `--prologue-phrase`, `--epilogue-phrase`, `--title`,
  `--intro-title`, `--prologue-title` and `--epilogue-title` (per file with
  `auto`).
  `abchapterize --lang de buch.m4b` finds "Kapitel eins" and writes
  "Kapitel 1" without further options; so does plain `abchapterize buch.m4b`,
  via auto-detection.

`-c`, `--chapter-phrase <p>`
: The word or phrase that announces a chapter (default: `/chapter/`,
  localized by `--lang` — see [section 7](#7-languages-and-number-recognition)
  for every language's default). Matching is always case-insensitive. Two
  forms:

  - A literal word/phrase: `--chapter-phrase Teil`. The chapter number is
    expected directly after it ("Teil sieben") or, failing that, directly
    before it ("Siebter Teil") — see section 7.
  - A regular expression between slashes: `--chapter-phrase "/part (\d+)/"`.
    If the regexp contains a capturing group, the group must capture the
    chapter number as digits; without a group, the number is parsed from the
    surrounding words as with a literal phrase.

  For a batch run over books in more than one language, the value may also be
  written **per language**: entries separated by `;`, each opened by a `[xx]`
  language tag.

  ```
  --chapter-phrase "[fr]/(?:premi|1).re partie.? chapitre/;[en]section"
  --title          "[fr]Chapitre;[en]Section"
  --custom         "[fr]/scène/:Scène;[en]/scene/:Scene"
  ```

  With `--lang auto` each file resolves its own language and takes that
  language's entry. One entry may be left untagged, which makes it the
  fallback for the languages the value does not name; without one, those
  languages keep their own built-in default, exactly as if the option had not
  been given. A value carrying no tag anywhere is taken whole, semicolons
  included, so a phrase written for an earlier version still means what it
  did; a semicolon inside a tagged entry is written `\;`. The same syntax
  works for `--title`, `--intro-title`, `--prologue-phrase`,
  `--prologue-title`, `--epilogue-phrase`, `--epilogue-title` and `--custom`.

`-p`, `--prologue-phrase <p>`
: The word or phrase that announces a prologue (default: `/prolog/`,
  localized by `--lang`). Takes the same literal and `/regexp/` forms as
  `--chapter-phrase`, but no number is parsed or expected. Only accepted
  before the first chapter has been found; see
  [Prologue and epilogue](#prologue-and-epilogue). An empty string switches
  prologue detection off.

`-g`, `--epilogue-phrase <p>`
: The same for the epilogue (default: `/epilog/`, localized by `--lang`),
  only accepted once at least one chapter has been found. An empty string
  switches epilogue detection off.

`-u`, `--custom <mappings>`
: Extra `phrase:title` mappings, separated by `;`, e.g.
  `--custom "zwischenspiel:Zwischenspiel;/zeit[- ]?tafel/:Zeittafel"`. A
  phrase is a word or a `/regexp/` and parses no number; a match anywhere in
  the file becomes a mark titled after the colon, as often as the phrase
  occurs. Titles may reference the phrase's capturing groups as `$1`, `$2` or
  by name. Repeat the option to add further mappings. Never localized — but a
  mapping may open with a `[xx]` language tag, which restricts it to files that
  resolve to that language; untagged mappings apply to every file. See
  [Custom marks](#custom-marks) for the full syntax and the per-file limit.

`-U`, `--custom-file <path>`
: Read `--custom` mappings from a text file, one per line; blank lines and
  lines starting with `#` are ignored.

`--ignore-chapter-numbers`
: Detect chapter announcements as usual, but form no opinion about the numbers
  in them: no sequence, no gaps, no missing chapters. The spoken number still
  reaches the title. Cannot be combined with `--pass3-model`,
  `--expected-start-chapter`, `--max-chapter-number`, `--trailing-scan` or
  `--verify`. See
  [Detecting chapters without believing their numbers](#detecting-chapters-without-believing-their-numbers).

`-m`, `--model <name>`
: Whisper model: `tiny`, `base`, `small`, `medium`, `turbo` (default) or
  `large`. `tiny` and `base` are not recommended for real audiobooks; see
  [section 8](#8-whisper-models). `custom:<path>` uses a GGML model file of
  your own instead — see [Using your own model](#using-your-own-model).

`-M`, `--pass3-model <name>`
: Whisper model to use for [pass 3](#pass-3--gap-filling-only-when-needed)
  (gap filling) only; same choices as `--model` including `custom:<path>`, and
  defaulting to whatever `--model` is. Use a lighter model to make pass 3
  faster (when you expect to fix any stragglers by hand anyway), or `large` for
  one last, best-effort attempt at the chapters the main model missed. Naming a
  *bigger* model here than `--model`'s also enables
  [pass 2.5](#pass-25--cheap-gap-re-probe-only-with-a-heavier---pass3-model),
  which often closes the gap far quicker than pass 3 would, and lets
  [pass 2](#pass-2--probing) ask it for a second reading of a chapter number
  that cannot be right, for a second look at an announcement its own window
  lost, and for a second attempt at any mark it could not pin
  down. Naming a *lighter* one is read as "don't spend more time
  on the stragglers" and is the one thing that switches
  [pass 3.5](#pass-35--the-shifted-re-read) off; leaving it alone or naming a
  heavier model both keep it. The pass-3 model is downloaded and loaded lazily — only
  if and when a file actually needs it — so naming a model here costs nothing on
  a clean run.

`-C`, `--cpu-only`
: Forces Whisper onto the CPU backend instead of the fastest available
  hardware acceleration (CUDA, then Vulkan, then CPU — see
  [section 9](#9-gpu-acceleration)). The Silero VAD pre-pass already always
  runs on CPU regardless of this option, so it only affects Whisper. Useful
  to leave a GPU free for other work, or to sidestep a flaky/unsupported GPU
  backend.

`--use-gpu <name>`
: Run Whisper on the GPU whose name contains `<name>`, matched
  case-insensitively against any part of it — `--use-gpu gtx`, `--use-gpu uhd`.
  A single discrete GPU is preferred automatically, so this is only needed to
  force the integrated card, or to choose between several discrete ones. A
  request matching no GPU, or more than one, stops the run and lists what is
  available. A bare number is taken as a device index if one exists, for the
  machine with two identical cards. Vulkan only, and not combinable with
  `--cpu-only`. See
  [Picking a GPU on a multi-GPU machine](#picking-a-gpu-on-a-multi-gpu-machine).

`-n`, `--min-silence-length <seconds|auto>`
: Minimum silence duration (0.1–60, default: `auto`) that counts as a
  potential chapter break; the silence scan always uses this as its floor
  (1.5 by default, and as `auto`'s floor). By default (`auto`), pass 2
  self-tightens the probing threshold to 75% of the *shortest* anchor
  silence observed so far as chapters are found (raised once at the second
  mark, only ever lowered after that), re-probing everything it skipped
  whenever a sequence gap turns up, so far fewer Whisper probes are needed
  without a fixed guess — see [Pass 2 — probing](#pass-2--probing). An explicit
  numeric value disables this and probes every silence at or above it
  instead; this is still the main manual speed knob if `auto`'s heuristic
  doesn't suit a particular audiobook: if the pauses are unusually generous
  and consistent, `-n 2.5` can cut the number of probes further still, but
  chapters go missing if it's set too high. Set it a little too high and a
  heavier `--pass3-model` will usually rescue the run anyway: a gap it leaves
  behind is swept for pauses down to half a second under whatever this says
  (see [Pass 2.5](#pass-25--cheap-gap-re-probe-only-with-a-heavier---pass3-model)).

`-j`, `--mark-before-jingle`
: Anchor the chapter mark to the end of the previous
  chapter's actual narration instead of the default fixed `--mark-lead` offset
  (see `--max-jingle-length` below): starting from whatever mark default mode
  (normally already refined, see `--quick-marks` below)
  found, the mark is walked backward — out of any silence
  it sits in, then, if no real speech is heard right there, back through the
  jingle's own music — until real narration is found, and placed there; a
  mark with real narration already right before it is left unchanged, keeping
  its ordinary `--mark-lead` offset, so chapters that happen to carry no jingle
  are marked exactly as they would be without this option. Two
  jingles playing back to back with an audible break between them stop the
  walk at that break, placing the mark at the second jingle's start rather
  than in front of the first. Where the walk comes to rest on a pause — the
  hush between the previous chapter and the jingle, or between two jingles —
  the mark is moved back into that pause by `--mark-lead` seconds, or to the
  pause's own beginning if it is shorter than that. If a jingle opens the file
  with nothing spoken before it at all, the mark instead backs off by a small
  fixed margin from the earliest point reached.
  The same backward-only quietest-point nudge described under
  `--quick-marks` below is then applied to the result.
  **Avoid combining this with `--quick-marks`.** The walk can only be as good
  as the mark it starts from, and `--quick-marks` leaves it at raw default
  placement — which occasionally lands *past* the announcement rather than
  before it. When that happens the walk stops at the pause following the
  announcement, leaving the mark after it instead of before the jingle
  (seconds to tens of seconds late, on the odd chapter). The refinement that
  runs by default corrects the starting point first, which avoids this. The probe-window
  widening and VAD pre-pass this placement relies on (see
  [Pass 1](#pass-1--silence-scan-and-vad-pre-pass)) already run by default
  regardless of this option. Without `--mark-before-jingle`, a mark is
  always placed `--mark-lead` seconds before the chapter phrase, no matter what
  precedes it.

`-k`, `--mark-lead <seconds>`
: How far in front of the announcement a mark is placed; default 0.35.
  This is a matter of taste, not of accuracy: marks are located just as
  precisely whatever it is set to, and it only decides how much lead-in you
  hear before the narrator starts speaking. Raise it for a longer run-up,
  lower it to land closer to the first word — though below roughly 0.2 the
  opening consonant of the announcement starts to get clipped, and a hard one
  such as the "K" of "Kapitel" is easy to lose without noticing. `0` marks the
  measured onset itself. Under `--mark-before-jingle` it still applies: in full
  where a chapter has no jingle in front of it, and as a back-off into the pause
  preceding a jingle where there is one (capped at that pause's length, so the
  mark never lands in the previous chapter's narration). Both `,` and `.`
  work as the decimal point.

`-Q`, `--quick-marks`
: **Experimental.** Skip the mark refinement that normally runs, and take
  probing's own placement as final. By default every mark — including the
  starting point `--mark-before-jingle` walks backward from — is verified by
  re-transcribing short clips of the audio it was heard in, until one of them
  hears the chapter phrase first; the announcement's beginning is then measured to
  within a tenth of a second and the mark set `--mark-lead` seconds ahead of
  it. A mark
  that can never be confirmed this way is
  left as originally placed rather than guessed at. Whatever mark results is
  then nudged up to 0.15 seconds earlier to the quietest point in that
  stretch, but only when that is a clear (at least 6 dB) improvement; a mark
  is never moved later, so a player never starts mid-word.
  `--quick-marks` skips all of it, which is markedly faster — the checks cost
  a handful of extra Whisper transcriptions per chapter, most of all for a mark
  probing left seconds away from its announcement (see
  [Pass 2](#pass-2--probing)) — at the price of accuracy: the marks it leaves
  are usually usable for jumping to a chapter, but one can sit *after* the
  chapter phrase instead of before it, so playback starts a moment into the
  announcement. That can happen even together with `--mark-before-jingle`.

`-X`, `--max-jingle-length <seconds|auto>`
: Longest expected jingle (0, or 1–600); this is always the probe window's
  ceiling (default, and ceiling with `auto`: 45). Above 0, probe windows
  are `--max-jingle-length` + 5 seconds wide and a VAD pre-pass runs (see
  [Pass 1](#pass-1--silence-scan-and-vad-pre-pass)) to add candidates for
  jingles with no detectable silence around them. `0` says no jingle is
  expected at all: probe windows fall back to a fixed 12 seconds, and the
  VAD pre-pass is skipped entirely unless `--mark-before-jingle` still needs
  it for mark placement — reproducing this tool's original,
  pre-jingle-detection behavior. An explicit numeric value (still above 0)
  disables the self-tightening below and keeps the probe window fixed at
  that width throughout — useful if the jingle length is known and
  consistent, or to shrink the window further for speed. With `auto` (the
  default), probing starts
  at the 45 s ceiling and, from the second jingle mark found (the first is
  excluded for the same reason as `--min-silence-length auto` excludes the
  first silence — the gap before it isn't necessarily representative),
  resizes the probe window to 1.25x the longest jingle actually observed so
  far plus the 5-second phrase margin, never past the original ceiling. The
  second mark narrows the window down from the ceiling; after that it only
  ever widens again (when a longer jingle turns up) — the exact mirror of
  `--min-silence-length auto`'s lower-only threshold, and for the mirrored
  reason: a window below an already observed jingle length would be too
  short for exactly the kind of jingle this book has proven to play. For a
  silence-less jingle (found via the VAD pre-pass), the observed length
  comes from the VAD region's own boundaries rather than the distance to
  the announcement, which can otherwise include a bit of post-jingle
  silence before the phrase starts. Chapters with no jingle (or an
  ultra-short one, under 2 seconds) are excluded from this — some
  audiobooks only play the jingle for some chapters, and such a chapter
  says nothing about how long the window needs to be for one that does
  have a full jingle. Same idea as `--min-silence-length auto`, just for
  the jingle window instead of the silence threshold. Once the window
  narrows, VAD regions longer than it stop being probed too — the same
  speedup pass 2 already gets from tightened silence candidates — but a
  later sequence gap temporarily resets the window back to the ceiling,
  retries every candidate since the last chapter at that full width — those
  skipped, and those already probed while the window was narrower than the
  ceiling, whose announcement may simply have sat past the end of it — and
  then returns to the adapted width, including whatever the recovered
  chapters' own jingles just taught it — and how far into its window a
  recovered chapter's announcement reached, which may widen the window by up
  to a quarter of its current width per recovery. `auto` implies a nonzero ceiling, so
  it cannot mean "no jingle expected."

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
  the chapter/prologue/epilogue phrases and all title words are localized
  for it (see [section 7](#7-languages-and-number-recognition)) exactly as an
  explicit `--lang <code>` would, but resolved individually per file.
- Below 0.5, or when the clip at the start of the file is too short to
  probe (well under half a second of audio), the detection is treated as
  inconclusive and the run falls back to `en` for that file - the same
  0.5 cutoff used for flagging low-confidence chapter marks (see
  [section 12](#12-output-progress-and-logging)), since Whisper itself is,
  on average, more unsure than sure below it.
- An explicitly given phrase or title option always wins over the localized
  default, regardless of the detected language.

The outcome is shown in the per-file result line, `--dry-run` listing and
`--verbose` log:

```
Whisper model "turbo" loaded (Vulkan backend on NVIDIA GeForce GTX 1070, auto language detection), 2 file(s) to process.
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

`-a`, `--early-abort <minutes>`
: Always on by default (60 minutes; pass `0` to disable it entirely and
  always probe the whole file). Once pass 2 has probed this many minutes
  into a file's play time without finding a single chapter, detection for
  that file is abandoned outright instead of transcribing the rest of what
  is plainly not going to yield any — a wrong `--chapter-phrase`, wrong
  `--lang`, or a book that just announces chapters differently. The file is
  left unchanged, reported exactly like a completed scan that found
  nothing. Only applies to a fresh, from-scratch run; a `--verify` gap
  recovery or a `.missing-marks` resume already has a confirmed chapter to
  build on and is never subject to this.

`-e`, `--expected-start-chapter <n>`
: For a split-book part that does not begin at chapter 1: the chapter number
  this file is expected to start at. Without it (the default), whatever
  number pass 2 finds first is trusted outright and nothing below it is ever
  searched for — see
  [Pass 3](#pass-3--gap-filling-only-when-needed). With it, a first chapter
  found *below* `<n>` aborts the file outright, left unchanged and reported
  exactly like a completed scan that found nothing — almost certainly the
  wrong file, `--chapter-phrase` or `--lang`, not a genuine split-book start.
  A first chapter found *above* `<n>` is instead treated like any other gap:
  pass 3 searches for the missing numbers down to `<n>`, and if it still
  can't find all of them, the file is tagged `.missing-marks-…` exactly as
  an unresolved gap between two detected chapters already is. Only applies
  to a fresh, from-scratch run, the same restriction as `--early-abort`.

`-L`, `--trailing-scan`
: Transcribe everything after the last chapter found, through to the end of the
  file, looking for further chapters (default: off). Pass 3 spots a missing
  chapter as a hole in the number sequence, which needs a known chapter on
  either side of it — so a chapter missing *after* the last one found is the
  one case nothing can notice, and the file is written out looking complete.
  This closes that hole. The catch is that it is not a safety net that only
  costs something when it fires: with no expected numbers to satisfy, the scan
  can never stop early, so every file pays a full final chapter's worth of
  transcription time whether or not anything was wrong. Reach for it when a
  book's last chapter matters more than the run time — and note that it takes
  that at its word: the tail is also given the shifted re-read of
  [pass 3.5](#pass-35--the-shifted-re-read), whatever `--pass3-model` says, so
  the price is two passes over it rather than one. Does nothing when no
  chapter was found at all — there is no "last chapter" to scan from — nor
  after an `--early-abort` or `--expected-start-chapter` abort.

`-N`, `--max-chapter-number <n>`
: The highest chapter number this book plausibly has (default: no limit). A
  detected chapter numbered above `<n>` is discarded on the spot as a
  mishearing rather than becoming a mark. Worth setting whenever you know the
  chapter count roughly: a single "chapter five hundred and ten" misheard in a
  twelve-chapter book otherwise stretches the expected sequence to 510, leaves
  pass 3 hunting for the ~500 chapters "missing" in between, and ends with the
  file tagged as missing all of them. Lighter Whisper models (`tiny`, `base`)
  are the usual source of such numbers. Not to be confused with
  `--max-chapters`, which counts a file's *pre-existing* marks rather than the
  numbers heard in the audio.

`-V`, `--verify`
: Checks one specific claim about every existing mark: *this mark is titled
  chapter N, and chapter N is announced in the audio right here*. That makes
  it the right tool for marks meant to be one per numbered chapter — an
  earlier ABChapterize run, or a tagger that followed the book's own chapter
  structure — and the wrong tool for marks that are not.

  Marks that **group several book chapters into one entry**, as retailers'
  own marks often do, cannot pass this check and are not meant to: a mark
  titled "Chapter 2" sitting where the narrator says "chapter four" is doing
  its job perfectly well, and `--verify` will still call it unconfirmed.
  That matters, because **`--verify` is not a read-only report**: an
  unconfirmed mark gets redetected. A file whose marks fail *wholesale* is
  the one case that is left completely alone — see below — so a coarsely
  marked book is not silently rewritten; but its marks are also not
  improved. On such a file, either leave it alone (the default, no
  `--verify`) or redo it wholesale with `--force` if you want
  ABChapterize's own chapter-level marks instead.

  Marks whose title carries no chapter number that can be read are not
  checked at all. If that is true of every mark in a file, there is nothing
  to verify, and the file is skipped with a note saying so — unchanged.

  What the check does: a short window around the mark's own timestamp is
  probed with Whisper for the chapter phrase and the expected chapter
  number, reusing the same transcription machinery as normal detection
  rather than a plain string/fingerprint comparison. If the phrase isn't
  found on the first pass, any long unrecognized stretch inside the window
  gets a further, closer look in small overlapping chunks before the mark is
  given up as unconfirmed — documented in the source. Marks that all check
  out are left untouched, same as a skip without `--verify`. If any mark fails but at
  least one other is confirmed, the confirmed marks are trusted and kept
  as-is, and detection - including its own proper pass 2 - runs only over
  the stretch(es) of the file around the unconfirmed mark(s), rather than
  the whole file; a still-missing mark past the last one in the file is
  covered by a further, file-end-only fallback pass, since nothing else
  would notice it is missing at all. If the failures start to *outnumber*
  the confirmations — nothing confirmed at all being the extreme case — the
  file is instead skipped with a warning and every existing mark is left
  exactly as it was. Marks failing in bulk almost always means they were
  never one-per-numbered-chapter to begin with, and replacing them is not a
  decision a batch run should make on its own; re-run that one file with
  `--force` and without `--verify` if replacing them is what you want. A file already over
  the `--max-chapters` threshold is still assumed bogus outright and skips
  verification entirely; `--verify` only decides the borderline cases where
  the mark count alone isn't proof of anything. Since its intent
  contradicts `--force` (always discard vs. decide based on a check) and it
  has nothing to check against with `--import` (which never runs
  detection), `--verify` cannot be combined with either. The progress bar's
  chapter state (see [section 12](#12-output-progress-and-logging)) tracks
  confirmations the same way it tracks fresh detections: the highest
  confirmed mark, with any lower one that failed confirmation shown as a
  `(-N)` gap.

`-h`, `--verify-threshold <n>`
: Requires `--verify`. Draws the "failed wholesale" line above by hand: more
  than `<n>` failed marks in a file leaves it untouched with a warning,
  however many others were confirmed. Without this option the line sits
  where the failures begin to outnumber the confirmations. Note this only
  moves the line — it cannot switch the outcome back to the full,
  from-scratch redetection that earlier versions did here.

### Safety and undo

`-b`, `--backup`
: Keep the original file as `<name>.<ext>.bak` next to the modified file.
  A `.bak` file already left behind by an earlier run is not an error - it is
  simply replaced, and the summary line notes that it was.

`-R`, `--revert`
: Restore backups instead of processing: for every supported audio file with
  an added `.bak` suffix under the target, the current file is deleted and
  the backup renamed back. Combinable with `--recurse`, `--filter` and the
  output options (`--quiet` and `--summary` take effect; `--verbose` and
  `--no-bar` are accepted but change nothing here). All detection and safety
  options are rejected. An audio file named directly has its own `.bak`
  neighbour restored, so the suffix need not be typed out.

`-O`, `--no-op`
: Lists every file `--filter` (and `--recurse`) would select, then exits
  without loading a Whisper model, invoking ffmpeg or touching any file - a
  quick way to check that a `--filter` regexp or extension list actually
  matches the intended files before committing to a real run. Requires
  `--filter`; combinable only with `--recurse` and the output options
  (`--quiet` suppresses the listing itself, leaving just `--summary`'s
  count), the same restriction `--revert` has.

`--ignore-progress`
: Start every listed directory over instead of resuming it, ignoring (and
  replacing) the progress file described in
  [Resuming an interrupted run](#resuming-an-interrupted-run) below. Nothing
  else about the run changes. Not to be confused with the `.missing-marks`
  resume of an individual file, which `--force` governs.

### Resuming an interrupted run

While a directory named on the command line is being processed, the files
already finished in it are recorded in a hidden `.abchapterize-progress` file
inside that directory. It is deleted again the moment that directory is
finished, so a run that ends normally leaves nothing behind — but a run cut
short by Ctrl+C, a crash or a power loss leaves the record, and running the
same command again skips straight past those files instead of scanning the
whole library from the top.

The details worth knowing:

- Each directory given on the command line keeps its own progress file, and
  each is removed as soon as that particular directory is done.
- A file named directly on the command line has no directory to keep a record
  in, so it is never checkpointed.
- `--dry-run`, `--no-op` and `--revert` never write one either — nothing they
  do is worth not doing twice.
- The record notes which options the run used. Change any option that affects
  the outcome and the stale record is discarded rather than misapplied;
  options that only change the output's appearance (`--quiet`, `--verbose`,
  `--verbose-transcripts`, `--log-file`, `--debug`, `--no-bar`, `--color`, `--summary`) or how fast
  the run gets there (`--vad-threads`, `--whisper-threads`, `--cpu-only`,
  `--use-gpu`) do not count, so those can be added or dropped when resuming —
  including moving an interrupted batch to a different machine or a different
  GPU.
- Being interrupted twice is no different from being interrupted once: a
  resumed run adds its own finished files to the record rather than starting
  it over, so however often a batch is stopped and restarted, no file is
  processed twice.
- A file the run renamed (a `.missing-marks` tag added or dropped) is
  recognized under either name.
- Deleting the file by hand is always safe; it only ever costs the resume.
- Only files the run actually finished are recorded. The one that was in
  flight when the run died is attempted again — a killed run never leaves a
  half-written audio file behind (see
  [section 4](#4-how-chapters-are-written--file-safety)), so there is nothing
  to clean up first.

### Titles

`-t`, `--title <word>`
: Word used to build chapter titles; the chapter number is appended
  (default: `Chapter`, localized by `--lang` — e.g. `Kapitel` with
  `--lang de`). "Chapter 1", "Chapter 2", …

`-i`, `--intro-title <word>`
: Title of the synthetic intro chapter covering the audio before the first
  detected mark (default: `Intro`, localized by `--lang` — see the table
  in [section 7](#7-languages-and-number-recognition)). See
  [The intro chapter](#the-intro-chapter).

`-P`, `--prologue-title <word>`
: Title written for a detected prologue (default: `Prologue`, localized by
  `--lang`). See [Prologue and epilogue](#prologue-and-epilogue).

`-G`, `--epilogue-title <word>`
: Title written for a detected epilogue (default: `Epilogue`, localized by
  `--lang`).

A `--custom` mark's title comes from its own mapping instead of from an option
of its own; see [Custom marks](#custom-marks).

### Output

`-q`, `--quiet`
: Suppress per-file output and progress bars; warnings and errors are still
  shown.

`-v`, `--verbose`
: Print processing details as timestamped log lines: each probe, gap and
  verify line stops at its `<length>@<time>` header. The best overview of what
  the pipeline is doing without the full recognizer output. See
  [section 12](#12-output-progress-and-logging).

`-T`, `--verbose-transcripts`
: Like `--verbose`, but also dumps every Whisper transcript's segments (with
  their timings and confidence) after the header line — the best way to see
  exactly what the recognizer heard, and what `--verbose` did before this flag
  existed. Implies `--verbose`.

`-o`, `--log-file <path>`
: Write the log to a file instead of the console. This switches logging on by
  itself, so `--verbose` is not needed alongside it — add `-T` if the
  transcripts should go in as well. The console is left with its progress bar
  and its result lines, and the file receives those too — the per-file
  summaries, including the ones `--quiet` keeps off the screen, and the
  `--summary` block closing the run. An existing file is appended to,
  never overwritten; each run is bracketed by a header line naming the version
  and the command line, and a closing line. Every line is written out as it
  happens, so an interrupted run still leaves a complete log behind. The path's
  directory must exist. See [section 12](#12-output-progress-and-logging).

`-B`, `--no-bar`
: Never display progress bars; per-file results are printed as timestamped
  log lines. Useful for CI jobs and log files. (Progress bars are also
  disabled automatically when the output is redirected.)

`--color <mode>`
: Whether output is drawn in color: `auto` (the default), `always` or `never`.
  Two things are colored, the progress bar and the closing
  [`--summary`](#12-output-progress-and-logging) block; log lines and per-file
  result lines stay plain, and a `--log-file` receives plain text whatever the
  console gets. `auto` turns color off when the output
  is redirected and when the `NO_COLOR` environment variable is set to
  anything. On Unix it additionally wants `TERM` to name a 16-color terminal
  such as `xterm-256color`: a terminal that still calls itself plain `xterm` is
  described by its own terminfo entry as having eight colors, and everything
  the tool draws is translated through that entry, so the bar's dark grey would
  come out as black on black. Since no terminal can actually be asked whether
  it does color, `auto` sometimes guesses wrong — Git Bash on Windows, CI logs
  and that `xterm` case are the usual ones, and `--color always` overrides it
  for all of them.

`-s`, `--summary`
: Print a summary at the end of the run: files encountered / processed /
  skipped, warnings, how many processed files had no chapter found at all,
  total and average processing time, and — when at least
  one chapter mark was written — the min/max/average Whisper confidence
  across those marks (not every probe attempted, only the ones that produced
  a mark). Also, across all processed files, the shortest silence and (when
  the VAD pre-pass ran, which it does by default) longest jingle found
  before any chapter — each reported
  both counting chapter 1 and, as an "inter-chapter" figure, ignoring it
  (its lead-in is often unrepresentative) — the total
  audio fed to Whisper as an absolute time and a share of the total run
  length (over 100 % is normal — re-probed stretches are counted each time),
  and Whisper's transcription speed as a percentage of real time.

  The block closes with two listings, each left out when it would be empty:
  every file that was **skipped** and why, and every file left **still missing
  chapter marks**, with how many are missing and — up to ten of them — which
  chapters those are. Files appear under the name they carry once the run is
  over, so a book tagged
  [`.missing-marks-…`](#pass-35--the-shifted-re-read) is listed under its
  tagged name and can be found in the folder as printed.

  ```
  Summary: 4 file(s) encountered, 2 processed, 2 skipped, 1 with warnings
  Total time: 1:42:07, average per processed file: 50:31
  Confidence of written chapter marks: min 0.71, max 0.99, avg 0.94
  Skipped 2 file(s):
    Stalker.m4b: has 30 chapter marking(s)
    Wintersmith.m4b: 14 pre-existing chapter marking(s) verified correct
  Still missing chapter marks in 1 file(s):
    Die Dritte Macht.missing-marks-3-7.m4b: 2 mark(s) missing (chapter 3, 7)
  ```

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
  handling (`--force`/`--max-chapters`/`--verify`), low-confidence flagging
  and `--summary` stats all behave exactly as in a real run; only the final
  ffmpeg remux is skipped, so the file is guaranteed untouched. Cannot be
  combined with `--revert` (there is nothing to preview when reverting).

### Chapter export/import

A rare misdetection (a mis-heard chapter number, a title that needs
tweaking) doesn't have to mean re-running Whisper on the whole file. Export
the detected chapters to a sidecar file, hand-correct it in a text editor,
then import it back — the corrected chapters are written directly, without
touching Whisper at all.

`-E`, `--export`
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
  `--chapter-phrase`, `--prologue-phrase`, `--epilogue-phrase`, `--custom`,
  `--custom-file`, `--ignore-chapter-numbers`, `--model`, `--pass3-model`,
  `--mark-before-jingle`, `--quick-marks`, `--mark-lead`,
  `--max-jingle-length`, `--min-silence-length`, `--early-abort`,
  `--expected-start-chapter`, `--max-chapter-number`,
  `--trailing-scan`, `--verify` — nor with the title options `--title`,
  `--intro-title`, `--prologue-title` and `--epilogue-title`, since an
  imported mark carries the title the sidecar gives it and no intro mark is
  prepended — nor with `--export`, `--revert` or
  `--no-op`. Pre-existing chapter
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

### Thread counts

Files are always processed one at a time, so each one gets the whole machine.
Both options below take a number or `auto`, and `auto` means one thread per
**physical** CPU core — not per hardware thread. Hyperthreads add a little on
machines where they help and cost a great deal on machines where they do not,
and nothing here can tell which machine it is running on; if you know yours
better, say so with an explicit number. Neither option is valid with `--revert`
or `--no-op`, which do no work to spread out.

`--vad-threads <n|auto>`
: How many stretches of the book the voice-activity pre-pass
  ([Pass 1](#pass-1--silence-scan-and-vad-pre-pass)) classifies at once
  (default: `auto`). This is the pass that benefits most from a wide machine — on
  a 12-core one, an 8.5-hour audiobook's Pass 1 drops from 201 seconds to 50 —
  and the speech it finds does not change with the thread count.

  Each thread holds about 11 minutes of decoded audio while it works, which is
  roughly 40 MB, so this is also the knob for how much memory Pass 1 uses: about
  480 MB on a 12-core machine, and proportionally more on a wider one. Lower it
  if that matters more to you than the seconds it costs.

  `--vad-threads 1` classifies the book as a single uninterrupted stream, which
  is what versions before 0.10.0 always did.

`--whisper-threads <n|auto>`
: CPU threads for Whisper transcription (default: `auto`). Mostly a CPU-backend
  concern: on a GPU backend the recognition itself runs on the GPU, and this
  only covers the work around it. A second model named with `--pass3-model` gets
  the same budget — the two never run at the same time, so there is nothing to
  divide between them.

Both counts are recorded in the `--verbose` log at the start of a run, together
with what the machine actually has:

```
[14:32:07] threads: Whisper 12, voice-activity pre-pass 12 (12 physical core(s), 24 logical)
```

### Miscellaneous

`-?`, `--help`
: Show the usage information.

`--version`
: Show the version number, plus the auto-incrementing build number and UTC
  build timestamp (e.g. `abchapterize 0.9.0 (build 42, built 2026-07-20
  14:33:12 UTC)`). Not shown anywhere else - `--help`'s banner only ever
  shows the plain version number.

`--list-gpus`
: List this machine's Vulkan GPUs by name, as `--use-gpu` matches them, then
  exit. Needs neither a file nor a model, and a machine with no Vulkan GPU is
  reported as such rather than treated as an error. See
  [Picking a GPU on a multi-GPU machine](#picking-a-gpu-on-a-multi-gpu-machine).

## 7. Languages and number recognition

By default (`--lang auto`), the language used below is detected per file -
see [Auto language detection](#auto-language-detection). An explicit
`--lang <code>` pins the whole run to one language instead.

The chapter number in an announcement is recognized in three ways:

- **Digits** work in *every* language: "Chapter 12", "Kapitel 12.",
  "2nd", "2e", …
- **Roman numerals** work in every language too: "Chapter XIII", "XVII.
  Kapitel". Whisper writes an announcement this way whenever it settles on a
  book-heading style for it ("CHAPTER XIII. THE SHAKING OF THE SHEETS"), which
  it may do for some chapters of a book and not others, so nothing about the
  book itself tells you in advance whether this form will turn up. Only
  the standard spelling of a number counts — "IIII" and "IC" are not read as 4
  and 99 — and a *one-letter* numeral (I, V, X, L, C, D, M) is read as a number
  only when a period follows it, as a heading gives it: without that guard,
  every English "chapter I wrote" would become a chapter 1.
- **Numbers transcribed as words** — Whisper often writes numbers out
  ("Chapter twenty-one") — are parsed for these languages:

| Language | Code | Example cardinal | Example ordinal |
| --- | --- | --- | --- |
| English | `en` | chapter twenty-one | twenty-first chapter |
| German | `de` | Kapitel einundzwanzig | Einundzwanzigstes Kapitel |
| French | `fr` | chapitre vingt et un | premier chapitre |
| Spanish | `es` | capítulo veintiuno | vigésimo primer capítulo |
| Italian | `it` | capitolo ventuno | primo capitolo |
| Dutch | `nl` | hoofdstuk eenentwintig | eenentwintigste hoofdstuk |
| Turkish | `tr` | bölüm yirmi bir | yirmi birinci bölüm |
| Portuguese | `pt` | capítulo vinte e um | vigésimo primeiro capítulo |
| Polish | `pl` | rozdział dwadzieścia jeden | rozdział dwudziesty pierwszy |
| Swedish | `sv` | kapitel tjugoett | tjugoförsta kapitlet |
| Danish | `da` | kapitel enogtyve | enogtyvende kapitel |

Cardinals are understood from 0 to 999 in every language, ordinals as far as
the language spells them compositionally (see below), and
the number may come **after** the phrase ("Chapter Seven") or **before** it
("Erstes Kapitel", "2. Kapitel", "chapitre premier", "Birinci Bölüm").
The parsers are exhaustively unit-tested against independent reference
spellers for every cardinal number 0–999 in every language, and for every
word ordinal too. Word ordinals reach 999 in most languages; Spanish and
Portuguese stop at 199, and Danish at 100 standing on its own, since past
that point these three reach for words ("ducentésimo") that no chapter
announcement plausibly uses. Digit ordinals ("21.", "200.") work at any
value regardless of language.

Spelling variants are covered rather than assumed: masculine and feminine
("vigésima primera"), fused and separate ("decimoctavo" as well as "décimo
octavo"), accented and not, European and Brazilian Portuguese, and both the
formal and the everyday Danish tens ("halvtredsindstyvende" and
"halvtredsende" for 50th).

Where two of the three readings would fit the same word, the language's own
number words win over the Roman reading: French "dix" is ten, not 509.

For these languages, `--lang` also localizes the defaults of
`--chapter-phrase`, `--title` and `--intro-title`:

| `--lang` | Default phrase | Default title word | Default intro title |
| --- | --- | --- | --- |
| `en` | `/chapter/` | Chapter | Intro |
| `de` | `/kapitel/` | Kapitel | Intro |
| `fr` | `/chapitre/` | Chapitre | Introduction |
| `es` | `/cap[íi]tulo/` | Capítulo | Introducción |
| `it` | `/capitolo/` | Capitolo | Introduzione |
| `nl` | `/hoofdstuk/` | Hoofdstuk | Intro |
| `tr` | `/b[öo]l[üu]m/` | Bölüm | Giriş |
| `pt` | `/cap[íi]tulo/` | Capítulo | Introdução |
| `pl` | `/rozdzia[łl]/` | Rozdział | Wstęp |
| `sv` | `/kapit(?:el\|let)/` | Kapitel | Introduktion |
| `da` | `/kapit(?:el\|let)/` | Kapitel | Introduktion |

The default phrases are regular expressions so that one language's spellings
are covered at once: an accent Whisper dropped (`capitulo` for `capítulo`), a
letter it wrote without its diacritic (`bolum` for `bölüm`), or a stem the
language itself changes (Swedish and Danish say "kapitlet" as readily as
"kapitel"). Nothing else changes — they are matched case-insensitively and as
a substring exactly as a plain word would be, so an inflected ending needs no
pattern of its own ("rozdziału" is found by `rozdzia[łl]`).

The same applies to the defaults of `--prologue-phrase`/`--prologue-title`
and `--epilogue-phrase`/`--epilogue-title` (see
[Prologue and epilogue](#prologue-and-epilogue)):

| `--lang` | Prologue phrase | Prologue title | Epilogue phrase | Epilogue title |
| --- | --- | --- | --- | --- |
| `en` | `/prolog/` | Prologue | `/epilog/` | Epilogue |
| `de` | `/prolog/` | Prolog | `/epilog/` | Epilog |
| `fr` | `/prologue/` | Prologue | `/[ée]pilogue/` | Épilogue |
| `es` | `/pr[óo]logo/` | Prólogo | `/ep[íi]logo/` | Epílogo |
| `it` | `/prologo/` | Prologo | `/epilogo/` | Epilogo |
| `nl` | `/proloog/` | Proloog | `/epiloog/` | Epiloog |
| `tr` | `/prolog/` | Prolog | `/epilog/` | Epilog |
| `pt` | `/pr[óo]logo/` | Prólogo | `/ep[íi]logo/` | Epílogo |
| `pl` | `/prolog/` | Prolog | `/epilog/` | Epilog |
| `sv` | `/prolog/` | Prolog | `/epilog/` | Epilog |
| `da` | `/prolog/` | Prolog | `/epilog/` | Epilog |

The English phrases cover the American "prolog"/"epilog" spellings simply by
stopping short of the ending.

Each language uses its Latin-derived form rather than a native near-synonym
(German "Vorwort", Turkish "Önsöz", …): those name a foreword, which is
front matter *about* the book, whereas a prologue is part of the story and is
what a narrator actually announces. Where a particular book disagrees,
`--prologue-phrase`/`--epilogue-phrase` override it.

Other languages work too: give `--lang` for transcription and a
`--chapter-phrase` (plain or regexp); announcements with digit numbers are
then fully supported, e.g. `--lang cs --chapter-phrase kapitola`. If you would
rather have your language supported properly — its spoken numbers included —
[doc/adding-a-language.md](adding-a-language.md) walks through adding one; it
is a self-contained job that needs no knowledge of the rest of the codebase.

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

If you would rather trade some of that safety margin for speed, pair the two
model options instead of lowering `--model` on its own: `-m small -M turbo`
runs the many short probe transcriptions with the quick model and keeps
`turbo` for the chapters `small` could not resolve — which, because `-M` then
names the *heavier* model, also brings
[pass 2.5](#pass-25--cheap-gap-re-probe-only-with-a-heavier---pass3-model) and
pass 2's second reading of an implausible chapter number into play. In testing
on English and German audiobooks this was meaningfully faster than plain
`turbo` and produced the same marks. It has not been checked across every
supported language, and it does put a smaller model in charge of most of the
listening, so compare a `--dry-run` against the default on one of your own
books before making it a habit.

### Memory requirements

A loaded model needs somewhat more memory than its file on disk. whisper.cpp,
the engine underneath ABChapterize, publishes its own
[memory usage](https://github.com/ggml-org/whisper.cpp#memory-usage) figures:

| Model | Disk | Memory |
| --- | --- | --- |
| `tiny` | 75 MiB | ~273 MB |
| `base` | 142 MiB | ~388 MB |
| `small` | 466 MiB | ~852 MB |
| `medium` | 1.5 GiB | ~2.1 GB |
| `large` | 2.9 GiB | ~3.9 GB |

That table has no `turbo` row — it predates the model. Expect roughly 2.2 GB
for the default model, consistent with `turbo` having about as many parameters
as `medium` (809 M against 769 M) on a larger file. Treat that one as an
estimate rather than a published number.

Be aware that figures quoted for Whisper elsewhere are often much higher —
OpenAI's
[available models](https://github.com/openai/whisper#available-models-and-languages)
table says ~5 GB for `medium` and ~10 GB for `large`. Those describe OpenAI's
own Python implementation, not the GGML build used here, which is roughly two
to three times leaner. Read them for the model lineup and the speed/accuracy
tradeoff, not for sizing your machine.

On a GPU backend (CUDA or Vulkan) this comes out of video memory, on a CPU
backend out of system RAM. Either way exactly one copy is loaded, whatever the
run's size: files are processed one at a time. Specifying a different
`--pass3-model` adds one further copy, loaded only once something actually asks
for it — pass 3, or one of the second opinions pass 2 asks a heavier model for.

The voice-activity pre-pass wants memory of its own on top, and unlike the model
that amount is yours to choose: about 40 MB per `--vad-threads` thread, so around
480 MB with the default of one per physical core on a 12-core machine. See
[thread counts](#thread-counts).

ABChapterize does not check any of this up front. If the memory is not there,
you will find out from the model loader or the operating system rather than
from a friendly message, so on a memory-constrained machine pick the model
deliberately: `small` is the smallest size that gives dependable results.

Audio is decoded and transcribed in bounded windows rather than a file at a
time, so memory does not grow with the length of the audiobook — a 30-hour
book costs no more than a 3-hour one.

Models live in the `models` folder next to the executable. A missing model is
downloaded automatically on first use from the
[ggerganov/whisper.cpp](https://huggingface.co/ggerganov/whisper.cpp/tree/main)
repository on Hugging Face, with a progress display; partial downloads never
count as installed. If the download fails (offline machine, write-protected
folder), the error message contains step-by-step instructions for installing
the model manually.

### Using your own model

`--model custom:<path>` (and `--pass3-model custom:<path>`) points at a GGML
Whisper model file anywhere on disk instead of one from the table above — a
fine-tune for a particular narrator or language, a quantized build, or simply
a model kept outside the `models` folder:

```
abchapterize -m custom:~/models/ggml-my-finetune.bin book.m4b
abchapterize -m small -M "custom:D:\models\ggml-large-v3-q5_0.bin" book.m4b
```

A leading `~` is expanded to your home directory, on Windows too. The path is
checked when the command line is parsed, so a typo fails immediately rather
than after the first hours of a batch run.

Such a file is used exactly as it is: nothing is downloaded, and nothing is
checked against a pinned digest — it is yours, so vouching for it is yours too
(see [Download integrity verification](#download-integrity-verification) for
what that check does and does not cover). It also has to be a model the
whisper.cpp engine underneath can load; if it is not, the failure comes from
that loader.

ABChapterize forms an opinion about a custom model in exactly two places:
[pass 2.5](#pass-25--cheap-gap-re-probe-only-with-a-heavier---pass3-model),
which needs to know whether `--pass3-model` is an upgrade over `--model`, and
[pass 3.5](#pass-35--the-shifted-re-read), which needs to know whether it is a
downgrade. That comparison is made by **file size**, for custom and built-in
models alike — within the Whisper family the bigger file has always been the
more capable model. A custom model smaller than `--model`'s therefore behaves
like naming a lighter built-in one: pass 2.5 and pass 3.5 stay off, pass 3
still uses it.

### Download integrity verification

Every downloaded model file is checked against a SHA-256 and a SHA3-256
digest pinned in the executable, computed while the file streams in (no
second pass over the multi-gigabyte file is needed). Both must match. This
guards against the model repository itself being compromised: if an attacker
ever took over the Hugging Face repository and swapped a model file for
something else, that something else would also need to satisfy two
independently-designed hash algorithms simultaneously to be accepted -
something not achievable in practice, unlike defeating a single hash. A file
that fails either check is rejected and deleted, never loaded by the native
Whisper model loader. On success, the startup message reads
"Model downloaded and verified at ...".

If a platform's cryptography stack has no SHA3-256 support (a possibility on
older or minimal Linux systems, depending on the OpenSSL version installed),
only SHA-256 is enforced and a note is printed; this has not been observed
on any system tested so far (Windows 11 and current WSL2/Ubuntu both support
SHA3-256 natively). Manually installed model files (see above) are not
verified automatically - if you install one by hand, check its SHA-256
yourself against the digest shown in the manual-installation error message
or listed here:

| Selector | SHA-256 |
| --- | --- |
| `tiny` | `be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21` |
| `base` | `60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe` |
| `small` | `1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b` |
| `medium` | `6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208` |
| `turbo` | `1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69` |
| `large` | `64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2` |

## 9. GPU acceleration

The native Whisper runtime is selected automatically at start: **CUDA**
(NVIDIA) when available, then **Vulkan** (any modern GPU, including inside
WSL2), then CPU. Both the backend and the GPU it settled on are shown in the
startup line:

```
Whisper model "turbo" loaded (Vulkan backend on NVIDIA GeForce GTX 1070), 3 file(s) to process.
```

`--cpu-only`/`-C` skips straight to the CPU backend instead, e.g. to leave a
GPU free for other work or to sidestep a flaky/unsupported GPU backend. The
Silero VAD pre-pass always runs on CPU regardless, so this option only
changes Whisper's own backend.

### Picking a GPU on a multi-GPU machine

On a machine with exactly one discrete GPU next to an integrated one,
ABChapterize picks the discrete card by itself and you need read no further.
This matters more than it sounds: left to its own devices the Vulkan runtime
takes whichever GPU it enumerates first, and on a test machine with a
GeForce GTX 1070 beside an Intel UHD 630 that turned out to be the
integrated one — the same job took 130 s there against 15 s on the GeForce,
**8.6× slower**, with nothing in the output to say why.

To override the automatic choice, name the GPU you want:

```
abchapterize --use-gpu gtx audiobook.m4b     # the discrete card
abchapterize --use-gpu uhd audiobook.m4b     # the integrated one
```

The text is matched case-insensitively against any part of the device name,
so `gtx`, `nvidia`, `geforce` and `1070` all pick the same card. `--list-gpus`
prints the names this machine reports:

```
> abchapterize --list-gpus
Vulkan GPUs on this machine:
  0: Intel(R) UHD Graphics 630 (integrated)
  1: NVIDIA GeForce GTX 1070 (discrete)
```

A request matching no GPU, or more than one, stops the run and lists what is
actually available — it never quietly falls back to a different card than the
one you asked for. The startup line names the GPU in use either way, so a
wrong choice is visible in the first line of output rather than only in the
wall clock.

Two situations still need a decision from you, because there is no sensible
way to guess: several discrete cards, and no discrete card at all. In both,
the runtime's own default applies until you pass `--use-gpu` — but the
startup line still names the device it ended up on.

That last part is worth a glance on any unfamiliar machine, because "Vulkan
backend" alone does not mean a GPU is doing the work. A software rasterizer
such as Mesa's `llvmpipe` is a genuine Vulkan device and will be used quite
happily if it is the only one present — a container or a WSL2 distro without
GPU passthrough is the usual way to end up there. If the startup line names
`llvmpipe`, the run is on the CPU by a slower route than `--cpu-only` would
take, and the fix is on the driver side rather than anything here.

**On names rather than numbers.** The indices `--list-gpus` prints are
positions in this machine's Vulkan enumeration, and they are less stable than
they look: on the test machine above, an interactive desktop session and a
remote SSH session enumerated the two cards in *opposite* order. An index
noted down once can therefore be wrong the next time you log in differently,
which is why `--use-gpu` matches on the name and resolves it afresh on every
run. A bare number is accepted as an index — for the rare machine with two
identical cards, where names cannot tell them apart — but only if such an
index exists, so `--use-gpu 1070` still means the GeForce.

The Vulkan runtime's own `GGML_VK_VISIBLE_DEVICES` environment variable still
works and is left alone if you set it, but it hides GPUs from the backend and
renumbers the rest, so combining it with `--use-gpu` is rejected rather than
guessed at. `--use-gpu` replaces it. A run that defers to the variable says so
in its startup line, and still names the GPU it leaves in charge:

```
Whisper model "turbo" loaded (Vulkan backend on NVIDIA GeForce GTX 1070 via GGML_VK_VISIBLE_DEVICES=1), 3 file(s) to process.
```

If the value does not resolve to one of the GPUs `--list-gpus` shows, the line
names the variable alone — the device is then whatever the runtime makes of it.

### A note on CUDA

CUDA is preferred over Vulkan whenever it can actually be loaded, but that
takes more than an NVIDIA card being present:

- The CUDA runtime libraries have to be installed on the machine. The
  bundled native links against `cublas64_13.dll` (CUDA 13) and does not ship
  it, so without a matching CUDA runtime installation the library fails to
  load.
- The card has to be recent enough. The bundled native carries kernels for
  Ampere, Ada and Blackwell (`sm_86`, `sm_89`, `sm_120`, `sm_121`) only.
  Older cards — Pascal, for instance — are not covered.

Neither case is an error: the selection quietly falls through to Vulkan,
which supports far older hardware and is usually a perfectly good outcome.
Just don't assume "NVIDIA card" means "CUDA backend" — check the startup
line, which names the backend that actually loaded.

`--use-gpu` and `--list-gpus` work on the Vulkan side, where multi-GPU
machines actually cause trouble. When CUDA loads, it keeps its own device 0;
a machine with several CUDA cards is rare enough not to have earned an option
of its own yet.

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
chapter state) that is replaced by a one-line result when the file is
done:

```
My Audiobook.m4b: 23 chapter(s) written (1-23) + intro
```

The chapter state reads `----` until the first mark is confirmed (all
of pass 1, where nothing can change anyway), then shows the highest
chapter number found so far — `ch 6` — followed by a bracket holding two
counts, either of which is left out when it is zero:

- a negative count of lower chapters that are still unconfirmed (the gaps
  pass 3 would have to chase during detection, or a mark that failed its
  check during `--verify`);
- a positive count of the extra marks found — prologue, epilogue and
  [`--custom`](#custom-marks) marks. The intro mark is not among them; it is
  added when the file is written, not detected.

So `ch 6(-2+1)` means chapter 6 is marked, two chapters below it are still
outstanding, and one extra mark has been found. An extra mark found before
the first chapter reads `ch 0(+1)`, and under
[`--ignore-chapter-numbers`](#detecting-chapters-without-believing-their-numbers),
where every mark is an
announcement without a number, the state shows the plain total instead:
`mk 12`. Pass 2's percentage follows the probe position within the
file's play time, so it can move nonlinearly — and, briefly, backwards,
when a sequence gap makes the detector re-probe earlier candidates.

Once detection finishes, the bar switches to a final `Muxing...` phase while
the chapter markings are written into the file — worth watching on a large
file or a slow disk, since this step still has to shuffle the whole file's
data through ffmpeg even though it only copies streams rather than
re-encoding them.

Ahead of the file name, a coarse `H:MM` timer (whole minutes only, e.g.
`1:05`) shows how long that file has been in progress, so a run stuck on
one book is easy to spot at a glance.

There is one bar, because there is one file in flight: it is replaced by that
file's one-line result as soon as it finishes, and the next file's bar takes its
place. The line is truncated to the terminal's width, and a resize is picked up
automatically on the next refresh.

Progress bars are only drawn on an interactive console; when the output is
redirected (pipe, log file), they are suppressed automatically.

**`--quiet`** drops the per-file lines too; only warnings and errors appear.
Combine with `--summary` for a batch run that prints totals at the end.

**`--no-bar`** keeps the per-file result lines but prints them in the
timestamped log format instead of drawing bars — the right choice for CI
logs.

The bar is drawn in color where the terminal supports it: the bar fill and the
file name in white, the separators and brackets in dark grey, the percentage
and the timer in cyan, the phase in a darker cyan, and the chapter count in
dark green — grey while it is still `----`, and with the bracketed count of
missing chapters in dark red, since that is the one part of the line reporting
something outstanding.

The `--summary` block is colored on the same principle: prose in white,
brackets in dark grey, and every measured value in cyan together with its unit,
so `1.52 s` and `3.7%` each read as one figure. The book titles in its closing
listings are dark cyan, whole — brackets, digits and all, so a name like
`Der Fall (Teil 2).m4b` reads as one thing. **`--color never`** turns all
of it off, **`--color always`** forces it on where it was not detected. Log
lines and per-file result lines are never colored, and a `--log-file` always
receives plain text.

**`--verbose`** additionally logs, with a `[HH:mm:ss]` timestamp and the file
name, everything the pipeline does:

- probe result (duration, codec/profile, existing chapter marks),
- the silence count of pass 1,
- each probe window and pass-3 chunk as a `<length>@<time>` header line,
- every accepted chapter detection with the exact mark position, confidence
  and the loudness of the audio right at that position (e.g. `-58.3 dBFS`;
  `-inf dBFS` for pure digital silence) — a figure close to silence means the
  mark landed in a real pause, a loud one that it landed mid-word or inside
  music and is worth a listen. Flagged `LOW CONFIDENCE` below 0.5, plus a
  `still missing:` list of any earlier chapter numbers not detected yet,
- every chapter number that was heard but *not* turned into a mark, with the
  reason: one that does not top the last number accepted (`skipped chapter 3 at
  1:12:04.20 - not above the last accepted chapter 7 (in-text mention?)`), one
  above the `--max-chapter-number` cap, or one with no usable silence in front
  of it to anchor a mark to — in which case the line names the silence it did
  find and how it fell short. Usually this is the tool correctly ignoring a
  passing reference in the narration, but if a chapter really is missing, the
  line tells you the recognizer *did* hear it, which is a very different
  problem from it never being heard at all. The anchor reasons in particular
  tend to point straight at `--min-silence-length`,
- the gap re-probes and sub-floor sweeps of pass 2.5, the regions transcribed
  in pass 3, the shifted re-reads of pass 3.5, and when each pass finishes,
- once the file is done, a `stats -` line: the shortest silence and (when
  the VAD pre-pass ran, which it does by default) longest jingle found
  before a chapter — each also given as
  an "inter-chapter" figure that ignores chapter 1's often-unrepresentative
  lead-in — how much audio was fed to Whisper (with its share of the file's
  run length), and Whisper's transcription speed as a percentage of real time.

**`--verbose-transcripts`** (`-T`) adds, after each header line, the full
Whisper transcript for that window — every segment with its timings and
confidence (`p=0.87`). This is the primary diagnosis tool: it shows verbatim
what the recognizer heard, so you can see *why* an announcement was missed and
adjust `--chapter-phrase`, `--min-silence-length` or the model.

**`--log-file <path>`** (`-o`) sends that whole log to a file rather than the
console, and switches it on in the first place — `-o run.log` alone logs
everything `--verbose` would have printed, `-o run.log -T` adds the
transcripts. The console keeps its progress bar and per-file result lines,
which the file also receives, so a run stays watchable while the detail is
kept for later. Timestamps in the file carry the date as well, and each run
appends a header and a footer line rather than replacing what is already
there, so one log can collect a whole library's worth of runs.

**`--debug`** writes a separate log for *each* processed file, named after it
(`book.m4b.debug.log`), holding everything the ordinary log carries plus the
raw material behind it: the settings and probe result the run worked from,
every silence found (including the short ones `--min-silence-length` rejects),
every voice-activity segment and non-speech region, every Whisper transcript
segment by segment, and the mark-refinement probes that appear nowhere else.
It switches logging on by itself and leaves the console alone, so
`--debug` on its own gives you a quiet run and a full file. Expect a few MB
per audiobook.

This is the option to reach for when a single mark landed somewhere
inexplicable and `-T` has not settled why — everything needed to reconstruct
the decision after the fact is in there, which is otherwise a matter of
re-running the same hour of decoding by hand. It is meant for reporting a
problem rather than for daily use, and the file it produces is written for
whoever reads the source.

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
(lower `-n`), or the jingle runs longer than the default 45 s ceiling (raise
`--max-jingle-length`).

**Chapters found but some are missing** — if the missing ones are announced
without a preceding pause, pass 3 usually catches them automatically. If a
gap remains, the partial marks are written and the file is renamed with a
`.missing-marks-…` tag (see the warning); simply running the tool again over
such a file resumes it automatically, re-probing only the still-tagged
gap(s). If that still doesn't find them, try a heavier `--pass3-model` (e.g.
`large`) — which also lets pass 2.5 sweep the gap for pauses shorter than
`--min-silence-length` allows — a better `--model`, or a lower
`--min-silence-length` before resuming again.

**A "chapter" was detected that isn't one** — in-text mentions are filtered
by the ordering heuristics, but a phrase like "chapter twelve" right after a
long pause can fool the tool. Use `--backup`, inspect the result, and
`--revert` if needed; a regexp phrase (`-c "/^\s*chapter (\d+)/"`) can help
with stubborn cases.

**A wildly wrong chapter number appeared, and everything below it is now
"missing"** — Whisper misheard a number (lighter models such as `tiny` and
`base` are prone to this), and everything between the last real chapter and
that number counts as a gap. Set `--max-chapter-number` to roughly the book's
real chapter count and the bogus number is thrown away as it is found. If the
run already left the file tagged, note that a tag naming more than ten
missing chapters is shortened to a plain `.missing-marks` and is *not*
resumed automatically — rerun it with `--force` (and the new option) once you
know what went wrong.

**It's slow** — see the speed knobs: `--min-silence-length` (fewer probes),
`--max-jingle-length` (smaller probe windows, or `0` if there's no jingle at
all), a smaller `--model` (or a
smaller `--pass3-model` if only the gap-filling pass drags). Check that the
startup line reports a GPU backend, not CPU.

**Model download fails** — the error message includes manual installation
steps; see [section 8](#8-whisper-models). A checksum-mismatch error means
the downloaded file did not match the pinned SHA-256/SHA3-256 digests and
was rejected before use - retry the download; if it keeps failing, this may
indicate a network issue corrupting the transfer, or the Hugging Face
repository serving something unexpected.

**ffmpeg not found** — see [section 10](#10-ffmpeg-requirements-and-discovery);
the quickest fix is setting `FFMPEG_DIR`.

**File skipped as xHE-AAC** — see [section 11](#11-xhe-aac-usac-files).

**A single mark landed somewhere inexplicable** — the one case `--verbose`
and `-T` usually cannot settle on their own, because the reasoning behind a
mark's exact position draws on silences, voice activity and short probe
transcriptions that the ordinary log does not carry. Rerun that one file with
`--debug` and attach the `.debug.log` it leaves beside it
([section 12](#12-output-progress-and-logging)).
