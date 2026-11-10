"""Guard real runner skip semantics before minting replacement admission."""

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

from eng.release import publication
from eng.tests.test_release_publication import (
    COMMIT,
    LINE_HASH,
    PROVENANCE_NAME,
    QUALIFIED_NAME,
    REPOSITORY,
    ROOT,
    VERSION,
    FakeGitHub,
    artifact,
    candidate,
    environment,
    frozen,
)


class FinalizationApi(FakeGitHub):
    """Reuse the existing transport double with two distinct, immutable run identities."""

    def __init__(self, root):
        super().__init__()
        self.line = frozen()
        self.line_bytes = json.dumps(self.line).encode("ascii")
        line_hash = hashlib.sha256(self.line_bytes).hexdigest()
        receipt, _ = publication.admit(self.line, line_hash, root, QUALIFIED_NAME, PROVENANCE_NAME,
                                        self, environment())
        self.retain(receipt)
        archive = io.BytesIO()
        with zipfile.ZipFile(archive, "w") as output:
            output.writestr("line.json", self.line_bytes)

        raw = archive.getvalue()
        self.archives[4] = raw
        self.artifacts.append(artifact("safe-migrations-release-line-7-1", 4, hashlib.sha256(raw).hexdigest()))
        self.head = "2" * 40
        self.original_run = {"id": 7, "head_sha": COMMIT, "head_branch": "main", "status": "completed",
                             "path": publication.lines.WORKFLOW_PATH, "event": "workflow_dispatch",
                             "repository": {"full_name": REPOSITORY}, "head_repository": {"full_name": REPOSITORY}}
        self.control_run = {"id": 9, "head_sha": self.head, "head_branch": "main", "run_attempt": 1,
                            "path": publication.lines.WORKFLOW_PATH, "event": "workflow_dispatch"}
        source = (ROOT / ".github/workflows/quality-gates.yml").read_text(encoding="ascii")
        names = [name.replace("Full qualification / ", "Full reversible qualification / ", 1)
                 for name in publication.maintenance.qualification_producers(source) if name != "schedule-identity"]
        self.jobs = [{"id": index + 1, "name": name, "status": "completed", "conclusion": "success",
                      "run_attempt": 1}
                     for index, name in enumerate(["Validate untagged candidate", *names, "Attest qualified packages"])]
        self.jobs.append({"id": 21, "name": publication.PUBLISHER_JOB, "status": "completed",
                          "conclusion": "failure", "run_attempt": 1,
                          "steps": [{"name": f"Publish {name} symbols", "conclusion": "success"}
                                    for name in ("Core", "MySQL", "PostgreSQL", "SQLite", "SQL Server")]})
        self.releases = [{"id": 42, "tag_name": f"v{VERSION}"}]

    def request(self, route):
        """Keep original and control identities independent in every API observation."""
        prefix = f"repos/{REPOSITORY}/"
        if route == prefix + "actions/runs/7":
            return copy.deepcopy(self.original_run)

        if route == prefix + "actions/runs/9":
            return copy.deepcopy(self.control_run)

        if route == prefix + f"contents/.github/workflows/quality-gates.yml?ref={COMMIT}":
            raw = (ROOT / ".github/workflows/quality-gates.yml").read_bytes()

            return {"type": "file", "path": ".github/workflows/quality-gates.yml", "encoding": "base64",
                    "content": base64.b64encode(raw).decode("ascii"),
                    "sha": hashlib.sha1(f"blob {len(raw)}\0".encode("ascii") + raw).hexdigest()}

        return super().request(route)

    def pages(self, route, collection):
        """Expose original evidence only through the selected bounded API inventory."""
        if collection == "releases":
            return copy.deepcopy(self.releases)

        return super().pages(route, collection)


def finalization_environment():
    """Represent the new control run, never a forged original GITHUB_SHA or run ID."""
    return {**environment(), "GITHUB_SHA": "2" * 40, "GITHUB_WORKFLOW_SHA": "2" * 40,
            "GITHUB_RUN_ID": "9", "QUALIFICATION_ONLY": "false"}


