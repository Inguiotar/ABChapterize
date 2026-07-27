# Changelog

All notable, user-visible changes to ABChapterize are recorded here — what changed
for you, not how it was built. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and version numbers follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.9.1] — unreleased

### Added

- **Several files and folders in one command.** The trailing argument is now a
  list: `abchapterize -r "D:\Audiobooks" "E:\More" "one-off.m4b"` works, mixing
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
