# Third-party notices

Winpepper itself is licensed under the Apache License 2.0 (see LICENSE).
The components below are NOT distributed inside the Winpepper installer;
they are downloaded on the user's request from the pinned sources recorded
in `src/Winpepper.Models/ModelRegistry.cs` (URL + SHA-256 verified).

## transcribe.cpp (native runtime, downloaded at user request)

- Project: https://github.com/handy-computer/transcribe.cpp — version v0.1.3
- License: MIT
- The runtime archive bundles ggml (MIT) and other MIT-licensed components;
  the archive ships its complete license texts under `licenses/` and they are
  installed verbatim to
  `%LOCALAPPDATA%\winpepper\models\nemotron-streaming-en\runtime\transcribe-native-windows-x86_64-cpu-vulkan\licenses\`.

```
MIT License

Copyright (c) 2026 The transcribe.cpp authors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Nemotron Speech Streaming model weights (downloaded at user request)

- Model: nvidia/nemotron-speech-streaming-en-0.6b, GGUF Q8_0 conversion by
  handy-computer (https://huggingface.co/handy-computer/nemotron-speech-streaming-en-0.6b-gguf)
- License: NVIDIA Open Model License ("license: other" on Hugging Face) —
  https://www.nvidia.com/en-us/agreements/enterprise-software/nvidia-open-model-license/
- Licensed by NVIDIA Corporation under the NVIDIA Open Model License
- The weights are not redistributed by this project. Users download them
  directly from Hugging Face via the Models tab; the License provides that by
  using, reproducing, or distributing any portion of the Model you agree to be
  bound by the Agreement (acceptance by conduct — no click-through is
  required). The attribution line above and this link to the Agreement are
  included preemptively to satisfy the License's Section 3 notice condition in
  case facilitating the download is ever characterized as distribution. The
  License may be updated by NVIDIA; the live URL above is authoritative.

## Nemotron 3.5 ASR Streaming multilingual model weights (downloaded at user request)

- Model: nvidia/nemotron-3.5-asr-streaming-0.6b, GGUF Q8_0 conversion by
  handy-computer (https://huggingface.co/handy-computer/nemotron-3.5-asr-streaming-0.6b-gguf)
- License: OpenMDW-1.1 (Open Model, Data, and Weights License Agreement,
  version 1.1), per the upstream Hugging Face model card and the GGUF
  conversion's README (verified 2026-08-08)
- The weights are not redistributed by this project. Users download them
  directly from Hugging Face via the Models tab. The attribution line and
  license identification above are included preemptively in case facilitating
  the download is ever characterized as distribution; the upstream Hugging
  Face model card is authoritative for the governing terms.

## Existing model downloads

The Parakeet-TDT ONNX models and the Qwen cleanup model are likewise
downloaded at user request from the sources in `ModelRegistry.cs` under their
respective upstream licenses.
