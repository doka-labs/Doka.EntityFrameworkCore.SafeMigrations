"""NuGet publication preflight and recovery state-machine tests."""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import zipfile


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
        with PublicationEnvironment("missing") as state:
            result = state.run_publish("preflight")

            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(state.publication_required(), "publication_required=true")
            self.assertEqual(state.dotnet_calls(), [])

    def test_publish_requires_a_key_only_when_content_is_missing(self) -> None:
        with PublicationEnvironment("missing") as state:
            result = state.run_publish("publish")

            self.assertEqual(result.returncode, 2)
            self.assertIn("required only while missing packages are published", result.stderr)

    def test_publish_uses_duplicate_tolerant_pushes_for_all_missing_payloads(self) -> None:
        with PublicationEnvironment("missing") as state:
            state.environment["NUGET_API_KEY"] = "temporary-key"
            result = state.run_publish("publish")

            self.assertEqual(result.returncode, 0, result.stderr)
            pushes = state.pushes()
            self.assertEqual(len(pushes), 6)
            self.assertTrue(all("--skip-duplicate" in push["args"] for push in pushes))
            self.assertIn("final readback remains authoritative", result.stdout)
            self.assertNotIn("temporary-key", result.stdout + result.stderr)

    def test_partial_primary_or_symbol_recovery_pushes_only_missing_payloads(self) -> None:
        for kind, suffix in (("primary", ".nupkg"), ("symbol", ".snupkg")):
            with self.subTest(kind=kind), PublicationEnvironment() as state:
                for package_id in PACKAGE_IDS:
                    state.respond(package_id, kind, "404")
                state.environment["NUGET_API_KEY"] = "temporary-key"
                result = state.run_publish("publish")

                self.assertEqual(result.returncode, 0, result.stderr)
                pushes = state.pushes()
                self.assertEqual(len(pushes), 3)
                self.assertTrue(all(push["args"][2].endswith(suffix) for push in pushes))

    def test_exact_existing_content_needs_neither_publication_nor_api_key(self) -> None:
        with PublicationEnvironment() as state:
            preflight = state.run_publish("preflight")
            publication = state.run_publish("publish")

            self.assertEqual(preflight.returncode, 0, preflight.stderr)
            self.assertEqual(publication.returncode, 0, publication.stderr)
            self.assertEqual(state.publication_required(), "publication_required=false")
            self.assertEqual(state.pushes(), [])

    def test_matching_pending_signatures_need_no_credentials_or_republication(self) -> None:
        with PublicationEnvironment() as state:
            for package_id in PACKAGE_IDS:
                state.respond(package_id, "primary", "pending")
            preflight = state.run_publish("preflight")
            publication = state.run_publish("publish")

            self.assertEqual(preflight.returncode, 0, preflight.stderr)
            self.assertEqual(publication.returncode, 0, publication.stderr)
            self.assertEqual(state.publication_required(), "publication_required=false")
            self.assertIn("matching-pending-signature", preflight.stdout)
            self.assertEqual(state.dotnet_calls(), [])

    def test_conflicting_existing_primary_package_fails_closed(self) -> None:
        with PublicationEnvironment() as state:
            state.respond(PACKAGE_IDS[0], "primary", "conflict")
            result = state.run_publish("preflight")

            self.assertEqual(result.returncode, 1)
            self.assertIn("differs from the qualified package", result.stderr)

    def test_conflicting_existing_symbols_fail_closed(self) -> None:
        with PublicationEnvironment() as state:
            state.respond(PACKAGE_IDS[0], "symbol", "conflict")
            result = state.run_publish("preflight")

            self.assertEqual(result.returncode, 1)
            self.assertIn("symbols differ", result.stderr)

    def test_checks_every_package_before_requesting_credentials_or_pushing(self) -> None:
        for kind, error in (("primary", "differs from the qualified package"), ("symbol", "symbols differ")):
            for credentials in (False, True):
                with self.subTest(kind=kind, credentials=credentials), PublicationEnvironment("missing") as state:
                    state.respond(PACKAGE_IDS[-1], kind, "conflict")
                    if credentials:
                        state.environment["NUGET_API_KEY"] = "temporary-key"
                    result = state.run_publish("publish")

                    self.assertEqual(result.returncode, 1, result.stderr)
                    self.assertIn(error, result.stderr)
                    self.assertEqual(len(state.http_calls()), 5 if kind == "primary" else 6)
                    self.assertEqual(state.pushes(), [])

    def test_symbol_tool_failures_cannot_pass_preflight_with_valid_stdout(self) -> None:
        for tool, error in (("shasum", "checksum calculation failed"), ("head", "header read failed")):
            with self.subTest(tool=tool), PublicationEnvironment() as state:
                state.fail_tool_after_output(tool)
                result = state.run_publish("publish")

                self.assertEqual(result.returncode, 1, result.stderr)
                self.assertIn(error, result.stderr)
                self.assertEqual(len(state.http_calls()), 2)
                self.assertEqual(state.pushes(), [])

    def test_invalid_present_signature_is_terminal(self) -> None:
        with PublicationEnvironment() as state:
            state.respond(PACKAGE_IDS[0], "primary", "invalid-signature", "exact")
            result = state.run_publish("preflight")

            self.assertEqual(result.returncode, 1)
            self.assertIn("signature verification failed", result.stderr)
            self.assertEqual(len(state.http_calls()), 1)

    def test_retries_transient_http_and_transport_errors_without_publication(self) -> None:
        for kind in ("primary", "symbol"):
            for status in ("408", "429", "500", "503", "transport"):
                with self.subTest(kind=kind, status=status), PublicationEnvironment() as state:
                    state.respond(PACKAGE_IDS[0], kind, status, "exact")
                    result = state.run_publish("preflight", polling=True)

                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertEqual(state.publication_required(), "publication_required=false")
                    self.assertEqual(state.pushes(), [])

    def test_transient_failures_timeout_without_requesting_credentials(self) -> None:
        with PublicationEnvironment() as state:
            state.respond(PACKAGE_IDS[0], "primary", "503")
            result = state.run_publish("publish", polling=True, timeout="1")

            self.assertEqual(result.returncode, 1)
            self.assertIn("timed out", result.stderr)
            self.assertIn("HTTP 503", result.stderr)
            self.assertEqual(state.pushes(), [])

    def test_local_io_url_and_tls_curl_errors_are_terminal(self) -> None:
        for kind in ("primary", "symbol"):
            for code in ("3", "23", "60", "77"):
                with self.subTest(kind=kind, code=code), PublicationEnvironment() as state:
                    state.respond(PACKAGE_IDS[0], kind, f"curl-{code}", "exact")
                    result = state.run_publish("publish", polling=True)

                    self.assertEqual(result.returncode, 1, result.stderr)
                    self.assertIn(f"terminal curl exit {code}", result.stderr)
                    self.assertEqual(len(state.http_calls(kind)), 1)
                    self.assertEqual(state.pushes(), [])

    def test_terminal_http_errors_fail_without_retrying(self) -> None:
        for kind in ("primary", "symbol"):
            for status in ("400", "401", "403", "409", "410"):
                with self.subTest(kind=kind, status=status), PublicationEnvironment() as state:
                    state.respond(PACKAGE_IDS[0], kind, status, "exact")
                    result = state.run_publish("preflight")

                    self.assertEqual(result.returncode, 1)
                    self.assertIn(f"returned HTTP {status}", result.stderr)
                    self.assertEqual(len(state.http_calls(kind)), 1)

    def test_partial_accepted_publication_retry_keeps_matching_subjects(self) -> None:
        with PublicationEnvironment("missing") as state:
            state.environment.update(
                {"NUGET_API_KEY": "temporary-key", "FAKE_FAIL_PUSH_NUMBER": "3"}
            )
            first = state.run_publish("publish")
            retry = state.run_publish("publish")
            state.environment.pop("NUGET_API_KEY")
            complete = state.run_publish("publish")

            self.assertEqual(first.returncode, 1)
            self.assertIn("accepted before connection failed", first.stderr)
            self.assertEqual(retry.returncode, 0, retry.stderr)
            self.assertEqual(complete.returncode, 0, complete.stderr)
            subjects = [Path(push["args"][2]).name for push in state.pushes()]
            self.assertEqual(len(subjects), 6)
            self.assertEqual(len(set(subjects)), 6)

    def test_missing_symbol_manifest_entry_fails_closed(self) -> None:
        with PublicationEnvironment() as state:
            state.omit_last_symbol()
            result = state.run_publish("preflight")

            self.assertEqual(result.returncode, 1)
            self.assertIn(f"omits {PACKAGE_IDS[-1]}", result.stderr)

    def test_duplicate_symbol_manifest_entries_fail_closed_before_push(self) -> None:
        with PublicationEnvironment("missing") as state:
            path = state.package_directory / "SYMBOLS.json"
            manifest = json.loads(path.read_text(encoding="utf-8"))
            manifest["symbols"].append(manifest["symbols"][0])
            path.write_text(json.dumps(manifest), encoding="utf-8")
            state.environment["NUGET_API_KEY"] = "temporary-key"
            result = state.run_publish("publish")

            self.assertEqual(result.returncode, 1, result.stderr)
            self.assertIn("must be unique", result.stderr)
            self.assertEqual(state.pushes(), [])

    def test_rejects_an_unknown_publication_mode(self) -> None:
        with PublicationEnvironment("missing") as state:
            result = state.run_publish("unknown")

            self.assertEqual(result.returncode, 2)
            self.assertIn("Usage:", result.stderr)

    def test_rejects_invalid_polling_controls_before_network_requests(self) -> None:
        for timeout in ("0", "-1", "1.5", "3601", "999999999999999999999"):
            with self.subTest(timeout=timeout), PublicationEnvironment() as state:
                result = state.run_publish("preflight", polling=True, timeout=timeout)

                self.assertEqual(result.returncode, 2)
                self.assertEqual(state.http_calls(), [])


