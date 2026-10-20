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
PACKAGE_VERSION = "10.5.0"
PKCS_ID = "System.Security.Cryptography.Pkcs"
PROVIDER_DEPENDENCIES = {
    "": "Microsoft.EntityFrameworkCore.Relational",
    ".MySql": "Doka.EntityFrameworkCore.MySql",
    ".PostgreSql": "Npgsql.EntityFrameworkCore.PostgreSQL",
    ".Sqlite": "Microsoft.EntityFrameworkCore.Sqlite.Core",
    ".SqlServer": "Microsoft.EntityFrameworkCore.SqlServer",
}


def write_packages(
    directory: Path,
    pkcs_version: str | None,
    leak: str | None = None,
    omitted_schema: str | None = None,
) -> None:
    """Create the exact five-package fixture without restoring or loading code."""
    for suffix, provider in PROVIDER_DEPENDENCIES.items():
        package_id = CORE_ID + suffix
        dependencies = [(provider, "[10.0.13,10.1.0)")]
        if suffix:
            dependencies.append((CORE_ID, PACKAGE_VERSION))

        if (suffix == ".SqlServer" and pkcs_version is not None) or suffix == leak:
            dependencies.append((PKCS_ID, pkcs_version or "9.0.21"))

        dependency_xml = "".join(
            f'<dependency id="{name}" version="{version}" />' for name, version in dependencies
        )
        nuspec = (
            f'<package><metadata><id>{package_id}</id><version>{PACKAGE_VERSION}</version>'
            '<readme>README.md</readme><repository type="git" '
            'url="https://github.com/doka-labs/Doka.EntityFrameworkCore.SafeMigrations" />'
            f'<dependencies><group targetFramework="net10.0">{dependency_xml}'
            '</group></dependencies></metadata></package>'
        )

        with zipfile.ZipFile(directory / f"{package_id}.{PACKAGE_VERSION}.nupkg", "w") as package:
            package.writestr(f"{package_id}.nuspec", nuspec)
            package.writestr("README.md", (REPOSITORY_ROOT / "README.md").read_bytes())
            package.writestr("LICENSE", (REPOSITORY_ROOT / "LICENSE").read_bytes())
            package.writestr(f"lib/net10.0/{package_id}.dll", b"synthetic fixture, never loaded")
            package.writestr(f"lib/net10.0/{package_id}.xml", "<doc />")
            if suffix:
                package.writestr(f"buildTransitive/{package_id}.props", "<Project />")
            else:
                for schema in (REPOSITORY_ROOT / "schemas").glob("*.schema.json"):
                    if schema.name != omitted_schema:
                        package.writestr(f"schemas/{schema.name}", schema.read_bytes())

        with zipfile.ZipFile(directory / f"{package_id}.{PACKAGE_VERSION}.snupkg", "w") as symbols:
            symbols.writestr(f"lib/net10.0/{package_id}.pdb", b"synthetic fixture, never loaded")


def verify(
    pkcs_version: str | None,
    leak: str | None = None,
    omitted_schema: str | None = None,
) -> subprocess.CompletedProcess[str]:
    """Exercise the real package gate against isolated archives, not a duplicate validator."""
    with tempfile.TemporaryDirectory() as temporary_root:
        directory = Path(temporary_root)
        write_packages(directory, pkcs_version, leak, omitted_schema)

        return subprocess.run(
            ["bash", str(VERIFIER), "--package-dir", str(directory), "--version", PACKAGE_VERSION],
            capture_output=True,
            text=True,
            check=False,
        )


def verify_consumer_graph(libraries: dict[str, dict[str, str]]) -> subprocess.CompletedProcess[str]:
    """Execute the consumer's actual assets predicate without restoring packages."""
    source = (REPOSITORY_ROOT / "eng/verify-package-consumer.sh").read_text(encoding="ascii")
    predicate = re.search(
        r'jq -e \'\.libraries\["System\.Security\.Cryptography\.Pkcs/[^"\n]+"\]'
        r'\.type == "package"\' \\\n\s*"\$assets_file" >/dev/null',
        source,
    )

    if predicate is None:
        raise AssertionError("The isolated consumer must retain its PKCS assets guard.")

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

    def test_accepts_published_fixed_minimum(self) -> None:
        """Keep the patched minimum compatible with the full five-package contract."""
        result = verify("9.0.21")

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("package contents verified", result.stdout)

    def test_rejects_missing_published_minimum(self) -> None:
        """A patched repository lock alone must not satisfy the publication gate."""
        result = verify(None)

        self.assertNotEqual(0, result.returncode)
        self.assertIn("PKCS minimum", result.stderr)

    def test_rejects_vulnerable_or_exactly_locked_published_minimum(self) -> None:
        """Prevent vulnerable lower bounds and exact locks that reject future patches."""
        for version in ("9.0.11", "9.0.20", "[9.0.11,10.0.0)", "[9.0.21]"):
            with self.subTest(version=version):
                result = verify(version)

                self.assertNotEqual(0, result.returncode)
                self.assertIn("PKCS minimum", result.stderr)

    def test_rejects_pkcs_dependency_in_other_providers(self) -> None:
        """Central pinning must not introduce SQL Server assets into sibling packages."""
        for suffix in ("", ".MySql", ".PostgreSql", ".Sqlite"):
            with self.subTest(provider=CORE_ID + suffix):
                result = verify("9.0.21", leak=suffix)

                self.assertNotEqual(0, result.returncode)
                self.assertIn("SQL Server-only PKCS dependency", result.stderr)

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
                result = verify("9.0.21", omitted_schema=schema)

                self.assertNotEqual(0, result.returncode)

    def test_accepts_fixed_isolated_consumer_package(self) -> None:
        """The consumer guard must accept the patched package identity and kind."""
        libraries = {f"{PKCS_ID}/9.0.21": {"type": "package"}}

        result = verify_consumer_graph(libraries)

        self.assertEqual(0, result.returncode, result.stderr)

    def test_rejects_missing_vulnerable_or_non_package_consumer_assets(self) -> None:
        """A missing, outdated or synthetic project identity must fail the real guard."""
        variants = (
            {},
            {f"{PKCS_ID}/9.0.11": {"type": "package"}},
            {f"{PKCS_ID}/9.0.21": {"type": "project"}},
        )

        for libraries in variants:
            with self.subTest(libraries=libraries):
                result = verify_consumer_graph(libraries)

                self.assertNotEqual(0, result.returncode)
