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
GRAPH = ROOT / ".github/workflows/dependency-graph.yml"
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
               "-getProperty:TreatWarningsAsErrors,WarningsAsErrors,WarningsNotAsErrors,NoWarn,NuGetAudit,"
               "NuGetAuditMode,NuGetAuditLevel,ContinuousIntegrationBuild,RestoreLockedMode,EfCorePackageVersion",
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


def capture_snapshot_restore(revision: str, returncode: int = 0, *, command=None):
    """Execute the real workflow shell with a non-restoring argument/exit-code recorder."""
    if command is None:
        command = step_command(job_blocks(RESOLVER)["restore"], "Restore complete dependency graph")

    with tempfile.TemporaryDirectory() as temporary_root:
        root = Path(temporary_root)
        recorder = root / "dotnet"
        arguments_file = root / "arguments.txt"
        recorder.write_text(
            '#!/usr/bin/env bash\nprintf "%s\\n" "$@" > "$RESTORE_ARGUMENTS"\n'
            'exit "$RESTORE_EXIT_CODE"\n', encoding="ascii")
        recorder.chmod(0o700)

        result = execute(command, root, SNAPSHOT_REVISION=revision,
                         PATH=str(root) + os.pathsep + os.environ["PATH"],
                         RESTORE_ARGUMENTS=str(arguments_file), RESTORE_EXIT_CODE=str(returncode))
        arguments = arguments_file.read_text(encoding="ascii").splitlines() if arguments_file.exists() else []

        return result, arguments


def sdk_guard_command():
    """Extract precisely the maintained SDK guard, leaving unrelated checks outside scope."""
    source = MAINTENANCE.read_text(encoding="ascii")
    prefix = 'explicit_sdk_versions="$('

    return prefix + source.split(prefix, 1)[1].split('floating_runner_references="$(', 1)[0]


def execute_snapshot_guard(command, payload, *, gh_returncode=0, **values):
    """Run maintained freshness shell with real jq and one bounded recorded GitHub response."""
    with tempfile.TemporaryDirectory() as temporary_root:
        root = Path(temporary_root)
        response = root / "response.json"
        response.write_text(payload if isinstance(payload, str) else json.dumps(payload), encoding="ascii")
        calls = root / "calls.txt"
        recorder = root / "gh"
        recorder.write_text(
            '#!/usr/bin/env bash\nprintf "%s\\n" "$@" > "$GH_CALLS"\n'
            'cat "$GH_RESPONSE"\nexit "$GH_EXIT_CODE"\n', encoding="ascii")
        recorder.chmod(0o700)
        environment = {
            "PATH": str(root) + os.pathsep + os.environ["PATH"],
            "GITHUB_REPOSITORY": "doka-labs/Doka.EntityFrameworkCore.SafeMigrations",
            "GITHUB_REF": "refs/heads/main", "GITHUB_SHA": "a" * 40,
            "GITHUB_WORKFLOW_SHA": "a" * 40, "RUNNER_TEMP": str(root),
            "GH_CALLS": str(calls), "GH_RESPONSE": str(response), "GH_EXIT_CODE": str(gh_returncode),
            "DEFAULT_BRANCH": "main", "SNAPSHOT_REF": "refs/heads/main", "SNAPSHOT_SHA": "a" * 40,
            **values,
        }

        result = execute(command, root, **environment)
        arguments = calls.read_text(encoding="ascii").splitlines() if calls.exists() else []

        return result, arguments


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


