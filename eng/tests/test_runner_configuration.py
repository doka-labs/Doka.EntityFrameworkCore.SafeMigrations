"""Execute the actual hosted-runner gate without starting SDK or server workloads."""

import re
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
MAINTENANCE = ROOT / "eng/tests/test-dependency-maintenance.sh"


def runner_guard_command() -> str:
    """Use the maintained shell guard, not a duplicated test implementation."""
    source = MAINTENANCE.read_text(encoding="ascii")
    start = source.index('floating_runner_references="$(')
    end = source.index('expected_lockfiles="$(')

    return source[start:end]


def execute_guard(directory: str, *labels: str) -> subprocess.CompletedProcess:
    """Run only runner admission against isolated synthetic workflow files."""
    workflows = Path(directory) / ".github/workflows"
    workflows.mkdir(parents=True)
    for index, label in enumerate(labels):
        (workflows / f"fixture-{index}.yml").write_text(
            f"name: Fixture\non: push\njobs:\n  test:\n    runs-on: {label}\n"
            "    steps:\n      - run: 'true'\n", encoding="ascii")

    return subprocess.run(["bash", "-euo", "pipefail", "-c", runner_guard_command()],
                          cwd=directory, capture_output=True, text=True, check=False)


class HostedRunnerConfigurationTests(unittest.TestCase):
    """Require explicit Ubuntu 26.04 x64 everywhere without floating fallback."""

    def test_real_workflows_use_only_the_explicit_supported_x64_runner(self):
        """The actual producer, publisher, maintenance and security jobs share one host contract."""
        # Arrange
        workflows = sorted((ROOT / ".github/workflows").glob("*.yml"))

        # Act
        runners = [(path.name, match[1]) for path in workflows
                   for match in re.finditer(r"^    runs-on: (.+)$", path.read_text(encoding="ascii"), re.M)]

        # Assert
        self.assertTrue(runners)
        self.assertTrue(all(label == "ubuntu-26.04" for _, label in runners), runners)

    def test_exact_runner_is_accepted_across_independent_workflows(self):
        """The gate accepts more than one owned workflow without SDK evaluation."""
        # Arrange
        with tempfile.TemporaryDirectory() as directory:
            # Act
            result = execute_guard(directory, "ubuntu-26.04", "ubuntu-26.04")

            # Assert
            self.assertEqual(0, result.returncode, result.stderr)

    def test_old_floating_other_architectures_and_indirect_runners_are_rejected(self):
        """An unreviewed host must not evade the guard by changing scalar syntax."""
        for label in ("ubuntu-22.04", "ubuntu-24.04", "ubuntu-latest", "ubuntu-26.04-arm",
                      "ubuntu-28.04", "'ubuntu-24.04'", "[ubuntu-26.04, x64]",
                      "${{ inputs.runner }}", "windows-latest", "self-hosted"):
            with self.subTest(label=label), tempfile.TemporaryDirectory() as directory:
                # Arrange
                expected = "Workflow runners must not use floating Ubuntu labels." if label == "ubuntu-latest" \
                    else "GitHub-hosted jobs must use the pinned ubuntu-26.04 x64 runner."

                # Act
                result = execute_guard(directory, "ubuntu-26.04", label)

                # Assert
                self.assertNotEqual(0, result.returncode)
                self.assertIn(expected, result.stderr)

    def test_one_remaining_old_runner_is_not_hidden_by_valid_jobs(self):
        """A partially migrated set fails even when the required new runner is present."""
        # Arrange
        with tempfile.TemporaryDirectory() as directory:
            # Act
            result = execute_guard(directory, "ubuntu-26.04", "ubuntu-24.04", "ubuntu-26.04")

            # Assert
            self.assertNotEqual(0, result.returncode)
            self.assertIn("ubuntu-24.04", result.stderr)

    def test_missing_hosted_runner_inventory_is_not_vacuous_success(self):
        """A reusable-only or empty fixture cannot establish a pinned runner contract."""
        # Arrange
        with tempfile.TemporaryDirectory() as directory:
            # Act
            result = execute_guard(directory)

            # Assert
            self.assertNotEqual(0, result.returncode)
            self.assertIn("At least one pinned ubuntu-26.04 x64 workflow job is required.", result.stderr)


if __name__ == "__main__":
    unittest.main()
