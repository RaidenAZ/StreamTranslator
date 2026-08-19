from __future__ import annotations

import base64
import io
import json
import os
import sys
import threading
import time
import traceback
from abc import ABC, abstractmethod
from concurrent.futures import Future, ThreadPoolExecutor
from dataclasses import dataclass
from typing import Any, TextIO

from openai import OpenAI


DEFAULT_MIMO_BASE_URL = "https://api.xiaomimimo.com/v1"
DEFAULT_MIMO_MODEL = "mimo-v2.5-asr"

PROVIDER_MIMO = "Mimo"
PROVIDER_WHISPER = "Whisper"


class WorkerError(Exception):
    """Protocol-level failure that already knows its errorKind and retryability."""

    def __init__(self, message: str, kind: str = "configuration", retryable: bool = False) -> None:
        super().__init__(message)
        self.kind = kind
        self.retryable = retryable


@dataclass(frozen=True)
class WorkerConfig:
    api_key: str
    base_url: str
    model: str
    provider: str  # "Mimo" | "Whisper"
    timeout_seconds: float
    max_concurrency: int

    @staticmethod
    def from_env() -> "WorkerConfig":
        # Support both ASR_* (new) and MIMO_* (legacy) env var names.
        def env(new: str, legacy: str, default: str = "") -> str:
            return os.environ.get(new) or os.environ.get(legacy, default)

        return WorkerConfig(
            api_key=env("ASR_API_KEY", "MIMO_API_KEY"),
            base_url=env("ASR_BASE_URL", "MIMO_BASE_URL", DEFAULT_MIMO_BASE_URL),
            model=env("ASR_MODEL", "MIMO_ASR_MODEL", DEFAULT_MIMO_MODEL),
            provider=os.environ.get("ASR_PROVIDER", PROVIDER_MIMO),
            timeout_seconds=float(env("ASR_TIMEOUT_SECONDS", "MIMO_TIMEOUT_SECONDS", "30")),
            max_concurrency=max(1, int(env("ASR_MAX_CONCURRENCY", "MIMO_MAX_CONCURRENCY", "2"))),
        )


class AsrBackend(ABC):
    """Abstract base for ASR provider-specific transcription logic."""

    @abstractmethod
    def transcribe(self, audio_base64: str, audio_format: str, language: str, client: Any) -> str:
        """Transcribe audio and return the recognized text."""


