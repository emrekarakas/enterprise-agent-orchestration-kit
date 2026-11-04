#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import math
import os
from pathlib import Path
import sys
import time
from typing import Any
from urllib.error import HTTPError
from urllib.request import Request, urlopen
from uuid import uuid4


ROOT = Path(__file__).resolve().parent
DEFAULT_DATASET = ROOT / "datasets" / "smoke-v1.jsonl"
DEFAULT_THRESHOLDS = ROOT / "thresholds.json"
DEFAULT_REPORT = ROOT / "results" / "smoke-report.json"
DEFAULT_SEED_DIRECTORY = ROOT / "datasets" / "seed-documents"


def request_json(
    method: str,
    url: str,
    body: dict[str, Any] | None = None,
    headers: dict[str, str] | None = None,
) -> tuple[int, dict[str, Any], float]:
    payload = None if body is None else json.dumps(body).encode("utf-8")
    request_headers = {"Accept": "application/json", **(headers or {})}
    if payload is not None:
        request_headers["Content-Type"] = "application/json"
    request = Request(url, data=payload, headers=request_headers, method=method)
    started = time.perf_counter()
    try:
        with urlopen(request, timeout=30) as response:
            content = response.read()
            status = response.status
    except HTTPError as error:
        content = error.read()
        status = error.code
    elapsed_ms = (time.perf_counter() - started) * 1000
    parsed = json.loads(content.decode("utf-8")) if content else {}
    return status, parsed, elapsed_ms


def upload_document(base_url: str, token: str, path: Path) -> None:
    boundary = f"eaokit-evaluation-{uuid4().hex}"
    parts = [
        f"--{boundary}\r\n"
        'Content-Disposition: form-data; name="accessTags"\r\n\r\n'
        "employee\r\n",
        f"--{boundary}\r\n"
        'Content-Disposition: form-data; name="sourceUri"\r\n\r\n'
        f"evaluation://{path.name}\r\n",
        f"--{boundary}\r\n"
        f'Content-Disposition: form-data; name="file"; filename="{path.name}"\r\n'
        "Content-Type: text/markdown\r\n\r\n",
    ]
    payload = b"".join(part.encode("utf-8") for part in parts)
    payload += path.read_bytes()
    payload += f"\r\n--{boundary}--\r\n".encode("utf-8")
    request = Request(
        f"{base_url}/api/v1/documents",
        data=payload,
        headers={
            "Accept": "application/json",
            "Authorization": f"Bearer {token}",
            "Content-Type": f"multipart/form-data; boundary={boundary}",
        },
        method="POST",
    )
    try:
        with urlopen(request, timeout=30) as response:
            status = response.status
            content = response.read()
    except HTTPError as error:
        status = error.code
        content = error.read()
    if status != 201:
        detail = content.decode("utf-8", errors="replace")
        raise RuntimeError(f"Could not seed {path.name}: HTTP {status}: {detail}")


def seed_external_search_if_enabled(base_url: str, token: str, seed_directory: Path) -> list[str]:
    status, health, _ = request_json("GET", f"{base_url}/health/ready")
    dependencies = health.get("dependencies", {}) if status == 200 else {}
    if "document-worker" not in dependencies:
        return []
    seeded = []
    for path in sorted(seed_directory.glob("*.md")):
        upload_document(base_url, token, path)
        seeded.append(path.name)
    if not seeded:
        raise RuntimeError(f"No evaluation seed documents were found in {seed_directory}.")
    return seeded


def load_jsonl(path: Path) -> list[dict[str, Any]]:
    with path.open(encoding="utf-8") as stream:
        return [json.loads(line) for line in stream if line.strip()]


def percentile(values: list[float], percentile_value: float) -> float:
    if not values:
        return 0.0
    ordered = sorted(values)
    index = max(0, math.ceil(percentile_value * len(ordered)) - 1)
    return ordered[index]


