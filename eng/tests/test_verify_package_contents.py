"""Positive and negative contracts for package dependency floors and report schemas."""

import json
import os
import re
import subprocess
import tempfile
import unittest
import zipfile
from pathlib import Path


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
VERIFIER = REPOSITORY_ROOT / "eng/verify-package-contents.sh"
CORE_ID = "Doka.EntityFrameworkCore.SafeMigrations"
PACKAGE_VERSION = "10.6.0"
PKCS_ID = "System.Security.Cryptography.Pkcs"
PROVIDER_DEPENDENCIES = {
    "": "Microsoft.EntityFrameworkCore.Relational",
    ".MySql": "Doka.EntityFrameworkCore.MySql",
    ".PostgreSql": "Npgsql.EntityFrameworkCore.PostgreSQL",
    ".Sqlite": "Microsoft.EntityFrameworkCore.Sqlite.Core",
    ".SqlServer": "Microsoft.EntityFrameworkCore.SqlServer",
}
TRANSPORT_DEPENDENCIES = {
    ".MySql": ("MySqlConnector", "2.6.2"),
    ".SqlServer": ("Microsoft.Data.SqlClient", "7.1.2"),
}


def write_packages(
    directory: Path,
    pkcs_version: str | None,
    leak: str | None = None,
    omitted_schema: str | None = None,
    transport_versions: dict[str, str | None] | None = None,
    transport_leak: tuple[str, str] | None = None,
    benchmark_leak: tuple[str, str] | None = None,
    core_versions: dict[str, str | None] | None = None,
    package_version: str = PACKAGE_VERSION,
    annotation_leak: tuple[str, str] | None = None,
    target_framework: str = "net11.0",
) -> None:
    """Create the exact five-package fixture without restoring or loading code."""
    for suffix, provider in PROVIDER_DEPENDENCIES.items():
        package_id = CORE_ID + suffix
        dependencies = [(provider, "[11.0.0,11.1.0)")]
        if suffix:
            core_version = (core_versions or {}).get(suffix, f"[{package_version}]")
            if core_version is not None:
                dependencies.append((CORE_ID, core_version))

        if suffix in TRANSPORT_DEPENDENCIES:
            name, minimum = TRANSPORT_DEPENDENCIES[suffix]
            version = (transport_versions or {}).get(name, minimum)
            if version is not None:
                dependencies.append((name, version))

        if transport_leak is not None and suffix == transport_leak[0]:
            minimum = next(version for name, version in TRANSPORT_DEPENDENCIES.values()
                           if name == transport_leak[1])
            dependencies.append((transport_leak[1], minimum))

        if (suffix == ".SqlServer" and pkcs_version is not None) or suffix == leak:
            dependencies.append((PKCS_ID, pkcs_version or "11.0.0"))

        if benchmark_leak is not None and suffix == benchmark_leak[0]:
            dependencies.append((benchmark_leak[1], "0.15.8"))

        if annotation_leak is not None and suffix == annotation_leak[0] and annotation_leak[1] == "dependency":
            dependencies.append(("JetBrains.Annotations", "2026.2.0"))

        dependency_xml = "".join(
            f'<dependency id="{name}" version="{version}" />' for name, version in dependencies
        )
        nuspec = (
            f'<package><metadata><id>{package_id}</id><version>{package_version}</version>'
            '<readme>README.md</readme><repository type="git" '
            'url="https://github.com/doka-labs/Doka.EntityFrameworkCore.SafeMigrations" />'
            f'<dependencies><group targetFramework="{target_framework}">{dependency_xml}'
            '</group></dependencies></metadata></package>'
        )

        with zipfile.ZipFile(directory / f"{package_id}.{package_version}.nupkg", "w") as package:
            package.writestr(f"{package_id}.nuspec", nuspec)
            package.writestr("README.md", (REPOSITORY_ROOT / "README.md").read_bytes())
            package.writestr("LICENSE", (REPOSITORY_ROOT / "LICENSE").read_bytes())
            package.writestr(f"lib/{target_framework}/{package_id}.dll", b"synthetic fixture, never loaded")
            package.writestr(f"lib/{target_framework}/{package_id}.xml", "<doc />")
            if annotation_leak is not None and suffix == annotation_leak[0] and annotation_leak[1] == "runtime":
                package.writestr(f"lib/{target_framework}/JetBrains.Annotations.dll", b"synthetic runtime asset")

            if suffix:
                package.writestr(f"buildTransitive/{package_id}.props", "<Project />")
            else:
                for schema in (REPOSITORY_ROOT / "schemas").glob("*.schema.json"):
                    if schema.name != omitted_schema:
                        package.writestr(f"schemas/{schema.name}", schema.read_bytes())

        with zipfile.ZipFile(directory / f"{package_id}.{package_version}.snupkg", "w") as symbols:
            symbols.writestr(f"lib/{target_framework}/{package_id}.pdb", b"synthetic fixture, never loaded")


