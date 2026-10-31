"""Qualify and audit each active release line using its own protected source.

The coordinator only dispatches branch-native workflows. It never restores a
maintenance branch inside the default branch's SDK or copies its check verdict.
All remote writes are the explicitly requested workflow dispatches; policy and
producer discovery remain read-only and exact-commit bound.
"""

from __future__ import annotations

import argparse
import base64
import concurrent.futures
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
import threading
import time
import xml.etree.ElementTree as ET
from pathlib import Path
from urllib.parse import quote


if not __package__:
    sys.path.insert(0, str(Path(__file__).resolve().parents[2]))


MAINTENANCE_WORKFLOW = "release-line-maintenance.yml"
CORRELATION = re.compile(r"schedule-[1-9][0-9]{0,17}-[1-9][0-9]{0,8}\Z")
SHA = re.compile(r"[0-9a-f]{40}\Z")
MAX_JSON_BYTES = 16 * 1024 * 1024
FAILURE_CODES = frozenset(("maintenance_failed", "github_http_error", "github_request_failed",
                           "github_invalid_response", "github_response_limit", "child_identity_mismatch",
                           "child_visibility_timeout", "child_wait_timeout", "child_result_timeout", "child_failed",
                           "child_state_invalid", "producer_evidence_invalid", "producer_unsuccessful"))
IDENTITY_FIELDS = frozenset(("id", "event", "head_sha", "head_branch", "path", "display_title"))
QUALIFICATION_JOBS = frozenset(("package-and-core", "coverage", "mysql-engine",
                                "postgresql-engine", "sqlite-engine", "sqlserver-engine", "required"))


class MaintenanceError(ValueError):
    """An exact native producer, complete audit or active-line contract is unproven."""

    def __init__(self, message: str, *, code: str = "maintenance_failed",
                 http_status: int | None = None, identity_field: str | None = None):
        """Keep report diagnostics bounded independently of an exception's private message."""
        super().__init__(message)
        self.code = code if code in FAILURE_CODES else "maintenance_failed"
        self.http_status = http_status if type(http_status) is int and 100 <= http_status <= 599 else None
        self.identity_field = identity_field if identity_field in IDENTITY_FIELDS else None


def failure_observation(error: Exception) -> dict:
    """Retain only authored categories, never an API body, command output or token."""
    result = {"verificationStatus": "failed", "failureCode": "maintenance_failed"}
    if isinstance(error, MaintenanceError):
        result["failureCode"] = error.code
        if error.http_status is not None:
            result["httpStatus"] = error.http_status

        if error.identity_field is not None:
            result["identityField"] = error.identity_field

    elif isinstance(error, subprocess.SubprocessError):
        result["failureCode"] = "maintenance_process_failed"
    elif isinstance(error, OSError):
        result["failureCode"] = "maintenance_io_failed"

    return result


def positive_number(value: object) -> int:
    """Keep API identifiers distinct from booleans and unbounded input."""
    if isinstance(value, bool) or not re.fullmatch(r"[1-9][0-9]{0,17}", str(value)):
        raise MaintenanceError("Invalid bounded GitHub identity.")

    return int(value)


def unique_object(pairs: list[tuple[str, object]]) -> dict:
    """Reject duplicate JSON fields at report and API boundaries."""
    result = {}
    for key, value in pairs:
        if key in result:
            raise MaintenanceError("Duplicate JSON property.")

        result[key] = value

    return result


def read_json(path: Path) -> dict:
    """Accept one bounded regular object without following a report symlink."""
    if path.is_symlink() or not path.is_file() or path.stat().st_size > MAX_JSON_BYTES:
        raise MaintenanceError("Expected a bounded regular JSON report.")

    result = json.loads(path.read_bytes(), object_pairs_hook=unique_object)
    if not isinstance(result, dict):
        raise MaintenanceError("Expected a JSON object.")

    return result


