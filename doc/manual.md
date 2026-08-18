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

Detection opens with one scan of the whole file that transcribes nothing, and
then puts as much of the file through Whisper as it has to: the probing pass
every file pays for, and up to three further passes that run only where chapters
are still missing. This section is an overview of what each pass does; the
machinery that keeps it accurate and fast — how probe windows are sized and
stitched together word-safely, how each mark is pinpointed to its exact
position, the transcript caching and the self-tuning that cut the number of
Whisper calls — is documented in the source. Only what affects using the tool
is covered here.

### Pass 1 — silence scan (and VAD pre-pass)

ffmpeg's `silencedetect` filter finds every silence of at least half a second
below `--noise-floor` dBFS (normally −35), in one quick decode pass over the
whole file — short ones as well as long, because mark placement and window seams
are anchored to the whole list. Which of them are worth probing is a separate
question, decided afterwards by `--min-silence-length` (default, and starting
point with `auto`: 1.5 s). Chapter announcements in audiobooks practically always
follow such a pause. If the scan ends prematurely (e.g. because of a damaged file), the file
is aborted with an error instead of silently reporting "no chapters".

Before that scan, a few short excerpts from across the file are decoded to
check that the threshold suits this particular recording — see
[`--noise-floor`](#detection-behaviour). It costs about a second even on a
long book, and on an ordinary master it confirms the usual −35 dBFS and
changes nothing.

`silencedetect` is amplitude-only: a jingle (a short music sting) that abuts
the narration with no detectable gap around it produces no silence at all, so
it never gives pass 2 a candidate near that transition. By default, a
voice-activity detection pre-pass runs over the same decode using a bundled
model — [Silero VAD](https://github.com/snakers4/silero-vad), MIT-licensed,
embedded in the executable (~2.2 MB, no separate download; see
[THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)). Music reads as
non-speech to a speech detector, the same as silence, so a jingle shows up as
a non-speech region flanked by speech even when there is no amplitude gap
around it, and pass 2 gets a candidate at every jingle it finds. There is no way
to switch this off: what the pre-pass measures — where each jingle starts and
where the speech behind it resumes — is what probe windows are cut to and what
mark placement is measured against, so a run without it would be a different
tool. A book with no music simply yields no jingles and costs the scan itself,
which is a fraction of one pass over the file.

### Pass 2 — probing

A short window of audio is transcribed with Whisper at the start of the file and
at every place a chapter could plausibly be announced. What made a place a
candidate also decides where its window opens and how far it runs:

- **A pause.** The announcement is expected directly after it, so the window
  opens a few seconds before the pause ends — enough lead-in for the recognizer
  to settle — and runs about twenty seconds past it.
- **A jingle**, whenever the VAD pre-pass ran (the default; see
  [Pass 1](#pass-1--silence-scan-and-vad-pre-pass)). The announcement is
  expected in the first speech behind the music, so the window opens shortly
  before that speech begins. Where the pre-pass heard a brief sound *inside* the
  music, the announcement may be buried in it, and the window covers the whole
  jingle as well.
- **A pause that merely leads into a jingle** is not a candidate of its own.
  Everything a window from there would hear belongs to the jingle behind it,
  and the jingle listens for the same announcement from a better place.

No setting decides how wide any of these windows is — each is cut to its own
candidate, so a book's jingles cost only the jingles' own probes however long
they run. How far back the tool believes music can reach, where that question
comes up at all (see [Pass 3](#pass-3--gap-filling-only-when-needed) and
`--mark-before-jingle`), is measured from the file's own jingles rather than
assumed.

Each transcript is matched against the chapter phrase (see `--chapter-phrase`),
and the chapter number is parsed from digits, Roman numerals or number words
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
says so, naming the recognizer it used.

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
measured to within a tenth of a second. Where a pause of at least half a second
runs up to the announcement, that measurement is replaced by a direct one: the
audio from the end of the pause onward is examined for the point where sound
actually resumes, and the mark is set `--mark-lead` seconds ahead of *that*.
Speech recognition is remarkably good at reading a word whose beginning has
been cut off, which makes it a poor judge of where the word began; the
waveform is not. The upshot is that a mark carries the full lead-in you asked
for rather than whatever was left of it. Books that play a jingle straight
into the announcement have no such pause and are marked as before.
Hearing the phrase at the mark is not by itself taken as proof that the
mark is right — a jingle is not transcribed at all, so a mark several seconds
inside one hears the announcement just as clearly as a mark sitting on it. A
mark that cannot be confirmed this way is searched for once more, in full, through
the model `--pass3-model` names where that is a better one than `--model` — a
quietly-spoken announcement inside a jingle can be lost on the smaller model and
plain to the larger. Only when neither hears it is the mark left as originally
placed rather than guessed at. A corrected mark that would land in the middle of
speech is refused the same way, and keeps the position it had before the
correction: a mark belongs in a pause or in a chapter's music, and only a short
gap between the announcement and whatever was said just before it — a recording
that names its reader before each chapter, say — can lead the search into the
earlier words. Finally, whatever mark results —
confirmed, corrected, or left as is — is nudged up to 0.15 seconds earlier to
the quietest point in that stretch, but only when doing so is a clear (at
least 6 dB) improvement over the mark's own position; a mark is never moved
later. This keeps a player from starting playback abruptly mid-sound (an
audible "plop") without ever risking eating into the announcement itself.
This costs a handful of extra Whisper transcriptions per chapter on top of pass
2's own probe — a mark that already sits close to its announcement is the
quickest case, one left seconds away from it the slowest. `--quick-marks`/`-Q`
skips the whole layer when that time matters more than the last few tenths of a
second of accuracy (the machinery is documented in the source).

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

Only chapters found at a *pause* teach it. One found at a jingle is left out:
the pause in front of a jingle is the run-up to the music, not the break
between two chapters, and it is routinely a different length — so learning
from it would move the threshold away from the breaks that still have to be
found. It also means a book whose chapters all open with a jingle never
narrows its threshold at all, which costs nothing: those chapters are being
found at the jingles, not at the pauses.

That measurement also runs the other way. When a book's own chapter breaks
turn out *shorter* than the 1.5 seconds probing started at — a narrator whose
pauses simply sit under the default — the figure settles below the starting
point, as low as 0.8 seconds.

Where a chapter is missing, that shorter figure is acted on twice. A gap in the
numbering re-reads its own stretch with the pauses down to it, so a chapter
behind a pause the run was never willing to probe is often recovered on the spot
and on the cheap model. And when pass 2 is done, the gaps still open are swept
for those pauses once more — a tenth of a second at a time, longest first, each
slice taking only the time it takes, so a gap dense with short pauses still gets
the slice most likely to hold the chapter rather than being given up as too slow
to attempt in one piece. The sweep stops as soon as the gap closes.

**The sweep runs off the gap, not off the measurement.** A book whose chapters
all open with music measures nothing at all (see above), and it is exactly such a
book — jingles on most chapters, a bare pause in front of the one that went
missing — that the sweep exists for. So a gap is reason enough on its own; where
nothing was measured, the pauses are swept down to the shortest length this run
would ever have believed in. Only gaps, and only within a budget, so a chapter
that would otherwise have cost a full pass 3 is usually recovered for a handful
of probes instead. Nothing else about pass 2 changes: the pauses it was willing
to probe in the first place are the same either way, and an explicit
`--min-silence-length` value is honoured to the second — nothing below it is ever
looked at. See the [`-n` reference](#detection-behaviour) for the knob itself.

A chapter turning up out of sequence puts the whole stretch since the previous
chapter back in question, not just the candidates that were passed over — and
that second look is a **re-reading rather than a wider search**. Every pause and
every jingle between the two chapters becomes a candidate again — including the
ones the first look ruled out because a jingle was thought to cover them, and the
ones that were never candidates at all for being shorter than the run demanded —
and each is framed a little differently from the first time: the window opens later and stops
sooner, which is what makes the recognizer read the same audio afresh instead of
returning the answer it already gave. The retry stops as soon as the gap is
closed. When there is nothing to retry — no candidate at all sits between the two
chapters — `--verbose` says so, and the gap goes straight to pass 3.

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
does not find falls through to pass 3 immediately afterward. The re-probe stops
the moment the last of the gap's missing chapters turns up: whatever is left of
the gap behind it is a chapter's worth of audio with no announcement in it.

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

A **detected prologue settles that question by itself**: a book's prologue
sits in the file that holds its beginning, so a file that has one is expected
to start at chapter 1, and anything missing below the first chapter found is
searched for exactly as `--expected-start-chapter 1` would have it — and, if
it stays missing, reported as missing. Passing `--expected-start-chapter`
overrules the implication, which is what a split-book part carrying its own
prologue wants (`-e 12`).

A chapter missing *after* the last one found is the one case none of this can
notice: a gap is a hole in the number sequence, which needs a known chapter on
either side of it, and there is nothing above the last one to compare against.
The trailing scan closes it by transcribing that stretch anyway — from the last
chapter found through to the end of the file — and it runs by default, at the
price of doing so on every file, every run, whether or not anything is wrong.
`--no-trailing-scan` switches it off. `--chapter-count` is the informed
alternative: told how many numbered chapters the book has, the run knows which
numbers are still owed, hunts only those, and does nothing at all when none are 
— so giving a count suppresses the blind scan entirely. See
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
The blind trailing scan is the exception in the other direction — it is read once
and never twice, because it already runs on every file and reading audio nothing
suspects a second time would double that standing cost. A trailing hunt that does
know what it is after (`--chapter-count`) goes by the same rule the gaps do.

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
[`--max-chapter-number`](#detection-safety-nets) for the most
common cause.

A later run over a *numbered* tagged file picks it up automatically (unless
`--force` or `--ignore-chapter-numbers` is given): the chapters already
committed are trusted outright,
and only the still-tagged gap(s) get their own pass 2 and, if needed, pass 3,
exactly as after a failed `--verify` (see
[`--verify`](#detection-safety-nets)). If that completes the
sequence, the file is renamed back to its original name; if a gap is still
unresolved, it is re-tagged with the (possibly shorter) remaining list.
`--force` bypasses this and redoes the file from scratch instead, discarding
every existing mark including the partial ones.

An unnumbered tagged file is never picked up automatically, so redoing it means
`--force` (or `--verify`, or a `--max-chapters` low enough to condemn its
partial marks). Whichever way it happens, *any* run that ends with a
complete chapter sequence takes the tag off again and gives the file its own
name back — the tag records work still to be done, and there is none left.
With `--debug`, the log written beside the file follows it to that name,
replacing any log already sitting there from the run that left the tag.

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
ordinary prose — and sometimes inside longer words, Italian "riepilogo"
containing "epilogo" — each is only accepted where it can plausibly be an
announcement. Three things have to hold: the prologue is only looked for
*before* the first chapter has been found and the epilogue only *after* at
least one has; the match must be preceded by a real pause — roughly a second of
silence or jingle — since a heading is spoken at a section boundary and never
mid-sentence;
and within that window the last occurrence wins — front matter frequently lists
what is coming ("read by …, contains a prologue") before the narrator actually
announces it.

The epilogue is held to one more rule, checked once at the very end of the
file: it has to **follow the book's last chapter**. Nothing else can be an
epilogue — a match between two chapters is the word turning up in prose, or an
inner part of an omnibus ending, and either way the mark would be wrong. Such a
mark is dropped, and `--verbose` says so. If your books really do have a section
there and you want it marked, that is what `--custom` is for: a mapping like
`--custom "/epilog/:Zwischenspiel"` claims exactly the same announcements and is
bound by no position at all. A mapping of yours that matches the same words as
the built-in phrase also inherits a dropped epilogue mark, so the mark stays
where it was under your own title.

Nothing is required of what *follows* the announcement: narrators routinely
read straight on from "Epilogue" into the epilogue's first sentence.

What may follow it is a *second* line of the same heading — a year, a date, a
place — and where that line is a number, it can look exactly like a chapter
announcement. A chapter mark landing within a few seconds of a prologue,
epilogue or `--custom` mark, and carrying a number that fits nowhere in the
book's sequence, is therefore taken as part of that same heading and dropped. A
real chapter that begins right after a short prologue keeps its mark, since its
number continues the sequence, and a book's first chapter is never dropped this
way.

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
of them — and it is exempt from the surrounding-pause requirement they are
held to. A custom mapping names whatever recurring element you say it does,
wherever you say it is.

What it is *not* is a full-text search: a custom phrase has to be **announced**,
and is held to exactly the same standard as a chapter phrase for deciding
whether it was — it has to turn up inside one of the windows probing actually
looks at, and those are anchored on the file's own pauses and jingles. A
narrator mentioning a timeline in the middle of a paragraph gets no mark; the
narrator announcing "Zeittafel" at a section boundary does. Titles may pull text
out of the phrase's own capturing groups — `${name}`, `${number}`,
`$roman{number}` and the rest of it are in
[Titles](#titles-what-a-mark-is-called).

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

#### Hints: telling a mapping what kind of thing it names

By default a `--custom` mapping is as unrestricted as it can be: it applies to
every file, matches anywhere in it, and produces a mark for every occurrence.
That is the right default for the "Zwischenspiel" case it was built for, and
the wrong one for a mapping naming something a book has exactly once, or only
at one end of itself.

The `[...]` tag that already carried a language code takes a comma-separated
list, and any of these **hints** may sit in it alongside the code:

| Hint | Short form | What it does |
| --- | --- | --- |
| `before-first-chapter` | `before-first` | Matches only before the first chapter is found. |
| `after-first-chapter` | `after-first` | Matches only once a chapter has been found. |
| `after-last-chapter` | `after-last` | Only a match after the book's *last* chapter is kept. |
| `once` | | At most one mark for this mapping per file. |
| `max=<n>` | | At most `<n>` marks for this mapping per file. |

```
--custom "[de,before-first-chapter,once]/vorwort/:Vorwort"
--custom "[after-last-chapter,once]/^nachwort/:Nachwort"
--custom "[max=3]/zwischenspiel/:Zwischenspiel"
```

**To require a real pause in front of a match, write `^` at the start of the
phrase**, as the second example does. There is no hint for it, deliberately:
`^` already says exactly this — see "`^` and `$` — asking for the pauses" under
The phrase syntax — and one demand with two spellings is one spelling too many.

Nothing here is new machinery. The built-in prologue *is*
`[before-first-chapter,once]` with a `^`, and the built-in epilogue *is*
`[after-last-chapter,once]` with one; what the hints do is let a mapping ask for
the same treatment. A mapping without a tag, or with a tag naming only a
language, keeps exactly the behaviour it had before hints existed.

Points worth knowing:

- **`once` keeps the *last* match, not the first.** Front matter routinely
  mentions what is coming ("…gelesen von…; Vorwort") before the narrator
  actually announces it, so the later of two matches inside the scope is the
  real one. It follows that `once` on its own can never end a search early —
  only a scope that closes can. `max=<n>` works the other way round, keeping
  the first `<n>` and dropping the rest, which is why `max=1` is rejected with
  an error pointing at `once` rather than quietly meaning something else.
- **A leading `^` is the check the prologue and epilogue always get**: the match
  must sit behind a real pause, which is what tells a heading read aloud from
  the same word buried in a sentence. `--custom` does not get it by default,
  because a mapping names whatever recurring element the user says it does, at
  whatever position — write the `^` when you want it.
- **`after-last-chapter` is applied at the end of the run**, not while probing.
  Which chapter is the last one is unknown until every pass has finished, so a
  match mid-book is heard, marked and then dropped — it buys precision, not
  time. The other two positions are free, being filters applied before a mark
  is ever placed.
- **A match dropped for being out of scope is noted once per mapping** under
  `--verbose`, and not per occurrence: an "epilogue" in the middle of a book is
  an ordinary word, and one line per match would drown the log.
- **A bracket run only counts as a tag when something in it is recognized.**
  Whisper writes bracketed non-speech tags into its transcripts — `[Musik]`,
  `[Abspann]`, `[BLANK_AUDIO]` — so `--custom "[Musik]:Zwischenmusik"` is a
  plausible mapping and goes on matching those words. A typo *beside* a good
  keyword (`[once,headnig]`) is an error, since one token was recognized. The
  only residual is a phrase that is itself exactly a bracketed keyword.
- The same tag on `--chapter-phrase`, `--chapter-title` and the other localized
  options names a language and nothing else; a hint there is an error rather
  than silently ignored.

### Named marks that land beside a chapter

A named mark — prologue, epilogue or `--custom` — sitting within
`--named-mark-distance` seconds of a chapter mark (10 by default) is not written
as an entry of its own. The chapter keeps its position, and the named mark
contributes its title in brackets:

```
0:00:00.000  Intro
2:14:07.500  Chapter 10 (Interlude)
```

Two entries a few seconds apart are worse than one: scrubbing to a chapter lands
the listener either in the tail of the previous section or a little way into the
new one, depending on which of the two they hit, and nobody asked for that
choice. Nothing is lost — the title survives — and the chapter wins the
position, being the mark people navigate by. Several named marks crowding one
chapter are appended in file order, separated by commas.

This is also what settles a chapter announcement and a named phrase spoken in
the same breath ("Chapter ten. Interlude."), which are found by two separate
searches and can only be compared once both have their final positions.

`--named-mark-distance 0` switches the whole thing off and writes every mark
separately, however close together they fall.

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

Use it for books whose numbering the tool cannot make sense of: one made of
several novels bound together, one that announces "Chapter" and then simply
reads on, or one whose parts restart their count in a way the automatic
handling below cannot follow.

An ordinary run will usually tell you when a book wants this. Announcements
numbered below the sequence are heard and understood, and then dropped for
repeating numbers already used; when enough of them go that way without adding
up to a new part, the file's summary line says how many were skipped and
suggests this option.

The prologue and epilogue keep their usual positional rules — the prologue
before the first chapter heard, the epilogue after it — and `--custom` marks
behave as always. The per-file limit of 100 custom marks does not apply to
chapter announcements.

The options that reason in chapter numbers are rejected rather than silently
ignored: `--pass3-model`, `--expected-start-chapter`, `--max-chapter-number`,
`--chapter-count` and `--verify`. `--chapter-phrase` and `--chapter-title` remain
perfectly useful and are accepted.

A file another run already tagged `.missing-marks-…` is not picked up either:
the tag is a statement about chapter numbers, which this run forms no opinion
about, so such a file is treated like any other one already carrying marks and
skipped unless `--force` asks for a detection from scratch. The blind scan after
the last chapter does not run here either — it lives in pass 3, and pass 3 does
not run at all.

### Books that count from one again in every part

Some books are divided into parts, and each part starts its chapters over at
one. ABChapterize follows that by itself: when it hears three consecutive
chapters that sit below the numbering it has been building — "one", "two",
"three" again after the last chapter of the part before — it accepts that a
new part has begun and marks everything from there under the new count.
Nothing is decided on less evidence than that, because a single announcement
below the sequence is far more likely to be prose mentioning an earlier
chapter ("as I said in chapter three") than the start of a part; those are
still passed over, as they always were.

What changes in the output is the titles. Every chapter of such a file carries
its part, including the first part's:

```
Intro
Part 1 - Chapter 1
…
Part 1 - Chapter 15
Part 2 - Chapter 1
…
```

The word is localized by `--lang` and can be set with `--part-title`. A file
with a single chapter sequence — virtually every book — is titled exactly as
before, with no part prefix anywhere.

Everything else follows the parts too. Each part's numbering is checked, gap-
hunted and reported on its own, so a chapter missing from part two is looked
for between part two's own chapters and never confused with the part-one
chapter of the same number; the boundary itself is not treated as a gap; and
`--chapter-count`, if given, describes the last part. The file's summary line
names the parts and the range each one covers. If a part is found to start
above one, its missing opening chapters are searched for like any other gap —
no `--expected-start-chapter` is needed for that, since counting from one is
what identified the part in the first place.

Two things are worth knowing. Marks written for such a file can be read back:
`--verify` and an interrupted run's auto-resume both understand the part prefix,
so a file this tool marked is picked up correctly. And if a book divides itself
in a way this cannot follow — parts of one or two chapters, say, or a first
part whose opening chapters were all missed — the fallback is
[`--ignore-chapter-numbers`](#detecting-chapters-without-believing-their-numbers),
which marks every announcement it hears and never consults a number.

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
   - with `--backup` and no `.bak` there yet: the original is renamed to
     `<name>.<ext>.bak`, then the new file takes its place (with rollback if
     that rename fails);
   - without `--backup`, and with it when a `.bak` from an earlier run is
     already there: the original is parked as `<name>.<ext>.abchapterize.orig`, the
     new file takes its place, and only then is the parked original deleted
     (again with rollback on failure). An existing backup is never overwritten
     — see [`--backup`](#safety-and-undo).

Temporary files (`*.abchapterize.*`) are cleaned up afterwards and are always
excluded from directory scans. If a power failure ever leaves one behind,
check which of the two kinds it is before deleting anything:

- `<name>.<ext>.abchapterize.tmp<ext>` is the half-written replacement. The
  audiobook next to it is untouched, so this one can simply be deleted.
- `<name>.<ext>.abchapterize.orig` **is your original**, parked for the moment
  it takes the finished file to move into its place (step 4 above, without
  `--backup`). If `<name>.<ext>` is missing, rename this one back to it; if the
  audiobook is sitting there complete, the parked copy has done its job and can
  go. Either way, look before deleting — or let
  [`--cleanup`](#cleaning-up-after-a-run) look for you, which is exactly the
  distinction it is built around.

`abchapterize -R <target>` (`--revert`) undoes a `--backup` run: for every
supported audio file with an added `.bak` suffix, the current file is deleted
and the backup renamed back. `--revert` can be combined with `--recurse`, with
`--filter` (the filter then selects which backups are restored) and with the
output options (`--quiet`, `--summary`), but with no detection or safety
options. To undo everything else a run leaves behind as well — its logs, its
name tags, its leftovers — see
[Cleaning up after a run](#cleaning-up-after-a-run).

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
abchapterize --cleanup [--revert] [--yes] [--recurse] [--filter <f>] <file-or-directory>...
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

  The filter also applies to `--revert` and `--cleanup` (it selects which backups are
  restored) and to directory scans in general. Under `--revert` the regexp form
  is matched against the backup's own path — the name still ending in `.bak` —
  so do not anchor it at the audio extension; `--cleanup` matches it against the
  audio file's path instead. A single file named directly
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
  `--chapter-phrase`, `--prologue-phrase`, `--epilogue-phrase`, `--chapter-title`,
  `--part-title`, `--intro-title`, `--prologue-title` and `--epilogue-title` (per
  file with `auto`).
  `abchapterize --lang de buch.m4b` finds "Kapitel eins" and writes
  "Kapitel 1" without further options; so does plain `abchapterize buch.m4b`,
  via auto-detection.

`-m`, `--model <name>`
: Whisper model used to find the chapters: `tiny`, `base`, `small`
  (default), `medium`, `turbo` or `large`. Bigger is not better here — this
  model listens to short windows a few seconds long, and the large ones are
  markedly worse at those; see [section 8](#8-whisper-models). `tiny` and
  `base` are not recommended for real audiobooks either. `custom:<path>` uses
  a GGML model file of your own instead — see
  [Using your own model](#using-your-own-model).

`-M`, `--pass3-model <name>`
: Whisper model to use for [pass 3](#pass-3--gap-filling-only-when-needed)
  (gap filling) only; same choices as `--model` including `custom:<path>`.
  Defaults to `turbo`, or to whatever `--model` says if you set that and not
  this — so `-m large` means large throughout rather than large probing and a
  quietly lighter pass 3. Pass 3 transcribes long, naturally framed stretches of
  audio, which is where the heavier models really are the better recognizers.
  Use a lighter model to make pass 3
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

`-n`, `--min-silence-length <seconds|auto>`
: The shortest pause probed as a potential chapter break (0, or 0.1–60,
  default: `auto`). An explicit value is used as given and nothing shorter is
  ever probed; `auto` treats 1.5 seconds as the starting point instead. Either
  way this governs *probing* alone — pass 1's scan keeps shorter silences too,
  and mark placement and refinement are anchored to them (see the note on `0`
  below, which spells that out). By default (`auto`), pass 2
  self-tightens the probing threshold to 75% of the *shortest* anchor
  silence observed so far as chapters are found at a pause (set at the second
  such mark, only ever lowered after that; chapters found at a jingle teach it
  nothing — see [Pass 2](#pass-2--probing)), re-probing everything it skipped
  whenever a sequence gap turns up, so far fewer Whisper probes are needed
  without a fixed guess. Should that figure come out *below* the 1.5-second
  starting point — a narrator whose chapter breaks are shorter than the
  default assumes — pass 2 sweeps the gaps in the numbering for pauses down to
  0.8 seconds before the later passes are called in; see
  [Pass 2 — probing](#pass-2--probing). An explicit
  numeric value disables this and probes every silence at or above it
  instead; this is still the main manual speed knob if `auto`'s heuristic
  doesn't suit a particular audiobook: if the pauses are unusually generous
  and consistent, `-n 2.5` can cut the number of probes further still, but
  chapters go missing if it's set too high. Set it a little too high and a
  heavier `--pass3-model` will usually rescue the run anyway: a gap it leaves
  behind is swept for pauses down to half a second under whatever this says
  (see [Pass 2.5](#pass-25--cheap-gap-re-probe-only-with-a-heavier---pass3-model)).

  **`0` switches silence-triggered probing off entirely**, leaving only the
  jingles the voice-activity pre-pass finds. For a book whose every chapter
  opens with one, that is the largest saving available anywhere in this tool:
  the hundreds of ordinary in-narration pauses that each cost a Whisper probe
  simply stop being candidates, and every jingle becomes one instead — including
  the ones led by a silence, which are normally left to that silence's own
  candidate. For a book whose chapters do not open with a jingle it removes the
  only way of finding them, which is why it is not a default.

  What `0` does **not** switch off is the silence scan itself. Pass 1 still runs
  and still keeps every silence it finds: window seams snap to them, transcript
  timestamps are corrected against them, and mark placement and refinement are
  anchored to them. Turning that off would make every mark worse rather than
  merely find fewer of them — so a chapter found in this mode lands in exactly
  the same place it would in an ordinary run. `--verbose` says so explicitly,
  reporting how many silences were found and that none were probed.

`--noise-floor <dBFS|auto>`
: How quiet audio has to be before it counts as a pause at all, in dBFS
  (-90 to -5, default: `auto`). Where `--min-silence-length` above is about
  *how long* a pause has to be, this is about *how quiet* — the other half of
  the same question, and the one that has no answer on the command line until
  now. Levels are negative because 0 dBFS is a full-scale signal. No short form.

  On an ordinary audiobook there is a wide gap between the room tone in the
  pauses and the narration itself, and the usual threshold of -35 dBFS sits in
  the middle of it with room to spare either side. `auto` samples a few short
  excerpts from across each file before the silence scan, works out where that
  file's own gap actually lies, and moves the threshold **only** if -35 would
  fall outside it — which on a normal master it does not, so the automatic mode
  changes nothing at all and every book behaves exactly as it always has.

  What it is for is the master that is not normal, where no amount of fiddling
  with `--min-silence-length` could help because the problem was never the
  length:

  - **Audible hiss.** If the quiet stretches never drop below -35, no pause is
    ever detected — and with no pause there is nothing to probe, so the file
    yields no chapters at all. The threshold rises to clear the hiss.
  - **A very quietly mastered book.** If the narration itself sits under -35,
    every gap between two words looks like a chapter break and the scan returns
    thousands of candidates, each of which costs a Whisper probe. The threshold
    drops below the narration.

  An explicit level fixes the threshold for the whole run, which is what to
  reach for when a book is known to need one — or to reproduce an older run
  exactly. `--verbose` prints the threshold in use and, under `auto`, the two
  levels it was derived from.

`-j`, `--mark-before-jingle`
: Anchor the chapter mark to the end of the previous
  chapter's actual narration instead of the default fixed `--mark-lead` offset:
  starting from whatever mark default mode
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
  within a tenth of a second — or, where a pause runs up to it, read straight off
  the waveform as the point where sound resumes — and the mark set `--mark-lead`
  seconds ahead of that. A mark
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

### Phrases and titles

What the run listens for, and what it calls the marks it makes. All of these
accept the per-language `[xx]` tag described in
[The phrase syntax](#the-phrase-syntax), though `--custom` reads it differently:
it restricts a mapping to one language rather than localizing it, a custom
phrase never being translated for you.

`-c`, `--chapter-phrase <p>`
: The word or phrase that announces a chapter (default:
  `/(?:^chapter ()|^() chapter|^chapter)/`, localized by `--lang` — see
  [section 7](#7-languages-and-number-recognition) for every language's
  default). Matching is always case-insensitive. The value is a list of
  **alternatives** separated by `;`, and every one of them is searched for:

  ```
  --chapter-phrase "/se[ck]tion ()/;partie;default"
  ```

  See [The phrase syntax](#the-phrase-syntax) below for the whole of it — what
  an alternative may look like, what `()` matches, how a `[xx]` tag restricts
  one to a language, and what `^` and `$` ask for. The same syntax serves
  `--prologue-phrase`, `--epilogue-phrase` and `--custom`; the title options
  take the `[xx]` tag but hold one value per language rather than a list.

#### The phrase syntax

Everything this tool listens for is written the same way, whether it is the
chapter phrase, the prologue, the epilogue or a `--custom` mapping. A value is
a list of **alternatives** separated by `;`, each of which may be:

| Alternative | Means |
| --- | --- |
| `chapitre` | a plain word, with the chapter number in front of it or behind it |
| `/regexp/` | a regular expression |
| `none` | the number spoken alone, with no phrase at all — chapter phrase only, and only for books really announced that way ([why](#bare-numbers-as-announcements)) |
| `default` | this tool's own phrase for the file's language |

A plain word given as a **chapter** phrase becomes two wordings — the word with
the number behind it and the word with the number in front of it, each asking
for a pause before the announcement — so `--chapter-phrase sektion` finds
"Sektion 5" and "Fünfte Sektion" alike, and a title's `${number}` works under
either. A plain word given anywhere else is exactly the word: nothing is parsed
beside it and nothing is asked for around it.

Every alternative is searched, not just the first that matches something: a
book that says "Kapitel 5" in some places and "Fünftes Kapitel" in others is
served by one value naming both. Where two alternatives match the same words,
the leftmost match wins, and the one written first breaks a tie — but a reading
the chapter numbering cannot accept is put aside and the next one tried, so a
wording claiming words the narrator never said does not cost the chapter.
`none` is always considered last, whatever position it was written in.

An alternation written *inside* an alternative is multiplied out, so that every
alternative ends up as one expression with nothing left to choose between:
`/kapit(?:el|let) ()/` is two alternatives and `/(?:a|b)c(?:d|e)/` is four. That
matters beyond tidiness — the alternative that found an announcement is the one
its position is later confirmed against, and one still offering a choice could
confirm itself on words it never matched. A single alternative that would expand
past 64 wordings is refused. The chapter number's own `()`, and any capturing group,
are left whole.

One more kind of group is left whole, and this one is deliberately in your
hands: a group quantified with `{1}`. Use it when an alternation does not name
two wordings but two *transcriptions* of one wording — the recognizer likes to
wobble between, say, "Première partie" and "1ère partie" for the identical
audio, and `/(?:premi|1).re partie/` written plainly becomes two alternatives,
each of which refuses to confirm a position on the other one's spelling.
Written `/(?:premi|1){1}.re partie/` the choice stays inside one alternative:
the `{1}` changes nothing about what the expression matches, it only declares
"this is one wording, however it comes out spelled", and the announcement's
position is then confirmed under either spelling.

Repeating the option is the same as writing the values as one list, so
`--chapter-phrase a --chapter-phrase b` and `--chapter-phrase "a;b"` mean
exactly the same thing. A `;` inside a regexp is written `\;`.

##### `()` — the chapter number

Inside a regexp, `()` stands for **a number in whatever notation the file's
language has**: digits, digit ordinals, Roman numerals, and the language's own
spelled-out cardinals and ordinals. `/chapter ()/` therefore matches all of

```
Chapter 12      Chapter 12.      Chapter XII.      Chapter twelve
Chapter 12th    Chapter one hundred and five
```

and, being a capturing group, it also **captures** what it matched, so a title
can write it out — see [Titles](#titles-what-a-mark-is-called) below.

A plain-word chapter alternative is shorthand for the shape the built-in phrases
have, minus their bare-word fallback: `partie` is read as
`/(?:^partie ()|^() partie)/`, so both announcement orders are covered —
"partie sept" by the first wording, "septième partie" by the second — and both
capture their number.

Any other capturing group must be **named**, `(?<name>...)`; write `(?:...)`
where you only need brackets for grouping. A non-empty *unnamed* group is read
as a number group with a pattern of your own, which is how
`--chapter-phrase "/part (\d+)/"` has always worked.

##### `^` and `$` — asking for the pauses

A `^` at the start of an alternative and a `$` at its end are **not** anchors.
They say what the audio around a match has to look like before a mark is
written:

- `^` — the announcement must be set off from what precedes it, either by at
  least 0.85 s of real non-speech (silence, or the jingle music a book plays
  into its chapters) **or** by the recognizer having written it as a transcript
  segment of its own. That is what a heading has and a sentence in the middle of
  a paragraph does not, and it is how a `--custom` mapping asks to be treated as
  a heading. The one exception is `none`, a number spoken alone: its
  entire claim to being an announcement is the pause around it, so there the
  pause is required outright.
- `$` — the announcement must be followed by at least 0.3 s of non-speech.
  Only sensible for something spoken alone, such as a bare number: a narrator
  routinely runs straight from a heading into the text behind it.

Both belong to the alternative that carries them, so `/^(?:a|b)$/` asks for
both pauses around either wording while `/(?:^a$|b$)/` asks for both around "a"
and only the trailing one around "b". They mean this at the two edges of an
alternative and nowhere else. A match that fails the check is dropped, and
`--verbose` says so with both measurements and both thresholds.

The built-in chapter phrases carry a `^` and no `$`. The `^` is affordable only
because a transcript segment start satisfies it too: against a pause alone it
would have cost a real chapter of the reference corpus, one whose announcement
follows the previous chapter's last words by 0.64 s — but which the recognizer
wrote as a segment of its own, so it passes.

##### `[xx]` — restricting an alternative to one language

An alternative may open with a two-letter language tag, which is what makes a
batch run over a mixed library workable:

```
--chapter-phrase "[fr]/(?:premi|1).re partie.? chapitre/;/chapter ()/"
--chapter-title  "[fr]Chapitre;[en]Section"
--custom         "[fr]/scène/:Scène;[en]/scene/:Scene"
```

With `--lang auto` each file resolves its own language and then takes **every
untagged alternative plus every alternative tagged for that language**, in the
order they were written. Above, a French file listens for the French wording
*and* for "chapter", while every other file listens for "chapter" alone. A
language the value says nothing about at all — no tag of its own, and no
untagged alternative anywhere in the value — keeps its own built-in phrase,
exactly as if the option had not been given.

`default` pulls that built-in phrase into the list explicitly, so a value can
add to it instead of replacing it:

```
--chapter-phrase "/abschnitt ()/;default"     add a wording, keep the usual one
--chapter-phrase "default;none"               ... and accept a bare number too
--chapter-phrase "a;[de]default;[fr]default"  per language
```

##### Titles: what a mark is called

A `--custom` mapping's title, and the prologue and epilogue titles, may write
out what the phrase captured:

| In the title | Writes |
| --- | --- |
| `${name}` | what the named group `(?<name>...)` captured, as it was written |
| `${number}` | the chapter number **in digits**, whatever notation was spoken |
| `$digits{name}` | the same for any group that holds a number |
| `$roman{name}` | that number as a Roman numeral |
| `$upper{name}`, `$lower{name}`, `$capital{name}` | the captured text, recased |
| `$$` | a literal dollar sign |

```
--custom "/(?<kind>interlude|intermezzo) ()/:$capital{kind} $roman{number}"
```

writes "Intermezzo XIV" for a spoken "intermezzo fourteen". A group that took
no part in the match — because a different alternative matched — writes
nothing, and a conversion asked of a group that holds no number leaves its text
alone.

A dollar sign followed by an ordinary word is ordinary text and needs no
escape; one followed by a **digit** is refused. `$1` used to name a capturing
group, and quietly turning it into text is the one outcome nobody would notice
until the file was written — so name the group and write `${name}` instead.

#### Bare numbers as announcements

**Experimental.** This wording has been calibrated against a single book so far.
It works and it is meant to be used, but check what it produces rather than
trusting it the way you would a phrase-based run, and expect the rules behind it
to keep moving between releases.

`--chapter-phrase none` says a chapter may be announced with no phrase at all:
the narrator simply says "Seventeen." and reads on. What counts as an
announcement is then a **number spoken alone** — one with a pause on either side
of it, rather than one occurring inside a sentence. "Seventeen." is an
announcement; "Seventeen men stood at the gate" is not, and neither is a year, a
price or a house number read out in the prose. A number that ends its sentence
still counts even when the recognizer runs it together with what follows
("Seventeen. He was late again."), which is common and no longer costs the
chapter.

**Give it only to the books that need it.** Being one alternative among others,
`none` invites being added to every run — "then anything numbered gets found" —
and it does not work that way. On a book whose chapters *are* announced by a
phrase, `none` finds nothing the phrase would have missed, and a great deal it
should not have found: a timetable of years in the front matter becomes a run of
chapters numbered by year, a number spoken in dialogue becomes a chapter, and so
does one in the closing pages. Each of those costs more than one wrong mark. A
number far above the real ones sets the sequence's ceiling, so the genuine
chapters behind it stop continuing the sequence; the book can come out looking as
though it holds several parts, numbered from the phantom onwards; and a prologue
can lose its place to a phantom accepted ahead of it. Reach for `none` when a
narrator really does announce chapters by saying nothing but the number, and
leave it off otherwise.

Where a book does both — some chapters announced, the rest merely numbered —
`--chapter-phrase "default;none"` covers it. Written out, `none` is exactly
`/^()$/` — the number and nothing else, with a pause asked for on either side of
it, which is what the `^` and the `$` are.

Where a phrase alternative and `none` both read the same announcement, they are
treated as two readings of it rather than two announcements, and the phrase
reading is preferred: `none` asks for a pause on both sides, so it is the
stricter of the two and the one to fall back on if the first is turned down.

Later passes look harder than the first one does, and then check their work
against the pauses the file actually has: an announcement they turn up is only
marked if it is flanked by real non-speech on both sides — roughly a second of
silence or jingle in front of it, and at least a third of a second behind it. A
run's `--verbose` output names both measurements and both thresholds when a
candidate is dropped for that reason.

Everything else about a run is unaffected: the prologue, the epilogue and every
`--custom` mapping still match their own phrases as usual, and marks are placed
and refined exactly as they are for a phrase-based book.

Two things are worth knowing before reaching for it:

- **It leans entirely on the chapter numbering.** With a phrase there are two
  independent signals, and a stray number in the prose fails the first of them.
  Here the number is all there is, and what rejects a false one is that it does
  not continue the sequence. `--ignore-chapter-numbers`, which switches that
  check off, is therefore refused in combination with it — the pair would mark
  every number spoken alone anywhere in the book.
- **It is per language,** like every other alternative, so a batch may hold one
  series announcing "Kapitel 17" and another just saying "Seventeen":
  `--chapter-phrase "[en]none;[de]default"`.

`-p`, `--prologue-phrase <p>`
: The word or phrase that announces a prologue (default: `/prolog/`,
  localized by `--lang`). Takes the same alternatives, tags and guards as
  `--chapter-phrase`, but no number is parsed or expected — including `none`,
  which is just the word here, there being no number for it to stand in for.
  A prologue is always required to sit behind a real pause, whatever its phrase
  says, so writing the `^` yourself changes nothing; it is what a prologue *is*.
  Only accepted before the first chapter has been found; see
  [Prologue and epilogue](#prologue-and-epilogue). An empty string switches
  prologue detection off.

`-g`, `--epilogue-phrase <p>`
: The same for the epilogue, which is additionally required to follow the
  book's last chapter (default: `/epilog/`, localized by `--lang`),
  only accepted once at least one chapter has been found. An empty string
  switches epilogue detection off.

`-u`, `--custom <mappings>`
: Extra `phrase:title` mappings, separated by `;`, e.g.
  `--custom "zwischenspiel:Zwischenspiel;/zeit[- ]?tafel/:Zeittafel"`. A
  phrase is a word or a `/regexp/` and parses no number; a match anywhere in
  the file becomes a mark titled after the colon, as often as the phrase
  occurs. Titles may write out what the phrase captured — `${name}` for a named
  group, and the conversions listed under
  [Titles](#titles-what-a-mark-is-called); a reference by number such as `$1` is
  refused. Repeat the option to add further mappings. Never localized — but a
  mapping may open with a `[...]` tag holding a `xx` language code, restricting
  it to files that resolve to that language, and/or hints restricting where and
  how often it matches (`before-first-chapter`, `after-first-chapter`,
  `after-last-chapter`, `once`, `max=<n>`); untagged mappings apply
  to every file, anywhere in it, as often as the phrase occurs. See
  [Custom marks](#custom-marks) for the full syntax, the hints and the per-file
  limit.

`-U`, `--custom-file <path>`
: Read `--custom` mappings from a text file, one per line; blank lines and
  lines starting with `#` are ignored.

`-D`, `--named-mark-distance <seconds>`
: How close a named mark (prologue, epilogue, `--custom`) may come to a chapter
  mark before the two are written as one entry (default: 10). The chapter keeps
  its position and the named mark contributes its title in brackets —
  "Chapter 10 (Interlude)". `0` writes every mark separately. See
  [Named marks that land beside a chapter](#named-marks-that-land-beside-a-chapter).

`-t`, `--chapter-title <word>`
: Word used to build chapter titles; the chapter number is appended
  (default: `Chapter`, localized by `--lang` — e.g. `Kapitel` with
  `--lang de`). "Chapter 1", "Chapter 2", …

`--part-title <word>`
: Word used to build the part prefix of a file whose chapter numbering restarts
  partway through — "Part 2 - Chapter 1" (default: `Part`, localized by
  `--lang` — e.g. `Teil` with `--lang de`). A file holding a single chapter
  sequence never uses it. See
  [Books that count from one again in every part](#books-that-count-from-one-again-in-every-part).

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

### Auto language detection

With `--lang auto` (the default - no `--lang` needed at all), each file's
language is detected independently, so a directory containing audiobooks in
several languages is processed correctly in one run without per-file options.

It happens once per file, right after the silence scan (pass 1) and before
any transcription. Short samples are taken from inside the book - never from
its opening seconds, which on an audiobook are label music, a copyright card
or a title read over a bed at least as often as they are narration - and each
is handed to Whisper's own language detector, which answers with a language
code and a probability. The answer is then fixed for the rest of that file
rather than re-detected per probe, which would be slower and could disagree
with itself partway through a book.

- At or above a probability of 0.6, that sample settles the file. The
  chapter/prologue/epilogue phrases and all title words are localized for the
  detected language (see [section 7](#7-languages-and-number-recognition))
  exactly as an explicit `--lang <code>` would, but resolved individually per
  file.
- Below 0.6, another sample is taken from a different part of the book, up to
  five in all, stopping at the first one that clears the bar. A single weak
  reading is not acted on: a sample can land on a song, a shouted exchange or
  a passage quoted in another language, and re-listening elsewhere costs a
  few seconds.
- If none of the five clears 0.6, the language named most often across them
  wins. This matters more than it sounds - five quiet agreements that a book
  is German are worth more than any one of them alone.
- Only when two languages tie for first place, or nothing decodable could be
  sampled at all, does the file fall back to `en`.
- An explicitly given phrase or title option always wins over the localized
  default, regardless of the detected language.

Getting this wrong is worth more than a mistitled chapter: the resolved
language supplies the phrase every pass searches for, so a German book taken
for an English one is looking for "chapter" and cannot see "Kapitel" at all.
If the result line's language looks wrong for a book, re-run it with an
explicit `--lang <code>` before investigating anything else.

The outcome is shown in the per-file result line, `--dry-run` listing and
`--verbose` log:

```
Whisper model "turbo" loaded (Vulkan backend on NVIDIA GeForce GTX 1070, auto language detection), 2 file(s) to process.
buch.m4b: 13 mark(s) written (12 chapter(s) 1-12, 1 named), language: de (p=1.00)
book.m4b: 9 mark(s) written (8 chapter(s) 1-8, 1 named), language: en (p=0.98)
```

`--verbose` additionally logs the detection as it happens:

```
[14:02:11] buch.m4b: language auto-detected: de (p=1.00) from the sample at 1:52:18.40
```

or, when the first samples were doubtful and the vote decided it:

```
[14:03:38] book.m4b: language probe at 1:41:07.10 inconclusive: de (p=0.44, below 0.60)
[14:03:41] book.m4b: language probe at 3:47:41.85 inconclusive: en (p=0.51, below 0.60)
[14:03:52] book.m4b: language auto-detection inconclusive after 5 probe(s) (de x3, en x2); de named most often (best p=0.50)
```

or, when nothing could be agreed on at all:

```
[14:05:10] book.m4b: language auto-detection inconclusive after 5 probe(s) (tr x2, nl x2, pl x1); no clear winner, falling back to en
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

### Detection safety nets

Bounds on what detection is willing to believe, and on how long it keeps
looking. None of these is needed for an ordinary book.

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
  searched for — unless the file's prologue is detected, which implies a
  start at chapter 1 all by itself; see
  [Pass 3](#pass-3--gap-filling-only-when-needed). With it, a first chapter
  found *below* `<n>` aborts the file outright, left unchanged and reported
  exactly like a completed scan that found nothing — almost certainly the
  wrong file, `--chapter-phrase` or `--lang`, not a genuine split-book start.
  A first chapter found *above* `<n>` is instead treated like any other gap:
  pass 3 searches for the missing numbers down to `<n>`, and if it still
  can't find all of them, the file is tagged `.missing-marks-…` exactly as
  an unresolved gap between two detected chapters already is. The *abort* is
  what only applies to a fresh, from-scratch run, the same restriction as
  `--early-abort`; the hunt for the leading numbers is not restricted that way,
  so a `--verify` recovery or a `.missing-marks` resume keeps looking for them.

`--no-trailing-scan`
: Skip the transcription of everything after the last chapter found (default: it
  runs). No short form. Pass 3 spots a missing chapter as a hole in the number
  sequence, which needs a known chapter on either side of it — so a chapter
  missing *after* the last one found is the one case nothing else can notice, and
  the file would be written out looking complete: nothing reported missing, no
  `.missing-marks` tag, nothing in the log to go on. The trailing scan closes
  that hole, and it is on by default because a run that silently drops the end of
  a book is worse than a run that takes a few minutes longer.

  The cost is real and worth knowing: it is not a safety net that only costs
  something when it fires. With no expected numbers to satisfy the scan can never
  stop early, so every file pays a final chapter's worth of transcription time
  whether or not anything was wrong. It is read once and never twice — the
  shifted re-read of [pass 3.5](#pass-35--the-shifted-re-read) does not apply to
  it — which bounds that price at one pass over the tail.

  Turn it off for a library you have already checked, or where the last chapter
  matters less than the run time. Nothing is scanned anyway when no chapter was
  found at all — there is no "last chapter" to scan from — nor after an
  `--early-abort` or `--expected-start-chapter` abort, nor under
  `--ignore-chapter-numbers`, which does away with pass 3 altogether. If you happen to know
  how many chapters the book has, `--chapter-count` answers the same question
  for a fraction of the time, and giving it switches the blind scan off for you.

`--no-denoise`
: Do not re-read a garbled announcement through the built-in speech denoiser
  (default: it may). No short form.

  On a dull-sounding recording the recognizer sometimes writes a chapter's
  *number* while losing the word beside it — "1. The Long Road" where the
  narrator said "Chapter one, The Long Road". Nothing matches the chapter phrase
  then, so the chapter is missed, and this is the quiet kind of miss: no gap in
  the numbering to notice if it happens before the first chapter found, and
  nothing in the log to say a heading was heard and thrown away. Where a window
  fails in exactly that way, it is read a second time through a denoiser first.

  It is cheap because it is narrow. A window that produced a mark never reaches
  it, nor does one that heard nothing at all — that is a different failure with
  its own remedies — and a book whose audio carries enough treble is refused it
  outright, so most files never run it once. What it costs where it does fire is
  one extra pass over that one window. It can only ever add a mark: the window
  keeps its own bounds, and anything it finds goes through exactly the same
  acceptance rules as a first-pass find.

  Turn it off to reproduce an older run, or where the extra pass is not wanted.
  It will not rescue an announcement that simply is not inside the window —
  that is a framing problem, and no amount of cleaning up the audio can recover
  words a window does not contain.

`--chapter-count <n>`
: How many numbered chapters this book has, exactly (default: no expectation).
  Takes exactly one file, never a directory or several files — it is a
  statement about one particular book, and applied to a library it would tag
  most of it as incomplete. No short form.

  This is the informed version of the trailing scan above, aimed at the same
  blind spot: a chapter missing after the last one found has nothing above it
  to make its absence visible. Told the count, the run knows exactly which
  numbers are still owed, hunts only those, and stops the moment they turn up —
  where the blind scan has to transcribe the whole tail on spec, every time, even
  on a book that was already complete. Giving a count therefore replaces that
  scan rather than adding to it, and when the count is reached nothing is
  transcribed at all. When it is not, the chapters still missing are named
  and the file is tagged `.missing-marks-…` like any other unresolved gap.

  It is a cap as well: a chapter numbered above the count is discarded as a
  mishearing, which is `--max-chapter-number`'s whole job — so the two cannot
  be combined, and `--chapter-count` is the one to reach for when you know the
  number rather than merely an upper bound.

  What it does **not** do is end the search once the count is reached. A book's
  numbered chapters are rarely the last thing in it: an epilogue, or any
  `--custom` phrase, may still follow, and those are still looked for through
  to the end of the file.

  With `--expected-start-chapter` it counts from there, so a split-book part
  starting at chapter 5 with `--chapter-count 3` runs 5 to 7.

`-N`, `--max-chapter-number <n>`
: The highest chapter number this book plausibly has (default: **200**, counted
  from `--expected-start-chapter`; see `--chapter-count` above for when the
  exact figure is known). A
  detected chapter numbered above `<n>` is discarded on the spot as a
  mishearing rather than becoming a mark. Raise it for a book that really runs
  longer — the default is well past the longest novels, but a collected edition
  or a serial can pass it, and every chapter above the cap is silently dropped.
  Lowering it to roughly the real count is worth it whenever you know that
  figure. The default already throws away a misheard "chapter five hundred and
  ten", but a misheard "chapter one hundred and fifty" in a twelve-chapter book
  sits comfortably under it: that becomes a mark of its own, and every real
  chapter behind it is then rejected for being numbered below it. Such a number
  no longer drags the rest of the run with it — nothing between it and the last
  real chapter is reported missing, and no pass goes looking there — but the
  mark is still written, and only the file's summary line says so. Lighter
  Whisper models (`tiny`, `base`) are the usual source of such numbers. Not to
  be confused with
  `--max-chapters`, which counts a file's *pre-existing* marks rather than the
  numbers heard in the audio.

`--ignore-chapter-numbers`
: Detect chapter announcements as usual, but form no opinion about the numbers
  in them: no sequence, no gaps, no missing chapters. The spoken number still
  reaches the title. Cannot be combined with `--pass3-model`,
  `--expected-start-chapter`, `--max-chapter-number`, `--chapter-count` or
  `--verify`. See
  [Detecting chapters without believing their numbers](#detecting-chapters-without-believing-their-numbers).

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

`--fix`
: Requires `--verify`. Lets it *correct* a mark instead of only reporting on
  it: where a mark's announcement is confirmed but the mark sits a little
  away from it, the mark is moved onto the announcement and the file
  rewritten. Without this, a mark that is confirmed-but-misplaced needs a
  full `--force` re-run of the whole book to move by half a second. No short
  form.

  It only ever nudges, and the two bounds say what that means:

  - A mark **already within a quarter of a second** is left where it is.
    Rewriting an audiobook remuxes the entire file, and that is not worth
    doing to move a mark by less than the placement's own accuracy.
  - A mark **more than 30 seconds** from its announcement is left alone too,
    and reported. A mark that far out did not drift; it means something else
    — a retailer's grouping, another edition's numbering — and dragging it
    onto the nearest matching phrase would destroy that information rather
    than correct it.

  Marks that could not be confirmed at all are untouched by this: they go to
  the gap recovery described above, exactly as they do without `--fix`. What
  gets written is the file's own mark list with corrected timestamps —
  nothing is renamed, dropped or added, and no intro entry is invented.

  One caveat worth knowing. Marks are located here by re-transcribing the
  audio at them, which is the same machinery a full run uses to pin every
  mark — but a full run then anchors the result against the silence scan of
  [pass 1](#pass-1--silence-scan-and-vad-pre-pass), which `--verify` never
  runs. A fixed mark can therefore sit a fraction of a second later than the
  same chapter would land in a from-scratch run. Against a mark that was
  seconds out that is not the problem; if a book's marks matter to the last
  tenth of a second, `--force` without `--verify` is still the answer.

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
  A `.bak` left behind by an earlier run is not an error, and is **kept
  exactly as it is** - so however many times a book is re-run, the backup
  stays the copy from before the first one, which is the state worth being
  able to get back to. This run's own original is discarded instead, and the
  summary line says `earlier backup kept (predates this run)` so it is clear
  that `--revert` will not simply undo the last run.

`-R`, `--revert`
: Restore backups instead of processing: for every supported audio file with
  an added `.bak` suffix under the target, the current file is deleted and
  the backup renamed back. Combinable with `--recurse`, `--filter` and the
  output options (`--quiet` and `--summary` take effect; `--verbose` and
  `--no-bar` are accepted but change nothing here). All detection and safety
  options are rejected. An audio file named directly has its own `.bak`
  neighbour restored, so the suffix need not be typed out.

`--cleanup`
: Housekeeping instead of processing — see
  [Cleaning up after a run](#cleaning-up-after-a-run) for what it removes and
  what it refuses to. Combinable with `--revert`, `--yes`, `--recurse`,
  `--filter` and the output options; all detection and safety options are
  rejected, as they are for `--revert`. No short form, deliberately.

`--yes`
: Answers `--cleanup`'s confirmation prompt in advance. Required for a
  scripted or scheduled cleanup, which has no console to be asked at: a
  cleanup that can neither ask nor has been told refuses to run rather than
  guess. Not needed with `--cleanup --revert`, which throws nothing away.
  Rejected without `--cleanup` — an option reading "stop asking me things"
  must not look like it covers prompts it does not. No short form.

`-O`, `--no-op`
: Lists every file `--filter` (and `--recurse`) would select, then exits
  without loading a Whisper model, invoking ffmpeg or touching any file - a
  quick way to check that a `--filter` regexp or extension list actually
  matches the intended files before committing to a real run. Requires
  `--filter`; combinable only with `--recurse` and the output options
  (`--quiet` suppresses the listing itself, leaving just `--summary`'s
  count), the same restriction `--revert` has.

### Running your own commands around each file

`--run-before <command>`
: Run a shell command for each file, just before ABChapterize starts work on it.

`--run-after <command>`
: Run a shell command for each file, once ABChapterize has finished with it.

Both take the command line you would have typed yourself, and both are handed to a
shell — `cmd` on Windows, `/bin/sh` elsewhere — so built-ins (`move`, `copy`),
pipes, redirection, `&&` and `~` all work as usual. The command runs in the folder
you started ABChapterize in, not in the file's folder; use the placeholders below
to be explicit about paths.

```
abchapterize --recurse --backup \
             --run-before "abnormalize $99" \
             --run-after "mv $99.bak ~/archive/$1" \
             ~/audiobooks
```

**When they run — and when they do not.** The hooks belong to a file that is
actually worked on:

- A file the run **skips before it starts work on it** runs neither hook. That
  includes a file that already carries chapter marks (without `--force`), one
  whose codec cannot be decoded, and — under `--import` — one with no sidecar
  next to it. Nothing had been done to the file, so there was nothing to prepare
  for and nothing to follow up on. A file `--verify` then leaves alone is
  different: there the check *is* the work, so `--run-before` has already run by
  the time it reaches a verdict, and only `--run-after` is withheld.
- `--run-after` additionally does **not** run for a file left tagged
  `.missing-marks-...`. Such a file is unfinished and a later run is expected to
  pick it up again, so a command that archives or tidies up after a finished book
  must not be told this one is done.
- Under `--dry-run` neither command is run. The command line each one *would* have
  run is printed instead, which is also the quickest way to check that your
  placeholders and quoting come out the way you meant.
- If `--run-before` exits with a non-zero status, the file is **skipped** with a
  warning and `--run-after` does not run for it either: the preparation you asked
  for did not happen, and marking a file that is in an unknown state is worse than
  leaving it alone. The run itself carries on with the next file. A non-zero exit
  from `--run-after` is only reported — the file is already written by then.

Because `--run-before` may well change the file it just ran for (joining a split
book, re-encoding it), the file is read again afterwards, so its duration, codec
and existing marks are the ones it has *now*.

The commands' own output goes into the log rather than to the console, so it
cannot scribble over the progress bar: use `--verbose`, or `--log-file` to keep it.

#### The placeholders

Anywhere in either command, `$` followed by a number stands for a part of the
file's path. Counting starts at the file name and works upwards, and the drive or
root counts as one element of the path:

| Placeholder | For `c:\test\buch.mp3` | What it is |
| --- | --- | --- |
| `$0` | `buch` | The file name without its path and without its last extension. |
| `$1` | `buch.mp3` | The file name without its path. |
| `$2` | `test\buch.mp3` | The file name with one parent folder; `$3` with two, and so on. |
| `$99` | `c:\test\buch.mp3` | A number larger than the path is deep gives the whole path — so `$99` is the way to say "wherever this file is". |
| `$-1` | `c:\test\` | The file's own folder. Always ends with a separator. |
| `$-2` | `c:\` | One folder further up; `$-3` further still. Never goes above the drive (Windows) or `/` (Linux). |

Paths are always resolved to absolute ones first, so `$99` and `$-1` name the same
place whatever folder you started the run in.

A `$` that is not followed by a number, such as `$HOME`, is left exactly as it is
and reaches the shell untouched. To write a literal `$1` that ABChapterize must not
replace, double the dollar: `$$1`. `$-0` is not a placeholder and is refused as a
typo — the whole path is `$99`.

#### Quoting, and why you rarely have to think about it

Audiobook file names contain spaces, ampersands and brackets, all of which a shell
would otherwise read as punctuation. Substituted values are therefore quoted for
you, and only where they need it:

```
--run-after "move $1.bak $0.bak"      for "buch 1.m4b" runs
move "buch 1.m4b.bak" "buch 1.bak"
```

Note that the quotes take in the `.bak` you appended, not just the placeholder —
otherwise the shell would end the argument at the closing quote. On Linux and
macOS the value is escaped where it stands instead (`buch\ 1.m4b.bak`), which
leaves the rest of what you wrote working normally: `~/archive/$1` still expands
the tilde, which it would not inside quotes. A value you have quoted yourself is
left alone, so `copy "$1" d:\arch` does what it looks like.

One case Windows cannot be protected from: `cmd` expands `%NAME%` inside quotes
just as readily as outside, and offers no way to escape a percent sign on a command
line. A file whose name happens to contain the name of an existing environment
variable between percent signs will reach the command with that expanded.

### Cleaning up after a run

`abchapterize --cleanup <target>` puts a folder back the way it was before this
tool was let loose on it. Nothing is transcribed, no model is loaded, and a line
is printed for every change:

- **Leftover temporary files** are deleted. The one exception is an original
  parked by a write that was killed at the wrong moment (see
  [How chapters are written](#4-how-chapters-are-written--file-safety)): if the
  audiobook is missing, that parked copy *is* the audiobook, and it is renamed
  back instead of deleted.
- **`.debug.log` troubleshooting logs** are deleted.
- **Progress files of interrupted batch runs** are deleted, so the next run
  starts those directories over. Only when no `--filter` is in play: a filter
  selects books, a progress file belongs to a directory, and quietly throwing
  away an interrupted batch's resume point is not something a narrowed cleanup
  should do.
- **`.missing-marks-...` name tags** are taken off, leaving each file under its
  original name. The chapter marks already written into it stay. A tag is left
  on where the original name is taken by something else — those marks are real
  work, and overwriting an unrelated file with them would be a poor trade for a
  tidier listing.
- **`.bak` backups** are deleted — but only where the file they back up is
  sitting next to them *and* runs the same length (within two seconds, the
  tolerance a remux can shift a duration by). A backup whose file is gone, or
  which turns out to be a different recording, is left alone with a line saying
  why. This is what makes the mode safe to point at a library: it can never
  throw away the only copy of anything.

With **`--revert`** the last point flips: every `.bak` is restored over the file
beside it, exactly as plain `--revert` does, and the rest of the cleanup happens
around it. The original comes back under the book's *plain* name even if it was
tagged, the tag having been added after the backup was taken.

Nothing is touched before you have seen what would happen:

```
--cleanup is about to:
  rename ".missing-marks" files back to their original names: 2 file(s)
  delete ".bak" backups whose file is present and of matching length: 5 file(s)
  delete ".debug.log" troubleshooting logs: 7 file(s)
This cannot be undone. Are you sure? Type "yes" to proceed:
```

Anything other than `yes` (or `y`) leaves the folder untouched — which doubles
as a preview, and is why `--dry-run` is not accepted here. `--yes` answers the
prompt in advance, and is **required** when there is no interactive console to
ask at; without it such a run refuses rather than guesses. `--cleanup --revert`
never asks, nothing being thrown away.

Files this tool did not create are never touched — cover images, sidecars from
`--export`, stray text files, audiobooks themselves. A step that fails (a file
held open by a player, say) is reported and the remaining ones still run, but
the run then ends with exit code 1 so a script cannot mistake a partial cleanup
for a complete one.

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
- `--dry-run`, `--no-op`, `--revert` and `--cleanup` never write one either —
  nothing they do is worth not doing twice. `--cleanup` does *delete* the ones
  it finds, unless a `--filter` narrows it; see
  [Cleaning up after a run](#cleaning-up-after-a-run).
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
  is redirected and when the `NO_COLOR` environment variable is set to a
  non-empty value (the no-color.org convention). On Unix it additionally wants `TERM` to name a 16-color terminal
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

  The block closes with four listings, each left out when it would be empty:
  every file that was **skipped** and why, every file **no chapters were found
  in** and which of the reasons applied, every file left **still missing
  chapter marks**, with how many are missing and — up to ten of them — which
  chapters those are, and every file carrying **low-confidence chapter marks**,
  meaning marks whose chapter number was read at a Whisper probability below
  0.50 and which are therefore worth a look by hand. Files appear under the name
  they carry once the run is over, so a book tagged
  [`.missing-marks-…`](#pass-35--the-shifted-re-read) is listed under its
  tagged name and can be found in the folder as printed.

  Where any of the low-confidence files was read with
  [`--chapter-phrase none`](#bare-numbers-as-announcements),
  that listing adds one line of warning about comparing the two: a number spoken
  alone is often a transcript segment of a single token, whose probability
  fluctuates far more than a whole phrase's does, so a low value there says much
  less about the mark than the same value would in an ordinary run.

  ```
  Summary: 5 file(s) encountered, 3 processed, 2 skipped, 1 with warnings, 1 with no chapters found
  Total time: 1:42:07, average per processed file: 34:02
  Confidence of written chapter marks: min 0.71, max 0.99, avg 0.94
  Skipped 2 file(s):
    Stalker.m4b: has 30 chapter mark(s)
    Wintersmith.m4b: 14 pre-existing chapter mark(s) verified correct
  No chapters found in 1 file(s):
    Interview.mp3: no chapter phrases found
  Still missing chapter marks in 1 file(s):
    Die Dritte Macht.missing-marks-3-7.m4b: 2 mark(s) missing (chapter 3, 7)
  Low-confidence chapter marks in 1 file(s) (below p=0.50, worth a manual check):
    Raumschiff Erde.m4b: 2 mark(s) (chapter 12, 31)
  ```

`-d`, `--dry-run`
: Run full detection but write nothing. Instead of the usual "N chapter(s)
  written" line, the file's result shows every chapter that *would* be
  written, with its exact timestamp and title:

  ```
  My Audiobook.m4b: DRY RUN - would write 24 mark(s) (23 chapter(s) 1-23, 1 named):
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

  The sidecar is written for a file detection completed normally. A file left
  with an unresolved chapter-sequence gap, a `.missing-marks` file being
  resumed, and a `--verify --fix` rewrite all write their marks into the audio
  file as usual but no sidecar — those files change their name as they are
  written, and a sidecar under the old one would not be found again.

`-I`, `--import`
: Skip Whisper detection entirely and write the chapters found in the
  sidecar file instead (looked up next to the audio file, same naming as
  `--export`). If no sidecar file is found, the file is skipped with a
  message suggesting `--export`. Because there is nothing to detect,
  `--import` cannot be combined with any detection option — `--lang`,
  `--chapter-phrase`, `--prologue-phrase`, `--epilogue-phrase`, `--custom`,
  `--custom-file`, `--ignore-chapter-numbers`, `--model`, `--pass3-model`,
  `--mark-before-jingle`, `--quick-marks`, `--mark-lead`,
  `--min-silence-length`, `--noise-floor`,
  `--early-abort`, `--expected-start-chapter`, `--max-chapter-number`,
  `--chapter-count`, `--no-trailing-scan`, `--no-denoise`, `--verify`,
  `--named-mark-distance` — nor with the title
  options `--chapter-title`, `--part-title`,
  `--intro-title`, `--prologue-title` and `--epilogue-title`, since an
  imported mark carries the title the sidecar gives it and no intro mark is
  prepended — nor with `--export`, `--revert`, `--cleanup` or
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

### Performance

Files are always processed one at a time, so each one gets the whole machine.

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

The two thread counts below both take a number or `auto`, and `auto` means one
thread per **physical** CPU core — not per hardware thread. Hyperthreads add a
little on machines where they help and cost a great deal on machines where they
do not, and nothing here can tell which machine it is running on; if you know
yours better, say so with an explicit number. Neither is valid with `--revert`
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
[14:32:07] threads: Whisper 12, voice-activity pre-pass 12 (cores: 12 physical, 24 logical)
```

### Miscellaneous

`-?`, `--help`
: Show the usage information.

`--version`
: Show the version number, plus the auto-incrementing build number and UTC
  build timestamp (e.g. `abchapterize 0.9.0 (build 42, built 2026-07-20
  14:33:12 UTC)`). `--help`'s banner shows the plain version number only; the
  build number otherwise appears just in the opening line of a `--log-file` or
  a `--debug` log, which outlives the build that wrote it.

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
("Erstes Kapitel", "2. Kapitel", "premier chapitre", "Birinci Bölüm").
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
`--chapter-phrase`, `--chapter-title`, `--part-title` and `--intro-title`:

| `--lang` | Default phrase | Default title word | Default part word | Default intro title |
| --- | --- | --- | --- | --- |
| `en` | `/(?:^chapter ()\|^() chapter\|^chapter)/` | Chapter | Part | Intro |
| `de` | `/(?:^kapitel ()\|^() kapitel\|^kapitel)/` | Kapitel | Teil | Intro |
| `fr` | `/(?:^chapitre ()\|^() chapitre\|^chapitre)/` | Chapitre | Partie | Introduction |
| `es` | `/(?:^cap[íi]tulo ()\|^() cap[íi]tulo\|^cap[íi]tulo)/` | Capítulo | Parte | Introducción |
| `it` | `/(?:^capitolo ()\|^() capitolo\|^capitolo)/` | Capitolo | Parte | Introduzione |
| `nl` | `/(?:^hoofdstuk ()\|^() hoofdstuk\|^hoofdstuk)/` | Hoofdstuk | Deel | Intro |
| `tr` | `/(?:^b[öo]l[üu]m ()\|^() b[öo]l[üu]m\|^b[öo]l[üu]m)/` | Bölüm | Kısım | Giriş |
| `pt` | `/(?:^cap[íi]tulo ()\|^() cap[íi]tulo\|^cap[íi]tulo)/` | Capítulo | Parte | Introdução |
| `pl` | `/(?:^rozdzia[łl] ()\|^() rozdzia[łl]\|^rozdzia[łl])/` | Rozdział | Część | Wstęp |
| `sv` | `/(?:^kapit(?:el\|let) ()\|^() kapit(?:el\|let)\|^kapit(?:el\|let))/` | Kapitel | Del | Introduktion |
| `da` | `/(?:^kapit(?:el\|let) ()\|^() kapit(?:el\|let)\|^kapit(?:el\|let))/` | Kapitel | Del | Introduktion |

Every one of them is the same shape: three alternatives, the first taking the
number that follows the word directly ("Kapitel 12"), the second the number that
precedes it ("Erstes Kapitel", and in Turkish "Birinci Bölüm", which is that
language's only order), and the third the bare word, which leaves the number to
be read off whatever stands around it. All three carry a `^`, since an
announcement is by definition set off from what precedes it — either by a pause
or by the recognizer writing it as a segment of its own.

Where two of them read the same words differently, the announcement is decided
by the chapter sequence rather than by which alternative got there first: a
number that cannot follow the chapters already found is put aside and the next
reading of the same words is tried.

They are regular expressions so that one language's spellings are covered at
once: an accent Whisper dropped (`capitulo` for `capítulo`), a letter it wrote
without its diacritic (`bolum` for `bölüm`), or a stem the language itself
changes (Swedish and Danish say "kapitlet" as readily as "kapitel"). Nothing
else changes — the word is matched case-insensitively and as a substring
exactly as a plain word would be, so an inflected ending needs no pattern of
its own ("rozdziału" is found by `rozdzia[łl]`).

`default` in a `--chapter-phrase` value stands for whichever of these rows the
file's language resolves to.

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
| `small` | ggml-small.bin | ~465 MB | **`--model` default** — the best prober, see below |
| `medium` | ggml-medium.bin | ~1.5 GB | |
| `turbo` | ggml-large-v3-turbo.bin | ~1.6 GB | **`--pass3-model` default** — near-large accuracy, much faster |
| `large` | ggml-large-v3.bin | ~3.1 GB | most accurate, slowest |

The two options are set to different models on purpose, and the pairing matters
more than either value on its own.

`--model` does the finding, and it works in short windows — a pause, perhaps a
jingle, an announcement, a few seconds in all. Whisper always processes audio in
30-second chunks and pads anything shorter out to fill one, and the large models
handle that padding badly: they tend to hand back the whole window as a single
run-on sentence with the announcement missing from it altogether. Smaller models
do not do this. On one French audiobook the difference was six chapters out of
twenty-five, lost by the larger model and found by `small` in the very same
windows. `small` is also several times faster, so it is the default for the
probing and there is nothing being traded away.

`--pass3-model` does the reading-out-loud: long, naturally framed stretches of
audio, where the usual ranking does hold and a heavier model genuinely hears
more. So it defaults to `turbo`, which is loaded only if a chapter actually
goes missing — and, being heavier than the probing model, also brings
[pass 2.5](#pass-25--cheap-gap-re-probe-only-with-a-heavier---pass3-model) and
pass 2's second reading of an implausible chapter number into play.

A word of warning about the small end of the scale: chapter detection hinges
on the recognizer catching one short, isolated phrase per chapter — there is
no surrounding context to recover from a misheard word, and a single missed
announcement leaves a sequence gap or a mismarked chapter. `tiny` mishears
or drops chapter announcements far too often for that to be reliable; its
support exists mostly for completeness — quick experiments, toy examples,
or extremely constrained machines. `base` fares somewhat better but is
still error-prone, especially for non-English audio. For real audiobooks, do not
go below `small`.

Raising `--model` is worth trying on a book whose narrator is genuinely hard to
make out, but check the result rather than assuming it improved — and note that
naming `--model` alone moves the pass-3 model with it, so `-m large` gives you
large in both roles. `-M large` on its own is the safer way to spend more time:
one last, best-effort attempt at the chapters that went missing, and nothing
changed about the ones that did not.

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
for it, consistent with `turbo` having about as many parameters as `medium`
(809 M against 769 M) on a larger file. Treat that one as an estimate rather than
a published number. A default run holds `small`'s ~852 MB throughout and adds
`turbo`'s share only once pass 2.5 or pass 3 actually runs.

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
[performance options](#performance).

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

`--list-gpus` and `--use-gpu`'s name matching read the Vulkan device list, which
is where multi-GPU machines actually cause trouble. The index they settle on is
handed to whichever backend then loads, and a device is only ever pinned when
that list holds two or more entries — so on a single-GPU machine, and on any
machine you leave `--use-gpu` off with only one card enumerated, the backend
keeps its own device 0. Where a CUDA card is enumerated next to an integrated
one, however, the ordinal handed over is a Vulkan ordinal, and CUDA may well
number its devices differently: check the startup line names the card you meant,
and fall back to `--cpu-only` or to leaving the machine's own default alone if
it does not. A machine with several CUDA cards is rare enough not to have earned
an option of its own yet.

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
My Audiobook.m4b: 24 mark(s) written (23 chapter(s) 1-23, 1 named)
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
when a sequence gap makes the detector re-probe earlier candidates. Pass 3
transcribes in chunks of several minutes each, and the bar follows the
recognizer's own position through the chunk it is working on rather than
jumping once per finished chunk — so a long gap keeps showing that something
is happening throughout.

Once detection finishes, the bar switches to a final `Muxing...` phase while
the chapter marks are written into the file — worth watching on a large
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
- the silence count of pass 1 and, when the VAD pre-pass ran, its non-speech
  region count followed by a jingle tally: how many stretches of at least two
  seconds the file holds that are neither speech nor silence — music, in other
  words — with the shortest, longest and average length of them. A brief vocal
  blip in the music does not split one jingle in two, and counts toward its
  length. A book whose chapters open with music says so here, in the first few
  seconds of a run, and the longest of them is the figure everything that has to
  look back over music is measured against. The two counts
  answer different questions and need not match: the regions are the places
  pass 2 will look, the jingles are what the audio actually holds,
- each probe window and pass-3 chunk as a `<length>@<time>` header line,
- every accepted chapter detection with the exact mark position, confidence
  and the loudness of the audio right at that position (e.g. `-58.3 dBFS`;
  `-inf dBFS` for pure digital silence) — a figure close to silence means the
  mark landed in a real pause, a loud one that it landed mid-word or inside
  music and is worth a listen. The same line names what made the spot a
  candidate in the first place — `at a silence`, `at a jingle`, or `embedded in
  a jingle` for a jingle the speech detector heard something inside — since that
  is what decided where the window opened and how far it ran (see
  [Pass 2](#pass-2--probing)); a mark found at the file's very start has no such
  class and says nothing. Flagged `LOW CONFIDENCE` below 0.5, plus a
  `still missing:` list of any earlier chapter numbers not detected yet,
- every chapter number that was heard but *not* turned into a mark, with the
  reason: one that does not top the last number accepted (`skipped chapter 3 at
  1:12:04.20 - not above last accepted 7 (in-text mention?)`), one
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
every voice-activity segment, non-speech region and jingle, every Whisper transcript
segment by segment, and the mark-refinement probes that appear nowhere else.
It switches logging on by itself and leaves the console alone, so
`--debug` on its own gives you a quiet run and a full file. Expect a few MB
per audiobook.

Unlike `--log-file`, each run starts its debug log over: the file holds one
run, so a search through it cannot land on a line from a different one, and
two runs can be compared by diffing their logs. Copy one aside before
re-running a book if you want to keep it.

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
| 1 | Fatal error — a file could not be processed (the run stops), or a `--cleanup` finished with failed steps. |
| 2 | Command line usage error. |
| 130 | Aborted with Ctrl+C. |

Ctrl+C is handled gracefully: child processes are terminated and temporary
files are cleaned up on the way out.

## 14. Troubleshooting

**"No chapter phrases found"** — run with `--verbose` and read what Whisper
actually transcribed. Typical causes: the announcements use a different word
(fix with `--chapter-phrase`), the language is wrong (with `--lang auto`,
check the "language used" note in the result line - the samples may have
agreed on the wrong language or failed to agree at all and fallen back to
`en`; pin it with an explicit `--lang` if so), or the pauses are shorter than
`--min-silence-length` (lower `-n`).

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

**The summary line says a number could not be corroborated** — a chapter was
detected whose number cannot continue the sequence, re-reading the audio
produced nothing better, and the run declined to take it at face value: the
mark is written where it was found, nothing under it is reported missing, and
no pass went looking there. Usually Whisper misheard a number (lighter models
such as `tiny` and `base` are prone to this); it can also be a number in the
audio that is not a chapter number at all, which is what `--chapter-phrase
none` invites. Either way the mark is worth a look. Set
`--max-chapter-number` to roughly the book's real chapter count and such a
number is thrown away as it is found instead. Note that a real chapter behind
it may have been lost, having been rejected for being numbered below it.

**A wildly wrong chapter number appeared, and everything below it is now
"missing"** — the same mishearing, but with something detected *after* it that
made the sequence believe in it. Set `--max-chapter-number` as above. If the
run already left the file tagged, note that a tag naming more than ten missing
chapters is shortened to a plain `.missing-marks` and is *not* resumed
automatically — rerun it with `--force` (and the new option) once you know
what went wrong.

**It's slow** — see the speed knobs: `--min-silence-length` (fewer probes, or
`0` for jingles only, on a book whose every chapter opens with
music), `--no-trailing-scan` (skips a final chapter's worth of transcription on
every file), and a smaller `--pass3-model` if only the gap-filling pass drags.
Check that the startup line reports a GPU backend, not CPU.

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
