"""Regression contracts for exact dependency-license exceptions and CI wiring."""

import copy
import json
import os
import re
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
VERIFIER = REPOSITORY_ROOT / "eng/verify-dependency-license-exceptions.py"
APPROVED_VERSIONS = {
    "Microsoft.Data.SqlClient.SNI.runtime": "6.0.2",
    "Microsoft.Identity.Client.NativeInterop": "0.20.6",
}


def dependency(name, version, change_type="added"):
    """Build a synthetic GitHub dependency comparison entry without secrets."""
    return {
        "change_type": change_type,
        "ecosystem": "nuget",
        "name": name,
        "version": version,
        "package_url": f"pkg:nuget/{name}@{version}",
    }


def verify(payload):
    """Run the production guard against an isolated response fixture."""
    with tempfile.TemporaryDirectory() as temporary_root:
        response_path = Path(temporary_root) / "comparison.json"
        response_path.write_text(json.dumps(payload), encoding="ascii")

        return subprocess.run(
            [sys.executable, str(VERIFIER), str(response_path)],
            capture_output=True,
            text=True,
            check=False,
        )


def run_workflow_guard(payload, snapshot_warning="", transport_error=False):
    """Execute the actual workflow step with an isolated, read-only API stub."""
    workflow = (REPOSITORY_ROOT / ".github/workflows/dependency-review.yml").read_text(encoding="ascii")
    step = workflow.split("- name: Require complete snapshots and exact license-exception versions", 1)[1]
    run_block = step.split("        run: |\n", 1)[1]
    command = "\n".join(line[10:] for line in run_block.splitlines())

    with tempfile.TemporaryDirectory() as temporary_root:
        fixture_root = Path(temporary_root)
        response_path = fixture_root / "comparison.json"
        response_path.write_text(json.dumps(payload), encoding="ascii")
        gh_path = fixture_root / "gh"
        gh_path.write_text(
            '#!/usr/bin/env bash\nset -euo pipefail\n'
            'if [[ "$*" == *"--paginate --slurp"* ]]; then\n'
            '    if [[ "$FIXTURE_TRANSPORT_ERROR" == "1" ]]; then exit 22; fi\n'
            '    cat "$FIXTURE_RESPONSE"\n'
            'else\n'
            '    printf "HTTP/2.0 200 OK\\nX-Github-Dependency-Graph-Snapshot-Warnings: %s\\n\\n" '
            '"$FIXTURE_SNAPSHOT_WARNING"\n'
            'fi\n',
            encoding="ascii",
        )
        gh_path.chmod(0o700)
        environment = {
            **os.environ,
            "PATH": f"{fixture_root}{os.pathsep}{os.environ['PATH']}",
            "GITHUB_REPOSITORY": "example/application",
            "BASE_SHA": "base",
            "HEAD_SHA": "head",
            "FIXTURE_RESPONSE": str(response_path),
            "FIXTURE_SNAPSHOT_WARNING": snapshot_warning,
            "FIXTURE_TRANSPORT_ERROR": "1" if transport_error else "0",
        }

        return subprocess.run(
            ["bash", "-eo", "pipefail", "-c", command],
            cwd=REPOSITORY_ROOT,
            env=environment,
            capture_output=True,
            text=True,
            check=False,
        )