class GitHub:
    """Use the existing job token through the official CLI, never command arguments."""

    def request(self, route: str, method: str = "GET", payload: dict | None = None):
        """Request bounded JSON while keeping failed API response bodies out of logs."""
        command = ["gh", "api", "--include", "--hostname", "github.com", "--method", method,
                   "-H", "Accept: application/vnd.github+json",
                   "-H", "X-GitHub-Api-Version: 2026-03-10", route]
        if payload is not None:
            command.extend(["--input", "-"])

        completed = subprocess.run(command, input=json.dumps(payload) if payload is not None else None,
                                   capture_output=True, text=True, timeout=60, check=False)
        if len(completed.stdout) > MAX_JSON_BYTES + 65536:
            raise MaintenanceError("GitHub response exceeded its bound.", code="github_response_limit")

        # WHY: gh exposes response status through --include. Stderr and JSON
        # messages are untrusted payloads, not a reliable or safe status parser.
        headers, separator, body = completed.stdout.partition("\r\n\r\n")
        if not separator:
            headers, separator, body = completed.stdout.partition("\n\n")

        first = headers.splitlines()[0] if headers else ""
        status = re.fullmatch(r"HTTP/[0-9.]+ ([1-5][0-9]{2})(?: [^\r\n]*)?", first)
        if not separator or len(headers) > 65536 or status is None:
            code = "github_request_failed" if completed.returncode else "github_invalid_response"
            raise MaintenanceError("GitHub did not return a bounded HTTP response.", code=code)

        http_status = int(status[1])
        if not 200 <= http_status <= 299:
            raise MaintenanceError("GitHub request returned an unsuccessful HTTP status.",
                                   code="github_http_error", http_status=http_status)

        if completed.returncode:
            raise MaintenanceError("GitHub request did not complete successfully.", code="github_request_failed")

        if len(body) > MAX_JSON_BYTES:
            raise MaintenanceError("GitHub JSON exceeded its bound.", code="github_response_limit")

        if http_status == 204 and not body.strip():
            return None

        try:
            return json.loads(body, object_pairs_hook=unique_object)
        except ValueError as error:
            raise MaintenanceError("GitHub response JSON is invalid.", code="github_invalid_response") from error


def active_lines(policy: dict) -> list[dict]:
    """Include the default active line exactly once and exclude inactive/prepared lines."""
    from eng.release.lines import validate_policy

    checked = validate_policy(policy)

    return [line for line in checked["lines"] if line["state"] == "active"]


def validate_child(policy: dict | None, workflow: str, environment: dict) -> bool:
    """Gate maintenance intent, leaving ordinary feature/manual CI unrestricted."""
    requested = environment.get("INPUT_MAINTENANCE", "false")
    if requested == "false":
        return False

    if requested != "true" or policy is None:
        raise MaintenanceError("Maintenance requires a boolean input and current policy.")

    repository = policy["repository"]
    ref = environment.get("GITHUB_REF")
    commit = environment.get("INPUT_SOURCE_SHA", "")
    correlation = environment.get("INPUT_CORRELATION", "")
    if (workflow not in ("ci.yml", MAINTENANCE_WORKFLOW)
            or environment.get("GITHUB_EVENT_NAME") != "workflow_dispatch"
            or environment.get("GITHUB_REPOSITORY") != repository
            or not SHA.fullmatch(commit) or environment.get("GITHUB_SHA") != commit
            or not CORRELATION.fullmatch(correlation)
            or environment.get("GITHUB_WORKFLOW_REF") != f"{repository}/.github/workflows/{workflow}@{ref}"
            or ref not in {line["sourceRef"] for line in active_lines(policy)}):
        raise MaintenanceError("Maintenance intent does not match its exact active workflow/source.")

    return True


def source_file(api, repository: str, path: str, commit: str) -> str:
    """Authenticate small branch-owned workflow source through its Git blob identity."""
    response = api.request(f"repos/{repository}/contents/{path}?ref={commit}")
    if (not isinstance(response, dict) or response.get("type") != "file"
            or response.get("path") != path or response.get("encoding") != "base64"
            or not isinstance(response.get("content"), str) or len(response["content"]) > 2 * 1024 * 1024):
        raise MaintenanceError("Branch-native workflow source is unavailable or malformed.")

    try:
        raw = base64.b64decode("".join(response["content"].split()), validate=True)
        text = raw.decode("ascii")
    except (ValueError, UnicodeError) as error:
        raise MaintenanceError("Branch-native source encoding is invalid.") from error

    blob = hashlib.sha1(b"blob " + str(len(raw)).encode("ascii") + b"\0" + raw).hexdigest()
    if response.get("sha") != blob:
        raise MaintenanceError("Branch-native source differs from its Git blob.")

    return text


def _scalar(value: str) -> str:
    """Read the retained plain/quoted matrix scalar, rejecting YAML control syntax."""
    text = value.strip()
    if len(text) >= 2 and text[0] in "\"'" and text[-1] == text[0]:
        text = text[1:-1]

    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9 .,+-]*", text):
        raise MaintenanceError("Unexpected branch-native matrix scalar shape.")

    return text


def _yaml_content(line: str) -> str:
    """Remove only ordinary YAML comments while retaining quoted literal content."""
    quote_character = None
    escaped = False
    for index, character in enumerate(line):
        if quote_character is not None:
            if character == quote_character and not escaped:
                quote_character = None

            escaped = character == "\\" and quote_character == '"' and not escaped
        elif character in "\"'":
            quote_character = character
        elif character == "#" and (index == 0 or line[index - 1].isspace()):
            return line[:index].rstrip()

    if quote_character is not None:
        raise MaintenanceError("Unterminated quoted matrix literal.")

    return line.rstrip()


