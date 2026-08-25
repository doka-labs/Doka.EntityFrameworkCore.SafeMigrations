"""NuGet primary-package and public-symbol readback contract tests."""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
READBACK_SCRIPT = REPOSITORY_ROOT / "eng" / "readback-nuget.sh"
VERSION = "10.0.0-rc.1"
PACKAGE_IDS = (
    "Doka.EntityFrameworkCore.SafeMigrations",
    "Doka.EntityFrameworkCore.SafeMigrations.MySql",
    "Doka.EntityFrameworkCore.SafeMigrations.PostgreSql",
)
SYMBOL_BYTES = b"BSJB-public-symbol-payload"


class NuGetReadbackTests(unittest.TestCase):
    """Covers exact, conflicting, unsigned, and incomplete public readbacks."""

    def test_writes_verified_primary_symbol_and_checksum_evidence(self) -> None:
        with ReadbackEnvironment("exact") as state:
            result = state.run()

            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(
                sorted(path.name for path in state.output_directory.glob("*.nupkg")),
                sorted(f"{package_id}.{VERSION}.nupkg" for package_id in PACKAGE_IDS),
            )
            self.assertEqual(
                sorted(path.name for path in (state.output_directory / "symbols").glob("*.pdb")),
                sorted(f"{package_id}.pdb" for package_id in PACKAGE_IDS),
            )
            checksum = subprocess.run(
                ["shasum", "-a", "256", "-c", "SIGNED_SHA256SUMS"],
                cwd=state.output_directory,
                check=False,
                capture_output=True,
                text=True,
            )

            self.assertEqual(checksum.returncode, 0, checksum.stderr)

    def test_rejects_conflicting_or_unsigned_primary_packages(self) -> None:
        cases = (
            ("conflicting-primary", "differ"),
            ("unsigned-primary", "exactly one NuGet repository signature"),
            ("verify-failure", "signature verification failed"),
        )
        for repository_state, expected in cases:
            with self.subTest(repository_state=repository_state), ReadbackEnvironment(
                repository_state
            ) as state:
                result = state.run()

                self.assertEqual(result.returncode, 1)
                self.assertIn(expected, result.stderr)

    def test_rejects_conflicting_or_invalid_symbol_readback(self) -> None:
        cases = (
            ("conflicting-symbol", "conflicting symbols"),
            ("invalid-symbol-status", "returned HTTP 400"),
        )
        for repository_state, expected in cases:
            with self.subTest(repository_state=repository_state), ReadbackEnvironment(
                repository_state
            ) as state:
                result = state.run()

                self.assertEqual(result.returncode, 1)
                self.assertIn(expected, result.stderr)

    def test_rejects_an_incomplete_symbol_manifest(self) -> None:
        with ReadbackEnvironment("exact") as state:
            manifest_path = state.package_directory / "SYMBOLS.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["symbols"] = manifest["symbols"][:-1]
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

            result = state.run()

            self.assertEqual(result.returncode, 1)
            self.assertIn(f"omits {PACKAGE_IDS[-1]}", result.stderr)

    def test_rejects_a_nonempty_evidence_directory(self) -> None:
        with ReadbackEnvironment("exact") as state:
            (state.output_directory / "existing.txt").write_text("existing", encoding="utf-8")

            result = state.run()

            self.assertEqual(result.returncode, 1)
            self.assertIn("must be empty", result.stderr)


class ReadbackState:
    """Contains one isolated NuGet readback execution environment."""

    def __init__(
        self,
        package_directory: Path,
        output_directory: Path,
        environment: dict[str, str],
    ) -> None:
        self.package_directory = package_directory
        self.output_directory = output_directory
        self.environment = environment

    def run(self) -> subprocess.CompletedProcess[str]:
        """Run the production readback script against the fake public endpoints."""

        return subprocess.run(
            [
                "bash",
                str(READBACK_SCRIPT),
                "--package-dir",
                str(self.package_directory),
                "--output",
                str(self.output_directory),
                "--version",
                VERSION,
            ],
            cwd=REPOSITORY_ROOT,
            env=self.environment,
            check=False,
            capture_output=True,
            text=True,
        )