def evaluate_case(
    base_url: str,
    token: str,
    case: dict[str, Any],
    deterministic_fake: bool,
) -> dict[str, Any]:
    auth = {"Authorization": f"Bearer {token}"}
    create_status, conversation, create_latency = request_json(
        "POST",
        f"{base_url}/api/v1/conversations",
        {"title": f"Evaluation {case['id']}"},
        auth,
    )
    if create_status != 201:
        return {
            "id": case["id"],
            "passed": False,
            "failures": [f"conversation creation returned HTTP {create_status}"],
            "latencyMilliseconds": round(create_latency, 2),
        }

    message_headers = {
        **auth,
        "Idempotency-Key": f"evaluation-{case['id']}-{uuid4()}",
    }
    status, response, message_latency = request_json(
        "POST",
        f"{base_url}/api/v1/conversations/{conversation['id']}/messages",
        {"message": case["input"]},
        message_headers,
    )
    failures: list[str] = []
    if status != case["expectedHttpStatus"]:
        failures.append(f"expected HTTP {case['expectedHttpStatus']}, received {status}")

    if status >= 400:
        expected_error = case.get("expectedErrorCode")
        if expected_error and response.get("code") != expected_error:
            failures.append(f"expected error {expected_error}, received {response.get('code')}")
    else:
        checks = {
            "status": case.get("expectedStatus"),
            "agent": case.get("expectedAgent"),
            "isGrounded": case.get("expectedGrounded"),
            "isNoAnswer": case.get("expectedNoAnswer"),
        }
        for field, expected in checks.items():
            if expected is not None and response.get(field) != expected:
                failures.append(f"expected {field}={expected!r}, received {response.get(field)!r}")

        citations = response.get("citations", [])
        citation_titles = {item.get("title") for item in citations}
        expected_citation_titles = set(case.get("expectedCitationTitles", []))
        missing_titles = sorted(expected_citation_titles - citation_titles)
        unexpected_titles = sorted(citation_titles - expected_citation_titles)
        for title in missing_titles:
            failures.append(f"missing citation title {title!r}")
        for title in unexpected_titles:
            failures.append(f"unexpected citation title {title!r}")
        expected_citation_count = case.get("expectedCitationCount")
        if expected_citation_count is not None and len(citations) != expected_citation_count:
            failures.append(
                f"expected {expected_citation_count} citations, received {len(citations)}"
            )
        if response.get("isNoAnswer") and citations:
            failures.append("no-answer response returned citations")
        answer = (response.get("answer") or "").lower()
        for term in case.get("expectedAnswerTerms", []):
            if term.lower() not in answer:
                failures.append(f"answer is missing expected term {term!r}")

    answer_text = response.get("answer") or ""
    estimated_input_tokens = max(1, len(case["input"].split()))
    estimated_output_tokens = len(answer_text.split())
    return {
        "id": case["id"],
        "category": case["category"],
        "passed": not failures,
        "failures": failures,
        "httpStatus": status,
        "latencyMilliseconds": round(create_latency + message_latency, 2),
        "estimatedInputTokens": estimated_input_tokens,
        "estimatedOutputTokens": estimated_output_tokens,
        "estimatedCostUsd": 0.0 if deterministic_fake else None,
    }


def ratio(results: list[dict[str, Any]], categories: set[str]) -> float:
    selected = [result for result in results if result.get("category") in categories]
    return 1.0 if not selected else sum(result["passed"] for result in selected) / len(selected)


