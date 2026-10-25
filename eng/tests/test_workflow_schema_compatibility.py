"""Pin native runner/cache policy independently of the older actionlint schema."""

import importlib.util
import json
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("workflow_schema", ROOT / "eng/verify-workflow-concurrency.py")
SCHEMA = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SCHEMA)
SOURCE = ("name: Probe\non: push\njobs:\n  test:\n    runs-on: ubuntu-26.04\n"
          "    cache-mode: none\n    steps:\n      - run: 'true'\n")
RUNNER_MESSAGE = ('label "ubuntu-26.04" is unknown. available labels are "ubuntu-24.04"'
                  '. if it is a custom label for self-hosted runner, set list of labels in actionlint.yaml config file')
CACHE_MESSAGE = ('unexpected key "cache-mode" for "job" section. expected one of "concurrency", "container", '
                 '"continue-on-error", "defaults", "env", "environment", "if", "name", "needs", "outputs", '
                 '"permissions", "runs-on", "secrets", "services", "snapshot", "steps", "strategy", '
                 '"timeout-minutes", "uses", "with"')


def workflow(directory: str, source: str = SOURCE) -> Path:
    """Write only the isolated workflow under test, never the repository configuration."""
    path = Path(directory) / "workflow.yml"
    path.write_text(source, encoding="ascii")

    return path


def diagnostics(path: Path) -> list[dict]:
    """Model the two observed old-schema messages without deriving source positions from the helper."""
    return [{"filepath": str(path), "line": 5, "kind": "runner-label", "message": RUNNER_MESSAGE},
            {"filepath": str(path), "line": 6, "kind": "syntax-check", "message": CACHE_MESSAGE}]


