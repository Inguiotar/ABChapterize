# Third-party notices

ABChapterize bundles the following third-party components. Their full license
texts are in the `licenses/` folder next to this file (and shipped alongside
the executable in every published build).

## Silero VAD

`assets/silero_vad.onnx` is the Silero VAD speech/non-speech detection model,
embedded into the executable and used to find jingle transitions that have no
detectable amplitude gap. The pre-pass runs over every file processed.

- Project: https://github.com/snakers4/silero-vad
- License: MIT — see [`licenses/silero-vad-LICENSE`](licenses/silero-vad-LICENSE)
- Copyright (c) 2020-present Silero Team

## LavaSR (speech denoiser)

`assets/lavasr_denoiser.onnx` is LavaSR's speech-enhancement denoiser, embedded
into the executable and used to re-read a probe window whose announcement the
recognizer garbled on low-fidelity audio.

- Project: https://github.com/ysharma3501/LavaSR
- ONNX conversion: https://github.com/Topping1/LavaSR-ONNX
- License: Apache-2.0 — see [`licenses/lavasr-LICENSE`](licenses/lavasr-LICENSE)
- Copyright (c) the LavaSR authors (the upstream LICENSE carries Apache's
  unfilled copyright placeholder, so no holder is named here rather than one
  being guessed at)

The bundled file is `denoiser_core_legacy_fixed63.onnx` from the conversion's
`Alpha-v0.1` release, 1,815,317 bytes, SHA-256
`8afa7f4db9f356f7bfb575bb207d8673a728a7baf6773e0b10226a5e15687f2a`.

**Maintenance note — re-check this release about monthly.** The conversion is
labelled alpha, so its assets may be replaced in place or superseded without a
new tag. Compare the SHA-256 above against the current
`Alpha-v0.1` asset (and look for a newer release); if it has changed, re-run the
equivalence check in `SpeechDenoiserTests` before taking the new file, since the
denoiser's exact output is what a probe's transcript depends on.

## Whisper.net

The `Whisper.net`, `Whisper.net.Runtime`, `Whisper.net.Runtime.Cuda`, and
`Whisper.net.Runtime.Vulkan` NuGet packages provide the .NET bindings and native
CPU/CUDA/Vulkan backends used for speech-to-text transcription.

- Project: https://github.com/sandrohanea/whisper.net
- License: MIT — see [`licenses/whisper-net-LICENSE`](licenses/whisper-net-LICENSE)
- Copyright (c) 2024 sandrohanea

## whisper.cpp

The Whisper.net.Runtime* packages bundle native libraries built from whisper.cpp,
the C/C++ inference engine that actually performs the Whisper transcription.

- Project: https://github.com/ggml-org/whisper.cpp
- License: MIT — see [`licenses/whisper-cpp-LICENSE`](licenses/whisper-cpp-LICENSE)
- Copyright (c) 2023-2026 The ggml authors

## Microsoft.ML.OnnxRuntime

Used to run the Silero VAD ONNX model for the speech/non-speech pre-pass.

- Project: https://github.com/Microsoft/onnxruntime
- License: MIT — see [`licenses/onnxruntime-LICENSE`](licenses/onnxruntime-LICENSE)
- Copyright (c) Microsoft Corporation
