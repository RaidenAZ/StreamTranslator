from __future__ import annotations

import json
import os
import sys
import time
import traceback
from dataclasses import dataclass
from typing import Any

from openai import OpenAI


DEFAULT_BASE_URL = "https://api.xiaomimimo.com/v1"
DEFAULT_MODEL = "mimo-v2.5-asr"


@dataclass(frozen=True)
class WorkerConfig:
    api_key: str
    base_url: str
    model: str
    timeout_seconds: float

    @staticmethod
    def from_env() -> "WorkerConfig":
        return WorkerConfig(
            api_key=os.environ.get("MIMO_API_KEY", ""),
            base_url=os.environ.get("MIMO_BASE_URL", DEFAULT_BASE_URL),
            model=os.environ.get("MIMO_ASR_MODEL", DEFAULT_MODEL),
            timeout_seconds=float(os.environ.get("MIMO_TIMEOUT_SECONDS", "30")),
        )


class AsrWorker:
    def __init__(self, config: WorkerConfig) -> None:
        self._config = config
        self._client: OpenAI | None = None

    def transcribe(self, request: dict[str, Any]) -> dict[str, Any]:
        started = time.perf_counter()
        audio_base64 = request.get("audioBase64") or request.get("audio_base64")
        audio_format = request.get("audioFormat") or request.get("audio_format") or "wav"
        language = request.get("language") or "auto"

        if not audio_base64:
            raise ValueError("audioBase64 is required")
        if not self._config.api_key:
            raise ValueError("MIMO_API_KEY is required")

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
    return {
        "id": request.get("id", ""),
        "type": "error",
        "ok": False,
        "sequence": request.get("sequence"),
        "errorCode": exc.__class__.__name__,
        "errorMessage": str(exc),
    }


def write_json(payload: dict[str, Any]) -> None:
    print(json.dumps(payload, ensure_ascii=False, separators=(",", ":")), flush=True)


def main() -> int:
    config = WorkerConfig.from_env()
    worker = AsrWorker(config)
    write_json({"id": "startup", "type": "ready", "ok": True})

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue

        request: dict[str, Any] = {}
        try:
            request = json.loads(line)
            message_type = request.get("type")
            request_id = request.get("id", "")

            if message_type == "ping":
                write_json(ok_response(request_id, "ready"))
            elif message_type == "shutdown":
                write_json(ok_response(request_id, "shutdown"))
                return 0
            elif message_type == "transcribe":
                write_json(worker.transcribe(request))
            else:
                raise ValueError(f"unknown request type: {message_type}")
        except Exception as exc:  # noqa: BLE001 - worker must keep running after request errors.
            traceback.print_exc(file=sys.stderr)
            write_json(error_response(request, exc))

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
