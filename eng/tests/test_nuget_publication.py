"""NuGet publication preflight and recovery state-machine tests."""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
PUBLISH_SCRIPT = REPOSITORY_ROOT / "eng" / "publish-nuget.sh"
VERSION = "10.0.0-rc.1"
PACKAGE_IDS = (
    "Doka.EntityFrameworkCore.SafeMigrations",
    "Doka.EntityFrameworkCore.SafeMigrations.MySql",
    "Doka.EntityFrameworkCore.SafeMigrations.PostgreSql",
)
SYMBOL_BYTES = b"BSJB-public-symbol-payload"


class NuGetPublicationTests(unittest.TestCase):
    """Covers absent, existing, conflicting, and credential-free recovery paths."""

    def test_preflight_reports_missing_packages_without_requesting_credentials(self) -> None:
        with self.publication_environment("missing") as state:
            result = self.run_publish(state, "preflight")

            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(
                state.github_output.read_text(encoding="utf-8").strip(),
                "publication_required=true",
            )
            self.assertEqual(state.dotnet_log.read_text(encoding="utf-8"), "")

    def test_publish_requires_a_key_only_when_content_is_missing(self) -> None:
        with self.publication_environment("missing") as state:
            result = self.run_publish(state, "publish")

            self.assertEqual(result.returncode, 2)
            self.assertIn("required only while missing packages are published", result.stderr)

    def test_publish_uses_duplicate_tolerant_pushes_for_all_missing_payloads(self) -> None:
        with self.publication_environment("missing") as state:
            state.environment["NUGET_API_KEY"] = "temporary-key"
            state.environment["FAKE_REQUIRE_SKIP_DUPLICATE"] = "true"
            result = self.run_publish(state, "publish")

            self.assertEqual(result.returncode, 0, result.stderr)
            pushes = [
                line
                for line in state.dotnet_log.read_text(encoding="utf-8").splitlines()
                if line.startswith("nuget push ")
            ]
            self.assertEqual(len(pushes), 6)
            self.assertTrue(all("--skip-duplicate" in push for push in pushes))

    def test_partial_primary_or_symbol_recovery_pushes_only_missing_payloads(self) -> None:
        cases = (
            ("missing-symbols", ".snupkg", 3),
            ("missing-primary", ".nupkg", 3),
        )
        for repository_state, expected_suffix, expected_count in cases:
            with self.subTest(repository_state=repository_state), self.publication_environment(
                repository_state
            ) as state:
                state.environment["NUGET_API_KEY"] = "temporary-key"
                result = self.run_publish(state, "publish")

                self.assertEqual(result.returncode, 0, result.stderr)
                pushes = [
                    line
                    for line in state.dotnet_log.read_text(encoding="utf-8").splitlines()
                    if line.startswith("nuget push ")
                ]
                self.assertEqual(len(pushes), expected_count)
                self.assertTrue(
                    all(
                        line.split(" --api-key", maxsplit=1)[0].endswith(expected_suffix)
                        for line in pushes
                    )
                )

    def test_exact_existing_content_needs_neither_publication_nor_api_key(self) -> None:
        with self.publication_environment("existing") as state:
            preflight = self.run_publish(state, "preflight")
            publication = self.run_publish(state, "publish")

            self.assertEqual(preflight.returncode, 0, preflight.stderr)
            self.assertEqual(publication.returncode, 0, publication.stderr)
            self.assertEqual(
                state.github_output.read_text(encoding="utf-8").strip(),
                "publication_required=false",
            )
            self.assertNotIn(
                "nuget push ",
                state.dotnet_log.read_text(encoding="utf-8"),
            )

    def test_conflicting_existing_primary_package_fails_closed(self) -> None:
        with self.publication_environment("conflicting") as state:
            result = self.run_publish(state, "preflight")

            self.assertEqual(result.returncode, 1)
            self.assertIn("differs from the qualified package", result.stderr)

    def test_conflicting_existing_symbols_fail_closed(self) -> None:
        with self.publication_environment("conflicting-symbols") as state:
            result = self.run_publish(state, "preflight")

            self.assertEqual(result.returncode, 1)
            self.assertIn("symbols differ", result.stderr)

    def test_unsigned_or_unverifiable_existing_primary_package_fails_closed(self) -> None:
        cases = (
            ("unsigned", {}, "no unique NuGet repository signature"),
            ("existing", {"FAKE_VERIFY_FAIL": "true"}, "signature verification failed"),
            ("http-error", {}, "returned HTTP 503"),
        )
        for repository_state, overrides, expected in cases:
            with self.subTest(repository_state=repository_state), self.publication_environment(
                repository_state
            ) as state:
                state.environment.update(overrides)
                result = self.run_publish(state, "preflight")

                self.assertEqual(result.returncode, 1)
                self.assertIn(expected, result.stderr)

    def test_missing_symbol_manifest_entry_fails_closed(self) -> None:
        with self.publication_environment("existing") as state:
            manifest_path = state.package_directory / "SYMBOLS.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["symbols"] = manifest["symbols"][:-1]
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

            result = self.run_publish(state, "preflight")

            self.assertEqual(result.returncode, 1)
            self.assertIn(f"omits {PACKAGE_IDS[-1]}", result.stderr)

    def test_rejects_an_unknown_publication_mode(self) -> None:
        with self.publication_environment("missing") as state:
            result = self.run_publish(state, "unknown")

            self.assertEqual(result.returncode, 2)
            self.assertIn("Usage:", result.stderr)

    def publication_environment(self, repository_state: str):
        return PublicationEnvironment(repository_state)

    @staticmethod
    def run_publish(
        state: "PublicationState",
        mode: str,
    ) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [
                "bash",
                str(PUBLISH_SCRIPT),
                "--package-dir",
                str(state.package_directory),
                "--version",
                VERSION,
                "--mode",
                mode,
            ],
            cwd=REPOSITORY_ROOT,
            env=state.environment,
            check=False,
            capture_output=True,
            text=True,
        )


