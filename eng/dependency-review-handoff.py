"""Bind read-only dependency requests and privileged results to authoritative PR facts."""

import argparse
import json
import os
import re
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import quote


if not __package__:
    # WHY: The trusted receiver executes this file from an isolated engineering checkout.
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))


WORKFLOW_PATH = ".github/workflows/dependency-review.yml"
RECEIVER_WORKFLOW_PATH = ".github/workflows/dependency-review-results.yml"
CI_WORKFLOW_PATH = ".github/workflows/ci.yml"
CHECK_NAME = "dependency-review"
MAX_JSON_BYTES = 16 * 1024 * 1024
REQUIRED_RESULTS = {"context", "submit-nuget"}
SOURCE_PRODUCERS = {"prepare-base / restore", "prepare-head / restore", "request-dependency-review"}


class PolicyNotFound(ValueError):
    """A genuine HTTP 404 for policy bytes, never an authorization or JSON failure."""

    def __init__(self, route: str):
        super().__init__("protected-main policy does not exist at this exact revision")
        self.route = route


def number(value: object) -> int:
    """Accept bounded positive API identities without bool/int ambiguity."""
    if isinstance(value, bool) or not re.fullmatch(r"[1-9][0-9]{0,17}", str(value)):
        raise ValueError("invalid numeric GitHub identity")

    return int(value)


def sha(value: object) -> str:
    """Require an exact lower-case Git SHA before constructing a route or output."""
    if not isinstance(value, str) or not re.fullmatch(r"[0-9a-f]{40}", value):
        raise ValueError("invalid Git SHA")

    return value


def read_json(path: Path) -> dict:
    """Read a bounded object and reject duplicate properties at the trust boundary."""
    if path.is_symlink() or not path.is_file() or path.stat().st_size > MAX_JSON_BYTES:
        raise ValueError("expected a bounded regular JSON file")

    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("duplicate JSON property")

            result[key] = value

        return result

    result = json.loads(path.read_bytes(), object_pairs_hook=unique)
    if not isinstance(result, dict):
        raise ValueError("expected a JSON object")

    return result


class GitHub:
    """Use the installed official CLI and fixed API host for bounded context responses."""

    def request(self, route: str, method: str = "GET", payload: dict | None = None):
        command = ["gh", "api", "--include", "--hostname", "github.com", "--method", method,
                   "-H", "Accept: application/vnd.github+json", route]
        if payload is not None:
            command.extend(["--input", "-"])

        result = subprocess.run(command, input=json.dumps(payload) if payload is not None else None,
                                capture_output=True, text=True, timeout=60, check=False)
        if len(result.stdout) > MAX_JSON_BYTES:
            raise ValueError("GitHub API request failed or exceeded its bound")

        headers, separator, body = result.stdout.replace("\r\n", "\n").partition("\n\n")
        status = re.match(r"HTTP/[0-9.]+ ([0-9]{3})(?: |\n)", headers)
        if not separator or status is None:
            raise ValueError("GitHub API response did not expose its actual HTTP status")

        if (status[1] == "404" and method == "GET" and re.fullmatch(
                r"repos/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/contents/eng/release/lines-policy\.json\?ref=[0-9a-f]{40}",
                route)):
            raise PolicyNotFound(route)

        if result.returncode or not 200 <= int(status[1]) < 300:
            raise ValueError("GitHub API request failed")

        return json.loads(body)


def registered_bases(api, name: str) -> tuple[set[str], str | None]:
    """Read current policy, with an authenticated PR-only bridge for its first main merge.

    WHY: A reviewed-head startup must work before this feature is on main.
    Only actual policy absence on independently protected main retains the old
    main-only PR contract. Publication never consumes this bridge.
    """
    from eng.release.publication import observe_authority

    try:
        authority = observe_authority(name, api)
    except PolicyNotFound as absent:
        branch = api.request(f"repos/{name}/branches/main")
        commit = sha(branch.get("commit", {}).get("sha")) if isinstance(branch, dict) else None
        if (not isinstance(branch, dict) or branch.get("name") != "main"
                or branch.get("protected") is not True
                or absent.route != f"repos/{name}/contents/eng/release/lines-policy.json?ref={commit}"):
            raise ValueError("legacy PR startup cannot prove exact protected-main policy absence")

        return {"refs/heads/main"}, commit
    except (RuntimeError, ValueError) as error:
        raise ValueError("current release-line authority could not be verified") from error

    return {line["sourceRef"] for line in authority["policy"]["lines"]}, None


