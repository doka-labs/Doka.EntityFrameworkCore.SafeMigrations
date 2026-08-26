"""Release-version contract tests for manually dispatched publication."""

from __future__ import annotations

import importlib.util
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import textwrap
import unittest
from unittest import mock

import test_release_tag_contract as tag_contract


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
VALIDATOR = REPOSITORY_ROOT / "eng" / "release" / "validate-version.sh"
QUALITY_WORKFLOW = REPOSITORY_ROOT / ".github" / "workflows" / "quality-gates.yml"
RELEASE_WORKFLOW = REPOSITORY_ROOT / ".github" / "workflows" / "release-candidate.yml"
PUBLISH_SCRIPT = REPOSITORY_ROOT / "eng" / "publish-nuget.sh"
ATTESTATION_SCRIPT = REPOSITORY_ROOT / "eng" / "release" / "verify-attestations.sh"
VERSION_CONTRACT_PATH = REPOSITORY_ROOT / "eng" / "release" / "version_contract.py"

VERSION_CONTRACT_SPEC = importlib.util.spec_from_file_location(
    "safe_migrations_version_contract",
    VERSION_CONTRACT_PATH,
)
assert VERSION_CONTRACT_SPEC is not None and VERSION_CONTRACT_SPEC.loader is not None
VERSION_CONTRACT = importlib.util.module_from_spec(VERSION_CONTRACT_SPEC)
sys.modules[VERSION_CONTRACT_SPEC.name] = VERSION_CONTRACT
VERSION_CONTRACT_SPEC.loader.exec_module(VERSION_CONTRACT)


