import base64
import io
import json
import threading
import time
import wave
from types import SimpleNamespace

import pytest

from asr_worker import AsrWorker, WorkerConfig, WorkerError, error_response, extract_text, run_protocol


def test_extract_text_reads_message_content_string():
    completion = SimpleNamespace(
        choices=[
            SimpleNamespace(
                message=SimpleNamespace(content="识别文本"),
            )
        ]
    )

    assert extract_text(completion) == "识别文本"


def test_extract_text_joins_text_parts():
    completion = SimpleNamespace(
        choices=[
            SimpleNamespace(
                message=SimpleNamespace(
                    content=[
                        {"type": "text", "text": "hello"},
                        {"type": "text", "text": " world"},
                    ]
                ),
            )
        ]
    )

    assert extract_text(completion) == "hello world"


def test_transcribe_sends_mimo_data_url_and_language():
    completions = FakeCompletions()
    worker = AsrWorker(config(api_key="key"), client=FakeClient(completions))

    result = worker.transcribe(
        {
            "id": "seg-1",
            "type": "transcribe",
            "sequence": 1,
            "audioBase64": "UklGRg==",
            "audioFormat": "wav",
            "language": "zh",
        }
    )

    audio = completions.kwargs["messages"][0]["content"][0]["input_audio"]
    assert audio["data"] == "data:audio/wav;base64,UklGRg=="
    assert completions.kwargs["extra_body"] == {"asr_options": {"language": "zh"}}
    assert result["text"] == "识别结果"


def test_health_check_rejects_missing_api_key():
    worker = AsrWorker(config(api_key=""))

    with pytest.raises(ValueError, match="ASR_API_KEY"):
        worker.health_check()


def test_protocol_accepts_utf8_bom_before_first_json_message():
    output = io.StringIO()
    worker = TrackingWorker()

    run_protocol(
        io.StringIO("\ufeff" + json.dumps({"id": "shutdown-1", "type": "shutdown"})),
        output,
        worker,
        max_concurrency=1,
    )

    response = json.loads(output.getvalue())
    assert response == {"id": "shutdown-1", "type": "shutdown", "ok": True}


def test_transcribe_whisper_uses_file_upload():
    transcriptions = FakeTranscriptions()
    worker = AsrWorker(config_whisper(api_key="key"), client=FakeWhisperClient(transcriptions))

    result = worker.transcribe(
        {
            "id": "seg-1",
            "type": "transcribe",
            "sequence": 1,
            "audioBase64": "UklGRg==",
            "audioFormat": "wav",
            "language": "zh",
        }
    )

    assert transcriptions.kwargs["model"] == "glm-asr-2512"
    assert transcriptions.kwargs.get("language") == "zh"
    # file must be a file-like object with a name attribute
    assert hasattr(transcriptions.kwargs["file"], "read")
    assert transcriptions.kwargs["file"].name == "audio.wav"
    assert result["text"] == "whisper 识别结果"


def test_transcribe_whisper_converts_auto_language_to_none():
    transcriptions = FakeTranscriptions()
    worker = AsrWorker(config_whisper(api_key="key"), client=FakeWhisperClient(transcriptions))

    worker.transcribe(
        {
            "id": "seg-1",
            "audioBase64": "UklGRg==",
            "audioFormat": "wav",
            "language": "auto",
        }
    )

    # "auto" must become None for the standard Whisper API
    assert transcriptions.kwargs.get("language") is None


def test_transcribe_deepseek_posts_multipart_wav_to_tools_asr():
    client = FakeHttpxClient(200, {"text": "deepseek 识别结果", "duration_ms": 3200})
    worker = AsrWorker(config_deepseek(api_key="ds-key"), client=client)

    result = worker.transcribe(
        {
            "id": "seg-1",
            "type": "transcribe",
            "sequence": 1,
            "audioBase64": base64.b64encode(wav_bytes(seconds=1)).decode("ascii"),
            "audioFormat": "wav",
            "language": "auto",
        }
    )

    assert client.url == "https://api.deepseek.com/v1/tools/asr"
    assert client.headers["Authorization"] == "Bearer ds-key"
    filename, content, content_type = client.files["file"]
    assert filename == "audio.wav"
    assert content.startswith(b"RIFF")
    assert content_type == "audio/wav"
    # The service detects the language itself; nothing language-related is sent.
    assert "language" not in client.captured
    assert result["text"] == "deepseek 识别结果"
    assert result["ok"] is True


def test_from_env_defaults_deepseek_to_documented_endpoint_without_model(monkeypatch):
    for name in ("ASR_BASE_URL", "MIMO_BASE_URL", "ASR_MODEL", "MIMO_ASR_MODEL"):
        monkeypatch.delenv(name, raising=False)
    monkeypatch.setenv("ASR_PROVIDER", "DeepSeekTools")
    monkeypatch.setenv("ASR_API_KEY", "ds-key")

    loaded = WorkerConfig.from_env()

    assert loaded.base_url == "https://api.deepseek.com/v1/tools/asr"
    assert loaded.model == ""


