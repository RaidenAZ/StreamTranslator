# ASR Worker

The Python worker is launched by the C# app in V1.0. It reads JSON Lines from stdin and writes JSON Lines to stdout.

Required environment variables:

- `MIMO_API_KEY`
- `MIMO_BASE_URL`, optional, defaults to `https://api.xiaomimimo.com/v1`
- `MIMO_ASR_MODEL`, optional, defaults to `mimo-v2.5-asr`
- `MIMO_TIMEOUT_SECONDS`, optional, defaults to `30`

The worker is an implementation detail. Users should not start or stop it manually.

