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

import httpx
from openai import OpenAI


DEFAULT_MIMO_BASE_URL = "https://api.xiaomimimo.com/v1"
DEFAULT_MIMO_MODEL = "mimo-v2.5-asr"

PROVIDER_MIMO = "Mimo"
PROVIDER_WHISPER = "Whisper"
PROVIDER_DEEPSEEK_TOOLS = "DeepSeekTools"

# Full endpoint from the DeepSeek ASR docs; it takes no model parameter.
DEFAULT_DEEPSEEK_ASR_URL = "https://api.deepseek.com/v1/tools/asr"

# DeepSeek ASR caps: 8 MiB upload, 120 s duration, 16-bit PCM WAV or Ogg Opus.
DEEPSEEK_MAX_AUDIO_BYTES = 8 * 1024 * 1024
DEEPSEEK_MAX_DURATION_SECONDS = 120


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
    provider: str  # "Mimo" | "Whisper" | "DeepSeekTools"
    timeout_seconds: float
    max_concurrency: int

    @staticmethod
    def from_env() -> "WorkerConfig":
        # Support both ASR_* (new) and MIMO_* (legacy) env var names.
        def env(new: str, legacy: str, default: str = "") -> str:
            return os.environ.get(new) or os.environ.get(legacy, default)

        provider = os.environ.get("ASR_PROVIDER", PROVIDER_MIMO)
        is_deepseek = provider == PROVIDER_DEEPSEEK_TOOLS
        return WorkerConfig(
            api_key=env("ASR_API_KEY", "MIMO_API_KEY"),
            # For DeepSeekTools this is the full endpoint, posted to verbatim.
            base_url=env("ASR_BASE_URL", "MIMO_BASE_URL",
                         DEFAULT_DEEPSEEK_ASR_URL if is_deepseek else DEFAULT_MIMO_BASE_URL),
            model=env("ASR_MODEL", "MIMO_ASR_MODEL", "" if is_deepseek else DEFAULT_MIMO_MODEL),
            provider=provider,
            timeout_seconds=float(env("ASR_TIMEOUT_SECONDS", "MIMO_TIMEOUT_SECONDS", "30")),
            max_concurrency=max(1, int(env("ASR_MAX_CONCURRENCY", "MIMO_MAX_CONCURRENCY", "2"))),
        )


class AsrBackend(ABC):
    """Abstract base for ASR provider-specific transcription logic."""

    @abstractmethod
    def transcribe(
        self,
        audio_base64: str,
        audio_format: str,
        language: str,
        client: Any,
        prompt: str | None = None,
    ) -> str:
        """Transcribe audio and return the recognized text."""


class MimoBackend(AsrBackend):
    """MiMo ASR: uses chat.completions with an inline base64 audio message."""

    _SUPPORTED_FORMATS = frozenset(("wav", "mp3"))
    _SUPPORTED_LANGUAGES = frozenset(("auto", "zh", "en"))
    _MAX_AUDIO_BYTES = 10 * 1024 * 1024

    def __init__(self, config: WorkerConfig) -> None:
        self._config = config

    def transcribe(
        self,
        audio_base64: str,
        audio_format: str,
        language: str,
        client: Any,
        prompt: str | None = None,
    ) -> str:
        del prompt  # MiMo-V2.5-ASR strictly supports single input_audio messages.
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

    _MAX_PROMPT_CHARS = 400

    def __init__(self, config: WorkerConfig) -> None:
        self._config = config

    def transcribe(
        self,
        audio_base64: str,
        audio_format: str,
        language: str,
        client: Any,
        prompt: str | None = None,
    ) -> str:
        audio_bytes = base64.b64decode(audio_base64)
        audio_file = io.BytesIO(audio_bytes)
        # The OpenAI library inspects the name attribute for MIME-type detection.
        audio_file.name = f"audio.{audio_format}"
        lang = language if language != "auto" else None
        kwargs: dict[str, Any] = {
            "model": self._config.model,
            "file": audio_file,
            "language": lang,
        }
        if prompt and prompt.strip():
            kwargs["prompt"] = prompt.strip()[-self._MAX_PROMPT_CHARS:]
        response = client.audio.transcriptions.create(**kwargs)
        return response.text if hasattr(response, "text") else str(response)