def verify(
    pkcs_version: str | None,
    leak: str | None = None,
    omitted_schema: str | None = None,
    transport_versions: dict[str, str | None] | None = None,
    transport_leak: tuple[str, str] | None = None,
    benchmark_leak: tuple[str, str] | None = None,
    core_versions: dict[str, str | None] | None = None,
    package_version: str = PACKAGE_VERSION,
    annotation_leak: tuple[str, str] | None = None,
    target_framework: str = "net11.0",
) -> subprocess.CompletedProcess[str]:
    """Exercise the real package gate against isolated archives, not a duplicate validator."""
    with tempfile.TemporaryDirectory() as temporary_root:
        directory = Path(temporary_root)
        write_packages(directory, pkcs_version, leak, omitted_schema, transport_versions,
                       transport_leak, benchmark_leak, core_versions, package_version, annotation_leak,
                       target_framework)

        return subprocess.run(
            ["bash", str(VERIFIER), "--package-dir", str(directory), "--version", package_version],
            capture_output=True,
            text=True,
            check=False,
        )


def verify_consumer_graph(
    libraries: dict[str, dict[str, str]],
    package_id: str = PKCS_ID,
) -> subprocess.CompletedProcess[str]:
    """Execute the selected dependency's actual consumer predicate without restoring packages."""
    source = (REPOSITORY_ROOT / "eng/verify-package-consumer.sh").read_text(encoding="ascii")
    predicate = re.search(
        r'jq -e \'\.libraries\["' + re.escape(package_id) + r'/[^"\n]+"\]'
        r'\.type == "package"\' \\\n\s*"\$assets_file" >/dev/null',
        source,
    )

    if predicate is None:
        raise AssertionError(f"The isolated consumer must retain its {package_id} assets guard.")

    with tempfile.TemporaryDirectory() as temporary_root:
        assets = Path(temporary_root) / "project.assets.json"
        assets.write_text(json.dumps({"libraries": libraries}), encoding="ascii")

        return subprocess.run(
            ["bash", "-eu", "-c", predicate.group()],
            env={**os.environ, "assets_file": str(assets)},
            capture_output=True,
            text=True,
            check=False,
        )


