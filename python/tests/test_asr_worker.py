import io
import json
import threading
import time
from types import SimpleNamespace

import pytest

from asr_worker import AsrWorker, WorkerConfig, error_response, extract_text, run_protocol


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

    with pytest.raises(ValueError, match="MIMO_API_KEY"):
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


def config(api_key: str) -> WorkerConfig:
    return WorkerConfig(
        api_key=api_key,
        base_url="https://api.xiaomimimo.com/v1",
        model="mimo-v2.5-asr",
        timeout_seconds=30,
        max_concurrency=2,
    )


class FakeCompletions:
    def __init__(self):
        self.kwargs = None

    def create(self, **kwargs):
        self.kwargs = kwargs
        return SimpleNamespace(choices=[SimpleNamespace(message=SimpleNamespace(content="识别结果"))])


class FakeClient:
    def __init__(self, completions):
        self.chat = SimpleNamespace(completions=completions)


class FakeHttpError(Exception):
    def __init__(self, message: str, status_code: int):
        super().__init__(message)
        self.status_code = status_code


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
