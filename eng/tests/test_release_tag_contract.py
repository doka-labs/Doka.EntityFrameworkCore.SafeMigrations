"""Release-tag trust and pre-allocation contract tests."""

from __future__ import annotations

import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import unittest


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
ALLOWED_SIGNERS = REPOSITORY_ROOT / "eng" / "release" / "allowed-signers"
PRE_TAG_CHECK = REPOSITORY_ROOT / "eng" / "release" / "pre-tag-check.sh"
PRE_TAG_ENTRYPOINT = REPOSITORY_ROOT / "eng" / "pre-tag-check.sh"
VERIFY_TAG = REPOSITORY_ROOT / "eng" / "release" / "verify-tag.sh"
VERIFY_MAIN_SOURCE = REPOSITORY_ROOT / "eng" / "release" / "verify-main-source.sh"
VERSION = "10.0.0-rc.1"
COMMIT = "a" * 40
RUN_ID = "12345"
PACKAGE_PRODUCER = "Full reversible qualification / Core, performance, packages, and SBOM"
ATTESTATION_PRODUCER = "Attest qualified candidate"
PUBLISH_JOB = "Verify tag, publish, and read back"


def isolated_environment() -> dict[str, str]:
    """Keep runner file-command sinks and user shell startup scripts outside fixtures."""

    return {
        key: value for key, value in os.environ.items()
        if not key.startswith(("GIT_", "GITHUB_")) and key not in ("BASH_ENV", "ENV")
    }


def qualified_run(**overrides: object) -> str:
    """Return one hosted run readback with optional contract deviations."""

    run = {
        "id": int(RUN_ID),
        "event": "workflow_dispatch",
        "path": ".github/workflows/release-candidate.yml",
        "head_branch": "main",
        "head_sha": COMMIT,
        "status": "waiting",
        "conclusion": None,
        "run_attempt": 1,
    }
    run.update(overrides)

    return json.dumps(run)


def workflow_job(name: str, attempt: int = 1, **overrides: object) -> dict[str, object]:
    """Model documented workflow-job identity without inventing YAML job IDs."""

    job = {
        "name": name,
        "run_id": int(RUN_ID),
        "head_sha": COMMIT,
        "run_attempt": attempt,
        "status": "completed",
        "conclusion": "success",
    }
    job.update(overrides)

    return job


def qualified_jobs(
    *,
    publish_status: str = "waiting",
    qualification: str = "success",
    package_attempt: int = 1,
    attestation_attempt: int = 1,
    run_attempt: int = 1,
) -> str:
    """Return the required completed qualification and protected publish job."""

    return json.dumps(
        {
            "jobs": [
                workflow_job(PACKAGE_PRODUCER, package_attempt, conclusion=qualification),
                workflow_job(ATTESTATION_PRODUCER, attestation_attempt),
                workflow_job(PUBLISH_JOB, run_attempt, status=publish_status, conclusion=None),
            ],
            "total_count": 3,
        }
    )


def qualified_artifacts(
    *,
    expired: bool = False,
    include_attestations: bool = True,
    package_attempt: int = 1,
    attestation_attempt: int = 1,
) -> str:
    """Return the attempt-qualified package and attestation artifacts."""

    identity = {"id": int(RUN_ID), "head_sha": COMMIT, "head_branch": "main"}
    artifacts = [
        {
            "name": f"safe-migrations-release-{VERSION}-{package_attempt}",
            "expired": expired,
            "workflow_run": identity,
        }
    ]
    if include_attestations:
        artifacts.append(
            {
                "name": f"safe-migrations-attestations-{VERSION}-{attestation_attempt}",
                "expired": expired,
                "workflow_run": identity,
            }
        )

    return json.dumps({"artifacts": artifacts, "total_count": len(artifacts)})


def paginated_readback(field: str, items: list[dict[str, object]]) -> str:
    """Emit the documented page stream with the requested per_page=100 boundary."""

    return "\n".join(
        json.dumps({field: items[index:index + 100], "total_count": len(items)})
        for index in range(0, len(items), 100)
    )


def other_jobs(count: int) -> list[dict[str, object]]:
    return [workflow_job(f"Full reversible qualification / Additional check {index}") for index in range(count)]


def other_artifacts(count: int) -> list[dict[str, object]]:
    artifact = json.loads(qualified_artifacts())["artifacts"][0]

    return [{**artifact, "name": f"diagnostic-{index}"} for index in range(count)]