class PublicationState:
    """Contains real packages and one isolated fake NuGet execution environment."""

    def __init__(self, root: Path, environment: dict[str, str]) -> None:
        self.root = root
        self.package_directory = root / "packages"
        self.output_directory = root / "readback"
        self.environment = environment

    def run_publish(
        self, mode: str, polling: bool = False, timeout: str = "15"
    ) -> subprocess.CompletedProcess[str]:
        arguments = ["--mode", mode]
        if polling:
            arguments += ["--timeout-seconds", timeout, "--poll-interval-seconds", "1"]
        return self.run_script(PUBLISH_SCRIPT, arguments)

    def run_script(self, script: Path, arguments: list[str]) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [
                "bash", str(script), "--package-dir", str(self.package_directory),
                "--version", VERSION, *arguments,
            ],
            cwd=REPOSITORY_ROOT,
            env=self.environment,
            check=False,
            capture_output=True,
            text=True,
            timeout=25,
        )

    def respond(self, package_id: str, kind: str, *responses: str) -> None:
        path = self.root / "http-config.json"
        config = json.loads(path.read_text(encoding="utf-8"))
        subject = (
            f"{package_id}.pdb" if kind == "symbol" else f"{package_id}.{VERSION}.nupkg".lower()
        )
        config["responses"][subject] = responses
        path.write_text(json.dumps(config), encoding="utf-8")

    def publication_required(self) -> str:
        return (self.root / "github-output").read_text(encoding="utf-8").strip()

    def dotnet_calls(self) -> list[dict]:
        return self.read_log("dotnet.log")

    def pushes(self) -> list[dict]:
        return [call for call in self.dotnet_calls() if call["args"][:2] == ["nuget", "push"]]

    def http_calls(self, kind: str | None = None) -> list[dict]:
        calls = self.read_log("http.log")
        if kind:
            return [call for call in calls if call["kind"] == kind]
        return calls

    def read_log(self, name: str) -> list[dict]:
        path = self.root / name
        return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]

    def omit_last_symbol(self) -> None:
        path = self.package_directory / "SYMBOLS.json"
        manifest = json.loads(path.read_text(encoding="utf-8"))
        manifest["symbols"] = manifest["symbols"][:-1]
        path.write_text(json.dumps(manifest), encoding="utf-8")

    def fail_tool_after_output(self, tool: str) -> None:
        real_tool = shutil.which(tool)
        assert real_tool is not None
        self.environment["FAKE_REAL_TOOL_PATH"] = real_tool
        path = self.root / "bin" / tool
        path.write_text(
            '#!/usr/bin/env bash\n'
            'set -euo pipefail\n'
            '"$FAKE_REAL_TOOL_PATH" "$@"\n'
            'exit 7\n',
            encoding="utf-8",
        )
        path.chmod(0o755)


