"""Exercise isolated consumer staging and native configuration without restores or product builds."""

import json
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
MAINTENANCE_SCRIPT = REPOSITORY_ROOT / "eng/tests/test-dependency-maintenance.sh"


def solution_project_paths(workspace: Path) -> list[Path]:
    """Read the SDK's actual solution inventory without evaluating or restoring its projects."""
    result = subprocess.run(
        ["dotnet", "sln", "Doka.EntityFrameworkCore.SafeMigrations.slnx", "list"],
        cwd=workspace, capture_output=True, text=True, check=False,
    )
    if result.returncode != 0:
        raise AssertionError(result.stderr)

    return [Path(line) for line in result.stdout.splitlines() if line.endswith(".csproj")]


def stage_lock_inventory_fixture(workspace: Path) -> list[Path]:
    """Create only source-shaped project paths and locks; no copied outputs or restore is needed."""
    shutil.copy2(REPOSITORY_ROOT / "Doka.EntityFrameworkCore.SafeMigrations.slnx", workspace)
    shutil.copy2(REPOSITORY_ROOT / "global.json", workspace)
    projects = solution_project_paths(REPOSITORY_ROOT)
    for relative in projects:
        project = workspace / relative
        project.parent.mkdir(parents=True, exist_ok=True)
        packable = "true" if relative.parts[0] == "src" else "false"
        project.write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
            f'<IsPackable>{packable}</IsPackable></PropertyGroup></Project>', encoding="ascii",
        )
        (project.parent / "packages.lock.json").write_text(
            '{"version":1,"dependencies":{}}', encoding="ascii",
        )

    return projects