def _mapping_block(source: str, indentation: int, key: str) -> str:
    """Select one complete block mapping; duplicate or inline alternatives fail closed."""
    rows = source.splitlines()
    prefix = " " * indentation + key + ":"
    matches = [index for index, line in enumerate(rows) if line.startswith(prefix)]
    if len(matches) != 1 or _yaml_content(rows[matches[0]]) != prefix:
        raise MaintenanceError(f"Expected one literal block mapping for {key}.")

    start = matches[0] + 1
    end = start
    while end < len(rows):
        line = rows[end]
        if line.strip() and not line.lstrip().startswith("#"):
            width = len(line) - len(line.lstrip(" "))
            if width <= indentation:
                break

        end += 1

    return "\n".join(rows[start:end])


def _job_sections(source: str) -> dict[str, str]:
    """Retain every declared job instead of overwriting or regex-filtering its identity."""
    rows = _mapping_block(source, 0, "jobs").splitlines()
    starts = []
    names = set()
    for index, line in enumerate(rows):
        if not line.strip() or line.lstrip().startswith("#") or not re.match(r"^  \S", line):
            continue

        match = re.fullmatch(r"  ([A-Za-z_][A-Za-z0-9_-]*):", _yaml_content(line))
        if match is None or match[1] in names:
            raise MaintenanceError("Qualification job identity is malformed or duplicated.")

        names.add(match[1])
        starts.append((index, match[1]))

    if names != QUALIFICATION_JOBS:
        raise MaintenanceError("Branch-native full qualification job inventory changed.")

    return {name: "\n".join(rows[start + 1:starts[index + 1][0] if index + 1 < len(starts) else len(rows)])
            for index, (start, name) in enumerate(starts)}


def _matrix_literal(value: str) -> str:
    """Accept plain or simply quoted literals, never YAML aliases or computed values."""
    value = value.strip()
    if not value:
        raise MaintenanceError("Matrix mapping values cannot be empty.")

    if value[0] in "\"'":
        if len(value) < 2 or value[-1] != value[0] or value[0] in value[1:-1] or "\\" in value[1:-1]:
            raise MaintenanceError("Matrix literal requires a simple balanced quote pair.")

        value = value[1:-1]
    elif value[0] in "&*!|>[{":
        raise MaintenanceError("Matrix aliases, flow values and expressions require explicit reader support.")
    elif value.casefold() in ("null", "true", "false") or value == "~":
        raise MaintenanceError("Matrix identity fields require literal values, not YAML nulls or booleans.")

    if not value:
        raise MaintenanceError("Matrix mapping values cannot be empty.")

    return value


def _matrix_rows(body: str, fields: frozenset[str]) -> list[dict[str, str]]:
    """Consume every include entry as a complete, key-order-independent literal mapping.

    WHY: Searching only for recognizable rows can silently omit one required
    cell. Any unconsumed row/key or unsupported composition invalidates the
    whole observation rather than shrinking the expected producer inventory.
    """
    strategy = _mapping_block(body, 4, "strategy")
    matrix = _mapping_block(strategy, 6, "matrix")
    content = [_yaml_content(line) for line in matrix.splitlines()]
    content = [line for line in content if line.strip()]
    if not content or content[0] != "        include:":
        raise MaintenanceError("Native matrices require one explicit include-only sequence.")

    rows, current = [], None
    for line in content[1:]:
        item = re.fullmatch(r"          -(?: (.*))?", line)
        if item:
            if current is not None:
                rows.append(current)

            current = {}
            field = item[1]
            if field is None:
                continue
        else:
            field = line[12:] if line.startswith("            ") else None

        match = re.fullmatch(r"([a-z][a-z0-9-]*):(?: +(.*))?", field or "")
        if current is None or match is None or match[1] not in fields or match[1] in current:
            raise MaintenanceError("Matrix row has an unsupported, unknown or duplicate field.")

        current[match[1]] = _matrix_literal(match[2] or "")

    if current is not None:
        rows.append(current)

    if not rows or any(set(row) != fields for row in rows):
        raise MaintenanceError("Every declared native matrix row must be complete.")

    for row in rows:
        if (re.fullmatch(r"[0-9]+(?:\.[0-9]+){0,3}", row["version"]) is None
                or re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._/@:+-]*", row["image"]) is None
                or ("engine" in fields and row["engine"] not in ("mysql", "mariadb"))):
            raise MaintenanceError("Native matrix engine/version/image identity is not a supported literal.")

    return rows