class MimoBackend(AsrBackend):
    """MiMo ASR: uses chat.completions with an inline base64 audio message."""

    _SUPPORTED_FORMATS = frozenset(("wav", "mp3"))
    _SUPPORTED_LANGUAGES = frozenset(("auto", "zh", "en"))
    _MAX_AUDIO_BYTES = 10 * 1024 * 1024

    def __init__(self, config: WorkerConfig) -> None:
        self._config = config

    def transcribe(self, audio_base64: str, audio_format: str, language: str, client: Any) -> str:
        if audio_format not in self._SUPPORTED_FORMATS:
            raise ValueError(f"audioFormat must be one of {sorted(self._SUPPORTED_FORMATS)}")
        if language not in self._SUPPORTED_LANGUAGES:
            raise ValueError(f"language must be one of {sorted(self._SUPPORTED_LANGUAGES)}")
        if len(audio_base64.encode("ascii")) > self._MAX_AUDIO_BYTES:
            raise ValueError("audioBase64 exceeds the MiMo 10 MB limit")

        mime_type = "audio/wav" if audio_format == "wav" else "audio/mpeg"
        completion = client.chat.completions.create(
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
        return extract_text(completion)


class WhisperBackend(AsrBackend):
    """Whisper-compatible ASR: uses audio.transcriptions with a multipart file upload."""

    def __init__(self, config: WorkerConfig) -> None:
        self._config = config

    def transcribe(self, audio_base64: str, audio_format: str, language: str, client: Any) -> str:
        audio_bytes = base64.b64decode(audio_base64)
        audio_file = io.BytesIO(audio_bytes)
        # The OpenAI library inspects the name attribute for MIME-type detection.
        audio_file.name = f"audio.{audio_format}"
        lang = language if language != "auto" else None
        response = client.audio.transcriptions.create(
            model=self._config.model,
            file=audio_file,
            language=lang,
        )
        return response.text if hasattr(response, "text") else str(response)


class AsrWorker:
    def __init__(self, config: WorkerConfig, client: Any | None = None) -> None:
        self._config = config
        self._client: Any | None = client
        self._client_lock = threading.Lock()
        self._backend: AsrBackend = (
            MimoBackend(config) if config.provider == PROVIDER_MIMO else WhisperBackend(config)
        )

    def health_check(self) -> None:
        if not self._config.api_key:
            raise ValueError("ASR_API_KEY is required")
        if self._config.max_concurrency < 1:
            raise ValueError("ASR_MAX_CONCURRENCY must be positive")

    def transcribe(self, request: dict[str, Any]) -> dict[str, Any]:
        started = time.perf_counter()
        audio_base64 = request.get("audioBase64") or request.get("audio_base64")
        audio_format = request.get("audioFormat") or request.get("audio_format") or "wav"
        language = request.get("language") or "auto"

        if not audio_base64:
            raise ValueError("audioBase64 is required")
        self.health_check()

        text = self._backend.transcribe(audio_base64, audio_format, language, self._get_client())
        latency_ms = int((time.perf_counter() - started) * 1000)
        return {
            "id": request["id"],
            "type": "transcribe_result",
            "ok": True,
            "sequence": request.get("sequence"),
            "text": text,
            "latencyMs": latency_ms,
        }

    def sensitive_values(self) -> list[str]:
        return [value for value in [self._config.api_key] if len(value) >= 4]

    def _get_client(self) -> OpenAI:
        if self._client is not None:
            return self._client

        with self._client_lock:
            if self._client is None:
                self._client = OpenAI(
                    api_key=self._config.api_key,
                    base_url=self._config.base_url,
                    timeout=self._config.timeout_seconds,
                    # The host owns the single-retry policy; SDK retries would
                    # stack on top of it and stall the realtime pipeline.
                    max_retries=0,
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


def redact(text: str, secrets: list[str]) -> str:
    # Longest first so a secret that contains another secret is fully masked.
    for secret in sorted(set(secrets), key=len, reverse=True):
        text = text.replace(secret, "[REDACTED]")
    return text


def error_response(
    request: dict[str, Any],
    exc: BaseException,
    secrets: list[str] | None = None,
) -> dict[str, Any]:
    status_code = getattr(exc, "status_code", None)
    error_kind, retryable = classify_error(exc, status_code)
    return {
        "id": request.get("id", ""),
        "type": "error",
        "ok": False,
        "sequence": request.get("sequence"),
        "errorCode": exc.__class__.__name__,
        "errorMessage": redact(str(exc), secrets or []),
        "errorKind": error_kind,
        "statusCode": status_code,
        "retryable": retryable,
    }


def classify_error(exc: BaseException, status_code: int | None) -> tuple[str, bool]:
    if isinstance(exc, WorkerError):
        return exc.kind, exc.retryable

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


def _sensitive_values(worker: Any) -> list[str]:
    getter = getattr(worker, "sensitive_values", None)
    return getter() if callable(getter) else []


def run_protocol(
    input_stream: TextIO,
    output_stream: TextIO,
    worker: Any,
    max_concurrency: int,
) -> None:
    write_lock = threading.Lock()
    # Second line of defense behind the host-side semaphore.
    slots = threading.BoundedSemaphore(max(1, max_concurrency))

    def write_payload(payload: dict[str, Any]) -> None:
        with write_lock:
            write_json(payload, output_stream)

    def log_exception(exc: BaseException) -> None:
        if isinstance(exc, WorkerError):
            return
        print(redact(traceback.format_exc(), _sensitive_values(worker)), file=sys.stderr, flush=True)

    def finish_transcription(future: Future[dict[str, Any]], request: dict[str, Any]) -> None:
        try:
            write_payload(future.result())
        except Exception as exc:  # noqa: BLE001
            log_exception(exc)
            write_payload(error_response(request, exc, _sensitive_values(worker)))
        finally:
            slots.release()

    executor = ThreadPoolExecutor(max_workers=max(1, max_concurrency), thread_name_prefix="asr-worker")
    try:
        for line in input_stream:
            line = line.strip().lstrip("﻿")
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
                    if not slots.acquire(blocking=False):
                        raise WorkerError(
                            "ASR worker is at capacity",
                            kind="backpressure",
                            retryable=True,
                        )
                    try:
                        future = executor.submit(worker.transcribe, request)
                    except BaseException:
                        slots.release()
                        raise
                    future.add_done_callback(lambda completed, item=request: finish_transcription(completed, item))
                else:
                    raise ValueError(f"unknown request type: {message_type}")
            except Exception as exc:  # noqa: BLE001
                log_exception(exc)
                write_payload(error_response(request, exc, _sensitive_values(worker)))
    finally:
        executor.shutdown(wait=True, cancel_futures=False)


def _force_utf8_stdio() -> None:
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