def test_transcribe_deepseek_rejects_non_wav_before_request():
    client = FakeHttpxClient(200, {"text": "unused"})
    worker = AsrWorker(config_deepseek(api_key="ds-key"), client=client)

    with pytest.raises(ValueError, match="WAV"):
        worker.transcribe({"id": "seg-1", "audioBase64": "AAAA", "audioFormat": "mp3", "language": "auto"})

    assert client.url is None


def test_transcribe_deepseek_rejects_audio_longer_than_120_seconds():
    client = FakeHttpxClient(200, {"text": "unused"})
    worker = AsrWorker(config_deepseek(api_key="ds-key"), client=client)

    with pytest.raises(ValueError, match="120"):
        worker.transcribe(
            {
                "id": "seg-1",
                "audioBase64": base64.b64encode(wav_bytes(seconds=121)).decode("ascii"),
                "audioFormat": "wav",
                "language": "auto",
            }
        )

    assert client.url is None


@pytest.mark.parametrize(
    ("status_code", "kind", "retryable"),
    [(401, "authentication", False), (429, "rate_limit", True), (503, "server", True), (504, "server", True)],
)
def test_transcribe_deepseek_classifies_http_failures(status_code, kind, retryable):
    worker = AsrWorker(
        config_deepseek(api_key="ds-key"),
        client=FakeHttpxClient(status_code, "service unavailable"),
    )

    with pytest.raises(WorkerError) as caught:
        worker.transcribe(
            {
                "id": "seg-1",
                "audioBase64": base64.b64encode(wav_bytes(seconds=1)).decode("ascii"),
                "audioFormat": "wav",
                "language": "auto",
            }
        )

    assert caught.value.kind == kind
    assert caught.value.retryable is retryable


def test_transcribe_deepseek_rejects_response_without_text():
    worker = AsrWorker(config_deepseek(api_key="ds-key"), client=FakeHttpxClient(200, {"duration_ms": 1}))

    with pytest.raises(WorkerError) as caught:
        worker.transcribe(
            {
                "id": "seg-1",
                "audioBase64": base64.b64encode(wav_bytes(seconds=1)).decode("ascii"),
                "audioFormat": "wav",
                "language": "auto",
            }
        )

    assert caught.value.kind == "invalid_response"
    assert caught.value.retryable is False


def test_unknown_provider_fails_at_construction():
    with pytest.raises(WorkerError, match="provider"):
        AsrWorker(config_with_provider("Nope"))


def test_transcribe_rejects_unsupported_language_before_api_call():
    worker = AsrWorker(config(api_key="key"), client=FakeClient(FakeCompletions()))

    with pytest.raises(ValueError, match="language"):
        worker.transcribe({"id": "seg-1", "audioBase64": "AAAA", "audioFormat": "wav", "language": "ja"})


@pytest.mark.parametrize(
    ("status_code", "kind", "retryable"),
    [(401, "authentication", False), (403, "authentication", False), (429, "rate_limit", True)],
)
def test_error_response_classifies_http_failures(status_code, kind, retryable):
    exc = FakeHttpError("request failed", status_code)

    response = error_response({"id": "seg-1", "sequence": 1}, exc)

    assert response["statusCode"] == status_code
    assert response["errorKind"] == kind
    assert response["retryable"] is retryable


def test_run_protocol_executes_transcriptions_with_bounded_concurrency():
    worker = TrackingWorker()
    requests = "\n".join(
        json.dumps({"id": f"seg-{index}", "type": "transcribe", "sequence": index})
        for index in (1, 2)
    )
    output = io.StringIO()

    run_protocol(io.StringIO(requests), output, worker, max_concurrency=2)

    responses = [json.loads(line) for line in output.getvalue().splitlines()]
    assert worker.max_active == 2
    assert {response["sequence"] for response in responses} == {1, 2}


def test_protocol_rejects_transcribe_over_capacity_without_unbounded_queue():
    worker = BlockingWorker()
    requests = "\n".join(
        json.dumps({"id": f"seg-{index}", "type": "transcribe", "sequence": index})
        for index in (1, 2)
    )
    output = io.StringIO()
    thread = threading.Thread(target=run_protocol, args=(io.StringIO(requests), output, worker, 1))
    thread.start()
    try:
        _wait_for_output(output, '"errorKind":"backpressure"')
    finally:
        worker.release.set()
        thread.join(timeout=5)
    assert not thread.is_alive()

    responses = {item["id"]: item for item in (json.loads(line) for line in output.getvalue().splitlines())}
    assert responses["seg-2"]["errorKind"] == "backpressure"
    assert responses["seg-2"]["retryable"] is True
    assert responses["seg-1"]["ok"] is True