class DeepSeekToolsBackend(AsrBackend):
    """DeepSeek ASR: POST base_url (the full /v1/tools/asr endpoint) with a multipart WAV upload.

    The endpoint is not part of the OpenAI SDK surface, so requests go through
    httpx directly. The request carries no model or language parameter; the
    service detects the language itself. Limits: 8 MiB, 120 s, 16-bit PCM WAV
    or Ogg Opus.
    """

    _SUPPORTED_FORMATS = frozenset(("wav",))

    def __init__(self, config: WorkerConfig) -> None:
        self._config = config

    def transcribe(
        self,
        audio_base64: str,
        audio_format: str,
        language: str,
        client: Any,
        prompt: str | None = None,
    ) -> str:
        del prompt  # DeepSeek /v1/tools/asr accepts only multipart file upload.
        if audio_format not in self._SUPPORTED_FORMATS:
            raise ValueError("DeepSeek ASR 仅支持 WAV（16 位 PCM），请确认音频格式。")
        audio_bytes = base64.b64decode(audio_base64)
        if len(audio_bytes) > DEEPSEEK_MAX_AUDIO_BYTES:
            raise ValueError("音频超过 DeepSeek ASR 的 8 MiB 上限。")
        duration_seconds = wav_duration_seconds(audio_bytes)
        if duration_seconds is not None and duration_seconds > DEEPSEEK_MAX_DURATION_SECONDS:
            raise ValueError("音频超过 DeepSeek ASR 的 120 秒时长上限。")

        try:
            response = client.post(
                self._config.base_url,
                headers={"Authorization": f"Bearer {self._config.api_key}"},
                files={"file": (f"audio.{audio_format}", audio_bytes, "audio/wav")},
            )
        except httpx.TimeoutException as exc:
            raise WorkerError("DeepSeek ASR 请求超时", kind="timeout", retryable=True) from exc
        except httpx.RequestError as exc:
            raise WorkerError("DeepSeek ASR 网络错误", kind="network", retryable=True) from exc

        if response.status_code != 200:
            raise http_status_error(response.status_code, response.text)

        try:
            payload = response.json()
        except ValueError as exc:
            raise WorkerError("DeepSeek ASR 返回了无法解析的响应", kind="invalid_response", retryable=False) from exc
        text = payload.get("text") if isinstance(payload, dict) else None
        if not isinstance(text, str):
            raise WorkerError("DeepSeek ASR 响应缺少 text 字段", kind="invalid_response", retryable=False)
        return text


def wav_duration_seconds(audio_bytes: bytes) -> float | None:
    """Best-effort duration from a WAV header; None when the header is unreadable."""
    if len(audio_bytes) < 44 or audio_bytes[:4] != b"RIFF" or audio_bytes[8:12] != b"WAVE":
        return None
    try:
        with io.BytesIO(audio_bytes) as stream:
            import wave

            with wave.open(stream, "rb") as wav_file:
                frame_rate = wav_file.getframerate()
                if frame_rate <= 0:
                    return None
                return wav_file.getnframes() / frame_rate
    except (wave.Error, EOFError):
        return None


def http_status_error(status_code: int, body: str) -> WorkerError:
    kind, retryable = classify_error(RuntimeError("http"), status_code)
    detail = body.strip().replace("\n", " ")
    if len(detail) > 300:
        detail = detail[:300] + "..."
    message = f"DeepSeek ASR 返回 HTTP {status_code}" + (f": {detail}" if detail else "")
    return WorkerError(message, kind=kind, retryable=retryable)


def create_backend(config: WorkerConfig) -> AsrBackend:
    if config.provider == PROVIDER_MIMO:
        return MimoBackend(config)
    if config.provider == PROVIDER_DEEPSEEK_TOOLS:
        return DeepSeekToolsBackend(config)
    if config.provider == PROVIDER_WHISPER:
        return WhisperBackend(config)
    raise WorkerError(
        f"未知的 ASR provider: {config.provider}",
        kind="configuration",
        retryable=False,
    )


class AsrWorker:
    def __init__(self, config: WorkerConfig, client: Any | None = None) -> None:
        self._config = config
        self._client: Any | None = client
        self._client_lock = threading.Lock()
        self._backend: AsrBackend = create_backend(config)

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
        raw_prompt = request.get("prompt")
        prompt = raw_prompt.strip() if isinstance(raw_prompt, str) and raw_prompt.strip() else None

        if not audio_base64:
            raise ValueError("audioBase64 is required")
        self.health_check()

        text = self._backend.transcribe(
            audio_base64,
            audio_format,
            language,
            self._get_client(),
            prompt=prompt,
        )
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

    def _get_client(self) -> Any:
        if self._client is not None:
            return self._client

        with self._client_lock:
            if self._client is None:
                self._client = self._create_client()

        return self._client

    def _create_client(self) -> Any:
        if self._config.provider == PROVIDER_DEEPSEEK_TOOLS:
            # The host owns the single-retry policy, so no transport-level retries.
            return httpx.Client(timeout=self._config.timeout_seconds)

        return OpenAI(
            api_key=self._config.api_key,
            base_url=self._config.base_url,
            timeout=self._config.timeout_seconds,
            # The host owns the single-retry policy; SDK retries would
            # stack on top of it and stall the realtime pipeline.
            max_retries=0,
        )


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
        # Malformed input and protocol mistakes are answered on stdout; a
        # traceback there would only bury the structured error response.
        if isinstance(exc, (WorkerError, ValueError, json.JSONDecodeError)):
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