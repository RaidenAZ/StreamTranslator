# Python Workers

Both workers are launched and stopped by the C# app. They read UTF-8 JSON Lines from stdin and write protocol messages only to stdout.

## Dependencies

All requirement files pin exact versions so worker builds are reproducible:

- `requirements.txt` — runtime dependencies only (what gets frozen into the worker exes).
- `requirements-dev.txt` — runtime + `pytest`, for running `python -m pytest tests`.
- `requirements-build.txt` — runtime + `pyinstaller`, used by `scripts/publish.ps1` inside a dedicated venv (`artifacts/build-venv`); the host Python environment is not touched.

## ASR Worker

Required environment variables:

- `ASR_API_KEY` (legacy alias `MIMO_API_KEY`)
- `ASR_BASE_URL`, optional (legacy alias `MIMO_BASE_URL`), defaults to `https://api.xiaomimimo.com/v1`
- `ASR_MODEL`, optional (legacy alias `MIMO_ASR_MODEL`), defaults to `mimo-v2.5-asr`
- `ASR_PROVIDER`, optional: `Mimo` (default), `Whisper`, or `DeepSeekTools`
- `ASR_TIMEOUT_SECONDS`, optional (legacy alias `MIMO_TIMEOUT_SECONDS`), defaults to `30`
- `ASR_MAX_CONCURRENCY`, optional (legacy alias `MIMO_MAX_CONCURRENCY`), defaults to `2`

The `Mimo` provider validates `wav`/`mp3`, the MiMo 10MB Base64 limit, and protocol languages `auto`/`zh`/`en`. `Whisper` uses the OpenAI `audio.transcriptions` file upload. `DeepSeekTools` posts a multipart upload to DeepSeek's standalone ASR endpoint; for this provider `ASR_BASE_URL` is the full endpoint and defaults to `https://api.deepseek.com/v1/tools/asr`. It accepts 16-bit PCM WAV only, up to 8 MiB and 120 seconds. The request has no model or language parameter (the service detects the language itself), so `ASR_MODEL` is ignored. The production C# app locks recognition to `auto`; `zh` and `en` remain worker-level compatibility values and are not exposed in the UI. Errors are returned with `errorKind`, `statusCode`, and `retryable` fields so C# can apply the V1.0 retry policy.

The worker is an implementation detail. Users should not start or stop it manually.

## Translation Worker

`translation_worker.py` is the independent V1.2 OpenAI Chat Completions compatible worker. C# sends one `configure` message over stdin, followed by concurrent `translate` messages. API Keys are never passed in process arguments.

The worker:

- keeps the ASR process and failure state independent;
- uses the configured Base URL exactly as supplied and resolves `chat/completions` without adding `/v1`;
- disables OpenAI SDK retries so C# owns the single retry policy;
- supports Standard, DeepSeek, Qwen + vLLM, and validated Custom `extra_body` templates;
- uses the built-in `translation-v1` prompt and non-streaming responses;
- normalizes text content and classifies protocol, authentication, timeout, rate-limit, connection, and server failures.

The translation service itself is user-managed. StreamTranslator does not start or stop vLLM, Ollama, llama.cpp, or LM Studio.