def qualification_producers(source: str) -> list[str]:
    """Derive every real reusable-workflow job and engine cell from its native source.

    WHY: A separate list of server patches would drift from the qualified
    branch. This intentionally bounded reader fails closed if the maintained
    workflow changes to another YAML shape; it does not interpret general YAML.
    """
    sections = _job_sections(source)

    names = []
    for job, body in sections.items():
        declared = re.findall(r"^    name: ([^\n]+)$", body, re.MULTILINE)
        if len(declared) != 1:
            raise MaintenanceError("Qualification job has no unique display name.")

        if job == "mysql-engine":
            if declared[0] != "${{ matrix.engine }} ${{ matrix.version }}":
                raise MaintenanceError("MySQL qualification display shape changed.")

            cells = _matrix_rows(body, frozenset(("engine", "version", "image")))
            expanded = [f"{row['engine']} {row['version']}" for row in cells]
        elif job in ("postgresql-engine", "sqlserver-engine"):
            label = "PostgreSQL" if job == "postgresql-engine" else "SQL Server"
            if declared[0] != label + " ${{ matrix.version }}":
                raise MaintenanceError("Native qualification display shape changed.")

            cells = _matrix_rows(body, frozenset(("version", "image")))
            expanded = [f"{label} {row['version']}" for row in cells]
        else:
            if re.search(r"^      matrix:", body, re.MULTILINE):
                raise MaintenanceError("Only the declared native jobs support matrix expansion.")

            expanded = [_scalar(declared[0])]

        if not expanded:
            raise MaintenanceError("A declared native qualification matrix is empty.")

        names.extend("Full qualification / " + name for name in expanded)

    if len(names) != len(set(names)):
        raise MaintenanceError("Duplicate native qualification producer.")

    return ["schedule-identity", *names]


def producer_names(api, repository: str, workflow: str, commit: str) -> list[str]:
    """Bind fixed audit producers or the complete current qualification matrix."""
    source = source_file(api, repository, f".github/workflows/{workflow}", commit)
    if not re.search(r"^  schedule-identity:\s*$", source, re.MULTILINE):
        raise MaintenanceError("Native workflow is missing its maintenance identity guard.")

    if workflow == MAINTENANCE_WORKFLOW:
        if not re.search(r"^  dependency-vulnerability-audit:\s*$", source, re.MULTILINE):
            raise MaintenanceError("Native workflow is missing its vulnerability audit producer.")

        return ["schedule-identity", "dependency-vulnerability-audit"]

    if workflow != "ci.yml" or "uses: ./.github/workflows/quality-gates.yml" not in source:
        raise MaintenanceError("Scheduled qualification does not use the actual quality workflow.")

    return qualification_producers(source_file(api, repository, ".github/workflows/quality-gates.yml", commit))


def verify_jobs(api, repository: str, run_id: int, expected: list[str]) -> None:
    """Read all pages and require every actual matrix cell, not a green dispatcher."""
    jobs = []
    total = None
    seen = set()
    for page in range(1, 102):
        response = api.request(f"repos/{repository}/actions/runs/{run_id}/jobs?per_page=100&page={page}")
        entries = response.get("jobs") if isinstance(response, dict) else None
        count = response.get("total_count") if isinstance(response, dict) else None
        if (type(count) is not int or count < 1 or count > 10000 or not isinstance(entries, list)
                or len(entries) > 100 or (total is not None and total != count)):
            raise MaintenanceError("Scheduled producer inventory is incomplete or changed during pagination.",
                                   code="producer_evidence_invalid")

        total = count
        for item in entries:
            identity = positive_number(item.get("id")) if isinstance(item, dict) else None
            if identity is None or identity in seen:
                raise MaintenanceError("Scheduled producer inventory repeats a job identity.",
                                       code="producer_evidence_invalid")

            seen.add(identity)
            jobs.append(item)

        if len(jobs) > total:
            raise MaintenanceError("Scheduled producer pagination ended before complete evidence.",
                                   code="producer_evidence_invalid")

        # WHY: A full page can coincide with a stale total. Require the actual
        # terminating short/empty page instead of hiding a later hundred jobs.
        if len(entries) < 100:
            if len(jobs) != total:
                raise MaintenanceError("Scheduled producer pagination ended before complete evidence.",
                                       code="producer_evidence_invalid")

            break
    else:
        raise MaintenanceError("Scheduled producer inventory exceeded its bounded page count.",
                               code="producer_evidence_invalid")

    for name in expected:
        matches = [job for job in jobs if job.get("name") == name]
        if len(matches) != 1 or matches[0].get("status") != "completed" or matches[0].get("conclusion") != "success":
            raise MaintenanceError("A required branch-native producer is missing, duplicated, skipped or unsuccessful.",
                                   code="producer_unsuccessful")


def classify_title(value: object, expected: str) -> str:
    """Categorize remote presentation metadata without retaining its untrusted text."""
    if value is None:
        return "missing"

    if not isinstance(value, str):
        return "invalid_type"

    if not value:
        return "empty"

    return "matching" if value == expected else "different"