class PublicationEnvironment:
    """Stubs only HTTP and dotnet; archive and checksum operations use real bytes."""

    def __init__(self, repository_state: str = "exact") -> None:
        self._repository_state = repository_state
        self._temporary_directory: tempfile.TemporaryDirectory[str] | None = None

    def __enter__(self) -> PublicationState:
        self._temporary_directory = tempfile.TemporaryDirectory()
        root = Path(self._temporary_directory.name)
        for directory in ("bin", "packages", "remote", "readback"):
            (root / directory).mkdir()

        symbols = []
        for package_id in PACKAGE_IDS:
            file_name = f"{package_id}.{VERSION}.nupkg"
            entries = {f"{package_id}.nuspec": b"<package />", "lib/net10.0/library.dll": b"qualified"}
            self.write_archive(root / "packages" / file_name, entries)
            self.write_archive(
                root / "packages" / f"{package_id}.{VERSION}.snupkg",
                {f"{package_id}.pdb": SYMBOL_BYTES},
            )
            for variant in (
                "exact", "pending", "conflict", "invalid-signature", "nested-signature",
                "extra-entry", "missing-entry", "duplicate-entry", "duplicate-signature",
            ):
                payload = dict(entries)
                if variant != "pending":
                    payload[".signature.p7s"] = (
                        b"invalid" if variant == "invalid-signature" else b"repository-signature"
                    )
                if variant == "conflict":
                    payload["lib/net10.0/library.dll"] = b"conflicting"
                if variant == "nested-signature":
                    payload["nested/.signature.p7s"] = payload.pop(".signature.p7s")
                if variant == "extra-entry":
                    payload["arbitrary.txt"] = b"not-qualified"
                if variant == "missing-entry":
                    del payload["lib/net10.0/library.dll"]
                archive_path = root / "remote" / f"{variant}-{file_name.lower()}"
                self.write_archive(archive_path, payload)
                if variant.startswith("duplicate-"):
                    duplicate = ".signature.p7s" if variant == "duplicate-signature" else f"{package_id}.nuspec"
                    with zipfile.ZipFile(archive_path, "a") as archive:
                        with unittest.TestCase().assertWarns(UserWarning):
                            archive.writestr(duplicate, payload[duplicate])

            symbols.append(
                {
                    "packageId": package_id,
                    "packageVersion": VERSION,
                    "pdbName": f"{package_id}.pdb",
                    "symbolKey": "0" * 32 + "FFFFFFFF",
                    "symbolUrl": f"https://symbols.nuget.org/{package_id}.pdb",
                    "checksumHeader": "SHA256:" + "0" * 64,
                    "sha256": hashlib.sha256(SYMBOL_BYTES).hexdigest(),
                }
            )
        (root / "packages" / "SYMBOLS.json").write_text(
            json.dumps({"schemaVersion": 1, "releaseVersion": VERSION, "symbols": symbols}), encoding="utf-8"
        )
        (root / "http-config.json").write_text(
            json.dumps({"default": self._repository_state, "responses": {}}), encoding="utf-8"
        )
        for name, value in (("http-counts.json", {}), ("accepted.json", [])):
            (root / name).write_text(json.dumps(value), encoding="utf-8")
        for name in ("dotnet.log", "http.log"):
            (root / name).write_text("", encoding="utf-8")
        for name, content in (("curl", self.curl_stub()), ("dotnet", self.dotnet_stub())):
            executable = root / "bin" / name
            executable.write_text(content, encoding="utf-8")
            executable.chmod(0o755)

        environment = os.environ.copy()
        environment.update(
            {
                "PATH": f"{root / 'bin'}{os.pathsep}{environment['PATH']}",
                "GITHUB_OUTPUT": str(root / "github-output"),
                "FAKE_NUGET_ROOT": str(root),
            }
        )
        environment.pop("NUGET_API_KEY", None)
        return PublicationState(root, environment)

    def __exit__(self, *args: object) -> None:
        assert self._temporary_directory is not None
        self._temporary_directory.cleanup()

    @staticmethod
    def write_archive(path: Path, entries: dict[str, bytes]) -> None:
        with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for name, content in entries.items():
                archive.writestr(name, content)

    @staticmethod
    def curl_stub() -> str:
        return r'''#!/usr/bin/env python3
import json
import os
from pathlib import Path
import sys

root = Path(os.environ["FAKE_NUGET_ROOT"])
arguments = sys.argv[1:]
output = Path(arguments[arguments.index("--output") + 1])
url = next(argument for argument in arguments if argument.startswith("https://"))
subject = url.rsplit("/", 1)[1]
kind = "symbol" if subject.endswith(".pdb") else "primary"
config = json.loads((root / "http-config.json").read_text())
counts = json.loads((root / "http-counts.json").read_text())
count = counts.get(subject, 0)
responses = config["responses"].get(subject, [config["default"]])
response = responses[min(count, len(responses) - 1)]
counts[subject] = count + 1
(root / "http-counts.json").write_text(json.dumps(counts))
accepted = json.loads((root / "accepted.json").read_text())
accepted_subject = subject if kind == "primary" else subject.removesuffix(".pdb").lower() + ".10.0.0-rc.1.snupkg"
if response == "missing":
    response = "exact" if accepted_subject in accepted else "404"
with (root / "http.log").open("a") as log:
    log.write(json.dumps({"subject": subject, "kind": kind, "response": response, "args": arguments}) + "\n")
if response == "transport" or response.startswith("curl-"):
    output.write_bytes(b"partial-response")
    print("000", end="")
    sys.exit(28 if response == "transport" else int(response.removeprefix("curl-")))
if response.isdigit():
    output.write_bytes(b"http-error-body")
    print(response, end="")
    sys.exit(0)
if kind == "symbol":
    payload = b"BSJB-conflicting-symbol-payload" if response == "conflict" else b"BSJB-public-symbol-payload"
    if response == "invalid-pdb":
        payload = b"not-a-portable-pdb"
elif response == "corrupt-zip":
    payload = b"not-a-package"
else:
    payload = (root / "remote" / (response + "-" + subject)).read_bytes()
output.write_bytes(payload)
print("200", end="")
'''

    @staticmethod
    def dotnet_stub() -> str:
        return r'''#!/usr/bin/env python3
import hashlib
import json
import os
from pathlib import Path
import sys
import zipfile

root = Path(os.environ["FAKE_NUGET_ROOT"])
arguments = sys.argv[1:]
logged_arguments = list(arguments)
if "--api-key" in logged_arguments:
    logged_arguments[logged_arguments.index("--api-key") + 1] = "<redacted>"
package = Path(arguments[2])
with (root / "dotnet.log").open("a") as log:
    log.write(json.dumps({"args": logged_arguments, "sha256": hashlib.sha256(package.read_bytes()).hexdigest()}) + "\n")
if arguments[:2] == ["nuget", "verify"]:
    with zipfile.ZipFile(package) as archive:
        valid = archive.read(".signature.p7s") == b"repository-signature"
    if not valid:
        for index in range(int(os.environ.get("FAKE_VERIFY_ERROR_LINES", "0"))):
            print(f"signature diagnostic line {index}", file=sys.stderr)
        print("signature verification failed", file=sys.stderr)
        sys.exit(1)
elif arguments[:2] == ["nuget", "push"]:
    if "--skip-duplicate" not in arguments:
        print("duplicate push was not tolerated", file=sys.stderr)
        sys.exit(1)
    accepted_path = root / "accepted.json"
    accepted = json.loads(accepted_path.read_text())
    accepted.append(package.name.lower())
    accepted_path.write_text(json.dumps(accepted))
    if len(accepted) == int(os.environ.get("FAKE_FAIL_PUSH_NUMBER", "0")):
        print("accepted before connection failed", file=sys.stderr)
        sys.exit(1)
else:
    sys.exit("unexpected dotnet command")
'''


if __name__ == "__main__":
    unittest.main()