class ExistingPublicationFinalizationTests(unittest.TestCase):
    """Complete admitted bytes only; all transport is the existing in-memory fake."""

    def setUp(self):
        """Isolate every candidate and original admission from other tests."""
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name).resolve()
        candidate(self.root)
        self.api = FinalizationApi(self.root)
        self.env = finalization_environment()

    def test_completion_preserves_both_identities_and_exact_original_admission(self):
        # Arrange
        original = self.api.archives[3]

        # Act
        receipt, line_bytes, _ = publication.finalization_evidence(VERSION, "7", self.api, self.env)
        publication.check_finalization(receipt, self.root, self.api, self.env)

        # Assert
        self.assertEqual("9", receipt["host"]["workflowRunId"])
        self.assertEqual("2" * 40, receipt["host"]["sourceCommit"])
        self.assertEqual("7", receipt["originalIdentity"]["workflowRunId"])
        self.assertEqual(COMMIT, receipt["originalIdentity"]["sourceCommit"])
        self.assertEqual("42", receipt["releaseId"])
        self.assertEqual(self.api.line_bytes, line_bytes)
        self.assertEqual(original, self.api.archives[3])
        self.assertEqual(21, len(self.api.jobs))

    def test_original_run_fields_cannot_be_borrowed_or_ignored(self):
        cases = ({"id": 8}, {"status": "in_progress"}, {"event": "pull_request"}, {"path": "other.yml"},
                 {"head_sha": "f" * 40}, {"head_branch": "other"}, {"repository": {"full_name": "fork/repo"}},
                 {"head_repository": {"full_name": "fork/repo"}})
        for changed in cases:
            with self.subTest(changed=changed):
                # Arrange
                self.api.original_run = {**self.api.original_run, **changed}

                # Act / Assert
                with self.assertRaises(publication.PublicationAdmissionError):
                    publication.finalization_evidence(VERSION, "7", self.api, self.env)

                self.api = FinalizationApi(self.root)

    def test_current_control_must_be_exact_protected_main_and_publication_intent(self):
        cases = ({"GITHUB_RUN_ID": "7"}, {"GITHUB_SHA": COMMIT}, {"GITHUB_REF": "refs/heads/other"},
                 {"QUALIFICATION_ONLY": "true"}, {"QUALIFICATION_ONLY": ""},
                 {"SAFE_MIGRATIONS_PUBLICATION_LOCK_HELD": "0"}, {"GITHUB_EVENT_NAME": "pull_request"})
        for changed in cases:
            with self.subTest(changed=changed):
                # Arrange
                env = {**self.env, **changed}

                # Act / Assert
                with self.assertRaises(publication.PublicationAdmissionError):
                    publication.finalization_evidence(VERSION, "7", self.api, env)

    def test_every_required_producer_remains_blocking(self):
        for index in range(len(self.api.jobs) - 1):
            for conclusion in ("skipped", "failure", None):
                with self.subTest(index=index, conclusion=conclusion):
                    # Arrange
                    self.api.jobs[index]["conclusion"] = conclusion

                    # Act / Assert
                    with self.assertRaises(publication.PublicationAdmissionError):
                        publication.finalization_evidence(VERSION, "7", self.api, self.env)

                    self.api.jobs[index]["conclusion"] = "success"

    def test_missing_duplicated_and_wrong_attempt_producers_are_rejected(self):
        for change in ("missing", "duplicate", "attempt"):
            with self.subTest(change=change):
                # Arrange
                jobs = copy.deepcopy(self.api.jobs)
                if change == "missing":
                    self.api.jobs.pop()
                elif change == "duplicate":
                    self.api.jobs.append(copy.deepcopy(self.api.jobs[0]))
                else:
                    self.api.jobs[0]["run_attempt"] = 2

                # Act / Assert
                with self.assertRaises(publication.PublicationAdmissionError):
                    publication.finalization_evidence(VERSION, "7", self.api, self.env)

                self.api.jobs = jobs

    def test_original_admission_cannot_be_replaced_or_minted(self):
        # Arrange
        self.api.artifacts = [entry for entry in self.api.artifacts if entry["id"] != 3]

        # Act / Assert
        with self.assertRaisesRegex(publication.PublicationAdmissionError, "replacement is forbidden"):
            publication.finalization_evidence(VERSION, "7", self.api, self.env)

    def test_expired_foreign_or_replaced_artifacts_fail_closed(self):
        for identifier in (1, 2, 3, 4):
            for changed in ({"expired": True}, {"digest": "sha256:" + "0" * 64},
                            {"workflow_run": {"id": 8, "head_sha": COMMIT}}):
                with self.subTest(identifier=identifier, changed=changed):
                    # Arrange
                    target = next(entry for entry in self.api.artifacts if entry["id"] == identifier)
                    target.update(changed)

                    # Act / Assert
                    with self.assertRaises(publication.PublicationAdmissionError):
                        publication.finalization_evidence(VERSION, "7", self.api, self.env)

                    self.api = FinalizationApi(self.root)

    def test_changed_candidate_is_rejected_before_completion(self):
        # Arrange
        receipt, _, _ = publication.finalization_evidence(VERSION, "7", self.api, self.env)
        next((self.root / "packages").glob("*.nupkg")).write_bytes(b"substituted")

        # Act / Assert
        with self.assertRaisesRegex(publication.PublicationAdmissionError, "candidate bytes differ"):
            publication.check_finalization(receipt, self.root, self.api, self.env)

    def test_release_replacement_and_permission_revocation_remain_blocking(self):
        for change in ("replace", "remove", "duplicate", "revoke"):
            with self.subTest(change=change):
                # Arrange
                self.api = FinalizationApi(self.root)
                receipt, _, _ = publication.finalization_evidence(VERSION, "7", self.api, self.env)
                if change == "replace":
                    self.api.releases[0]["id"] = 43
                elif change == "remove":
                    self.api.releases.clear()
                elif change == "duplicate":
                    self.api.releases.append(copy.deepcopy(self.api.releases[0]))
                else:
                    self.api.policy["lines"][0]["state"] = "inactive"

                # Act / Assert
                with self.assertRaises((publication.PublicationAdmissionError, publication.lines.LinePolicyError)):
                    publication.check_finalization(receipt, self.root, self.api, self.env)

    def test_archive_download_uses_the_rest_endpoint_media_type(self):
        # Arrange
        route = f"repos/{REPOSITORY}/actions/artifacts/3/zip"
        result = subprocess.CompletedProcess([], 0, b"zip content", b"")

        # Act
        with mock.patch.object(publication.subprocess, "run", return_value=result) as execute:
            observed = publication.GitHubClient().download(route)

        # Assert
        self.assertEqual(b"zip content", observed)
        self.assertIn("Accept: application/vnd.github+json", execute.call_args.args[0])
        self.assertNotIn("Accept: application/octet-stream", execute.call_args.args[0])

    def test_completion_requires_all_original_symbol_uploads(self):
        for index in range(5):
            with self.subTest(index=index):
                # Arrange
                self.api.jobs[-1]["steps"][index]["conclusion"] = "skipped"

                # Act / Assert
                with self.assertRaisesRegex(publication.PublicationAdmissionError, "symbol upload"):
                    publication.finalization_evidence(VERSION, "7", self.api, self.env)

                self.api.jobs[-1]["steps"][index]["conclusion"] = "success"

    def test_failed_job_retry_can_retain_successful_providers_from_prior_attempt(self):
        # Arrange
        receipt, _, _ = publication.finalization_evidence(VERSION, "7", self.api, self.env)
        identity = copy.deepcopy(receipt["originalIdentity"])
        identity["qualifiedArtifact"]["name"] = f"safe-migrations-release-{VERSION}-2"
        identity["provenanceArtifact"]["name"] = "safe-migrations-release-provenance-7-2"
        identity["originalPublicationAttempt"] = "2"
        original_jobs = copy.deepcopy(self.api.jobs)
        core_name = "Full reversible qualification / Core, performance, packages, and SBOM"
        retried = [{**job, "run_attempt": 2} for job in original_jobs
                   if job["name"] in (core_name, "Attest qualified packages", publication.PUBLISHER_JOB)]

        def pages(route, collection):
            """Expose only the actual executed jobs of each bounded attempt."""
            return copy.deepcopy(retried if "/attempts/2/" in route else original_jobs)

        # Act
        with mock.patch.object(self.api, "pages", side_effect=pages):
            publication._original_producers(self.api.line, identity, self.api)

        # Assert
        self.assertEqual(3, len(retried))
        self.assertEqual("1", identity["qualificationAttempt"])

    def test_retained_success_never_hides_a_newer_failed_or_skipped_provider(self):
        for conclusion in ("failure", "skipped", None):
            with self.subTest(conclusion=conclusion):
                # Arrange
                receipt, _, _ = publication.finalization_evidence(VERSION, "7", self.api, self.env)
                identity = copy.deepcopy(receipt["originalIdentity"])
                identity["qualifiedArtifact"]["name"] = f"safe-migrations-release-{VERSION}-2"
                identity["originalPublicationAttempt"] = "2"
                original_jobs = copy.deepcopy(self.api.jobs)
                retried = [{**original_jobs[1], "run_attempt": 2, "conclusion": conclusion}]

                # Act / Assert
                with mock.patch.object(self.api, "pages", side_effect=lambda route, collection:
                                       copy.deepcopy(retried if "/attempts/2/" in route else original_jobs)), \
                        self.assertRaisesRegex(publication.PublicationAdmissionError, "producer"):
                    publication._original_producers(self.api.line, identity, self.api)

    def test_retried_coverage_can_succeed_after_the_original_package_producer(self):
        # Arrange
        receipt, _, _ = publication.finalization_evidence(VERSION, "7", self.api, self.env)
        identity = copy.deepcopy(receipt["originalIdentity"])
        identity["provenanceArtifact"]["name"] = "safe-migrations-release-provenance-7-2"
        identity["originalPublicationAttempt"] = "2"
        original_jobs = copy.deepcopy(self.api.jobs)
        coverage = next(job for job in original_jobs if job["name"].endswith(" / Merged product coverage"))
        retried = [{**job, "run_attempt": 2} for job in original_jobs
                   if job["name"] in (coverage["name"], "Attest qualified packages", publication.PUBLISHER_JOB)]
        coverage["conclusion"] = "failure"

        # Act
        with mock.patch.object(self.api, "pages", side_effect=lambda route, collection:
                               copy.deepcopy(retried if "/attempts/2/" in route else original_jobs)):
            publication._original_producers(self.api.line, identity, self.api)

        # Assert
        self.assertTrue(identity["qualifiedArtifact"]["name"].endswith("-1"))

    def test_actual_cli_prepares_and_rechecks_original_bytes(self):
        # Arrange
        target = self.root / "release-lines"
        outputs = self.root / "outputs.txt"
        prepare = ["publication", "prepare-finalization", "--version", VERSION, "--original-run-id", "7",
                   "--output-dir", str(target)]

        # Act
        with mock.patch.dict(os.environ, {**self.env, "GITHUB_OUTPUT": str(outputs)}, clear=True), \
                mock.patch.object(publication, "GitHubClient", return_value=self.api), \
                mock.patch.object(sys, "argv", prepare):
            prepared = publication.main()

        receipt = target / "finalization.json"
        recheck = ["publication", "recheck-finalization", "--receipt", str(receipt),
                   "--receipt-sha256", publication.file_digest(receipt), "--artifact-dir", str(self.root)]
        with mock.patch.dict(os.environ, self.env, clear=True), \
                mock.patch.object(publication, "GitHubClient", return_value=self.api), \
                mock.patch.object(sys, "argv", recheck):
            checked = publication.main()

        # Assert
        self.assertEqual(0, prepared)
        self.assertEqual(0, checked)
        self.assertEqual(self.api.line_bytes, (target / "line.json").read_bytes())
        self.assertIn("original-run-id=7", outputs.read_text(encoding="ascii"))
        self.assertEqual("7", json.loads((target / "admission.json").read_bytes())["identity"]["workflowRunId"])
        with zipfile.ZipFile(io.BytesIO(self.api.archives[3])) as original_archive:
            self.assertEqual(original_archive.read("admission.json"), (target / "admission.json").read_bytes())


