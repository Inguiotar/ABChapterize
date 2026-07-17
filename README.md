# chapterize

Stand-alone Windows CLI tool (C# / .NET 10) that finds the start of chapters in
`.m4a`/`.m4b` audiobooks with a Whisper speech-recognition model and writes the
chapter markings into the file(s).

## How it works

1. **Silence scan** - ffmpeg's `silencedetect` finds pauses longer than typical
   sentence/paragraph gaps (>= 1.5 s below -35 dBFS).
2. **Probing** - the audio following each silence (and the file start) is
   transcribed with Whisper and matched against the chapter phrase
   (default: `chapter`, always case-insensitive). The chapter number is parsed
   from digits or spoken number words (English and German).
3. **Gap filling** - if the detected chapter numbers contain sequence gaps, the
   audio between the mismatched markings is fully transcribed. If a gap remains,
   the file is left unchanged and a warning is printed (not a fatal error).
4. **Safe writing** - chapters are written by stream-copy remuxing into a
   temporary file, which is verified before it atomically replaces the original.
   The original data is never deleted before the new file is verified, so
   audiobooks cannot be lost even without `--backup`.

## Usage

```
chapterize [options] <file-or-directory>
chapterize --revert [--recurse] <file-or-directory>
```

| Option | Description |
| --- | --- |
| `-r`, `--recurse` | Recurse into subdirectories (directories only). |
| `-b`, `--backup` | Keep the original file as `*.bak`. |
| `--revert` | Restore `*.m4a.bak` / `*.m4b.bak` backups. Only combinable with `--recurse`. |
| `-l`, `--lang <code>` | Two-letter language hint for Whisper (default `en`). |
| `-c`, `--chapter-phrase <p>` | Phrase that identifies chapter starts (default `chapter`). `/regexp/` syntax supported, optionally with a `(\d+)` group for the number. |
| `-m`, `--model <name>` | `tiny`, `base`, `small`, `medium`, `turbo` (default) or `large`. |
| `-f`, `--force` | Discard pre-existing chapter markings (otherwise such files are skipped). |
| `-x`, `--max-chapters <n>` | Treat more than `<n>` pre-existing markings as bogus and discard them. |
| `-j`, `--jingle` | A short jingle may precede the phrase; marks are placed 0.5 s before it. |
| `-t`, `--title <word>` | Chapter title word; the number is appended (default `Chapter`). |
| `-i`, `--intro-title <word>` | Title of the mark covering the audio before the first detected chapter (default: the `--title` word followed by `0`, e.g. `Chapter 0`). |
| `-?`, `--help` | Usage info. |

Short options without parameters can be collapsed (`-rb` = `-r -b`).

## Requirements

* **ffmpeg/ffprobe** - searched in `PATH`, `.\ffmpeg\bin`, `%USERPROFILE%\ffmpeg\bin`,
  Program Files and finally `%FFMPEG_DIR%\bin` (`FFMPEG_DIR` = ffmpeg base directory).
* **Whisper models** - GGML files in the `models` folder next to `chapterize.exe`
  (all six sizes are included in the published output; source:
  https://huggingface.co/ggerganov/whisper.cpp).
* **Hardware acceleration** - the best available backend is picked automatically:
  CUDA, then Vulkan (any modern GPU), then CPU.

## Building / publishing

```
dotnet publish -c Release -o bin\publish
```

The Whisper models are expected in `bin\publish\models` (they are intentionally
not committed to git; download them from the URL above).

Set the environment variable `CHAPTERIZE_DEBUG=1` to dump all Whisper
transcripts to stderr for diagnosis.
