"""Current-authority and exact-byte retry tests, without hosted writes."""

from __future__ import annotations

import base64
import copy
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

from eng.release import lines, publication


ROOT = Path(__file__).resolve().parents[2]
REPOSITORY = "doka-labs/Doka.EntityFrameworkCore.SafeMigrations"
COMMIT = "1" * 40
VERSION = "10.6.0"
LINE_HASH = "2" * 64
QUALIFIED_NAME = "safe-migrations-release-10.6.0-1"
PROVENANCE_NAME = "safe-migrations-release-provenance-7-1"


def environment(attempt="1"):
    """Supply one synthetic exact workflow/run identity, not actual credentials."""
    return {"GITHUB_ACTIONS": "true", "GITHUB_EVENT_NAME": "workflow_dispatch",
            "GITHUB_REPOSITORY": REPOSITORY, "GITHUB_REF": "refs/heads/main", "GITHUB_SHA": COMMIT,
            "GITHUB_WORKFLOW_REF": f"{REPOSITORY}/{lines.WORKFLOW_PATH}@refs/heads/main",
            "GITHUB_WORKFLOW_SHA": COMMIT, "GITHUB_RUN_ID": "7", "GITHUB_RUN_ATTEMPT": attempt,
            "SAFE_MIGRATIONS_PUBLICATION_LOCK_GROUP": publication.PUBLICATION_LOCK,
            "SAFE_MIGRATIONS_PUBLICATION_LOCK_HELD": "1"}


def authority(policy=None):
    """Make independent frozen evidence that never acts as current permission."""
    policy = copy.deepcopy(policy) if policy is not None else lines.load_policy()

    return {"schemaVersion": 1, "kind": "release-line-authority-observation", "repository": REPOSITORY,
            "controlCommit": COMMIT, "authorizationRevision": policy["revision"],
            "authorizationDigest": lines.policy_digest(policy), "policy": policy,
            "observedUtc": "2026-10-25T02:00:00Z"}


def frozen(policy=None, source_ref="refs/heads/main", publishable=True):
    """Construct the exact portable qualification used by admission controls."""
    observed = authority(policy)
    snapshot = lines.freeze_line(observed["policy"], source_ref, VERSION)

    return {"schemaVersion": 1, "kind": "safe-migrations-release-qualification", "repository": REPOSITORY,
            "sourceRef": source_ref, "sourceCommit": COMMIT, "version": VERSION, "tag": f"v{VERSION}",
            "workflowPath": lines.WORKFLOW_PATH, "workflowRunId": "7", "qualificationAttempt": "1",
            "releaseLine": snapshot, "toolchain": lines.inspect_toolchain(ROOT, snapshot),
            "authority": observed, "qualifiedForPublication": publishable, "qualifiedAtUtc": "2026-10-25T02:00:00Z"}


def artifact(name, identifier, digest="a" * 64):
    """Build an immutable same-run artifact descriptor with a distinct identity."""
    return {"id": identifier, "name": name, "digest": f"sha256:{digest}", "expired": False,
            "workflow_run": {"id": 7, "head_sha": COMMIT}}


def candidate(root):
    """Materialize small synthetic candidate assets in the test's isolated directory."""
    for package in publication.PACKAGES:
        for suffix in ("nupkg", "snupkg"):
            path = root / f"packages/{package}.{VERSION}.{suffix}"
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(f"{package}:{suffix}".encode("ascii"))

    for name in ("packages/SHA256SUMS", "sbom/_manifest/spdx_2.2/manifest.spdx.json",
                 "release-provenance/release-provenance.intoto.jsonl"):
        path = root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(b"synthetic exact bytes")