def repository(environment: dict) -> tuple[str, int]:
    """Resolve only the receiving repository, never a PR-provided API host."""
    name = environment.get("GITHUB_REPOSITORY", "")
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", name):
        raise ValueError("invalid receiving repository")

    if environment.get("GITHUB_SERVER_URL", "https://github.com") != "https://github.com":
        raise ValueError("this workflow requires github.com")

    return name, number(environment.get("GITHUB_REPOSITORY_ID"))


def is_source_workflow_path(value: object, expected_path: str = WORKFLOW_PATH) -> bool:
    """Accept an exact workflow file and GitHub's optional bounded @ref metadata."""
    if not isinstance(value, str) or len(value) > len(expected_path) + 1025:
        return False

    path, separator, ref = value.partition("@")
    # WHY: The REST examples include @main although existing runs also return
    # the bare path. The suffix is metadata; it cannot select another workflow.
    return path == expected_path and (not separator or bool(ref)
                                     and all(character.isprintable() and not character.isspace()
                                             for character in ref))


def receiver_identity(environment: dict) -> tuple[str, str]:
    """Select the caller from GitHub's actual event, never from reusable context data."""
    event_name = environment.get("GITHUB_EVENT_NAME")
    if event_name == "workflow_dispatch":
        return event_name, CI_WORKFLOW_PATH

    if event_name == "workflow_run" and not environment.get("DEPENDENCY_REVIEW_PR"):
        return event_name, RECEIVER_WORKFLOW_PATH

    raise ValueError("unexpected receiving event or input")


def latest_source_run(api, prefix: str, workflow_id: int, head_sha: str,
                      head_repository_id: int, head_branch: str) -> dict:
    """Read one bounded inventory and select the latest matching request, regardless of result."""
    runs = api.request(f"{prefix}/actions/workflows/{workflow_id}/runs"
                       f"?event=pull_request&head_sha={head_sha}&per_page=100")
    records = runs.get("workflow_runs")
    if (type(runs.get("total_count")) is not int or not 0 < runs["total_count"] < 100
            or not isinstance(records, list) or len(records) != runs["total_count"]
            or any(not isinstance(item, dict) for item in records)):
        raise ValueError("unbounded source workflow run inventory")

    if len({number(item.get("id")) for item in records}) != len(records):
        raise ValueError("duplicate source workflow run inventory")

    matching = [item for item in records
                if item.get("head_sha") == head_sha
                and item.get("head_branch") == head_branch
                and item.get("head_repository", {}).get("id") == head_repository_id]
    if not matching:
        raise ValueError("no matching source workflow run")

    return max(matching, key=lambda item: number(item.get("id")))


def require_manual_revision(environment: dict, head_sha: str, head_repository_id: int,
                            head_branch: str) -> None:
    """Allow reviewed same-repository branch code only at the current PR head and workflow SHA."""
    name, repository_id = repository(environment)
    ref = f"refs/heads/{head_branch}"
    if (head_repository_id != repository_id or head_branch == "main"
            or not isinstance(head_branch, str) or not 0 < len(head_branch) <= 1024
            or any(not character.isprintable() or character.isspace() for character in head_branch)
            or sha(environment.get("GITHUB_SHA")) != head_sha
            or sha(environment.get("GITHUB_WORKFLOW_SHA")) != head_sha
            or environment.get("GITHUB_REF_TYPE") != "branch" or environment.get("GITHUB_REF") != ref
            or environment.get("GITHUB_WORKFLOW_REF", f"{name}/{CI_WORKFLOW_PATH}@{ref}")
            != f"{name}/{CI_WORKFLOW_PATH}@{ref}"):
        raise ValueError("manual receiver is not the exact reviewed current PR revision")


