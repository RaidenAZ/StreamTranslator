from __future__ import annotations

import argparse
import json
import os
import statistics
import sys
from datetime import datetime
from pathlib import Path
from types import SimpleNamespace
from typing import Any

from translation_worker import PROMPT_VERSION, TranslationWorker


API_KEY_ENVIRONMENT_VARIABLE = "STREAMTRANSLATOR_TRANSLATION_API_KEY"


class FakeCompletions:
    def create(self, **kwargs: Any) -> Any:
        user_payload = json.loads(kwargs["messages"][1]["content"])
        source = user_payload["currentSubtitle"]
        target = user_payload["targetLanguage"]["code"]
        content = f"[fake:{target}] {source}"
        return SimpleNamespace(
            choices=[SimpleNamespace(message=SimpleNamespace(content=content, reasoning_content=None))],
            usage=SimpleNamespace(prompt_tokens=1, completion_tokens=1, total_tokens=2),
        )


class FakeClient:
    def __init__(self, **_: Any) -> None:
        self.chat = SimpleNamespace(completions=FakeCompletions())


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Evaluate the StreamTranslator translation worker.")
    parser.add_argument("--samples", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--settings")
    parser.add_argument("--profile-id")
    parser.add_argument("--allow-live-api", action="store_true")
    return parser.parse_args()


def load_samples(path: Path) -> list[dict[str, Any]]:
    samples: list[dict[str, Any]] = []
    with path.open("r", encoding="utf-8") as stream:
        for line_number, line in enumerate(stream, 1):
            line = line.strip()
            if not line:
                continue
            try:
                sample = json.loads(line)
            except json.JSONDecodeError as exc:
                raise ValueError(f"Invalid sample JSON at line {line_number}: {exc}") from exc
            for field in ("id", "sourceLanguage", "sourceText", "targetLanguage"):
                if not sample.get(field):
                    raise ValueError(f"Sample line {line_number} is missing {field}")
            samples.append(sample)
    if not samples:
        raise ValueError("The sample set is empty")
    return samples


def load_live_profile(settings_path: Path, profile_id: str | None) -> dict[str, Any]:
    settings = json.loads(settings_path.read_text(encoding="utf-8-sig"))
    translation = settings.get("translation") or {}
    selected_id = profile_id or translation.get("activeProfileId")
    profile = next(
        (item for item in translation.get("profiles", []) if item.get("id") == selected_id),
        None,
    )
    if profile is None:
        raise ValueError("The requested translation profile was not found in settings.json")
    api_key = os.environ.get(API_KEY_ENVIRONMENT_VARIABLE)
    if not api_key:
        raise ValueError(
            f"Set {API_KEY_ENVIRONMENT_VARIABLE} in the current process before using --allow-live-api"
        )
    return {
        "profileId": profile["id"],
        "baseUrl": profile["baseUrl"],
        "model": profile["model"],
        "apiKey": api_key,
        "requestCompatibility": profile.get("requestCompatibility", "Standard"),
        "customExtraBody": profile.get("customExtraBody", {}),
        "timeoutMs": profile.get("timeoutMs", 10000),
        "maxConcurrency": profile.get("maxConcurrency", 2),
    }


def fake_profile() -> dict[str, Any]:
    return {
        "profileId": "offline-fake",
        "baseUrl": "http://127.0.0.1:1/v1",
        "model": "offline-fake",
        "apiKey": "",
        "requestCompatibility": "Standard",
        "customExtraBody": {},
        "timeoutMs": 10000,
        "maxConcurrency": 1,
    }


def percentile(values: list[int], fraction: float) -> int | None:
    if not values:
        return None
    ordered = sorted(values)
    index = max(0, min(len(ordered) - 1, int((len(ordered) * fraction) + 0.999999) - 1))
    return ordered[index]


def write_jsonl(path: Path, values: list[dict[str, Any]]) -> None:
    with path.open("w", encoding="utf-8", newline="\n") as stream:
        for value in values:
            stream.write(json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n")


def main() -> int:
    args = parse_args()
    samples_path = Path(args.samples).resolve()
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    samples = load_samples(samples_path)

    if args.allow_live_api:
        if not args.settings:
            raise ValueError("--settings is required with --allow-live-api")
        profile = load_live_profile(Path(args.settings).resolve(), args.profile_id)
        worker = TranslationWorker()
        mode = "live"
    else:
        profile = fake_profile()
        worker = TranslationWorker(client_factory=FakeClient)
        mode = "fake"

    configured = worker.configure(
        {
            "id": "evaluation-config",
            "type": "configure",
            "profile": profile,
            "promptVersion": PROMPT_VERSION,
        }
    )
    requests: list[dict[str, Any]] = []
    results: list[dict[str, Any]] = []
    latencies: list[int] = []
    warning_count = 0
    failures = 0
    revisions = 0
    latest_revision: dict[str, int] = {}

    for sequence, sample in enumerate(samples, 1):
        group_id = sample.get("utteranceGroupId") or f"evaluation:{sample['id']}"
        revision = int(sample.get("sourceRevision", 1))
        if group_id in latest_revision and revision > latest_revision[group_id]:
            revisions += 1
        latest_revision[group_id] = max(revision, latest_revision.get(group_id, 0))
        request = {
            "id": f"eval-{sample['id']}",
            "type": "translate",
            "sequence": sequence,
            "utteranceGroupId": group_id,
            "sourceRevision": revision,
            "sourceLanguage": sample["sourceLanguage"],
            "targetLanguage": sample["targetLanguage"],
            "sourceText": sample["sourceText"],
            "context": [],
            "createdAt": datetime.now().astimezone().isoformat(),
            "isConnectionTest": False,
        }
        requests.append({key: value for key, value in request.items() if key != "type"})
        try:
            response = worker.translate(request)
            latency = int(response.get("latencyMs") or 0)
            latencies.append(latency)
            warnings = response.get("warningCodes") or []
            warning_count += len(warnings)
            results.append(
                {
                    "id": sample["id"],
                    "ok": True,
                    "sourceLanguage": sample["sourceLanguage"],
                    "targetLanguage": sample["targetLanguage"],
                    "sourceText": sample["sourceText"],
                    "translatedText": response["translatedText"],
                    "latencyMs": latency,
                    "warningCodes": warnings,
                    "usage": response.get("usage"),
                }
            )
        except Exception as exc:  # noqa: BLE001 - evaluation continues after individual failures.
            failures += 1
            results.append(
                {
                    "id": sample["id"],
                    "ok": False,
                    "sourceLanguage": sample["sourceLanguage"],
                    "targetLanguage": sample["targetLanguage"],
                    "sourceText": sample["sourceText"],
                    "errorKind": getattr(exc, "kind", "unknown"),
                    "errorMessage": str(exc)[:512],
                }
            )

    metrics = {
        "mode": mode,
        "sampleSet": samples_path.name,
        "sampleCount": len(samples),
        "successCount": len(samples) - failures,
        "failureCount": failures,
        "failureRate": failures / len(samples),
        "warningCount": warning_count,
        "warningRate": warning_count / len(samples),
        "revisionRetranslations": revisions,
        "latencyP50Ms": percentile(latencies, 0.50),
        "latencyP95Ms": percentile(latencies, 0.95),
        "latencyMeanMs": round(statistics.mean(latencies), 2) if latencies else None,
        "profileId": profile["profileId"],
        "model": profile["model"],
        "finalEndpoint": configured["finalEndpoint"],
        "promptVersion": PROMPT_VERSION,
    }
    write_jsonl(output / "requests.jsonl", requests)
    write_jsonl(output / "results.jsonl", results)
    (output / "metrics.json").write_text(
        json.dumps(metrics, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    report = f"""# Translation Evaluation

- Mode: {mode}
- Samples: {len(samples)}
- Failures: {failures} ({metrics['failureRate']:.2%})
- Warnings: {warning_count} ({metrics['warningRate']:.2%})
- Latency P50/P95: {metrics['latencyP50Ms']} / {metrics['latencyP95Ms']} ms
- Revision retranslations: {revisions}
- Prompt: {PROMPT_VERSION}
- Model: {profile['model']}

Semantic accuracy, factual preservation, and language quality require human annotation of `results.jsonl`.
"""
    (output / "report.md").write_text(report, encoding="utf-8", newline="\n")
    print(json.dumps(metrics, ensure_ascii=False))
    return 1 if failures / len(samples) >= 0.05 else 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:  # noqa: BLE001 - the wrapper needs one concise terminal failure.
        print(f"translation evaluation failed: {exc}", file=sys.stderr)
        raise SystemExit(2)
