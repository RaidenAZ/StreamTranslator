# ASR Worker

The Python worker is launched by the C# app in V1.0. It reads JSON Lines from stdin and writes JSON Lines to stdout.

Required environment variables:

- `MIMO_API_KEY`
- `MIMO_BASE_URL`, optional, defaults to `https://api.xiaomimimo.com/v1`
- `MIMO_ASR_MODEL`, optional, defaults to `mimo-v2.5-asr`
- `MIMO_TIMEOUT_SECONDS`, optional, defaults to `30`
- `MIMO_MAX_CONCURRENCY`, optional, defaults to `2`

The worker validates `wav`/`mp3`, the MiMo 10MB Base64 limit, and languages `auto`/`zh`/`en`. Errors are returned with `errorKind`, `statusCode`, and `retryable` fields so C# can apply the V1.0 retry policy.

The worker is an implementation detail. Users should not start or stop it manually.