class FakeGitHub:
    """Observe routing and return bounded synthetic API data; no writes are available."""

    def __init__(self, policy=None):
        self.policy = copy.deepcopy(policy) if policy is not None else lines.load_policy()
        self.protected = True
        self.head = COMMIT
        self.blob_mismatch = False
        self.mode = "protected"
        self.attempt = 1
        self.branch_name = "main"
        self.artifacts = [artifact(QUALIFIED_NAME, 1), artifact(PROVENANCE_NAME, 2, "b" * 64)]
        self.archives = {}
        self.jobs = [{"id": 10, "name": publication.PUBLISHER_JOB, "status": "completed",
                      "conclusion": "skipped", "steps": []}]
        self.grants = [{"id": 1, "name": "main", "type": "branch"}]
        self.calls = []

    def request(self, route):
        """Return exact endpoints only so an unexpected source/path fails the test."""
        self.calls.append(route)
        prefix = f"repos/{REPOSITORY}/"
        if route == prefix + "branches/main":
            return {"name": "main", "protected": self.protected, "commit": {"sha": self.head}}

        if route.startswith(prefix + "branches/"):
            return {"name": route.removeprefix(prefix + "branches/").replace("%2F", "/"),
                    "protected": self.protected, "commit": {"sha": self.head}}

        if route == prefix + f"contents/{publication.POLICY_PATH}?ref={self.head}":
            raw = json.dumps(self.policy).encode("ascii")
            blob = hashlib.sha1(f"blob {len(raw)}\0".encode("ascii") + raw).hexdigest()

            return {"type": "file", "path": publication.POLICY_PATH, "encoding": "base64",
                    "content": base64.b64encode(raw).decode("ascii"),
                    "sha": "0" * 40 if self.blob_mismatch else blob}

        if route == prefix + "environments/nuget":
            return {"name": "nuget", "protection_rules": [{"type": "required_reviewers", "reviewers": [{}]}],
                    "deployment_branch_policy": {"protected_branches": self.mode == "protected",
                                                 "custom_branch_policies": self.mode == "custom"}}

        if route == prefix + "actions/runs/7":
            return {"id": 7, "head_sha": self.head, "head_branch": self.branch_name, "path": lines.WORKFLOW_PATH,
                    "event": "workflow_dispatch", "run_attempt": self.attempt}

        if route == prefix + f"compare/{COMMIT}...{self.head}":
            return {"status": "identical", "base_commit": {"sha": COMMIT}}

        raise AssertionError(f"Unexpected API read: {route}")

    def pages(self, route, collection):
        """Preserve exact producer routes while avoiding unrelated network mechanics."""
        self.calls.append(route)
        if collection == "artifacts":
            return copy.deepcopy(self.artifacts)

        if collection == "jobs":
            return copy.deepcopy(self.jobs)

        if collection == "branch_policies":
            return copy.deepcopy(self.grants)

        raise AssertionError((route, collection))

    def download(self, route):
        """Read only a registered synthetic archive."""
        self.calls.append(route)

        return self.archives[int(route.split("/")[-2])]

    def retain(self, receipt):
        """Register the exact immutable admission archive returned by the original attempt."""
        data = io.BytesIO()
        with zipfile.ZipFile(data, "w") as archive:
            archive.writestr("admission.json", json.dumps(receipt))

        raw = data.getvalue()
        self.archives[3] = raw
        self.artifacts.append(artifact(f"{publication.ADMISSION_PREFIX}7-1", 3, hashlib.sha256(raw).hexdigest()))


