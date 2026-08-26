"""NuGet primary-package and public-symbol readback contract tests."""

from __future__ import annotations

import hashlib
import json
import shutil
import subprocess
import unittest
import zipfile

try:
    from .test_nuget_publication import PACKAGE_IDS, REPOSITORY_ROOT, VERSION, PublicationEnvironment
except ImportError:
    from test_nuget_publication import PACKAGE_IDS, REPOSITORY_ROOT, VERSION, PublicationEnvironment


READBACK_SCRIPT = REPOSITORY_ROOT / "eng" / "readback-nuget.sh"


class NuGetReadbackTests(unittest.TestCase):
    """Covers exact, conflicting, pending, and incomplete public readbacks."""

    def test_writes_verified_primary_symbol_and_checksum_evidence(self) -> None:
        with PublicationEnvironment() as state:
            result = self.run_readback(state)

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
            checksum_lines = (state.output_directory / "SIGNED_SHA256SUMS").read_text().splitlines()
            self.assertEqual(len(checksum_lines), 6)
            verified = {call["sha256"] for call in state.dotnet_calls()}
            for path in state.output_directory.glob("*.nupkg"):
                self.assertIn(hashlib.sha256(path.read_bytes()).hexdigest(), verified)
            self.assertIn("exit_code=0", (state.output_directory / "result.txt").read_text())
            for call in state.http_calls("symbol"):
                self.assertIn("SymbolChecksumValidationSupported: 1", call["args"])
                self.assertIn("SymbolChecksum: SHA256:" + "0" * 64, call["args"])

    def test_missing_pending_signature_and_signed_states_converge(self) -> None:
        with PublicationEnvironment() as state:
            state.respond(PACKAGE_IDS[0], "primary", "404", "pending", "exact")
            result = self.run_readback(state)

            self.assertEqual(result.returncode, 0, result.stderr)
            observations = (state.output_directory / "observations.log").read_text()
            self.assertIn("absent", observations)
            self.assertIn("matching-pending-signature", observations)
            self.assertIn("matching-signed", observations)
            self.assertEqual(len(state.dotnet_calls()), 3)

    def test_preserves_the_original_readback_cli(self) -> None:
        with PublicationEnvironment() as state:
            result = state.run_script(READBACK_SCRIPT, ["--output", str(state.output_directory)])

            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertTrue((state.output_directory / "SIGNED_SHA256SUMS").exists())

    def test_compares_real_zip_payloads_with_bounded_reads(self) -> None:
        with PublicationEnvironment() as state:
            payload = {"large.bin": b"qualified" * 65536}
            file_name = f"{PACKAGE_IDS[0]}.{VERSION}.nupkg"
            PublicationEnvironment.write_archive(state.package_directory / file_name, payload)
            PublicationEnvironment.write_archive(
                state.root / "remote" / f"exact-{file_name.lower()}",
                {**payload, ".signature.p7s": b"repository-signature"},
            )
            probe_directory = state.root / "python-probe"
            probe_directory.mkdir()
            (probe_directory / "sitecustomize.py").write_text(
                "import sys\n"
                "import zipfile\n"
                "if sys.argv[0] == '-':\n"
                "    original_read = zipfile.ZipExtFile.read\n"
                "    def bounded_read(self, size=-1):\n"
                "        assert 0 < size <= 65536, 'unbounded ZIP entry read'\n"
                "        return original_read(self, size)\n"
                "    zipfile.ZipExtFile.read = bounded_read\n",
                encoding="utf-8",
            )
            state.environment["PYTHONPATH"] = str(probe_directory)
            result = self.run_readback(state)

            self.assertEqual(result.returncode, 0, result.stderr)

    def test_rejects_oversized_remote_entries_before_decompressing(self) -> None:
        with PublicationEnvironment() as state:
            file_name = f"{PACKAGE_IDS[0]}.{VERSION}.nupkg"
            with zipfile.ZipFile(state.package_directory / file_name) as archive:
                payload = {name: archive.read(name) for name in archive.namelist()}
            payload["lib/net10.0/library.dll"] = b"x" * (8 * 1024 * 1024)
            payload[".signature.p7s"] = b"repository-signature"
            PublicationEnvironment.write_archive(
                state.root / "remote" / f"exact-{file_name.lower()}", payload
            )
            result = self.run_readback(state)

            self.assertEqual(result.returncode, 1, result.stderr)
            self.assertIn("entry size differs", result.stderr)
            self.assertEqual(state.dotnet_calls(), [])
            self.assertEqual(len(state.http_calls()), 1)

    def test_rejects_same_size_payload_mismatch_after_the_first_zip_chunk(self) -> None:
        with PublicationEnvironment() as state:
            file_name = f"{PACKAGE_IDS[0]}.{VERSION}.nupkg"
            content = b"qualified" * 65536
            PublicationEnvironment.write_archive(state.package_directory / file_name, {"large.bin": content})
            PublicationEnvironment.write_archive(
                state.root / "remote" / f"exact-{file_name.lower()}",
                {"large.bin": content[:-1] + b"x", ".signature.p7s": b"repository-signature"},
            )
            result = self.run_readback(state)

            self.assertEqual(result.returncode, 1, result.stderr)
            self.assertIn("payload differs from the qualified package", result.stderr)
            self.assertEqual(state.dotnet_calls(), [])

    def test_rejects_bad_zip_entry_crc(self) -> None:
        for corrupt_entry in ("content", ".signature.p7s"):
            with self.subTest(entry=corrupt_entry), PublicationEnvironment() as state:
                file_name = f"{PACKAGE_IDS[0]}.{VERSION}.nupkg"
                remote = state.root / "remote" / f"exact-{file_name.lower()}"
                payload = {"content": b"qualified", ".signature.p7s": b"repository-signature"}
                PublicationEnvironment.write_archive(state.package_directory / file_name, {"content": b"qualified"})
                with zipfile.ZipFile(remote, "w", compression=zipfile.ZIP_STORED) as archive:
                    for name, value in payload.items():
                        archive.writestr(name, value)
                    offset = archive.getinfo(corrupt_entry).header_offset + 30 + len(corrupt_entry)
                data = bytearray(remote.read_bytes())
                data[offset] ^= 1
                remote.write_bytes(data)
                result = self.run_readback(state)

                self.assertEqual(result.returncode, 1, result.stderr)
                self.assertIn("Bad CRC-32", result.stderr)
                self.assertEqual(state.dotnet_calls(), [])

    def test_failure_diagnostics_are_fully_flushed_when_the_command_returns(self) -> None:
        with PublicationEnvironment() as state:
            state.respond(PACKAGE_IDS[0], "primary", "invalid-signature")
            state.environment["FAKE_VERIFY_ERROR_LINES"] = "1024"
            result = self.run_readback(state)

            self.assertEqual(result.returncode, 1, result.stderr)
            errors = (state.output_directory / "diagnostics" / "errors.log").read_text()
            self.assertEqual(errors, result.stderr)
            self.assertEqual(len(errors.splitlines()), 1025)
            self.assertEqual((state.output_directory / "result.txt").read_text(), "exit_code=1\n")

    def test_checksum_failure_preserves_payloads_without_a_success_manifest(self) -> None:
        with PublicationEnvironment() as state:
            real_shasum = shutil.which("shasum")
            assert real_shasum is not None
            state.environment["FAKE_REAL_SHASUM"] = real_shasum
            state.environment["FAKE_CHECKSUM_OUTPUT"] = str(state.output_directory.resolve())
            checksum_stub = state.root / "bin" / "shasum"
            checksum_stub.write_text(
                '#!/usr/bin/env bash\n'
                'set -euo pipefail\n'
                'if [[ "$PWD" == "$FAKE_CHECKSUM_OUTPUT" ]]; then\n'
                '    echo "injected checksum failure" >&2\n'
                '    exit 7\n'
                'fi\n'
                'exec "$FAKE_REAL_SHASUM" "$@"\n',
                encoding="utf-8",
            )
            checksum_stub.chmod(0o755)
            result = self.run_readback(state)

            self.assertEqual(result.returncode, 7, result.stderr)
            self.assertIn("injected checksum failure", result.stderr)
            self.assertFalse((state.output_directory / "SIGNED_SHA256SUMS").exists())
            self.assertEqual(len(list(state.output_directory.glob("*.nupkg"))), 3)
            self.assertEqual((state.output_directory / "result.txt").read_text(), "exit_code=7\n")

    def test_rejects_conflicting_or_invalid_primary_packages_without_retrying(self) -> None:
        cases = (
            ("conflict", "differs from the qualified package"),
            ("invalid-signature", "signature verification failed"),
            ("nested-signature", "differs from the qualified package"),
            ("extra-entry", "differs from the qualified package"),
            ("missing-entry", "differs from the qualified package"),
            ("duplicate-entry", "duplicate ZIP entries"),
            ("duplicate-signature", "duplicate ZIP entries"),
            ("corrupt-zip", "invalid package archive"),
        )
        for response, expected in cases:
            with self.subTest(response=response), PublicationEnvironment() as state:
                state.respond(PACKAGE_IDS[0], "primary", response, "exact")
                result = self.run_readback(state)

                self.assertEqual(result.returncode, 1, result.stderr)
                self.assertIn(expected, result.stderr)
                self.assertEqual(len(state.http_calls()), 1)
                self.assertFalse((state.output_directory / "SIGNED_SHA256SUMS").exists())
                self.assertIn(expected, (state.output_directory / "diagnostics" / "errors.log").read_text())
                self.assertTrue(list((state.output_directory / "diagnostics").glob("*.nupkg")))

    def test_pending_or_missing_publication_times_out_with_failure_evidence(self) -> None:
        for response in ("pending", "404", "503", "transport"):
            with self.subTest(response=response), PublicationEnvironment() as state:
                state.respond(PACKAGE_IDS[0], "primary", response)
                result = self.run_readback(state, timeout="1")

                self.assertEqual(result.returncode, 1, result.stderr)
                self.assertIn("timed out", result.stderr)
                self.assertFalse((state.output_directory / "SIGNED_SHA256SUMS").exists())
                self.assertIn("exit_code=1", (state.output_directory / "result.txt").read_text())
                self.assertIn("timed out", (state.output_directory / "diagnostics" / "errors.log").read_text())
                if response == "pending":
                    self.assertIn("matching-pending-signature", result.stdout)
                    self.assertEqual(state.dotnet_calls(), [])

    def test_transient_primary_and_symbol_failures_converge(self) -> None:
        for kind in ("primary", "symbol"):
            for response in ("408", "429", "500", "503", "transport"):
                with self.subTest(kind=kind, response=response), PublicationEnvironment() as state:
                    state.respond(PACKAGE_IDS[0], kind, response, "exact")
                    result = self.run_readback(state)

                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertTrue((state.output_directory / "SIGNED_SHA256SUMS").exists())

    def test_terminal_primary_and_symbol_http_errors_do_not_retry(self) -> None:
        for kind in ("primary", "symbol"):
            for status in ("400", "401", "403", "409", "410"):
                with self.subTest(kind=kind, status=status), PublicationEnvironment() as state:
                    state.respond(PACKAGE_IDS[0], kind, status, "exact")
                    result = self.run_readback(state)

                    self.assertEqual(result.returncode, 1, result.stderr)
                    self.assertIn(f"returned HTTP {status}", result.stderr)
                    self.assertEqual(len(state.http_calls(kind)), 1)

    def test_local_io_url_and_tls_curl_errors_do_not_retry(self) -> None:
        for kind in ("primary", "symbol"):
            for code in ("3", "23", "60", "77"):
                with self.subTest(kind=kind, code=code), PublicationEnvironment() as state:
                    state.respond(PACKAGE_IDS[0], kind, f"curl-{code}", "exact")
                    result = self.run_readback(state)

                    self.assertEqual(result.returncode, 1, result.stderr)
                    self.assertIn(f"terminal curl exit {code}", result.stderr)
                    self.assertEqual(len(state.http_calls(kind)), 1)

    def test_retryable_curl_transport_errors_converge(self) -> None:
        for code in ("5", "6", "7", "16", "18", "52", "55", "56", "92", "95"):
            with self.subTest(code=code), PublicationEnvironment() as state:
                state.respond(PACKAGE_IDS[0], "primary", f"curl-{code}", "exact")
                result = self.run_readback(state)

                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(len(state.http_calls("primary")), 4)

    def test_symbol_visibility_delay_keeps_verified_primary_bytes(self) -> None:
        with PublicationEnvironment() as state:
            state.respond(PACKAGE_IDS[0], "symbol", "404", "exact")
            result = self.run_readback(state)

            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(len(state.http_calls("primary")), 3)
            self.assertEqual(len(state.http_calls("symbol")), 4)
            self.assertEqual(len(state.dotnet_calls()), 3)

    def test_symbol_timeout_retains_verified_primary_but_no_success_manifest(self) -> None:
        with PublicationEnvironment() as state:
            state.respond(PACKAGE_IDS[0], "symbol", "404")
            result = self.run_readback(state, timeout="2")

            self.assertEqual(result.returncode, 1, result.stderr)
            self.assertIn("symbol readback timed out", result.stderr)
            self.assertTrue((state.output_directory / f"{PACKAGE_IDS[0]}.{VERSION}.nupkg").exists())
            self.assertFalse((state.output_directory / "SIGNED_SHA256SUMS").exists())
            self.assertEqual(len(state.http_calls("primary")), 1)

    def test_rejects_conflicting_or_nonportable_symbol_bytes(self) -> None:
        for response in ("conflict", "invalid-pdb"):
            with self.subTest(response=response), PublicationEnvironment() as state:
                state.respond(PACKAGE_IDS[0], "symbol", response, "exact")
                if response == "invalid-pdb":
                    path = state.package_directory / "SYMBOLS.json"
                    manifest = json.loads(path.read_text())
                    manifest["symbols"][0]["sha256"] = hashlib.sha256(b"not-a-portable-pdb").hexdigest()
                    path.write_text(json.dumps(manifest))
                result = self.run_readback(state)

                self.assertEqual(result.returncode, 1, result.stderr)
                self.assertIn("symbols differ", result.stderr)
                self.assertEqual(len(state.http_calls("symbol")), 1)
                self.assertTrue((state.output_directory / f"{PACKAGE_IDS[0]}.{VERSION}.nupkg").exists())
                self.assertFalse((state.output_directory / "SIGNED_SHA256SUMS").exists())

    def test_symbol_tool_failures_do_not_accept_valid_stdout_as_verification(self) -> None:
        for tool, error in (("shasum", "checksum calculation failed"), ("head", "header read failed")):
            with self.subTest(tool=tool), PublicationEnvironment() as state:
                state.fail_tool_after_output(tool)
                result = self.run_readback(state)

                self.assertEqual(result.returncode, 1, result.stderr)
                self.assertIn(error, result.stderr)
                self.assertEqual(len(state.http_calls()), 2)
                self.assertEqual(list((state.output_directory / "symbols").iterdir()), [])
                self.assertFalse((state.output_directory / "SIGNED_SHA256SUMS").exists())

    def test_rejects_an_incomplete_symbol_manifest(self) -> None:
        with PublicationEnvironment() as state:
            state.omit_last_symbol()
            result = self.run_readback(state)

            self.assertEqual(result.returncode, 1)
            self.assertIn(f"omits {PACKAGE_IDS[-1]}", result.stderr)

    def test_rejects_a_nonempty_evidence_directory(self) -> None:
        with PublicationEnvironment() as state:
            (state.output_directory / "existing.txt").write_text("existing", encoding="utf-8")
            result = self.run_readback(state)

            self.assertEqual(result.returncode, 1)
            self.assertIn("must be empty", result.stderr)
            self.assertEqual((state.output_directory / "existing.txt").read_text(), "existing")

    def test_output_enumeration_failure_preserves_existing_evidence_without_requests(self) -> None:
        with PublicationEnvironment() as state:
            sentinel = state.output_directory / "sentinel.bin"
            sentinel.write_bytes(b"preserve-existing-evidence")
            find_stub = state.root / "bin" / "find"
            find_stub.write_text(
                "#!/usr/bin/env bash\necho 'find: Permission denied' >&2\nexit 7\n", encoding="utf-8"
            )
            find_stub.chmod(0o755)
            result = self.run_readback(state)

            self.assertEqual(result.returncode, 1, result.stderr)
            self.assertIn("Cannot inspect readback output directory", result.stderr)
            self.assertEqual(state.http_calls(), [])
            self.assertEqual(list(state.output_directory.iterdir()), [sentinel])
            self.assertEqual(sentinel.read_bytes(), b"preserve-existing-evidence")

    def test_rejects_invalid_polling_controls_without_network_requests(self) -> None:
        for timeout, interval in (("0", "1"), ("3601", "1"), ("10", "0"), ("10", "1.5"), ("10", "61")):
            with self.subTest(timeout=timeout, interval=interval), PublicationEnvironment() as state:
                result = self.run_readback(state, timeout=timeout, interval=interval)

                self.assertEqual(result.returncode, 2)
                self.assertEqual(state.http_calls(), [])

    @staticmethod
    def run_readback(state, timeout: str = "15", interval: str = "1") -> subprocess.CompletedProcess[str]:
        return state.run_script(
            READBACK_SCRIPT,
            [
                "--output", str(state.output_directory), "--timeout-seconds", timeout,
                "--poll-interval-seconds", interval,
            ],
        )


if __name__ == "__main__":
    unittest.main()