def wait_for_child(api, repository: str, run_id: int, workflow: str, ref: str,
                   commit: str, correlation: str, expected: list[str], *,
                   timeout: int = 14400, poll_interval: int = 15, record=lambda update: None) -> dict:
    """Bound execution and queue waits while verifying every observation's source identity."""
    started = time.monotonic()
    deadline = started + timeout
    # WHY: Dispatch creation and readable run metadata are separate API
    # observations. Initial visibility gets two minutes, not the four-hour run
    # budget, and never authorizes substituting a similarly named run.
    visibility_deadline = min(deadline, started + 120)
    initialized = False
    result_deadline = None
    identities = {"event": "workflow_dispatch", "head_sha": commit,
                  "head_branch": ref.removeprefix("refs/heads/"),
                  "path": f".github/workflows/{workflow}"}
    pre_execution = ("queued", "pending", "requested", "waiting")
    states = (*pre_execution, "in_progress", "completed")
    conclusions = (None, "success", "failure", "cancelled", "skipped", "timed_out", "neutral",
                   "action_required", "stale", "startup_failure")

    expected_title = f"maintenance {correlation}"
    while True:
        try:
            run = api.request(f"repos/{repository}/actions/runs/{run_id}")
        except MaintenanceError as error:
            if initialized or error.code != "github_http_error" or error.http_status != 404:
                raise

            record({"observationCode": "child_not_visible", "lastTransientHttpStatus": 404})
            if time.monotonic() >= visibility_deadline:
                raise MaintenanceError("Newly dispatched child did not become visible before its bound.",
                                       code="child_visibility_timeout", http_status=404) from error

            time.sleep(min(poll_interval, max(0, visibility_deadline - time.monotonic())))
            continue

        if not isinstance(run, dict) or type(run.get("id")) is not int or run["id"] != run_id:
            raise MaintenanceError("Scheduled child has a different run identity.",
                                   code="child_identity_mismatch", identity_field="id")

        status, conclusion = run.get("status"), run.get("conclusion")
        title = run.get("display_title")
        # WHY: Record categories before rejection as well as while polling.
        # Raw titles and malformed status/conclusion payloads are never reports.
        observation = {"status": status if status in states else "unknown",
                       "conclusion": conclusion if conclusion in conclusions else "unknown",
                       "titleState": classify_title(title, expected_title)}

        record({**observation, "observationCode": "child_metadata_observed"})

        if status not in states:
            raise MaintenanceError("Scheduled child has an unrecognized execution state.", code="child_state_invalid")

        if conclusion not in conclusions:
            raise MaintenanceError("Scheduled child has an unrecognized conclusion.", code="child_state_invalid")

        if status != "completed" and conclusion is not None:
            raise MaintenanceError("Scheduled child reports a conclusion before completion.",
                                   code="child_state_invalid")

        missing = []
        for field, target in identities.items():
            value = run.get(field)
            if value is None or value == "":
                missing.append(field)
                continue

            actual = value.split("@", 1)[0] if field == "path" and isinstance(value, str) else value
            if actual != target:
                raise MaintenanceError("Scheduled child contradicts its dispatched source identity.",
                                       code="child_identity_mismatch", identity_field=field)

        title_matches = title == expected_title
        if not title_matches and (initialized or (title is not None and not isinstance(title, str))):
            raise MaintenanceError("Scheduled child has a different correlation title.",
                                   code="child_identity_mismatch", identity_field="display_title")

        if status == "completed" and conclusion not in (None, "success"):
            raise MaintenanceError("Scheduled child failed or was cancelled; it is not qualification evidence.",
                                   code="child_failed")

        if not title_matches:
            # WHY: run-name presentation can lag the immutable run identity.
            # Even completed success cannot prove the title has materialized;
            # exact correlation must settle within the original visibility bound.
            missing.append("display_title")

        if missing:
            if initialized or (status not in pre_execution and missing != ["display_title"]):
                raise MaintenanceError("Scheduled child lacks its required exact execution identity.",
                                       code="child_identity_mismatch", identity_field=missing[0])

            record({**observation, "observationCode": "child_identity_pending"})
            if time.monotonic() >= visibility_deadline:
                raise MaintenanceError("Initial child metadata did not become complete before its bound.",
                                       code="child_visibility_timeout")

            time.sleep(min(poll_interval, max(0, visibility_deadline - time.monotonic())))
            continue

        record({**observation, "observationCode": "child_observed"})
        # WHY: A late scheduler wakeup or slow response must not bypass the
        # bound merely because the next observation is already completed.
        observed_at = time.monotonic()
        if not initialized and observed_at >= visibility_deadline:
            raise MaintenanceError("Initial child identity became visible after its bound.",
                                   code="child_visibility_timeout")

        if observed_at >= deadline:
            raise MaintenanceError("Scheduled child exceeded its queue/execution wait; no success is recorded.",
                                   code="child_wait_timeout")

        if status == "completed" and conclusion is None and result_deadline is None:
            # WHY: Completion and its conclusion can be separate observations.
            # A long-running proven child gets one bounded result window, not
            # a renewed workload deadline or a reset on each missing response.
            result_deadline = min(deadline, observed_at + 120) if initialized else visibility_deadline

        if result_deadline is not None and observed_at >= result_deadline:
            raise MaintenanceError("Completed child did not provide its result before the fixed deadline.",
                                   code="child_result_timeout")

        initialized = True
        if status == "completed":
            if conclusion is None:
                assert result_deadline is not None
                record({**observation, "observationCode": "child_result_pending"})
                time.sleep(min(poll_interval, max(0, result_deadline - time.monotonic())))
                continue

            verify_jobs(api, repository, run_id, expected)

            return {"sourceRef": ref, "sourceCommit": commit, "runId": run_id,
                    "workflow": workflow, "conclusion": "success", "verificationStatus": "success"}

        if time.monotonic() >= deadline:
            raise MaintenanceError("Scheduled child exceeded its queue/execution wait; no success is recorded.",
                                   code="child_wait_timeout")

        poll_deadline = deadline if result_deadline is None else result_deadline
        time.sleep(min(poll_interval, max(0, poll_deadline - time.monotonic())))