class AuthorityTests(unittest.TestCase):
    """Prove current authority and environment checks cannot fall back to checkout data."""

    def test_current_policy_is_read_from_protected_exact_git_blob(self):
        # Arrange
        api = FakeGitHub()

        # Act
        result = publication.observe_authority(REPOSITORY, api)

        # Assert
        self.assertEqual(COMMIT, result["controlCommit"])
        self.assertEqual(api.policy, result["policy"])
        self.assertIn(f"repos/{REPOSITORY}/contents/{publication.POLICY_PATH}?ref={COMMIT}", api.calls)

    def test_unprotected_authority_is_rejected_before_policy_fetch(self):
        # Arrange
        api = FakeGitHub()
        api.protected = False

        # Act / Assert
        with self.assertRaises(publication.PublicationAdmissionError):
            publication.observe_authority(REPOSITORY, api)
        self.assertEqual(1, len(api.calls))

    def test_mismatching_policy_git_blob_is_rejected(self):
        # Arrange
        api = FakeGitHub()
        api.blob_mismatch = True

        # Act / Assert
        with self.assertRaises(publication.PublicationAdmissionError):
            publication.observe_authority(REPOSITORY, api)

    def test_malformed_nested_authority_commit_has_controlled_failure(self):
        # Arrange
        api = FakeGitHub()

        # Act / Assert
        with mock.patch.object(api, "request", return_value={"name": "main", "protected": True, "commit": None}):
            with self.assertRaises(publication.PublicationAdmissionError):
                publication.observe_authority(REPOSITORY, api)

    def test_foreign_repository_policy_is_rejected(self):
        # Arrange
        api = FakeGitHub()
        api.policy["repository"] = "other/repository"

        # Act / Assert
        with self.assertRaises(publication.PublicationAdmissionError):
            publication.observe_authority(REPOSITORY, api)

    def test_protected_branches_only_preserves_existing_approval_mode(self):
        # Arrange
        api = FakeGitHub()
        selected = lines.select_line(api.policy, "refs/heads/main")

        # Act
        result = publication.validate_environment(REPOSITORY, selected, api.policy, api)

        # Assert
        self.assertIsNone(result)
        self.assertFalse(any("deployment-branch-policies" in call for call in api.calls))

    def test_exact_custom_grants_remain_supported(self):
        # Arrange
        api = FakeGitHub()
        api.mode = "custom"

        # Act
        result = publication.validate_environment(REPOSITORY, api.policy["lines"][0], api.policy, api)

        # Assert
        self.assertIsNone(result)
        self.assertTrue(any("deployment-branch-policies" in call for call in api.calls))

    def test_custom_wildcard_grant_is_rejected(self):
        # Arrange
        api = FakeGitHub()
        api.mode = "custom"
        api.grants[0]["name"] = "*"

        # Act / Assert
        with self.assertRaises(publication.PublicationAdmissionError):
            publication.validate_environment(REPOSITORY, api.policy["lines"][0], api.policy, api)

    def test_qualified_snapshot_cannot_borrow_changed_authority_digest(self):
        # Arrange
        value = frozen()
        value["authority"]["policy"]["revision"] = "changed"

        # Act / Assert
        with self.assertRaises(publication.PublicationAdmissionError):
            publication.validate_frozen(value)

    def test_lock_is_mandatory(self):
        # Arrange
        env = environment()
        del env["SAFE_MIGRATIONS_PUBLICATION_LOCK_HELD"]

        # Act / Assert
        with self.assertRaises(publication.PublicationAdmissionError):
            publication.require_lock(env)

    def test_foreign_workflow_is_rejected(self):
        # Arrange
        env = environment()
        env["GITHUB_WORKFLOW_REF"] = f"{REPOSITORY}/.github/workflows/other.yml@refs/heads/main"

        # Act / Assert
        with self.assertRaises(publication.PublicationAdmissionError):
            publication.hosted_identity(env)

    def test_prepared_qualification_never_reads_publisher_environment(self):
        # Arrange
        value = lines.load_policy()
        value["lines"][0]["state"] = "prepared"
        api = FakeGitHub(value)
        result = subprocess.CompletedProcess([], 0, stdout=COMMIT + "\n")

        # Act
        with mock.patch.object(publication.subprocess, "run", return_value=result):
            observed = publication.create_qualification(VERSION, True, ROOT, api, environment())

        # Assert
        self.assertFalse(observed["qualifiedForPublication"])
        self.assertFalse(any("environments" in call for call in api.calls))

    def test_prepared_line_cannot_request_publishable_qualification(self):
        # Arrange
        value = lines.load_policy()
        value["lines"][0]["state"] = "prepared"
        api = FakeGitHub(value)

        # Act / Assert
        with self.assertRaises(publication.PublicationAdmissionError):
            publication.create_qualification(VERSION, False, ROOT, api, environment())

    def test_stale_qualified_source_is_rejected(self):
        # Arrange
        api = FakeGitHub()
        api.head = "3" * 40

        # Act / Assert
        with self.assertRaises(publication.PublicationAdmissionError):
            publication.create_qualification(VERSION, False, ROOT, api, environment())


