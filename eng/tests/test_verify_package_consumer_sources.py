"""Exercise actual isolated consumer staging without NuGet, builds or databases."""

import os
import re
import shutil
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
CONSUMER_ROOT = REPOSITORY_ROOT / "eng/package-consumer"
SHARED_SOURCE = "GeneratedExpressionConsumerContract.cs"
PROVIDERS = ("MySql", "PostgreSql", "Sqlite", "SqlServer")


def stage_sources(script_directory: Path, workspace: Path) -> subprocess.CompletedProcess[str]:
    """Run the real pre-tool staging block, not a parallel copy implementation."""
    source = (REPOSITORY_ROOT / "eng/verify-package-consumer.sh").read_text(encoding="ascii")
    match = re.search(
        r'^mkdir -p "\$work_dir/\.config".*?(?=^dotnet tool restore)',
        source,
        flags=re.MULTILINE | re.DOTALL,
    )
    if match is None:
        raise AssertionError("The isolated consumer's actual source staging block was not found.")

    return subprocess.run(
        ["bash", "-eu", "-c", match.group()],
        env={**os.environ, "script_dir": str(script_directory), "work_dir": str(workspace)},
        capture_output=True,
        text=True,
        check=False,
    )


class VerifyPackageConsumerSourcesTests(unittest.TestCase):
    """Keep linked consumer sources present across all isolated tooling profiles."""

    def test_both_isolated_profile_paths_copy_all_provider_sources(self) -> None:
        """The real consumer and incremental-registration paths need the same complete source set."""
        # Arrange
        source = (REPOSITORY_ROOT / "eng/verify-package-consumer.sh").read_text(encoding="ascii")
        blocks = re.findall(
            r'^    mkdir -p "\$consumer_dir"\n.*?(?=^\n)',
            source, flags=re.MULTILINE | re.DOTALL,
        )
        self.assertEqual(2, len(blocks))
        with tempfile.TemporaryDirectory() as temporary_root:
            for index, block in enumerate(blocks):
                for provider in PROVIDERS:
                    with self.subTest(path=index, provider=provider):
                        project = next((CONSUMER_ROOT / provider).glob("*.csproj"))
                        destination = Path(temporary_root) / str(index) / provider

                        # Act
                        result = subprocess.run(
                            ["bash", "-eu", "-c", block], capture_output=True, text=True, check=False,
                            env={**os.environ, "script_dir": str(REPOSITORY_ROOT / "eng"),
                                 "consumer_name": provider, "consumer_dir": str(destination),
                                 "source_project": project.name, "consumer_project": str(destination / project.name)},
                        )

                        # Assert
                        self.assertEqual(0, result.returncode, result.stderr)
                        for original in (CONSUMER_ROOT / provider).glob("*.cs"):
                            staged = destination / original.name
                            self.assertTrue(staged.is_file(), f"Missing {provider} source: {original.name}")
                            self.assertEqual(original.read_bytes(), staged.read_bytes())

    def test_stages_every_shared_compile_link_for_all_providers(self) -> None:
        """Compile links must resolve from the staged provider/profile directory."""
        # Arrange
        links = []
        for provider in PROVIDERS:
            project = next((CONSUMER_ROOT / provider).glob("*.csproj"))
            for node in ET.parse(project).getroot().iter():
                include = node.attrib.get("Include", "")
                if node.tag.rsplit("}", 1)[-1] == "Compile" and include.startswith("../"):
                    links.append((provider, include))

        with tempfile.TemporaryDirectory() as temporary_root:
            workspace = Path(temporary_root)

            # Act
            result = stage_sources(REPOSITORY_ROOT / "eng", workspace)

            # Assert
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(set(PROVIDERS), {provider for provider, _ in links})
            for provider, include in links:
                staged = workspace / "eng/package-consumer" / f"{provider}-Design" / include
                original = CONSUMER_ROOT / provider / include
                self.assertTrue(staged.resolve().is_file(), f"Missing {provider} source: {include}")
                self.assertEqual(original.read_bytes(), staged.resolve().read_bytes())

    def test_missing_shared_source_fails_before_tool_restore(self) -> None:
        """A broken source snapshot must not proceed toward misleading build evidence."""
        # Arrange
        staging_inputs = (
            ".config/dotnet-tools.json",
            ".editorconfig",
            "Directory.Build.props",
            "global.json",
            "eng/Directory.Build.props",
            "eng/package-consumer/Directory.Build.props",
        )
        with tempfile.TemporaryDirectory() as temporary_root:
            root = Path(temporary_root)
            fixture = root / "fixture"
            workspace = root / "workspace"
            for relative in staging_inputs:
                destination = fixture / relative
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(REPOSITORY_ROOT / relative, destination)

            # Act
            result = stage_sources(fixture / "eng", workspace)

            # Assert
            self.assertNotEqual(0, result.returncode)
            self.assertIn(SHARED_SOURCE, result.stderr)
            self.assertFalse((workspace / "eng/package-consumer" / SHARED_SOURCE).exists())


if __name__ == "__main__":
    unittest.main()