def skipped_steps():
    """Match the runner's completed/skipped steps, including precondition timestamps."""
    return [{"name": name, "status": "completed", "conclusion": "skipped",
             "started_at": "2026-10-25T03:00:00Z", "completed_at": "2026-10-25T03:00:00Z"}
            for name in sorted(publication.WRITE_STEPS)]


class ReleaseLineRecoveryTests(unittest.TestCase):
    """A skipped conclusion permits retry; an executed or ambiguous write never does."""

    def test_timestamped_skipped_write_steps_permit_the_actual_admission_retry(self):
        # Arrange
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            candidate(root)
            api = FakeGitHub()
            api.jobs[0].update(conclusion="failure", steps=skipped_steps())

            # Act
            receipt, restored = publication.admit(
                frozen(), LINE_HASH, root, QUALIFIED_NAME, PROVENANCE_NAME, api, environment("2"))

            # Assert
            self.assertFalse(restored)
            self.assertEqual("1", receipt["identity"]["qualificationAttempt"])
            self.assertEqual("2", receipt["identity"]["originalPublicationAttempt"])
            self.assertEqual(13, len(publication.candidate_manifest(root, receipt["identity"]["version"])))

    def test_skipped_step_timestamps_do_not_change_the_safe_history_verdict(self):
        for stamp in (None, "2026-10-25T03:00:00Z"):
            with self.subTest(stamp=stamp):
                # Arrange
                api = FakeGitHub()
                steps = skipped_steps()
                for step in steps:
                    step["started_at"] = stamp

                api.jobs[0].update(conclusion="failure", steps=steps)

                # Act
                publication.prior_publication_is_unstarted(frozen(), "2", api)

                # Assert
                self.assertEqual("jobs", api.calls[-1].split("/")[-1])

    def test_non_skipped_write_is_rejected_even_without_a_start_timestamp(self):
        for conclusion in ("success", "failure", "cancelled", "neutral", "unknown", None):
            with self.subTest(conclusion=conclusion):
                # Arrange
                api = FakeGitHub()
                steps = skipped_steps()
                steps[0].update(conclusion=conclusion, started_at=None)
                api.jobs[0].update(conclusion="failure", steps=steps)

                # Act / Assert
                with self.assertRaises(publication.PublicationAdmissionError):
                    publication.prior_publication_is_unstarted(frozen(), "2", api)

    def test_duplicate_skipped_name_cannot_hide_an_executed_write(self):
        # Arrange
        api = FakeGitHub()
        steps = skipped_steps()
        for step in steps:
            step["started_at"] = None

        executed = copy.deepcopy(steps[0])
        executed["conclusion"] = "success"
        api.jobs[0].update(conclusion="failure", steps=[executed, *steps])

        # Act / Assert
        with self.assertRaises(publication.PublicationAdmissionError):
            publication.prior_publication_is_unstarted(frozen(), "2", api)

    def test_duplicate_name_remains_ambiguous_even_when_every_write_is_skipped(self):
        # Arrange
        api = FakeGitHub()
        steps = skipped_steps()
        for step in steps:
            step["started_at"] = None

        api.jobs[0].update(conclusion="failure", steps=[*steps, copy.deepcopy(steps[0])])

        # Act / Assert
        with self.assertRaises(publication.PublicationAdmissionError):
            publication.prior_publication_is_unstarted(frozen(), "2", api)

    def test_missing_write_and_unfinished_or_missing_publisher_remain_blocked(self):
        for mutation in ("missing-write", "running-publisher", "missing-publisher"):
            with self.subTest(mutation=mutation):
                # Arrange
                api = FakeGitHub()
                api.jobs[0].update(conclusion="failure", steps=skipped_steps())
                if mutation == "missing-write":
                    api.jobs[0]["steps"].pop()
                elif mutation == "running-publisher":
                    api.jobs[0]["status"] = "in_progress"
                else:
                    api.jobs = []

                # Act / Assert
                with self.assertRaises(publication.PublicationAdmissionError):
                    publication.prior_publication_is_unstarted(frozen(), "2", api)


if __name__ == "__main__":
    unittest.main()
