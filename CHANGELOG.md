# Changelog

All notable, user-visible changes to ABChapterize are recorded here — what changed
for you, not how it was built. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

Version numbers are deliberately *not*
[Semantic Versioning](https://semver.org/spec/v2.0.0.html), so read them like this:
a **patch** release brings bugfixes and new features that break nothing you were
using; a **minor** release brings significant new features and may change or remove
something you relied on, so skim its notes before upgrading a script; and a **major**
release is whatever felt big enough to deserve one — the program has turned into a
different animal, or a headline feature landed, or it has simply grown up enough to
earn a round number.

## [0.11.0] — unreleased

### Added

- **`--min-silence-length 0` searches the jingles and nothing else.** On a book whose every
  chapter opens with a jingle, the hundreds of ordinary pauses in the narration are so much
  dead weight — each one a candidate, each candidate a Whisper probe. Setting the length to
  zero drops them all and leaves only the jingles, which on such a book is the largest
  saving available anywhere in the tool. It is not a default and never will be: on a book
  whose chapters open with a plain pause instead, it removes the only way of finding them.
  (For the same reason it cannot be combined with `--max-jingle-length 0`, which would
  leave nothing at all to look at.) What it does not switch off is the silence scan itself,
  which marks are still placed and refined against — so a chapter found this way lands in
  exactly the same place it otherwise would.

- **`--verify --fix` corrects a misplaced mark instead of only reporting it.** `--verify`
  could tell you that a mark's chapter really is announced right there — and then, if the
  mark sat half a second off, leave you re-running the whole book with `--force` to move
  it. With `--fix`, a confirmed mark is nudged onto its announcement and the file
  rewritten. Only a nudge, and both ends are bounded: a mark already within a quarter of a
  second is left alone, since remuxing an audiobook to move it that far is not worth doing;
  and one more than 30 seconds from its announcement is left alone too, because a mark that
  far out is not a mark that drifted but one that means something else. Marks that could
  not be confirmed at all are untouched and still go to the usual gap recovery, and nothing
  but the timestamps changes — no mark is renamed, dropped or added.

- **`--chapter-phrase none`, for books that announce a chapter by its number alone**
  (experimental — calibrated against a single book so far, so check its results and expect
  the rules behind them to keep moving). Some narrators say "Seventeen." and read on, with
  no "chapter" anywhere — and until now the tool had nothing to look for in such a book.
  This mode drops the phrase entirely and takes a number spoken *alone*, with a pause on
  either side of it, as the announcement.
  A number inside a sentence is not one, so the years, prices and house numbers in the
  prose are left alone; a number that ends its own sentence still counts even where the
  recognizer runs it straight into what follows ("Seventeen. He was late again."), which
  is common enough that not allowing it would lose chapters outright. The later recovery
  passes look harder still, and then check what they find against the file's own pauses:
  a candidate is only marked with a real pause in front of it — roughly a second of silence
  or jingle — and a shorter one behind, and `--verbose` names the measurement and the
  thresholds when one is dropped for that reason.
  Since the number is otherwise the only evidence there is, what rejects a false one is
  that it does not continue the chapter sequence — which is why this cannot be combined
  with `--ignore-chapter-numbers`. Per language, like every other value of the option.

- **The silence threshold now adapts to the recording, and `--noise-floor` sets it by
  hand.** Finding chapters starts with finding the pauses, and a pause has always been
  defined as audio quieter than a fixed −35 dBFS. That figure sits comfortably between
  the room tone and the narration of any ordinary audiobook — but not of one recorded
  with audible hiss, which never drops below it, so no pause and therefore no chapter is
  ever found; nor of one mastered very quietly, where the narration itself falls below it
  and every gap between two words looks like a chapter break. Neither could be fixed from
  the command line, and no amount of adjusting `--min-silence-length` helped, the problem
  never having been the length of the pauses. Each file's own levels are now sampled
  before the scan (about a second, even on a long book) and the threshold moved only
  where the usual one would not fit — on a normal master it does, and nothing changes.
  `--noise-floor` overrides the whole thing with a fixed level in dBFS.

- **`--chapter-count`: tell it how many chapters the book has.** A missing chapter is
  normally spotted as a hole in the numbering, which needs a known chapter on either side
  of it — so one missing *after* the last chapter found was the one case nothing noticed,
  and the file came out looking complete. The trailing scan closes that hole by transcribing
  the whole tail on spec, on every file, whether or not anything was wrong.
  Given the count instead, the run knows precisely which numbers are still owed, hunts
  only those, stops the moment they turn up, and does nothing at all when the count was
  already reached. It also caps the numbering, so a misheard "chapter five hundred" cannot
  invent five hundred missing chapters — which is `--max-chapter-number`'s job, and why the
  two cannot be combined. Takes one file at a time, being a statement about one particular
  book, and reaching the count does not end the search: an epilogue or a `--custom` phrase
  may still follow.

- **`--cleanup`: one command to sweep up after a run.** Finishing a library used to
  leave a trail — `.bak` backups, `.debug.log` files, half-written temporary files from
  an interrupted run, and books still carrying a `.missing-marks-...` tag in their name —
  and clearing it out by hand meant knowing which of those were safe to delete. This does
  it for you: leftovers and logs go, name tags come off, and backups are deleted only
  where the file they back up is sitting next to them and runs the same length, so it can
  never throw away the only copy of anything. A backup it declines to delete says why.
  It prints what it is about to do and waits for a "yes"; `--yes` answers that in advance
  for a scripted cleanup, and is required where there is no console to ask at. Add
  `--revert` to restore the backups over their files instead of deleting them.

### Changed

- **The default is now a small model for finding chapters and `turbo` for filling the gaps**
  (`-m small -M turbo`), where both used to be `turbo`. This is not a speed compromise: the
  model that finds chapters listens to windows a few seconds long, and the large models are
  markedly *worse* at those — they tend to return the whole window as one run-on sentence with
  the announcement missing from it. The heavier model is kept for pass 3, which transcribes long
  stretches where it genuinely does hear more, and is downloaded and loaded only if a chapter
  actually goes missing. Runs are also faster, and the first-run download is much smaller.
  Because the two models now differ by default, the second-opinion pass (pass 2.5) and pass 2's
  re-reading of an implausible chapter number are on by default too. Naming `--model` without
  `--pass3-model` still points both at your choice, so `-m large` means large throughout.

- **The trailing scan runs by default.** A missing chapter is normally spotted as a hole in the
  numbering, which needs a known chapter on either side of it — so one missing *after* the last
  chapter found was the one case nothing noticed, and the file was written out looking complete:
  nothing reported missing, no `.missing-marks` tag, nothing in the log to go on. That silent
  failure is worse than a run that takes a few minutes longer, so the stretch after the last
  chapter is now transcribed by default. It costs about one final chapter's worth of
  transcription per file and can never stop early, having no expected numbers to satisfy;
  `--no-trailing-scan` buys that time back for a library you have already checked. It is read
  once and never twice now, so the price is bounded at a single pass over the tail.

- **`--chapter-count` now switches the blind trailing scan off.** Telling the run how many
  chapters a book has is a statement about what is in the tail, so it replaces the speculative
  sweep instead of running alongside it: the numbers still owed are hunted directly, the search
  stops the moment they turn up, and nothing at all is transcribed once the count is reached.

### Removed

- **`--trailing-scan` / `-L`**, replaced by `--no-trailing-scan` now that the scan is the
  default. Either spelling stops the run with a message pointing at the new option rather than
  quietly doing the opposite of what was meant.

### Fixed

- **A multi-word `--chapter-phrase` is no longer defeated by where the recognizer breaks its
  sentences.** If the announcement came back split — "Première partie." and then
  "Chapitre 19." — a phrase containing a space could not match across the break, and the chapter
  was dropped without a word in the log. The built-in phrases are all single words and never ran
  into it; a phrase of your own routinely could. Whitespace is now normalized before any phrase
  is matched, so where the words fall between two transcript sentences no longer matters.

- **Chapter announcements are no longer lost to an over-narrow probe window.** The window that
  looks for an announcement narrows itself to fit the jingles a book actually has, and on a book
  with short jingles it could narrow far enough that the recognizer started padding it out — at
  which point the larger models hand back the whole window as one run-on sentence with the
  announcement missing from it. On one book that cost six chapters of twenty-five, five of them
  at the very end where nothing else would have noticed. The automatic narrowing now stops at a
  width that transcribes reliably. An explicit `--max-jingle-length` is still honoured exactly
  as given.

- **A prologue or epilogue is no longer marked on a word that merely contains the phrase.**
  "Prologue" and "epilogue" are ordinary words, and in some languages they hide inside
  longer ones — Italian "riepilogo" ("summary") contains "epilogo" — so a narrator reading
  an ordinary sentence could plant the book's epilogue mark hours before the epilogue. It
  did more damage than an extra mark: only one of each is written per file, so the false
  match displaced the real one, which had already been found correctly. Both are now
  required to sit behind a real pause, as a spoken heading always does and a word in
  mid-sentence never can. Nothing is asked about what follows the announcement, since
  narrators routinely read straight on from "Epilogue" into the first sentence of it, and
  `--custom` phrases are exempt from the check entirely — they name whatever recurring
  element you say they do, wherever you say it is.

- **One implausible chapter number no longer sends the search after chapters that were
  never there.** A number that cannot possibly continue the sequence has always been
  re-read from differently framed audio, and usually that settles it. When it does not —
  because the number was heard perfectly and simply belongs to something other than a
  chapter, a spoken year being the case that prompted this — the mark used to be taken at
  face value anyway, and everything below it declared missing. On an eighteen-hour book
  that meant a long, fruitless search of audio with nothing in it, and a file left tagged
  `.missing-marks` for chapters it had never lost. Such a mark is now written where it was
  found and left out of the reckoning: nothing under it is reported missing and no pass
  goes looking for it. Because the output then looks entirely clean, the file's summary
  line names the number in question so it can be checked by hand.

- **A chapter mark is no longer planted on the heading of a prologue or epilogue.** Where a
  book's epilogue is introduced by more than one line — a title, then a date or a year, then
  a place — a numbered announcement could be read out of the second line and written as a
  chapter of its own, seconds behind the epilogue's own mark. A mark that lands within a few
  seconds of a prologue, epilogue or `--custom` mark and carries a number that fits nowhere
  in the book's sequence is now recognised as part of that same announcement and dropped. A
  real chapter beginning right after a short prologue keeps its mark, since its number
  continues the sequence, and the first chapter of a book is never dropped this way.


## [0.10.1] — 2026-08-04

### Fixed

- **Marks no longer land on the announcement they should sit in front of.** A mark is
  meant to start playback `--mark-lead` seconds before the narrator says "Chapter
  five"; on some books it arrived with almost none of that lead left, so seeking to the
  chapter dropped you into the middle of the word "chapter" and you heard the number
  and nothing before it. The announcement's start was being measured from the point
  where speech recognition stops recognising a phrase cut off at the front — which is
  not where the phrase begins, because a clipped opening word is often still recognised.
  Where a pause runs up to the announcement, the waveform is now read directly for the
  point where sound resumes, and the mark measured from there. Affected marks move
  earlier by up to three tenths of a second, most of them by around a tenth; books that
  play a jingle straight into the announcement have no such pause and are unaffected.

- **A book whose chapter numbering restarts no longer fails silently.** In a book
  divided into parts, each part may begin again at chapter one. Those later
  announcements were heard and read correctly, then dropped for not continuing the
  sequence, and nothing said so — the book simply stopped producing chapters partway
  through, which is indistinguishable from a detection that stopped working. The file's
  summary line now reports how many announcements were skipped that way and points at
  `--ignore-chapter-numbers`, which marks every announcement it hears regardless of its
  number. What gets written is unchanged; only the silence about it is.

- **Automatic language detection no longer listens to the label music.** With
  `--lang auto`, a file's language was decided from the very start of the book — which
  on an audiobook is a label jingle, a copyright card or a title read over a bed at
  least as often as it is narration. A book whose opening seconds said nothing useful
  could be run in the wrong language from beginning to end, and that costs far more
  than a mistitled chapter: the language supplies the phrase every pass searches for,
  so a German book taken for an English one is looking for "chapter" and cannot see
  "Kapitel" at all. Chapters go missing, and the passes that hunt for them spend a
  long time doing it. Detection now samples narration from inside the book instead.

### Changed

- **`--summary` now names the books it found no chapters in.** The closing block
  already counted them, but finding out *which* ones meant scrolling back through the
  run — and in a batch large enough for that to matter, the per-file lines are long
  gone (or were never printed, under `--quiet`). They now get a listing of their own
  next to the skipped and still-incomplete ones, each with the reason it came back
  empty-handed: no chapter phrase anywhere, an early abort, or a first chapter below
  `--expected-start-chapter`.

- **A doubtful language reading is now re-checked elsewhere in the book.** One weak
  sample is no longer acted on: a sample can land on a song, a shouted exchange or a
  passage quoted in another language. Up to five samples are taken from different
  parts of the file, stopping at the first confident one; if none of them is
  confident, the language named most often wins, and only a genuine tie falls back to
  English. Five quiet agreements that a book is German are worth more than any one of
  them alone. Costs a few seconds per file, and only where the first sample was
  already in doubt.

- **`--backup` no longer overwrites a backup it already made.** Re-running a book with
  `--backup` used to replace the `.bak` with the previous run's *output*, so after the
  second run "undo" meant "undo the last run" rather than "undo everything" — and it
  happened silently, on exactly the re-run that says the first result was not what you
  wanted. An existing `.bak` is now left alone and this run's original discarded
  instead, so the backup stays the copy from before the tool first touched the book.
  The file's summary line says `earlier backup kept (predates this run)` when that
  happens, since it changes what `--revert` gives you back.

- **`--debug` starts a fresh log for every run.** Previously each run appended to
  whatever log was already beside the audiobook. One run per file makes the log
  searchable again — a hit no longer belongs to who-knows-which run — and lets two
  runs be compared by diffing their logs, which is the usual way of showing a change
  moved nothing. Copy a debug log aside before re-running a book if you want to keep
  it. `--log-file` is unchanged and still appends.

## [0.10.0] — 2026-08-03

### Changed

- **The first pass is several times faster.** The voice-activity scan that opens
  every run used to work through the book one short frame at a time on a single
  thread; it now spreads the timeline across every core, and finds exactly the same
  speech, with not one segment boundary moved — which was the condition for shipping
  it at all. How much of the machine it uses is yours to set with the new
  `--vad-threads`.

- **The second pass stops paying twice for the same stretch of audio.** Where a
  book's silences sit close together, the recognizer used to be handed one short clip
  after another, each costing a full turn no matter how little audio was in it. Each
  read now runs on to the end of the next clip that fits in the turn it has already
  paid for, and the clips that follow come out of what it read — roughly a fifth off
  the second pass's recognizer work on a densely-marked book. Where a read stops has
  not changed: still on a silence, so no announcement is ever cut in half by one. Nor
  is a read believed further than it actually got: where the recognizer fell silent
  partway through — as it can when a long read opens with a stretch of music — the
  rest is read again from a shorter clip rather than taken for silence, so a quietly
  announced chapter cannot be lost to a read that ran past it. Under `--verbose` a
  read that got ahead of itself says so, and says how much of that it gave back.

- **One file at a time, with the whole machine behind it.** Multi-file runs no
  longer process several books at once. That parallelism was worth less than it
  looked — on a GPU it never happened anyway (one file at a time has always been
  the rule there, for video memory), and on a CPU the concurrent files were
  dividing one fixed pool of threads between them rather than adding to it. Giving
  each file everything is what makes the faster first pass possible. Batches of
  many files take about as long as before; a single file is quicker.

- **`--pass3-model` now also backs up mark placement.** Pinning a mark means asking the
  recognizer, over and over in short clips, where the announcement really begins — and a
  smaller model can write a quietly-spoken announcement inside a jingle off as music, at
  which point the search has nothing to find and the mark keeps its estimated position.
  Where a better model is named, the whole search is now run again through it before that
  happens. Only a mark the first attempt could not confirm pays for it, which on a normal
  book is a handful at most, and the heavier model is still loaded only if something
  actually asks for it.

- **`--pass3-model` also gets the second look at an announcement a window lost.** Where
  the speech detector hears someone speaking inside a jingle that the transcript has no
  words for, that spot is read again from a shorter window — and where a better model is
  named, now through that one as well. An announcement quiet enough to be dropped from a
  long window is exactly the kind a bigger model recovers, and the second read was going to
  happen anyway, so this costs nothing beyond loading the model. `--verbose` names the
  recognizer it used.

- **`--verify` no longer replaces a whole set of marks that failed wholesale.** Where
  some marks fail the check, nothing has changed: the confirmed ones are kept and only
  the stretches around the failures are redetected. Where nearly all of them fail —
  previously the case that discarded every mark and redetected the file from scratch —
  the file is now skipped with a warning and left exactly as it was. Marks failing in
  bulk almost always means they were never one-per-numbered-chapter to begin with, which
  is true of every retailer mark set that groups several book chapters into one entry,
  and replacing those is not a decision a batch run should be making by itself. Re-run
  that one file with `--force` and without `--verify` if replacing them is what you
  want. `--verify-threshold` now draws that line by hand instead of forcing the
  from-scratch redetection.

- **Thread counts now default to your machine's physical cores** rather than
  nearly all of its hardware threads. Hyperthreads help a little where they help
  and hurt a lot where they do not, and the tool cannot tell which machine it is
  on; physical cores is the setting that cannot go badly wrong. If you know your
  own machine better, `--whisper-threads` and `--vad-threads` take any number you
  like.

### Added

- **`--vad-threads <n|auto>`** — threads for the voice-activity pre-pass. Each one
  holds a block of decoded audio while it works, so on a machine with many cores
  this is also the knob for how much memory that pass uses. `1` runs it as a single
  uninterrupted stream, exactly as earlier versions did.

- **`--whisper-threads <n|auto>`** — threads for Whisper transcription, replacing
  the old "nearly all logical cores" default.

- **Phrases and titles can now be written per language**, for a batch run over a library
  that is not all in one language. Where `--lang auto` gives every file its own language,
  a single literal phrase can only ever be right for some of them — so the value may
  instead be a list of `[xx]`-tagged entries separated by semicolons:

  ```
  --chapter-phrase "[fr]/(?:premi|1).re partie.? chapitre/;[en]section"
  --title          "[fr]Chapitre;[en]Section"
  --custom         "[fr]/scène/:Scène;[en]/scene/:Scene"
  ```

  Each file takes the entry for its own language. One entry may be left untagged as the
  fallback; a language the value does not name keeps its own built-in default, so naming
  French does not impose French on the German books in the same run. `--title`,
  `--intro-title`, `--prologue-phrase`, `--prologue-title`, `--epilogue-phrase`,
  `--epilogue-title` and `--custom` all take the same syntax, and a `--custom` mapping
  tagged for one language is not even looked for in the others. A value with no tag
  anywhere is taken whole, semicolons and all, so anything that worked before still means
  exactly what it did.

- **`--summary` now names the files it counted.** After the totals it lists every
  file that was skipped, with the reason, and every file left with chapter marks
  still missing, with how many are missing and which chapters they are. In a large
  batch these were the two questions the closing counts raised and could not answer,
  and the per-file lines they were buried in have long scrolled away by then (or,
  under `--quiet`, were never printed). Where the output is colored, the book titles
  are shown in dark cyan.

  Consequently the skipped count now includes the two kinds of skip it used to leave
  out: a file whose codec this ffmpeg cannot decode, and an `--import` run finding no
  sidecar file. Both always said "skipped" on their own result line.

### Removed

- **`-J`, `--jobs`** — there is no longer more than one file in flight to count.
  Naming it still produces an error rather than an "unknown option", so a script
  carrying it says what to use instead.

### Fixed

- **A file left tagged as incomplete could stop a whole batch with an error.** Such a
  file is picked up automatically by a later run, which trusts the marks already in it
  and re-hunts only the ones the tag names — but if those marks had since been removed
  by something else, there was nothing to trust and nothing to hunt, and the run gave
  up on the spot with an internal error instead of moving on. The file is now simply
  written out and given its own name back, since there is no work left in it to record.

- **`--use-gpu` is now refused alongside `--revert` and `--no-op`**, which load no
  speech model and so have no GPU to pick. Both already refused `--cpu-only`; the
  option that names a card instead of refusing one slipped through.

- **Some books quietly pulled the tool's sense of time out from under it.** An
  audiobook stitched together from separately encoded pieces — a common way of
  building an `.m4b` — hands the decoder slightly more audio at every seam than the
  file's own timeline accounts for. The speech scan counted that surplus as play
  time, so the further into such a book it got, the later everything it heard
  appeared to be, drifting by more than a second by the end of a long book. Marks
  that took their position from that scan landed correspondingly late, in the worst
  case past the announcement and onto the chapter title behind it. The scan is now
  held to the file's own timeline, seam by seam. Books without the defect are
  unaffected, down to the millisecond.

- **A chapter mark could land on the previous chapter's closing sentence**, on a book
  whose chapters open with a music jingle. Where the narrator pauses for breath a word
  or two before the end, the speech detector cuts those last words into a fragment of
  their own, and the tool mistook that fragment for a chapter announcement spoken
  quietly inside the jingle — putting the mark back into the chapter before, mid-word.
  Such a fragment is now recognized by the pause in front of it, which a jingle's music
  does not have, and the mark goes back on the announcement.

- **One misheard chapter number could cost a book every mark after it.** Where a
  chapter's spoken number came out wrong and too high, every later chapter was
  measured against it, found wanting, and thrown away — so a book could lose most
  of its marks to a single mishearing, all of them correctly found and correctly
  placed. Chapter numbers still have to ascend through a book, but when one mark
  contradicts the rest, it is now the mark that gives way rather than the rest of
  the book. The worst a mishearing can cost is its own mark — and usually not even
  that, see below.

- **A misheard chapter number is now caught by the marking that follows it.**
  Placing a mark precisely means asking the recognizer about the announcement
  several times over in short, tightly framed windows, and those readings are far
  more reliable about the *number* than the long window that first found the
  chapter. Their verdict now counts: when they agree clearly with each other,
  disagree with the number in hand, and offer one the chapter sequence can actually
  accommodate, the mark is recorded under theirs. This costs no extra recognition —
  the readings were already being taken and discarded — and it is skipped entirely
  under `--quick-marks`. `--verbose` reports each correction.

- **A gap search no longer accepts a chapter number that cannot be in the gap.**
  When the tool goes back over a stretch between two known chapters looking for the
  ones missing between them, it knows exactly which numbers it is looking for. It
  now says so: a number from outside that range is questioned and re-read before it
  is believed, and one at or beyond the chapter closing the gap is refused outright.
  Previously such a reading was taken at face value on the reasoning that the wider
  window used for the search was already the best available look — but the wider
  window is precisely what produces this kind of mishearing.

- **A mark whose number contradicts the chapters around it is now repaired from
  them.** Between a chapter 13 and a chapter 15 there is exactly one number a mark
  can carry, whatever was heard there, and the chapters that settle it are often
  found long after the misreading happened. Where the surrounding chapters leave a
  single possibility, the mark is simply renumbered; where they leave several, the
  audio is read again and held to that range. This runs before the tool decides
  which chapters are missing, so a book no longer spends passes hunting through
  hours of audio for a chapter that was never missing.

- **A file could keep a `.missing-marks` tag it had earned its way out of.** The tag
  is a note that chapters are still missing, and a run that finds them all takes it
  back off — but only the resume path did so, which never picks up the unnumbered
  `<name>.missing-marks<ext>` form. Redoing such a file with `--force` left the tag on
  a book that was now completely marked. Any completed run now hands the file its
  own name back, and with `--debug` the log beside it follows; where a log under that
  name is already there from the run that left the tag, the two are joined rather
  than one replacing the other.

- **A chapter mark could land about a second before its announcement**, on a book
  whose chapters open with a music jingle. Pinning down exactly where the narrator
  starts speaking means asking Whisper the same question at a series of positions,
  and a smaller model occasionally answers "no" for a stretch of audio it can
  plainly hear — which was taken as "the announcement starts here" and cut the mark
  short. Those stretches are now checked rather than believed. Marks that were
  already right do not move; the extra checking costs a few seconds per chapter.

- **Two chapter marks could land on one announcement under different numbers.** Where a
  chapter's spoken number was heard as its own neighbour's, one pass could mark the
  announcement as chapter 12 while another, reading the very same words correctly, marked
  it as chapter 13 — leaving two entries a hundredth of a second apart. Nothing caught it:
  every safeguard against a misheard number asks whether the number fits the chapter
  sequence, and one misheard as its neighbour fits perfectly. What gives it away is not the
  numbering but the clock, since two chapters cannot begin at the same moment. Marks that
  land on top of each other are now recognized as the single announcement they are, the
  audio is read again to settle which number it really carries, and one mark is kept.

- **The second look at a stubborn gap is now a second look all the way through.** A gap
  that survives being transcribed in full is read once more with every window shifted by
  15 seconds — but past the first ten minutes of it, only *probably* differently framed:
  the reading is cut into chunks at convenient pauses, and both attempts could pick the
  same pause and then re-read everything after it exactly as before. Where a second look
  is possible, both now cut at fixed positions instead, so every chunk of the re-read
  really does sit 15 seconds off the one that failed. Only gaps longer than ten minutes
  were affected.

- **`--verify` could not read most chapter mark titles, and said the file had nothing
  to check.** It looked for digits anywhere in a title and otherwise expected the number
  to be its very first word, so the ordinary written form was unreadable in every
  language: "Chapter Five", "Kapitel Fünf", "Capítulo Cinco", "Chapitre Cinq", and Roman
  numerals anywhere. A file whose marks are all titled that way was reported as having
  no checkable marks and skipped — verification asked for, and nothing verified. Titles
  are now read the way an announcement in the audio is: from the chapter word outwards,
  in both word orders, with digits, spelled-out numbers and Roman numerals all
  understood. A title written in a different language than the audio is read too, so an
  English-tooled tagger's marks on a German book still check out.

  Two things that used to go wrong in the other direction are gone with it: a heading
  behind the number is no longer mistaken for it ("Chapter Two: Seven Days Later" was
  read as chapter nine), and a year in a title is no longer read as a chapter number
  ("Capitolo uno - Anno 1984" was read as chapter 1984, and enough such marks used to be
  enough to get a file's marks discarded). Under `--verbose`, every title that still
  cannot be read is now named in the log.

- **A mark could be left unpinned because the search looked in the wrong place.**
  Pinning a mark means searching the stretch of audio the announcement was heard in,
  bounded at the far end by the clip it was found in. Whisper routinely reports a
  clip's last sentence as running well past the clip itself, though, and where that
  happened the search covered only audio in front of the announcement, found nothing
  there, and left the mark at its estimated position — reported exactly like the
  ordinary "this was only a passing mention" outcome. That bound now yields to what
  was actually heard, and under `--verbose` a search that never reached the
  announcement says so instead of sharing a line with one that looked and found
  nothing.

- **The same search could also stop just short of the announcement.** Whisper sometimes
  timestamps a sentence seconds before the words are actually spoken, and where it did
  that to the announcement itself, the stretch searched for it ended in front of it —
  the search ran to the far end still hearing the announcement ahead, and then gave up
  with nothing to show. It now takes that far end for what it is, the last place the
  announcement is known to still lie ahead, and starts confirming from there. Where the
  announcement is further off than that can reach, the mark is left at its estimated
  position as before, and `--verbose` now says which of the two happened.

## [0.9.1] — 2026-07-31

### Added

- **`--debug` writes a full troubleshooting log beside each processed file.** It
  records everything the ordinary log carries plus the raw material behind it —
  the settings in force, every silence found including the ones the threshold
  rejects, the voice-activity segments and non-speech regions, every Whisper
  transcript segment by segment, and the mark-refinement probes that appear
  nowhere else. Meant for reporting a mark that landed somewhere inexplicable,
  where the alternative was re-running an hour of decoding by hand. See the
  manual's logging section; it is not on the `--help` list.

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
  at face value, leaving dozens of chapters "missing" and sending the gap re-probe
  off after them. Now, when a number would either leave more than three chapters
  missing at once or falls below the chapters already found, the spot is read
  again: first with `--pass3-model` when it names a better model than the probing
  one, then from two differently framed windows around the announcement. A
  re-reading is adopted only if it actually continues the chapter sequence, so a
  genuine jump in a book's numbering is left alone. `--verbose` reports each attempt
  and what came of it. A number below the sequence used to be discarded unheard; now
  it is discarded only once re-reading has failed to make sense of it — which turns
  some of those into the chapter they always were.

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

- **Pick the GPU by name.** `--use-gpu <text>` runs Whisper on the GPU whose name
  contains that text, matched case-insensitively against any part of it, and
  `--list-gpus` prints the names your machine reports:

  ```
  > abchapterize --list-gpus
  Vulkan GPUs on this machine:
    0: <integrated GPU name> (integrated)
    1: <discrete GPU name> (discrete)
  ```

  A request that matches no GPU, or more than one, stops the run and lists what
  is available rather than quietly using a different card. Names are matched
  instead of numbers on purpose: the numbering is the driver's, and the same
  machine has been seen to report it in opposite order depending on whether the
  session was local or remote.

- **`--mark-lead <seconds>`** (`-k`) sets how far in front of the announcement a
  mark is placed. Marks are located just as precisely whatever it is; all it
  decides is how much lead-in you hear before the narrator starts, which is a
  matter of taste. `0` marks the measured onset itself. It applies under
  `--mark-before-jingle` too: in full for a chapter with no jingle in front of it,
  and as a back-off into the pause before the jingle where there is one, capped at
  that pause's own length so the mark can never reach back into the previous
  chapter's narration.
- **Marks for anything else the narrator announces.** `--custom` takes
  `phrase:title` mappings, several of them separated by semicolons:

  ```
  abchapterize --custom "interlude:Interlude;/time[- ]?line/:Timeline" book.m4b
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
  list: `abchapterize -r "Audiobooks" "More books" "one-off.m4b"` works, mixing
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
  was heard in when none of them confirmed. That first step almost never earned
  the minutes it cost, and the search it was meant to save turned out to be
  quicker on its own. It has been dropped, so every mark now goes straight to the
  search, with unchanged accuracy. With `--verbose`, refinement announces itself
  once per mark (`refining mark at … - narrowing in on the phrase between … and …`)
  instead of reporting a candidate walk first.
- **The progress bar leads with the percentage**, with the phase moved behind it
  into a separated section of its own — `[####----]  42% | Pass 2 | ch 6 | …`
  rather than `[####----] Pass 2  42% | ch 6 | …`.
- **Marks now sit slightly further before the announcement.** Since marks became
  accurate to a tenth of a second, the old lead-in turned out to be cutting it too
  fine: a chapter would occasionally start so close to the first word that its
  opening consonant was clipped — and a hard one, like the "K" of "Kapitel", is
  easy to lose without a listener being able to say whether they heard it. Use
  `--mark-lead` to set it back, or anywhere else you like.
- **A single discrete GPU is now preferred automatically**, and the startup line
  names the GPU in use instead of only the backend:

  ```
  Whisper model "turbo" loaded (Vulkan backend on <GPU name>), 3 file(s) to process.
  ```

  Previously the Vulkan runtime took whichever GPU it enumerated first, which on
  a machine with an integrated GPU beside a discrete one could be the integrated
  one — many times slower, with nothing in the output to say so. Machines with
  one GPU, several discrete GPUs, or none that report themselves as discrete keep
  the runtime's own choice; use `--use-gpu` to decide those cases. The device is
  named either way, which also makes a less obvious case visible: a software
  rasterizer like `llvmpipe` is a real Vulkan device, so a container or WSL2
  distro without GPU passthrough would report "Vulkan backend" while quietly
  running on the CPU. It now says `on llvmpipe`. A run that steps aside for the
  Vulkan runtime's own `GGML_VK_VISIBLE_DEVICES` variable names both the variable
  and the GPU it leaves in charge, rather than falling back to a bare "Vulkan
  backend" that looks like the naming failed.
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
  to comb the surrounding audio a tenth of a second at a time, which for a badly
  misplaced mark meant hundreds of transcriptions and a long stretch of apparent
  silence on that one mark. It now closes in on the announcement instead, roughly
  halving the search each time, and finds the same position in a couple of dozen
  checks. Marks that were already being placed correctly are unaffected.

- **`--verbose` now reports mark refinement** instead of going quiet while it
  works. Each mark logs the candidates it is about to check, and — for the rare
  mark whose announcement none of them confirm — the stretch it is about to
  search and where in it the announcement turned out to be.

- **`--import` now rejects the title options** — `--title`, `--intro-title`,
  `--prologue-title` and `--epilogue-title` — instead of accepting them and doing
  nothing. An imported mark carries the title its sidecar gives it and no intro mark
  is prepended, so naming one was always a promise the run could not keep. Every other
  option `--import` cannot act on was already refused this way.

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
  just been recovered from a gap, because that recovery taught the window nothing — so
  the same shape of chapter cost its own retry pass over and over in one book. A chapter
  recovered from a gap now also reports how far into its window the announcement actually
  ended, and the window widens to cover that for the rest of the file. One recovery may
  widen the window by at most 25 %, so a single outlier chapter cannot leave every later
  probe running at the maximum window for hours; an extreme reach is granted over several
  recoveries instead.
- **A chapter recovered from a sequence gap is no longer found several times over.**
  The retry that closes a gap kept going through the candidates behind the recovered
  chapter, and because they cover the same stretch of audio each one found that same
  chapter again — placing, refining and then discarding an identical mark every time,
  for minutes of needless work on a long book. The retry now stops the moment the gap
  is closed, and an already recovered chapter cannot be picked up a second time.
  Detection then carries on from where it was, with the recovered chapters' own
  silences and jingles folded into the two self-tightening settings first.
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
  nothing, and left the mark where the first estimate had put it — which could be a
  full second *into* the spoken announcement, exactly where a listener notices. The
  search now reaches far enough behind a suspect timestamp to cover the words it was
  meant to describe.
- **A mark near the end of the stretch it was found in is no longer placed at
  random.** Deciding whether the announcement is still audible from a given point
  means asking the recognizer about the audio after it, and when the mark sat close
  to the end of the transcribed stretch there was barely a second of it left to ask
  about — at which length the answer is a coin flip, and one wrong answer could send
  the search half a minute off. Refinement now always listens to a few seconds,
  however little is nominally left.
- **A batch run no longer garbles its progress bars when it cannot keep its resume
  record.** The warning about an unwritable `.abchapterize-progress` file was printed
  straight past the display, which then erased the wrong lines and left the bars
  scattered across the screen for the rest of the run. It now goes through the same
  channel as every other message, and reaches the `--log-file` as well.
- **Interrupting a batch run with Ctrl+C no longer occasionally ends in a crash
  report.** A file starting at the exact moment another finished could race the
  cancellation and take the run down with an unhandled error instead of the ordinary
  "Aborted by user."
- **A file whose decoding provokes a flood of complaints from ffmpeg no longer hangs
  the run.** Enough of them filled a buffer nothing was emptying, and the two sides
  waited on each other indefinitely.
- **`--mark-before-jingle` no longer stops on a musical sting inside the jingle.** A
  drum hit or a stab of vocals partway through the music could pass for the previous
  chapter's last words, leaving the mark seconds deep in the jingle instead of at its
  start — audible as a run of music before the chapter begins. Such a transient is now
  recognized for what it is wherever it sounds.
- **`--mark-before-jingle` no longer lands at the start of the hush before the
  jingle.** Where a faint transient sounded within a few hundredths of a second of the
  silence ending, the mark skipped past the silence entirely and came to rest on the
  previous chapter's closing words — a second or two of the old chapter left playing
  before the new one. The mark now lands where the music begins, as intended.
- **A chapter whose pause falls just short of `--min-silence-length` is now found.**
  Where a narrator's chapter break is right at the limit — no jingle, just a breath's
  pause — every chapter of the book can sit a fraction under it, and the tool looked at
  none of them. With `--pass3-model` naming a better model, a gap that the cheap retry
  cannot close is now swept for shorter pauses first, in steps of a tenth of a second
  down to half a second under the setting, stopping as soon as the missing chapter turns
  up. That is both faster than transcribing the whole gap and, on the book this came
  from, the only thing that found some of the chapters at all: the recognizer had simply
  dropped their announcements from its reading of the long stretch, while a short probe
  aimed at the spot read them without trouble. The sweep stops early on a long gap where
  it would end up costing more than the full transcription it is trying to avoid.
- **A gap that survives being transcribed in full is now read one more time, with
  every decode shifted by 15 seconds.** A gap that outlives a complete
  transcription is not audio nobody read — every second of it was — but audio the
  recognizer read wrongly, and by far the likeliest reason is where the
  announcement happened to fall inside the 30-second windows Whisper decodes in.
  One landing right on a window border can drop out of the transcript entirely
  while the text around it reads as though nothing were missing. The re-read
  displaces every window by half of one, which puts whatever sat on a border as
  far from one as it can get — and reads back cleanly, at high confidence, a
  chapter that had vanished from the very same stretch of audio. It runs unless
  `--pass3-model` names a *lighter* model than `--model`, which is the one setting
  that unambiguously says "don't spend more time on the stragglers", and it always
  runs for `--trailing-scan`, where asking for the scan is itself the statement
  that the time is worth it.

- **An announcement whose number cannot be read is now re-read during the ordinary
  scan**, not only during the full-transcription pass. This matters most for the one
  place the later pass can never reach: an announcement past the last chapter found is
  inside no gap, so without `--trailing-scan` nothing ever looked at it again — which
  is how a book's closing chapter could go missing after its number came out as
  gibberish. The spot is now read again straight away, first with `--pass3-model` where
  it names a better model and then from two differently framed windows, and a reading is
  only believed if it continues the chapter sequence, so an in-book mention of the word
  "chapter" still cannot produce a mark. The recovered chapter is marked exactly where
  it was first heard.

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
