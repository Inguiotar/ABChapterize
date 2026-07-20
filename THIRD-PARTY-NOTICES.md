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