def dispatch_source(api, environment: dict, event: dict, prefix: str,
                    workflow_id: int) -> tuple[int, dict]:
    """Normalize a dispatch using API facts, then leave all ordinary source validation intact."""
    pr_number = number(event.get("inputs", {}).get("dependency-review-pr"))
    if number(environment.get("DEPENDENCY_REVIEW_PR")) != pr_number:
        raise ValueError("manual request input differs from the original dispatch")

    pr = api.request(f"{prefix}/pulls/{pr_number}")
    if pr.get("number") != pr_number:
        raise ValueError("manual request does not identify the returned pull request")

    head = pr.get("head", {})
    head_sha, head_repository_id = sha(head.get("sha")), number(head.get("repo", {}).get("id"))
    head_branch = head.get("ref")
    require_manual_revision(environment, head_sha, head_repository_id, head_branch)
    latest = latest_source_run(api, prefix, workflow_id, head_sha, head_repository_id, head_branch)
    run = api.request(f"{prefix}/actions/runs/{number(latest.get('id'))}")
    # WHY: Discovery must not filter for success: a newer failed, cancelled or
    # pending request supersedes an older success just as on automatic handoff.
    normalized = {"action": "completed", "repository": event.get("repository"), "workflow_run": run}

    return pr_number, normalized


def source_inventory(api, route: str, field: str) -> list[dict]:
    """Require the entire bounded authenticated inventory, never a truncated first page."""
    response = api.request(route)
    records = response.get(field) if isinstance(response, dict) else None
    count = response.get("total_count") if isinstance(response, dict) else None
    if (type(count) is not int or not 0 < count < 100 or not isinstance(records, list)
            or len(records) != count or any(not isinstance(item, dict) for item in records)
            or len({number(item.get("id")) for item in records}) != count):
        raise ValueError("source inventory is missing, duplicated or incomplete")

    return records


def source_artifacts(api, prefix: str, run: dict, repository_id: int) -> dict:
    """Bind complete successful PR producers to exact immutable data-only artifacts.

    WHY: Restore executes contributor MSBuild hooks. Only the pull_request run
    may own that execution; a default-branch receiver never inherits it. Remote
    artifact names discover IDs once, while context reuse pins the IDs/digests.
    """
    run_id, attempt = number(run.get("id")), number(run.get("run_attempt"))
    jobs = source_inventory(api, f"{prefix}/actions/runs/{run_id}/jobs?filter=all&per_page=100", "jobs")
    producers, identities = {}, set()
    for job in jobs:
        name, producing_attempt = job.get("name"), number(job.get("run_attempt"))
        if (not isinstance(name, str) or name not in SOURCE_PRODUCERS
                or number(job.get("run_id")) != run_id or producing_attempt > attempt
                or job.get("head_sha") != run["head_sha"] or (name, producing_attempt) in identities):
            raise ValueError("source producer identity or attempt is contradictory")

        identities.add((name, producing_attempt))
        if name not in producers or producing_attempt > number(producers[name]["run_attempt"]):
            producers[name] = job

    if (set(producers) != SOURCE_PRODUCERS
            or max(number(job["run_attempt"]) for job in producers.values()) != attempt
            or any(job.get("status") != "completed" or job.get("conclusion") != "success"
                   for job in producers.values())):
        raise ValueError("latest source producers did not all succeed")

    records = source_inventory(api, f"{prefix}/actions/runs/{run_id}/artifacts?per_page=100", "artifacts")
    result = {}
    for revision in ("base", "head"):
        producer = producers[f"prepare-{revision} / restore"]
        producing_attempt = number(producer["run_attempt"])
        # WHY: Re-run failed jobs retains successful earlier producers. Their
        # original immutable artifacts, not the receiver attempt, bind recovery.
        expected_name = f"dependency-inputs-{revision}-{run_id}-{producing_attempt}"
        matches = [item for item in records if item.get("name") == expected_name]
        if len(matches) != 1:
            raise ValueError("expected one immutable artifact for each exact source revision")

        artifact = matches[0]
        origin = artifact.get("workflow_run")
        expires = artifact.get("expires_at")
        digest = artifact.get("digest")
        if (not isinstance(origin, dict) or number(origin.get("id")) != run_id
                or number(origin.get("repository_id")) != repository_id
                or number(origin.get("head_repository_id")) != run["head_repository"]["id"]
                or origin.get("head_sha") != run["head_sha"] or origin.get("head_branch") != run["head_branch"]
                or artifact.get("expired") is not False
                or type(artifact.get("size_in_bytes")) is not int or not 0 < artifact["size_in_bytes"] <= 1024**3
                or not isinstance(digest, str) or not re.fullmatch(r"sha256:[0-9a-f]{64}", digest)
                or not isinstance(expires, str)):
            raise ValueError("source artifact identity, size, expiry or digest is unproven")

        expiration = datetime.fromisoformat(expires.replace("Z", "+00:00"))
        if expiration.tzinfo is None or expiration <= datetime.now(timezone.utc):
            raise ValueError("source artifact has expired")

        result[revision] = {"id": number(artifact["id"]), "digest": digest,
                            "producer_id": number(producer["id"]), "producer_attempt": producing_attempt}

    return result


