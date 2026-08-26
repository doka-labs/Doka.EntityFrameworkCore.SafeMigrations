"""Regression tests for the vertical-slice architecture gate."""

from __future__ import annotations

import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]


class VerticalSliceGateTests(unittest.TestCase):
    """Keep the architecture gate independent of runner-specific tooling."""

    @staticmethod
    def _restricted_environment(tool_directory: Path) -> dict[str, str]:
        for command in ("dirname", "find", "grep"):
            executable = shutil.which(command)
            if executable is None:
                raise AssertionError(f"Required test command is unavailable: {command}")

            (tool_directory / command).symlink_to(executable)

        environment = os.environ.copy()
        environment["PATH"] = str(tool_directory)

        return environment

    @staticmethod
    def _run_validator(
        repository_root: Path,
        environment: dict[str, str],
    ) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            ["/bin/bash", str(repository_root / "eng" / "verify-vertical-slices.sh")],
            cwd=repository_root,
            env=environment,
            check=False,
            capture_output=True,
            text=True,
        )

    def test_repository_contract_passes_without_ripgrep(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            environment = self._restricted_environment(Path(directory))
            result = self._run_validator(REPOSITORY_ROOT, environment)

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("vertical-slice gate passed.\n", result.stdout)
        self.assertEqual("", result.stderr)

    def test_repository_contract_fails_when_slice_has_no_facts(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            temporary_root = Path(directory)
            repository_root = temporary_root / "repository"
            shutil.copytree(
                REPOSITORY_ROOT,
                repository_root,
                ignore=shutil.ignore_patterns(".git", "artifacts", "bin", "obj", "__pycache__"),
            )
            slice_root = (
                repository_root
                / "tests"
                / "Doka.EntityFrameworkCore.SafeMigrations.Tests"
                / "Unit"
                / "Features"
                / "Schemas"
            )
            for source_file in slice_root.glob("*.cs"):
                source_file.unlink()

            tool_directory = temporary_root / "tools"
            tool_directory.mkdir()
            environment = self._restricted_environment(tool_directory)
            result = self._run_validator(repository_root, environment)

        self.assertEqual(1, result.returncode, result.stdout)
        self.assertEqual("", result.stdout)
        self.assertIn(
            "core test slice has no facts: "
            "tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Unit/Features/Schemas",
            result.stderr,
        )


if __name__ == "__main__":
    unittest.main()
