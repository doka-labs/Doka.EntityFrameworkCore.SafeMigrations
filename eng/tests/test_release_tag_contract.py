"""Release-tag trust and pre-allocation contract tests."""

from __future__ import annotations

import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
ALLOWED_SIGNERS = REPOSITORY_ROOT / "eng" / "release" / "allowed-signers"
PRE_TAG_CHECK = REPOSITORY_ROOT / "eng" / "release" / "pre-tag-check.sh"
VERIFY_TAG = REPOSITORY_ROOT / "eng" / "release" / "verify-tag.sh"
VERSION = "10.0.0-rc.1"
COMMIT = "a" * 40
RUN_ID = "12345"


def qualified_run(**overrides: object) -> str:
    """Return one hosted run readback with optional contract deviations."""

    run = {
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


def qualified_jobs(*, publish_status: str = "waiting", qualification: str = "success") -> str:
    """Return the required completed qualification and protected publish job."""

    return json.dumps(
        {
            "jobs": [
                {
                    "name": "Full reversible qualification / Core",
                    "status": "completed",
                    "conclusion": qualification,
                },
                {
                    "name": "Attest qualified candidate",
                    "status": "completed",
                    "conclusion": "success",
                },
                {
                    "name": "Verify tag, publish, and read back",
                    "status": publish_status,
                    "conclusion": None,
                },
            ]
        }
    )


def qualified_artifacts(*, expired: bool = False, include_attestations: bool = True) -> str:
    """Return the attempt-qualified package and attestation artifacts."""

    artifacts = [
        {
            "name": f"safe-migrations-release-{VERSION}-1",
            "expired": expired,
        }
    ]
    if include_attestations:
        artifacts.append(
            {
                "name": f"safe-migrations-attestations-{VERSION}-1",
                "expired": expired,
            }
        )

    return json.dumps({"artifacts": artifacts})


class ReleaseTagContractTests(unittest.TestCase):
    """Exercises authorized tag verification and the hosted waiting-state gate."""

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

    def test_pre_tag_check_rejects_dirty_untrusted_or_not_waiting_states(self) -> None:
        cases = (
            ({"FAKE_DIRTY": "true"}, "worktree must be clean"),
            ({"FAKE_HEAD_COMMIT": "b" * 40}, "local checkout does not identify"),
            ({"FAKE_MAIN_COMMIT": "b" * 40}, "no longer current main"),
            ({"FAKE_LOCAL_TAG": "true"}, "Release tag already exists"),
            ({"FAKE_REMOTE_TAG": "present"}, "Remote release tag already exists"),
            ({"FAKE_GPG_FORMAT": "openpgp"}, "must use SSH signing"),
            ({"FAKE_PRINCIPAL": "unauthorized@example.invalid"}, "not authorized"),
            ({"FAKE_SIGNING_KEY": "/missing/signing-key.pub"}, "identity is incomplete"),
            ({"FAKE_SIGNING_KEYS_JSON": "[]"}, "not registered"),
            (
                {"FAKE_RUN_JSON": qualified_run(event="push")},
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

        environment = os.environ.copy()
        environment.update(
            {
                "PATH": f"{bin_directory}{os.pathsep}{environment['PATH']}",
                "FAKE_COMMIT": COMMIT,
                "FAKE_HEAD_COMMIT": COMMIT,
                "FAKE_MAIN_COMMIT": COMMIT,
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
        case "${1:-}" in
            HEAD) echo "$FAKE_HEAD_COMMIT" ;;
            refs/remotes/origin/main) echo "$FAKE_MAIN_COMMIT" ;;
            *) echo "$FAKE_COMMIT" ;;
        esac
        ;;
    fetch)
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
        endpoint="${2:-}"
        case "$endpoint" in
            user/ssh_signing_keys*) echo "$FAKE_SIGNING_KEYS_JSON" ;;
            */actions/runs/12345/jobs*) echo "$FAKE_JOBS_JSON" ;;
            */actions/runs/12345/artifacts*) echo "$FAKE_ARTIFACTS_JSON" ;;
            */actions/runs/12345) echo "$FAKE_RUN_JSON" ;;
            *)
                echo "Unexpected fake gh endpoint: $endpoint" >&2
                exit 99
                ;;
        esac
        ;;
    *)
        echo "Unexpected fake gh command: $*" >&2
        exit 99
        ;;
esac
"""


if __name__ == "__main__":
    unittest.main()
