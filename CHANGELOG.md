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

## [1.0.0] — unreleased

### Added

- **`--summary` now closes a pushing run with the books Audiobookshelf did not get.** After the
  listings of skipped, empty, unfinished and low-confidence files comes a fifth: every file whose
  marks did not reach the server, and why — a title that matched several books, a file that
  matched none, a chapter set held back because it still has a gap. The marks are in the files
  either way, so on a shelf of two hundred audiobooks this is the list to act on once the per-file
  lines have scrolled away. A book the server already holds the same marks for is left out: nothing
  was sent for it, but nothing needed to be. `--abs-push-only` reports the same refusals under
  *skipped*, sending being the whole of what it does with a file.

- **`--version` now also says which platform build you are running, and on what system.**
  A second line reads `win-x64 on Microsoft Windows 10.0.26200`, or whatever the equivalent is
  where you are — and says so too when the process is running as one architecture on a
  machine that is another, such as an x64 build under Windows-on-ARM. The same line now opens
  every `--log-file` and every `.debug.log`, between the version and the command line, so a log
  attached to a bug report answers "which platform" without being asked.

- **The progress bar fills in only the stretches a pass actually reads**, and its percentage now
  counts that work rather than the file. A pass working a handful of sequence gaps leaves the
  audio between them empty instead of filling it in as it goes past — that audio was skipped, not
  read — and reaches 100 % when the gaps are done, though its bar stops well short of the file's
  end. The fill still says where in the book the reading head is; the percentage now says how much
  of the work is done. Marked with `#` against `-` rather than by colour alone, so it reads the
  same under `--color never` or in a screenshot.

- **`--max-chapters` now also applies to what Audiobookshelf holds.** A chapter set left with a
  gap is normally kept back when the server's list for that book is at least as long, on the
  grounds that the longer list might be the better one. A list longer than `--max-chapters` is
  one you have already told the run is bogus, so it no longer earns that protection and the
  partial set is sent over it — which is what you want for a book the server has filled with a
  "chapter" every few minutes.

- **`--lang` takes a list of candidate languages.** Give it several codes separated by commas —
  `--lang no,da,sv` — and each file's language is still worked out individually, but only from
  the languages you named. It costs nothing extra: the detector already weighs every language it
  knows in one pass, and the list only decides which of them may win. A file that fits none of
  them falls back to the **first** language named rather than to English, a list being as much a
  statement of what a shelf mostly is as of what it might be, so order matters. Codes this tool
  has no number words for may appear in a list exactly as they may on their own.

- **Norwegian and Czech are now understood**, bringing the count to thirteen: chapter
  announcements, spoken numbers 0–999 as cardinals and ordinals, and the localized `--lang no`
  and `--lang cs` defaults for the chapter phrase, the title words and the prologue/epilogue.
  Norwegian is read in both of the counting systems its audiobooks use — the modern "tjueen"
  for 21 and the older "enogtyve" it replaced, along with the conservative spellings that
  travel with it — and Czech in both the formal "dvacet jedna" and the everyday
  "jedenadvacet", with the ordinal agreeing in gender as the language requires.

- **`--newer-than` (`-w`) works only on what arrived recently.** Give it an age — `12h`, `7d`,
  `1.5d` — and everything older is passed over, which is what you want for a shelf you keep
  marked as it fills up rather than re-walking it from the top each time. Local files are judged
  by their last-write time; with `--abs` it is the date the server says the book joined its
  library. It narrows alongside `--filter` rather than replacing it, applies to `--revert`, and
  is enough on its own to satisfy `--no-op`.