def dispatch_line(api, repository: str, workflow: str, ref: str, correlation: str,
                  *, record=lambda update: None, timeout: int = 14400) -> dict:
    """Resolve the protected native head, retain the returned immutable run ID, then await it."""
    branch = ref.removeprefix("refs/heads/")
    observed = api.request(f"repos/{repository}/branches/{quote(branch, safe='')}")
    commit = observed.get("commit", {}).get("sha", "") if isinstance(observed, dict) else ""
    if (not isinstance(observed, dict) or observed.get("name") != branch
            or observed.get("protected") is not True or not SHA.fullmatch(commit)):
        raise MaintenanceError("Active branch is missing, unprotected or has no exact source commit.")

    record({"sourceCommit": commit, "verificationStatus": "resolved"})
    expected = producer_names(api, repository, workflow, commit)
    response = api.request(f"repos/{repository}/actions/workflows/{workflow}/dispatches", "POST", {
        "ref": branch, "inputs": {"maintenance": True, "schedule-correlation": correlation,
                                    "schedule-source-sha": commit}})
    # WHY: The current API returns the run ID. Empty/legacy 204 cannot safely
    # be replaced with a racy search over concurrently dispatched workflows.
    run_id = positive_number(response.get("workflow_run_id")) if isinstance(response, dict) else None
    if run_id is None:
        raise MaintenanceError("Dispatch did not return its immutable workflow run identity.")

    record({"runId": run_id, "verificationStatus": "dispatched"})

    return wait_for_child(api, repository, run_id, workflow, ref, commit,
                          correlation, expected, timeout=timeout, record=record)


def persist_report(path: Path, report: dict) -> None:
    """Atomically retain incomplete and failed children before later polling can fail."""
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(mode="w", encoding="ascii", dir=path.parent,
                                     prefix=".maintenance-", delete=False) as stream:
        json.dump(report, stream, indent=2, sort_keys=True)
        stream.write("\n")

    Path(stream.name).replace(path)


def coordinate_lines(api, authority: dict, workload: str, environment: dict,
                     output: Path) -> dict:
    """Dispatch all active native lines once and collect failed siblings independently."""
    policy = authority["policy"]
    if (workload not in ("audit", "qualification")
            or environment.get("GITHUB_EVENT_NAME") not in ("schedule", "workflow_dispatch")
            or environment.get("GITHUB_REF") != policy["authorityRef"]
            or environment.get("GITHUB_SHA") != authority["controlCommit"]
            or environment.get("GITHUB_REPOSITORY") != policy["repository"]
            or environment.get("GITHUB_WORKFLOW_REF")
            != f"{policy['repository']}/.github/workflows/{MAINTENANCE_WORKFLOW}@{policy['authorityRef']}"):
        raise MaintenanceError("Only current protected-main maintenance can coordinate active lines.")

    workflow = "ci.yml" if workload == "qualification" else MAINTENANCE_WORKFLOW
    correlation = (f"schedule-{positive_number(environment.get('GITHUB_RUN_ID'))}"
                   f"-{positive_number(environment.get('GITHUB_RUN_ATTEMPT'))}")
    targets = active_lines(policy)
    report = {"schemaVersion": 1, "authorityCommit": authority["controlCommit"],
              "workload": workload, "correlation": correlation, "conclusion": "in_progress",
              "children": [{"sourceRef": line["sourceRef"], "workflow": workflow,
                            "verificationStatus": "pending"} for line in targets]}
    lock = threading.Lock()

    def record(index: int, observation: dict) -> None:
        """Serialize diagnostic updates without losing independent sibling results."""
        with lock:
            report["children"][index].update(observation)
            persist_report(output, report)

    persist_report(output, report)
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as executor:
        futures = [(index, executor.submit(dispatch_line, api, policy["repository"], workflow,
                                           line["sourceRef"], correlation,
                                           record=lambda update, owner=index: record(owner, update)))
                   for index, line in enumerate(targets)]
        for index, future in futures:
            try:
                record(index, future.result())
            except (MaintenanceError, ValueError, OSError, subprocess.SubprocessError) as error:
                record(index, failure_observation(error))

    report["conclusion"] = ("success" if all(child["verificationStatus"] == "success"
                                             for child in report["children"]) else "failure")
    persist_report(output, report)

    return report


