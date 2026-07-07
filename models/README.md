# Models

V1.0 expects the Silero VAD ONNX model at:

```text
models/silero_vad.onnx
```

The current workspace has this file downloaded from the official Silero VAD repository:

```text
https://raw.githubusercontent.com/snakers4/silero-vad/master/src/silero_vad/data/silero_vad.onnx
```

The model file is ignored by git because it is a binary asset. The publish script copies it into:

```text
artifacts/StreamTranslator/models/silero_vad.onnx
```

