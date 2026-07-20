# Third-party notices

ABChapterize bundles the following third-party components. Their full license
texts are in the `licenses/` folder next to this file (and shipped alongside
the executable in every published build).

## Silero VAD

`assets/silero_vad.onnx` is the Silero VAD speech/non-speech detection model,
embedded into the executable and used (with `--jingle`) to find jingle
transitions that have no detectable amplitude gap.

- Project: https://github.com/snakers4/silero-vad
- License: MIT — see [`licenses/silero-vad-LICENSE`](licenses/silero-vad-LICENSE)
- Copyright (c) 2020-present Silero Team

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

Used to run the Silero VAD ONNX model for `--jingle`'s speech/non-speech pre-pass.

- Project: https://github.com/Microsoft/onnxruntime
- License: MIT — see [`licenses/onnxruntime-LICENSE`](licenses/onnxruntime-LICENSE)
- Copyright (c) Microsoft Corporation
