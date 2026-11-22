"""Exercise isolated consumer staging and SDK publishing without building product packages."""

import json
import os
import re
import shutil
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path
from unittest import mock


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


def stage_bundle_configuration_fixture(workspace: Path, *, test_owner: bool = True) -> tuple[Path, Path]:
    """Use actual build/restore contracts with executable projects that have no authored package references."""
    for name in ("global.json", "Directory.Build.targets"):
        shutil.copy2(REPOSITORY_ROOT / name, workspace / name)

    # WHY: Single-file publishing restores implicit SDK support packages even
    # without authored references. An empty source list only works with a warm cache.
    shutil.copy2(REPOSITORY_ROOT / "NuGet.config", workspace / "NuGet.Config")
    framework = ET.parse(REPOSITORY_ROOT / "Directory.Build.props").findtext(".//TargetFramework")
    (workspace / "Directory.Build.props").write_text(
        f'<Project><PropertyGroup><TargetFramework>{framework}</TargetFramework>'
        '<SelfContained>false</SelfContained></PropertyGroup></Project>',
        encoding="ascii",
    )
    owner = workspace / "Owner/Owner.csproj"
    host = workspace / "Host/Host.csproj"
    for project in (owner, host):
        project.parent.mkdir()

    owner.write_text(
        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
        f'<IsTestProject>{str(test_owner).lower()}</IsTestProject></PropertyGroup></Project>', encoding="ascii",
    )
    (owner.parent / "Program.cs").write_text(
        'public static class Owner { public static string Value => "bundle-owner-ok"; '
        'public static void Main() => System.Console.WriteLine(Value); }', encoding="ascii",
    )
    host.write_text(
        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup>'
        '<ItemGroup><ProjectReference Include="../Owner/Owner.csproj" /></ItemGroup></Project>', encoding="ascii",
    )
    (host.parent / "Program.cs").write_text('System.Console.WriteLine(Owner.Value);', encoding="ascii")

    return owner, host


def publish_configuration_fixture(
    project: Path, destination: Path, *, environment: dict[str, str] | None = None,
) -> subprocess.CompletedProcess[str]:
    """Publish with normal SDK analyzers and optional caller-owned restore caches; expose the first failure."""
    rid = subprocess.run(
        ["dotnet", "msbuild", str(project), "-getProperty:NETCoreSdkRuntimeIdentifier"],
        cwd=project.parent, env=environment, capture_output=True, text=True, check=True,
    ).stdout.strip()

    result = subprocess.run(
        ["dotnet", "publish", str(project), "-c", "Release", "-r", rid,
         "-p:PublishSingleFile=true", "-p:SelfContained=false",
         "-o", str(destination)],
        cwd=project.parent, env=environment, capture_output=True, text=True, check=False,
    )

    # WHY: A restore/publish failure must not be masked by a later missing DLL or executable.
    if result.returncode != 0:
        raise AssertionError(f"Native SDK publish failed: exit code {result.returncode}\n"
                             + result.stdout + result.stderr)

    return result


