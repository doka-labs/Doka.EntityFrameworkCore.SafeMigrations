"""Pin and execute the five native BenchmarkDotNet workflow command boundaries."""

import os
import re
import shlex
import subprocess
import tempfile
import textwrap
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / ".github/workflows/quality-gates.yml"
SUITES = {
    "core": ("Core", ""),
    "mysql": ("MySQL and MariaDB", ".MySql"),
    "postgresql": ("PostgreSQL", ".PostgreSql"),
    "sqlite": ("SQLite", ".Sqlite"),
    "sqlserver": ("SQL Server", ".SqlServer"),
}


def benchmark_step(name):
    """Extract one maintained step without a YAML parser or copied shell implementation."""
    workflow = WORKFLOW.read_text(encoding="ascii")
    step = workflow.split(f"      - name: {name}\n", 1)[1].split("\n      - name:", 1)[0]
    command = textwrap.dedent(step.split("        run: >-\n", 1)[1]).strip().replace("\n", " ")

    return step, command


def execute_benchmark(command, exit_code):
    """Record actual workflow arguments and propagate a controlled benchmark process result."""
    with tempfile.TemporaryDirectory() as temporary_root:
        root = Path(temporary_root)
        arguments = root / "arguments.txt"
        recorder = root / "dotnet"
        recorder.write_text(
            '#!/usr/bin/env bash\nprintf "%s\\n" "$@" > "$BENCHMARK_ARGUMENTS"\n'
            'exit "$BENCHMARK_EXIT_CODE"\n', encoding="ascii")
        recorder.chmod(0o700)
        result = subprocess.run(
            ["bash", "-euo", "pipefail", "-c", command], cwd=root,
            env={**os.environ, "PATH": str(root) + os.pathsep + os.environ["PATH"],
                 "BENCHMARK_ARGUMENTS": str(arguments), "BENCHMARK_EXIT_CODE": str(exit_code)},
            capture_output=True, text=True, check=False, timeout=10)

        return result, arguments.read_text(encoding="ascii").splitlines()


class BenchmarkWorkflowTests(unittest.TestCase):
    """Smoke is complete execution/export evidence, never a hardware-dependent budget gate."""

    def test_all_five_suites_run_all_cases_as_native_dry_jobs(self):
        for suite, (label, suffix) in SUITES.items():
            with self.subTest(suite=suite):
                # Arrange
                step, command = benchmark_step(f"{label} BenchmarkDotNet execution smoke")
                project = f"Doka.EntityFrameworkCore.SafeMigrations{suffix}.Benchmarks"

                # Act
                result, arguments = execute_benchmark(command, 0)

                # Assert
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertEqual([
                    "run", "--project", f"benchmarks/{project}/{project}.csproj",
                    "--configuration", "Release", "--no-build", "--no-restore", "--",
                    "--filter", "*", "--job", "Dry", "--artifacts", f"artifacts/performance/{suite}",
                ], arguments)
                self.assertNotIn("        if:", step)

    def test_every_benchmark_process_failure_remains_fatal(self):
        names = [f"{label} BenchmarkDotNet execution smoke" for label, _ in SUITES.values()]
        names.append("Capture SQL Server performance baseline evidence")
        for name in names:
            for exit_code in (1, 23):
                with self.subTest(step=name, exit_code=exit_code):
                    # Arrange
                    _, command = benchmark_step(name)

                    # Act
                    result, _ = execute_benchmark(command, exit_code)

                    # Assert
                    self.assertEqual(exit_code, result.returncode, result.stderr)

    def test_optional_sqlserver_capture_is_a_short_native_run_with_required_artifacts(self):
        # Arrange
        workflow = WORKFLOW.read_text(encoding="ascii")
        step, command = benchmark_step("Capture SQL Server performance baseline evidence")

        # Act
        arguments = shlex.split(command)
        upload = workflow.split("      - name: Upload SQL Server baseline evidence\n", 1)[1] \
            .split("\n      - name:", 1)[0]

        # Assert
        self.assertEqual(["--filter", "*", "--job", "Short", "--artifacts",
                          "artifacts/performance/sqlserver-baseline"], arguments[arguments.index("--") + 1:])
        self.assertIn("if: ${{ inputs.capture-sqlserver-baseline }}", step)
        self.assertIn("path: artifacts/performance/sqlserver-baseline\n", upload)
        self.assertIn("if-no-files-found: error", upload)

    def test_numeric_limits_and_legacy_samplers_cannot_replace_or_weaken_qualification(self):
        # Arrange
        workflow = WORKFLOW.read_text(encoding="ascii")

        # Assert
        self.assertEqual(5, len(re.findall(r"--job Dry\b", workflow)))
        self.assertEqual(1, len(re.findall(r"--job Short\b", workflow)))
        for obsolete in ("--report-only", "--capture-baseline", "performance-budgets.json",
                         "continue-on-error:"):
            self.assertNotIn(obsolete, workflow)
        for gate in ("Core tests", "Qualify reproducible packages", "Generate and validate SPDX SBOM"):
            self.assertIn(f"- name: {gate}\n", workflow)
        self.assertIn("            artifacts/performance\n", workflow)


if __name__ == "__main__":
    unittest.main()
