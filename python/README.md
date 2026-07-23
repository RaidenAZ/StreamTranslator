# Python Workers

Both workers are launched and stopped by the C# app. They read UTF-8 JSON Lines from stdin and write protocol messages only to stdout.

## ASR Worker

Required environment variables:

- `MIMO_API_KEY`
- `MIMO_BASE_URL`, optional, defaults to `https://api.xiaomimimo.com/v1`
- `MIMO_ASR_MODEL`, optional, defaults to `mimo-v2.5-asr`
- `MIMO_TIMEOUT_SECONDS`, optional, defaults to `30`
- `MIMO_MAX_CONCURRENCY`, optional, defaults to `2`

The worker validates `wav`/`mp3`, the MiMo 10MB Base64 limit, and protocol languages `auto`/`zh`/`en`. The production C# app locks recognition to `auto`; `zh` and `en` remain worker-level compatibility values and are not exposed in the UI. Errors are returned with `errorKind`, `statusCode`, and `retryable` fields so C# can apply the V1.0 retry policy.

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