class PreflightCliTests(unittest.TestCase):
    """Verify producer outputs against actual serialized frozen qualification bytes."""

    def setUp(self):
        """Own receipt/output files without changing any process-wide working directory."""
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name).resolve()
        self.receipt = self.root / "line.json"
        self.outputs = self.root / "github-output.txt"

    def test_active_cli_preflight_emits_exact_original_identity_and_hash(self):
        # Arrange
        api = FakeGitHub()
        api.attempt = 3
        env = {**environment("3"), "GITHUB_OUTPUT": str(self.outputs)}
        args = ["publication", "preflight", "--version", VERSION, "--qualification-only", "false",
                "--output", str(self.receipt)]
        git_result = subprocess.CompletedProcess([], 0, stdout=COMMIT + "\n")

        # Act
        with mock.patch.dict(os.environ, env, clear=True), mock.patch.object(
                publication, "GitHubClient", return_value=api), mock.patch.object(
                publication.subprocess, "run", return_value=git_result) as git_read, mock.patch.object(
                publication.Path, "cwd", return_value=ROOT), mock.patch.object(sys, "argv", args):
            result = publication.main()

        # Assert
        self.assertEqual(0, result)
        value = publication.validate_frozen(lines.read_json(self.receipt))
        outputs = dict(line.split("=", 1) for line in self.outputs.read_text(encoding="ascii").splitlines())
        self.assertEqual({"source-ref": "refs/heads/main", "line-artifact-name": "safe-migrations-release-line-7-3",
                          "line-sha256": hashlib.sha256(self.receipt.read_bytes()).hexdigest(),
                          "publish-allowed": "true"}, outputs)
        self.assertEqual("3", value["qualificationAttempt"])
        self.assertEqual(COMMIT, value["sourceCommit"])
        self.assertTrue(value["qualifiedForPublication"])
        self.assertEqual("net10.0", value["toolchain"]["targetFramework"])
        self.assertFalse(any("environments" in call for call in api.calls))
        git_read.assert_called_once_with(["git", "-C", str(ROOT), "rev-parse", "HEAD"],
                                         capture_output=True, text=True, check=False)

    def test_prepared_cli_preflight_serializes_nonpublishing_branch_identity(self):
        # Arrange
        policy = lines.load_policy()
        policy["lines"][1]["state"] = "prepared"
        api = FakeGitHub(policy)
        api.attempt = 4
        api.branch_name = "maintenance/10.x"
        source_ref = "refs/heads/maintenance/10.x"
        env = {**environment("4"), "GITHUB_REF": source_ref, "GITHUB_OUTPUT": str(self.outputs),
               "GITHUB_WORKFLOW_REF": f"{REPOSITORY}/{lines.WORKFLOW_PATH}@{source_ref}"}
        args = ["publication", "preflight", "--version", VERSION, "--qualification-only", "true",
                "--output", str(self.receipt)]
        git_result = subprocess.CompletedProcess([], 0, stdout=COMMIT + "\n")

        # Act
        with mock.patch.dict(os.environ, env, clear=True), mock.patch.object(
                publication, "GitHubClient", return_value=api), mock.patch.object(
                publication.subprocess, "run", return_value=git_result) as git_read, mock.patch.object(
                publication.Path, "cwd", return_value=ROOT), mock.patch.object(sys, "argv", args):
            result = publication.main()

        # Assert
        self.assertEqual(0, result)
        value = publication.validate_frozen(lines.read_json(self.receipt))
        outputs = dict(line.split("=", 1) for line in self.outputs.read_text(encoding="ascii").splitlines())
        self.assertEqual({"source-ref": source_ref, "line-artifact-name": "safe-migrations-release-line-7-4",
                          "line-sha256": hashlib.sha256(self.receipt.read_bytes()).hexdigest(),
                          "publish-allowed": "false"}, outputs)
        self.assertEqual("4", value["qualificationAttempt"])
        self.assertEqual(source_ref, value["sourceRef"])
        self.assertEqual("prepared", value["releaseLine"]["stateAtQualification"])
        self.assertFalse(value["qualifiedForPublication"])
        self.assertFalse(any("environments" in call or "artifacts" in call for call in api.calls))
        self.assertNotIn("NUGET_API_KEY", env)
        git_read.assert_called_once_with(["git", "-C", str(ROOT), "rev-parse", "HEAD"],
                                         capture_output=True, text=True, check=False)

    def test_invalid_cli_preflight_never_emits_candidate_or_publish_permission(self):
        # Arrange
        api = FakeGitHub()
        env = {**environment(), "GITHUB_OUTPUT": str(self.outputs)}
        args = ["publication", "preflight", "--version", "10.06.0", "--qualification-only", "false",
                "--output", str(self.receipt)]

        # Act
        with mock.patch.dict(os.environ, env, clear=True), mock.patch.object(
                publication, "GitHubClient", return_value=api), mock.patch.object(sys, "argv", args):
            result = publication.main()

        # Assert
        self.assertEqual(1, result)
        self.assertFalse(self.receipt.exists())
        self.assertFalse(self.outputs.exists())
        self.assertFalse(any("environments" in call or "artifacts" in call for call in api.calls))