class WorkflowSchemaCompatibilityTests(unittest.TestCase):
    """Schema compatibility cannot authorize another runner, cache capability or ignored error."""

    def test_exact_source_bound_native_diagnostics_are_adapted(self):
        """Accept only the old linter's missing knowledge after independent field validation."""
        # Arrange
        with tempfile.TemporaryDirectory() as directory:
            path = workflow(directory)
            completed = subprocess.CompletedProcess([], 1, json.dumps(diagnostics(path)), "")

            # Act
            with mock.patch.object(SCHEMA.subprocess, "run", return_value=completed):
                result = SCHEMA.verify([path], "actionlint")

            # Assert
            self.assertEqual(0, result)

    def test_wrong_message_kind_path_or_position_remains_a_failure(self):
        """A genuine error or a copied compatibility message at another source location cannot disappear."""
        changes = ({"line": 6}, {"kind": "expression"}, {"filepath": "another.yml"},
                   {"message": RUNNER_MESSAGE.replace("26.04", "25.04")},
                   {"message": RUNNER_MESSAGE.replace('"ubuntu-24.04"', "not a label list")},
                   {"message": "label \"ubuntu-26.04\" is unknown: unrelated diagnostic"})
        for change in changes:
            with self.subTest(change=change), tempfile.TemporaryDirectory() as directory:
                # Arrange
                path = workflow(directory)
                messages = diagnostics(path)
                messages[0].update(change)
                completed = subprocess.CompletedProcess([], 1, json.dumps(messages), "")

                # Act
                with mock.patch.object(SCHEMA.subprocess, "run", return_value=completed):
                    result = SCHEMA.verify([path], "actionlint")

                # Assert
                self.assertEqual(1, result)

    def test_cache_diagnostic_requires_its_exact_source_field(self):
        """Neither another job key nor a misplaced cache error is an upstream-schema exemption."""
        changes = ({"line": 5}, {"kind": "expression"},
                   {"message": CACHE_MESSAGE.replace('"cache-mode"', '"unreviewed-key"')})
        for change in changes:
            with self.subTest(change=change), tempfile.TemporaryDirectory() as directory:
                # Arrange
                path = workflow(directory)
                messages = diagnostics(path)
                messages[1].update(change)
                completed = subprocess.CompletedProcess([], 1, json.dumps(messages), "")

                # Act
                with mock.patch.object(SCHEMA.subprocess, "run", return_value=completed):
                    result = SCHEMA.verify([path], "actionlint")

                # Assert
                self.assertEqual(1, result)

    def test_runner_and_cache_capabilities_are_validated_without_a_linter(self):
        """Offline contract checks also reject old/floating/ARM runners and executable cache policies."""
        values = [("ubuntu-26.04", value) for value in (
            "ubuntu-24.04", "ubuntu-22.04", "ubuntu-latest", "ubuntu-26.04-arm",
            "self-hosted", "${{ inputs.runner }}", "[ubuntu-26.04]", '"ubuntu-26.04"')]
        values.extend(("cache-mode: none", "cache-mode: " + value) for value in (
            "read", "write", "write-only", "${{ inputs.cache }}", '"none"', ""))
        for old, replacement in values:
            with self.subTest(value=replacement), tempfile.TemporaryDirectory() as directory:
                # Arrange
                path = workflow(directory, SOURCE.replace(old, replacement, 1))

                # Act / Assert
                with self.assertRaises(ValueError):
                    SCHEMA.verify([path])

    def test_duplicate_and_misplaced_native_policy_fields_are_rejected(self):
        """No dictionary overwrite or indentation ambiguity may authorize a capability."""
        sources = (
            SOURCE.replace("    runs-on: ubuntu-26.04\n", "    runs-on: ubuntu-26.04\n" * 2),
            SOURCE.replace("    cache-mode: none\n", "    cache-mode: none\n" * 2),
            SOURCE.replace("    runs-on:", "      runs-on:"),
            SOURCE.replace("    cache-mode:", "      cache-mode:"),
            "cache-mode: none\n" + SOURCE,
            "runs-on: ubuntu-26.04\n" + SOURCE,
        )
        for source in sources:
            with self.subTest(source=source), tempfile.TemporaryDirectory() as directory:
                # Arrange
                path = workflow(directory, source)

                # Act / Assert
                with self.assertRaises(ValueError):
                    SCHEMA.verify([path])

    def test_same_native_fields_are_allowed_in_separate_jobs(self):
        """The uniqueness boundary is the actual owning job, not the whole workflow."""
        # Arrange
        source = SOURCE + SOURCE.split("jobs:\n", 1)[1].replace("  test:\n", "  sibling:\n", 1)
        with tempfile.TemporaryDirectory() as directory:
            path = workflow(directory, source)

            # Act
            result = SCHEMA.verify([path])

            # Assert
            self.assertEqual(0, result)

    def test_duplicate_linter_messages_are_not_silently_deduplicated(self):
        """A repeated schema report is ambiguous evidence, not another supported missing feature."""
        # Arrange
        with tempfile.TemporaryDirectory() as directory:
            path = workflow(directory)
            messages = diagnostics(path)
            completed = subprocess.CompletedProcess([], 1, json.dumps(messages + messages[:1]), "")

            # Act
            with mock.patch.object(SCHEMA.subprocess, "run", return_value=completed):
                result = SCHEMA.verify([path], "actionlint")

            # Assert
            self.assertEqual(1, result)

    def test_duplicate_workflow_input_identity_is_rejected(self):
        """Aliases of the same source cannot overwrite independently validated evidence."""
        # Arrange
        with tempfile.TemporaryDirectory() as directory:
            path = workflow(directory)
            alias = path.parent / "." / path.name

            # Act / Assert
            with self.assertRaises(ValueError):
                SCHEMA.verify([path, alias])

    def test_incomplete_or_malformed_linter_output_fails_closed(self):
        """Missing diagnostics, malformed identities and stderr never produce clean lint evidence."""
        results = ((1, "", ""), (0, "", "warning"), (1, "[]", ""), (1, "{}", ""),
                   (1, "[null]", ""), (1, "not-json", ""),
                   (1, '[{"filepath": [], "line": 5, "kind": "runner-label", "message": "x"}]', ""),
                   (1, '[{"filepath": "x", "line": true, "kind": "runner-label", "message": "x"}]', ""))
        for code, output, error in results:
            with self.subTest(output=output, code=code), tempfile.TemporaryDirectory() as directory:
                # Arrange
                path = workflow(directory)
                completed = subprocess.CompletedProcess([], code, output, error)

                # Act / Assert
                with mock.patch.object(SCHEMA.subprocess, "run", return_value=completed):
                    with self.assertRaises(ValueError):
                        SCHEMA.verify([path], "actionlint")

    def test_unexpected_linter_exit_cannot_be_adapted(self):
        """A crash/nonstandard exit remains failed even if its stdout resembles known schema reports."""
        for code in (2, -9):
            with self.subTest(code=code), tempfile.TemporaryDirectory() as directory:
                # Arrange
                path = workflow(directory)
                completed = subprocess.CompletedProcess([], code, json.dumps(diagnostics(path)), "")

                # Act
                with mock.patch.object(SCHEMA.subprocess, "run", return_value=completed):
                    result = SCHEMA.verify([path], "actionlint")

                # Assert
                self.assertEqual(1, result)

    def test_clean_newer_linter_output_remains_a_success(self):
        """The bridge is not required once upstream recognizes the native fields itself."""
        # Arrange
        with tempfile.TemporaryDirectory() as directory:
            path = workflow(directory)
            completed = subprocess.CompletedProcess([], 0, "", "")

            # Act
            with mock.patch.object(SCHEMA.subprocess, "run", return_value=completed):
                result = SCHEMA.verify([path], "actionlint")

            # Assert
            self.assertEqual(0, result)

    @unittest.skipUnless(shutil.which("actionlint"), "Actual actionlint executable is not installed.")
    def test_actual_actionlint_uses_the_same_narrow_bridge(self):
        """Exercise a real linter process, not only the mocked diagnostic transport."""
        # Arrange
        with tempfile.TemporaryDirectory() as directory:
            path = workflow(directory)

            # Act
            result = SCHEMA.verify([path], "actionlint")

            # Assert
            self.assertEqual(0, result)

    @unittest.skipUnless(shutil.which("actionlint"), "Actual actionlint executable is not installed.")
    def test_actual_actionlint_keeps_an_unrelated_job_error_blocking(self):
        """The real process must still reject unknown keys next to the two supported native fields."""
        # Arrange
        source = SOURCE.replace("    steps:\n", "    unreviewed-key: true\n    steps:\n", 1)
        with tempfile.TemporaryDirectory() as directory:
            path = workflow(directory, source)

            # Act
            result = SCHEMA.verify([path], "actionlint")

            # Assert
            self.assertEqual(1, result)


if __name__ == "__main__":
    unittest.main()