class ReadbackEnvironment:
    """Provides deterministic HTTP, NuGet verification, and archive behavior."""

    def __init__(self, repository_state: str) -> None:
        self._repository_state = repository_state
        self._temporary_directory: tempfile.TemporaryDirectory[str] | None = None

    def __enter__(self) -> ReadbackState:
        self._temporary_directory = tempfile.TemporaryDirectory()
        root = Path(self._temporary_directory.name)
        bin_directory = root / "bin"
        package_directory = root / "packages"
        output_directory = root / "readback"
        bin_directory.mkdir()
        package_directory.mkdir()
        output_directory.mkdir()

        symbol_sha256 = hashlib.sha256(SYMBOL_BYTES).hexdigest()
        symbols = []
        for package_id in PACKAGE_IDS:
            (package_directory / f"{package_id}.{VERSION}.nupkg").write_bytes(b"primary")
            symbols.append(
                {
                    "packageId": package_id,
                    "packageVersion": VERSION,
                    "pdbName": f"{package_id}.pdb",
                    "symbolKey": "0" * 32 + "FFFFFFFF",
                    "symbolUrl": f"https://symbols.nuget.org/{package_id}.pdb",
                    "checksumHeader": "SHA256:" + "0" * 64,
                    "sha256": symbol_sha256,
                }
            )

        (package_directory / "SYMBOLS.json").write_text(
            json.dumps(
                {
                    "schemaVersion": 1,
                    "releaseVersion": VERSION,
                    "symbols": symbols,
                }
            ),
            encoding="utf-8",
        )

        self.write_executable(bin_directory / "curl", self.curl_stub())
        self.write_executable(bin_directory / "dotnet", self.dotnet_stub())
        self.write_executable(bin_directory / "unzip", self.unzip_stub())

        environment = os.environ.copy()
        environment.update(
            {
                "PATH": f"{bin_directory}{os.pathsep}{environment['PATH']}",
                "FAKE_NUGET_STATE": self._repository_state,
                "FAKE_PACKAGE_DIR": str(package_directory.resolve()),
            }
        )

        return ReadbackState(package_directory, output_directory, environment)

    def __exit__(self, *args: object) -> None:
        assert self._temporary_directory is not None
        self._temporary_directory.cleanup()

    @staticmethod
    def write_executable(path: Path, content: str) -> None:
        path.write_text(content, encoding="utf-8")
        path.chmod(0o755)

    @staticmethod
    def curl_stub() -> str:
        return """#!/usr/bin/env bash
set -euo pipefail

output=""
url=""
while (($# > 0)); do
    case "$1" in
        --output)
            output="$2"
            shift 2
            ;;
        --write-out|--connect-timeout|--max-time|--header)
            shift 2
            ;;
        --fail|--silent|--show-error|--location)
            shift
            ;;
        *)
            url="$1"
            shift
            ;;
    esac
done

if [[ "$url" == https://symbols.nuget.org/* ]]; then
    if [[ "$FAKE_NUGET_STATE" == "invalid-symbol-status" ]]; then
        printf '400'
        exit 0
    fi

    if [[ "$FAKE_NUGET_STATE" == "conflicting-symbol" ]]; then
        printf 'BSJB-conflicting-symbol-payload' > "$output"
    else
        printf 'BSJB-public-symbol-payload' > "$output"
    fi
    printf '200'
    exit 0
fi

printf 'published-primary' > "$output"
"""

    @staticmethod
    def dotnet_stub() -> str:
        return """#!/usr/bin/env bash
set -euo pipefail

if [[ "$FAKE_NUGET_STATE" == "verify-failure" ]]; then
    echo "signature verification failed" >&2
    exit 1
fi
"""

    @staticmethod
    def unzip_stub() -> str:
        return """#!/usr/bin/env bash
set -euo pipefail

archive=""
destination=""
while (($# > 0)); do
    case "$1" in
        -q)
            shift
            ;;
        -d)
            destination="$2"
            shift 2
            ;;
        *)
            archive="$1"
            shift
            ;;
    esac
done

mkdir -p "$destination"
if [[ "$archive" == "$FAKE_PACKAGE_DIR/"* ]]; then
    printf 'same' > "$destination/content"
    exit 0
fi

if [[ "$FAKE_NUGET_STATE" == "conflicting-primary" ]]; then
    printf 'different' > "$destination/content"
else
    printf 'same' > "$destination/content"
fi

if [[ "$FAKE_NUGET_STATE" != "unsigned-primary" ]]; then
    printf 'signature' > "$destination/.signature.p7s"
fi
"""


if __name__ == "__main__":
    unittest.main()