class ReleaseTagContractTests(unittest.TestCase):
    """Exercises authorized tag verification and the hosted waiting-state gate."""

    def test_preparation_checks_current_main_and_signing_without_a_candidate_run(self) -> None:
        for entrypoint in (PRE_TAG_CHECK, PRE_TAG_ENTRYPOINT):
            with self.subTest(entrypoint=entrypoint), self.fake_release_environment() as environment:
                result = self.run_script(entrypoint, environment=environment)

                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertIn(f"Commit {COMMIT} is ready for untagged qualification", result.stdout)
                self.assertIn("do not create a tag yet", result.stdout)
                self.assertNotIn("Verified qualified commit", result.stdout)
                calls = Path(environment["FAKE_GH_CALLS"]).read_text(encoding="utf-8")
                self.assertIn("user/ssh_signing_keys?per_page=100 --paginate", calls)
                self.assertNotIn("/actions/runs", calls)

    def test_preparation_rejects_incomplete_source_or_signing_readiness(self) -> None:
        cases = (
            ({"FAKE_DIRTY": "true"}, "worktree must be clean"),
            ({"FAKE_MAIN_COMMIT": "b" * 40}, "Preparation requires"),
            ({"FAKE_LOCAL_BRANCH": "feature/not-main"}, "Preparation requires"),
            ({"FAKE_LOCAL_BRANCH": ""}, "Preparation requires"),
            ({"FAKE_ANCESTOR": "false"}, "not an ancestor"),
            ({"FAKE_GPG_FORMAT": "openpgp"}, "must use SSH signing"),
            ({"FAKE_TAG_GPG_SIGN": "false"}, "must use SSH signing"),
            ({"FAKE_PRINCIPAL": "unauthorized@example.invalid"}, "not authorized"),
            ({"FAKE_SIGNING_KEY": "/missing/signing-key.pub"}, "identity is incomplete"),
            ({"FAKE_SIGNING_KEYS_JSON": "[]"}, "not registered"),
        )
        for overrides, expected in cases:
            with self.subTest(overrides=overrides), self.fake_release_environment(overrides) as environment:
                result = self.run_script(PRE_TAG_ENTRYPOINT, environment=environment)

                self.assertEqual(result.returncode, 1, result.stdout)
                self.assertIn(expected, result.stderr)
                self.assertNotIn("ready for untagged qualification", result.stdout)

    def test_incomplete_arguments_never_fall_back_to_preparation(self) -> None:
        cases = (
            ("--version",),
            ("--version", VERSION),
            ("--commit", COMMIT),
            ("--run-id", RUN_ID),
            ("--version", VERSION, "--commit", COMMIT),
            ("--version", VERSION, "--commit", COMMIT, "--run-id"),
            ("--version", VERSION, "--commit", COMMIT, "--run-id", ""),
            ("--version", VERSION, "--commit", COMMIT, "--run-id", "0"),
            ("--version", VERSION, "--commit", COMMIT, "--run-id", "1;false"),
            ("--version", VERSION, "--version", VERSION, "--run-id", RUN_ID),
            ("--version", VERSION, "--commit", COMMIT, "--unknown", RUN_ID),
            ("--version", VERSION, "--commit", COMMIT, "--run-id", RUN_ID, "extra"),
        )
        for arguments in cases:
            with self.subTest(arguments=arguments), self.fake_release_environment() as environment:
                result = self.run_script(PRE_TAG_ENTRYPOINT, *arguments, environment=environment)

                self.assertEqual(result.returncode, 2, result.stdout)
                self.assertIn("Usage:", result.stderr)
                self.assertFalse(Path(environment["FAKE_GH_CALLS"]).exists())

    def test_pre_tag_check_accepts_only_the_exact_qualified_waiting_run(self) -> None:
        with self.fake_release_environment() as environment:
            result = self.run_script(
                PRE_TAG_CHECK,
                "--version",
                VERSION,
                "--commit",
                COMMIT,
                "--run-id",
                RUN_ID,
                environment=environment,
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn(f"ready for signed tag v{VERSION}", result.stdout)

    def test_pre_tag_check_accepts_the_documented_main_workflow_path_form(self) -> None:
        """GitHub's workflow-run example uses .github/workflows/build.yml@main."""

        for path in (".github/workflows/release-candidate.yml", ".github/workflows/release-candidate.yml@main"):
            overrides = {"FAKE_RUN_JSON": qualified_run(path=path)}
            with self.subTest(path=path), self.fake_release_environment(overrides) as environment:
                result = self.run_pre_tag(environment)

                self.assertEqual(result.returncode, 0, result.stderr)

    def test_pre_tag_check_rejects_other_workflow_paths_and_refs(self) -> None:
        paths = (
            ".github/workflows/ci.yml@main",
            ".github/workflows/release-candidate.yml@release",
            ".github/workflows/release-candidate.yml@refs/heads/release",
            ".github/workflows/release-candidate.yml@refs/tags/main",
            ".github/workflows/release-candidate.yml@refs/heads/main",
            f".github/workflows/release-candidate.yml@{COMMIT}",
            ".github/workflows/release-candidate.yml@main@release",
            ".github/workflows/release-candidate.yml@main/other",
            ".github/workflows/release-candidate.yml@",
            ".github/workflows/release-candidate.yml@main\n",
            "other/.github/workflows/release-candidate.yml@main",
        )
        for path in paths:
            overrides = {"FAKE_RUN_JSON": qualified_run(path=path)}
            with self.subTest(path=path), self.fake_release_environment(overrides) as environment:
                result = self.run_pre_tag(environment)

                self.assertEqual(result.returncode, 1, result.stdout)
                self.assertIn("does not identify the waiting qualified main commit", result.stderr)

    def test_package_producer_name_tracks_both_reusable_workflow_job_names(self) -> None:
        names = []
        for filename, job_id in (("release-candidate.yml", "quality-gates"), ("quality-gates.yml", "package-and-core")):
            source = (REPOSITORY_ROOT / ".github" / "workflows" / filename).read_text(encoding="utf-8")
            job = re.search(rf"(?ms)^  {re.escape(job_id)}:\n(.*?)(?=^  \S|\Z)", source)
            self.assertIsNotNone(job, f"Missing workflow job: {filename}/{job_id}")
            assert job is not None
            job_names = re.findall(r"(?m)^    name: (.+)$", job.group(1))
            self.assertEqual(len(job_names), 1, f"Expected one plain job name: {filename}/{job_id}")
            names.append(job_names[0])

        expected = " / ".join(names)
        self.assertEqual(PACKAGE_PRODUCER, expected)
        self.assertIn(f'package_producer="{expected}"', PRE_TAG_CHECK.read_text(encoding="utf-8"))

    def test_pre_tag_check_rejects_dirty_untrusted_or_not_waiting_states(self) -> None:
        cases = (
            ({"FAKE_DIRTY": "true"}, "worktree must be clean"),
            ({"FAKE_HEAD_COMMIT": "b" * 40}, "local checkout does not identify"),
            ({"FAKE_ANCESTOR": "false"}, "not an ancestor of current origin/main"),
            ({"FAKE_LOCAL_TAG": "true"}, "Release tag already exists"),
            ({"FAKE_REMOTE_TAG": "present"}, "Remote release tag already exists"),
            ({"FAKE_GPG_FORMAT": "openpgp"}, "must use SSH signing"),
            ({"FAKE_TAG_GPG_SIGN": "false"}, "must use SSH signing"),
            ({"FAKE_PRINCIPAL": "unauthorized@example.invalid"}, "not authorized"),
            ({"FAKE_SIGNING_KEY": "/missing/signing-key.pub"}, "identity is incomplete"),
            ({"FAKE_SIGNING_KEYS_JSON": "[]"}, "not registered"),
            (
                {"FAKE_RUN_JSON": qualified_run(event="push")},
                "does not identify the waiting qualified main commit",
            ),
            (
                {"FAKE_RUN_JSON": qualified_run(id=98765)},
                "does not identify the waiting qualified main commit",
            ),
            (
                {"FAKE_RUN_JSON": qualified_run(head_branch="release")},
                "does not identify the waiting qualified main commit",
            ),
            (
                {"FAKE_RUN_JSON": qualified_run(run_attempt=0)},
                "does not identify the waiting qualified main commit",
            ),
            (
                {"FAKE_RUN_JSON": qualified_run(run_attempt=1.5)},
                "does not identify the waiting qualified main commit",
            ),
            (
                {"FAKE_RUN_JSON": qualified_run(path=".github/workflows/ci.yml")},
                "does not identify the waiting qualified main commit",
            ),
            (
                {"FAKE_RUN_JSON": qualified_run(head_sha="b" * 40)},
                "does not identify the waiting qualified main commit",
            ),
            (
                {"FAKE_RUN_JSON": qualified_run(status="completed", conclusion="success")},
                "does not identify the waiting qualified main commit",
            ),
            (
                {"FAKE_JOBS_JSON": qualified_jobs(qualification="failure")},
                "not in the required qualified-and-waiting state",
            ),
            (
                {"FAKE_JOBS_JSON": qualified_jobs(publish_status="in_progress")},
                "not in the required qualified-and-waiting state",
            ),
            (
                {"FAKE_ARTIFACTS_JSON": qualified_artifacts(include_attestations=False)},
                "does not expose the exact qualified package and attestation artifacts",
            ),
            (
                {"FAKE_ARTIFACTS_JSON": qualified_artifacts(expired=True)},
                "does not expose the exact qualified package and attestation artifacts",
            ),
        )

        for overrides, expected in cases:
            with self.subTest(expected=expected), self.fake_release_environment(overrides) as environment:
                result = self.run_script(
                    PRE_TAG_CHECK,
                    "--version",
                    VERSION,
                    "--commit",
                    COMMIT,
                    "--run-id",
                    RUN_ID,
                    environment=environment,
                )

                self.assertEqual(result.returncode, 1)
                self.assertIn(expected, result.stderr)

    def test_pre_tag_check_uses_each_successful_producer_attempt(self) -> None:
        for package_attempt, attestation_attempt, run_attempt in ((1, 2, 2), (1, 1, 2), (2, 2, 2), (1, 2, 3)):
            jobs = json.loads(qualified_jobs(
                package_attempt=package_attempt,
                attestation_attempt=attestation_attempt,
                run_attempt=run_attempt,
            ))["jobs"]
            if package_attempt == 2:
                jobs.append(workflow_job(PACKAGE_PRODUCER, conclusion="failure"))
            if attestation_attempt == 2:
                jobs.append(workflow_job(ATTESTATION_PRODUCER, conclusion="failure"))
            jobs.append(workflow_job(PUBLISH_JOB, status="completed", conclusion="failure"))
            artifacts = json.loads(qualified_artifacts(
                package_attempt=package_attempt,
                attestation_attempt=attestation_attempt,
            ))["artifacts"]
            overrides = {
                "FAKE_RUN_JSON": qualified_run(run_attempt=run_attempt),
                "FAKE_JOBS_JSON": paginated_readback("jobs", other_jobs(100) + jobs),
                "FAKE_ARTIFACTS_JSON": paginated_readback("artifacts", other_artifacts(100) + artifacts),
            }
            with self.subTest(attempts=(package_attempt, attestation_attempt, run_attempt)):
                with self.fake_release_environment(overrides) as environment:
                    result = self.run_pre_tag(environment)

                    self.assertEqual(result.returncode, 0, result.stderr)
                    calls = Path(environment["FAKE_GH_CALLS"]).read_text(encoding="utf-8")
                    self.assertIn("jobs?filter=all&per_page=100 --paginate", calls)
                    self.assertIn("artifacts?per_page=100 --paginate", calls)

    def test_pre_tag_check_rejects_missing_ambiguous_or_failed_latest_jobs(self) -> None:
        base = json.loads(qualified_jobs(run_attempt=2, attestation_attempt=2))["jobs"]
        cases = {
            "missing package producer": base[1:],
            "missing attestation producer": [base[0], base[2]],
            "missing publish job": base[:2],
            "duplicate package producer": base + [base[0]],
            "duplicate attestation producer": base + [base[1]],
            "duplicate publish job": base + [base[2]],
            "newer failed package producer": base + [workflow_job(PACKAGE_PRODUCER, 2, conclusion="failure")],
            "newer pending package producer": base + [
                workflow_job(PACKAGE_PRODUCER, 2, status="in_progress", conclusion=None)
            ],
            "future producer": base + [workflow_job(PACKAGE_PRODUCER, 3)],
            "zero producer attempt": [{**base[0], "run_attempt": 0}, *base[1:]],
            "fractional producer attempt": [{**base[0], "run_attempt": 1.5}, *base[1:]],
            "wrong producer run": [{**base[0], "run_id": 98765}, *base[1:]],
            "wrong producer commit": [{**base[0], "head_sha": "b" * 40}, *base[1:]],
            "missing producer attempt": [
                {key: value for key, value in base[0].items() if key != "run_attempt"}, *base[1:]
            ],
            "stale waiting publish": [*base[:2], {**base[2], "run_attempt": 1}],
            "failed other qualification": base + [
                workflow_job("Full reversible qualification / PostgreSQL 18.4", 2, conclusion="failure")
            ],
        }
        for description, jobs in cases.items():
            overrides = {
                "FAKE_RUN_JSON": qualified_run(run_attempt=2),
                "FAKE_JOBS_JSON": paginated_readback("jobs", other_jobs(97) + jobs),
                "FAKE_ARTIFACTS_JSON": qualified_artifacts(attestation_attempt=2),
            }
            with self.subTest(description=description), self.fake_release_environment(overrides) as environment:
                result = self.run_pre_tag(environment)

                self.assertEqual(result.returncode, 1, result.stdout)
                self.assertIn("not in the required qualified-and-waiting state", result.stderr)

    def test_pre_tag_check_rejects_artifacts_not_bound_to_their_producer(self) -> None:
        base = json.loads(qualified_artifacts(attestation_attempt=2))["artifacts"]
        cases = {
            "current attempt package without producer": [
                {**base[0], "name": f"safe-migrations-release-{VERSION}-2"}, base[1]
            ],
            "stale attestations": [base[0], {**base[1], "name": f"safe-migrations-attestations-{VERSION}-1"}],
            "future attestations": [base[0], {**base[1], "name": f"safe-migrations-attestations-{VERSION}-3"}],
            "duplicate package on later page": base + [base[0]],
            "duplicate attestations on later page": base + [base[1]],
            "expired duplicate": base + [{**base[0], "expired": True}],
            "expired package": [{**base[0], "expired": True}, base[1]],
            "expired attestations": [base[0], {**base[1], "expired": True}],
            "missing workflow identity": [{**base[0], "workflow_run": None}, base[1]],
        }
        for index, producer in enumerate(("package", "attestations")):
            for field, value in (("id", 98765), ("head_sha", "b" * 40), ("head_branch", "release")):
                artifacts = base.copy()
                artifacts[index] = {**base[index], "workflow_run": {**base[index]["workflow_run"], field: value}}
                cases[f"{producer} wrong {field}"] = artifacts

        for description, artifacts in cases.items():
            overrides = {
                "FAKE_RUN_JSON": qualified_run(run_attempt=2),
                "FAKE_JOBS_JSON": qualified_jobs(run_attempt=2, attestation_attempt=2),
                "FAKE_ARTIFACTS_JSON": paginated_readback("artifacts", other_artifacts(98) + artifacts),
            }
            with self.subTest(description=description), self.fake_release_environment(overrides) as environment:
                result = self.run_pre_tag(environment)

                self.assertEqual(result.returncode, 1, result.stdout)
                self.assertIn("does not expose the exact qualified package and attestation artifacts", result.stderr)

    def test_signing_registration_is_paginated(self) -> None:
        with self.fake_release_environment() as environment:
            first_page = json.dumps([{"key": f"unrelated-key-{index}"} for index in range(100)])
            environment["FAKE_SIGNING_KEYS_JSON"] = first_page + "\n" + environment["FAKE_SIGNING_KEYS_JSON"]
            result = self.run_pre_tag(environment)

            self.assertEqual(result.returncode, 0, result.stderr)

    def test_verify_tag_accepts_only_annotated_authorized_direct_tags(self) -> None:
        with self.fake_release_environment() as environment:
            result = self.run_script(
                VERIFY_TAG,
                f"v{VERSION}",
                COMMIT,
                environment=environment,
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn("Verified authorized release tag", result.stdout)

        cases = (
            ({"FAKE_TAG_TYPE": "commit"}, "annotated tag object"),
            ({"FAKE_TAG_COMMIT": "b" * 40}, "does not identify"),
            ({"FAKE_VERIFY_TAG_FAIL": "true"}, "signature rejected"),
        )
        for overrides, expected in cases:
            with self.subTest(expected=expected), self.fake_release_environment(overrides) as environment:
                result = self.run_script(
                    VERIFY_TAG,
                    f"v{VERSION}",
                    COMMIT,
                    environment=environment,
                )

                self.assertEqual(result.returncode, 1)
                self.assertIn(expected, result.stderr)

    def fake_release_environment(self, overrides: dict[str, str] | None = None):
        return FakeReleaseEnvironment(overrides or {})

    def run_pre_tag(self, environment: dict[str, str]) -> subprocess.CompletedProcess[str]:
        return self.run_script(
            PRE_TAG_CHECK, "--version", VERSION, "--commit", COMMIT, "--run-id", RUN_ID,
            environment=environment,
        )

    @staticmethod
    def run_script(
        script: Path,
        *arguments: str,
        environment: dict[str, str],
    ) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            ["bash", str(script), *arguments],
            cwd=REPOSITORY_ROOT,
            env=environment,
            check=False,
            capture_output=True,
            text=True,
        )


class FakeReleaseEnvironment:
    """Provides deterministic Git and GitHub readbacks without external mutation."""

    def __init__(self, overrides: dict[str, str]) -> None:
        self._overrides = overrides
        self._temporary_directory: tempfile.TemporaryDirectory[str] | None = None

    def __enter__(self) -> dict[str, str]:
        self._temporary_directory = tempfile.TemporaryDirectory()
        root = Path(self._temporary_directory.name)
        bin_directory = root / "bin"
        bin_directory.mkdir()

        principal, key_type, key_data, *_ = ALLOWED_SIGNERS.read_text(encoding="utf-8").split()
        signing_key = root / "release-signing-key.pub"
        signing_key.write_text(f"{key_type} {key_data} test\n", encoding="utf-8")

        self.write_executable(bin_directory / "git", self.git_stub())
        self.write_executable(bin_directory / "gh", self.gh_stub())

        environment = isolated_environment()
        environment.update(
            {
                "PATH": f"{bin_directory}{os.pathsep}{environment['PATH']}",
                "FAKE_COMMIT": COMMIT,
                "FAKE_HEAD_COMMIT": COMMIT,
                "FAKE_MAIN_COMMIT": COMMIT,
                "FAKE_LOCAL_BRANCH": "main",
                "FAKE_ANCESTOR": "true",
                "FAKE_GH_CALLS": str(root / "gh-calls.log"),
                "FAKE_DIRTY": "false",
                "FAKE_LOCAL_TAG": "false",
                "FAKE_GPG_FORMAT": "ssh",
                "FAKE_TAG_GPG_SIGN": "true",
                "FAKE_PRINCIPAL": principal,
                "FAKE_SIGNING_KEY": str(signing_key),
                "FAKE_SIGNING_KEYS_JSON": json.dumps(
                    [{"key": f"{key_type} {key_data}"}]
                ),
                "FAKE_RUN_JSON": qualified_run(),
                "FAKE_JOBS_JSON": qualified_jobs(),
                "FAKE_ARTIFACTS_JSON": qualified_artifacts(),
                "FAKE_REMOTE_TAG": "",
                "FAKE_TAG_TYPE": "tag",
                "FAKE_TAG_COMMIT": COMMIT,
                "FAKE_VERIFY_TAG_FAIL": "false",
            }
        )
        environment.update(self._overrides)

        return environment

    def __exit__(self, *args: object) -> None:
        assert self._temporary_directory is not None
        self._temporary_directory.cleanup()

    @staticmethod
    def write_executable(path: Path, content: str) -> None:
        path.write_text(content, encoding="utf-8")
        path.chmod(0o755)

    @staticmethod
    def git_stub() -> str:
        return """#!/usr/bin/env bash
set -euo pipefail

while [[ "${1:-}" == "-C" || "${1:-}" == "-c" ]]; do
    shift 2
done

command="${1:-}"
shift || true
case "$command" in
    status)
        if [[ "$FAKE_DIRTY" == "true" ]]; then
            echo " M dirty"
        fi
        ;;
    rev-parse)
        case "${*: -1}" in
            HEAD) echo "$FAKE_HEAD_COMMIT" ;;
            refs/remotes/origin/main) echo "$FAKE_MAIN_COMMIT" ;;
            *) echo "$FAKE_COMMIT" ;;
        esac
        ;;
    fetch)
        ;;
    symbolic-ref)
        [[ -n "$FAKE_LOCAL_BRANCH" ]] || exit 1
        echo "$FAKE_LOCAL_BRANCH"
        ;;
    merge-base)
        [[ "$FAKE_ANCESTOR" == "true" ]]
        ;;
    show-ref)
        if [[ "$FAKE_LOCAL_TAG" == "true" ]]; then
            exit 0
        fi
        exit 1
        ;;
    ls-remote)
        if [[ -n "$FAKE_REMOTE_TAG" ]]; then
            echo "$FAKE_COMMIT refs/tags/v10.0.0-rc.1"
        fi
        ;;
    config)
        case "$*" in
            "--get gpg.format") echo "$FAKE_GPG_FORMAT" ;;
            "--bool --get tag.gpgSign") echo "$FAKE_TAG_GPG_SIGN" ;;
            "--get user.email") echo "$FAKE_PRINCIPAL" ;;
            "--path --get user.signingkey") echo "$FAKE_SIGNING_KEY" ;;
            *) exit 1 ;;
        esac
        ;;
    cat-file)
        echo "$FAKE_TAG_TYPE"
        ;;
    rev-list)
        echo "$FAKE_TAG_COMMIT"
        ;;
    verify-tag)
        if [[ "$FAKE_VERIFY_TAG_FAIL" == "true" ]]; then
            echo "signature rejected" >&2
            exit 1
        fi
        ;;
    *)
        echo "Unexpected fake git command: $command $*" >&2
        exit 99
        ;;
esac
"""

    @staticmethod
    def gh_stub() -> str:
        return """#!/usr/bin/env bash
set -euo pipefail

case "${1:-}" in
    repo)
        echo "doka-labs/Doka.EntityFrameworkCore.SafeMigrations"
        ;;
    api)
        printf '%s\\n' "$*" >> "$FAKE_GH_CALLS"
        endpoint="${2:-}"
        case "$endpoint" in
            user/ssh_signing_keys*) response="$FAKE_SIGNING_KEYS_JSON" ;;
            */actions/runs/12345/jobs*) response="$FAKE_JOBS_JSON" ;;
            */actions/runs/12345/artifacts*) response="$FAKE_ARTIFACTS_JSON" ;;
            */actions/runs/12345) response="$FAKE_RUN_JSON" ;;
            *)
                echo "Unexpected fake gh endpoint: $endpoint" >&2
                exit 99
                ;;
        esac
        if [[ "$*" == *"--paginate"* ]]; then
            printf '%s\\n' "$response"
        else
            jq -sc '.[0]' <<< "$response"
        fi
        ;;
    *)
        echo "Unexpected fake gh command: $*" >&2
        exit 99
        ;;
esac
"""


class ReleaseMainSourceTests(unittest.TestCase):
    """Run the checked-in source verifier with real Git objects and isolated remotes."""

    def test_preparation_accepts_only_the_current_main_tip(self) -> None:
        for advance in (False, True):
            with self.subTest(advance=advance), RealSourceRepository() as repository:
                if advance:
                    repository.advance_main()
                before = repository.refs(remote=True)
                result = repository.run_script(PRE_TAG_CHECK.name)

                self.assertEqual(repository.refs(remote=True), before)
                self.assertEqual(repository.git("rev-parse", "HEAD"), repository.candidate)
                if advance:
                    self.assertEqual(result.returncode, 1, result.stdout)
                    self.assertIn("Preparation requires", result.stderr)
                else:
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertIn("ready for untagged qualification", result.stdout)

    def test_main_tip_and_advanced_main_both_accept_the_exact_candidate(self) -> None:
        for advance in (False, True):
            with self.subTest(advance=advance), RealSourceRepository() as repository:
                if advance:
                    repository.advance_main()
                result = repository.verify_source(repository.candidate)

                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(repository.git("rev-parse", "HEAD"), repository.candidate)
                self.assertEqual(
                    repository.git("rev-parse", "refs/remotes/origin/main"),
                    repository.git("rev-parse", "refs/heads/main", remote=True),
                )

    def test_changed_local_head_is_rejected_before_fetch(self) -> None:
        with RealSourceRepository() as repository:
            repository.advance_main()
            repository.git("-c", "commit.gpgSign=false", "commit", "--allow-empty", "-m", "Unqualified local commit")
            before = repository.refs()
            result = repository.verify_source(repository.candidate)

            self.assertEqual(result.returncode, 1)
            self.assertIn("local checkout does not identify", result.stderr)
            self.assertEqual(repository.refs(), before)

    def test_rewritten_main_rejects_a_candidate_reachable_only_from_stale_tracking(self) -> None:
        with RealSourceRepository() as repository:
            repository.advance_main(rewrite=True)
            result = repository.verify_source(repository.candidate)

            self.assertEqual(result.returncode, 1)
            self.assertIn("not an ancestor of current origin/main", result.stderr)
            self.assertEqual(
                repository.git("rev-parse", "refs/remotes/origin/main"),
                repository.git("rev-parse", "refs/heads/main", remote=True),
            )

    def test_candidate_ahead_of_main_is_rejected(self) -> None:
        with RealSourceRepository() as repository:
            repository.git("-c", "commit.gpgSign=false", "commit", "--allow-empty", "-m", "Not on main")
            result = repository.verify_source(repository.git("rev-parse", "HEAD"))

            self.assertEqual(result.returncode, 1)
            self.assertIn("not an ancestor of current origin/main", result.stderr)

    def test_failed_refresh_never_accepts_stale_main_evidence(self) -> None:
        with RealSourceRepository() as repository:
            repository.git("update-ref", "-d", "refs/heads/main", remote=True)
            result = repository.verify_source(repository.candidate)

            self.assertNotEqual(result.returncode, 0)
            self.assertIn("couldn't find remote ref refs/heads/main", result.stderr)

    def test_refresh_preserves_all_tags_despite_fetch_and_pruning_configuration(self) -> None:
        for unsafe_configuration in (False, True):
            with self.subTest(unsafe_configuration=unsafe_configuration), RealSourceRepository() as repository:
                main = repository.advance_main()
                repository.git("tag", "--no-sign", "unrelated", repository.candidate)
                repository.git("tag", "--no-sign", "local-only", repository.candidate)
                repository.git("tag", "--no-sign", "configured-target", repository.candidate)
                repository.git("update-ref", "refs/tags/unrelated", main, remote=True)
                repository.git("update-ref", "refs/tags/remote-only", main, remote=True)
                repository.git("update-ref", "refs/heads/other", main, remote=True)
                repository.git("update-ref", "refs/remotes/origin/stale", repository.candidate)
                if unsafe_configuration:
                    for scope, key, value in (
                        ("--global", "fetch.prune", "true"),
                        ("--global", "fetch.pruneTags", "true"),
                        ("--global", "fetch.all", "true"),
                        ("--global", "fetch.recurseSubmodules", "true"),
                        ("--local", "remote.origin.prune", "true"),
                        ("--local", "remote.origin.pruneTags", "true"),
                        ("--local", "remote.origin.tagOpt", "--tags"),
                    ):
                        repository.git("config", scope, key, value)
                    repository.git("config", "--local", "--add", "remote.origin.fetch", "+refs/tags/*:refs/tags/*")
                    repository.git(
                        "config", "--local", "--add", "remote.origin.fetch",
                        "+refs/heads/main:refs/tags/configured-target",
                    )
                before = repository.refs()
                remote_before = repository.refs(remote=True)
                configuration = repository.git("config", "--list", "--show-origin")
                result = repository.verify_source(repository.candidate)

                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(repository.refs(remote=True), remote_before)
                self.assertEqual(repository.git("config", "--list", "--show-origin"), configuration)
                expected = dict(before)
                expected["refs/remotes/origin/main"] = main
                self.assertEqual(repository.refs(), expected)
                self.assertNotIn("refs/tags/remote-only", repository.refs())
                self.assertEqual(repository.refs()["refs/tags/unrelated"], repository.candidate)

    def test_pre_tag_runs_the_shared_verifier_against_a_real_advanced_main(self) -> None:
        with RealSourceRepository() as repository:
            main = repository.advance_main()
            repository.git("tag", "--no-sign", "unrelated", repository.candidate)
            repository.git("update-ref", "refs/tags/unrelated", main, remote=True)
            repository.git("update-ref", "refs/tags/remote-only", main, remote=True)
            before = repository.refs()
            result = repository.pre_tag()

            self.assertEqual(repository.refs()["refs/tags/unrelated"], before["refs/tags/unrelated"])
            self.assertNotIn("refs/tags/remote-only", repository.refs())
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn(f"ready for signed tag v{VERSION}", result.stdout)


class RealSourceRepository:
    """Own all Git mutations in a fresh fixture; GitHub responses remain offline."""

    def __enter__(self) -> RealSourceRepository:
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary_directory.name)
        self.checkout = self.root / "checkout"
        self.origin = self.root / "origin.git"
        self.checkout.mkdir()
        self.origin.mkdir()
        self.environment = isolated_environment()
        self.environment.update({
            "GIT_CONFIG_NOSYSTEM": "1",
            "GIT_CONFIG_GLOBAL": str(self.root / "global.gitconfig"),
            "GIT_AUTHOR_NAME": "Release fixture",
            "GIT_AUTHOR_EMAIL": "release@example.invalid",
            "GIT_COMMITTER_NAME": "Release fixture",
            "GIT_COMMITTER_EMAIL": "release@example.invalid",
        })
        release = self.checkout / "eng" / "release"
        release.mkdir(parents=True)
        for path in (
            PRE_TAG_CHECK, VERIFY_MAIN_SOURCE, ALLOWED_SIGNERS,
            PRE_TAG_CHECK.with_name("validate-version.sh"), PRE_TAG_CHECK.with_name("version_contract.py"),
        ):
            if path.exists():
                shutil.copy2(path, release / path.name)
        (self.checkout / "src").mkdir()
        shutil.copy2(REPOSITORY_ROOT / "src" / "Directory.Build.props", self.checkout / "src" / "Directory.Build.props")
        shutil.copy2(REPOSITORY_ROOT / "CHANGELOG.md", self.checkout / "CHANGELOG.md")
        self.git("init", "--quiet", "--initial-branch=main")
        self.git("init", "--quiet", "--bare", "--initial-branch=main", remote=True)
        self.git("add", "--all")
        self.git("commit", "--quiet", "-m", "Qualified release candidate")
        self.candidate = self.git("rev-parse", "HEAD")
        self.git("remote", "add", "origin", str(self.origin))
        self.git("push", "--quiet", "origin", "main")

        principal, key_type, key_data, *_ = ALLOWED_SIGNERS.read_text(encoding="utf-8").split()
        signing_key = self.root / "release-signing-key.pub"
        signing_key.write_text(f"{key_type} {key_data} fixture\n", encoding="utf-8")
        for key, value in (
            ("gpg.format", "ssh"), ("tag.gpgSign", "true"), ("user.email", principal),
            ("user.signingkey", str(signing_key)),
        ):
            self.git("config", key, value)
        bin_directory = self.root / "bin"
        bin_directory.mkdir()
        FakeReleaseEnvironment.write_executable(bin_directory / "gh", FakeReleaseEnvironment.gh_stub())
        self.environment.update({
            "PATH": f"{bin_directory}{os.pathsep}{self.environment['PATH']}",
            "FAKE_GH_CALLS": str(self.root / "gh-calls.log"),
            "FAKE_SIGNING_KEYS_JSON": json.dumps([{"key": f"{key_type} {key_data}"}]),
            "FAKE_RUN_JSON": qualified_run(head_sha=self.candidate, run_attempt=2),
            "FAKE_JOBS_JSON": qualified_jobs(attestation_attempt=2, run_attempt=2).replace(COMMIT, self.candidate),
            "FAKE_ARTIFACTS_JSON": qualified_artifacts(attestation_attempt=2).replace(COMMIT, self.candidate),
        })

        return self

    def __exit__(self, *args: object) -> None:
        self.temporary_directory.cleanup()

    def git(self, *arguments: str, remote: bool = False) -> str:
        result = subprocess.run(
            ["git", "-C", str(self.origin if remote else self.checkout), *arguments],
            env=self.environment, check=True, capture_output=True, text=True,
        )

        return result.stdout.strip()

    def refs(self, *, remote: bool = False) -> dict[str, str]:
        output = self.git("for-each-ref", "--format=%(refname) %(objectname)", remote=remote)

        return dict(line.split() for line in output.splitlines())

    def advance_main(self, *, rewrite: bool = False) -> str:
        tree = self.git("rev-parse", f"{self.candidate}^{{tree}}", remote=True)
        parents = [] if rewrite else ["-p", self.candidate]
        commit = self.git("commit-tree", tree, *parents, "-m", "Main advanced", remote=True)
        self.git("update-ref", "refs/heads/main", commit, remote=True)

        return commit

    def verify_source(self, commit: str) -> subprocess.CompletedProcess[str]:
        return self.run_script(VERIFY_MAIN_SOURCE.name, commit)

    def pre_tag(self) -> subprocess.CompletedProcess[str]:
        return self.run_script(PRE_TAG_CHECK.name, "--version", VERSION, "--commit", self.candidate, "--run-id", RUN_ID)

    def run_script(self, name: str, *arguments: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            ["bash", str(self.checkout / "eng" / "release" / name), *arguments],
            cwd=self.checkout, env=self.environment, check=False, capture_output=True, text=True,
        )


if __name__ == "__main__":
    unittest.main()