class MainDependencyGraphWorkflowTests(unittest.TestCase):
    """Keep protected-main graph refresh separate from PR comparison and restored hooks."""

    def test_main_refresh_has_automatic_and_manual_protected_main_entrypoints(self):
        # Arrange
        self.assertTrue(GRAPH.is_file(), "The authoritative main snapshot needs its own refresh path.")
        source = GRAPH.read_text(encoding="ascii")

        # Act
        jobs = job_blocks(GRAPH)

        # Assert
        self.assertEqual({"capture", "publish"}, set(jobs))
        self.assertRegex(source, r"(?m)^  push:\n    branches:.*main")
        self.assertIn("  workflow_dispatch:", source)
        self.assertNotIn("pull_request_target:", source)
        for job in jobs.values():
            self.assertIn("github.ref == 'refs/heads/main'", job)
            self.assertIn("github.event.repository.default_branch == 'main'", job)
            self.assertIn("github.ref_protected", job)

    def test_main_capture_cannot_inherit_publisher_permissions_or_cache(self):
        # Arrange
        jobs = job_blocks(GRAPH)
        defaults = GRAPH.read_text(encoding="ascii").split("jobs:\n", 1)[0]

        # Act
        capture = jobs["capture"]

        # Assert
        self.assertIn("permissions:\n  contents: read", defaults)
        self.assertNotIn("permissions:", capture)
        self.assertIn("cache-mode: none", capture)
        self.assertNotIn(": write", capture)
        self.assertNotIn("continue-on-error:", capture)
        self.assertIn("dotnet restore", capture)
        self.assertIn("--locked-mode", capture)
        self.assertIn("-p:WarningsNotAsErrors=", capture)
        self.assertIn("prepare-dependency-snapshot-inputs.py snapshot-source", capture)
        self.assertIn("artifact-id: ${{ steps.upload.outputs.artifact-id }}", capture)
        self.assertIn("persist-credentials: false", capture)

    def test_main_publisher_uses_only_exact_validated_data_and_the_existing_snapshot_identity(self):
        # Arrange
        publish = job_blocks(GRAPH)["publish"]

        # Act
        guard = publish.index("- name: Require current protected main")
        submission = publish.index("- name: Submit current main snapshot")

        # Assert
        self.assertLess(guard, submission)
        self.assertIn("needs: capture", publish)
        self.assertIn("permissions:\n      contents: write", publish)
        self.assertIn("artifact-ids: ${{ needs.capture.outputs.artifact-id }}", publish)
        self.assertNotIn("run-id:", publish)
        self.assertNotIn("always()", publish)
        self.assertIn("prepare-dependency-snapshot-inputs.py receive", publish)
        self.assertIn("git/trees/", publish)
        self.assertIn("correlator: safemigrations-nuget-resolved-v1", publish)
        self.assertIn("detector-name: SafeMigrations NuGet qualification", publish)
        self.assertIn("snapshot-sha: ${{ github.sha }}", publish)
        self.assertIn("snapshot-ref: ${{ github.ref }}", publish)
        self.assertIn("detectorsFilter: MSBuildBinaryLog", publish)
        self.assertNotIn("dotnet restore", publish)
        self.assertNotIn("continue-on-error:", publish)
        self.assertNotIn("checks: write", publish)

    def test_main_restore_remains_strict_despite_ambient_warning_or_audit_exceptions(self):
        # Arrange
        command = step_command(job_blocks(GRAPH)["capture"], "Restore current main dependency graph")
        environment = {"GITHUB_ACTIONS": "true", "NoWarn": "NU1902;NU1900",
                       "WarningsNotAsErrors": "NU1901;NU1902;NU1903;NU1904;CS0618",
                       "TreatWarningsAsErrors": "false", "NuGetAudit": "false",
                       "NuGetAuditMode": "direct", "NuGetAuditLevel": "critical"}

        # Act
        result, arguments = capture_snapshot_restore("head", command=command)
        properties = tuple(argument for argument in arguments if argument.startswith("-p:"))
        observed = evaluate_build_policy(environment=environment, properties=properties)

        # Assert
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("", observed["NoWarn"])
        self.assertEqual("true", observed["TreatWarningsAsErrors"])
        self.assertEqual("true", observed["NuGetAudit"])
        self.assertEqual("all", observed["NuGetAuditMode"])
        self.assertEqual("low", observed["NuGetAuditLevel"])
        self.assertEqual(1, arguments.count("--locked-mode"))
        # Only the audit advisories themselves stay non-fatal; an ambient
        # CS0618 exception cannot ride along into the protected-main writer.
        self.assertEqual(AUDIT_WARNINGS,
                         set(filter(None, observed["WarningsNotAsErrors"].split(";"))))

    def test_main_graph_refresh_survives_an_advisory_that_main_still_carries(self):
        # Arrange
        command = step_command(job_blocks(GRAPH)["capture"], "Restore current main dependency graph")

        # Act
        result, arguments = capture_snapshot_restore("head", command=command)
        properties = tuple(argument for argument in arguments if argument.startswith("-p:"))
        observed = evaluate_build_policy(properties=properties)

        # Assert
        # WHY: A fatal audit here would disable the refresh exactly while a
        # stale vulnerable snapshot is the one that has to be replaced.
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertTrue(AUDIT_WARNINGS.issubset(set(observed["WarningsNotAsErrors"].split(";"))))
        self.assertNotIn("NU1900", observed["WarningsNotAsErrors"].split(";"))
        self.assertNotIn("NU1900", observed["NoWarn"].split(";"))

    def test_main_and_pr_writers_share_a_retained_branch_scoped_queue(self):
        # Arrange
        main_publish = job_blocks(GRAPH)["publish"]
        pr_publish = job_blocks(RECEIVER)["submit-nuget"]

        # Act
        main_group = re.search(r"(?m)^      group: (.+)$", main_publish).group(1)
        pr_group = re.search(r"(?m)^      group: (.+)$", pr_publish).group(1)

        # Assert
        self.assertEqual(main_group.replace("github.ref", "matrix.ref"), pr_group)
        self.assertIn("github.repository_id", main_group)
        for writer in (main_publish, pr_publish):
            self.assertIn("      queue: max", writer)
            self.assertIn("      cancel-in-progress: false", writer)

    def test_current_main_passes_both_live_guards(self):
        for job in ("capture", "publish"):
            with self.subTest(job=job):
                # Arrange
                command = step_command(job_blocks(GRAPH)[job], "Require current protected main")
                branch = {"name": "main", "protected": True, "commit": {"sha": "a" * 40}}

                # Act
                result, calls = execute_snapshot_guard(command, branch)

                # Assert
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertTrue(any(value.endswith("/branches/main") for value in calls))

    def test_stale_unprotected_malformed_or_unavailable_main_fails_both_live_guards(self):
        cases = (
            ({"name": "main", "protected": True, "commit": {"sha": "b" * 40}}, 0),
            ({"name": "main", "protected": False, "commit": {"sha": "a" * 40}}, 0),
            ({"name": "other", "protected": True, "commit": {"sha": "a" * 40}}, 0),
            ({"name": "main", "protected": "true", "commit": {"sha": "a" * 40}}, 0),
            ({}, 0), ("not-json", 0),
            ({"name": "main", "protected": True, "commit": {"sha": "a" * 40}}, 22),
        )
        for job in ("capture", "publish"):
            for payload, status in cases:
                with self.subTest(job=job, payload=payload, status=status):
                    # Arrange
                    command = step_command(job_blocks(GRAPH)[job], "Require current protected main")

                    # Act
                    result, _ = execute_snapshot_guard(command, payload, gh_returncode=status)

                    # Assert
                    self.assertNotEqual(0, result.returncode)

    def test_wrong_ref_workflow_or_sha_cannot_reach_main_api(self):
        cases = ({"GITHUB_REF": "refs/heads/feature"}, {"GITHUB_SHA": "A" * 40},
                 {"GITHUB_SHA": "a" * 39}, {"GITHUB_WORKFLOW_SHA": "b" * 40})
        for job in ("capture", "publish"):
            for environment in cases:
                with self.subTest(job=job, environment=environment):
                    # Arrange
                    command = step_command(job_blocks(GRAPH)[job], "Require current protected main")

                    # Act
                    result, calls = execute_snapshot_guard(command, {}, **environment)

                    # Assert
                    self.assertNotEqual(0, result.returncode)
                    self.assertEqual([], calls)

    def test_pr_default_branch_guard_runs_inside_writer_before_submission(self):
        # Arrange
        submit = job_blocks(RECEIVER)["submit-nuget"]

        # Act
        guard = submit.index("- name: Require current default-branch snapshot")
        publication = submit.index("- name: Submit resolved NuGet snapshot")

        # Assert
        self.assertLess(guard, publication)
        self.assertIn("SNAPSHOT_REF: ${{ matrix.ref }}", submit)
        self.assertIn("SNAPSHOT_SHA: ${{ matrix.sha }}", submit)
        self.assertIn("DEFAULT_BRANCH: ${{ github.event.repository.default_branch }}", submit)

    def test_pr_default_guard_accepts_current_but_rejects_stale_or_unproven_main(self):
        cases = (({"protected": True, "commit": {"sha": "a" * 40}}, 0, True),
                 ({"protected": True, "commit": {"sha": "b" * 40}}, 0, False),
                 ({"protected": False, "commit": {"sha": "a" * 40}}, 0, False),
                 ({}, 0, False), ("not-json", 0, False),
                 ({"protected": True, "commit": {"sha": "a" * 40}}, 22, False))
        for payload, status, accepted in cases:
            with self.subTest(payload=payload, status=status):
                # Arrange
                command = step_command(job_blocks(RECEIVER)["submit-nuget"],
                                       "Require current default-branch snapshot")

                # Act
                result, calls = execute_snapshot_guard(command, payload, gh_returncode=status)

                # Assert
                self.assertEqual(accepted, result.returncode == 0, result.stderr)
                self.assertTrue(any(value.endswith("/branches/main") for value in calls))

    def test_pr_head_or_nondefault_snapshot_does_not_query_default_main(self):
        for ref in ("refs/pull/61/head", "refs/heads/maintenance/10.x"):
            with self.subTest(ref=ref):
                # Arrange
                command = step_command(job_blocks(RECEIVER)["submit-nuget"],
                                       "Require current default-branch snapshot")

                # Act
                result, calls = execute_snapshot_guard(command, {}, SNAPSHOT_REF=ref)

                # Assert
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertEqual([], calls)


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