def execute_lock_inventory(workspace: Path) -> subprocess.CompletedProcess[str]:
    """Execute the maintained exact-inventory guard, not a parallel lockfile implementation."""
    source = MAINTENANCE_SCRIPT.read_text(encoding="ascii")
    start = source.index('expected_lockfiles="$(')
    end = source.index('package_project="')

    return subprocess.run(
        ["bash", "-euo", "pipefail", "-c", source[start:end]], cwd=workspace,
        capture_output=True, text=True, check=False,
    )


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

    def test_lock_inventory_accepts_all_24_current_solution_projects(self) -> None:
        """The real gate includes source, tests, benchmarks, engineering consumers and samples."""
        # Arrange
        expected_count = 24

        # Act
        projects = solution_project_paths(REPOSITORY_ROOT)
        result = execute_lock_inventory(REPOSITORY_ROOT)

        # Assert
        self.assertEqual(expected_count, len(projects))
        self.assertEqual(0, result.returncode, result.stderr)

    def test_lock_inventory_rejects_missing_lock_in_every_project_role(self) -> None:
        """Every default-profile project lock is required, not only shipping package locks."""
        for role in ("src", "tests", "benchmarks", "eng", "samples"):
            with self.subTest(role=role), tempfile.TemporaryDirectory() as temporary_root:
                # Arrange
                workspace = Path(temporary_root)
                projects = stage_lock_inventory_fixture(workspace)
                missing = next(project.parent / "packages.lock.json"
                               for project in projects if project.parts[0] == role)
                (workspace / missing).unlink()

                # Act
                result = execute_lock_inventory(workspace)

                # Assert
                self.assertNotEqual(0, result.returncode)
                self.assertIn("solution project inventory exactly", result.stderr)
                self.assertIn(missing.as_posix(), result.stderr)

    def test_lock_inventory_rejects_ordinary_extra_paths(self) -> None:
        """An unlisted engineering or lookalike artifact directory cannot hide an extra lock."""
        for relative in ("ordinary-extra/packages.lock.json", "eng/unlisted/packages.lock.json",
                         "artifacts-lookalike/packages.lock.json"):
            with self.subTest(path=relative), tempfile.TemporaryDirectory() as temporary_root:
                # Arrange
                workspace = Path(temporary_root)
                stage_lock_inventory_fixture(workspace)
                extra = workspace / relative
                extra.parent.mkdir(parents=True)
                extra.write_text("{}", encoding="ascii")

                # Act
                result = execute_lock_inventory(workspace)

                # Assert
                self.assertNotEqual(0, result.returncode)
                self.assertIn("solution project inventory exactly", result.stderr)
                self.assertIn(relative, result.stderr)

    def test_lock_inventory_ignores_only_exact_generated_directory_segments(self) -> None:
        """Git metadata and generated output locks do not become default-profile source locks."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary_root:
            workspace = Path(temporary_root)
            stage_lock_inventory_fixture(workspace)
            for relative in (".git/snapshot/packages.lock.json", "artifacts/nested/packages.lock.json",
                             "nested/bin/packages.lock.json", "nested/obj/packages.lock.json",
                             "nested/BenchmarkDotNet.Artifacts/packages.lock.json"):
                generated = workspace / relative
                generated.parent.mkdir(parents=True, exist_ok=True)
                generated.write_text("{}", encoding="ascii")

            # Act
            result = execute_lock_inventory(workspace)

            # Assert
            self.assertEqual(0, result.returncode, result.stderr)

    def test_lock_inventory_uses_sdk_inventory_after_project_source_reordering(self) -> None:
        """Valid solution/project layout changes cannot silently remove required project roles."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary_root:
            workspace = Path(temporary_root)
            projects = stage_lock_inventory_fixture(workspace)
            solution = ET.parse(workspace / "Doka.EntityFrameworkCore.SafeMigrations.slnx")
            for folder in solution.getroot().findall("Folder"):
                entries = folder.findall("Project")
                for entry in entries:
                    folder.remove(entry)
                for entry in reversed(entries):
                    folder.append(entry)
            solution.write(workspace / "Doka.EntityFrameworkCore.SafeMigrations.slnx", encoding="ascii")
            for relative in projects:
                packable = "true" if relative.parts[0] == "src" else "false"
                (workspace / relative).write_text(
                    '<Project Sdk="Microsoft.NET.Sdk">\n<PropertyGroup>\n'
                    '<IsTestProject>false</IsTestProject>\n<IsPackable\n>'
                    f'{packable}</IsPackable>\n</PropertyGroup>\n</Project>\n', encoding="ascii",
                )

            # Act
            result = execute_lock_inventory(workspace)

            # Assert
            self.assertEqual(0, result.returncode, result.stderr)

    def test_lock_inventory_rejects_empty_solution_without_vacuous_success(self) -> None:
        """An empty SDK project inventory is not a qualified default-lock contract."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary_root:
            workspace = Path(temporary_root)
            shutil.copy2(REPOSITORY_ROOT / "global.json", workspace)
            (workspace / "Doka.EntityFrameworkCore.SafeMigrations.slnx").write_text(
                "<Solution />", encoding="ascii",
            )

            # Act
            result = execute_lock_inventory(workspace)

            # Assert
            self.assertNotEqual(0, result.returncode)
            self.assertIn("solution project inventory is empty", result.stderr)

    def test_all_repository_roles_receive_the_reviewed_lock_and_pinning_policy(self) -> None:
        """Evaluate the actual root/source policy instead of simulating MSBuild conditions."""
        # Arrange
        projects = sorted(REPOSITORY_ROOT.glob("**/*.csproj"))
        generated_directories = {"artifacts", "bin", "obj", "BenchmarkDotNet.Artifacts"}
        projects = [project for project in projects
                    if not generated_directories.intersection(project.relative_to(REPOSITORY_ROOT).parts)]
        environment = {**os.environ, "GITHUB_ACTIONS": "true", "ContinuousIntegrationBuild": "false"}

        # Act
        observations = []
        for project in projects:
            result = subprocess.run(
                ["dotnet", "msbuild", str(project), "-nologo",
                 "-getProperty:RestorePackagesWithLockFile,RestoreLockedMode,CentralPackageTransitivePinningEnabled"],
                cwd=REPOSITORY_ROOT, env=environment, capture_output=True, text=True, check=False,
            )
            observations.append((project, result))

        # Assert
        self.assertEqual(24, len(observations))
        for project, result in observations:
            with self.subTest(project=project.relative_to(REPOSITORY_ROOT)):
                self.assertEqual(0, result.returncode, result.stderr)
                properties = json.loads(result.stdout)["Properties"]
                self.assertEqual("true", properties["RestorePackagesWithLockFile"])
                self.assertEqual("true", properties["RestoreLockedMode"])
                self.assertEqual("false" if project.is_relative_to(REPOSITORY_ROOT / "src") else "true",
                                 properties["CentralPackageTransitivePinningEnabled"])

    def test_distinct_runtime_and_isolated_package_profiles_do_not_inherit_default_locked_mode(self) -> None:
        """Default CI locks must not prevent creating a different explicit qualification graph."""
        for properties in (("-p:RuntimeIdentifier=linux-x64",),
                           ("-p:SafeMigrationsPackageConsumerMode=Package",)):
            with self.subTest(properties=properties):
                # Arrange
                project = next((CONSUMER_ROOT / "MySql").glob("*.csproj"))

                # Act
                result = subprocess.run(
                    ["dotnet", "msbuild", str(project), "-nologo", "-getProperty:RestoreLockedMode",
                     "-p:ContinuousIntegrationBuild=true", *properties],
                    cwd=REPOSITORY_ROOT, capture_output=True, text=True, check=False,
                )

                # Assert
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertEqual("", result.stdout.strip())

    def test_repository_nuget_config_clears_inherited_source_mappings(self) -> None:
        """The native NuGet merge must remove an inherited mapping, not merely its source."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary_root:
            root = Path(temporary_root)
            workspace = root / "workspace"
            workspace.mkdir()
            (root / "NuGet.Config").write_text(
                '<configuration><packageSourceMapping><packageSource key="canary-inherited-mapping">'
                '<package pattern="Impossible.Mapping.Canary" />'
                '</packageSource></packageSourceMapping></configuration>', encoding="ascii",
            )
            shutil.copy2(REPOSITORY_ROOT / "NuGet.config", workspace / "NuGet.Config")

            # Act
            result = subprocess.run(
                ["dotnet", "nuget", "config", "get", "all", "--working-directory", str(workspace),
                 "--force-english-output"], capture_output=True, text=True, check=False,
            )

            # Assert
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertNotIn('packageSource key="canary-inherited-mapping"', result.stdout)
            self.assertIn("https://api.nuget.org/v3/index.json", result.stdout)

    def test_missing_mapping_clear_really_exposes_the_inherited_canary(self) -> None:
        """The source-mapping check has a native negative control, not a vacuous string search."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary_root:
            root = Path(temporary_root)
            workspace = root / "workspace"
            workspace.mkdir()
            (root / "NuGet.Config").write_text(
                '<configuration><packageSourceMapping><packageSource key="canary-inherited-mapping">'
                '<package pattern="Impossible.Mapping.Canary" />'
                '</packageSource></packageSourceMapping></configuration>', encoding="ascii",
            )
            configuration = ET.parse(REPOSITORY_ROOT / "NuGet.config")
            mapping = configuration.getroot().find("packageSourceMapping")
            self.assertIsNotNone(mapping)
            configuration.getroot().remove(mapping)
            configuration.write(workspace / "NuGet.Config", encoding="ascii")

            # Act
            result = subprocess.run(
                ["dotnet", "nuget", "config", "get", "all", "--working-directory", str(workspace),
                 "--force-english-output"], capture_output=True, text=True, check=False,
            )

            # Assert
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn('packageSource key="canary-inherited-mapping"', result.stdout)

    def test_generated_directories_are_excluded_from_actual_compile_items(self) -> None:
        """Old local outputs and BDN children must not reenter the SDK's source glob."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary_root:
            workspace = Path(temporary_root)
            shutil.copy2(REPOSITORY_ROOT / "Directory.Build.props", workspace / "Directory.Build.props")
            shutil.copy2(REPOSITORY_ROOT / "global.json", workspace / "global.json")
            project = workspace / "Probe.csproj"
            project.write_text('<Project Sdk="Microsoft.NET.Sdk" />', encoding="ascii")
            for relative in ("Owned.cs", "nested/bin/Generated.cs", "nested/obj/Generated.cs",
                             "nested/BenchmarkDotNet.Artifacts/Generated.cs"):
                path = workspace / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("internal sealed class Probe { }", encoding="ascii")

            # Act
            result = subprocess.run(
                ["dotnet", "msbuild", str(project), "-nologo", "-getItem:Compile"],
                cwd=workspace, capture_output=True, text=True, check=False,
            )

            # Assert
            self.assertEqual(0, result.returncode, result.stderr)
            identities = [item["Identity"] for item in json.loads(result.stdout)["Items"]["Compile"]]
            self.assertEqual(["Owned.cs"], identities)

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
            "NuGet.config",
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