def test_error_response_redacts_secrets_from_message():
    response = error_response(
        {"id": "seg-1", "sequence": 1},
        RuntimeError("authorization failed for key secret-key-1234"),
        ["secret-key-1234"],
    )

    assert "secret-key-1234" not in response["errorMessage"]
    assert "[REDACTED]" in response["errorMessage"]


def test_get_client_disables_sdk_retries(monkeypatch):
    captured = {}

    class TrackingOpenAI:
        def __init__(self, **kwargs):
            captured.update(kwargs)

    monkeypatch.setattr("asr_worker.OpenAI", TrackingOpenAI)
    worker = AsrWorker(config(api_key="key-12345"))

    worker._get_client()

    assert captured["max_retries"] == 0
    assert captured["timeout"] == 30


def _wait_for_output(output: io.StringIO, marker: str, timeout: float = 2.0) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if marker in output.getvalue():
            return
        time.sleep(0.01)
    raise AssertionError(f"marker {marker!r} not found in output: {output.getvalue()!r}")


def config(api_key: str) -> WorkerConfig:
    return WorkerConfig(
        api_key=api_key,
        base_url="https://api.xiaomimimo.com/v1",
        model="mimo-v2.5-asr",
        provider="Mimo",
        timeout_seconds=30,
        max_concurrency=2,
    )


def config_whisper(api_key: str) -> WorkerConfig:
    return WorkerConfig(
        api_key=api_key,
        base_url="https://open.bigmodel.cn/api/paas/v4",
        model="glm-asr-2512",
        provider="Whisper",
        timeout_seconds=30,
        max_concurrency=2,
    )


def config_deepseek(api_key: str) -> WorkerConfig:
    return WorkerConfig(
        api_key=api_key,
        base_url="https://api.deepseek.com/v1/tools/asr",
        model="",
        provider="DeepSeekTools",
        timeout_seconds=30,
        max_concurrency=2,
    )


def config_with_provider(provider: str) -> WorkerConfig:
    base = config(api_key="key")
    return WorkerConfig(
        api_key=base.api_key,
        base_url=base.base_url,
        model=base.model,
        provider=provider,
        timeout_seconds=base.timeout_seconds,
        max_concurrency=base.max_concurrency,
    )


def wav_bytes(seconds: int, sample_rate: int = 16000) -> bytes:
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as wav_file:
        wav_file.setnchannels(1)
        wav_file.setsampwidth(2)
        wav_file.setframerate(sample_rate)
        wav_file.writeframes(b"\x00\x00" * sample_rate * seconds)
    return buffer.getvalue()


class FakeCompletions:
    def __init__(self):
        self.kwargs = None

    def create(self, **kwargs):
        self.kwargs = kwargs
        return SimpleNamespace(choices=[SimpleNamespace(message=SimpleNamespace(content="识别结果"))])


class FakeClient:
    def __init__(self, completions):
        self.chat = SimpleNamespace(completions=completions)


class FakeTranscriptions:
    def __init__(self):
        self.kwargs = None

    def create(self, **kwargs):
        self.kwargs = kwargs
        return SimpleNamespace(text="whisper 识别结果")


class FakeWhisperClient:
    def __init__(self, transcriptions):
        self.audio = SimpleNamespace(transcriptions=transcriptions)


class FakeHttpResponse:
    def __init__(self, status_code: int, payload):
        self.status_code = status_code
        self._payload = payload
        self.text = payload if isinstance(payload, str) else json.dumps(payload)

    def json(self):
        if isinstance(self._payload, str):
            raise ValueError("not json")
        return self._payload


class FakeHttpxClient:
    def __init__(self, status_code: int, payload):
        self._response = FakeHttpResponse(status_code, payload)
        self.url = None
        self.headers = None
        self.files = None
        self.captured = {}

    def post(self, url, headers=None, files=None, **kwargs):
        self.url = url
        self.headers = headers or {}
        self.files = files or {}
        self.captured = kwargs
        return self._response


class FakeHttpError(Exception):
    def __init__(self, message: str, status_code: int):
        super().__init__(message)
        self.status_code = status_code


class BlockingWorker:
    def __init__(self):
        self.release = threading.Event()

    def transcribe(self, request):
        self.release.wait(timeout=5)
        return {
            "id": request["id"],
            "type": "transcribe_result",
            "ok": True,
            "sequence": request["sequence"],
            "text": "ok",
        }

    def health_check(self):
        return None


class TrackingWorker:
    def __init__(self):
        self.active = 0
        self.max_active = 0
        self.lock = threading.Lock()

    def transcribe(self, request):
        with self.lock:
            self.active += 1
            self.max_active = max(self.max_active, self.active)
        time.sleep(0.05)
        with self.lock:
            self.active -= 1
        return {
            "id": request["id"],
            "type": "transcribe_result",
            "ok": True,
            "sequence": request["sequence"],
            "text": "ok",
        }

    def health_check(self):
        return None