def resolve(api, environment: dict, event: dict) -> dict:
    """Validate automatic and startup requests through the same source/PR trust boundary."""
    name, repository_id = repository(environment)
    receiver_event, receiver_path = receiver_identity(environment)
    if event.get("repository", {}).get("id") != repository_id:
        raise ValueError("unexpected receiving event repository")

    prefix = f"repos/{name}"
    # WHY: A startup checkout may be the reviewed PR head. Only independently
    # observed protected-main policy can authorize the privileged target base.
    registered_refs, legacy_main_commit = registered_bases(api, name)
    workflow = api.request(f"{prefix}/actions/workflows/dependency-review.yml")
    if workflow.get("path") != WORKFLOW_PATH or workflow.get("state") != "active":
        raise ValueError("unexpected source workflow")

    workflow_id = number(workflow.get("id"))
    manual_pr = None
    if receiver_event == "workflow_dispatch":
        manual_pr, event = dispatch_source(api, environment, event, prefix, workflow_id)

    if event.get("action") != "completed":
        raise ValueError("unexpected source completion action")

    emitted = event.get("workflow_run", {})
    run_id, attempt = number(emitted.get("id")), number(emitted.get("run_attempt"))
    current = api.request(f"{prefix}/actions/runs/{run_id}")
    run = api.request(f"{prefix}/actions/runs/{run_id}/attempts/{attempt}")
    for item in (emitted, current, run):
        if (item.get("id") != run_id or item.get("run_attempt") != attempt
                or item.get("workflow_id") != workflow_id or not is_source_workflow_path(item.get("path"))
                or item.get("repository", {}).get("id") != repository_id
                or item.get("event") != "pull_request" or item.get("status") != "completed"):
            raise ValueError("source workflow run identity or latest attempt differs")

    head_sha = sha(run.get("head_sha"))
    head_repository_id = number(run.get("head_repository", {}).get("id"))
    head_branch = run.get("head_branch")
    if (not isinstance(head_branch, str) or not 0 < len(head_branch) <= 1024
            or any(not character.isprintable() or character.isspace() for character in head_branch)):
        raise ValueError("invalid source head branch")

    if any(item.get("head_sha") != head_sha
           or item.get("head_repository", {}).get("id") != head_repository_id
           or item.get("head_branch") != head_branch
           or item.get("conclusion") != run.get("conclusion") for item in (emitted, current)):
        raise ValueError("source run head, branch or conclusion differs")

    # WHY: GitHub can leave workflow_run.pull_requests empty for forks. Resolve
    # association from the commit API, then reread the full current PR object.
    candidates = api.request(f"{prefix}/commits/{head_sha}/pulls?per_page=100")
    if not isinstance(candidates, list) or len(candidates) >= 100:
        raise ValueError("ambiguous or unbounded pull request association")

    associated = [item for item in candidates
                  if item.get("state") == "open" and item.get("head", {}).get("sha") == head_sha
                  and item.get("head", {}).get("ref") == head_branch
                  and item.get("head", {}).get("repo", {}).get("id") == head_repository_id
                  and item.get("base", {}).get("repo", {}).get("id") == repository_id
                  and f"refs/heads/{item.get('base', {}).get('ref')}" in registered_refs]
    if len(associated) != 1:
        raise ValueError("expected one current pull request for the source head")

    pr_number = number(associated[0].get("number"))
    pr = api.request(f"{prefix}/pulls/{pr_number}")
    if (pr.get("number") != pr_number or pr.get("state") != "open"
            or pr.get("head", {}).get("sha") != head_sha
            or pr.get("head", {}).get("ref") != head_branch
            or pr.get("head", {}).get("repo", {}).get("id") != head_repository_id
            or pr.get("base", {}).get("repo", {}).get("id") != repository_id
            or f"refs/heads/{pr.get('base', {}).get('ref')}" not in registered_refs):
        raise ValueError("pull request no longer matches the source head")

    base_sha = sha(pr["base"].get("sha"))
    if legacy_main_commit is not None and base_sha != legacy_main_commit:
        raise ValueError("legacy PR startup must target the exact independently observed main revision")

    base_branch = pr["base"]["ref"]
    base_route = quote(base_branch, safe="")
    if api.request(f"{prefix}/git/ref/heads/{base_route}").get("object", {}).get("sha") != base_sha:
        raise ValueError("pull request base differs from its current registered branch")

    latest = latest_source_run(api, prefix, workflow_id, head_sha, head_repository_id, head_branch)
    if number(latest.get("id")) != run_id:
        raise ValueError("a newer source workflow run supersedes this result")

    if manual_pr is not None:
        if pr_number != manual_pr:
            raise ValueError("source association differs from the manually requested pull request")

        require_manual_revision(environment, head_sha, head_repository_id, head_branch)

    ready = run.get("conclusion") == "success"
    artifacts, admission = {}, "source_not_successful"
    if ready:
        try:
            artifacts = source_artifacts(api, prefix, run, repository_id)
            admission = "source_artifacts_verified"
        except (OSError, ValueError, KeyError, TypeError, AttributeError, subprocess.SubprocessError):
            # WHY: PR/source identity is already authenticated. An unavailable
            # or contradictory artifact must replace any old same-head green
            # check with an owned failure, not abort before creating a verdict.
            ready, admission = False, "source_artifact_admission_failed"

    return {
        "schema_version": 1, "repository": name, "repository_id": repository_id,
        "source_run_id": run_id, "source_attempt": attempt, "source_workflow_id": workflow_id,
        "head_repository_id": head_repository_id, "head_branch": head_branch,
        "pr": pr_number, "head_sha": head_sha, "base_sha": base_sha,
        "base_ref": f"refs/heads/{base_branch}", "head_ref": f"refs/pull/{pr_number}/head",
        "receiver_run_id": number(environment.get("GITHUB_RUN_ID")),
        "receiver_attempt": number(environment.get("GITHUB_RUN_ATTEMPT")),
        "receiver_event": receiver_event, "receiver_workflow_path": receiver_path,
        "ready": ready, "source_admission": admission, "source_artifacts": artifacts,
        "base_artifact_id": artifacts.get("base", {}).get("id", ""),
        "head_artifact_id": artifacts.get("head", {}).get("id", ""),
    }