class VerifyPackageContentsTests(unittest.TestCase):
    """Prove the security floor reaches consumers without crossing provider boundaries."""

    def test_rejects_foreign_framework_assets_and_dependency_group(self) -> None:
        """An older, platform-specific or next-major archive cannot satisfy the NET11 contract."""
        for framework in ("net10.0", "net11.0-windows", "net12.0"):
            with self.subTest(framework=framework):
                # Arrange
                target_framework = framework

                # Act
                result = verify("11.0.0", target_framework=target_framework)

                # Assert
                self.assertNotEqual(0, result.returncode)

    def test_accepts_exact_matching_core_for_stable_and_prerelease_packages(self) -> None:
        """Own-package ABI identity follows the complete stable or prerelease version."""
        for version in ("10.6.0", "10.6.0-dev", "10.6.0-rc.2"):
            with self.subTest(version=version):
                # Arrange
                package_version = version

                # Act
                result = verify("11.0.0", package_version=package_version)

                # Assert
                self.assertEqual(0, result.returncode, result.stderr)

    def test_rejects_missing_minimum_broad_or_mismatched_core_dependencies(self) -> None:
        """A private ABI dependency must not permit another Core binary identity."""
        for suffix in tuple(PROVIDER_DEPENDENCIES)[1:]:
            for core_version in (None, "10.6.0", "[10.6.0,11.0.0)", "[10.6.1]", "[10.6.0-rc.2]"):
                with self.subTest(provider=suffix, core_version=core_version):
                    # Arrange
                    versions = {suffix: core_version}

                    # Act
                    result = verify("11.0.0", core_versions=versions)

                    # Assert
                    self.assertNotEqual(0, result.returncode)
                    self.assertIn("matching Core private ABI", result.stderr)

    def test_rejects_annotation_dependency_or_runtime_assets_in_all_product_packages(self) -> None:
        """Build-only Rider annotations must not become consumer dependencies or assets."""
        for suffix in PROVIDER_DEPENDENCIES:
            for kind in ("dependency", "runtime"):
                with self.subTest(provider=suffix, kind=kind):
                    # Arrange
                    leak = (suffix, kind)

                    # Act
                    result = verify("11.0.0", annotation_leak=leak)

                    # Assert
                    self.assertNotEqual(0, result.returncode)
                    self.assertIn("Build-only annotation", result.stderr)

    def test_rejects_benchmark_dependencies_in_all_product_packages(self) -> None:
        """Benchmark assets must never reach any published product dependency graph."""
        for name in ("BenchmarkDotNet", "BenchmarkDotNet.Annotations", "benchmarkdotnet"):
            for suffix in PROVIDER_DEPENDENCIES:
                with self.subTest(package=name, provider=CORE_ID + suffix):
                    # Arrange
                    leak = (suffix, name)

                    # Act
                    result = verify("11.0.0", benchmark_leak=leak)

                    # Assert
                    self.assertNotEqual(0, result.returncode)
                    self.assertIn("Benchmark-only dependency", result.stderr)

    def test_accepts_published_fixed_minimum(self) -> None:
        """Keep the patched minimum compatible with the full five-package contract."""
        result = verify("11.0.0")

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("package contents verified", result.stdout)

    def test_rejects_missing_published_minimum(self) -> None:
        """A patched repository lock alone must not satisfy the publication gate."""
        result = verify(None)

        self.assertNotEqual(0, result.returncode)
        self.assertIn("PKCS minimum", result.stderr)

    def test_rejects_outdated_or_exactly_locked_published_minimum(self) -> None:
        """Reject stale servicing floors and exact locks that reject future patches."""
        for version in ("9.0.11", "9.0.20", "9.0.21", "9.0.22", "9.0.23", "10.0.13", "10.0.14", "10.0.15",
                        "[9.0.11,10.0.0)", "[9.0.22]", "[9.0.23]", "[11.0.0]"):
            with self.subTest(version=version):
                result = verify(version)

                self.assertNotEqual(0, result.returncode)
                self.assertIn("PKCS minimum", result.stderr)

    def test_rejects_pkcs_dependency_in_other_providers(self) -> None:
        """Central pinning must not introduce SQL Server assets into sibling packages."""
        for suffix in ("", ".MySql", ".PostgreSql", ".Sqlite"):
            with self.subTest(provider=CORE_ID + suffix):
                result = verify("11.0.0", leak=suffix)

                self.assertNotEqual(0, result.returncode)
                self.assertIn("SQL Server-only PKCS dependency", result.stderr)

    def test_rejects_missing_stale_or_exactly_locked_transport_minimums(self) -> None:
        """Transport updates must reach the nuspec as minimums, not only repository locks."""
        for name, minimum, previous in (("MySqlConnector", "2.6.2", "2.6.1"),
                                        ("Microsoft.Data.SqlClient", "7.1.2", "7.1.1")):
            for version in (None, previous, f"[{minimum}]"):
                with self.subTest(package=name, version=version):
                    # Arrange
                    versions = {name: version}

                    # Act
                    result = verify("11.0.0", transport_versions=versions)

                    # Assert
                    self.assertNotEqual(0, result.returncode)
                    self.assertIn(f"{name.rsplit('.', 1)[-1]} minimum", result.stderr)

    def test_rejects_transport_dependencies_in_other_providers(self) -> None:
        """A central transport minimum must never grow another adapter's runtime graph."""
        for owner, (name, _) in TRANSPORT_DEPENDENCIES.items():
            for suffix in PROVIDER_DEPENDENCIES:
                if suffix == owner:
                    continue

                with self.subTest(package=name, provider=CORE_ID + suffix):
                    # Arrange
                    leak = (suffix, name)

                    # Act
                    result = verify("11.0.0", transport_leak=leak)

                    # Assert
                    self.assertNotEqual(0, result.returncode)
                    self.assertIn("provider-only transport dependency", result.stderr)

    def test_rejects_missing_current_or_historical_report_schema(self) -> None:
        """New diagnostics must ship their schemas without dropping saved-report contracts."""
        schemas = (
            "safe-migration-run-report-v1.schema.json",
            "safe-migration-run-report-v4.schema.json",
            "safe-migration-report-view-v1.schema.json",
            "safe-migration-report-view-v3.schema.json",
        )

        for schema in schemas:
            with self.subTest(schema=schema):
                result = verify("11.0.0", omitted_schema=schema)

                self.assertNotEqual(0, result.returncode)

    def test_accepts_fixed_isolated_consumer_package(self) -> None:
        """The consumer guard must accept the patched package identity and kind."""
        libraries = {f"{PKCS_ID}/11.0.0": {"type": "package"}}

        result = verify_consumer_graph(libraries)

        self.assertEqual(0, result.returncode, result.stderr)

    def test_rejects_missing_vulnerable_or_non_package_consumer_assets(self) -> None:
        """A missing, outdated or synthetic project identity must fail the real guard."""
        variants = (
            {},
            {f"{PKCS_ID}/9.0.11": {"type": "package"}},
            {f"{PKCS_ID}/9.0.21": {"type": "package"}},
            {f"{PKCS_ID}/9.0.22": {"type": "package"}},
            {f"{PKCS_ID}/9.0.23": {"type": "package"}},
            {f"{PKCS_ID}/10.0.13": {"type": "package"}},
            {f"{PKCS_ID}/10.0.14": {"type": "package"}},
            {f"{PKCS_ID}/10.0.15": {"type": "package"}},
            {f"{PKCS_ID}/9.0.22": {"type": "project"}},
            {f"{PKCS_ID}/9.0.23": {"type": "project"}},
            {f"{PKCS_ID}/11.0.0": {"type": "project"}},
        )

        for libraries in variants:
            with self.subTest(libraries=libraries):
                result = verify_consumer_graph(libraries)

                self.assertNotEqual(0, result.returncode)

    def test_accepts_transport_consumer_packages(self) -> None:
        """The real package-only consumer guards accept each requested patched transport."""
        for name, version in TRANSPORT_DEPENDENCIES.values():
            with self.subTest(package=name):
                # Arrange
                libraries = {f"{name}/{version}": {"type": "package"}}

                # Act
                result = verify_consumer_graph(libraries, name)

                # Assert
                self.assertEqual(0, result.returncode, result.stderr)

    def test_rejects_missing_stale_or_non_package_transport_consumer_assets(self) -> None:
        """Missing, previous-version or project substitutes cannot satisfy package qualification."""
        for name, minimum, previous in (("MySqlConnector", "2.6.2", "2.6.1"),
                                        ("Microsoft.Data.SqlClient", "7.1.2", "7.1.1")):
            variants = ({}, {f"{name}/{previous}": {"type": "package"}},
                        {f"{name}/{minimum}": {"type": "project"}})

            for libraries in variants:
                with self.subTest(package=name, libraries=libraries):
                    # Arrange
                    package_id = name

                    # Act
                    result = verify_consumer_graph(libraries, package_id)

                    # Assert
                    self.assertNotEqual(0, result.returncode)
