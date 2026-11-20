"""Guard actual release workflow wiring and the narrow queue-schema adapter."""

import importlib.util
import json
import os
import re
import shutil
import subprocess
import tempfile
import textwrap
import unittest
from pathlib import Path
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("workflow_queue", ROOT / "eng/verify-workflow-concurrency.py")
QUEUE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(QUEUE)


def jobs(path: Path) -> dict[str, str]:
    """Inspect real job boundaries without adding a YAML parser dependency."""
    source = path.read_text(encoding="ascii").split("jobs:\n", 1)[1]
    matches = list(re.finditer(r"^  ([a-z-]+):\n", source, re.MULTILINE))

    return {match[1]: source[match.start():matches[index + 1].start()
                            if index + 1 < len(matches) else len(source)]
            for index, match in enumerate(matches)}


class ReleaseWorkflowTests(unittest.TestCase):
    """Keep candidate qualification, authority and irreversible boundaries distinct."""

    def test_qualification_only_skips_the_entire_existing_publisher(self):
        # Arrange
        source = ROOT / ".github/workflows/release-candidate.yml"

        # Act
        blocks = jobs(source)
        publisher = blocks["publish"]

        # Assert
        self.assertIn("if: ${{ !inputs.qualification-only && "
                      "needs.preflight.outputs.publish-allowed == 'true' }}", publisher)
        self.assertIn("environment:\n      name: nuget", publisher)
        self.assertIn("group: safe-migrations-publication\n"
                      "      cancel-in-progress: false\n      queue: max", publisher)
        for name, block in blocks.items():
            if name not in ("publish", "finalize"):
                self.assertNotIn("name: nuget", block)
                self.assertNotIn("NuGet/login@", block)

    def test_version_intent_reaches_both_validation_and_frozen_authority(self):
        # Arrange
        preflight = jobs(ROOT / ".github/workflows/release-candidate.yml")["preflight"]
        validation = preflight.split("- name: Validate release version\n", 1)[1].split("\n      - name:", 1)[0]

        # Act / Assert
        self.assertIn("QUALIFICATION_ONLY: ${{ inputs.qualification-only }}", validation)
        self.assertIn('--qualification-only "$QUALIFICATION_ONLY"', validation)
        self.assertIn('--qualification-only "$QUALIFICATION_ONLY"', preflight)
        self.assertIn("- name: Require an unused NuGet version", preflight)
        self.assertIn("- name: Require the candidate tag to remain absent", preflight)
    def test_finalization_is_a_separate_nonbuilding_nonuploading_protected_path(self):
        # Arrange
        blocks = jobs(ROOT / ".github/workflows/release-candidate.yml")

        # Act
        finish = blocks["finalize"]

        # Assert
        self.assertIn("if: ${{ inputs.finalize-run-id == '' || inputs.qualification-only }}", blocks["preflight"])
        self.assertIn("if: ${{ inputs.finalize-run-id != '' && !inputs.qualification-only }}", finish)
        self.assertIn("environment:\n      name: nuget", finish)
        self.assertIn("group: safe-migrations-publication", finish)
        self.assertIn("cancel-in-progress: false\n      queue: max", finish)
        for forbidden in ("dotnet build", "dotnet pack", "dotnet nuget push", "NuGet/login@", "id-token: write",
                          "release upload", "release create", "gh workflow run", "git tag -", "git push"):
            self.assertNotIn(forbidden, finish)

        self.assertNotIn("uses: ./.github/workflows/quality-gates.yml", finish)
        self.assertEqual(2, finish.count("artifact-ids: ${{ steps.original.outputs."))
        self.assertEqual(2, finish.count("run-id: ${{ steps.original.outputs.original-run-id }}"))
        self.assertIn('--signer-digest "$SOURCE_COMMIT"', finish)
        self.assertIn('--source-ref "$SOURCE_REF" --source-digest "$SOURCE_COMMIT"', finish)
        self.assertIn('verify-tag "$tag"', finish)
        self.assertIn('reconcile-github-release.sh finalize "$PACKAGE_VERSION"', finish)
        self.assertIn("FINALIZATION_RELEASE_ID: ${{ steps.original.outputs.release-id }}", finish)

    def test_finalization_cannot_publish_before_all_original_and_public_checks(self):
        # Arrange
        finish = jobs(ROOT / ".github/workflows/release-candidate.yml")["finalize"]

        # Act
        positions = [finish.index("- name: " + name) for name in (
            "Restore original admission and successful qualification",
            "Download original qualified packages without rebuilding", "Download original portable SLSA provenance",
            "Verify original tag and exact qualified content",
            "Cryptographically verify original portable SLSA provenance",
            "Verify all existing public NuGet signatures and package contents",
            "Recheck authorization and complete only the existing GitHub Release")]

        # Assert
        self.assertEqual(sorted(positions), positions)
        self.assertEqual(2, finish.count("publication recheck-finalization"))
        self.assertIn('--receipt-sha256 "$RECEIPT_SHA256"', finish)
        self.assertIn("retention-days: 30", finish)

    def test_conflicting_intent_fails_before_any_protected_job(self):
        # Arrange
        preflight = jobs(ROOT / ".github/workflows/release-candidate.yml")["preflight"]
        body = preflight.split("- name: Reject incompatible qualification and finalization intent\n", 1)[1]
        command = textwrap.dedent(body.split("        run: |\n", 1)[1].split("\n      - name:", 1)[0])

        # Act
        result = subprocess.run(["bash", "-euo", "pipefail", "-c", command],
                                env={**os.environ, "FINALIZE_RUN_ID": "7"}, capture_output=True, text=True,
                                check=False, timeout=5)

        # Assert
        self.assertEqual(2, result.returncode)
        self.assertIn("Qualification-only cannot finalize", result.stderr)

    def test_original_artifact_and_receipt_identities_survive_failed_job_retry(self):
        # Arrange
        source = (ROOT / ".github/workflows/release-candidate.yml").read_text(encoding="ascii")

        # Act
        publisher = jobs(ROOT / ".github/workflows/release-candidate.yml")["publish"]

        # Assert
        self.assertIn("line-sha256: ${{ steps.line.outputs.line-sha256 }}", source)
        self.assertIn("name: ${{ needs.preflight.outputs.line-artifact-name }}", publisher)
        self.assertIn("name: ${{ needs.quality-gates.outputs.qualified-artifact-name }}", publisher)
        self.assertIn("name: ${{ needs.attest.outputs.provenance_artifact_name }}", publisher)
        self.assertIn("if: steps.admission.outputs.admission-restored != 'true'", publisher)
        self.assertEqual(3, publisher.count('--admission-sha256 "$ADMISSION_SHA256"'))
        self.assertNotIn("github.run_attempt", publisher)

    def test_real_write_steps_follow_signature_provenance_and_current_admission(self):
        # Arrange
        publisher = jobs(ROOT / ".github/workflows/release-candidate.yml")["publish"]

        # Act
        ordered = [publisher.index("- name: " + name) for name in (
            "Verify source and authorized signed tag", "Verify qualified bytes again",
            "Verify portable SLSA provenance", "Admit the exact candidate under current protected-main authority",
            "Retain original publication admission", "Recheck current permission before creating public draft state",
            "Prepare verified GitHub Release draft", "Recheck current permission before NuGet credentials",
            "Request short-lived NuGet.org key", "Publish Core package", "Verify public NuGet packages",
            "Publish or verify immutable GitHub Release")]

        # Assert
        self.assertEqual(sorted(ordered), ordered)
        self.assertIn('--source-ref "$GITHUB_REF"', publisher)
        self.assertNotIn("--source-ref refs/heads/main", publisher)
        self.assertIn('verify-tag "$RELEASE_TAG"', publisher)
        self.assertNotIn("dotnet build", publisher)
        self.assertNotIn("dotnet pack", publisher)

    def test_ordinary_ci_is_nonpublishing_and_scheduled_identity_is_a_dependency(self):
        # Arrange
        source = ROOT / ".github/workflows/ci.yml"

        # Act
        blocks = jobs(source)

        # Assert
        self.assertIn("needs: schedule-identity", blocks["quality-gates"])
        self.assertNotIn("    if:", blocks["quality-gates"])
        self.assertIn("if: ${{ !inputs.maintenance }}", blocks["schedule-identity"])
        self.assertIn("guard --workflow ci.yml", blocks["schedule-identity"])
        self.assertIn("INPUT_SOURCE_SHA: ${{ inputs.schedule-source-sha }}", blocks["schedule-identity"])
        self.assertNotIn("NuGet/login@", source.read_text(encoding="ascii"))

    def test_existing_tag_rejection_executes_before_qualification_identity_upload(self):
        source = (ROOT / ".github/workflows/release-candidate.yml").read_text(encoding="ascii")
        name = "Require the candidate tag to remain absent"
        step = source.split("- name: " + name + "\n", 1)[1].split("\n      - name:", 1)[0]
        command = textwrap.dedent(step.split("        run: |\n", 1)[1])
        for tag_exists in (False, True):
            with self.subTest(tag_exists=tag_exists), tempfile.TemporaryDirectory() as directory:
                # Arrange
                git = Path(directory) / "git"
                git.write_text("#!/bin/sh\nexit " + ("0" if tag_exists else "1") + "\n", encoding="ascii")
                git.chmod(0o755)

                # Act
                result = subprocess.run(["bash", "-euo", "pipefail", "-c", command],
                                        env={**os.environ, "PATH": directory + os.pathsep + os.environ["PATH"],
                                             "RELEASE_TAG": "v10.6.0"},
                                        capture_output=True, text=True, check=False)

                # Assert
                self.assertEqual(1 if tag_exists else 0, result.returncode)
                self.assertEqual(tag_exists, "before the release tag exists" in result.stderr)
                self.assertLess(source.index("- name: " + name),
                                source.index("- name: Retain exact source-line qualification identity"))




