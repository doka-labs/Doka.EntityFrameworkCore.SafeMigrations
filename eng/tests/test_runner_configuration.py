"""Execute hosted runner/image admission without starting SDK or server workloads."""

import os
import re
import shutil
import subprocess
import tempfile
import textwrap
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

    def test_database_version_literals_remain_text_in_matrix_and_environment(self):
        """YAML numeric coercion must not turn a native version such as 15.20 into 15.2."""
        # Arrange
        source = (ROOT / ".github/workflows/quality-gates.yml").read_text(encoding="ascii")

        # Act
        values = re.findall(r"^\s*(?:-\s+)?(?:version|SAFE_MIGRATIONS_[A-Z]+_VERSION):\s*([^\n]+)$",
                            source, re.MULTILINE)
        literals = [value for value in values if not value.startswith("${{")]

        # Assert
        self.assertEqual(17, len(literals))
        for value in literals:
            self.assertRegex(value, r"^(['\"])[0-9]+(?:\.[0-9]+)*\1$")

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


class CanonicalImageAdmissionTests(unittest.TestCase):
    """Reject inaccessible exact manifests before exporting native fixture identities."""

    def test_actual_image_resolution_steps_verify_every_exact_pin_before_export(self):
        source = (ROOT / ".github/workflows/quality-gates.yml").read_text(encoding="ascii")
        images = {row.split()[3]: row.split()[1] for row in
                  (ROOT / "docker/database-images.Dockerfile").read_text(encoding="ascii").splitlines()
                  if row.startswith("FROM ")}
        steps = {"Resolve canonical coverage images": ("mysql84", "postgres18", "sqlserver2022"),
                 "Resolve canonical MySQL or MariaDB image": ("mysql84",),
                 "Resolve canonical PostgreSQL image": ("postgres18",),
                 "Resolve canonical SQL Server image": ("sqlserver2022",)}
        variables = {"mysql84": "SAFE_MIGRATIONS_MYSQL_IMAGE", "postgres18": "SAFE_MIGRATIONS_POSTGRES_IMAGE",
                     "sqlserver2022": "SAFE_MIGRATIONS_SQLSERVER_IMAGE"}
        for name, targets in steps.items():
            for failing in (None, *dict.fromkeys((targets[0], targets[-1]))):
                with self.subTest(step=name, failing=failing), tempfile.TemporaryDirectory() as directory:
                    # Arrange
                    root = Path(directory)
                    (root / "eng").mkdir()
                    (root / "docker").mkdir()
                    shutil.copyfile(ROOT / "eng/select-database-image.sh", root / "eng/select-database-image.sh")
                    shutil.copyfile(ROOT / "docker/database-images.Dockerfile",
                                    root / "docker/database-images.Dockerfile")
                    native = root / "docker-client"
                    native.mkdir()
                    (native / "docker").write_text(
                        '#!/usr/bin/env bash\nset -euo pipefail\n'
                        '[[ "$#" == 3 && "$1 $2" == "manifest inspect" && "$3" == *@sha256:* ]] || exit 86\n'
                        'printf "%s\\n" "$3" >> "$IMAGE_LOG"\n'
                        '[[ "$3" != "$FAIL_IMAGE" ]] || exit 7\n', encoding="ascii")
                    (native / "docker").chmod(0o755)
                    log = root / "images.log"
                    exported = root / "env"
                    exported.write_text("", encoding="ascii")
                    step = source.split(f"- name: {name}\n", 1)[1].split("\n      - name:", 1)[0]
                    command = textwrap.dedent(step.split("        run: |\n", 1)[1])
                    stop = len(targets) if failing is None else targets.index(failing)
                    env = {**os.environ, **{value: key for key, value in variables.items()},
                           "PATH": str(native) + os.pathsep + os.environ["PATH"], "GITHUB_ENV": str(exported),
                           "IMAGE_LOG": str(log), "FAIL_IMAGE": images[failing] if failing else ""}

                    # Act
                    result = subprocess.run(["bash", "-euo", "pipefail", "-c", command], cwd=root, env=env,
                                            capture_output=True, text=True, check=False, timeout=5)

                    # Assert
                    self.assertEqual(0 if failing is None else 7, result.returncode, result.stderr)
                    self.assertEqual([images[target] for target in targets[:stop + (failing is not None)]],
                                     log.read_text(encoding="ascii").splitlines() if log.exists() else [])
                    self.assertEqual([f"{variables[target]}={images[target]}" for target in targets[:stop]],
                                     exported.read_text(encoding="ascii").splitlines())


if __name__ == "__main__":
    unittest.main()
