"""Exercise actual admission commands and pin independent dependency handoff jobs."""

import json
import os
import re
import subprocess
import tempfile
import textwrap
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
CI = ROOT / ".github/workflows/ci.yml"
CAPTURE = ROOT / ".github/workflows/dependency-review.yml"
RECEIVER = ROOT / ".github/workflows/dependency-review-results.yml"
RESOLVER = ROOT / ".github/workflows/dependency-snapshot.yml"
MAINTENANCE = ROOT / "eng/tests/test-dependency-maintenance.sh"
AUDIT_WARNINGS = frozenset(("NU1901", "NU1902", "NU1903", "NU1904"))
BUILD_POLICY_PROJECT = (ROOT / "src/Doka.EntityFrameworkCore.SafeMigrations"
                        / "Doka.EntityFrameworkCore.SafeMigrations.csproj")


def build_policy_environment(**values):
    """Keep a local/CI probe independent of the runner executing the test itself."""
    environment = {**os.environ, "GITHUB_ACTIONS": "false", "ContinuousIntegrationBuild": "false",
                   "WarningsNotAsErrors": "", "WarningsAsErrors": "", "NoWarn": "",
                   "DOTNET_NOLOGO": "1", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1"}
    environment.update(values)

    return environment


def evaluate_build_policy(project=BUILD_POLICY_PROJECT, *, environment=None, properties=()):
    """Read real MSBuild evaluation without restoring packages or simulating warning tasks."""
    command = ["dotnet", "msbuild", str(project), "-nologo",
               "-getProperty:TreatWarningsAsErrors,WarningsNotAsErrors,NoWarn,NuGetAudit,NuGetAuditMode,"
               "NuGetAuditLevel,ContinuousIntegrationBuild,RestoreLockedMode,EfCorePackageVersion",
               *properties]
    result = subprocess.run(command, cwd=ROOT, env=build_policy_environment(**(environment or {})),
                            capture_output=True, text=True, check=False, timeout=60)
    if result.returncode:
        raise AssertionError("MSBuild policy evaluation failed: " + result.stderr)

    return json.loads(result.stdout)["Properties"]


def job_blocks(path=RECEIVER):
    """Read exact job boundaries without adding a YAML parser dependency."""
    jobs = path.read_text(encoding="ascii").split("jobs:\n", 1)[1]
    matches = list(re.finditer(r"^  ([a-z-]+):\n", jobs, re.M))

    return {match[1]: jobs[match.start():matches[index + 1].start()
                          if index + 1 < len(matches) else len(jobs)]
            for index, match in enumerate(matches)}


def step_command(job, name):
    """Read the workflow's actual shell body, not a duplicated test predicate."""
    step = job.split(f"- name: {name}\n", 1)[1].split("\n      - name:", 1)[0]
    commands = re.findall(r"^        run: (.+)$", step, re.M)
    if len(commands) != 1:
        raise ValueError("expected exactly one workflow command")

    if commands[0] == "|":
        return textwrap.dedent(step.split("        run: |\n", 1)[1])

    return commands[0]


def execute(command, directory, **values):
    """Run only a bounded engineering fragment with fixture environment values."""
    return subprocess.run(
        ["bash", "-euo", "pipefail", "-c", command], cwd=directory,
        env={**os.environ, **values}, capture_output=True, text=True, check=False)


def sdk_guard_command():
    """Extract precisely the maintained SDK guard, leaving unrelated checks outside scope."""
    source = MAINTENANCE.read_text(encoding="ascii")
    prefix = 'explicit_sdk_versions="$('

    return prefix + source.split(prefix, 1)[1].split('floating_runner_references="$(', 1)[0]


def sdk_fixture(root: Path, path: str, extra: str = "") -> None:
    """Create the smallest workflow exercising the real SDK-source guard."""
    workflows = root / ".github/workflows"
    workflows.mkdir(parents=True)
    (workflows / "fixture.yml").write_text(
        "steps:\n"
        "  - uses: actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68\n"
        "    with:\n"
        f"      global-json-file: {path}\n" + extra,
        encoding="ascii")


class DependencyReviewWorkflowTests(unittest.TestCase):
    """Keep untrusted restore, trusted publishing and PR-head status routing distinct."""

    def test_manual_ci_startup_calls_the_same_pipeline_with_explicit_pr_input(self):
        # Arrange
        caller = CI.read_text(encoding="ascii")
        receiver = RECEIVER.read_text(encoding="ascii")

        # Act
        startup = job_blocks(CI)["dependency-review-startup"]

        # Assert
        self.assertIn("workflow_dispatch:", caller)
        self.assertIn("workflow_call:", receiver)
        self.assertIn("name: Dependency review startup", startup)
        self.assertNotIn("name: dependency-review\n", startup)
        self.assertIn("if: ${{ github.event_name == 'workflow_dispatch' "
                      "&& inputs.dependency-review-pr != '' }}", startup)
        self.assertIn("uses: ./.github/workflows/dependency-review-results.yml", startup)
        self.assertIn("dependency-review-pr: ${{ inputs.dependency-review-pr }}", startup)
        self.assertEqual(2, receiver.count(
            "DEPENDENCY_REVIEW_PR: ${{ inputs.dependency-review-pr }}"))
        self.assertEqual(1, receiver.count("uses: actions/dependency-review-action@"))
        self.assertNotIn("workflow_dispatch:", receiver)
        self.assertNotIn("continue-on-error:", startup + receiver)

    def test_manual_startup_does_not_skip_or_elevate_normal_qualification(self):
        # Arrange
        caller = CI.read_text(encoding="ascii")

        # Act
        qualification = job_blocks(CI)["quality-gates"]
        defaults = caller.split("jobs:\n", 1)[0]

        # Assert
        self.assertIn("permissions:\n  contents: read", defaults)
        self.assertIn("uses: ./.github/workflows/quality-gates.yml", qualification)
        self.assertIn("permissions:\n      contents: read", qualification)
        self.assertIn("capture-sqlserver-baseline: ${{ github.event_name "
                      "== 'workflow_dispatch' && inputs.capture-sqlserver-baseline }}", qualification)
        self.assertNotIn("    if:", qualification)
        self.assertNotIn(": write", qualification)
        self.assertNotIn("dependency-review-pr", qualification)
        self.assertNotIn("continue-on-error:", caller)

    def test_successful_preparation_and_submission_admit_review(self):
        # Arrange
        command = step_command(job_blocks()["review"], "Require both completed snapshot submissions")

        # Act
        result = execute(command, ROOT, CONTEXT_RESULT="success", SNAPSHOT_RESULT="success")

        # Assert
        self.assertEqual(0, result.returncode, result.stderr)

    def test_failed_cancelled_skipped_missing_or_injected_results_block_review(self):
        command = step_command(job_blocks()["review"], "Require both completed snapshot submissions")
        fields = ("CONTEXT_RESULT", "SNAPSHOT_RESULT")
        for field in fields:
            for value in ("failure", "cancelled", "skipped", "", "success\nfailure"):
                with self.subTest(field=field, value=value):
                    # Arrange
                    environment = dict.fromkeys(fields, "success")
                    environment[field] = value

                    # Act
                    result = execute(command, ROOT, **environment)

                    # Assert
                    self.assertNotEqual(0, result.returncode)

    def test_forks_and_bots_restore_only_in_the_unprivileged_pr_workflow(self):
        """The privileged receiver never executes code from either contributor revision."""
        # Arrange
        capture = CAPTURE.read_text(encoding="ascii")
        receiver = RECEIVER.read_text(encoding="ascii")

        # Act
        capture_jobs = job_blocks(CAPTURE)
        receiver_jobs = job_blocks()

        # Assert
        self.assertEqual({"request-dependency-review", "prepare-base", "prepare-head"}, set(capture_jobs))
        self.assertEqual({"context", "submit-nuget", "review"}, set(receiver_jobs))
        self.assertIn("permissions: {}", capture)
        self.assertIn("pull_request:", capture)
        self.assertIn("workflow_run:", receiver)
        self.assertIn("needs: [prepare-base, prepare-head]", capture_jobs["request-dependency-review"])
        self.assertNotIn("uses: ./.github/workflows/dependency-snapshot.yml", receiver)
        self.assertNotIn("dotnet restore", receiver)
        for forbidden in ("actions/checkout@", "dotnet restore", "upload-artifact@", "GH_TOKEN"):
            self.assertNotIn(forbidden, capture)

        for forbidden in ("workflow_dispatch:", "pull_request_target:", "restricted", "dependabot"):
            self.assertNotIn(forbidden, capture + receiver)

    def test_both_exact_revisions_use_separate_readonly_restore_calls(self):
        # Arrange
        jobs = job_blocks(CAPTURE)
        resolver = RESOLVER.read_text(encoding="ascii")

        # Act
        restore_calls = {revision: jobs["prepare-" + revision] for revision in ("base", "head")}

        # Assert
        for revision, call in restore_calls.items():
            self.assertIn("permissions:\n      contents: read", call)
            self.assertIn("uses: ./.github/workflows/dependency-snapshot.yml", call)
            self.assertIn("cache-mode: none", call)
            self.assertIn(f"sha: ${{{{ github.event.pull_request.{revision}.sha }}}}", call)
            self.assertIn(f"revision: {revision}", call)
            self.assertNotIn("write", call)

        self.assertIn("permissions:\n  contents: read", resolver)
        self.assertIn("if: ${{ github.event_name == 'pull_request' }}", resolver)
        self.assertIn("cache-mode: none", job_blocks(RESOLVER)["restore"])
        self.assertIn("ref: ${{ inputs.sha }}", resolver)
        self.assertIn("global-json-file: snapshot-source/global.json", resolver)
        self.assertIn("dotnet restore Doka.EntityFrameworkCore.SafeMigrations.slnx --locked-mode", resolver)
        self.assertIn("prepare-dependency-snapshot-inputs.py snapshot-source snapshot-inputs", resolver)
        self.assertIn("name: dependency-inputs-${{ inputs.revision }}-${{ github.run_id }}", resolver)
        self.assertIn("-${{ github.run_attempt }}", resolver)
        self.assertIn("artifact-id: ${{ steps.upload.outputs.artifact-id }}", resolver)
        self.assertIn("value: ${{ jobs.restore.outputs.artifact-id }}", resolver)
        self.assertNotIn("contents: write", resolver)

    def test_publisher_binds_each_revision_to_its_own_immutable_artifact(self):
        # Arrange
        submit = job_blocks()["submit-nuget"]

        # Act
        matrix = submit.split("matrix:\n", 1)[1].split("    steps:\n", 1)[0]

        # Assert
        for revision in ("base", "head"):
            item = matrix.split(f"- revision: {revision}\n", 1)[1].split("- revision:", 1)[0]
            self.assertIn(f"sha: ${{{{ needs.context.outputs.{revision}_sha }}}}", item)
            self.assertIn(f"artifact_id: ${{{{ needs.context.outputs.{revision}_artifact_id }}}}", item)

        self.assertIn("artifact-ids: ${{ matrix.artifact_id }}", submit)
        self.assertIn("run-id: ${{ needs.context.outputs.source_run_id }}", submit)
        self.assertIn("github-token: ${{ github.token }}", submit)
        self.assertIn("actions: read", submit)
        self.assertNotIn("name: dependency-inputs-", submit)
        self.assertIn("path: ${{ runner.temp }}/dependency-inputs", submit)
        self.assertIn("merge-multiple: true", submit)

    def test_publisher_validates_directory_data_before_official_submission(self):
        # Arrange
        submit = job_blocks()["submit-nuget"]
        resolver = RESOLVER.read_text(encoding="ascii")

        # Act
        validation = submit.index("- name: Revalidate source-bound data before snapshot writes")
        publication = submit.index("- name: Submit resolved NuGet snapshot")

        # Assert
        self.assertLess(validation, publication)
        self.assertIn("contents: write", submit)
        self.assertIn("ref: ${{ github.workflow_sha }}", submit)
        self.assertIn("path: engineering", submit)
        self.assertIn("persist-credentials: false", submit)
        self.assertIn("git/trees/${SNAPSHOT_SHA}?recursive=1", submit)
        self.assertIn("prepare-dependency-snapshot-inputs.py receive", submit)
        self.assertIn('"$RUNNER_TEMP/dependency-inputs" snapshot-source', submit)
        self.assertIn("detectorsFilter: MSBuildBinaryLog", submit)
        self.assertIn("correlator: safemigrations-nuget-resolved-v1", submit)
        self.assertIn("snapshot-sha: ${{ matrix.sha }}", submit)
        self.assertIn("snapshot-ref: ${{ matrix.ref }}", submit)
        self.assertNotIn("dotnet restore", submit)
        self.assertNotIn("checks: write", submit + resolver)
        self.assertNotIn("ref: ${{ inputs.sha }}", submit)

    def test_receiver_policy_comes_only_from_its_trusted_workflow_sha(self):
        # Arrange
        receiver = RECEIVER.read_text(encoding="ascii")

        # Act
        checkouts = re.findall(r"uses: actions/checkout@[^\n]+\n(.*?)(?=\n      - name:|\Z)",
                               receiver, re.S)

        # Assert
        self.assertEqual(3, len(checkouts))
        for checkout in checkouts:
            self.assertIn("persist-credentials: false", checkout)
            self.assertIn("ref: ${{ github.workflow_sha }}", checkout)

        self.assertNotIn("\n          ref: ${{ needs.context.outputs.head_sha }}", receiver)
        self.assertNotIn("\n          ref: ${{ needs.context.outputs.base_sha }}", receiver)

    def test_context_and_finalizer_use_immutable_context_and_all_guard_outcomes(self):
        # Arrange
        jobs = job_blocks()
        review = jobs["review"]

        # Act
        finalizer = review.split("- name: Finalize the exact PR-head check\n", 1)[1]

        # Assert
        self.assertIn("artifact_id: ${{ steps.upload.outputs.artifact-id }}", jobs["context"])
        self.assertIn("artifact-ids: ${{ needs.context.outputs.artifact_id }}", review)
        self.assertIn("always() && needs.context.result == 'success'", review)
        self.assertIn("always() && steps.context-download.outcome == 'success'", finalizer)
        for step in ("admission", "review", "guards"):
            self.assertIn(f"steps.{step}.outcome == 'success'", finalizer)

        for job in ("context", "submit-nuget"):
            self.assertIn(f'"{job}":"${{{{ needs.{job}.result }}}}"', finalizer)

        self.assertIn("dependency-review-handoff.py finish", finalizer)
        self.assertNotIn("continue-on-error:", review)
        self.assertNotIn("warn-only:", review)


class DependencySdkSourceTests(unittest.TestCase):
    """Exercise the maintained SDK guard with allowed and rejected workflow fixtures."""

    def test_actual_guard_accepts_root_and_exact_snapshot_checkout_global_json(self):
        for path in ("global.json", "snapshot-source/global.json"):
            with self.subTest(path=path), tempfile.TemporaryDirectory() as temporary:
                # Arrange
                root = Path(temporary)
                command = sdk_guard_command()
                sdk_fixture(root, path)

                # Act
                result = execute(command, root)

                # Assert
                self.assertEqual(0, result.returncode, result.stderr)

    def test_actual_guard_rejects_arbitrary_paths_and_wrong_filenames(self):
        for path in ("other/global.json", "../global.json", "snapshot-source/other.json",
                     "snapshot-source/global.json.old"):
            with self.subTest(path=path), tempfile.TemporaryDirectory() as temporary:
                # Arrange
                root = Path(temporary)
                command = sdk_guard_command()
                sdk_fixture(root, path)

                # Act
                result = execute(command, root)

                # Assert
                self.assertNotEqual(0, result.returncode)

    def test_actual_guard_rejects_explicit_numeric_sdk_versions(self):
        # Arrange
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            command = sdk_guard_command()
            sdk_fixture(root, "global.json", "      dotnet-version: 10.0.402\n")

            # Act
            result = execute(command, root)

            # Assert
            self.assertNotEqual(0, result.returncode)
            self.assertIn("Workflow SDK versions must come from global.json.", result.stderr)


class LocalAdvisoryPolicyTests(unittest.TestCase):
    """Keep local advisory visibility separate from mandatory CI/release rejection."""

    def test_local_advisories_remain_warnings_without_disabling_audit(self):
        # Arrange
        environment = build_policy_environment()

        # Act
        observed = evaluate_build_policy(environment=environment)

        # Assert
        exceptions = set(filter(None, observed["WarningsNotAsErrors"].split(";")))
        self.assertEqual(AUDIT_WARNINGS, exceptions)
        self.assertEqual("true", observed["TreatWarningsAsErrors"])
        self.assertEqual("true", observed["NuGetAudit"])
        self.assertEqual("all", observed["NuGetAuditMode"])
        self.assertEqual("low", observed["NuGetAuditLevel"])
        self.assertTrue(AUDIT_WARNINGS.isdisjoint(observed["NoWarn"].split(";")))
        self.assertTrue({"NU1900", "NU1905", "CS0123"}.isdisjoint(exceptions))

    def test_local_exception_preserves_existing_caller_warning_choices(self):
        # Arrange
        environment = {"WarningsNotAsErrors": "CS0618"}

        # Act
        observed = evaluate_build_policy(environment=environment)

        # Assert
        self.assertEqual(AUDIT_WARNINGS | {"CS0618"},
                         set(filter(None, observed["WarningsNotAsErrors"].split(";"))))

    def test_github_and_explicit_ci_override_inherited_warning_exceptions(self):
        for context in ({"GITHUB_ACTIONS": "true"}, {"ContinuousIntegrationBuild": "true"},
                        {"GITHUB_ACTIONS": "true", "ContinuousIntegrationBuild": "false"}):
            with self.subTest(context=context):
                # Arrange
                environment = {"WarningsNotAsErrors": "CS0618;NU1902", **context}

                # Act
                observed = evaluate_build_policy(environment=environment)

                # Assert
                self.assertEqual("", observed["WarningsNotAsErrors"])
                self.assertEqual("true", observed["TreatWarningsAsErrors"])
                self.assertEqual("true", observed["NuGetAudit"])
                self.assertEqual("all", observed["NuGetAuditMode"])
                self.assertEqual("low", observed["NuGetAuditLevel"])

    def test_strict_global_cli_override_remains_authoritative_locally(self):
        # Arrange
        properties = ("-p:WarningsNotAsErrors=",)

        # Act
        observed = evaluate_build_policy(properties=properties)

        # Assert
        self.assertEqual("", observed["WarningsNotAsErrors"])
        self.assertEqual("true", observed["TreatWarningsAsErrors"])

    def test_ci_preserves_product_consumer_and_bundle_lock_boundaries(self):
        consumer = (ROOT / "eng/package-consumer/MySql"
                    / "Doka.EntityFrameworkCore.SafeMigrations.MySql.PackageConsumer.csproj")

        cases = ((BUILD_POLICY_PROJECT, (), "true"),
                 (consumer, ("-p:SafeMigrationsPackageConsumerMode=Package",), ""),
                 (BUILD_POLICY_PROJECT, ("-p:RuntimeIdentifier=linux-x64",), ""))

        for project, properties, expected_lock in cases:
            with self.subTest(project=project.name, properties=properties):
                # Arrange
                environment = {"ContinuousIntegrationBuild": "true"}

                # Act
                observed = evaluate_build_policy(project, environment=environment, properties=properties)

                # Assert
                self.assertEqual(expected_lock, observed["RestoreLockedMode"])
                self.assertEqual("", observed["WarningsNotAsErrors"])

    def test_shared_version_override_survives_configuration_regrouping(self):
        # Arrange
        properties = ("-p:EfCorePackageVersion=10.0.14",)

        # Act
        observed = evaluate_build_policy(properties=properties)

        # Assert
        self.assertEqual("10.0.14", observed["EfCorePackageVersion"])

    def test_local_release_entrypoints_export_strict_policy_to_child_evaluation(self):
        for script in ("qualify-packages.sh", "verify-package-consumer.sh", "generate-sbom.sh"):
            with self.subTest(script=script):
                # Arrange
                source = (ROOT / "eng" / script).read_text(encoding="ascii")

                # Act
                declarations = re.findall(r"^export ContinuousIntegrationBuild=true$", source, re.M)
                command = "\n".join(declarations) + '\ndotnet msbuild "$POLICY_PROJECT" -nologo ' \
                    '-getProperty:WarningsNotAsErrors,NuGetAudit,TreatWarningsAsErrors'
                result = subprocess.run(["bash", "-eu", "-c", command], cwd=ROOT,
                                        env=build_policy_environment(POLICY_PROJECT=str(BUILD_POLICY_PROJECT)),
                                        capture_output=True, text=True, check=False, timeout=60)

                # Assert
                self.assertEqual(1, len(declarations), "Missing strict release policy in " + script)
                self.assertLess(source.index(declarations[0]), source.index("dotnet "))
                self.assertEqual(0, result.returncode, result.stderr)
                observed = json.loads(result.stdout)["Properties"]
                self.assertEqual("", observed["WarningsNotAsErrors"])
                self.assertEqual("true", observed["NuGetAudit"])
                self.assertEqual("true", observed["TreatWarningsAsErrors"])


if __name__ == "__main__":
    unittest.main()
