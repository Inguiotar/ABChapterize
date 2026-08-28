# Building ABChapterize on Linux ARM64

**Read this first:** there is no ARM64 release, and there is no ARM64 support.
What exists is a source tree that should compile and run on 64-bit ARM Linux,
written by someone who has never run it there. No CI builds it, no board here
has ever executed it, and not one chapter mark has ever been placed by this
tool on ARM. Everything below is "this is what it is supposed to do"; if it
turns out to be wrong, that is a bug and not a surprise.

The good news, compared with the [macOS page](building-on-macos.md): nothing
here can stop you before you start. There is no code signing and no
quarantine attribute, so a binary cross-published from an x64 machine runs as
happily as one built on the board. What you are risking is time, not a puzzle.

If you would rather not be the person finding out, use the Windows or Linux
x64 build from the [Releases](../../../releases) page instead.

## What you need

- **A 64-bit ARM machine** — a Raspberry Pi 4 or 5, an ARM VPS, a Jetson, an
  Apple Silicon Mac running Linux. It must be `aarch64`; 32-bit `armhf` is out
  of scope, see [Why not 32-bit ARM](#why-not-32-bit-arm).
- **A glibc distribution.** Debian, Ubuntu, Fedora, Arch — anything mainstream.
  **Alpine and other musl distributions do not work**, and they fail in a way
  that looks like success until you run the thing. See
  [Why not musl](#why-not-musl).
- **The [.NET 10 SDK](https://dotnet.microsoft.com/download)**, arm64 build —
  on the board if you are building there, or any host SDK if you are
  cross-publishing.
- **ffmpeg and ffprobe.** `sudo apt install ffmpeg` or your distribution's
  equivalent. ABChapterize looks in `$FFMPEG_DIR/bin` and `$FFMPEG_DIR` itself,
  then `PATH`, `./ffmpeg`, `~/ffmpeg`, `/usr/bin`, `/usr/local/bin`,
  `/opt/ffmpeg/bin`, `/opt/ffmpeg`, `/snap/bin`, `~/bin` and `~/.local/bin`.
- **Enough RAM to hold a model.** The models are downloaded on first use, not
  shipped, and they are loaded whole: `small` is 0.45 GB, `turbo` 1.51 GB,
  `medium` 1.43 GB, `large-v3` 2.88 GB. The default pair is `small` for probing
  and `turbo` for re-reads, which wants comfortably more than 2 GB of RAM to
  sit in. On a 2 GB board, pass `--model tiny --upgrade-model small` or
  similar; on 8 GB the defaults are fine.
- **About 7 GB of free disk** wherever the models land, if you end up pulling
  several.

## Building it

```sh
git clone https://github.com/Inguiotar/ABChapterize.git
cd ABChapterize
dotnet publish -c Release -r linux-arm64
```

The result lands in `bin/publish/linux-arm64/`. Run it from there:

```sh
./bin/publish/linux-arm64/abchapterize --version
```

If you cross-published and copied the folder over, restore the executable bit,
which most transfer methods drop:

```sh
chmod +x ./bin/publish/linux-arm64/abchapterize
```

Run the tests too, because they are the only part of this that anyone can
actually check:

```sh
dotnet test tests/ABChapterize.Tests
```

`SileroVadDetectorTests` is the interesting one: it loads the real ONNX Runtime
and runs the actual VAD model. If that passes, the native library resolution
described below is working on your machine, which is the single most likely
thing to be wrong. It is also the slowest test on a small board — it runs the
model over a synthetic signal and takes seconds on a desktop.

## What the publish folder should look like

ARM64 Linux is the tidiest of the four targets: both native bundles spell the
platform the same way, so there is exactly one runtimes folder and nothing
loose in the root.

```
bin/publish/linux-arm64/
  abchapterize
  runtimes/linux-arm64/
    libwhisper.so
    libggml-whisper.so
    libggml-base-whisper.so
    libggml-cpu-whisper.so
    libonnxruntime.so
    libonnxruntime_providers_shared.so
```

Six native libraries, one folder. If `runtimes/` is missing, holds a
`win-x64` or `linux-x64` folder instead, or holds **only** the two
`libonnxruntime*` files, the prune step in `ABChapterize.csproj` went wrong —
that is the one place in this whole port that cannot fail gracefully, so it is
worth a look before anything else. There should be no `.dll` and no
`ggml-metal.metal` anywhere; both are pruned as belonging to other platforms.

## What works, what is a coin flip

| | |
| --- | --- |
| Detection, ffmpeg handling, all languages | Pure C# and an external process. No reason for these to behave differently than on x64 Linux. |
| The VAD pre-pass | Needs ONNX Runtime to load out of `runtimes/linux-arm64/`. Covered by the unit tests — run them. |
| Whisper on the CPU | Should work, and will be slow. See [Expect it to be slow](#expect-it-to-be-slow). |
| Whisper on a GPU | Not available, whatever your board has. See below. |
| `--vad-threads auto` / `--whisper-threads auto` | Counts physical cores from the kernel's own topology tree, which is architecture-neutral and should be right here. See [Thread counts](#thread-counts). |

### GPUs

`--use-gpu` and `--list-gpus` will not get you GPU transcription on ARM64,
and the reason is worth stating precisely, because it is not the same reason as
on macOS.

Whisper.net publishes its Vulkan and CUDA runtimes for `win-x64` and
`linux-x64` **only**. No ARM64 build of either exists in the package, so
neither is in your publish folder and neither can load. Whisper.net probes
CUDA, then Vulkan, then CPU, and on ARM64 it lands on CPU every time. The
startup banner will say `Cpu backend`, and that will be the truth.

What makes this different from macOS is that your board may well *have* a
working Vulkan loader — Mesa on a Pi, or NVIDIA's stack on a Jetson. So
`--list-gpus` can genuinely list devices, and `--use-gpu` can genuinely match
one, and it will still change nothing: this tool uses Vulkan only to enumerate
and select a device, and the transcription itself is Whisper.net's to run.
Selecting a GPU that no native can address is a no-op, not an error.

A Jetson is the sharpest form of this. It has CUDA, it has a capable GPU, and
none of that is reachable from here, because the package that would have to
carry the ARM64 CUDA native does not.

### Thread counts

`--vad-threads auto` and `--whisper-threads auto` count *physical* cores rather
than hardware threads, so hyperthreads are not handed work as if they were
cores. On Linux that count comes from distinct (package, core) pairs under
`/sys/devices/system/cpu/cpu*/topology/`, which is the kernel's own view and is
not architecture-specific — this is the one platform-dependent number in the
tool that has a decent chance of being right on ARM without anyone having
checked.

The fallback behind it is the part that does not travel: if the topology tree
is missing, the count is rebuilt from `physical id` and `core id` in
`/proc/cpuinfo`, and those two keys are an x86 convention that ARM boards
commonly do not print at all. That is not a bug to report — the code treats a
zero result as acceptable and falls back again to the plain logical processor
count. On a board with no SMT, which is most of them, logical and physical are
the same number anyway.

If the thread counts look wrong, pass them explicitly rather than fighting it.

### Why not musl

Alpine and other musl distributions fail in the worst possible way: the build
succeeds. `dotnet publish -c Release -r linux-musl-arm64` completes without an
error and produces a publish folder whose `runtimes/linux-musl-arm64/` holds
`libonnxruntime.so` and `libonnxruntime_providers_shared.so` — and **nothing
else**. Microsoft.ML.OnnxRuntime ships a musl native; Whisper.net.Runtime does
not, so every Whisper library is simply absent and the first transcription has
nothing to load.

There is no workaround here short of Whisper.net publishing a musl build. Use a
glibc distribution.

### Why not 32-bit ARM

Whisper.net.Runtime does ship a `linux-arm` native, so a 32-bit build is not
categorically impossible the way an Intel Mac is. It is simply not a target
this project offers, and nothing in it has been thought about with 32-bit
address space in mind — a `turbo` model alone is a third of what a 32-bit
process can address. If you want to try, you are on your own further than the
rest of this page leaves you.

## Expect it to be slow

This is the part to set expectations on, because it is the difference between
"it works" and "it is useful". Transcription is the whole cost of a run, and on
ARM it is CPU-only.

The tool reports its own throughput at the end of every run as a percentage of
real time: audio handed to Whisper, divided by the wall-clock time it took.
100% is break-even, a minute of audio per minute of work — so "transcription
speed 793% of real-time" is just under eight times faster than listening, not
793 times. On a desktop with a GPU the figure runs in the high hundreds to the
low thousands of percent. On a small ARM board on CPU, expect a fraction of
that, and do not be alarmed by a number below 100%.

Below 100% is slower than listening, but it is not the same as a run that
outlasts the book, because only a fraction of a book is ever transcribed —
the passes probe pauses and jingles rather than reading the whole thing. Across
a 134-book reference library that share came out at a median of 12%, and under
19% for nine books in ten. So a board managing 50% of real time on a
twelve-hour book is looking at roughly three hours of transcription, not
twenty-four. The share is the second figure on the same summary line, so your
own runs will tell you what it is for your material.

Two things follow:

- **Prefer a small model.** `--model tiny --upgrade-model small` is a
  reasonable ARM default. The pass structure matters more than the model here:
  most of a book is read by the probing model, and only gaps and re-reads by
  the upgrade one.
- **Use `--summary`** so a long unattended run tells you afterwards what it
  did, and let it work through a folder overnight rather than watching it.

A Pi acting as a small home server that chews through one audiobook a night is
a realistic use of this; converting a library in an afternoon is not.

## If it works, or if it does not

Either way, say so on the [issue tracker](../../../issues) — including the
boring case where it just worked. "Ran a 12-hour book on a Pi 5, all 40
chapters correct, took six hours" is worth more here than any amount of code
review, because it is the one thing nobody on this side can produce. If you do
report a run, the `--version` banner's second line names the platform build and
the system, which answers most of the follow-up questions before they are
asked.