class AdmissionTests(unittest.TestCase):
    """Exercise real manifests and adversarial same-run admission/recovery boundaries."""

    def setUp(self):
        """Own one isolated candidate directory per test method."""
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name).resolve()
        candidate(self.root)
        self.api = FakeGitHub()
        self.frozen = frozen()

    def admit(self, env=None):
        """Use the actual admission implementation with one independently owned candidate."""
        return publication.admit(self.frozen, LINE_HASH, self.root, QUALIFIED_NAME, PROVENANCE_NAME,
                                 self.api, env or environment())

    def test_new_active_candidate_binds_all_thirteen_assets_and_original_producers(self):
        # Arrange
        expected = publication.candidate_manifest(self.root, VERSION)

        # Act
        receipt, restored = self.admit()

        # Assert
        self.assertFalse(restored)
        self.assertEqual(13, len(expected))
        self.assertEqual(publication.canonical_digest(expected), receipt["identity"]["manifestSha256"])
        self.assertEqual(1, receipt["identity"]["qualifiedArtifact"]["id"])
        self.assertEqual(2, receipt["identity"]["provenanceArtifact"]["id"])

    def test_failed_qualification_attempt_with_skipped_publisher_can_admit(self):
        # Arrange
        env = environment("2")

        # Act
        receipt, restored = self.admit(env)

        # Assert
        self.assertFalse(restored)
        self.assertEqual("1", receipt["identity"]["qualificationAttempt"])
        self.assertEqual("2", receipt["identity"]["originalPublicationAttempt"])

    def test_missing_prior_publisher_history_cannot_remint_admission(self):
        # Arrange
        self.api.jobs = []

        # Act / Assert
        with self.assertRaises(publication.PublicationAdmissionError):
            self.admit(environment("2"))

    def test_started_prior_write_without_original_admission_is_rejected(self):
        # Arrange
        self.api.jobs[0].update(conclusion="failure", steps=[
            {"name": name, "conclusion": "skipped", "started_at": None} for name in publication.WRITE_STEPS])
        self.api.jobs[0]["steps"][0].update(conclusion="success", started_at="2026-10-25T02:00:00Z")

        # Act / Assert
        with self.assertRaises(publication.PublicationAdmissionError):
            self.admit(environment("2"))

    def test_malformed_prior_step_identity_has_controlled_failure(self):
        # Arrange
        self.api.jobs[0].update(conclusion="failure", steps=[{"name": [], "conclusion": "skipped"}])

        # Act / Assert
        with self.assertRaises(publication.PublicationAdmissionError):
            self.admit(environment("2"))

    def test_original_receipt_resumes_exact_bytes_under_current_authority(self):
        # Arrange
        original, _ = self.admit()
        self.api.retain(original)
        self.api.policy["revision"] = "later-authority-revision"

        # Act
        observed, restored = self.admit(environment("2"))

        # Assert
        self.assertTrue(restored)
        self.assertEqual(original, observed)
        self.assertEqual("1", observed["identity"]["originalPublicationAttempt"])

    def test_retry_rejects_changed_candidate_bytes(self):
        # Arrange
        original, _ = self.admit()
        self.api.retain(original)
        (self.root / "packages/SHA256SUMS").write_bytes(b"rebuilt")

        # Act / Assert
        with self.assertRaises(publication.PublicationAdmissionError):
            self.admit(environment("2"))

    def test_retry_rejects_replaced_producer_artifact_identity(self):
        # Arrange
        original, _ = self.admit()
        self.api.retain(original)
        self.api.artifacts[0]["id"] = 9

        # Act / Assert
        with self.assertRaises(publication.PublicationAdmissionError):
            self.admit(environment("2"))

    def test_revoked_current_line_rejects_original_admission(self):
        # Arrange
        original, _ = self.admit()
        self.api.retain(original)
        self.api.policy["lines"][0]["state"] = "inactive"

        # Act / Assert
        with self.assertRaises(lines.LinePolicyError):
            self.admit(environment("2"))

    def test_expired_original_admission_is_not_replaced(self):
        # Arrange
        original, _ = self.admit()
        self.api.retain(original)
        self.api.artifacts[2]["expired"] = True

        # Act / Assert
        with self.assertRaises(publication.PublicationAdmissionError):
            self.admit(environment("2"))

    def test_original_admission_archive_digest_is_verified(self):
        # Arrange
        original, _ = self.admit()
        self.api.retain(original)
        self.api.archives[3] += b"corruption"

        # Act / Assert
        with self.assertRaises(publication.PublicationAdmissionError):
            self.admit(environment("2"))

    def test_foreign_run_cannot_borrow_candidate(self):
        # Arrange
        env = environment()
        env["GITHUB_RUN_ID"] = "8"

        # Act / Assert
        with self.assertRaises(publication.PublicationAdmissionError):
            self.admit(env)

    def test_expired_qualified_artifact_blocks_first_admission(self):
        # Arrange
        self.api.artifacts[0]["expired"] = True

        # Act / Assert
        with self.assertRaises(publication.PublicationAdmissionError):
            self.admit()

    def test_unexpected_package_archive_is_rejected(self):
        # Arrange
        (self.root / "packages/unexpected.nupkg").write_bytes(b"foreign")

        # Act / Assert
        with self.assertRaises(publication.PublicationAdmissionError):
            self.admit()

    def test_symlink_candidate_is_rejected(self):
        # Arrange
        path = self.root / "packages/SHA256SUMS"
        path.unlink()
        path.symlink_to(self.root / "release-provenance/release-provenance.intoto.jsonl")

        # Act / Assert
        with self.assertRaises(publication.PublicationAdmissionError):
            self.admit()

    def test_recheck_revalidates_actual_files_before_another_write(self):
        # Arrange
        receipt, _ = self.admit()
        (self.root / "release-provenance/release-provenance.intoto.jsonl").write_bytes(b"changed")

        # Act / Assert
        with self.assertRaises(publication.PublicationAdmissionError):
            publication.recheck(self.frozen, LINE_HASH, receipt, self.root, self.api, environment())

    def test_recheck_observes_revocation_after_prior_admission(self):
        # Arrange
        receipt, _ = self.admit()
        self.api.policy["lines"][0]["state"] = "prepared"

        # Act / Assert
        with self.assertRaises(lines.LinePolicyError):
            publication.recheck(self.frozen, LINE_HASH, receipt, self.root, self.api, environment())

    def test_qualification_only_never_admits(self):
        # Arrange
        self.frozen["qualifiedForPublication"] = False

        # Act / Assert
        with self.assertRaises(publication.PublicationAdmissionError):
            self.admit()

    def test_original_frozen_file_hash_is_bound(self):
        # Arrange
        path = self.root / "line.json"
        publication.write_json(path, self.frozen)

        # Act / Assert
        with self.assertRaises(publication.PublicationAdmissionError):
            publication._read_frozen(path, LINE_HASH)

    def test_receipt_self_hash_tampering_is_rejected(self):
        # Arrange
        receipt, _ = self.admit()
        receipt["identity"]["manifestSha256"] = "3" * 64

        # Act / Assert
        with self.assertRaises(publication.PublicationAdmissionError):
            publication.validate_admission(receipt)

    def test_public_cli_admit_binds_actual_candidate_and_output(self):
        # Arrange
        line_file, output = self.root / "line.json", self.root / "admission.json"
        publication.write_json(line_file, self.frozen)
        line_hash = publication.file_digest(line_file)
        args = ["publication", "admit", "--line-file", str(line_file), "--line-sha256", line_hash,
                "--artifact-dir", str(self.root), "--qualified-artifact-name", QUALIFIED_NAME,
                "--provenance-artifact-name", PROVENANCE_NAME, "--output", str(output)]

        # Act
        with mock.patch.dict(os.environ, environment(), clear=True), mock.patch.object(
                publication, "GitHubClient", return_value=self.api), mock.patch.object(sys, "argv", args):
            admitted = publication.main()

        # Assert
        self.assertEqual(0, admitted)
        self.assertEqual(self.frozen["sourceCommit"], lines.read_json(output)["identity"]["sourceCommit"])

    def test_public_cli_recheck_accepts_exact_original_receipt(self):
        # Arrange
        line_file, output = self.root / "line.json", self.root / "admission.json"
        publication.write_json(line_file, self.frozen)
        receipt, _ = publication.admit(self.frozen, publication.file_digest(line_file), self.root,
                                       QUALIFIED_NAME, PROVENANCE_NAME, self.api, environment())
        publication.write_json(output, receipt)
        args = ["publication", "recheck", "--line-file", str(line_file),
                "--line-sha256", publication.file_digest(line_file), "--artifact-dir", str(self.root),
                "--admission", str(output), "--admission-sha256", publication.file_digest(output)]

        # Act
        with mock.patch.dict(os.environ, environment(), clear=True), mock.patch.object(
                publication, "GitHubClient", return_value=self.api), mock.patch.object(sys, "argv", args):
            checked = publication.main()

        # Assert
        self.assertEqual(0, checked)

    def test_public_cli_recheck_rejects_substituted_receipt_file(self):
        # Arrange
        receipt, _ = self.admit()
        line_file, output = self.root / "line.json", self.root / "admission.json"
        publication.write_json(line_file, self.frozen)
        publication.write_json(output, receipt)
        args = ["publication", "recheck", "--line-file", str(line_file),
                "--line-sha256", publication.file_digest(line_file), "--artifact-dir", str(self.root),
                "--admission", str(output), "--admission-sha256", "0" * 64]

        # Act
        with mock.patch.dict(os.environ, environment(), clear=True), mock.patch.object(
                publication, "GitHubClient", return_value=self.api), mock.patch.object(sys, "argv", args):
            checked = publication.main()

        # Assert
        self.assertEqual(1, checked)