def external_id(context: dict) -> str:
    """Keep a check bound to its source and original context-producing receiver attempt."""
    return (f"dependency-review:{context['source_run_id']}:{context['source_attempt']}:"
            f"{context['receiver_run_id']}:{context['receiver_attempt']}")


def check_identity(check: dict, context: dict) -> None:
    """Refuse to update another check or a verdict on a different commit."""
    if (check.get("name") != CHECK_NAME or check.get("head_sha") != context["head_sha"]
            or check.get("external_id") != external_id(context)
            or check.get("app", {}).get("slug") != "github-actions"):
        raise ValueError("check is not owned by this dependency review attempt")


def begin(api, environment: dict, event: dict) -> dict:
    """Create an explicit PR-head check only after the receiving context is validated."""
    context = resolve(api, environment, event)
    require_receiver_current(api, environment, context)
    payload = {
        "name": CHECK_NAME, "head_sha": context["head_sha"], "status": "in_progress",
        "external_id": external_id(context),
        "details_url": f"https://github.com/{context['repository']}/actions/runs/{context['receiver_run_id']}",
        "output": {"title": "Dependency review in progress",
                   "summary": "Validating exact base/head NuGet snapshots and dependency policy."},
    }
    if not context["ready"]:
        payload.update({
            "status": "completed", "conclusion": "failure",
            "output": {"title": "Dependency review did not pass",
                       "summary": "The current source run or its exact dependency artifacts were not admitted."},
        })

    result = api.request(f"repos/{context['repository']}/check-runs", "POST", payload)
    context["check_id"] = number(result.get("id"))

    return context


def require_current(api, environment: dict, event: dict, context: dict) -> None:
    """Reread PR/source facts after the executing receiver attempt was validated."""
    current = resolve(api, environment, event)
    if any(context.get(key) != value for key, value in current.items() if key != "receiver_attempt"):
        raise ValueError("dependency review context is stale or was changed")

    number(context.get("check_id"))