class VerifyPackageConsumerSourcesTests(unittest.TestCase):
    """Keep linked consumer sources present across all isolated tooling profiles."""

    def test_bundle_fixture_uses_repository_restore_configuration(self) -> None:
        """The native probe retains the reviewed sources and inherited-mapping isolation on fresh runners."""
        # Arrange
        with tempfile.TemporaryDirectory() as directory:
            workspace = Path(directory)

            # Act
            stage_bundle_configuration_fixture(workspace)

            # Assert
            self.assertEqual((REPOSITORY_ROOT / "NuGet.config").read_bytes(),
                             (workspace / "NuGet.Config").read_bytes())

    def test_bundle_publish_reports_restore_failure_before_output_discovery(self) -> None:
        """Keep SDK diagnostics and cache ownership observable instead of secondary missing-file failures."""
        # Arrange
        with tempfile.TemporaryDirectory() as directory:
            workspace = Path(directory)
            owner, _ = stage_bundle_configuration_fixture(workspace)
            environment = {**os.environ, "NUGET_PACKAGES": str(workspace / "packages")}
            responses = [subprocess.CompletedProcess([], 0, stdout="linux-x64\n"),
                         subprocess.CompletedProcess([], 1, stdout="error NU1100: SDK package missing\n", stderr="")]

            # Act / Assert
            with mock.patch.object(subprocess, "run", side_effect=responses) as execute:
                with self.assertRaisesRegex(AssertionError, "Native SDK publish failed:.*") as failure:
                    publish_configuration_fixture(owner, workspace / "published", environment=environment)

            self.assertIn("error NU1100: SDK package missing", str(failure.exception))
            self.assertEqual(2, execute.call_count)
            self.assertTrue(all(call.kwargs["env"] is environment for call in execute.call_args_list))

    def test_referenced_test_configuration_is_a_sidecar_not_a_bundle_primary(self) -> None:
        """A test executable retains its configuration without poisoning its referencing bundle."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary_root:
            workspace = Path(temporary_root)
            owner, host = stage_bundle_configuration_fixture(workspace)
            destination = workspace / "published"
            package_cache = workspace / "packages"
            package_cache.mkdir()
            environment = {**os.environ, "NUGET_PACKAGES": str(package_cache),
                           "NUGET_HTTP_CACHE_PATH": str(workspace / "http-cache")}

            # Act
            result = publish_configuration_fixture(host, destination, environment=environment)
            owner_assembly = next(owner.parent.glob("bin/Release/**/Owner.dll"))
            standalone = subprocess.run(
                ["dotnet", str(owner_assembly)],
                env=environment, capture_output=True, text=True, check=False,
            )
            executable = destination / ("Host.exe" if os.name == "nt" else "Host")
            bundled = subprocess.run([str(executable)], env=environment, capture_output=True, text=True, check=False)
            exported = subprocess.run(
                ["dotnet", "msbuild", str(owner), "-t:GetCopyToPublishDirectoryItems",
                 "-getItem:AllPublishItemsFullPathWithTargetPath", "-p:Configuration=Release"],
                cwd=workspace, env=environment, capture_output=True, text=True, check=False,
            )

            # Assert
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertNotIn("NETSDK1236", result.stdout + result.stderr)
            self.assertTrue((destination / "Owner.runtimeconfig.json").is_file())
            self.assertFalse((destination / "Host.runtimeconfig.json").exists())
            self.assertFalse((destination / "Host.deps.json").exists())
            self.assertFalse((destination / "Owner.dll").exists())
            self.assertEqual(0, standalone.returncode, standalone.stderr)
            self.assertEqual("bundle-owner-ok", standalone.stdout.strip())
            self.assertEqual(0, bundled.returncode, bundled.stderr)
            self.assertEqual("bundle-owner-ok", bundled.stdout.strip())
            self.assertEqual(0, exported.returncode, exported.stderr)
            configurations = [
                item for item in json.loads(exported.stdout)["Items"]["AllPublishItemsFullPathWithTargetPath"]
                if item["TargetPath"] in ("Owner.deps.json", "Owner.runtimeconfig.json")
            ]
            self.assertEqual({"Owner.deps.json", "Owner.runtimeconfig.json"},
                             {item["TargetPath"] for item in configurations})
            self.assertTrue(all(item["ExcludeFromSingleFile"] == "true" for item in configurations))
            self.assertTrue(all(item["CopyToPublishDirectory"] == "PreserveNewest" for item in configurations))

    def test_test_executable_own_configuration_remains_embedded(self) -> None:
        """The same producer still embeds its primary runtime configuration when published directly."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary_root:
            workspace = Path(temporary_root)
            owner, _ = stage_bundle_configuration_fixture(workspace)
            destination = workspace / "published"

            # Act
            result = publish_configuration_fixture(owner, destination)
            executable = destination / ("Owner.exe" if os.name == "nt" else "Owner")
            bundled = subprocess.run([str(executable)], capture_output=True, text=True, check=False)

            # Assert
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertNotIn("NETSDK1236", result.stdout + result.stderr)
            self.assertFalse((destination / "Owner.runtimeconfig.json").exists())
            self.assertFalse((destination / "Owner.deps.json").exists())
            self.assertEqual(0, bundled.returncode, bundled.stderr)
            self.assertEqual("bundle-owner-ok", bundled.stdout.strip())

    def test_ordinary_executable_configuration_exclusions_are_not_silenced(self) -> None:
        """The fix must not blanket-hide unrelated SDK bundle exclusions or modify normal producers."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary_root:
            workspace = Path(temporary_root)
            _, host = stage_bundle_configuration_fixture(workspace, test_owner=False)
            destination = workspace / "published"

            # Act
            result = publish_configuration_fixture(host, destination)

            # Assert
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertIn("NETSDK1236", result.stdout + result.stderr)
            self.assertIn("Owner.runtimeconfig.json", result.stdout + result.stderr)
            self.assertFalse((destination / "Host.runtimeconfig.json").exists())

    def test_generated_benchmark_driver_does_not_inherit_authored_workload_items(self) -> None:
        """A source-local native host must compile only its own driver, not shared workload sources."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary_root:
            workspace = Path(temporary_root)
            for relative in ("Directory.Build.props", "global.json", "benchmarks/Directory.Build.props",
                             "benchmarks/Shared/BenchmarkProject.props", "benchmarks/Shared/BenchmarkProgram.cs"):
                source = REPOSITORY_ROOT / relative
                if source.is_file():
                    target = workspace / relative
                    target.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(source, target)

            driver = workspace / "benchmarks/Owned/.bdn/invocation/native-driver.csproj"
            driver.parent.mkdir(parents=True)
            driver.write_text('<Project Sdk="Microsoft.NET.Sdk" />', encoding="ascii")
            (driver.parent / "gen.cs").write_text("internal sealed class NativeDriver { }", encoding="ascii")

            # Act
            result = subprocess.run(
                ["dotnet", "msbuild", str(driver),
                 "-getItem:Compile,PackageReference,AssemblyMetadata,EmbeddedResource"],
                cwd=workspace, capture_output=True, text=True, check=False,
            )

            # Assert
            self.assertEqual(0, result.returncode, result.stderr)
            items = json.loads(result.stdout)["Items"]
            self.assertEqual(["gen.cs"], [item["Identity"] for item in items["Compile"]])
            self.assertNotIn("BenchmarkDotNet", {item["Identity"] for item in items["PackageReference"]})
            self.assertNotIn("BenchmarkProjectPath", {item["Identity"] for item in items["AssemblyMetadata"]})
            self.assertEqual([], items["EmbeddedResource"])

    def test_authored_benchmark_projects_keep_explicit_workload_ownership(self) -> None:
        """All five authored executables retain shared workloads, native assets and exact source stamping."""
        for suffix in ("", ".MySql", ".PostgreSql", ".Sqlite", ".SqlServer"):
            with self.subTest(provider=suffix):
                # Arrange
                name = f"Doka.EntityFrameworkCore.SafeMigrations{suffix}.Benchmarks"
                project = REPOSITORY_ROOT / "benchmarks" / name / f"{name}.csproj"

                # Act
                result = subprocess.run(
                    ["dotnet", "msbuild", str(project),
                     "-getItem:Compile,PackageReference,AssemblyMetadata,EmbeddedResource"],
                    cwd=REPOSITORY_ROOT, capture_output=True, text=True, check=False,
                )

                # Assert
                self.assertEqual(0, result.returncode, result.stderr)
                items = json.loads(result.stdout)["Items"]
                self.assertIn(str(REPOSITORY_ROOT / "benchmarks/Shared/BenchmarkProgram.cs"),
                              {str(Path(item["FullPath"])) for item in items["Compile"]})
                self.assertIn("BenchmarkDotNet", {item["Identity"] for item in items["PackageReference"]})
                self.assertEqual([str(project)], [item["Value"] for item in items["AssemblyMetadata"]
                                                 if item["Identity"] == "BenchmarkProjectPath"])
                self.assertIn("Doka.SafeMigrations.DatabaseImages",
                              {item["LogicalName"] for item in items["EmbeddedResource"]})

    def test_tools_profiles_reference_design_without_changing_no_tooling_controls(self) -> None:
        """EF11 tooling requires Design explicitly; the unsupported None profile must stay negative."""
        for provider in PROVIDERS:
            for mode in ("Source", "Package"):
                for profile in ("Tools", "None"):
                    with self.subTest(provider=provider, mode=mode, profile=profile):
                        # Arrange
                        project = (CONSUMER_ROOT / provider
                                   / f"Doka.EntityFrameworkCore.SafeMigrations.{provider}.PackageConsumer.csproj")
                        arguments = ["dotnet", "msbuild", str(project), "-getItem:PackageReference",
                                     f"-p:SafeMigrationsPackageConsumerMode={mode}",
                                     f"-p:SafeMigrationsEfToolingReference={profile}",
                                     "-p:SafeMigrationsPackageVersion=11.0.0-dev"]

                        # Act
                        result = subprocess.run(arguments, cwd=REPOSITORY_ROOT, capture_output=True,
                                                text=True, check=False)
                        references = ({item["Identity"] for item in json.loads(result.stdout)
                                       ["Items"]["PackageReference"]} if result.returncode == 0 else set())

                        # Assert
                        self.assertEqual(0, result.returncode, result.stderr)
                        self.assertEqual(profile == "Tools", "Microsoft.EntityFrameworkCore.Design" in references)
                        self.assertEqual(profile == "Tools", "Microsoft.EntityFrameworkCore.Tools" in references)

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
                         "nested/.bdn-lookalike/packages.lock.json",
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
                             "nested/BenchmarkDotNet.Artifacts/packages.lock.json",
                             "nested/.bdn/generated/packages.lock.json"):
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
        generated_directories = {"artifacts", "bin", "obj", "BenchmarkDotNet.Artifacts", ".bdn"}
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
                             "nested/BenchmarkDotNet.Artifacts/Generated.cs", "nested/.bdn/generated/Generated.cs"):
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