class ReleaseVersionTests(unittest.TestCase):
    """Exercise the real validator against isolated released source snapshots."""

    def setUp(self):
        """Copy only the existing validator; own all source and output fixtures."""
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        (self.root / "eng").mkdir()
        (self.root / "src").mkdir()
        self.validator = self.root / "eng/validate-release-version.sh"
        shutil.copyfile(ROOT / "eng/validate-release-version.sh", self.validator)
        self.write_source()

    def write_source(self, source_version="10.7.3"):
        """Arrange a released source snapshot independently of this repository's version."""
        (self.root / "src/Directory.Build.props").write_text(
            f"<Project><PropertyGroup><VersionPrefix>{source_version}</VersionPrefix></PropertyGroup></Project>\n",
            encoding="ascii")
        (self.root / "CHANGELOG.md").write_text(f"## [{source_version}] - 2026-11-20\n", encoding="ascii")

    def validate(self, version, *arguments):
        """Return process evidence without contacting GitHub or public package feeds."""
        return subprocess.run(["bash", str(self.validator), version, *arguments], cwd=self.root,
                              capture_output=True, text=True, check=False, timeout=5)

    def test_qualification_accepts_unused_same_major_versions_without_source_changes(self):
        for source_version, version in (("10.7.3", "10.7.4-rc.1"), ("10.7.3", "10.8.0"),
                                        ("11.0.0", "11.0.1-rc.1")):
            with self.subTest(source=source_version, version=version):
                # Arrange
                expected = f"## [{source_version}] - 2026-11-20\n"
                self.write_source(source_version)

                # Act
                result = self.validate(version, "--qualification-only", "true")

                # Assert
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertIn(version, result.stdout)
                self.assertEqual(expected, (self.root / "CHANGELOG.md").read_text(encoding="ascii"))
                self.assertIn(f"<VersionPrefix>{source_version}</VersionPrefix>",
                              (self.root / "src/Directory.Build.props").read_text(encoding="ascii"))

    def test_publication_keeps_exact_source_and_dated_changelog_requirements(self):
        for version in ("10.7.4-rc.1", "10.7.3-rc.1", "10.8.0"):
            for arguments in ((), ("--qualification-only", "false")):
                with self.subTest(version=version, arguments=arguments):
                    # Arrange / Act
                    result = self.validate(version, *arguments)

                    # Assert
                    self.assertNotEqual(0, result.returncode)

    def test_qualification_rejects_foreign_major_and_noncanonical_versions(self):
        for version in ("11.0.0-rc.1", "9.9.9", "v10.7.4", "10.7.4-RC.1", "10.7.4-rc.01",
                        "10.7.4+build.1", "10.7.4--rc", "010.7.4", "10.7.4\n", ""):
            with self.subTest(version=version):
                # Arrange / Act
                result = self.validate(version, "--qualification-only", "true")

                # Assert
                self.assertNotEqual(0, result.returncode)

    def test_invalid_intent_cannot_skip_publication_validation(self):
        for arguments in (("--qualification-only",), ("--qualification-only", "TRUE"),
                          ("--qualification-only", "1"), ("--qualification-only", ""),
                          ("--unknown", "true"), ("--qualification-only", "true", "extra")):
            with self.subTest(arguments=arguments):
                # Arrange / Act
                result = self.validate("10.7.3", *arguments)

                # Assert
                self.assertNotEqual(0, result.returncode)

    def test_publication_rejects_missing_or_duplicate_dated_release_notes(self):
        for content in ("", "## [10.7.3] - pending\n", "## [10.7.3] - 2026-11-20\n" * 2):
            with self.subTest(content=content):
                # Arrange
                (self.root / "CHANGELOG.md").write_text(content, encoding="ascii")

                # Act
                result = self.validate("10.7.3", "--qualification-only", "false")

                # Assert
                self.assertEqual(1, result.returncode)
                self.assertIn("exactly one dated entry", result.stderr)

    def test_qualification_rejects_missing_malformed_or_ambiguous_source_version(self):
        for content in ("<Project />", "<VersionPrefix>10.07.3</VersionPrefix>",
                        "<VersionPrefix>10.7.3</VersionPrefix>\n<VersionPrefix>11.0.0</VersionPrefix>"):
            with self.subTest(content=content):
                # Arrange
                (self.root / "src/Directory.Build.props").write_text(content, encoding="ascii")

                # Act
                result = self.validate("10.7.4-rc.1", "--qualification-only", "true")

                # Assert
                self.assertEqual(1, result.returncode)
                self.assertIn("canonical stable VersionPrefix", result.stderr)

    def test_real_workflow_command_binds_intent_to_version_outputs(self):
        for qualification_only, version, expected_exit in (("true", "10.7.4-rc.1", 0),
                                                          ("false", "10.7.4-rc.1", 1),
                                                          ("false", "10.7.3", 0)):
            with self.subTest(intent=qualification_only, version=version):
                # Arrange
                preflight = jobs(ROOT / ".github/workflows/release-candidate.yml")["preflight"]
                step = preflight.split("- name: Validate release version\n", 1)[1].split("\n      - name:", 1)[0]
                command = step.split("        run: ", 1)[1].strip()
                output = self.root / "outputs.txt"
                output.write_text("", encoding="ascii")
                env = {**os.environ, "PACKAGE_VERSION": version, "QUALIFICATION_ONLY": qualification_only,
                       "GITHUB_OUTPUT": str(output)}

                # Act
                result = subprocess.run(["bash", "-euo", "pipefail", "-c", command], env=env, cwd=self.root,
                                        capture_output=True, text=True, check=False, timeout=5)

                # Assert
                self.assertEqual(expected_exit, result.returncode, result.stderr)
                expected = f"package-version={version}\nrelease-tag=v{version}\n" if expected_exit == 0 else ""
                self.assertEqual(expected, output.read_text(encoding="ascii"))