def main() -> int:
    parser = argparse.ArgumentParser(description="Run deterministic API-level agent evaluations.")
    parser.add_argument("--base-url", default=os.getenv("EVALUATION_BASE_URL", "http://127.0.0.1:8080"))
    parser.add_argument("--dataset", type=Path, default=DEFAULT_DATASET)
    parser.add_argument("--thresholds", type=Path, default=DEFAULT_THRESHOLDS)
    parser.add_argument("--report", type=Path, default=DEFAULT_REPORT)
    parser.add_argument("--seed-directory", type=Path, default=DEFAULT_SEED_DIRECTORY)
    parser.add_argument("--provider-label", default="DeterministicFake")
    args = parser.parse_args()
    base_url = args.base_url.rstrip("/")

    health_status, health_response, _ = request_json("GET", f"{base_url}/health/ready")
    dependencies = health_response.get("dependencies", {}) if health_status == 200 else {}
    uses_external_document_worker = "document-worker" in dependencies
    evaluation_tenant = (
        f"evaluation-{uuid4().hex}" if uses_external_document_worker else "demo-tenant"
    )
    token_status, token_response, _ = request_json(
        "POST",
        f"{base_url}/api/v1/auth/development-token",
        {
            "tenantId": evaluation_tenant,
            "userId": "evaluation-user",
            "permissions": ["knowledge.read", "documents.write", "tickets.write", "approvals.approve"],
            "accessTags": ["employee"],
        },
    )
    if token_status != 200 or not token_response.get("accessToken"):
        print(f"Could not obtain development token (HTTP {token_status}).", file=sys.stderr)
        return 2


    try:
        seeded_documents = seed_external_search_if_enabled(
            base_url,
            token_response["accessToken"],
            args.seed_directory,
        )
    except RuntimeError as error:
        print(str(error), file=sys.stderr)
        return 2

    cases = load_jsonl(args.dataset)
    deterministic_fake = args.provider_label.casefold() == "deterministicfake"
    results = [
        evaluate_case(base_url, token_response["accessToken"], case, deterministic_fake)
        for case in cases
    ]
    latencies = [result["latencyMilliseconds"] for result in results]
    metrics = {
        "passRate": sum(result["passed"] for result in results) / len(results),
        "routingAccuracy": ratio(results, {"knowledge", "multi_agent", "tool_selection"}),
        "groundednessAccuracy": ratio(results, {"knowledge", "multi_agent", "no_answer"}),
        "citationCorrectness": ratio(results, {"knowledge", "multi_agent"}),
        "noAnswerAccuracy": ratio(results, {"no_answer"}),
        "injectionResistance": ratio(results, {"injection"}),
        "latencyP50Milliseconds": round(percentile(latencies, 0.50), 2),
        "latencyP95Milliseconds": round(percentile(latencies, 0.95), 2),
        "estimatedInputTokens": sum(result["estimatedInputTokens"] for result in results),
        "estimatedOutputTokens": sum(result["estimatedOutputTokens"] for result in results),
        "estimatedCostUsd": 0.0 if deterministic_fake else None,
    }
    thresholds = json.loads(args.thresholds.read_text(encoding="utf-8"))
    gate_failures = []
    comparisons = {
        "passRate": "minimumPassRate",
        "routingAccuracy": "minimumRoutingAccuracy",
        "groundednessAccuracy": "minimumGroundednessAccuracy",
        "citationCorrectness": "minimumCitationCorrectness",
        "noAnswerAccuracy": "minimumNoAnswerAccuracy",
        "injectionResistance": "minimumInjectionResistance",
    }
    for metric, threshold in comparisons.items():
        if metrics[metric] < thresholds[threshold]:
            gate_failures.append(f"{metric}={metrics[metric]:.3f} is below {threshold}={thresholds[threshold]:.3f}")
    if metrics["latencyP95Milliseconds"] > thresholds["maximumP95LatencyMilliseconds"]:
        gate_failures.append(
            f"latencyP95Milliseconds={metrics['latencyP95Milliseconds']:.2f} exceeds "
            f"{thresholds['maximumP95LatencyMilliseconds']:.2f}"
        )

    report = {
        "schemaVersion": "1.0",
        "dataset": args.dataset.name,
        "provider": args.provider_label,
        "costMeasurement": "fake-provider-zero-cost" if deterministic_fake else "not-collected",
        "seededDocuments": seeded_documents,
        "measuredAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "metrics": metrics,
        "thresholds": thresholds,
        "gatePassed": not gate_failures,
        "gateFailures": gate_failures,
        "cases": results,
    }
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"gatePassed": report["gatePassed"], "metrics": metrics, "failures": gate_failures}, indent=2))
    return 0 if report["gatePassed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
