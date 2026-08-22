# Building ABChapterize on macOS

**Read this first:** there is no macOS release, and there is no macOS support.
What exists is a source tree that should compile and run on Apple Silicon,
written by someone who has never run it there. Nobody on this project owns a
Mac, no CI builds one, and not one chapter mark has ever been placed by this
tool on macOS. Everything below is "this is what it is supposed to do"; if it
turns out to be wrong, that is a bug and not a surprise.

If you would rather not be the person finding out, use the Windows or Linux
build from the [Releases](../../../releases) page instead.

## What you need

- **Apple Silicon.** Intel Macs are out of scope permanently, not merely
  untested — see [Why not Intel](#why-not-intel) at the bottom.
- **The [.NET 10 SDK](https://dotnet.microsoft.com/download)**, arm64 build.
- **ffmpeg and ffprobe.** `brew install ffmpeg`, or MacPorts'
  `sudo port install ffmpeg`. ABChapterize looks in `PATH`, then in
  `/opt/homebrew/bin`, `/usr/local/bin`, `/opt/local/bin`, `/usr/bin`, `~/bin`
  and `~/.local/bin`, and `$FFMPEG_DIR` overrides all of it.
- **About 7 GB free** wherever the speech models land, if you use the larger
  ones. They are downloaded on first use, not shipped.

## Building it

```sh
git clone https://github.com/Inguiotar/ABChapterize.git
cd ABChapterize
dotnet publish -c Release -r osx-arm64
```

The result lands in `bin/publish/osx-arm64/`. Run it from there:

```sh
./bin/publish/osx-arm64/abchapterize --version
```

**Build on the Mac, not from another machine.** That is not a preference, it
removes both of the things that would otherwise stand between you and a binary
that runs:

- Apple Silicon refuses to execute an unsigned binary. `dotnet publish` ad-hoc
  signs the apphost by itself, but only when it is running *on* macOS. A copy
  cross-published from Windows or Linux arrives unsigned and will not start.
- A file you built yourself was never downloaded, so it carries no
  `com.apple.quarantine` attribute and Gatekeeper has nothing to object to.
  A binary someone sent you would need `xattr -d` first.

Run the tests too, because they are the only part of this that anyone can
actually check:

```sh
dotnet test tests/ABChapterize.Tests
```

`SileroVadDetectorTests` is the interesting one: it loads the real ONNX Runtime
and runs the actual VAD model. If that passes, the native library resolution
described below is working on your machine, which is the single most likely
thing to be wrong.

## What the publish folder should look like

macOS is the one platform where the two bundles of native libraries disagree
about how to spell the platform, so both spellings turn up side by side:

```
bin/publish/osx-arm64/
  abchapterize
  ggml-metal.metal
  runtimes/macos-arm64/     <- Whisper's natives (Whisper.net's spelling)
    libwhisper.dylib, libggml-*.dylib including -metal- and -blas-
  runtimes/osx-arm64/       <- ONNX Runtime's native (the .NET RID spelling)
    libonnxruntime.dylib
```

If `runtimes/` is missing or holds a `win-x64` or `linux-x64` folder instead,
the prune step in `ABChapterize.csproj` went wrong — that is the one place in
this whole port that cannot fail gracefully, so it is worth a look before
anything else.

Do not move or delete `ggml-metal.metal`. It is ggml's Metal shader source and
ggml looks for it beside the executable.

## What works, what is a coin flip

| | |
| --- | --- |
| Detection, ffmpeg handling, all languages | Pure C# and an external process. No reason for these to behave differently than on Linux. |
| The VAD pre-pass | Needs ONNX Runtime to load out of `runtimes/osx-arm64/`. Covered by the unit tests — run them. |
| Whisper on the CPU | Should work. |
| Whisper on the GPU | Not selectable. See below. |
| `--vad-threads auto` / `--whisper-threads auto` | Reads `hw.perflevel0.physicalcpu` so that efficiency cores are not counted as workers. Written to Apple's documented sysctl names, never observed. If the numbers look wrong, pass the thread counts explicitly. |

### GPUs

`--use-gpu` and `--list-gpus` do nothing useful on macOS, and say so when you
run them. They work through Vulkan, macOS has no Vulkan loader, and Whisper.net
publishes neither a Vulkan nor a CUDA native for the platform.

What that does *not* mean is that the GPU sits idle. Whisper.net loads its CPU
runtime, and ggml then picks up the Metal backend from
`libggml-metal-whisper.dylib` in the same folder if it can. The startup banner
will say `Cpu backend` regardless, because that is the runtime Whisper.net
reports loading, and guessing anything else would be exactly that. Whether
Metal actually engages is the largest single unknown in this port.

### Why not Intel

Microsoft.ML.OnnxRuntime ships no `osx-x64` native at all — the package covers
android, ios, linux-arm64, linux-x64, osx-arm64, win-arm64 and win-x64, and
that is the complete list. No ONNX Runtime means no Silero VAD, and since
0.12.0 the VAD pre-pass runs on every file with a load failure ending the run
rather than degrading it. An Intel Mac would therefore not run this tool at
all, rather than run it without jingle geometry. Adding a fallback path for one
untestable platform is not a trade this project is going to make.

## If it works, or if it does not

Either way, say so on the [issue tracker](../../../issues) — including the
boring case where it just worked. "Ran a 12-hour book on an M2, all 40 chapters
correct" is worth more here than any amount of code review, because it is the
one thing nobody on this side can produce.
