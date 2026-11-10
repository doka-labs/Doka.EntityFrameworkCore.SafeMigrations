"""Compose real admission and reconciliation adapters without hosted writes."""

import hashlib
import io
import json
import os
import subprocess
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest import mock

from eng.release import publication
from eng.tests.test_release_publication import (
    COMMIT,
    PROVENANCE_NAME,
    QUALIFIED_NAME,
    ROOT,
    VERSION,
    FakeGitHub,
    candidate,
    environment,
    frozen,
)


class ReleaseLineFlowTests(unittest.TestCase):
    """Bind original qualification bytes to the existing thirteen-asset publisher."""

    def setUp(self):
        """Own synthetic candidates and reuse the existing shell test instrument."""
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name).resolve()
        self.artifacts = self.root / "artifacts"
        self.state = self.root / "state"
        self.bin = self.root / "bin"
        self.state.mkdir()
        self.bin.mkdir()
        candidate(self.artifacts)

        # WHY: Reuse the already-tested gh instrument rather than create a
        # second fake with subtly different publication or readback behavior.
        instrument = (ROOT / "eng/tests/test-github-release-reconciliation.sh").read_text(encoding="ascii")
        start = 'cat >"$fake_bin/gh" <<\'FAKE_GH\'\n'
        body = instrument.split(start, 1)[1].split("\nFAKE_GH\n", 1)[0]
        executable = self.bin / "gh"
        executable.write_text(body + "\n", encoding="ascii")
        executable.chmod(0o755)

        (self.root / "CHANGELOG.md").write_text(
            f"# Changelog\n\n## [{VERSION}] - 2026-10-25\n\nIsolated release-flow regression.\n",
            encoding="ascii",
        )
        self.line_file = self.root / "line.json"
        self.admission_file = self.root / "admission.json"
        self.outputs = self.root / "outputs.txt"
        publication.write_json(self.line_file, frozen())
        self.line_hash = publication.file_digest(self.line_file)
        self.api = FakeGitHub()
        self.shell_operations = []

    def invoke_publication(self, command, attempt="1"):
        """Call the real CLI entry while substituting only its read-only API transport."""
        arguments = [
            "publication", command, "--line-file", str(self.line_file),
            "--line-sha256", self.line_hash, "--artifact-dir", str(self.artifacts),
        ]
        if command == "admit":
            arguments += ["--qualified-artifact-name", QUALIFIED_NAME,
                          "--provenance-artifact-name", PROVENANCE_NAME,
                          "--output", str(self.admission_file)]
        else:
            arguments += ["--admission", str(self.admission_file), "--admission-sha256",
                          publication.file_digest(self.admission_file)]

        stdout, stderr = io.StringIO(), io.StringIO()
        env = {**environment(attempt), "GITHUB_OUTPUT": str(self.outputs)}
        with mock.patch.dict(os.environ, env, clear=True), mock.patch.object(
            publication, "GitHubClient", return_value=self.api
        ), mock.patch.object(sys, "argv", arguments), mock.patch.object(
            sys, "stdout", stdout
        ), mock.patch.object(sys, "stderr", stderr):
            result = publication.main()

        return result, stdout.getvalue(), stderr.getvalue()

    def invoke_shell(self, operation, verification_failures=0, attempt="1", release_id="42", extra_env=None):
        """Execute the actual publisher with the shared fake gh and real local asset hashes."""
        self.shell_operations.append(operation)
        env = {
            **os.environ,
            **environment(attempt),
            "PATH": str(self.bin) + os.pathsep + os.environ["PATH"],
            "FAKE_GH_STATE": str(self.state),
            "FAKE_GH_RELEASE_VERIFY_FAILURES": str(verification_failures),
            "SAFE_MIGRATIONS_RELEASE_READBACK_ATTEMPTS": "1",
            "SAFE_MIGRATIONS_RELEASE_READBACK_DELAY_SECONDS": "0",
            "FINALIZATION_RELEASE_ID": release_id,
            **(extra_env or {}),
        }

        return subprocess.run(
            ["bash", str(ROOT / "eng/reconcile-github-release.sh"), operation, VERSION, f"v{VERSION}"],
            cwd=self.root, env=env, capture_output=True, text=True, check=False, timeout=60,
        )

    def test_existing_draft_finalization_edits_once_without_upload_or_creation(self):
        # Arrange
        staged = self.invoke_shell("stage")
        self.assertEqual(0, staged.returncode, staged.stderr)
        log = self.state / "commands.log"
        log.write_text("", encoding="ascii")
        original = publication.candidate_manifest(self.artifacts, VERSION)

        # Act
        completed = self.invoke_shell("finalize")
        replay = self.invoke_shell("finalize")

        # Assert
        self.assertEqual(0, completed.returncode, completed.stderr)
        self.assertEqual(0, replay.returncode, replay.stderr)
        self.assertEqual(original, publication.candidate_manifest(self.artifacts, VERSION))
        commands = log.read_text(encoding="ascii").splitlines()
        self.assertEqual(1, sum(line.startswith("api --method PATCH ") for line in commands))
        self.assertFalse(any(line.startswith("release edit ") for line in commands))
        self.assertFalse(any(line.startswith(("release create ", "release upload ")) for line in commands))
        self.assertEqual(26, sum(line.startswith("release verify-asset ") for line in commands))

    def test_finalization_never_creates_a_missing_release(self):
        # Arrange
        log = self.state / "commands.log"

        # Act
        result = self.invoke_shell("finalize")

        # Assert
        self.assertNotEqual(0, result.returncode)
        self.assertFalse((self.state / "release.json").exists())
        self.assertNotIn("release create ", log.read_text(encoding="ascii"))

    def test_finalization_rejects_replaced_release_id_before_any_write(self):
        # Arrange
        staged = self.invoke_shell("stage")
        self.assertEqual(0, staged.returncode, staged.stderr)
        log = self.state / "commands.log"
        log.write_text("", encoding="ascii")

        # Act
        result = self.invoke_shell("finalize", release_id="43")

        # Assert
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Release ID changed", result.stderr)
        self.assertFalse(any(line.startswith(("release edit ", "release upload ", "release create ",
                                             "api --method PATCH "))
                             for line in log.read_text(encoding="ascii").splitlines()))

    def test_finalization_rejects_release_replacement_after_initial_observation(self):
        # Arrange
        staged = self.invoke_shell("stage")
        self.assertEqual(0, staged.returncode, staged.stderr)
        (self.state / "release-visibility-count").write_text("0", encoding="ascii")
        log = self.state / "commands.log"
        log.write_text("", encoding="ascii")

        # Act
        result = self.invoke_shell("finalize", extra_env={"FAKE_GH_REPLACE_RELEASE_AT": "2"})

        # Assert
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Release ID changed", result.stderr)
        self.assertFalse(any(line.startswith(("release edit ", "api --method PATCH "))
                             for line in log.read_text(encoding="ascii").splitlines()))

    def test_finalization_rejects_changed_or_incomplete_assets_without_repairing_them(self):
        for change in ("missing", "digest", "unexpected"):
            with self.subTest(change=change):
                # Arrange
                staged = self.invoke_shell("stage")
                self.assertEqual(0, staged.returncode, staged.stderr)
                path = self.state / "release.json"
                original = path.read_bytes()
                value = json.loads(original)
                if change == "missing":
                    value["assets"].pop()
                elif change == "digest":
                    value["assets"][0]["digest"] = "sha256:" + "0" * 64
                else:
                    value["assets"].append({"name": "unexpected.txt", "digest": "sha256:" + "0" * 64})

                path.write_text(json.dumps(value), encoding="ascii")
                log = self.state / "commands.log"
                log.write_text("", encoding="ascii")

                # Act
                result = self.invoke_shell("finalize")

                # Assert
                self.assertNotEqual(0, result.returncode)
                self.assertFalse(any(line.startswith(("release edit ", "release upload ", "release create ",
                                                     "api --method PATCH "))
                                     for line in log.read_text(encoding="ascii").splitlines()))
                path.write_bytes(original)

    def run_public_readback(self, change=None, signature_exit=0):
        """Exercise real ZIP comparison and signature-command gating without contacting NuGet."""
        public = self.root / "public"
        public.mkdir()
        for package in publication.PACKAGES:
            original = self.artifacts / f"packages/{package}.{VERSION}.nupkg"
            with zipfile.ZipFile(original, "w") as archive:
                archive.writestr("payload", b"qualified content")

            with zipfile.ZipFile(public / original.name.lower(), "w") as archive:
                archive.writestr("payload", b"changed" if change == "content" else b"qualified content")
                if change != "unsigned":
                    archive.writestr(".signature.p7s", b"synthetic signature; verifier is a test double")

        curl = self.bin / "curl"
        curl.write_text("""#!/usr/bin/env bash
set -euo pipefail
url="${!#}"
while (($#)); do
    if [[ "$1" == --output ]]; then
        cp "$PUBLIC_ROOT/${url##*/}" "$2"
        break
    fi
    shift
done
printf '%s' "${FAKE_HTTP_STATUS:-200}"
""", encoding="ascii")
        curl.chmod(0o755)
        dotnet = self.bin / "dotnet"
        dotnet.write_text("""#!/usr/bin/env bash
set -euo pipefail
[[ "$1" == nuget && "$2" == verify && "${!#}" == --all ]]
printf '%s\\n' "$*" >>"$VERIFY_LOG"
exit "$VERIFY_EXIT"
""", encoding="ascii")
        dotnet.chmod(0o755)
        sleep = self.bin / "sleep"
        sleep.write_text("#!/bin/sh\nexit 0\n", encoding="ascii")
        sleep.chmod(0o755)

        return subprocess.run(
            ["bash", str(ROOT / "eng/readback-nuget.sh"), "--package-dir", str(self.artifacts / "packages"),
             "--version", VERSION, "--timeout-seconds", "1" if change == "unsigned" else "3600"],
            env={**os.environ, "PATH": str(self.bin) + os.pathsep + os.environ["PATH"],
                 "PUBLIC_ROOT": str(public), "VERIFY_LOG": str(self.root / "verify.log"),
                 "VERIFY_EXIT": str(signature_exit)}, capture_output=True, text=True, timeout=20, check=False)

    def test_real_public_readback_requires_all_five_signature_checks_and_exact_content(self):
        # Arrange
        expected_count = len(publication.PACKAGES)

        # Act
        result = self.run_public_readback()

        # Assert
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(expected_count, len((self.root / "verify.log").read_text(encoding="ascii").splitlines()))
        self.assertEqual(expected_count, result.stdout.count("Verified published package:"))

    def test_real_public_readback_rejects_invalid_signatures(self):
        # Arrange
        signature_exit = 1

        # Act
        result = self.run_public_readback(signature_exit=signature_exit)

        # Assert
        self.assertNotEqual(0, result.returncode)
        self.assertNotIn("NuGet repository signatures and package contents verified.", result.stdout)

    def test_real_public_readback_rejects_changed_public_content(self):
        # Arrange
        change = "content"

        # Act
        result = self.run_public_readback(change=change)

        # Assert
        self.assertNotEqual(0, result.returncode)
        self.assertNotIn("NuGet repository signatures and package contents verified.", result.stdout)

    def test_real_public_readback_never_accepts_an_unsigned_public_package(self):
        # Arrange
        change = "unsigned"

        # Act
        result = self.run_public_readback(change=change)

        # Assert
        self.assertNotEqual(0, result.returncode)
        self.assertIn("NuGet readback timed out", result.stderr)
        self.assertIn("Waiting for NuGet repository signature", result.stderr)

    def test_failed_readback_resumes_original_admission_without_republishing(self):
        """An interrupted write/readback boundary keeps its original source and exact thirteen assets."""
        # Arrange
        original_line = self.line_file.read_bytes()
        original_manifest = publication.candidate_manifest(self.artifacts, VERSION)

        # Act
        admitted = self.invoke_publication("admit")
        original_admission = self.admission_file.read_bytes()
        self.api.retain(json.loads(original_admission))
        checked = self.invoke_publication("recheck")
        staged = self.invoke_shell("stage")
        before_first_write = self.invoke_publication("recheck")
        interrupted = self.invoke_shell("publish", verification_failures=1)
        self.api.attempt = 2
        self.api.jobs[0].update(conclusion="failure", steps=[
            {"name": name, "conclusion": "success", "started_at": "2026-10-25T02:00:00Z"}
            for name in publication.WRITE_STEPS
        ])
        restored = self.invoke_publication("admit", attempt="2")
        rechecked = self.invoke_publication("recheck", attempt="2")
        restaged = self.invoke_shell("stage", attempt="2")
        before_retry_write = self.invoke_publication("recheck", attempt="2")
        resumed = self.invoke_shell("publish", attempt="2")

        # Assert
        self.assertEqual([0] * 6, [result[0] for result in (
            admitted, checked, before_first_write, restored, rechecked, before_retry_write)])
        self.assertEqual(0, staged.returncode, staged.stderr)
        self.assertEqual(1, interrupted.returncode, interrupted.stderr)
        self.assertIn("Timed out waiting for GitHub Release and asset attestations", interrupted.stderr)
        self.assertEqual(0, restaged.returncode, restaged.stderr)
        self.assertEqual(0, resumed.returncode, resumed.stderr)
        self.assertIn("Immutable GitHub Release and every asset attestation are verified.", resumed.stdout)
        self.assertEqual(original_line, self.line_file.read_bytes())
        self.assertEqual(original_admission, self.admission_file.read_bytes())
        self.assertEqual(original_manifest, publication.candidate_manifest(self.artifacts, VERSION))
        receipt = json.loads(original_admission)
        self.assertEqual(COMMIT, receipt["identity"]["sourceCommit"])
        self.assertEqual("1", receipt["identity"]["originalPublicationAttempt"])
        self.assertEqual(publication.canonical_digest(original_manifest), receipt["identity"]["manifestSha256"])
        self.assertEqual(hashlib.sha256(original_line).hexdigest(), receipt["identity"]["releaseLineSha256"])
        self.assertIn("admission-restored=true", self.outputs.read_text(encoding="ascii"))
        self.assertTrue(any("actions/artifacts/3/zip" in route for route in self.api.calls))
        remote = json.loads((self.state / "release.json").read_text(encoding="ascii"))
        self.assertFalse(remote["isDraft"])
        self.assertTrue(remote["isImmutable"])
        self.assertEqual(13, len(remote["assets"]))
        self.assertEqual({Path(name).name for name in original_manifest},
                         {asset["name"] for asset in remote["assets"]})
        self.assertEqual({Path(name).name: f"sha256:{digest}" for name, digest in original_manifest.items()},
                         {asset["name"]: asset["digest"] for asset in remote["assets"]})
        commands = (self.state / "commands.log").read_text(encoding="ascii").splitlines()
        self.assertEqual(1, sum(command.startswith("release create ") for command in commands))
        self.assertEqual(13, sum(command.startswith("release upload ") for command in commands))
        self.assertEqual(1, sum(command.startswith("release edit ") for command in commands))

    def test_revoked_line_stops_before_any_publisher_write(self):
        """Frozen active evidence cannot bypass current revocation at the actual admission entry."""
        # Arrange
        original_manifest = publication.candidate_manifest(self.artifacts, VERSION)
        self.api.policy["lines"][0]["state"] = "inactive"

        # Act
        admitted = self.invoke_publication("admit")
        if admitted[0] == 0:
            self.invoke_shell("stage")

        # Assert
        self.assertEqual(1, admitted[0])
        self.assertIn("Release-line admit failed:", admitted[2])
        self.assertFalse(self.admission_file.exists())
        self.assertFalse(self.outputs.exists())
        self.assertEqual([], self.shell_operations)
        self.assertFalse((self.state / "commands.log").exists())
        self.assertFalse((self.state / "release.json").exists())
        self.assertEqual(original_manifest, publication.candidate_manifest(self.artifacts, VERSION))
        self.assertTrue(any("branches/main" in route for route in self.api.calls))
        self.assertFalse(any("artifacts" in route for route in self.api.calls))


if __name__ == "__main__":
    unittest.main()