def require_receiver_current(api, environment: dict, context: dict) -> None:
    """Allow reused context only inside the authoritative latest attempt of this receiver run."""
    name, repository_id = repository(environment)
    receiver_event, receiver_path = receiver_identity(environment)
    run_id = number(environment.get("GITHUB_RUN_ID"))
    attempt = number(environment.get("GITHUB_RUN_ATTEMPT"))
    if (context.get("repository") != name or context.get("repository_id") != repository_id
            or context.get("receiver_run_id") != run_id
            or context.get("receiver_event") != receiver_event
            or context.get("receiver_workflow_path") != receiver_path
            or number(context.get("receiver_attempt")) > attempt):
        raise ValueError("check context belongs to another or future receiving attempt")

    workflow = api.request(f"repos/{name}/actions/workflows/{receiver_path.rsplit('/', 1)[1]}")
    if workflow.get("path") != receiver_path or workflow.get("state") != "active":
        raise ValueError("unexpected receiving workflow")

    run = api.request(f"repos/{name}/actions/runs/{run_id}")
    if (run.get("id") != run_id or number(run.get("run_attempt")) != attempt
            or run.get("workflow_id") != number(workflow.get("id"))
            or not is_source_workflow_path(run.get("path"), receiver_path)
            or run.get("repository", {}).get("id") != repository_id
            or run.get("event") != receiver_event
            or run.get("status") not in {"queued", "in_progress", "completed"}):
        raise ValueError("executing receiver is not the authoritative latest workflow attempt")

    if receiver_event == "workflow_dispatch":
        require_manual_revision(environment, context["head_sha"], context["head_repository_id"],
                                context["head_branch"])
        if (run.get("head_sha") != context["head_sha"]
                or run.get("head_repository", {}).get("id") != repository_id
                or run.get("head_branch") != context["head_branch"]):
            raise ValueError("manual receiver API identity differs from the reviewed PR head")


def finish(api, environment: dict, event: dict, context: dict,
           results: object, review_outcome: str) -> bool:
    """Publish success only for current, complete, independently successful jobs."""
    prefix = f"repos/{repository(environment)[0]}"
    check_id = number(context.get("check_id"))
    # WHY: Re-run failed jobs preserves successful context-job outputs from an
    # older attempt. Reuse that exact owned check, but never let a late process
    # from an older executing attempt overwrite the latest receiver's verdict.
    require_receiver_current(api, environment, context)

    check = api.request(f"{prefix}/check-runs/{check_id}")
    check_identity(check, context)
    successful = False
    try:
        require_current(api, environment, event, context)
        successful = (context.get("ready") is True and isinstance(results, dict)
                      and set(results) == REQUIRED_RESULTS
                      and all(value == "success" for value in results.values())
                      and review_outcome == "success")
    except (KeyError, TypeError, ValueError):
        # WHY: A superseded run may close only its own old check. It must never
        # make a changed PR head/base or a newer source attempt look successful.
        successful = False

    api.request(f"{prefix}/check-runs/{check_id}", "PATCH", {
        "status": "completed", "conclusion": "success" if successful else "failure",
        "output": {
            "title": "Dependency review passed" if successful else "Dependency review did not pass",
            "summary": "Exact snapshot and dependency checks passed." if successful
            else "A required request, snapshot, policy check or current-revision validation did not pass.",
        },
    })

    return successful


def main() -> int:
    """Expose the two trusted context operations without executing PR-owned code."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("begin", "finish"))
    parser.add_argument("--context", type=Path, required=True)
    arguments = parser.parse_args()
    try:
        api = GitHub()
        event = read_json(Path(os.environ["GITHUB_EVENT_PATH"]))
        if arguments.command == "begin":
            context = begin(api, os.environ, event)
            with arguments.context.open("x", encoding="ascii") as output:
                json.dump(context, output, sort_keys=True)

            with Path(os.environ["GITHUB_OUTPUT"]).open("a", encoding="ascii") as output:
                for key in ("base_sha", "head_sha", "base_ref", "head_ref", "pr", "check_id", "ready",
                            "source_run_id", "base_artifact_id", "head_artifact_id"):
                    value = str(context[key]).lower() if type(context[key]) is bool else str(context[key])
                    output.write(f"{key}={value}\n")

        elif not finish(api, os.environ, event, read_json(arguments.context),
                        json.loads(os.environ["RESULTS_JSON"]), os.environ["REVIEW_OUTCOME"]):
            return 1

    except (OSError, ValueError, KeyError, TypeError, AttributeError, subprocess.SubprocessError):
        print("Dependency review handoff validation failed; no success verdict was published.", file=sys.stderr)

        return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
