"""Exercise the real SPDX producer/attestation verifier boundary offline."""

from __future__ import annotations

import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
VERIFIER = ROOT / "eng/release/verify-attestations.sh"
PRODUCER = ROOT / "eng/generate-sbom.sh"
COMMIT = "a" * 40
REPOSITORY = "doka-labs/Doka.EntityFrameworkCore.SafeMigrations"


class AttestationContractTests(unittest.TestCase):
    """Keep manifest-derived predicates and all attestation identity checks aligned."""

    def test_real_producer_format_is_accepted_for_every_attested_subject(self) -> None:
        formats = re.findall(r"-mi SPDX:([0-9.]+)", PRODUCER.read_text(encoding="utf-8"))

        self.assertEqual(len(formats), 2)
        self.assertEqual(len(set(formats)), 1)

        result, calls = self.run_verifier({"spdxVersion": f"SPDX-{formats[0]}"})

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(len(calls), 15)
        self.assertEqual(sum("--predicate-type" in call for call in calls), 6)

    def test_unsupported_missing_and_malformed_spdx_versions_fail_before_verification(self) -> None:
        for version in ("SPDX-2.3", "SPDX-3.0", "spdx-2.2", None, 2.2, "SPDX-2.2;false"):
            with self.subTest(version=version):
                result, calls = self.run_verifier({"spdxVersion": version})

                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(calls, [])

    def test_wrong_bundle_predicate_is_rejected_by_the_verification_boundary(self) -> None:
        result, calls = self.run_verifier(
            {"spdxVersion": "SPDX-2.2"}, {"FAKE_BUNDLE_PREDICATE": "https://spdx.dev/Document/v2.3"}
        )

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("predicate mismatch", result.stderr)
        self.assertEqual(len(calls), 10)

    def test_verification_failure_cannot_be_masked_by_later_successful_subjects(self) -> None:
        for failing_subject in (1, 9, 10, 15):
            with self.subTest(failing_subject=failing_subject):
                result, calls = self.run_verifier(
                    {"spdxVersion": "SPDX-2.2"}, {"FAKE_FAIL_CALL": str(failing_subject)}
                )

                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(len(calls), failing_subject)

    def test_missing_workflow_identity_fails_before_contacting_github(self) -> None:
        for variable in ("GITHUB_REPOSITORY", "GITHUB_SHA"):
            with self.subTest(variable=variable):
                result, calls = self.run_verifier({"spdxVersion": "SPDX-2.2"}, {variable: ""})

                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(calls, [])

    @staticmethod
    def run_verifier(
        manifest: dict[str, object], overrides: dict[str, str] | None = None
    ) -> tuple[subprocess.CompletedProcess[str], list[list[str]]]:
        with tempfile.TemporaryDirectory(prefix="safemigrations-attest-") as directory:
            root = Path(directory)
            package_root = root / "artifacts/packages"
            package_root.mkdir(parents=True)
            for name in ("core", "mysql", "postgresql"):
                for extension in ("nupkg", "snupkg"):
                    (package_root / f"{name}.{extension}").write_text("qualified bytes", encoding="ascii")
            for name in ("SHA256SUMS", "SYMBOLS.json"):
                (package_root / name).write_text("qualified evidence", encoding="ascii")

            manifest_path = root / "artifacts/sbom/_manifest/spdx_2.2/manifest.spdx.json"
            manifest_path.parent.mkdir(parents=True)
            manifest_path.write_text(json.dumps(manifest), encoding="ascii")
            bin_root = root / "bin"
            bin_root.mkdir()
            stub = bin_root / "gh"
            stub.write_text(GH_STUB, encoding="ascii")
            stub.chmod(0o755)
            call_log = root / "calls.jsonl"
            environment = os.environ.copy()
            environment.update({
                "PATH": f"{bin_root}{os.pathsep}{environment['PATH']}",
                "GITHUB_REPOSITORY": REPOSITORY,
                "GITHUB_SHA": COMMIT,
                "FAKE_CALL_LOG": str(call_log),
            })
            environment.update(overrides or {})
            result = subprocess.run(
                ["bash", str(VERIFIER)], cwd=root, env=environment,
                capture_output=True, text=True, check=False, timeout=30,
            )
            calls = [json.loads(line) for line in call_log.read_text().splitlines()] if call_log.exists() else []

            return result, calls


GH_STUB = r'''#!/usr/bin/env python3
import json
import os
from pathlib import Path
import sys

args = sys.argv[1:]
log = Path(os.environ["FAKE_CALL_LOG"])
with log.open("a", encoding="ascii") as stream:
    stream.write(json.dumps(args) + "\n")
assert args[:2] == ["attestation", "verify"]
assert Path(args[2]).is_file()
expected = {
    "--repo": os.environ["GITHUB_REPOSITORY"],
    "--signer-workflow": os.environ["GITHUB_REPOSITORY"] + "/.github/workflows/release-candidate.yml",
    "--signer-digest": os.environ["GITHUB_SHA"],
    "--source-ref": "refs/heads/main",
    "--source-digest": os.environ["GITHUB_SHA"],
}
for flag, value in expected.items():
    assert args[args.index(flag) + 1] == value
assert "--deny-self-hosted-runners" in args
bundle = args[args.index("--bundle") + 1]
if bundle.endswith("sbom-attestation.sigstore.json"):
    manifest = json.loads(Path("artifacts/sbom/_manifest/spdx_2.2/manifest.spdx.json").read_text())
    # actions/attest@1e69f48 src/sbom.ts derives the predicate from spdxVersion.
    expected_predicate = os.environ.get(
        "FAKE_BUNDLE_PREDICATE", "https://spdx.dev/Document/v" + manifest["spdxVersion"].split("-")[1]
    )
    if args[args.index("--predicate-type") + 1] != expected_predicate:
        sys.exit("predicate mismatch")
else:
    assert bundle.endswith("build-provenance.sigstore.json")
if len(log.read_text().splitlines()) == int(os.environ.get("FAKE_FAIL_CALL", "0")):
    sys.exit("attestation identity rejected")
'''


if __name__ == "__main__":
    unittest.main()