def solution_projects(root: Path) -> list[str]:
    """Require the solution inventory to include every regular source project."""
    solution = root / "Doka.EntityFrameworkCore.SafeMigrations.slnx"
    parsed = ET.parse(solution)
    projects = [str((root / node.attrib["Path"]).resolve()) for node in parsed.findall(".//Project")]
    if not projects or len(projects) != len(set(projects)):
        raise MaintenanceError("Audit solution project inventory is missing or duplicated.")

    expected = []
    for group in ("src", "tests", "benchmarks", "eng", "samples"):
        for path in (root / group).rglob("*.csproj"):
            if any(part in ("bin", "obj", "artifacts") for part in path.relative_to(root).parts):
                continue

            if path.is_symlink() or not path.is_file():
                raise MaintenanceError("Audit source projects must be regular files.")

            expected.append(str(path.resolve()))

    if set(projects) != set(expected):
        raise MaintenanceError("Audit solution does not cover the complete branch-native project graph.")

    return projects


def validate_audit(report: dict, expected_projects: list[str]) -> dict:
    """Reject findings and incomplete advisory/project evidence even after exit zero."""
    if type(report.get("version")) is not int or report["version"] != 1:
        raise MaintenanceError("Audit schema is not the qualified version.")

    parameters = report.get("parameters", "")
    if (not isinstance(parameters, str)
            or any(not re.search(r"(?:^|\s)" + flag + r"(?:\s|$)", parameters)
                   for flag in ("--vulnerable", "--include-transitive"))):
        raise MaintenanceError("Audit must inspect direct and transitive vulnerabilities.")

    if (report.get("sources") != ["https://api.nuget.org/v3/index.json"]
            or any(key in report and report[key] != [] for key in ("problems", "warnings", "errors"))):
        raise MaintenanceError("Audit sources or advisory retrieval are incomplete.")

    projects = report.get("projects")
    if not isinstance(projects, list) or not projects:
        raise MaintenanceError("Audit has no completed project inventory.")

    observed = []
    for project in projects:
        if not isinstance(project, dict) or not isinstance(project.get("path"), str):
            raise MaintenanceError("Audit project evidence is malformed.")

        observed.append(str(Path(project["path"]).resolve()))
        # WHY: Clean .NET vulnerability reports omit frameworks entirely.
        # Requiring finding-only fields would reject genuine clean scans.
        frameworks = project.get("frameworks", [])
        if not isinstance(frameworks, list):
            raise MaintenanceError("Audit framework inventory is malformed.")

        seen_frameworks = set()
        for framework in frameworks:
            if (not isinstance(framework, dict) or not isinstance(framework.get("framework"), str)
                    or not framework["framework"] or framework["framework"] in seen_frameworks):
                raise MaintenanceError("Audit framework identity is missing or duplicated.")

            seen_frameworks.add(framework["framework"])
            for kind in ("topLevelPackages", "transitivePackages"):
                packages = framework.get(kind, [])
                if not isinstance(packages, list):
                    raise MaintenanceError("Audit package inventory is malformed.")

                seen_packages = set()
                for package in packages:
                    if (not isinstance(package, dict) or not isinstance(package.get("id"), str)
                            or not package["id"] or package["id"] in seen_packages
                            or not isinstance(package.get("resolvedVersion"), str)
                            or not package["resolvedVersion"]
                            or not isinstance(package.get("vulnerabilities"), list)):
                        raise MaintenanceError("Audit package evidence is incomplete or duplicated.")

                    seen_packages.add(package["id"])
                    if package["vulnerabilities"]:
                        raise MaintenanceError("Known direct or transitive vulnerabilities remain.")

    if (len(observed) != len(set(observed)) or len(expected_projects) != len(set(expected_projects))
            or set(observed) != {str(Path(path).resolve()) for path in expected_projects}):
        raise MaintenanceError("Audit does not cover the exact complete project inventory.")

    return {"schemaVersion": 1, "conclusion": "success", "projectCount": len(observed)}