class QueueContractTests(unittest.TestCase):
    """The compatibility path must reject bad queues and every unrelated lint failure."""

    def test_valid_top_and_job_queues_have_exact_proven_positions(self):
        # Arrange
        source = "concurrency:\n  group: x\n  cancel-in-progress: false\n  queue: max\njobs:\n" \
                 "  publish:\n    concurrency:\n      group: y\n      queue: single\n"

        # Act
        positions = QUEUE.queue_positions(source)

        # Assert
        self.assertEqual({4, 9}, positions)

    def test_invalid_values_duplicates_and_cancellation_fail(self):
        cases = ("  queue: all\n", "  queue: ${{ inputs.queue }}\n",
                 "  queue: max\n  queue: max\n", "  queue: max\n  cancel-in-progress: true\n",
                 "  queue: max\n")
        for tail in cases:
            with self.subTest(tail=tail):
                # Arrange
                source = "concurrency:\n  group: x\n" + tail

                # Act / Assert
                with self.assertRaises(ValueError):
                    QUEUE.queue_positions(source)

    def test_only_exact_separately_validated_queue_diagnostic_is_adapted(self):
        # Arrange
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "workflow.yml"
            path.write_text("concurrency:\n  group: x\n  cancel-in-progress: false\n  queue: max\n",
                            encoding="ascii")
            diagnostic = {"filepath": str(path), "line": 4, "kind": "syntax-check",
                          "message": QUEUE.QUEUE_DIAGNOSTIC}
            completed = subprocess.CompletedProcess([], 1, json.dumps([diagnostic]), "")

            # Act
            with mock.patch.object(QUEUE.subprocess, "run", return_value=completed):
                result = QUEUE.verify([path], "actionlint")

            # Assert
            self.assertEqual(0, result)

    def test_wrong_location_and_unrelated_diagnostics_remain_failures(self):
        for changed in ({"line": 3}, {"kind": "expression"}, {"message": "real failure"}):
            with self.subTest(changed=changed), tempfile.TemporaryDirectory() as directory:
                # Arrange
                path = Path(directory) / "workflow.yml"
                path.write_text("concurrency:\n  group: x\n  cancel-in-progress: false\n  queue: max\n",
                                encoding="ascii")
                diagnostic = {"filepath": str(path), "line": 4, "kind": "syntax-check",
                              "message": QUEUE.QUEUE_DIAGNOSTIC, **changed}
                completed = subprocess.CompletedProcess([], 1, json.dumps([diagnostic]), "")

                # Act
                with mock.patch.object(QUEUE.subprocess, "run", return_value=completed):
                    result = QUEUE.verify([path], "actionlint")

                # Assert
                self.assertEqual(1, result)


if __name__ == "__main__":
    unittest.main()