class PaginationTests(unittest.TestCase):
    """Verify the API inventory reader itself, not just its admission consumers."""

    def test_full_page_requires_reading_terminator(self):
        # Arrange
        api = publication.GitHubClient()
        entries = [{"id": number} for number in range(1, 101)]

        # Act
        with mock.patch.object(api, "request", side_effect=[
                {"total_count": 100, "artifacts": entries}, {"total_count": 100, "artifacts": []}]) as request:
            observed = api.pages("route", "artifacts")

        # Assert
        self.assertEqual(entries, observed)
        self.assertEqual(2, request.call_count)

    def test_changed_inventory_total_is_rejected(self):
        # Arrange
        api = publication.GitHubClient()
        entries = [{"id": number} for number in range(1, 101)]

        # Act / Assert
        with mock.patch.object(api, "request", side_effect=[
                {"total_count": 100, "jobs": entries}, {"total_count": 101, "jobs": []}]):
            with self.assertRaises(publication.PublicationAdmissionError):
                api.pages("route", "jobs")

    def test_duplicate_inventory_identity_is_rejected(self):
        # Arrange
        api = publication.GitHubClient()

        # Act / Assert
        with mock.patch.object(api, "request", return_value={"total_count": 2, "artifacts": [{"id": 1}, {"id": 1}]}):
            with self.assertRaises(publication.PublicationAdmissionError):
                api.pages("route", "artifacts")


if __name__ == "__main__":
    unittest.main()