class PublicationState:
    """Contains one isolated fake NuGet execution environment."""

    def __init__(
        self,
        package_directory: Path,
        github_output: Path,
        dotnet_log: Path,
        environment: dict[str, str],
    ) -> None:
        self.package_directory = package_directory
        self.github_output = github_output
        self.dotnet_log = dotnet_log
        self.environment = environment


class PublicationEnvironment:
    """Provides deterministic HTTP, archive, and dotnet behavior."""

    def __init__(self, repository_state: str) -> None:
        self._repository_state = repository_state
        self._temporary_directory: tempfile.TemporaryDirectory[str] | None = None

    def __enter__(self) -> PublicationState:
        self._temporary_directory = tempfile.TemporaryDirectory()
        root = Path(self._temporary_directory.name)
        bin_directory = root / "bin"
        package_directory = root / "packages"
        bin_directory.mkdir()
        package_directory.mkdir()

        symbols = []
        symbol_sha256 = hashlib.sha256(SYMBOL_BYTES).hexdigest()
        for package_id in PACKAGE_IDS:
            (package_directory / f"{package_id}.{VERSION}.nupkg").write_bytes(b"primary")
            (package_directory / f"{package_id}.{VERSION}.snupkg").write_bytes(b"symbols")
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

        dotnet_log = root / "dotnet.log"
        dotnet_log.write_text("", encoding="utf-8")
        github_output = root / "github-output"
        self.write_executable(bin_directory / "curl", self.curl_stub())
        self.write_executable(bin_directory / "dotnet", self.dotnet_stub())
        self.write_executable(bin_directory / "unzip", self.unzip_stub())

        environment = os.environ.copy()
        environment.update(
            {
                "PATH": f"{bin_directory}{os.pathsep}{environment['PATH']}",
                "GITHUB_OUTPUT": str(github_output),
                "FAKE_DOTNET_LOG": str(dotnet_log),
                "FAKE_NUGET_STATE": self._repository_state,
                "FAKE_REQUIRE_SKIP_DUPLICATE": "false",
                "FAKE_VERIFY_FAIL": "false",
            }
        )
        environment.pop("NUGET_API_KEY", None)

        return PublicationState(
            package_directory,
            github_output,
            dotnet_log,
            environment,
        )

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
        --silent|--show-error|--location)
            shift
            ;;
        *)
            url="$1"
            shift
            ;;
    esac
done

if [[ "$FAKE_NUGET_STATE" == "http-error" ]]; then
    printf '503'
    exit 0
fi

if [[ "$FAKE_NUGET_STATE" == "missing" \
    || ("$FAKE_NUGET_STATE" == "missing-primary" \
        && "$url" != https://symbols.nuget.org/*) \
    || ("$FAKE_NUGET_STATE" == "missing-symbols" \
        && "$url" == https://symbols.nuget.org/*) ]]; then
    printf '404'
    exit 0
fi

if [[ "$url" == https://symbols.nuget.org/* ]]; then
    if [[ "$FAKE_NUGET_STATE" == "conflicting-symbols" ]]; then
        printf 'BSJB-conflicting-symbol-payload' > "$output"
    else
        printf 'BSJB-public-symbol-payload' > "$output"
    fi
else
    printf 'published-primary' > "$output"
fi
printf '200'
"""

    @staticmethod
    def dotnet_stub() -> str:
        return """#!/usr/bin/env bash
set -euo pipefail

printf '%s\n' "$*" >> "$FAKE_DOTNET_LOG"
if [[ "${1:-}" == "nuget" && "${2:-}" == "verify" \
    && "$FAKE_VERIFY_FAIL" == "true" ]]; then
    echo "signature verification failed" >&2
    exit 1
fi

if [[ "${1:-}" == "nuget" && "${2:-}" == "push" \
    && "$FAKE_REQUIRE_SKIP_DUPLICATE" == "true" \
    && " $* " != *" --skip-duplicate "* ]]; then
    echo "duplicate push was not tolerated" >&2
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
if [[ "$archive" == */published-*.nupkg ]]; then
    if [[ "$FAKE_NUGET_STATE" == "conflicting" ]]; then
        printf 'different' > "$destination/content"
    else
        printf 'same' > "$destination/content"
    fi
    if [[ "$FAKE_NUGET_STATE" != "unsigned" ]]; then
        printf 'signature' > "$destination/.signature.p7s"
    fi
else
    printf 'same' > "$destination/content"
fi
"""


if __name__ == "__main__":
    unittest.main()
