"""Bind one qualified candidate to current protected-main publication authority.

The existing workflow still owns signing, provenance, NuGet and Release writes.
These bounded adapters only establish exact source/bytes/current permission.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import io
import json
import os
import re
import subprocess
import sys
import zipfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Any
from urllib.parse import quote

from eng.release import lines


POLICY_PATH = "eng/release/lines-policy.json"
PUBLICATION_LOCK = "safe-migrations-publication"
PUBLISHER_JOB = "Verify tag and publish"
ADMISSION_PREFIX = "safe-migrations-publication-admission-"
SHA1 = re.compile(r"[0-9a-f]{40}\Z")
SHA256 = re.compile(r"[0-9a-f]{64}\Z")
NUMBER = re.compile(r"[1-9][0-9]*\Z")
FROZEN_FIELDS = {"schemaVersion", "kind", "repository", "sourceRef", "sourceCommit", "version", "tag",
                 "workflowPath", "workflowRunId", "qualificationAttempt", "releaseLine", "toolchain",
                 "authority", "qualifiedForPublication", "qualifiedAtUtc"}
IDENTITY_FIELDS = {"repository", "sourceRef", "sourceCommit", "version", "tag", "workflowPath",
                   "workflowRunId", "qualificationAttempt", "originalPublicationAttempt",
                   "releaseLineSha256", "manifestSha256", "qualifiedArtifact", "provenanceArtifact"}
PACKAGES = ("Doka.EntityFrameworkCore.SafeMigrations", "Doka.EntityFrameworkCore.SafeMigrations.MySql",
            "Doka.EntityFrameworkCore.SafeMigrations.PostgreSql", "Doka.EntityFrameworkCore.SafeMigrations.Sqlite",
            "Doka.EntityFrameworkCore.SafeMigrations.SqlServer")
# WHY: missing admission evidence is recoverable only if prior job history
# proves that none of the existing publisher's write boundaries was reached.
WRITE_STEPS = frozenset({
    "Prepare verified GitHub Release draft", "Request short-lived NuGet.org key",
    "Publish Core package", "Publish MySQL package", "Publish PostgreSQL package",
    "Publish SQLite package", "Publish SQL Server package", "Publish Core symbols",
    "Publish MySQL symbols", "Publish PostgreSQL symbols", "Publish SQLite symbols",
    "Publish SQL Server symbols", "Publish or verify immutable GitHub Release",
})


class PublicationAdmissionError(RuntimeError):
    """Current permission or exact candidate/retry identity cannot be proven."""


class GitHubClient:
    """Read-only official-CLI adapter with bounded, complete API inventories."""

    def request(self, route: str) -> Any:
        """Read JSON without leaking authentication or remote response payloads."""
        data = self._read(["gh", "api", "--method", "GET", route])
        try:
            return json.loads(data, object_pairs_hook=lines.unique_object)
        except (ValueError, UnicodeError, lines.LinePolicyError) as error:
            raise PublicationAdmissionError("GitHub returned invalid or ambiguous JSON.") from error

    def download(self, route: str) -> bytes:
        """Download a small immutable receipt archive, not arbitrary candidate files."""
        return self._read(["gh", "api", "-H", "Accept: application/octet-stream", route])

    @staticmethod
    def _read(arguments: list[str]) -> bytes:
        """Keep API failure visible while excluding payloads and secrets from logs."""
        try:
            result = subprocess.run(arguments, capture_output=True, check=False, timeout=60)
        except (OSError, subprocess.TimeoutExpired) as error:
            raise PublicationAdmissionError("GitHub evidence read failed or timed out.") from error

        if result.returncode != 0:
            raise PublicationAdmissionError("GitHub evidence read was unsuccessful.")

        if len(result.stdout) > 16 * 1024 * 1024:
            raise PublicationAdmissionError("GitHub evidence exceeded the bounded response size.")

        return result.stdout

    def pages(self, route: str, collection: str) -> list[dict]:
        """Reject partial, changing or duplicate inventory rather than trusting page one."""
        values, seen, expected, page = [], set(), None, 1
        separator = "&" if "?" in route else "?"
        while True:
            response = self.request(f"{route}{separator}per_page=100&page={page}")
            entries = response.get(collection) if isinstance(response, dict) else response
            if not isinstance(entries, list) or any(not isinstance(entry, dict) for entry in entries):
                raise PublicationAdmissionError("GitHub paginated collection is invalid.")

            if isinstance(response, dict):
                total = response.get("total_count")
                if type(total) is not int or total < 0 or (expected is not None and total != expected):
                    raise PublicationAdmissionError("GitHub inventory count changed or is invalid.")

                expected = total

            for entry in entries:
                key = entry.get("id")
                if type(key) is not int or key <= 0 or key in seen:
                    raise PublicationAdmissionError("GitHub inventory has missing or duplicate identities.")

                seen.add(key)

            values.extend(entries)
            if (expected is not None and len(values) > expected) or len(values) > 100000:
                raise PublicationAdmissionError("GitHub inventory is inconsistent or exceeds its bound.")

            if len(entries) < 100:
                if expected is not None and expected != len(values):
                    raise PublicationAdmissionError("GitHub inventory is incomplete.")

                return values

            # WHY: even a matching total_count is not proof that a full page is
            # the last page. Read the terminating page and detect stale totals.
            page += 1


def canonical_digest(value: Any) -> str:
    """Hash stable ASCII evidence independent of human-readable indentation."""
    encoded = json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode("ascii")

    return hashlib.sha256(encoded).hexdigest()


def _sha(value: Any, label: str, pattern=SHA256) -> str:
    """Require exact lowercase hash identities before constructing trusted evidence."""
    if not isinstance(value, str) or pattern.fullmatch(value) is None:
        raise PublicationAdmissionError(f"{label} is not an exact hash identity.")

    return value


def _number(value: Any, label: str) -> str:
    """Reject bool/zero/negative/aliased workflow numeric identities."""
    if not isinstance(value, str) or NUMBER.fullmatch(value) is None:
        raise PublicationAdmissionError(f"{label} is not a canonical positive workflow identity.")

    return value


def _timestamp(value: Any) -> None:
    """Require explicitly UTC evidence times without relying on local timezone."""
    if not isinstance(value, str) or not value.endswith("Z"):
        raise PublicationAdmissionError("Evidence requires an explicit UTC timestamp.")

    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as error:
        raise PublicationAdmissionError("Evidence timestamp is invalid.") from error

    if parsed.tzinfo is None:
        raise PublicationAdmissionError("Evidence timestamp must retain explicit UTC timezone information.")


def utc_now() -> str:
    """Return an explicit UTC timestamp for source/authority observations."""
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def _commit(value: Any) -> str | None:
    """Narrow malformed remote commit objects before accessing nested metadata."""
    commit = value.get("commit") if isinstance(value, dict) else None

    return commit.get("sha") if isinstance(commit, dict) else None


def write_json(path: Path, value: dict) -> None:
    """Write a bounded regular receipt without following substituted directories."""
    _regular_parents(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, sort_keys=True, ensure_ascii=True) + "\n", encoding="ascii")


def _regular_parents(path: Path) -> None:
    """Reject both final and parent symlinks in candidate and receipt paths."""
    if any(part.is_symlink() for part in (path, *path.parents)):
        raise PublicationAdmissionError("Evidence paths must not contain symbolic links.")


def file_digest(path: Path) -> str:
    """Hash candidate bytes incrementally without retaining whole package archives."""
    _regular_parents(path)
    if not path.is_file():
        raise PublicationAdmissionError(f"Required candidate file is missing: {path.name}.")

    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(65536), b""):
            digest.update(block)

    return digest.hexdigest()


def validate_authority(value: Any) -> dict:
    """Validate observed authority without converting it into permanent permission."""
    lines.exact_object(value, {"schemaVersion", "kind", "repository", "controlCommit", "authorizationRevision",
                               "authorizationDigest", "policy", "observedUtc"}, "Authority observation")
    policy = lines.validate_policy(value["policy"])
    if (type(value["schemaVersion"]) is not int or value["schemaVersion"] != 1
            or value["kind"] != "release-line-authority-observation"
            or value["repository"] != policy["repository"]
            or value["authorizationRevision"] != policy["revision"]
            or value["authorizationDigest"] != lines.policy_digest(policy)):
        raise PublicationAdmissionError("Authority observation identity does not match its policy.")

    _sha(value["controlCommit"], "Authority commit", SHA1)
    _timestamp(value["observedUtc"])

    return value


def observe_authority(repository: str, client=None) -> dict:
    """Read protected-main policy at its exact observed commit, never from checkout."""
    if not isinstance(repository, str) or lines.REPOSITORY.fullmatch(repository) is None:
        raise PublicationAdmissionError("Repository identity must be an exact owner/name.")

    api = client or GitHubClient()
    branch = api.request(f"repos/{repository}/branches/main")
    commit = _commit(branch)
    if not isinstance(branch, dict) or branch.get("name") != "main" or branch.get("protected") is not True:
        raise PublicationAdmissionError("Current authority requires an independently protected main branch.")

    _sha(commit, "Authority commit", SHA1)
    entry = api.request(f"repos/{repository}/contents/{POLICY_PATH}?ref={commit}")
    if (not isinstance(entry, dict) or entry.get("type") != "file" or entry.get("path") != POLICY_PATH
            or entry.get("encoding") != "base64" or not isinstance(entry.get("content"), str)):
        raise PublicationAdmissionError("Current protected-main policy is unavailable or not a regular file.")

    _sha(entry.get("sha"), "Policy Git blob", SHA1)
    try:
        raw = base64.b64decode("".join(entry["content"].split()), validate=True)
        if len(raw) > 1024 * 1024:
            raise PublicationAdmissionError("Current release policy exceeds its bound.")

        policy = lines.parse_policy(raw)
    except (ValueError, TypeError, lines.LinePolicyError) as error:
        raise PublicationAdmissionError("Current protected-main policy cannot be validated.") from error

    blob = hashlib.sha1(f"blob {len(raw)}\0".encode("ascii") + raw).hexdigest()
    if blob != entry["sha"] or policy["repository"] != repository:
        raise PublicationAdmissionError("Current policy bytes do not match their repository/Git blob.")

    value = {"schemaVersion": 1, "kind": "release-line-authority-observation", "repository": repository,
             "controlCommit": commit, "authorizationRevision": policy["revision"],
             "authorizationDigest": lines.policy_digest(policy), "policy": policy, "observedUtc": utc_now()}

    return validate_authority(value)


def validate_environment(repository: str, line: dict, policy: dict, api: GitHubClient) -> None:
    """Verify existing reviewer/protected-source settings without changing operator choices."""
    name = quote(line["publisherEnvironment"], safe="")
    value = api.request(f"repos/{repository}/environments/{name}")
    rules = value.get("protection_rules") if isinstance(value, dict) else None
    branch_policy = value.get("deployment_branch_policy") if isinstance(value, dict) else None
    if (not isinstance(value, dict) or value.get("name") != line["publisherEnvironment"]
            or not isinstance(rules, list)
            or not any(isinstance(rule, dict) and rule.get("type") == "required_reviewers"
                       and isinstance(rule.get("reviewers"), list) and rule["reviewers"]
                       and all(isinstance(reviewer, dict) for reviewer in rule["reviewers"]) for rule in rules)
            or not isinstance(branch_policy, dict)):
        raise PublicationAdmissionError("The existing nuget reviewer and branch protections are unavailable.")

    active = {entry["sourceRef"] for entry in policy["lines"] if entry["state"] == "active"}
    protected_only = branch_policy.get("protected_branches") is True
    custom = branch_policy.get("custom_branch_policies") is True
    if protected_only == custom:
        raise PublicationAdmissionError("The nuget environment branch policy is ambiguous or unrestricted.")

    if custom:
        grants = api.pages(f"repos/{repository}/environments/{name}/deployment-branch-policies", "branch_policies")
        actual = set()
        for grant in grants:
            branch = grant.get("name")
            if grant.get("type") != "branch" or not isinstance(branch, str) or any(c in branch for c in "*?["):
                raise PublicationAdmissionError("The nuget environment contains a wildcard or non-branch grant.")

            actual.add(f"refs/heads/{branch}")

        if actual != active:
            raise PublicationAdmissionError("Exact nuget branch grants differ from current active authority.")

    # WHY: preserve an existing protected-branches-only environment, but never
    # mistake that setting for proof that the actual active sources are protected.
    for source_ref in sorted(active):
        branch_name = source_ref.removeprefix("refs/heads/")
        branch = api.request(f"repos/{repository}/branches/{quote(branch_name, safe='')}")
        if not isinstance(branch, dict) or branch.get("name") != branch_name or branch.get("protected") is not True:
            raise PublicationAdmissionError("An active publication source branch is not protected.")

    if line["sourceRef"] not in active:
        raise PublicationAdmissionError("The selected source is not currently active.")


def hosted_identity(environment=None) -> dict:
    """Bind operations to the actual dispatched workflow/run/ref and immutable SHA."""
    env = os.environ if environment is None else environment
    repository, source_ref = env.get("GITHUB_REPOSITORY"), env.get("GITHUB_REF")
    if not isinstance(repository, str) or lines.REPOSITORY.fullmatch(repository) is None:
        raise PublicationAdmissionError("Hosted repository identity is invalid.")

    source_commit = _sha(env.get("GITHUB_SHA"), "Hosted source commit", SHA1)
    run_id = _number(env.get("GITHUB_RUN_ID"), "Hosted workflow run")
    attempt = _number(env.get("GITHUB_RUN_ATTEMPT"), "Hosted workflow attempt")
    workflow = f"{repository}/{lines.WORKFLOW_PATH}@{source_ref}"
    if (env.get("GITHUB_ACTIONS") != "true" or env.get("GITHUB_EVENT_NAME") != "workflow_dispatch"
            or env.get("GITHUB_WORKFLOW_REF") != workflow
            or env.get("GITHUB_WORKFLOW_SHA", source_commit) != source_commit
            or not isinstance(source_ref, str) or not source_ref.startswith("refs/heads/")):
        raise PublicationAdmissionError("Publication must run the exact dispatched branch-owned workflow.")

    return {"repository": repository, "sourceRef": source_ref, "sourceCommit": source_commit,
            "workflowPath": lines.WORKFLOW_PATH, "workflowRunId": run_id, "currentAttempt": attempt}


def require_lock(environment=None) -> None:
    """Require the shared non-cancelling publisher exclusion supplied by trusted YAML."""
    env = os.environ if environment is None else environment
    if (env.get("GITHUB_ACTIONS") != "true"
            or env.get("SAFE_MIGRATIONS_PUBLICATION_LOCK_GROUP") != PUBLICATION_LOCK
            or env.get("SAFE_MIGRATIONS_PUBLICATION_LOCK_HELD") != "1"):
        raise PublicationAdmissionError("Publication requires the shared safe-migrations-publication lock.")


def validate_frozen(value: Any) -> dict:
    """Verify a frozen qualification independently of later authority changes."""
    lines.exact_object(value, FROZEN_FIELDS, "Frozen qualification")
    if (type(value["schemaVersion"]) is not int or value["schemaVersion"] != 1
            or value["kind"] != "safe-migrations-release-qualification"
            or value["workflowPath"] != lines.WORKFLOW_PATH
            or type(value["qualifiedForPublication"]) is not bool
            or value["tag"] != f"v{value['version']}"):
        raise PublicationAdmissionError("Frozen qualification identity is invalid.")

    _sha(value["sourceCommit"], "Frozen source commit", SHA1)
    _number(value["workflowRunId"], "Frozen workflow run")
    _number(value["qualificationAttempt"], "Frozen qualification attempt")
    _timestamp(value["qualifiedAtUtc"])
    authority = validate_authority(value["authority"])
    snapshot = lines.validate_snapshot(value["releaseLine"], source_ref=value["sourceRef"], version=value["version"],
                                       workflow_path=value["workflowPath"], repository=value["repository"])
    if (authority["repository"] != value["repository"]
            or snapshot["authorizationDigest"] != authority["authorizationDigest"]
            or snapshot["authorizationRevision"] != authority["authorizationRevision"]
            or lines.freeze_line(authority["policy"], value["sourceRef"], value["version"]) != snapshot):
        raise PublicationAdmissionError("Frozen qualification is not bound to its independently observed authority.")

    if value["qualifiedForPublication"] and snapshot["stateAtQualification"] != "active":
        raise PublicationAdmissionError("Prepared qualification cannot be relabelled as publishable.")

    lines.validate_toolchain(value["toolchain"], snapshot)

    return value


def _read_frozen(path: Path, expected_hash: str) -> dict:
    """Bind downloaded line bytes to the successful producer output, not filename."""
    _sha(expected_hash, "Frozen artifact digest")
    if file_digest(path) != expected_hash:
        raise PublicationAdmissionError("Downloaded qualification bytes differ from the preflight output.")

    return validate_frozen(lines.read_json(path))


def _bind_host(frozen: dict, environment=None) -> dict:
    """Retain the original run/source identity across nondecreasing retry attempts."""
    identity = hosted_identity(environment)
    for key in ("repository", "sourceRef", "sourceCommit", "workflowPath", "workflowRunId"):
        if identity[key] != frozen[key]:
            raise PublicationAdmissionError("The current workflow cannot borrow another candidate identity.")

    if int(identity["currentAttempt"]) < int(frozen["qualificationAttempt"]):
        raise PublicationAdmissionError("A retry cannot precede its qualification attempt.")

    return identity


def create_qualification(version: str, qualification_only: bool, repo: Path, api: GitHubClient,
                         environment=None) -> dict:
    """Qualify exact protected source/toolchain while leaving publisher permission revocable."""
    identity = hosted_identity(environment)
    authority = observe_authority(identity["repository"], api)
    line = lines.select_line(authority["policy"], identity["sourceRef"], version)
    if line["state"] == "prepared" and not qualification_only:
        raise PublicationAdmissionError("Prepared lines require qualification-only execution.")

    branch_name = identity["sourceRef"].removeprefix("refs/heads/")
    branch = api.request(f"repos/{identity['repository']}/branches/{quote(branch_name, safe='')}")
    if (not isinstance(branch, dict) or branch.get("name") != branch_name or branch.get("protected") is not True
            or _commit(branch) != identity["sourceCommit"]):
        raise PublicationAdmissionError("Qualification requires the exact current protected source head.")

    run = api.request(f"repos/{identity['repository']}/actions/runs/{identity['workflowRunId']}")
    if (not isinstance(run, dict) or run.get("id") != int(identity["workflowRunId"])
            or run.get("head_sha") != identity["sourceCommit"] or run.get("head_branch") != branch_name
            or run.get("path") != lines.WORKFLOW_PATH or run.get("event") != "workflow_dispatch"
            or run.get("run_attempt") != int(identity["currentAttempt"])):
        raise PublicationAdmissionError("Hosted qualification run identity is inconsistent.")

    result = subprocess.run(["git", "-C", str(repo), "rev-parse", "HEAD"], capture_output=True, text=True, check=False)
    if result.returncode != 0 or result.stdout.strip() != identity["sourceCommit"]:
        raise PublicationAdmissionError("Checked-out source differs from the dispatched source commit.")

    snapshot = lines.freeze_line(authority["policy"], identity["sourceRef"], version)
    value = {key: identity[key] for key in ("repository", "sourceRef", "sourceCommit", "workflowPath", "workflowRunId")}
    value.update(schemaVersion=1, kind="safe-migrations-release-qualification", version=version, tag=f"v{version}",
                 qualificationAttempt=identity["currentAttempt"], releaseLine=snapshot,
                 toolchain=lines.inspect_toolchain(repo, snapshot), authority=authority,
                 qualifiedForPublication=not qualification_only, qualifiedAtUtc=utc_now())

    return validate_frozen(value)


def candidate_manifest(root: Path, version: str) -> dict[str, str]:
    """Bind the existing thirteen-asset byte contract without adding public assets."""
    lines.parse_version(version)
    paths = [f"packages/{package}.{version}.{suffix}" for package in PACKAGES for suffix in ("nupkg", "snupkg")]
    paths += ["packages/SHA256SUMS", "sbom/_manifest/spdx_2.2/manifest.spdx.json",
              "release-provenance/release-provenance.intoto.jsonl"]
    package_dir = root / "packages"
    actual = {str(path.relative_to(root)) for pattern in ("*.nupkg", "*.snupkg") for path in package_dir.glob(pattern)}
    if actual != set(paths[:10]):
        raise PublicationAdmissionError("Candidate archives differ from the exact five-package inventory.")

    return {name: file_digest(root / name) for name in paths}


def _artifact(value: dict, frozen: dict, *, name: str | None = None) -> dict:
    """Retain immutable same-run artifact identity, including archive digest and expiry."""
    workflow = value.get("workflow_run")
    digest = value.get("digest")
    if (type(value.get("id")) is not int or value["id"] <= 0 or value.get("expired") is not False
            or not isinstance(value.get("name"), str) or (name is not None and value["name"] != name)
            or not isinstance(digest, str) or not digest.startswith("sha256:")
            or not isinstance(workflow, dict) or workflow.get("id") != int(frozen["workflowRunId"])
            or workflow.get("head_sha") != frozen["sourceCommit"]):
        raise PublicationAdmissionError("Candidate artifact is missing, expired, or belongs to another source/run.")

    _sha(digest.removeprefix("sha256:"), "Artifact archive digest")

    return {"id": value["id"], "name": value["name"], "digest": digest,
            "workflowRunId": frozen["workflowRunId"], "sourceCommit": frozen["sourceCommit"]}


def resolve_artifact(name: str, frozen: dict, api: GitHubClient, inventory=None) -> dict:
    """Select exactly one producer-output artifact from a complete same-run inventory."""
    if not isinstance(name, str) or not name or len(name) > 255:
        raise PublicationAdmissionError("Qualified artifact name is invalid.")

    values = inventory if inventory is not None else api.pages(
        f"repos/{frozen['repository']}/actions/runs/{frozen['workflowRunId']}/artifacts", "artifacts")
    matches = [entry for entry in values if entry.get("name") == name]
    if len(matches) != 1:
        raise PublicationAdmissionError("Exact qualified artifact identity is unavailable or ambiguous.")

    return _artifact(matches[0], frozen, name=name)


def _expected_identity(frozen: dict, line_hash: str, manifest: dict, qualified: dict, provenance: dict,
                       original_attempt: str) -> dict:
    """Construct the one immutable finite admission subject."""
    identity = {key: frozen[key] for key in ("repository", "sourceRef", "sourceCommit", "version", "tag",
                                            "workflowPath", "workflowRunId", "qualificationAttempt")}
    identity.update(originalPublicationAttempt=original_attempt, releaseLineSha256=line_hash,
                    manifestSha256=canonical_digest(manifest), qualifiedArtifact=qualified,
                    provenanceArtifact=provenance)

    return identity


def validate_admission(value: Any) -> dict:
    """Validate receipt integrity independently from whether its rights remain active."""
    lines.exact_object(value, {"schemaVersion", "kind", "identity", "admissionId", "authority", "admittedUtc"},
                       "Publication admission")
    identity = lines.exact_object(value["identity"], IDENTITY_FIELDS, "Publication admission identity")
    if (type(value["schemaVersion"]) is not int or value["schemaVersion"] != 1
            or value["kind"] != "safe-migrations-publication-admission"
            or value["admissionId"] != canonical_digest(identity)
            or identity["tag"] != f"v{identity['version']}" or identity["workflowPath"] != lines.WORKFLOW_PATH):
        raise PublicationAdmissionError("Publication admission identity or digest is invalid.")

    lines.parse_version(identity["version"])
    _sha(identity["sourceCommit"], "Admission source commit", SHA1)
    for key in ("releaseLineSha256", "manifestSha256"):
        _sha(identity[key], key)

    for key in ("workflowRunId", "qualificationAttempt", "originalPublicationAttempt"):
        _number(identity[key], key)

    if int(identity["qualificationAttempt"]) > int(identity["originalPublicationAttempt"]):
        raise PublicationAdmissionError("Admission cannot precede its qualification.")

    if (not isinstance(identity["repository"], str) or lines.REPOSITORY.fullmatch(identity["repository"]) is None
            or not isinstance(identity["sourceRef"], str) or not identity["sourceRef"].startswith("refs/heads/")):
        raise PublicationAdmissionError("Admission repository/source identity is invalid.")

    for key in ("qualifiedArtifact", "provenanceArtifact"):
        artifact = lines.exact_object(identity[key], {"id", "name", "digest", "workflowRunId", "sourceCommit"},
                                      "Admitted artifact")
        if (type(artifact["id"]) is not int or artifact["id"] <= 0
                or not isinstance(artifact["name"], str) or not artifact["name"]
                or artifact["workflowRunId"] != identity["workflowRunId"]
                or artifact["sourceCommit"] != identity["sourceCommit"]
                or not isinstance(artifact["digest"], str) or not artifact["digest"].startswith("sha256:")):
            raise PublicationAdmissionError("Admitted artifact identity is invalid.")

        _sha(artifact["digest"].removeprefix("sha256:"), "Admitted artifact archive digest")

    _timestamp(value["admittedUtc"])
    validate_authority(value["authority"])
    if value["authority"]["repository"] != identity["repository"]:
        raise PublicationAdmissionError("Admission authority belongs to another repository.")

    return value


def prior_publication_is_unstarted(frozen: dict, current_attempt: str, api: GitHubClient) -> None:
    """Prove that missing admission is not being replaced after writes may have begun."""
    for attempt in range(1, int(current_attempt)):
        jobs = api.pages(
            f"repos/{frozen['repository']}/actions/runs/{frozen['workflowRunId']}/attempts/{attempt}/jobs", "jobs")
        publisher = [job for job in jobs if job.get("name") == PUBLISHER_JOB]
        if len(publisher) != 1 or publisher[0].get("status") != "completed":
            raise PublicationAdmissionError("Earlier publisher history is missing, ambiguous or still running.")

        job = publisher[0]
        steps = job.get("steps")
        if job.get("conclusion") == "skipped" and steps in (None, []):
            continue

        if not isinstance(steps, list):
            raise PublicationAdmissionError("Earlier publisher step history is unavailable.")

        if any(not isinstance(step, dict) or not isinstance(step.get("name"), str) for step in steps):
            raise PublicationAdmissionError("Earlier publisher step identities are malformed.")

        writes = {}
        for step in steps:
            name = step["name"]
            if name not in WRITE_STEPS:
                continue

            # WHY: A later skipped duplicate must not overwrite an earlier executed write.
            if name in writes:
                raise PublicationAdmissionError("Earlier publisher write-step identities are duplicated.")

            writes[name] = step

        # WHY: The runner starts each step's execution context before evaluating its condition.
        # Even skipped steps can have timestamps; the completed job and skipped conclusions
        # prove non-execution. Other/absent conclusions never authorize replacement admission.
        if set(writes) != WRITE_STEPS or any(step.get("conclusion") != "skipped" for step in writes.values()):
            raise PublicationAdmissionError("Earlier publication may have started; original admission is required.")


def restore_admission(frozen: dict, inventory: list[dict], api: GitHubClient) -> dict | None:
    """Restore the original immutable archive, never synthesize an expired admission."""
    prefix = f"{ADMISSION_PREFIX}{frozen['workflowRunId']}-"
    matches = [entry for entry in inventory if str(entry.get("name", "")).startswith(prefix)]
    if not matches:
        return None

    if len(matches) != 1:
        raise PublicationAdmissionError("Original publication admission inventory is ambiguous.")

    metadata = _artifact(matches[0], frozen)
    suffix = metadata["name"].removeprefix(prefix)
    _number(suffix, "Original publication artifact attempt")
    raw = api.download(f"repos/{frozen['repository']}/actions/artifacts/{metadata['id']}/zip")
    if hashlib.sha256(raw).hexdigest() != metadata["digest"].removeprefix("sha256:"):
        raise PublicationAdmissionError("Original admission archive digest differs from GitHub metadata.")

    try:
        with zipfile.ZipFile(io.BytesIO(raw)) as archive:
            entries = archive.infolist()
            if (len(entries) != 1 or entries[0].filename != "admission.json"
                    or entries[0].file_size > 1024 * 1024
                    or (entries[0].external_attr >> 16) & 0o170000 == 0o120000):
                raise PublicationAdmissionError("Original admission archive has an invalid inventory.")

            value = json.loads(archive.read(entries[0]), object_pairs_hook=lines.unique_object)
    except (ValueError, OSError, RuntimeError, zipfile.BadZipFile, UnicodeError) as error:
        raise PublicationAdmissionError("Original admission archive cannot be verified.") from error

    value = validate_admission(value)
    if value["identity"]["originalPublicationAttempt"] != suffix:
        raise PublicationAdmissionError("Original admission archive name differs from its frozen attempt.")

    return value


def _current_grant(frozen: dict, api: GitHubClient) -> dict:
    """Re-read current permission under the lock; frozen authority is not a fallback."""
    authority = observe_authority(frozen["repository"], api)
    line = lines.select_line(authority["policy"], frozen["sourceRef"], frozen["version"], purpose="publication")
    if line["workflowPath"] != frozen["workflowPath"]:
        raise PublicationAdmissionError("The original workflow is no longer authorized.")

    validate_environment(frozen["repository"], line, authority["policy"], api)
    branch_name = frozen["sourceRef"].removeprefix("refs/heads/")
    branch = api.request(f"repos/{frozen['repository']}/branches/{quote(branch_name, safe='')}")
    head = _commit(branch)
    _sha(head, "Current release source head", SHA1)
    comparison = api.request(f"repos/{frozen['repository']}/compare/{frozen['sourceCommit']}...{head}")
    base_commit = comparison.get("base_commit") if isinstance(comparison, dict) else None
    if (not isinstance(comparison, dict) or comparison.get("status") not in ("identical", "ahead")
            or not isinstance(base_commit, dict) or base_commit.get("sha") != frozen["sourceCommit"]):
        raise PublicationAdmissionError("Qualified source is no longer on its protected line history.")

    return authority


def admit(frozen: dict, line_hash: str, artifact_root: Path, qualified_name: str, provenance_name: str,
          api: GitHubClient, environment=None) -> tuple[dict, bool]:
    """Admit once or restore the same exact run/candidate after a failed publisher."""
    require_lock(environment)
    host = _bind_host(validate_frozen(frozen), environment)
    if frozen["qualifiedForPublication"] is not True:
        raise PublicationAdmissionError("Qualification-only candidates never request publication.")

    _sha(line_hash, "Frozen line digest")
    authority = _current_grant(frozen, api)
    inventory = api.pages(
        f"repos/{frozen['repository']}/actions/runs/{frozen['workflowRunId']}/artifacts", "artifacts")
    qualified = resolve_artifact(qualified_name, frozen, api, inventory)
    provenance = resolve_artifact(provenance_name, frozen, api, inventory)
    if qualified["id"] == provenance["id"]:
        raise PublicationAdmissionError("Qualified packages and provenance must retain separate producer identities.")

    manifest = candidate_manifest(artifact_root, frozen["version"])
    original = restore_admission(frozen, inventory, api)
    if original is not None:
        attempt = original["identity"]["originalPublicationAttempt"]
        if int(attempt) > int(host["currentAttempt"]):
            raise PublicationAdmissionError("A retry cannot precede its original publication admission.")

        expected = _expected_identity(frozen, line_hash, manifest, qualified, provenance, attempt)
        if original["identity"] != expected:
            raise PublicationAdmissionError(
                "Original admission does not permit rebuilt or substituted candidate bytes.")

        return original, True

    prior_publication_is_unstarted(frozen, host["currentAttempt"], api)
    identity = _expected_identity(frozen, line_hash, manifest, qualified, provenance, host["currentAttempt"])
    value = {"schemaVersion": 1, "kind": "safe-migrations-publication-admission", "identity": identity,
             "admissionId": canonical_digest(identity), "authority": authority, "admittedUtc": utc_now()}

    return validate_admission(value), False


def recheck(frozen: dict, line_hash: str, receipt: dict, artifact_root: Path, api: GitHubClient,
            environment=None) -> None:
    """Revalidate exact bytes and fresh permission immediately before irreversible writes."""
    require_lock(environment)
    host = _bind_host(validate_frozen(frozen), environment)
    receipt = validate_admission(receipt)
    if not frozen["qualifiedForPublication"]:
        raise PublicationAdmissionError("Qualification-only execution cannot publish.")

    _current_grant(frozen, api)
    identity = receipt["identity"]
    if int(identity["originalPublicationAttempt"]) > int(host["currentAttempt"]):
        raise PublicationAdmissionError("Current attempt precedes original publication admission.")

    artifacts = api.pages(
        f"repos/{frozen['repository']}/actions/runs/{frozen['workflowRunId']}/artifacts", "artifacts")
    qualified = resolve_artifact(identity["qualifiedArtifact"]["name"], frozen, api, artifacts)
    provenance = resolve_artifact(identity["provenanceArtifact"]["name"], frozen, api, artifacts)
    expected = _expected_identity(frozen, line_hash, candidate_manifest(artifact_root, frozen["version"]),
                                  qualified, provenance, identity["originalPublicationAttempt"])
    if expected != identity:
        raise PublicationAdmissionError("Publication identity or candidate bytes changed after admission.")


def _output(values: dict[str, str]) -> None:
    """Expose only finite canonical workflow outputs, never arbitrary remote text."""
    target = os.environ.get("GITHUB_OUTPUT")
    if target:
        with Path(target).open("a", encoding="ascii") as stream:
            for key, value in values.items():
                if "\n" in value or "\r" in value:
                    raise PublicationAdmissionError("Workflow output contains an invalid newline.")

                stream.write(f"{key}={value}\n")


def main() -> int:
    """Execute read-only qualification/admission checks around the existing publisher."""
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    preflight = commands.add_parser("preflight")
    preflight.add_argument("--version", required=True)
    preflight.add_argument("--qualification-only", choices=("true", "false"), required=True)
    preflight.add_argument("--output", type=Path, required=True)
    for name in ("admit", "recheck"):
        command = commands.add_parser(name)
        command.add_argument("--line-file", type=Path, required=True)
        command.add_argument("--line-sha256", required=True)
        command.add_argument("--artifact-dir", type=Path, required=True)
        if name == "admit":
            command.add_argument("--qualified-artifact-name", required=True)
            command.add_argument("--provenance-artifact-name", required=True)
            command.add_argument("--output", type=Path, required=True)
        else:
            command.add_argument("--admission", type=Path, required=True)
            command.add_argument("--admission-sha256", required=True)

    args = parser.parse_args()
    try:
        api = GitHubClient()
        if args.command == "preflight":
            value = create_qualification(args.version, args.qualification_only == "true", Path.cwd(), api)
            write_json(args.output, value)
            _output({"source-ref": value["sourceRef"],
                     "line-artifact-name": f"safe-migrations-release-line-{value['workflowRunId']}-"
                                           f"{value['qualificationAttempt']}",
                     "line-sha256": file_digest(args.output),
                     "publish-allowed": str(value["qualifiedForPublication"]).lower()})
        else:
            frozen = _read_frozen(args.line_file, args.line_sha256)
            if args.command == "admit":
                value, restored = admit(frozen, args.line_sha256, args.artifact_dir, args.qualified_artifact_name,
                                        args.provenance_artifact_name, api)
                write_json(args.output, value)
                identity = value["identity"]
                _output({"admission-artifact-name": f"{ADMISSION_PREFIX}{identity['workflowRunId']}-"
                                                    f"{identity['originalPublicationAttempt']}",
                         "admission-sha256": file_digest(args.output),
                         "admission-restored": str(restored).lower()})
            else:
                _sha(args.admission_sha256, "Original admission file digest")
                if file_digest(args.admission) != args.admission_sha256:
                    raise PublicationAdmissionError("Admission bytes changed after the admitting step.")

                recheck(frozen, args.line_sha256, lines.read_json(args.admission), args.artifact_dir, api)

        print(f"Release-line {args.command} verified.")
    except (PublicationAdmissionError, lines.LinePolicyError, OSError) as error:
        print(f"Release-line {args.command} failed: {error}", file=sys.stderr)

        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