- **Audiobookshelf, directly.** With `--abs` (`-A`), books are named by what an
  [Audiobookshelf](https://www.audiobookshelf.org/) server calls them rather than by path:
  `library:Discworld`, `series:Tiffany Aching`, `collection:Favourites`, `item:<id>`, a bare
  title, or `all`. Each selected book is downloaded to a temporary copy, marked exactly as a
  local file would be, and its finished chapters are sent back to the server; the copy is then
  deleted. Nothing on the server changes but the chapter list, and every list sent is read back
  off the server afterwards and checked against what was sent — a server holding something else
  is reported as a warning rather than passed over in silence. Books the server already has
  chapters for are skipped without `--force`, and books held as more than one audio file are
  reported and passed over. `--no-op` lists what a selector picked without fetching anything,
  which is worth doing before a selector that turns out to name a hundred books.

- **`--abs-pull` brings Audiobookshelf's chapters back into your files.** The mirror of
  `--abs-push`: before working on a local file, the run asks the server what chapters it holds
  for that book and treats them as the file's own — the server's list wins, the file's fills in
  where the server has none. A file the run then has nothing to detect for is written the pulled
  list, so an edit made in Audiobookshelf's web interface ends up in the audio file as well.
  `--abs-pull-only` does just that and no detection at all, for putting a whole shelf's marks
  back in one pass.

  **`--abs-pull --abs-push` reconciles both directions at once**, and it is the one combination
  of the server modes that is allowed: whatever list the run settles on is written to the file
  unless the file already had it, and sent to the server unless the server already had it. Two
  sides that agree are left alone, so running it twice over the same shelf does nothing the
  second time. `--verify` fits in the middle of that, checking the marks before either side is
  given them, and **`--abs-sync` is a shorthand for exactly that trio** —
  `--abs-pull --verify --abs-push` — which is the whole of what it does.

- **`--abs-retry` waits out a server that is not answering.** Every exchange with
  Audiobookshelf — signing in, listing a library, fetching a book, sending its chapters — is
  tried again for up to three minutes by default, a minute between attempts, so a server
  restarting in the middle of a long batch no longer ends it. `--abs-retry 0` goes back to giving
  up at the first failure. What the server refuses on purpose — an item it does not know, a
  right the account has not got — is still reported straight away; there is nothing to wait for.
  A download that breaks off part way through is started again once.

- **`--abs-push` marks your own files and tells the server as well.** An ordinary run — the
  marks go into each file exactly as they always have — that then sends the finished chapter
  list to Audiobookshelf, for a shelf you keep on this machine and also serve from there. Each
  file is matched to a book on the server the same way `--abs-push-only` matches it; one that
  matches nothing is still marked and simply not sent. Only a complete chapter set is sent, so a
  file left with a gap keeps its partial marks and goes to the server once it has been finished.

- **`--abs-push-only` sends chapters a book already has to Audiobookshelf**, detecting nothing
  and changing no file. With `--abs` the selected books are fetched and read; without it, the
  local files you name are matched against the server's libraries by their album tag, their
  title tag, the folder they sit in, or their file name, in that order. Useful for putting a
  shelf you marked long ago onto a server that never saw the marks.

  **A book has to be the same recording as the file, not merely the same title.** One whose
  play time differs by more than a minute is passed over with a note giving both — it is one
  part of a split book, or an abridgement, or another edition, and its chapter list describes
  none of them. This applies wherever a local file is paired with a book, so every mode that
  sends marks up or brings them down is covered by it.

- **`--abs` takes any format ffmpeg can read**, not only the ones chapter marks can be written
  into. A `.flac` or `.ogg` book on a server is now marked like any other: the marks go into
  Audiobookshelf's own database, so the container's inability to carry them never comes up.
  Outside `--abs` nothing changes — there the file itself is the destination.

- **The server is named by `--abs-url`** — `http://host:13378`, `host:13378`, or just `host` —
  and the credentials by `--abs-key`, or `--abs-user` with `--abs-password`. All of them,
  along with `--abs-temp` for where the downloads land, can come from the environment instead
  (`ABCHAPTERIZE_ABS_URL`, `ABCHAPTERIZE_ABS_KEY`, `ABCHAPTERIZE_ABS_USER`,
  `ABCHAPTERIZE_ABS_PASSWORD`, `ABCHAPTERIZE_ABS_TEMP`), which keeps a key or a password out
  of your shell history and out of the process list.

- **`--lang` takes Whisper's three-letter codes** as well as the usual two-letter ones, so a
  language Whisper spells with three — Hawaiian `haw`, Cantonese `yue` — can now be named at
  all. The same goes for the `[xx]` tag that scopes a phrase, a title or a `--custom` mapping
  to one language of a mixed batch.

- **Two more platforms build from source: Linux ARM64 and Apple Silicon.** Neither is
  released, and neither has ever been run by anyone on this project — the Windows and
  Linux x64 builds on the Releases page remain the only ones anybody can vouch for. Linux
  ARM64 suits a small ARM home server and is CPU-only, the GPU backends having no ARM64
  build of their own. Apple Silicon gets a page to itself,
  [doc/building-on-macos.md](doc/building-on-macos.md): what should work, what is a coin
  flip, and the one thing that will stop you before you start. Intel Macs are out of scope
  permanently, for a reason spelled out there.

- **macOS is no longer handed Linux's advice.** Where ffmpeg is looked for, what `--help`
  says about it, and what the `could not be found` error suggests installing now name
  Homebrew and MacPorts and their directories rather than apt and Linux's. `--list-gpus`
  says plainly that GPU selection does not apply on a Mac, instead of offering it CUDA.

- **The manual is now also readable as a set of web pages.** `doc/html/index.html` in a
  clone opens the same manual split into one page per section, with a navigation column, a
  contents table two levels deep and a permalink on every heading. It is generated from the
  Markdown, which stays the source of truth, and it is deliberately not part of the download
  archives — those already carry the manual itself.

- **`--no-rename` leaves your file names alone.** A file left with a chapter-sequence gap is
  normally renamed to `<name>.missing-marks-3-7.<ext>` so the hole is visible and a later run can
  pick it up. Where the name is not yours to change — a media server keying its database off the
  path, a seeded or hard-linked file, somebody else's naming scheme — this withholds the tag: the
  marks that were found are still written and the file is still reported as incomplete, it simply
  keeps its name. The cost is the automatic resume, since the tag is what tells a later run which
  chapters to go back for; finishing such a file takes `--force`. A tag an earlier run left behind
  is still taken off once the file is complete.

- **`--verify-only` checks a library without changing it.** Every selected file's marks are read
  against the audio, what became of each is reported, and no file is touched — useful before
  committing to a run that changes things, or after one as a second opinion. It is the same as
  writing `--verify --no-op`, which used to be refused. With `--summary` the run closes with the
  two listings that answer the question it was asked: every file that could not confirm all of its
  marks, naming the ones that failed, and every file carrying marks nothing in the run could check
  at all.

- **`--verify` now checks prologue, epilogue and `--custom` marks too.** Where a mark carries no
  chapter number, the question asked of it is whether the phrase its title belongs to is really
  spoken there. The answer is reported and nothing more: a mark without a chapter number opens no
  gap in the numbering, so there is nothing detection could be sent back to fix, and which files a
  run redetects or leaves alone is decided by the numbered marks exactly as before. A mark this run
  has no phrase for — another tool's, or one whose `--custom` mapping you left off the command
  line — is reported as unverifiable rather than as wrong. Each one that does confirm is counted
  into the progress line's `(+N)`, so a prologue or a `--custom` mark going past is visibly
  passing rather than leaving the bar unmoved; one that fails does not lower that count.

- **`--summary` now reports a run that was interrupted or failed.** Ctrl+C part way through a
  shelf of two hundred audiobooks used to end the run with nothing at all to show for it —
  which is precisely the run whose report is worth having, since the listing of files still
  missing chapter marks is what the next command line gets built from. The block is now printed
  for the files the run got through, its first line saying that the run did not finish and how
  far it got. `--revert` and `--cleanup` do the same.

### Changed

- **A file whose name matches several Audiobookshelf books is now settled by its play time.**
  Matching a local file against the server has always insisted the two be the same recording,
  to within a minute, before anything is sent or pulled. That test now also breaks a tie: where
  a title matches more than one book — a series carrying both *Stalker* and *Stalker Strikes
  Back*, say — the books that cannot be this recording drop out, and if one is left, it is the
  match. Files that used to be skipped with "matches 2 books on the server" now go through.
  Nothing is matched on weaker evidence than before: a book settled this way had to clear the
  same test it would have faced had the name pointed at it alone. Two books that both fit are
  still reported as ambiguous, and the note now lists only those two rather than every book
  sharing the name.

- **The manual now lives on the web, and no longer as HTML inside the repository.** It is at
  <https://abchapterize.anetos.de>, published with each release. A clone previously also carried
  a rendered copy under `doc/html`; that copy is gone, since the website makes it redundant.
  `doc/manual.md` is unchanged and still the source of truth, readable in a clone and on GitHub
  as it always was, and the release archives never contained the HTML in the first place.

- **A chapter set held back from the server words its reason like the other push refusals.** The
  result line now reads `not sent to ABS (chapters are still missing and the server already has 34
  mark(s))` where it used to read `not sent to ABS while chapters are missing (it already has 34)`.
  Same rule, same decision; the wording is now the one the new `--summary` listing prints, so the
  two cannot describe an outcome differently.

- **A file that matches several books but is none of them says so.** Where every book a name
  matched is the wrong length — most often one part of a split book that has not been joined
  yet — the note no longer asks which of them you meant, an answer that would only have been
  refused a step later, and reports that the file is none of them instead.

- **`--max-chapters` is now refused with `--abs-push-only` and `--abs-pull-only` instead of
  being quietly ignored.** Neither mode detects anything or judges the marks it is given: one
  sends the file's own list to the server, the other writes the server's list into the file.
  The option had no effect in either, which is worse than an error message — it read as though
  a ceiling were in force. Every other option those modes cannot honour was already refused.

- **The progress bar marks its stretches with `~` where there is no colour.** The bar tints the
  parts of the book a pass is going to read, which said nothing at all on a terminal without
  colour, under `--color never`, or in a screenshot — a gap scan looked exactly like a whole-file
  pass that had stalled. Those cells now draw as `~` instead of `-` until the pass reads them, so
  the bar carries the same two facts either way: `~` is marked out and still to come, `#` is read,
  and `-` is audio this pass skips entirely. Nothing changes on a console that does have colour.

- **`--max-chapters` and `--max-chapter-number` now stand in for one another.** They answer
  related questions — how many marks a book plausibly has, and how high its chapter numbers
  plausibly run — and giving only one left the other on a default far looser than what you had
  just said. Give `--max-chapters` and detection will not believe a chapter number above it, since
  a book's chapters are a subset of its marks; give `--max-chapter-number` (or `--chapter-count`)
  and a mark list longer than that many chapters plus ten is written off as bogus, the ten being
  room for an intro, a prologue, an epilogue and whatever else sits around the chapters, with any
  `--custom` phrases that declare a limit counted in on top. A `--custom` phrase with no `once`
  and no `max=<n>` can produce a hundred marks by itself, so none of that second inference is
  drawn while one is in play, and `--max-chapters 0` — which asks for a file's marks to be thrown
  away rather than saying how long the book is — implies nothing about chapter numbers. Whichever
  option you give outright is always used as given.

  One consequence worth knowing before a large `--abs` run: `--max-chapter-number` now implies an
  opinion about mark counts, so already-marked books are downloaded and judged with the audio in
  hand instead of being passed over from the library listing. That is what `--max-chapters` has
  always done, and it is what stops either option quietly skipping a book the other would have
  processed.

- **The progress bar is now a map of the whole book, in every pass.** The passes that read only
  part of a file — chasing the gaps in a numbering, the stretches a music-first read left over,
  the file's tail — used to give their bar a scale of their own, so it ran a tidy 0 to 100 %
  while saying nothing about where in the book the reading actually was, and the same position
  meant something different from one pass to the next. Every bar now spans the file, its
  percentage is a position in the book, and the stretches a pass is going to read are marked out
  inside it: dark cyan for all of them, cyan for the one being read right now. A pass that reads
  part of a book therefore finishes short of 100 %, which is the point — that is where the
  reading stopped.

### Fixed

- **A chapter announced behind a spoken heading is no longer thrown away.** Some books name the
  setting before the number — "*the Milky Way. Chapter 14.*" — with barely a breath between the
  two, and the recognizer writes both as one line. That left the announcement looking like a
  phrase in mid-sentence rather than a heading, and the chapter was dropped even though it had
  been heard perfectly, several times over. A punctuation mark and a space in front of the
  announcement now count as setting it off, alongside the pause and the transcript-segment start
  that counted before. Over a 159-book library the change recovered three chapters and admitted
  nothing that was not one. A number spoken alone is unaffected: its claim to being an
  announcement is still the pause around it.

- **A run of missing chapters under a chapter number the run distrusted is now looked for.** When
  a number leaves an implausibly large hole beneath it, that number is treated as possibly misheard
  and the hole is not reported or hunted — which is right, because hunting an imaginary hole can
  cost hours. But it also meant the one cheap pass that could have settled the question, the
  re-read of the gap on the larger model, skipped the very stretch that needed it. That pass now
  looks there too: it re-reads only the pauses already found in the stretch, so it costs minutes,
  and if it turns the missing chapters up they are marked and the number is taken off suspicion.
  Everything that *reports* a gap still waits for that proof. One audiobook in testing was missing
  six real chapters this way — all six announced, all six heard cleanly by the larger model.

- **`--summary`'s per-file average no longer under-reports a `--verify --fix` run.** Files whose
  marks were nudged onto their announcements counted toward the average but contributed none of
  their time to it, so a run made up of them reported an average of nothing at all. Every other
  figure in the summary was right.

- **A `--set:` chunk overlap at or above its own chunk length no longer hangs a run.** Giving
  `GapRetryChunkOverlapSeconds` a value at or above `GapRetryChunkSeconds` left one step of the
  `--verify` gap re-read unable to move, so it re-transcribed the same few seconds for ever.
  Such a pair is now skipped rather than followed. Only reachable through `--set:`; the
  same combination was already handled everywhere else it occurs.

- **An Audiobookshelf key or password typed on the command line no longer ends up in the log.**
  Every log a run opens records the command that produced it, and it recorded it exactly as
  typed — so an `--abs-key` or `--abs-password` given there went into the `--log-file` and into
  every `.debug.log`, which is the file you are asked to attach to a bug report. Both values are
  now replaced with `***`; everything else about the line is unchanged. A key kept in the
  environment or in a `--config` file was never affected, and still is not.

- **A chapter number the run could not corroborate no longer takes the rest of the book with
  it.** Such a number kept its mark and became the count everything after it was measured
  against, so on a book where one announcement was misheard as a much larger number, every real
  chapter behind it read as sitting *below* the numbering — which is the shape a book divided
  into parts has, and three of them in a row were enough to invent one. The result was a
  one-part book whose titles all named a part, and a wrong number the run could no longer put
  right, because the invented split hid it from the two later checks that would have caught it.
  A number nothing corroborates now leaves the count where it was, so the chapters after it stay
  ordinary chapters and the misreading is usually renumbered from its neighbours without the
  audio being read again. Where such a number used to displace a real chapter announced behind
  it, that chapter now survives.

- **`--abs-push` no longer withholds a partial chapter set from a book the server has nothing
  for.** It used to send only a complete set, so a book left with an unresolved chapter-sequence
  gap kept its marks to itself — including when Audiobookshelf had no chapters for it at all,
  which is precisely the case where a nearly-complete list is worth having. Such a set is now sent
  whenever the server holds *fewer* marks for that book than the run does, and still kept back when
  the server's list is at least as long, so a longer list is never replaced by a shorter one.
  (`--abs-push-only` sends whatever a file carries and always has.)

- **`--export` now writes a sidecar wherever marks are written.** A file left with an unresolved
  chapter-sequence gap, a `.missing-marks` file being resumed and a `--verify --fix` rewrite all
  wrote their marks into the audio file and quietly skipped the sidecar — which is exactly the
  case somebody reaches for `--export` in. All three export now, the sidecar is named after the
  name the file ends up under where the run renames it (so `--import` finds it), and a sidecar
  already sitting there is overwritten rather than left to go stale.

- **`--verify` works much harder before writing a mark off.** A mark whose chapter number the
  first reading does not turn up is now read again from several shorter, differently placed
  windows, on the heavier `--upgrade-model` recognizer. Whether the chapter word is heard at all
  depends on where a window happens to start about as much as on the audio itself, so the same
  seconds read from a different angle — by a better listener — recover marks a single reading
  writes off, and fewer good marks are needlessly redetected. A file with failing marks may now
  load the upgrade model and take noticeably longer than one where everything checks out.
- **`--revert` no longer gives up on the rest when one backup will not go back.** A file held
  open by a player, or a folder that has turned read-only, used to end the run there and leave
  every remaining backup unrestored. Each one is now reported and the others still restored, the
  run ending with exit code 1 so a script cannot mistake a partial revert for a complete one —
  the rule `--cleanup` already followed.
- **A file that cannot be renamed or moved is now named in the message.** Renaming is the last
  step of writing a book's marks, of taking a `.missing-marks` tag off, and of `--revert`; when
  one failed — the audiobook open in a player, a folder that has turned read-only — the run
  ended on a bare "Access to the path is denied." naming no file at all, which in a batch of two
  hundred left nothing to act on. The error now says which file, where it was going, and, for a
  book whose marks were already written, that only its name is still the old one.
- **A long Audiobookshelf run no longer stops working part way through.** Recent servers hand
  out a session that lasts an hour, which is shorter than a job of more than two or three books,
  so every request after that came back "401 Unauthorized" — the marks were still written to the
  files, but nothing more reached the server. A run given `--abs-user` and `--abs-password` now
  signs in again whenever the server says the session has run out, and carries on where it was.
  An API key cannot be renewed this way, so a key that has passed its expiry date is reported as
  refused rather than worked around.
- **A chapter is no longer lost when one announcement is heard as two.** Where a single reading
  came back as the chapter plus a phantom carrying the next number, the phantom claimed that
  number, and the real chapter bearing it was turned away every time it later came up — while the
  progress line went on counting it as found. Such a pair is now recognised as soon as it is made,
  rather than after every pass that could have used the answer.
- **The count of chapters still missing is refreshed once more before a file is written**, so the
  figure the progress line finishes on matches what the file actually got.
- **Text outside your console's code page no longer prints as question marks.** Windows still
  starts a console on a legacy code page in most locales, so a Cyrillic, Greek or Japanese file
  name — or a chapter title in one — arrived on screen as a row of `?`. What went into the audio
  file was always right; only the report of it was wrong. The console is now switched to UTF-8
  at startup.
- **A run no longer stops dead on a digit it cannot read.** A transcribed word combining a
  non-ASCII digit — Arabic-Indic, Devanagari or the full-width forms — with an ordinal ending
  ended the entire batch with an error, rather than being passed over like any other word that
  is not a chapter number.

## [0.12.1] — 2026-08-21

### Added

- **The tuning constants detection runs on can be overridden for a run**, with
  `--set:<class>.<constant>=<value>`. An in-depth feature: the numbers are calibrated
  against real audiobooks and changing one on a hunch is a good way to lose a chapter
  quietly, so it is documented in the manual only, alongside a new `doc/constants.md`
  listing every constant, its default and what it does.

- **A folder can carry its own settings.** Drop a `.abchapterize-config` into it — the
  same one-option-per-line format as `--config` — and every book in that folder is
  processed with those options; a `.abchapterize-custom` beside it is read as `--custom`
  mappings. Settings layer from the outside in, so a shelf overrides the library and the
  command line overrides both, and only folders the run actually reached through are read.
  A per-folder file may change how a book is read — phrases, titles, language, mark
  placement, where and how hard the tool looks — but not what the run is: the models, the
  file selection, the output and mode options stay the command line's, and asking for one
  of those in a folder file is an error naming it rather than a setting silently ignored.

- **`--config <path>` reads options from a file**, one option per line, written exactly
  as you would type it. Everything after the option name is its argument, so a phrase or
  a `--custom` mapping needs no quoting; blank lines and `#` comments are ignored. What
  you type on the command line always beats the same option in a file, wherever `--config`
  stands, while options meant to be repeated — `--custom`, `--chapter-phrase` — accumulate.
  The option may be given more than once, and a config file may pull in another —
  including one that two of them share, which is read once rather than twice.

- **Books that announce every chapter after a jingle are now probed music-first, which
  saves a great deal of time.** Instead of walking a file's pauses and its music together
  in one sweep, such a book gets its jingles read first, in order, and its pauses
  afterwards — but only where they can still be carrying something: anywhere the chapter
  numbering still has a hole, everything before the first chapter found, and everything
  after the last. So a prologue and an epilogue are still looked for exactly where they
  belong, and the pauses that get skipped are the ones between two chapters whose numbers
  already run consecutively, where nothing else could be announced anyway.

  A file takes this shape by itself when it has at least one jingle per hour of play time,
  unless one of your own `--custom` mappings may be announced between two chapters — that
  being the one thing the shape would stop looking for. A mapping restricted to
  `before-first-chapter` or `after-last-chapter` costs nothing.

  `--jingle-first` asks for the shape on any file, including one that qualifies for
  neither reason; it cannot be combined with `--ignore-chapter-numbers`, which leaves no
  chapter sequence to scope the second half by. `--verbose` says which shape a file ran
  under, and the progress bar shows the two halves as phases of their own, `J-probing...`
  and `S-probing...`.

- **Books with no jingles now read their longest pauses first, which saves time on a long
  file.** Chapters are announced after a book's longer pauses, and the pauses it announces
  nothing after outnumber them by a hundred to one. Such a file is now skimmed once
  through in descending pause length to find out roughly where its chapters are, and the
  ordinary sweep behind it then passes over every pause lying between two chapters whose
  numbers already run consecutively — where nothing else could be announced anyway. A
  window read during the skim is not read a second time.

  The skim stops by itself once the pauses get too short to be this book's chapter breaks,
  and gives up early on a file that announces nothing at all, spending no more looking
  than `--early-abort` would have. Nothing about how chapters are found, numbered or
  placed changes, because the file is still read in order afterwards — so a book whose
  numbering restarts for a second part is still recognised as one.

  A file takes this shape by itself when it is not already being read music-first and none
  of your own `--custom` mappings may be announced between two chapters. It is skipped
  where `--min-silence-length` was given explicitly, that being you naming the pauses worth
  probing. `--verbose` says which shape a file ran under, and the skim is a phase of its
  own, `SD-probing...` — which is not a progress bar, having a position in the file but
  no notion of how far along it is. It shows a single `X` moving about the track as it reads,
  and counts the locations it has looked at where the percentage would be.

### Changed

- **Chapter marks worth a second listen now name their part.** On a book whose numbering
  restarts, `--summary`'s low-confidence list and the file's own result line read e.g.
  `part 1 chapter 4, 9; part 2 chapter 3` — "chapter 4" alone did not say which one to go
  and listen to. Books with a single sequence are unchanged, except that the result line now
  names at most ten chapters and counts the rest, as the summary listing already did.

- **The progress bar shows every part of a book that restarts its numbering.** Instead of
  one chapter number it now reads e.g. `ch 11,15,4(+1)` — one per part, in order — so a run
  on part 3's chapter 4 no longer looks as though it had gone backwards from part 1's
  chapter 11. Books with a single sequence are unchanged.

- **A processed file's result line now says how long it took.** Both the plain figure and
  its share of that book's own run length, so it means something without your having to
  remember how long the book is: `; took 38:20 (7.2% of run length)`. Files that were
  skipped, or that failed before anything was read, have no such figure.

- **The progress bar says when it is going back over ground it has already covered.**
  Probing's percentage runs backwards while a gap in the chapter numbering sends it to
  re-probe earlier candidates, which looked like the bar misbehaving. The phase now reads
  `Probing... (<<)` for exactly that stretch, and the stretch itself is marked out on the
  bar.

- **The progress display is now two lines.** The bar and its percentage take the first,
  as wide as the console; the phase, chapter state, elapsed timer and file name move to a
  line of their own underneath, where a long book title no longer has to compete with the
  bar for room.

- **Phase names read as something in progress** — `Analyzing...`, `Probing...`,
  `Scanning...`, `Finishing...` — and the last of those replaces the old `Muxing...` for
  the step that writes the chapter marks into the file, under a name that says what is
  happening rather than how. A file with no chapter music, where all probing has to read
  is pauses, names its walk `SC-probing...`, and a gap swept for pauses shorter than
  probing was willing to consider shows as `SF-probing...` — both were silently part of
  `Probing...` before.

- **The bar shows which piece of the book is being worked.** Wherever a pass is on one
  stretch rather than the whole file — a gap in the numbering, the stretches a music-first
  read left over, the file's tail — that stretch is picked out in dark cyan, so a fill that
  stops short or runs backwards can be read against the part it belongs to.

- **A finished file's result line carries its name in white**, so a name is as easy to pick
  out of a long run's backlog as it is on the bar. Under `--verbose` or `--no-bar`, where
  that line is a log line, it stays plain as before.

- **The processing passes have names instead of numbers.** They had grown into Pass 1, 2,
  2.5, 3 and 3.5, and the fractions were a fiction: "pass 2.5" was never a step between
  two others, it was the probing pass run again over a gap with a heavier model. Each pass
  is now named for what it does, and that name is what `--verbose` writes at the start of
  a log line — and what the progress bar shows, spelled as something in progress:

  | was | is now | what it does |
  | --- | --- | --- |
  | Pass 1 | `Analyze` | Measures the file — silences, speech, music. Recognizes nothing. |
  | Pass 2 | `Probe` | Transcribes a short window everywhere a chapter could start. |
  | Pass 2.5 | `Re-probe` | Probes a gap again on the heavier model. |
  | Pass 3 | `Scan` | Transcribes a whole stretch end to end. |
  | Pass 3.5 | `Re-scan` | Reads that stretch once more, framed differently. |

  The names run cheapest to dearest, and a `Re-` prefix means the same machinery again
  over audio that already came back empty. A book read music-first shows `J-probe` and
  `S-probe` in place of `Probe` for its two halves.

  If you grep your own logs for a phase, this is the change to know about; logs written by
  0.12.0 and earlier keep the old wording, since nothing rewrites them.

- **`--pass3-model` is now `--upgrade-model`** (`-M` is unchanged). The old spelling keeps
  working, silently and indefinitely, so no script needs touching. The rename fixes a name
  that was already wrong before the passes lost their numbers: that model is consulted by
  five different steps, only one of which was pass 3.

### Fixed

- **A multi-part book's “shortest silence” and “longest jingle” figures are measured over all
  its chapters.** On a book whose numbering restarts, the per-chapter measurements behind those
  two `--verbose` and `--summary` figures were filed under the chapter number alone, so every
  part's chapter 1 shared one entry and only the last of them survived — which could report a
  shortest or longest that was simply the last one measured. Books with a single sequence were
  never affected, and no mark ever moved either way: these figures are reported, not acted on.

- **A phrase that cannot finish no longer stops a run without saying so.** A `/regexp/`
  you write is matched against every window the run reads, and a pattern with repetition
  nested inside repetition — `(\w+\s?)*` and the like — can take effectively for ever on
  text it does not match. Such a wording is now given a second on each transcript and then
  abandoned, ending the run with a message naming the phrase, instead of leaving it to sit
  there making no progress. The same bound applies to a `--filter` regexp.

- **A chapter announced over music is no longer missed because the recognizer wrote
  "[Musik]" instead of hearing it.** Speech recognizers trained on subtitles sometimes
  label a stretch of music with a bracketed tag rather than transcribing it, and two
  checks took such a tag for spoken words: one skipped the second look at a window whose
  announcement had been drowned out, the other could decide an announcement was not where
  it in fact was. Both now treat a tag as the non-speech it describes.

- **A prologue or epilogue is now judged by where it sits, not by when it was heard.** A
  phrase restricted to "before the first chapter" or "after the first chapter" was measured
  against how many chapters had been found so far, which is the same thing only for a pass
  that reads a book strictly forward. A prologue heard by a window that had already picked
  up the chapter behind it was refused.

- **A multi-part book whose music starts partway through no longer loses the chapters in front
  of it.** When a file is read music-first and the earliest chapter the music yields is the one
  the book was expected to open on, everything before it is now searched without an upper limit
  on the chapter numbers it may hold. Previously that stretch was restricted to numbers below
  the first chapter found — of which, for a chapter 1, there are none — so a previous part's
  closing chapters sitting in front of it could not be picked up at all.

- **A mark that would have landed in the words before an announcement is now moved onto the
  announcement instead of being given up on.** Where only a short pause separates a chapter
  announcement from whatever is read just before it — a reader's credit, most often — the
  mark can end up inside that, and the safeguard against it used to fall back on the
  position the file had been marked at before the announcement was pinned down. That is
  only as good as that earlier position happened to be, and on a book where it sat *after*
  the announcement the chapter was marked several seconds late, past the very words you
  jump to a chapter to hear. The mark now moves forward to where the announcement itself
  begins.

## [0.12.0] — 2026-08-18

### Added

- **`--run-before` and `--run-after` run a command of your own around each file.** Both take
  the command line you would have typed — a shell runs it, so built-ins, pipes, redirection
  and `~` all work — and both understand placeholders for the parts of a file's path:

  ```
  abchapterize --recurse --backup \
               --run-before "abnormalize $99" \
               --run-after "mv $99.bak ~/archive/$1" \
               ~/audiobooks
  ```

  `$1` is the file name, `$0` the same without its extension, `$2` adds a parent folder,
  `$99` gives the whole path, and `$-1`, `$-2`, … name the folders above it. Names with
  spaces, ampersands and brackets are quoted for the shell for you, including whatever you
  appended to a placeholder: `--run-after "move $1.bak $0.bak"` really does move
  `"buch 1.m4b.bak"` to `"buch 1.bak"`.

  Neither command runs for a file the run skips — one that already carries marks, say — and
  `--run-after` also stays out of the way of a file left tagged `.missing-marks-...`, which
  a later run is expected to pick up again. A `--run-before` that fails skips its file with
  a warning rather than marking a book whose preparation did not happen. Under `--dry-run`
  the command line is printed instead of run, which is the quickest way to check that your
  placeholders come out the way you meant. See the manual for the whole syntax.

- **Books that count their chapters from one again in every part are now marked in full.**
  Until now such a file yielded chapters up to the end of its first part and then stopped,
  every later announcement being heard, understood, and dropped for not continuing the
  numbering — which looks exactly like a detection failure. The restart is now recognized
  once three consecutive chapters of the new part have been heard, and everything from
  there is marked under the new count. Nothing is assumed on weaker evidence: a single
  announcement below the sequence is still an in-text mention ("as I said in chapter
  three") and is still passed over.

  Chapters of such a file are titled with their part — "Part 2 - Chapter 1" — and every
  part is labelled, including the first. A book with a single chapter sequence, which is
  virtually all of them, is written exactly as before. The word is localized like the
  others and can be set with the new **`--part-title <word>`**. The file's summary line
  reports the parts it found and the range each one covers.

- **`--custom` mappings can say what kind of thing they name.** The `[...]` tag that
  restricted a mapping to one language now takes a comma-separated list, and hints may
  sit in it beside the language code:

  ```
  --custom "[de,before-first-chapter,once]/vorwort/:Vorwort"
  --custom "[after-last-chapter,once]/^nachwort/:Nachwort"
  --custom "[max=3]/zwischenspiel/:Zwischenspiel"
  ```

  `before-first-chapter`, `after-first-chapter` and `after-last-chapter` restrict where
  in the book a match counts (short forms `before-first`, `after-first`, `after-last`);
  `once` keeps a single mark, the last match winning; `max=<n>` caps how many marks one
  mapping may produce. To require a real pause in front of a match — the check that tells
  a heading read aloud from the same word buried in a sentence — write `^` at the start of
  the phrase, as in `--custom "[after-last-chapter,once]/^nachwort/:Nachwort"`. None of it
  is new behaviour — the built-in prologue and epilogue have always been exactly this —
  and a mapping without hints does what it always did.

  A bracket run only counts as a tag when something in it is recognized, so
  `--custom "[Musik]:Zwischenmusik"` still matches those words rather than being read as
  a tag. A hint on `--chapter-phrase` or another localized option, where it would mean
  nothing, is an error rather than silently ignored.

- **`--named-mark-distance <seconds>` keeps a named mark from crowding a chapter.** A
  prologue, epilogue or `--custom` mark landing within 10 seconds of a chapter mark (the
  new default) is no longer written as an entry of its own: the chapter keeps its
  position, and the named mark contributes its title in brackets — "Chapter 10
  (Interlude)". Two entries a few seconds apart are worse than one, since scrubbing to a
  chapter then lands you either in the tail of the previous section or a little way into
  the new one depending on which you hit. Several crowding marks are appended in file
  order; `--named-mark-distance 0` writes everything separately, as before.

- **Phrases are now a list of alternatives, and a phrase can say what it is looking for.**
  `--chapter-phrase`, `--prologue-phrase`, `--epilogue-phrase` and `--custom` all take
  several alternatives separated by `;`, every one of which is searched:

  ```
  --chapter-phrase "/se[ck]tion ()/;partie;default"
  ```

  Inside a regexp, **`()` stands for a chapter number in whatever notation the file's
  language has** — digits, digit ordinals, Roman numerals, spoken cardinals and ordinals —
  and captures it, so `/chapter ()/` covers "Chapter 12", "Chapter XII." and "Chapter one
  hundred and five" alike. A leading **`^`** asks for a real pause in front of the
  announcement — either a real pause or the recognizer writing it as a transcript segment of
  its own — and a trailing **`$`** for one behind it; neither is an anchor, and each
  belongs to the alternative that carries it. **`default`** pulls this tool's own phrase for
  the language into the list, so a value can add to it rather than replace it, and
  **`none`** — the bare-number wording — is now one alternative among others rather than a
  mode the whole run is in. Repeating one of these options adds alternatives.

  A title may write out what its phrase captured: `${name}` for a named group, `${number}`
  for the chapter number in digits whatever notation was spoken, and `$roman{}`,
  `$digits{}`, `$upper{}`, `$lower{}` and `$capital{}` to convert one.

  The whole syntax, with examples, is in
  [the manual](doc/manual.md#the-phrase-syntax). The built-in phrases are written in it too
  — English is now `/(?:^chapter ()|^() chapter|^chapter)/`, which says out loud what it
  always meant: a chapter word in the middle of a sentence is not an announcement, and a
  chapter's number may be spoken before the word as readily as after it. They were checked
  against a sixteen-book reference corpus first, and every announcement of all of them is
  found at the same place, with the same number, as before.

  Where two wordings of one phrase read the same words differently, the chapter sequence
  decides between them instead of the leftmost match simply winning: a number that cannot
  follow the chapters already found is put aside and the next reading of those words is
  tried. Announcements the recognizer prefixes with an invented word — which it does, in a
  short window — keep their real number this way. `none` takes part in that as any other
  alternative does: where it and a phrase read the same announcement, the phrase is tried
  first and the number spoken alone, which asks for a pause on both sides, is what the run
  falls back on. It is also always considered last, wherever in the value it was written.

  A plain word given as a chapter phrase now becomes the same pair of wordings a built-in
  default has — the number behind the word and the number in front of it, both asking for a
  pause before the announcement — so `--chapter-phrase sektion` finds "Sektion 5" and
  "Fünfte Sektion" alike and a title's `${number}` works under either. It no longer falls
  back on reading a number off whatever else is nearby; a phrase that needs that is written
  as a `/regexp/` without a `()`.

- **A garbled announcement on a dull-sounding recording gets a second chance.** On some
  recordings the recognizer writes a chapter's *number* but loses the word beside it —
  "1. The Long Road" where the narrator said "Chapter one, The Long Road" — and the chapter
  is then missed with nothing in the output to show a heading was heard at all. Where that
  happens, the window is now read once more through a built-in speech denoiser, which on
  the book this was measured against turned a coin flip into every attempt succeeding.

  It costs one extra pass over the few windows that fail this way, never moves a mark that
  was already found, and does not run at all on a book whose audio is clear enough not to
  need it — most files never reach it. **`--no-denoise`** switches it off.

### Changed

- **`--max-chapter-number` now defaults to 200** instead of to no limit at all, counted from
  `--expected-start-chapter`. A chapter numbered above the cap is discarded as a mishearing, so
  a stray number can no longer set the sequence's ceiling and turn every real chapter behind it
  into one that "does not continue the sequence". A book that genuinely runs longer needs the
  option set explicitly — chapters above the cap are dropped without a word.

  What that is worth: a timetable of years read out in a book's front matter used to become a
  run of chapters numbered by year, which pushed the real chapter 1 below the sequence, made
  the book look like it held several parts, and could cost a prologue whose place in the book
  had by then been given away.

- **An alternation written inside a phrase alternative is now multiplied out.**
  `/kapit(?:el|let) ()/` becomes two alternatives rather than one, and `/(?:a|b)c(?:d|e)/`
  becomes four. Every alternative is now one expression with no choice left in it, which is
  what lets a mark's re-transcription be held to the alternative that found it rather than to
  the whole phrase. A phrase that would expand into more than 64 alternatives is refused with
  an error naming the problem.

- **A mark is now confirmed by the alternative that found it**, not by any alternative of the
  phrase. This only shows where one value names several — most sharply where a phrase is
  combined with `none`, since a number spoken alone would otherwise vouch for an announcement
  found by a phrase, and the mark then drifted to the number and landed inside the
  announcement rather than in front of it.

- **Swedish: two ordinals are now recognized in the spelling the recognizer prefers.**
  "Älfte" for "elfte" (11) and "tolvte" for "tolfte" (12) — both were previously reported as
  an announcement with no readable number, which on a book announcing "Tolvte kapitlet" costs
  the chapter.

- **Three things about phrase and title options changed meaning.** All of them are visible
  the moment they matter, and each has a one-line fix:

  - A `;` now always separates alternatives in a **phrase** option (`--chapter-phrase`,
    `--prologue-phrase`, `--epilogue-phrase`) and in `--custom`. It used to do so only once
    some entry carried a `[xx]` tag, so a value with no tag anywhere was taken whole,
    semicolons included. Write `\;` for a semicolon that belongs to a regexp. The title
    options are unchanged: they hold one value per language, and an untagged value is still
    taken whole — a title containing a semicolon is a title, not two titles.
  - An untagged alternative now applies to **every** language rather than only to the ones
    the value does not name: `"[fr]chapitre;kapitel"` has French listening for both, where it
    used to listen for "chapitre" alone. Tag it as well to keep the old reading.
  - A title referencing a capturing group **by number** (`$1`, `$2`) is refused with an error
    naming the fix. Name the group — `(?<part>...)` in the phrase, `${part}` in the title.
    Silently turning `$1` into literal text was the one outcome nobody would have noticed
    until the file was written. `$$` still writes a dollar sign, and a dollar before an
    ordinary word still needs no escape.

- **Everything the tool prints now calls a chapter entry a "mark".** Some messages used to
  say "marking" for one a file arrived with and "mark" for one this run placed — a
  distinction that is real inside the code and of no use to anyone reading the output. A
  script matching on `chapter marking(s)` needs updating; the counts and their order are
  unchanged.

- **A file's result line groups its marks.** It now opens with what the file arrived carrying
  and lost — `3 existing mark(s) dropped` — and then states the total written, with the
  split in brackets: `24 mark(s) written (23 chapter(s) 1-23, 1 named)`. The named count
  covers everything that is not a numbered chapter: intro, prologue, epilogue and `--custom`
  marks alike, which used to be spread over a `+ intro` suffix and a separate note further
  along the line.

- **The cheap gap re-probe stops as soon as the gap is closed.** It used to walk the rest of
  the gap after finding the last chapter that was missing from it, re-detecting that same
  chapter from every window that overlapped it and discarding the duplicates. On a gap that
  closes early this saves a chapter's worth of re-transcription; a gap that never closes is
  unaffected.

- **A gap in the chapter numbers now reaches under the pause length the run demanded.** With
  `--min-silence-length auto`, a book whose chapter breaks measure shorter than the 1.5 seconds
  probing opens at can now act on that straight away: the stretch between the two chapters
  around a missing one is re-read with the shorter pauses included, so the chapter is often
  recovered on the spot and on the faster model instead of waiting for the later passes. An
  explicit `--min-silence-length` is unaffected and still probes nothing below itself.

- **The sweep for short pauses now runs off a missing chapter rather than off a measurement,
  and spends its budget where the chapter is likeliest to be.** It used to need some chapter to
  have measured a short break first, which a book whose chapters all open with music never does
  — leaving the sweep switched off on exactly the kind of book it was written for. A gap in the
  numbering is now reason enough. It also sweeps a tenth of a second at a time, longest pauses
  first, the way the later pass already did, instead of taking the whole range in one go: on a
  gap dense with short pauses the wide version cost more than transcribing the gap outright and
  so was abandoned without probing anything at all, even though the missing chapter was sitting
  in the very first slice it would have looked at.

- **A missing chapter is now hunted by re-reading its stretch, not by widening the search.**
  When the chapter numbers leave a hole, everything between the two chapters around it goes
  back on the list — every pause and every jingle, including the ones the first look ruled
  out — and each is read again in a slightly different framing. It used to be read again in
  one deliberately wide window instead, which is the one thing a recognizer handles worst:
  a window long enough to span a book's longest jingle is exactly the width that loses a
  one-word announcement. The same goes for pass 2.5 and the gap sweeps.

- **The epilogue mark now has to follow the book's last chapter.** "Epilogue" is an
  ordinary word, and a match sitting between two chapters was never the book's epilogue —
  it was the word turning up in prose, or an omnibus part ending halfway through the file.
  Such a mark is dropped, and `--verbose` says which one and why. If you do want a mark
  there, `--custom` is bound by no position and can claim exactly the same announcements
  (`--custom "/epilog/:Interlude"`); a mapping of yours that matches the same words even
  inherits the dropped mark, so it stays where it was under your own title.

- **The prologue and the epilogue now keep the announcement later in the book**, rather
  than the one heard latest in the run. Both are written at most once per file, and the
  later announcement wins because front matter tends to name what is coming before the
  narrator announces it — but the recovery passes work backwards through a book's gaps
  after the main scan, so a stray match early in the file could displace the real mark
  found near the end.

- **A detected prologue now implies that the book starts at chapter 1.** A first chapter
  numbered above 1 is normally trusted outright, because nothing tells a legitimate
  split-book part from a chapter the scan simply missed — but a book's prologue is in the
  file that holds its beginning, so once one is found the chapters under the first one
  detected really are missing, and they are searched for and reported as such.
  `--expected-start-chapter` still wins where it is given, which is how a split part that
  carries its own prologue says so.

### Removed

- **`--max-jingle-length` / `-X` is gone**, and the voice-activity pre-pass it could switch
  off now runs on every file. Its last job was to say how far back a book's music can reach,
  which the tool measures from the file's own jingles instead — tighter than the 45-second
  assumption on nearly every book, and no longer something to get wrong from the command
  line. A script still passing it gets an error saying so rather than a silent
  "unknown option". `--min-silence-length 0` (probe only at jingles) no longer conflicts with
  anything and can be given on its own.

  One consequence worth knowing: the pre-pass is now required, so a failure to load the
  bundled voice-activity model ends the run instead of quietly continuing without it. It also
  means a mark can land a shade differently where an announcement is preceded by an unusually
  long stretch of music.

### Fixed

- **Finishing a file never overwrites another one to get its name.** When a run renames a
  file it has just written — taking a `.missing-marks-...` tag off, or putting one on — and
  something is already sitting under that name, the rename is now refused and the file keeps
  the name it has. Its marks are written either way, and the summary line says which name it
  ended up with. Previously the file in the way was replaced without a word, which could cost
  you a copy you had put back beside a tagged book.

- **`--cleanup` given a whole drive now finds its leftovers.** Naming a drive root as the
  target (`--cleanup -R X:\`) matched nothing at all and reported "nothing to clean up",
  however much there was to do. Ordinary folders were never affected.

- **A chapter announced between two pauses is no longer missed when the first pause is a
  short one.** Where a heading sits between a brief pause and a longer one, the only window
  covering that stretch used to open on the longer pause — that is, just after the heading
  had been spoken — so nothing ever read it. Such a book lost the chapter outright when no
  later chapter happened to bracket it. The short pause is now probed in its own right when
  a longer one follows within a few seconds, which is what a spoken heading sounds like.
  Marks that were already found do not move.

- **Recovering a chapter no longer makes the rest of the file crawl.** When a chapter was found by
  the deeper search that runs after a gap in the numbering, the very short pause it turned up behind
  was taken as a statement about the whole book, and everything after it was searched at that
  setting — on one long audiobook this doubled the running time while finding exactly the same
  chapters, all of them announced after music the search was already looking at. Such a pause is now
  remembered for the recovery passes, which are the ones that can act on it, without opening up the
  main scan. A book whose chapters really do follow short pauses still adapts as before, because its
  own main scan measures them.

- **A mark no longer lands in the middle of whatever was said just before the chapter.**
  Where only a short pause separates a chapter announcement from speech in front of it — a
  recording that names its reader before each chapter is the case that turned this up — the
  step that pins a mark to the exact start of the announcement could walk straight past that
  pause and settle inside the earlier words, putting the mark a second or more too early. A
  mark that would land inside speech is now refused and keeps the position it had before that
  step, which in every observed case was already the right one. Marks that sit in a pause or
  in a chapter's music, which is all of them on a well-behaved book, are untouched.

- **An announcement spoken over a chapter's jingle is looked for properly.** Where the
  music might be hiding the announcement rather than preceding it, the tool used to widen
  one probe window to cover the whole jingle — and a window that long is exactly what makes
  the recognizer drop a lone word. The music is now read afterwards, and only when the
  window behind it came up empty, in overlapping pieces short enough to be heard reliably.
  Nothing else moves: on every mark checked the position is unchanged to the millisecond.

- **A `--run-before` / `--run-after` folder placeholder no longer breaks the command on
  Windows.** `$-1` and its relatives always end in a path separator, and Windows reads a
  backslash in front of a quote as escaping that quote — so a folder whose name contains a
  space handed the started program one mangled argument with everything after it swallowed.
  The separator is now escaped where it needs to be.

- **ffmpeg is found behind a quoted `PATH` entry.** An entry wrapped in double quotes, which
  Windows allows and some installers write, was skipped silently — and the run then failed
  saying it had searched `PATH`.

- **`--verbose` names the right option when a chapter number is discarded as too high.** The
  line credited the cap to `--chapter-count` even on a run that gave neither cap option, and
  so pointed at the wrong switch to change.

- **`--mark-before-jingle` re-checks its walk in two more places.** A non-default
  `--mark-lead` no longer skews the decision on whether the check is worth making, and a mark
  the refinement placed and the new speech guard then took back is now treated as unconfirmed,
  so the walk starting from it is verified rather than trusted.

- **A model download no longer scribbles over the progress bar** on a platform without
  SHA3-256, where the note about it went straight to the console.

## [0.11.0] — 2026-08-10

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

- **`--summary` now lists the low-confidence marks.** A mark whose chapter number was read at a
  Whisper probability below 0.50 has always been flagged on the file's own result line, which in a
  batch of two hundred books has scrolled away long before the run ends. The closing block now
  carries a fourth listing naming those files and chapters, alongside the skipped, empty-handed and
  still-incomplete ones, so "which of these should I check by hand" is answered without reading a
  log back. Files are named as they are once the run is over, so what is printed is what is in the
  folder. Where any of them was read with `--chapter-phrase none`, one line is added warning that
  the two kinds of confidence are not comparable: a number spoken alone is often a transcript
  segment of a single token, whose probability fluctuates far more than a whole phrase's, so a low
  value there says much less about the mark.

- **`--verbose` now counts the book's jingles after pass 1.** The non-speech regions the
  voice-activity pre-pass reports mix music in with ordinary pauses, so their number never
  said whether a book has chapter music at all. A second line now tallies the jingles among
  them — a stretch of at least two seconds that is neither speech nor silence — and gives
  their shortest, longest and average length. A brief vocal blip in the music does not split
  one jingle in two, and its length counts toward the whole, using the same reading of "that
  was the music, not a speaker" that `--mark-before-jingle` walks by. It costs nothing (both
  signals are already in hand) and arrives in the first seconds of a run, which is roughly
  when you would want to know whether `--mark-before-jingle` is worth adding and what
  `--max-jingle-length` has to cover. `--debug` lists each jingle with its position.

### Changed

- **`--title` is now `--chapter-title`.** Every other option naming a part of a book says which
  part - `--chapter-phrase`, `--intro-title`, `--prologue-title` - while a bare `--title` read
  like the book's own title rather than the word put in front of each chapter number. The short
  form is still `-t`, and `--title` still works exactly as before; it is simply no longer
  documented, so nothing that already uses it needs changing.

- **The option groups in `--help` and the documentation have been rearranged.** The phrase
  options and the title options now sit together in one "Phrases & titles" group, since choosing
  what to listen for and choosing what to call the result is one decision made at one time;
  `--ignore-chapter-numbers` has moved to "Detection safety nets", alongside the other options
  that bound what the run is willing to believe about chapter numbers; and `--cpu-only` and
  `--use-gpu` have moved to "Performance", which is where someone looking to make a run faster
  will look for them. No option changed its meaning, and nothing was added or removed.

- **Pass 2 now frames each probe around what it expects to hear there, instead of handing
  every candidate the same window.** A pause and a jingle are different promises: after a
  pause the announcement follows within seconds, behind a jingle it is the first speech
  after the music, and a jingle with a sound buried in it may be hiding the announcement
  inside the music. Each probe now opens shortly before what it is actually looking for and
  runs only as far as an announcement plausibly reaches — and a pause that merely leads into
  a jingle is no longer probed on its own, since the jingle behind it listens for the same
  announcement from a better place. Across the test corpus this roughly halves the
  recognizer work pass 2 does, and it looks in the places the one-size window was stretched
  to cover.

  The knock-on effect is on `--max-jingle-length`: it no longer sizes pass 2's windows at
  all, so a book with long jingles costs one probe per jingle rather than a book-wide window
  wide enough for the longest of them. It still bounds the recovery passes, still decides
  whether the voice-activity pre-pass runs, and `0` still means "no jingles here, look only
  at the pauses".

- **The progress bar keeps moving during pass 3.** Pass 3 transcribes a gap in chunks of
  several minutes, and the bar only ever moved when a whole chunk was done — so on a long gap
  it could sit at the same percentage for the better part of an hour, which looks exactly like
  a run that has hung. It now follows the recognizer's own position through the chunk it is
  working on. Nothing is slower for it: the recognizer already reports as it goes, and the
  figure was simply being thrown away.

- **`--verbose` names the class on each mark line** — `at a silence`, `at a jingle`, or `embedded
  in a jingle` — next to the confidence and the loudness already there. Since the class is what
  decided where that probe's window opened and how far it ran, it is the piece that makes a mark
  in an unexpected place interpretable rather than merely visible.

- **`--min-silence-length auto` now learns only from chapters found at a pause.** A chapter
  found at a jingle used to teach it from the hush in front of the music, which measures the
  run-up to a jingle rather than the break between two chapters and is routinely a different
  length — so on a book that plays jingles the threshold could drift away from the breaks it
  still had to find, in either direction. Books without jingles are unaffected.

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

- **`--min-silence-length auto` now notices when a book's chapter breaks are shorter than it
  assumed, and goes looking.** The adaptive threshold could tighten as much as a book
  warranted but never loosen past the 1.5 seconds it started at, so a narrator whose chapter
  breaks sit just under that line — a common enough shape, and one where every chapter is
  affected — had those chapters left to the later, slower gap-filling passes, with the
  occasional one lost outright. Now the measurement is allowed to settle below the starting
  point, as low as 0.8 seconds, and pass 2 finishes by sweeping the gaps in the numbering for
  the pauses in between. Chapters that used to cost a full pass 3, or go missing, are
  typically recovered for a handful of probes instead. A book with ordinary breaks is
  unaffected, and so is which pauses pass 2 probes in the first place — the sweep is an extra
  step, not a wider net. An explicit `--min-silence-length` is untouched: the value you give
  is still the shortest pause that will ever be looked at.

- **`--chapter-count` now switches the blind trailing scan off.** Telling the run how many
  chapters a book has is a statement about what is in the tail, so it replaces the speculative
  sweep instead of running alongside it: the numbers still owed are hunted directly, the search
  stops the moment they turn up, and nothing at all is transcribed once the count is reached.

### Removed

- **`--trailing-scan` / `-L`**, replaced by `--no-trailing-scan` now that the scan is the
  default. Either spelling stops the run with a message pointing at the new option rather than
  quietly doing the opposite of what was meant.

### Fixed

- **Chapter marks land less late on a book that plays music into every chapter.** A mark is
  normally placed against the end of the pause in front of the announcement, which is found by
  measuring the audio itself. Music is not a pause, so where a jingle led into the chapter there
  was nothing to measure against and the mark simply kept the position the announcement was first
  recognised at - always a little after the words actually begin, and liable to move by a tenth of
  a second from one run to the next for no reason a listener could hear. Such a mark now anchors
  to the point where the music gives way to speech, by up to a tenth of a second and no further,
  so a misjudged reading of that point can only cost a fraction of the mark lead instead of
  dropping the mark into the middle of the music. Books whose chapters open with a plain pause are
  unaffected, and so is any mark already sitting at or before that point.

- **A chapter is no longer lost to a neighbouring window's transcript being reused.** Probe
  windows overlap, and rather than pay to transcribe the same seconds twice, a window reuses
  what the window before it already heard. The recognizer, though, sometimes hands back a long
  stretch of audio as one unbroken sentence — and a sentence that began before the current
  window cannot be placed within it, so it is discarded, leaving a hole precisely where that
  window's own candidate expected its announcement, with no fresh audio to fill it. A window
  in that position is now read on its own instead of reusing anything. It costs one extra
  transcription on the rare window it applies to; every other window reuses exactly as before.

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

- **Downloading a `--pass3-model` no longer scrambles the progress display.** Unlike the
  main model, a separate pass-3 model is fetched only when a book actually needs it, which
  is well into a run with the progress bar already on screen — and the download's own
  percentage line and the bar then fought over the same line of the terminal, leaving both
  unreadable for as long as the transfer took. The download now reports itself on lines of
  its own, and lands in the log file alongside everything else.


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
