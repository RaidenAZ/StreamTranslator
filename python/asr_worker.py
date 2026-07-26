from __future__ import annotations

import json
import os
import sys
import threading
import time
import traceback
from concurrent.futures import Future, ThreadPoolExecutor
from dataclasses import dataclass
from typing import Any, TextIO

from openai import OpenAI


DEFAULT_BASE_URL = "https://api.xiaomimimo.com/v1"
DEFAULT_MODEL = "mimo-v2.5-asr"


@dataclass(frozen=True)
class WorkerConfig:
    api_key: str
    base_url: str
    model: str
    timeout_seconds: float
    max_concurrency: int

    @staticmethod
    def from_env() -> "WorkerConfig":
        return WorkerConfig(
            api_key=os.environ.get("MIMO_API_KEY", ""),
            base_url=os.environ.get("MIMO_BASE_URL", DEFAULT_BASE_URL),
            model=os.environ.get("MIMO_ASR_MODEL", DEFAULT_MODEL),
            timeout_seconds=float(os.environ.get("MIMO_TIMEOUT_SECONDS", "30")),
            max_concurrency=max(1, int(os.environ.get("MIMO_MAX_CONCURRENCY", "2"))),
        )


class AsrWorker:
    def __init__(self, config: WorkerConfig, client: Any | None = None) -> None:
        self._config = config
        self._client: Any | None = client
        self._client_lock = threading.Lock()

    def health_check(self) -> None:
        if not self._config.api_key:
            raise ValueError("MIMO_API_KEY is required")
        if self._config.max_concurrency < 1:
            raise ValueError("MIMO_MAX_CONCURRENCY must be positive")

    def transcribe(self, request: dict[str, Any]) -> dict[str, Any]:
        started = time.perf_counter()
        audio_base64 = request.get("audioBase64") or request.get("audio_base64")
        audio_format = request.get("audioFormat") or request.get("audio_format") or "wav"
        language = request.get("language") or "auto"

        if not audio_base64:
            raise ValueError("audioBase64 is required")
        self.health_check()
        if audio_format not in ("wav", "mp3"):
            raise ValueError("audioFormat must be wav or mp3")
        if language not in ("auto", "zh", "en"):
            raise ValueError("language must be auto, zh, or en")
        if len(audio_base64.encode("ascii")) > 10 * 1024 * 1024:
            raise ValueError("audioBase64 exceeds the MiMo 10MB limit")

        mime_type = "audio/wav" if audio_format == "wav" else "audio/mpeg"
        completion = self._get_client().chat.completions.create(
            model=self._config.model,
            messages=[
                {
                    "role": "user",
                    "content": [
                        {
                            "type": "input_audio",
                            "input_audio": {
                                "data": f"data:{mime_type};base64,{audio_base64}",
                            },
                        }
                    ],
                }
            ],
            extra_body={
                "asr_options": {
                    "language": language,
                }
            },
        )

        text = extract_text(completion)
        latency_ms = int((time.perf_counter() - started) * 1000)
        return {
            "id": request["id"],
            "type": "transcribe_result",
            "ok": True,
            "sequence": request.get("sequence"),
            "text": text,
            "latencyMs": latency_ms,
        }

    def _get_client(self) -> OpenAI:
        if self._client is not None:
            return self._client

        with self._client_lock:
            if self._client is None:
                self._client = OpenAI(
                    api_key=self._config.api_key,
                    base_url=self._config.base_url,
                    timeout=self._config.timeout_seconds,
                )

        return self._client


def extract_text(completion: Any) -> str:
    choices = getattr(completion, "choices", None)
    if not choices:
        return ""

    message = getattr(choices[0], "message", None)
    content = getattr(message, "content", "") if message is not None else ""
    if isinstance(content, str):
        return content

    if isinstance(content, list):
        parts: list[str] = []
        for item in content:
            if isinstance(item, dict) and isinstance(item.get("text"), str):
                parts.append(item["text"])
        return "".join(parts)

    return str(content)