class DependencyLicenseExceptionTests(unittest.TestCase):
    """Verify accepted versions, rejected metadata, pagination, and removals."""

    def test_accepts_exact_versions_with_duplicate_manifest_entries(self):
        entries = [dependency(name, version) for name, version in APPROVED_VERSIONS.items()]

        result = verify([entries, entries])

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("restricted to the approved versions", result.stdout)
        self.assertEqual("", result.stderr)

    def test_accepts_case_insensitive_nuget_identity(self):
        entries = [dependency(name.upper(), version) for name, version in APPROVED_VERSIONS.items()]
        for entry in entries:
            entry["ecosystem"] = "NuGet"

        result = verify([entries])

        self.assertEqual(0, result.returncode, result.stderr)

    def test_rejects_other_versions_on_a_later_page(self):
        for name in APPROVED_VERSIONS:
            for version in ("0.0.1", "99.0.0", APPROVED_VERSIONS[name] + "-preview.1"):
                with self.subTest(name=name, version=version):
                    pages = [[dependency("Unrelated.Package", "1.0.0")], [dependency(name, version)]]

                    result = verify(pages)

                    self.assertNotEqual(0, result.returncode)
                    self.assertIn(f"license exception requires NuGet {name.lower()}", result.stderr)

    def test_rejects_inconsistent_or_missing_excepted_metadata(self):
        mutations = (
            ("version", None),
            ("version", 6),
            ("package_url", None),
            ("package_url", "pkg:nuget/Other.Package@6.0.2"),
            ("package_url", "pkg:nuget/Microsoft.Data.SqlClient.SNI.runtime@99.0.0"),
            ("name", "Other.Package"),
            ("name", None),
            ("ecosystem", "npm"),
        )
        for name, version in APPROVED_VERSIONS.items():
            for field, value in mutations:
                with self.subTest(name=name, field=field, value=value):
                    entry = dependency(name, version)
                    entry[field] = value

                    result = verify([[entry]])

                    self.assertNotEqual(0, result.returncode)
                    self.assertIn("license exception requires NuGet", result.stderr)

    def test_removing_an_unapproved_version_does_not_require_an_exception(self):
        entries = [dependency(name, "99.0.0", "removed") for name in APPROVED_VERSIONS]

        result = verify([entries])

        self.assertEqual(0, result.returncode, result.stderr)

    def test_percent_encoded_identity_cannot_hide_an_unapproved_version(self):
        for name in APPROVED_VERSIONS:
            with self.subTest(name=name):
                entry = dependency(name, "99.0.0")
                entry["ecosystem"] = "npm"
                entry["package_url"] = entry["package_url"].replace("Microsoft", "%4dicrosoft")

                result = verify([[entry]])

                self.assertNotEqual(0, result.returncode)
                self.assertIn("license exception requires NuGet", result.stderr)

    def test_accepts_encoded_identity_with_consistent_approved_metadata(self):
        entries = [dependency(name, version) for name, version in APPROVED_VERSIONS.items()]
        for entry in entries:
            entry["package_url"] = entry["package_url"].replace("Microsoft", "%4dicrosoft")

        result = verify([entries])

        self.assertEqual(0, result.returncode, result.stderr)

    def test_malformed_purls_the_action_can_exempt_are_not_ignored(self):
        for name in APPROVED_VERSIONS:
            for suffix in (f"?{name}@99.0.0", f"#{name}@99.0.0", f"@{name}@99.0.0", f"{name}/@"):
                with self.subTest(name=name, suffix=suffix):
                    entry = dependency("Unrelated.Package", "99.0.0")
                    entry["ecosystem"] = "npm"
                    entry["package_url"] = f"pkg:nuget/{suffix}"

                    result = verify([[entry]])

                    self.assertNotEqual(0, result.returncode)
                    self.assertIn("license exception requires NuGet", result.stderr)

    def test_unrelated_packages_remain_the_review_actions_responsibility(self):
        pages = [[dependency("Other.Package", "99.0.0")]]

        result = verify(pages)

        self.assertEqual(0, result.returncode, result.stderr)

    def test_an_empty_complete_delta_is_valid(self):
        pages = [[]]

        result = verify(pages)

        self.assertEqual(0, result.returncode, result.stderr)

    def test_rejects_malformed_or_incomplete_page_shapes(self):
        invalid_responses = (
            [], {}, [None], [[None]], [[{}]], [[{"change_type": "changed"}]], [[{"change_type": "added"}]]
        )
        for response in invalid_responses:
            with self.subTest(response=response):
                payload = copy.deepcopy(response)

                result = verify(payload)

                self.assertNotEqual(0, result.returncode)
                self.assertIn("could not validate", result.stderr)

    def test_rejects_invalid_json(self):
        with tempfile.TemporaryDirectory() as temporary_root:
            response_path = Path(temporary_root) / "comparison.json"
            response_path.write_text("{", encoding="ascii")

            result = subprocess.run(
                [sys.executable, str(VERIFIER), str(response_path)], capture_output=True, text=True, check=False
            )

            self.assertNotEqual(0, result.returncode)
            self.assertIn("could not validate", result.stderr)

    def test_rejects_an_absent_response(self):
        with tempfile.TemporaryDirectory() as temporary_root:
            response_path = Path(temporary_root) / "absent.json"

            result = subprocess.run(
                [sys.executable, str(VERIFIER), str(response_path)], capture_output=True, text=True, check=False
            )

            self.assertNotEqual(0, result.returncode)
            self.assertIn("response does not exist", result.stderr)

    def test_all_product_lockfiles_obey_the_approved_version_bounds(self):
        entries = []
        for lockfile in (REPOSITORY_ROOT / "src").glob("*/packages.lock.json"):
            lock = json.loads(lockfile.read_text(encoding="ascii"))
            for dependencies in lock["dependencies"].values():
                for name, details in dependencies.items():
                    if name in APPROVED_VERSIONS:
                        entries.append(dependency(name, details["resolved"]))

        result = verify([entries])

        self.assertEqual(set(APPROVED_VERSIONS), {entry["name"] for entry in entries})
        self.assertEqual(0, result.returncode, result.stderr)

    def test_workflow_keeps_other_gates_and_requires_paginated_version_validation(self):
        workflow = (REPOSITORY_ROOT / ".github/workflows/dependency-review.yml").read_text(encoding="ascii")
        quality = (REPOSITORY_ROOT / ".github/workflows/quality-gates.yml").read_text(encoding="ascii")
        exception_block = re.search(r"allow-dependencies-licenses: >-\n((?:            .*\n)+)", workflow)
        license_block = re.search(r"allow-licenses: >-\n((?:            .*\n)+)", workflow)
        expected = {f"pkg:nuget/{name}@{version}" for name, version in APPROVED_VERSIONS.items()}

        actual = set(exception_block.group(1).replace(",", "").split()) if exception_block else set()
        licenses = set(license_block.group(1).replace(",", "").split()) if license_block else set()

        self.assertEqual(expected, actual)
        self.assertEqual(
            {"Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "CC0-1.0", "ISC", "MIT", "MIT-0", "Unlicense"},
            licenses,
        )
        self.assertIn("fail-on-severity: high", workflow)
        self.assertIn("show-openssf-scorecard: true", workflow)
        self.assertNotIn("continue-on-error", workflow)
        self.assertIn("bash eng/verify-dependency-snapshot-headers.sh", workflow)
        self.assertIn("gh api --paginate --slurp", workflow)
        self.assertIn('python3 eng/verify-dependency-license-exceptions.py "$response_body"', workflow)
        self.assertIn("python3 -m unittest eng/tests/test_dependency_license_exceptions.py -v", quality)


class DependencyLicenseWorkflowTests(unittest.TestCase):
    """Exercise the real shell step without hosted requests or credentials."""

    def test_complete_snapshots_and_approved_versions_pass(self):
        pages = [[dependency(name, version) for name, version in APPROVED_VERSIONS.items()]]

        result = run_workflow_guard(pages)

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("reports complete snapshots", result.stdout)
        self.assertIn("restricted to the approved versions", result.stdout)

    def test_unapproved_version_on_a_later_page_blocks_the_step(self):
        pages = [[], [dependency("Microsoft.Identity.Client.NativeInterop", "99.0.0")]]

        result = run_workflow_guard(pages)

        self.assertNotEqual(0, result.returncode)
        self.assertIn("license exception requires NuGet", result.stderr)

    def test_snapshot_warning_still_blocks_the_step(self):
        pages = [[]]

        result = run_workflow_guard(pages, snapshot_warning="c25hcHNob3QgbWlzc2luZw==")

        self.assertNotEqual(0, result.returncode)
        self.assertIn("still reports incomplete snapshots", result.stderr)
        self.assertNotIn("restricted to the approved versions", result.stdout)

    def test_dependency_response_transport_failure_cannot_pass(self):
        pages = [[]]

        result = run_workflow_guard(pages, transport_error=True)

        self.assertEqual(22, result.returncode)
        self.assertNotIn("restricted to the approved versions", result.stdout)


if __name__ == "__main__":
    unittest.main()
