"""Guard actual release workflow wiring and the narrow queue-schema adapter."""

import importlib.util
import json
import os
import re
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
            if name != "publish":
                self.assertNotIn("name: nuget", block)
                self.assertNotIn("NuGet/login@", block)

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