def run_audit(root: Path, output: Path) -> dict:
    """Refresh audited restore metadata and retain raw branch-native vulnerability evidence."""
    expected = solution_projects(root)
    output = output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    # WHY: NuGet HTTP-cache names contain ':' and cannot be uploaded as GitHub
    # artifacts. An explicit sibling directory stays outside evidence even if
    # TMPDIR points inside it; context cleanup also runs on failed/aborted scans.
    with tempfile.TemporaryDirectory(prefix=".nuget-audit-http-", dir=output.parent) as cache:
        environment = {**os.environ, "NUGET_HTTP_CACHE_PATH": cache}
        restore = ["dotnet", "restore", str(root / "Doka.EntityFrameworkCore.SafeMigrations.slnx"),
                   "--locked-mode", "--force", "--no-http-cache", "--disable-build-servers",
                   "-p:NuGetAudit=true", "-p:NuGetAuditMode=all", "-p:NuGetAuditLevel=low",
                   "-p:TreatWarningsAsErrors=true", "-p:NoWarn=", "-p:WarningsNotAsErrors="]
        with (output / "restore.log").open("w", encoding="utf-8") as log:
            result = subprocess.run(restore, cwd=root, env=environment, stdout=log, stderr=subprocess.STDOUT,
                                    check=False, timeout=1200)

        if result.returncode:
            raise MaintenanceError("Fresh audited restore failed; retained log is not clean audit evidence.")

        command = ["dotnet", "package", "list", "--project", str(root / "Doka.EntityFrameworkCore.SafeMigrations.slnx"),
                   "--no-restore", "--vulnerable", "--include-transitive", "--format", "json", "--output-version", "1",
                   "--source", "https://api.nuget.org/v3/index.json"]
        with (output / "audit.json").open("w", encoding="utf-8") as report, \
                (output / "audit.log").open("w", encoding="utf-8") as errors:
            result = subprocess.run(command, cwd=root, env=environment, stdout=report, stderr=errors,
                                    check=False, timeout=1200)

        if result.returncode or (output / "audit.log").read_text(encoding="utf-8").strip():
            raise MaintenanceError("Vulnerability listing failed or emitted a warning; inspect retained evidence.")

        summary = validate_audit(read_json(output / "audit.json"), expected)

    persist_report(output / "summary.json", summary)

    return summary


def main(argv: list[str] | None = None) -> int:
    """Run a native guard/audit, default-branch coordinator or dependency-policy check."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=(
        "guard", "coordinate", "audit", "check-dependencies", "render-dependencies"))
    parser.add_argument("--workflow", choices=("ci.yml", MAINTENANCE_WORKFLOW))
    parser.add_argument("--workload", choices=("audit", "qualification"), default="audit")
    parser.add_argument("--root", type=Path, default=Path.cwd())
    parser.add_argument("--output", type=Path)
    arguments = parser.parse_args(argv)
    try:
        if arguments.command == "guard" and os.environ.get("INPUT_MAINTENANCE", "false") == "false":
            validate_child(None, arguments.workflow, dict(os.environ))
            print("Ordinary CI remains nonpublishing and unrestricted by release activation.")

            return 0

        if arguments.command in ("check-dependencies", "render-dependencies"):
            from eng.release import dependencies, lines

            policy = lines.load_policy(arguments.root / "eng/release/lines-policy.json")
            source = (arguments.root / ".github/dependabot.yml").read_text(encoding="ascii")
            template = (arguments.root / ".github/dependabot-template.yml").read_text(encoding="ascii")
            if arguments.command == "check-dependencies":
                dependencies.validate_configuration(policy, source, template=template)
                print("Dependency update targets match active release lines.")
            else:
                rendered = dependencies.render_configuration(policy, source, template=template)
                if arguments.output:
                    arguments.output.write_text(rendered, encoding="ascii")
                else:
                    print(rendered, end="")

            return 0

        if arguments.command == "audit":
            if arguments.output is None:
                raise MaintenanceError("Audit requires its retained output directory.")

            summary = run_audit(arguments.root.resolve(), arguments.output.resolve())
            print(json.dumps(summary, sort_keys=True))

            return 0

        from eng.release.publication import observe_authority

        api = GitHub()
        authority = observe_authority(os.environ["GITHUB_REPOSITORY"], api)
        if arguments.command == "guard":
            validate_child(authority["policy"], arguments.workflow, dict(os.environ))
            print("Maintenance source and active-line identity verified.")

            return 0

        if arguments.output is None:
            raise MaintenanceError("Coordinator requires a retained output report.")

        report = coordinate_lines(api, authority, arguments.workload, dict(os.environ), arguments.output)
        print(json.dumps(report, sort_keys=True))

        return 0 if report["conclusion"] == "success" else 1
    except (ValueError, RuntimeError, OSError, KeyError, TypeError, ET.ParseError, subprocess.SubprocessError):
        print("Release-line maintenance failed; inspect the retained source-bound evidence.", file=sys.stderr)

        return 1


if __name__ == "__main__":
    raise SystemExit(main())
