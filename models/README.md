# Models

V1.0 expects the Silero VAD ONNX model at:

```text
models/silero_vad.onnx
```

The publish process verifies the model and downloads it from a pinned Silero VAD commit when missing:

```text
https://raw.githubusercontent.com/snakers4/silero-vad/b163605b3f44c3aadf28f97b125a2f7c461e9a7f/src/silero_vad/data/silero_vad.onnx
```

The model file is ignored by git because it is a binary asset. The publish script copies it into:

```text
artifacts/StreamTranslator/models/silero_vad.onnx
```

Expected SHA256:

```text
1A153A22F4509E292A94E67D6F9B85E8DEB25B4988682B7E174C65279D8788E3
```