class ReleaseVersionTests(unittest.TestCase):
    """Pins candidate identity and publication-boundary contracts."""

    def test_accepts_documented_release_candidate(self) -> None:
        result = self.run_validator("10.0.0-rc.1")

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("package-version=10.0.0-rc.1", result.stdout)
        self.assertIn("prerelease=true", result.stdout)
        self.assertIn("release-tag=v10.0.0-rc.1", result.stdout)

    def test_accepts_stable_and_prerelease_versions_when_source_and_changelog_match(self) -> None:
        cases = (
            ("10.0.0", "## [10.0.0] - 2026-08-25", False),
            ("10.0.0-preview.4", "## [10.0.0-preview.4] - 2026-08-25", True),
            ("10.0.0-alpha-1", "## [10.0.0-alpha-1] - 2026-08-25", True),
        )
        for version, changelog, prerelease in cases:
            with self.subTest(version=version):
                contract = VERSION_CONTRACT.validate_version(version, "10.0.0", changelog)

                self.assertEqual(contract.package_version, version)
                self.assertEqual(contract.prerelease, prerelease)
                self.assertEqual(contract.release_tag, f"v{version}")

    def test_rejects_source_line_and_changelog_contract_drift(self) -> None:
        cases = (
            ("10.0.0-rc.1", "11.0.0", "## [10.0.0-rc.1] - 2026-08-25", "source release line"),
            ("10.0.0-rc.1", "10.0.0", "", "exactly one dated"),
            (
                "10.0.0-rc.1",
                "10.0.0",
                "## [10.0.0-rc.1] - 2026-08-25\n## [10.0.0-rc.1] - 2026-08-25",
                "exactly one dated",
            ),
            ("10.0.0-rc.1", "10.0.0", "## [10.0.0-rc.1] - not-a-date", "invalid release date"),
        )
        for package_version, version_prefix, changelog, expected in cases:
            with self.subTest(expected=expected):
                with self.assertRaisesRegex(VERSION_CONTRACT.VersionContractError, expected):
                    VERSION_CONTRACT.validate_version(package_version, version_prefix, changelog)

    def test_requires_one_canonical_stable_source_version(self) -> None:
        cases = (
            "<Project />",
            "<Project><PropertyGroup><VersionPrefix>10.0.0-rc.1</VersionPrefix></PropertyGroup></Project>",
            "<Project><PropertyGroup><VersionPrefix>10.0.0</VersionPrefix><VersionPrefix>10.0.0</VersionPrefix></PropertyGroup></Project>",
        )
        for content in cases:
            with self.subTest(content=content), tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "Directory.Build.props"
                path.write_text(content, encoding="utf-8")

                with self.assertRaises(VERSION_CONTRACT.VersionContractError):
                    VERSION_CONTRACT.read_version_prefix(path)

    def test_rejects_noncanonical_or_out_of_contract_versions(self) -> None:
        invalid_versions = (
            "v10.0.0",
            "01.0.0",
            "10.01.0",
            "10.0.01",
            "10.0",
            "10.0.0-",
            "10.0.0-RC.1",
            "10.0.0-rc.01",
            "10.0.0-rc..1",
            "10.0.0+build",
            "10.0.0-rc.1+build",
            "10.0.0-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "11.0.0-rc.1",
            "10.0.0",
        )

        for version in invalid_versions:
            with self.subTest(version=version):
                result = self.run_validator(version)

                self.assertEqual(result.returncode, 1)
                self.assertEqual(result.stdout, "")

    def test_requires_exactly_one_version(self) -> None:
        missing = self.run_validator()
        additional = self.run_validator("10.0.0", "10.0.0-rc.1")

        self.assertEqual(missing.returncode, 2)
        self.assertIn("Usage:", missing.stderr)
        self.assertEqual(additional.returncode, 2)
        self.assertIn("Usage:", additional.stderr)

    def test_writes_only_machine_outputs_to_github_output(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "github-output"
            result = self.run_validator(
                "10.0.0-rc.1",
                environment={"GITHUB_OUTPUT": str(output)},
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(
                output.read_text(encoding="utf-8").splitlines(),
                [
                    "package-version=10.0.0-rc.1",
                    "prerelease=true",
                    "release-tag=v10.0.0-rc.1",
                ],
            )
            self.assertNotIn("package-version=", result.stdout)

    def test_validator_fixture_does_not_inherit_the_hosted_step_output(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "hosted-output"
            output.write_text("Existing runner state.\n", encoding="ascii")

            with mock.patch.dict(os.environ, {"GITHUB_OUTPUT": str(output)}):
                result = self.run_validator("10.0.0-rc.1")

            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(output.read_text(encoding="ascii"), "Existing runner state.\n")
            self.assertIn("package-version=10.0.0-rc.1", result.stdout)

    def test_candidate_workflow_qualifies_before_waiting_for_the_tag(self) -> None:
        validator = VALIDATOR.read_text(encoding="utf-8")
        release_workflow = RELEASE_WORKFLOW.read_text(encoding="utf-8")
        attestation_script = ATTESTATION_SCRIPT.read_text(encoding="utf-8")

        qualification = release_workflow.index("\n  quality-gates:")
        attestation = release_workflow.index("\n  attest:")
        publication = release_workflow.index("\n  publish:")
        environment = release_workflow.index("    environment:\n      name: nuget", publication)
        tag_binding = release_workflow.index(
            "Bind publication to the qualified commit and requested tag",
            publication,
        )
        tag_signature = release_workflow.index(
            "Verify authorized signed annotated release tag",
            publication,
        )
        attestation_verification = release_workflow.index(
            "Verify provenance and SBOM attestations",
            publication,
        )
        release_staging = release_workflow.index(
            "Stage and verify GitHub Release draft",
            publication,
        )
        nuget_preflight = release_workflow.index(
            "Check NuGet.org immediately before publication",
            publication,
        )
        nuget_login = release_workflow.index(
            "Exchange GitHub OIDC token for NuGet API key",
            publication,
        )
        nuget_publication = release_workflow.index(
            "Publish missing packages or verify exact existing bytes",
            publication,
        )
        nuget_readback = release_workflow.index(
            "Verify NuGet repository signatures and content readback",
            publication,
        )
        release_finalization = release_workflow.index(
            "Publish and read back immutable GitHub Release",
            publication,
        )

        self.assertIn("workflow_dispatch:", release_workflow)
        self.assertNotIn("\n  push:\n", release_workflow)
        self.assertNotIn("\n    tags:", release_workflow)
        self.assertNotIn("v10.0.0", release_workflow)
        self.assertNotIn("10.0.0", validator)
        self.assertLess(qualification, attestation)
        self.assertLess(attestation, publication)
        self.assertLess(publication, environment)
        self.assertLess(environment, tag_binding)
        self.assertLess(tag_binding, tag_signature)
        self.assertLess(tag_signature, attestation_verification)
        self.assertLess(attestation_verification, release_staging)
        self.assertLess(release_staging, nuget_preflight)
        self.assertLess(nuget_preflight, nuget_login)
        self.assertLess(nuget_login, nuget_publication)
        self.assertLess(nuget_publication, release_finalization)
        self.assertLess(release_finalization, nuget_readback)
        self.assertLess(tag_signature, nuget_login)
        self.assertNotIn("git tag -a", release_workflow)
        self.assertNotIn("git tag -s", release_workflow)
        self.assertNotIn("git tag --points-at", release_workflow)
        self.assertIn(
            'git show-ref --verify --quiet "refs/tags/$RELEASE_TAG"',
            release_workflow,
        )
        self.assertIn(
            "Candidate qualification must complete before its release tag exists.",
            release_workflow,
        )
        self.assertIn("verifySignedAnnotatedTag", release_workflow)
        self.assertIn("eng/release/verify-tag.sh", release_workflow)
        self.assertIn("bash eng/release/verify-attestations.sh", release_workflow)
        self.assertIn("gh attestation verify", attestation_script)
        self.assertIn('--bundle "$provenance_bundle"', attestation_script)
        self.assertIn('--bundle "$sbom_bundle"', attestation_script)
        self.assertIn('--predicate-type "$sbom_predicate"', attestation_script)
        self.assertIn('--source-digest "$GITHUB_SHA"', attestation_script)
        self.assertIn("--deny-self-hosted-runners", attestation_script)
        self.assertIn("const { candidateReleaseOptions, recordReleaseEvidence, stageRelease }", release_workflow)
        self.assertIn("const { candidateReleaseOptions, recordReleaseEvidence, reconcileRelease }", release_workflow)
        self.assertNotIn("SIGNED_SHA256SUMS", release_workflow)
        self.assertIn('bash eng/release/verify-main-source.sh "$GITHUB_SHA"', release_workflow)
        self.assertIn(
            'if [[ "$(git rev-parse refs/remotes/origin/main)" != "$GITHUB_SHA" ]]; then',
            release_workflow.split("\n  quality-gates:")[0],
        )

    def test_workflow_inputs_cross_shell_boundaries_only_through_environment_variables(self) -> None:
        quality_workflow = QUALITY_WORKFLOW.read_text(encoding="utf-8")
        release_workflow = RELEASE_WORKFLOW.read_text(encoding="utf-8")

        self.assertIn("REQUESTED_VERSION: ${{ inputs.version }}", release_workflow)
        self.assertNotIn(
            'run: eng/release/validate-version.sh "${{ inputs.version }}"',
            release_workflow,
        )
        self.assertNotIn('--version "${{ inputs.package-version }}"', quality_workflow)
        self.assertIn('eng/release/validate-version.sh "$REQUESTED_VERSION"', release_workflow)
        self.assertIn('--version "$PACKAGE_VERSION"', quality_workflow)
        self.assertNotIn('LogFileName=${{ matrix.', quality_workflow)
        self.assertNotIn('eng/verify-ef-tooling.sh "${{ matrix.', quality_workflow)
        self.assertNotIn('eng/verify-ef-tooling.sh postgres "${{ matrix.', quality_workflow)
        self.assertIn(
            'LogFileName=$SAFE_MIGRATIONS_MYSQL_ENGINE-$SAFE_MIGRATIONS_MYSQL_VERSION.trx',
            quality_workflow,
        )
        self.assertIn(
            'LogFileName=postgresql-$SAFE_MIGRATIONS_POSTGRES_VERSION.trx',
            quality_workflow,
        )

    def test_failed_job_rerun_uses_the_exported_qualification_artifact(self) -> None:
        quality_workflow = QUALITY_WORKFLOW.read_text(encoding="utf-8")
        release_workflow = RELEASE_WORKFLOW.read_text(encoding="utf-8")

        self.assertIn(
            "value: ${{ jobs.package-and-core.outputs.qualified-artifact-name }}",
            quality_workflow,
        )
        self.assertIn(
            "qualified-artifact-name: ${{ steps.artifact-identity.outputs.name }}",
            quality_workflow,
        )
        self.assertIn(
            "name: ${{ needs.quality-gates.outputs.qualified-artifact-name }}",
            release_workflow,
        )
        self.assertIn(
            "safe-migrations-publication-${{ needs.preflight.outputs.package-version }}-${{ github.run_attempt }}",
            release_workflow,
        )
        self.assertIn(
            "name: ${{ needs.attest.outputs.attestation-artifact-name }}",
            release_workflow,
        )
        self.assertIn(
            "if: steps.nuget-preflight.outputs.publication_required == 'true'",
            release_workflow,
        )
        self.assertEqual(
            release_workflow.count(
                "if: steps.nuget-preflight.outputs.publication_required == 'true'"
            ),
            2,
        )

    def test_failure_diagnostics_are_always_retained_separately_from_release_assets(self) -> None:
        workflow = RELEASE_WORKFLOW.read_text(encoding="utf-8")
        publication = workflow.split("\n  publish:\n")[1]

        for step in (
            "Initialize publication evidence",
            "Record publication attempt outcome",
            "Upload publication attempt evidence",
        ):
            body = publication.split(f"      - name: {step}\n")[1].split("\n      - name:")[0]

            self.assertIn("        if: always()", body)

        self.assertIn("path: artifacts/release-publication", publication)
        self.assertIn("--output artifacts/release-publication/nuget-readback", publication)
        self.assertNotIn("dotnet pack", publication)
        self.assertNotIn("dotnet build", publication)
        self.assertNotIn("clean: false", publication)
        self.assertIn('"checkout":"${{ steps.checkout.outcome }}"', publication)
        self.assertIn('"nuget-login":"${{ steps.nuget-login.outcome }}"', publication)
        self.assertNotIn("toJSON(steps)", publication)
        self.assertIn("github-tag.json", publication)

    def test_engineering_gate_checks_every_shell_script_before_running_later_gates(self) -> None:
        workflow = QUALITY_WORKFLOW.read_text(encoding="utf-8")
        body = workflow.split("      - name: Verify engineering and release contracts\n")[1]
        body = body.split("\n      - name:")[0]
        script = textwrap.dedent(body.split("        run: |\n")[1])
        scripts = ("eng/a.sh", "eng/z.sh", "eng/release/a.sh", "eng/release/z.sh")

        for invalid in (None, *scripts):
            with self.subTest(invalid=invalid), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                for file_name in scripts:
                    helper = root / file_name
                    helper.parent.mkdir(parents=True, exist_ok=True)
                    helper.write_text("if then\n" if file_name == invalid else "true\n", encoding="ascii")

                (root / "bin").mkdir()
                for command in ("python3", "node"):
                    helper = root / "bin" / command
                    helper.write_text("#!/bin/sh\nprintf '%s\\n' gate >> later-gates.log\n", encoding="ascii")
                    helper.chmod(0o755)

                environment = tag_contract.isolated_environment()
                environment["PATH"] = f"{root / 'bin'}{os.pathsep}{environment['PATH']}"
                result = subprocess.run(
                    ["bash", "--noprofile", "--norc", "-e", "-c", script],
                    cwd=root, env=environment, capture_output=True, text=True, check=False, timeout=10,
                )

                if invalid is None:
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertEqual((root / "later-gates.log").read_text().splitlines(), ["gate"] * 3)
                else:
                    self.assertNotEqual(result.returncode, 0, result.stdout)
                    self.assertIn(invalid, result.stderr)
                    self.assertFalse((root / "later-gates.log").exists())

    def test_every_logged_publication_gate_preserves_failure_and_diagnostics(self) -> None:
        workflow = RELEASE_WORKFLOW.read_text(encoding="utf-8").split("\n  publish:\n")[1]
        checks = (
            ("Bind publication to the qualified commit and requested tag", "source.log", 1),
            ("Verify authorized signed annotated release tag", "tag.log", 23),
            ("Verify qualified bytes again before publication", "qualified-bytes.log", 23),
            ("Verify provenance and SBOM attestations", "attestations.log", 23),
            ("Check NuGet.org immediately before publication", "nuget-preflight.log", 23),
            ("Publish missing packages or verify exact existing bytes", "nuget-push.log", 23),
        )
        for name, log_name, expected_exit in checks:
            with self.subTest(step=name), tempfile.TemporaryDirectory(prefix="safemigrations-gate-") as directory:
                body = workflow.split(f"      - name: {name}\n")[1].split("\n      - name:")[0]
                self.assertIn("        shell: bash\n", body)
                script = textwrap.dedent(body.split("        run: |\n")[1])
                root = Path(directory)
                for file_name in (
                    "eng/release/verify-tag.sh", "eng/release/verify-attestations.sh",
                    "eng/verify-package-contents.sh", "eng/publish-nuget.sh", "bin/sha256sum",
                ):
                    helper = root / file_name
                    helper.parent.mkdir(parents=True, exist_ok=True)
                    helper.write_text("#!/bin/sh\necho 'fixture rejection' >&2\nexit 23\n", encoding="ascii")
                    helper.chmod(0o755)
                (root / "artifacts/release-publication").mkdir(parents=True)
                (root / "artifacts/packages").mkdir()
                environment = tag_contract.isolated_environment()
                environment.update({
                    "PATH": f"{root / 'bin'}{os.pathsep}{environment['PATH']}",
                    "GITHUB_REF": "refs/heads/other",
                    "GITHUB_SHA": "a" * 40,
                    "RELEASE_TAG": "v10.0.0-rc.1",
                    "PACKAGE_VERSION": "10.0.0-rc.1",
                })
                result = subprocess.run(
                    ["bash", "--noprofile", "--norc", "-e", "-o", "pipefail", "-c", script],
                    cwd=root, env=environment, capture_output=True, text=True, check=False, timeout=10,
                )

                self.assertEqual(result.returncode, expected_exit, result.stdout + result.stderr)
                self.assertTrue((root / "artifacts/release-publication" / log_name).read_text().strip())

    def test_readback_failure_is_not_masked_by_successful_log_capture(self) -> None:
        workflow = RELEASE_WORKFLOW.read_text(encoding="utf-8")
        body = workflow.split(
            "      - name: Verify NuGet repository signatures and content readback\n"
        )[1].split("\n      - name:")[0]
        script = textwrap.dedent(body.split("        run: |\n")[1])
        # Match GitHub's documented distinction: implicit bash has only -e;
        # explicit shell: bash also sets pipefail. Exercise the actual run block.
        shell = ["bash", "--noprofile", "--norc", "-e"]
        if "        shell: bash\n" in body:
            shell.extend(["-o", "pipefail"])

        with tempfile.TemporaryDirectory(prefix="safemigrations-readback-shell-") as directory:
            root = Path(directory)
            helper = root / "eng/readback-nuget.sh"
            helper.parent.mkdir()
            helper.write_text("#!/bin/sh\necho 'signature rejected' >&2\nexit 23\n", encoding="ascii")
            helper.chmod(0o755)
            (root / "artifacts/release-publication").mkdir(parents=True)
            environment = tag_contract.isolated_environment()
            environment["PACKAGE_VERSION"] = "10.0.0-rc.1"
            result = subprocess.run(
                [*shell, "-c", script], cwd=root, env=environment,
                capture_output=True, text=True, check=False, timeout=10,
            )

            self.assertEqual(result.returncode, 23, result.stdout + result.stderr)
            self.assertIn(
                "signature rejected",
                (root / "artifacts/release-publication/nuget-readback.log").read_text(),
            )

    def test_nuget_preflight_functions_run_under_fail_fast_semantics(self) -> None:
        publish_script = PUBLISH_SCRIPT.read_text(encoding="utf-8")

        self.assertNotIn("if ! verify_existing_package", publish_script)
        self.assertNotIn("if verify_existing_symbols", publish_script)
        self.assertIn(
            'existing_package_matches=false\n    verify_existing_package "$package_id"',
            publish_script,
        )
        self.assertIn(
            'existing_symbols_match=false\n    verify_existing_symbols "$package_id"',
            publish_script,
        )
        self.assertIn("--skip-duplicate", publish_script)

    @staticmethod
    def run_validator(
        *arguments: str,
        environment: dict[str, str] | None = None,
    ) -> subprocess.CompletedProcess[str]:
        process_environment = tag_contract.isolated_environment()
        process_environment.update(environment or {})

        return subprocess.run(
            ["bash", str(VALIDATOR), *arguments],
            cwd=REPOSITORY_ROOT,
            env=process_environment,
            check=False,
            capture_output=True,
            text=True,
        )


if __name__ == "__main__":
    unittest.main()