def ok_response(request_id: str, message_type: str) -> dict[str, Any]:
    return {
        "id": request_id,
        "type": message_type,
        "ok": True,
    }


def error_response(request: dict[str, Any], exc: BaseException) -> dict[str, Any]:
    status_code = getattr(exc, "status_code", None)
    error_kind, retryable = classify_error(exc, status_code)
    return {
        "id": request.get("id", ""),
        "type": "error",
        "ok": False,
        "sequence": request.get("sequence"),
        "errorCode": exc.__class__.__name__,
        "errorMessage": str(exc),
        "errorKind": error_kind,
        "statusCode": status_code,
        "retryable": retryable,
    }


def classify_error(exc: BaseException, status_code: int | None) -> tuple[str, bool]:
    class_name = exc.__class__.__name__.lower()
    if status_code in (401, 403) or "authentication" in class_name or "permissiondenied" in class_name:
        return "authentication", False
    if status_code == 429 or "ratelimit" in class_name:
        return "rate_limit", True
    if "timeout" in class_name:
        return "timeout", True
    if "connection" in class_name:
        return "network", True
    if status_code is not None and status_code >= 500:
        return "server", True
    if isinstance(exc, ValueError):
        return "invalid_request", False
    return "api", status_code in (408, 409, 425)


def write_json(payload: dict[str, Any], stream: TextIO | None = None) -> None:
    target = stream or sys.stdout
    print(json.dumps(payload, ensure_ascii=False, separators=(",", ":")), file=target, flush=True)


def run_protocol(
    input_stream: TextIO,
    output_stream: TextIO,
    worker: Any,
    max_concurrency: int,
) -> None:
    write_lock = threading.Lock()

    def write_payload(payload: dict[str, Any]) -> None:
        with write_lock:
            write_json(payload, output_stream)

    def finish_transcription(future: Future[dict[str, Any]], request: dict[str, Any]) -> None:
        try:
            write_payload(future.result())
        except Exception as exc:  # noqa: BLE001 - each request must receive a terminal response.
            traceback.print_exc(file=sys.stderr)
            write_payload(error_response(request, exc))

    executor = ThreadPoolExecutor(max_workers=max(1, max_concurrency), thread_name_prefix="mimo-asr")
    try:
        for line in input_stream:
            line = line.strip().lstrip("\ufeff")
            if not line:
                continue

            request: dict[str, Any] = {}
            try:
                request = json.loads(line)
                message_type = request.get("type")
                request_id = request.get("id", "")

                if message_type == "ping":
                    worker.health_check()
                    write_payload(ok_response(request_id, "ready"))
                elif message_type == "shutdown":
                    write_payload(ok_response(request_id, "shutdown"))
                    executor.shutdown(wait=False, cancel_futures=True)
                    return
                elif message_type == "transcribe":
                    future = executor.submit(worker.transcribe, request)
                    future.add_done_callback(lambda completed, item=request: finish_transcription(completed, item))
                else:
                    raise ValueError(f"unknown request type: {message_type}")
            except Exception as exc:  # noqa: BLE001 - malformed requests must not terminate the worker.
                traceback.print_exc(file=sys.stderr)
                write_payload(error_response(request, exc))
    finally:
        executor.shutdown(wait=True, cancel_futures=False)


def _force_utf8_stdio() -> None:
    # The host process speaks UTF-8 JSON lines; never fall back to the locale
    # code page (e.g. GBK on Chinese Windows).
    for stream in (sys.stdin, sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is not None:
            reconfigure(encoding="utf-8")


def main() -> int:
    _force_utf8_stdio()
    config = WorkerConfig.from_env()
    worker = AsrWorker(config)
    write_json({"id": "startup", "type": "ready", "ok": True})
    run_protocol(sys.stdin, sys.stdout, worker, config.max_concurrency)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