class DependencySnapshotRestorePolicyTests(unittest.TestCase):
    """Historical comparison capture must not become a release or PR-head audit waiver."""

    def test_revision_binding_is_data_not_interpolated_shell(self):
        # Arrange
        source = RESOLVER.read_text(encoding="ascii")

        # Act
        step = source.split("- name: Restore complete dependency graph\n", 1)[1].split("\n      - name:", 1)[0]
        command = step_command(job_blocks(RESOLVER)["restore"], "Restore complete dependency graph")

        # Assert
        self.assertIn("SNAPSHOT_REVISION: ${{ inputs.revision }}", step)
        self.assertNotIn("${{", command)

    def test_base_advisories_remain_visible_with_no_other_warning_exceptions(self):
        # Arrange
        environment = {"GITHUB_ACTIONS": "true", "NoWarn": "NU1902;NU1900",
                       "WarningsAsErrors": "NU1902", "WarningsNotAsErrors": "CS0618;NU1900"}

        # Act
        result, arguments = capture_snapshot_restore("base")
        properties = tuple(argument for argument in arguments if argument.startswith("-p:"))
        observed = evaluate_build_policy(environment=environment, properties=properties)

        # Assert
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(AUDIT_WARNINGS, set(filter(None, observed["WarningsNotAsErrors"].split(";"))))
        self.assertEqual("", observed["NoWarn"])
        self.assertEqual("", observed["WarningsAsErrors"])
        self.assertEqual("true", observed["TreatWarningsAsErrors"])
        self.assertEqual("true", observed["NuGetAudit"])
        self.assertEqual("all", observed["NuGetAuditMode"])
        self.assertEqual("low", observed["NuGetAuditLevel"])
        self.assertEqual(1, arguments.count("--locked-mode"))
        self.assertEqual(1, arguments.count("restore"))

    def test_head_restore_cannot_inherit_local_or_disabled_audit_policy(self):
        # Arrange
        environment = {"GITHUB_ACTIONS": "true", "NoWarn": "NU1901;NU1902;NU1903;NU1904",
                       "WarningsNotAsErrors": "NU1902;CS0618", "TreatWarningsAsErrors": "false",
                       "NuGetAudit": "false", "NuGetAuditMode": "direct", "NuGetAuditLevel": "critical"}

        # Act
        result, arguments = capture_snapshot_restore("head")
        properties = tuple(argument for argument in arguments if argument.startswith("-p:"))
        observed = evaluate_build_policy(environment=environment, properties=properties)

        # Assert
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("", observed["WarningsNotAsErrors"])
        self.assertEqual("", observed["NoWarn"])
        self.assertEqual("true", observed["TreatWarningsAsErrors"])
        self.assertEqual("true", observed["NuGetAudit"])
        self.assertEqual("all", observed["NuGetAuditMode"])
        self.assertEqual("low", observed["NuGetAuditLevel"])
        self.assertEqual(1, arguments.count("--locked-mode"))
        self.assertEqual(1, arguments.count("restore"))

    def test_unknown_revision_cannot_execute_a_restore(self):
        for revision in ("", "Base", "HEAD", "other", "base;head"):
            with self.subTest(revision=revision):
                # Arrange
                invalid_revision = revision

                # Act
                result, arguments = capture_snapshot_restore(invalid_revision)

                # Assert
                self.assertNotEqual(0, result.returncode)
                self.assertEqual([], arguments)
                self.assertIn("Unsupported dependency snapshot revision.", result.stderr)

    def test_failed_base_or_head_restore_is_never_accepted(self):
        for revision in ("base", "head"):
            for failure in (1, 42):
                with self.subTest(revision=revision, failure=failure):
                    # Arrange
                    failed_exit_code = failure

                    # Act
                    result, arguments = capture_snapshot_restore(revision, failed_exit_code)

                    # Assert
                    self.assertEqual(failed_exit_code, result.returncode)
                    self.assertEqual(1, arguments.count("restore"))


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
