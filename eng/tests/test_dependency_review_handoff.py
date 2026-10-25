"""Exercise the trusted handoff without tokens, network or external writes."""

import base64
import copy
import hashlib
import importlib.util
import io
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock


HELPER = Path(__file__).resolve().parents[1] / "dependency-review-handoff.py"


def load_helper():
    """Load the production module without running its command line."""
    spec = importlib.util.spec_from_file_location("dependency_handoff", HELPER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)

    return module


class HandoffTests(unittest.TestCase):
    """Pin automatic bot/fork handling and fail-closed authoritative identities."""

    def test_source_artifacts_bind_separate_exact_pr_revisions(self):
        """The privileged receiver consumes immutable PR-run data, never restores contributor code."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        add_source_input_evidence(api)

        # Act
        context = helper.begin(api, environment, event)

        # Assert
        self.assertEqual(801, context["source_artifacts"]["base"]["id"])
        self.assertEqual(802, context["source_artifacts"]["head"]["id"])
        self.assertEqual("sha256:" + "e" * 64, context["source_artifacts"]["head"]["digest"])

    def test_invalid_new_source_artifacts_replace_stale_same_head_success_with_failure(self):
        """Authenticated identity plus failed artifact admission creates a current explicit red check."""
        for mutation in ("missing", "expired", "duplicate"):
            with self.subTest(mutation=mutation):
                # Arrange
                helper = load_helper()
                environment, event, api = fixture()
                api.responses["repos/owner/repository/check-runs/699"] = {
                    "name": "dependency-review", "head_sha": "b" * 40,
                    "external_id": "dependency-review:400:1:899:1", "conclusion": "success"}
                inventory = api.responses[source_artifacts_route()]
                if mutation == "missing":
                    inventory["artifacts"].pop()
                    inventory["total_count"] -= 1
                elif mutation == "expired":
                    inventory["artifacts"][1]["expired"] = True
                else:
                    duplicate = copy.deepcopy(inventory["artifacts"][1])
                    duplicate["id"] = 803
                    inventory["artifacts"].append(duplicate)
                    inventory["total_count"] += 1

                # Act
                context = helper.begin(api, environment, event)

                # Assert
                self.assertFalse(context["ready"])
                self.assertEqual("POST", api.writes[0][0])
                self.assertEqual("b" * 40, api.writes[0][2]["head_sha"])
                self.assertEqual("dependency-review:400:2:900:1", api.writes[0][2]["external_id"])
                self.assertEqual("completed", api.writes[0][2]["status"])
                self.assertEqual("failure", api.writes[0][2]["conclusion"])
                self.assertEqual("success", api.responses["repos/owner/repository/check-runs/699"]["conclusion"])

    def test_missing_duplicate_expired_and_rebound_source_artifacts_are_rejected(self):
        """Authenticated source success cannot stand in for complete immutable artifact identities."""
        for mutation in ("missing", "duplicate", "expired", "past-expiry", "wrong-run", "wrong-sha",
                         "wrong-repository", "wrong-head-repository", "wrong-branch", "wrong-attempt",
                         "invalid-digest", "zero-size", "incomplete", "unbounded"):
            with self.subTest(mutation=mutation):
                # Arrange
                helper = load_helper()
                environment, event, api = fixture()
                add_source_input_evidence(api)
                inventory = api.responses[source_artifacts_route()]
                artifact = inventory["artifacts"][1]
                if mutation == "missing":
                    inventory["artifacts"].pop()
                    inventory["total_count"] -= 1
                elif mutation == "duplicate":
                    duplicate = copy.deepcopy(artifact)
                    duplicate["id"] = 803
                    inventory["artifacts"].append(duplicate)
                    inventory["total_count"] += 1
                elif mutation == "expired":
                    artifact["expired"] = True
                elif mutation == "past-expiry":
                    artifact["expires_at"] = "2020-01-01T00:00:00Z"
                elif mutation == "wrong-attempt":
                    artifact["name"] = "dependency-inputs-head-400-1"
                elif mutation == "invalid-digest":
                    artifact["digest"] = "sha256:unverified"
                elif mutation == "zero-size":
                    artifact["size_in_bytes"] = 0
                elif mutation == "incomplete":
                    inventory["total_count"] += 1
                elif mutation == "unbounded":
                    inventory["total_count"] = 100
                else:
                    field = {"wrong-run": "id", "wrong-sha": "head_sha",
                             "wrong-repository": "repository_id", "wrong-branch": "head_branch",
                             "wrong-head-repository": "head_repository_id"}[mutation]
                    artifact["workflow_run"][field] = 999 if field.endswith("id") else "changed"

                # Act
                context = helper.begin(api, environment, event)

                # Assert
                self.assertFalse(context["ready"])
                self.assertEqual("source_artifact_admission_failed", context["source_admission"])
                self.assertEqual("failure", api.writes[0][2]["conclusion"])

    def test_missing_skipped_or_rebound_source_producer_jobs_are_rejected(self):
        """Source artifacts need the complete successful producer set from this exact attempt."""
        for mutation in ("missing", "skipped", "failed", "pending", "duplicate", "unexpected",
                         "wrong-run", "wrong-attempt", "future-attempt", "wrong-sha", "incomplete"):
            with self.subTest(mutation=mutation):
                # Arrange
                helper = load_helper()
                environment, event, api = fixture()
                add_source_input_evidence(api)
                inventory = api.responses[source_jobs_route()]
                job = inventory["jobs"][0]
                if mutation == "missing":
                    inventory["jobs"].pop()
                    inventory["total_count"] -= 1
                elif mutation in ("skipped", "failed"):
                    job["conclusion"] = "skipped" if mutation == "skipped" else "failure"
                elif mutation == "pending":
                    job["status"] = "in_progress"
                elif mutation == "duplicate":
                    other = copy.deepcopy(job)
                    other["id"] = 999
                    inventory["jobs"].append(other)
                    inventory["total_count"] += 1
                elif mutation == "unexpected":
                    job["name"] = "another producer"
                elif mutation == "incomplete":
                    inventory["total_count"] += 1
                elif mutation == "future-attempt":
                    job["run_attempt"] = 3
                else:
                    job[{"wrong-run": "run_id", "wrong-attempt": "run_attempt",
                         "wrong-sha": "head_sha"}[mutation]] = "changed"

                # Act
                context = helper.begin(api, environment, event)

                # Assert
                self.assertFalse(context["ready"])
                self.assertEqual("source_artifact_admission_failed", context["source_admission"])
                self.assertEqual("failure", api.writes[0][2]["conclusion"])

    def test_failed_job_retry_reuses_each_successful_original_producer(self):
        """A retained base or both retained graphs keep their original artifact IDs and attempts."""
        for retained in (("base",), ("base", "head")):
            with self.subTest(retained=retained):
                # Arrange
                helper = load_helper()
                environment, event, api = fixture()
                for index, revision in enumerate(("base", "head")):
                    if revision in retained:
                        api.responses[source_jobs_route()]["jobs"][index]["run_attempt"] = 1
                        api.responses[source_artifacts_route()]["artifacts"][index]["name"] = (
                            f"dependency-inputs-{revision}-400-1")

                # Act
                context = helper.begin(api, environment, event)

                # Assert
                self.assertTrue(context["ready"])
                self.assertEqual(2, context["source_attempt"])
                for index, revision in enumerate(("base", "head")):
                    self.assertEqual(801 + index, context["source_artifacts"][revision]["id"])
                    self.assertEqual(1 if revision in retained else 2,
                                     context["source_artifacts"][revision]["producer_attempt"])

    def test_newer_failed_producer_cannot_borrow_an_older_successful_artifact(self):
        """Selecting all producer histories never permits success-filtered resurrection."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        inventory = api.responses[source_jobs_route()]
        older = copy.deepcopy(inventory["jobs"][1])
        older.update({"id": 999, "run_attempt": 1})
        inventory["jobs"].append(older)
        inventory["total_count"] += 1
        inventory["jobs"][1]["conclusion"] = "failure"

        # Act
        context = helper.begin(api, environment, event)

        # Assert
        self.assertFalse(context["ready"])
        self.assertEqual("failure", api.writes[0][2]["conclusion"])

    def test_replaced_source_artifact_cannot_complete_an_admitted_context(self):
        """A later immutable-ID or digest substitution invalidates the trusted handoff."""
        for field, replacement in (("id", 999), ("digest", "sha256:" + "f" * 64)):
            with self.subTest(field=field):
                # Arrange
                helper, environment, event, api, context = started_fixture()
                api.responses[source_artifacts_route()]["artifacts"][1][field] = replacement

                # Act
                successful = helper.finish(api, environment, event, context, success_results(), "success")

                # Assert
                self.assertFalse(successful)
                self.assertEqual("failure", api.writes[-1][2]["conclusion"])

    def test_registered_inactive_maintenance_base_accepts_fork_preparation(self):
        """A preparation PR needs Required Checks before its line is activated."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        set_base(api, "maintenance/10.x")

        # Act
        context = helper.begin(api, environment, event)

        # Assert
        self.assertTrue(context["ready"])
        self.assertEqual("refs/heads/maintenance/10.x", context["base_ref"])
        self.assertEqual("a" * 40, context["base_sha"])

    def test_registered_maintenance_base_uses_its_own_current_head(self):
        """Main metadata must not stand in for an independently maintained base."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        set_base(api, "maintenance/10.x")
        api.responses["repos/owner/repository/git/ref/heads/maintenance%2F10.x"]["object"]["sha"] = "c" * 40

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.begin(api, environment, event)

        self.assertEqual([], api.writes)

    def test_preparation_base_uses_independent_protected_main_authority(self):
        """A reviewed PR checkout cannot authorize another privileged target."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        api.responses["repos/owner/repository/branches/main"]["protected"] = False

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.begin(api, environment, event)

        self.assertEqual([], api.writes)

    def test_initial_main_startup_retains_exact_legacy_pr_contract(self):
        """The feature's first main PR can use reviewed-head startup before policy exists."""
        # Arrange
        helper = load_helper()
        environment, event, api = legacy_fixture(helper, manual_fixture)

        # Act
        context = helper.begin(api, environment, event)

        # Assert
        self.assertTrue(context["ready"])
        self.assertEqual("refs/heads/main", context["base_ref"])
        self.assertEqual("a" * 40, context["base_sha"])
        self.assertEqual(1, len(api.writes))

    def test_legacy_bridge_never_admits_maintenance_without_protected_policy(self):
        """Absence preserves only old main PR checks, not any new line permission."""
        # Arrange
        helper = load_helper()
        environment, event, api = legacy_fixture(helper, manual_fixture)
        set_base(api, "maintenance/10.x")

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.begin(api, environment, event)

        self.assertEqual([], api.writes)

    def test_legacy_bridge_rejects_failed_malformed_stale_and_unprotected_authority(self):
        """Only authenticated 404 at the exact current protected main is absence."""
        for mutation in ("forbidden", "malformed", "stale", "unprotected", "foreign404"):
            with self.subTest(mutation=mutation):
                # Arrange
                helper = load_helper()
                environment, event, api = legacy_fixture(helper, manual_fixture)
                route = "repos/owner/repository/contents/eng/release/lines-policy.json?ref=" + "a" * 40
                if mutation == "forbidden":
                    api.responses[route] = ValueError("HTTP 403 is not policy absence")
                elif mutation == "malformed":
                    api.responses[route] = {"type": "file", "encoding": "base64", "content": "invalid"}
                elif mutation == "stale":
                    api.responses["repos/owner/repository/git/ref/heads/main"]["object"]["sha"] = "c" * 40
                elif mutation == "unprotected":
                    api.responses["repos/owner/repository/branches/main"]["protected"] = False
                else:
                    api.responses[route] = helper.PolicyNotFound(route.replace("a" * 40, "c" * 40))

                # Act / Assert
                with self.assertRaises(ValueError):
                    helper.begin(api, environment, event)

                self.assertEqual([], api.writes)

    def test_http_status_bridge_recognizes_only_a_policy_get_404(self):
        """A textual error or unrelated API absence cannot enable legacy startup."""
        # Arrange
        helper = load_helper()
        route = "repos/owner/repository/contents/eng/release/lines-policy.json?ref=" + "a" * 40
        response = subprocess.CompletedProcess(
            [], 1, "HTTP/2.0 404 Not Found\nContent-Type: application/json\n\n{}", "")
        with mock.patch.object(helper.subprocess, "run", return_value=response) as run:
            # Act / Assert
            with self.assertRaises(helper.PolicyNotFound):
                helper.GitHub().request(route)

        self.assertIn("--include", run.call_args.args[0])

    def test_http_forbidden_body_cannot_spoof_policy_not_found(self):
        """The actual response header, not JSON/error prose, chooses the bridge."""
        # Arrange
        helper = load_helper()
        route = "repos/owner/repository/contents/eng/release/lines-policy.json?ref=" + "a" * 40
        response = subprocess.CompletedProcess([], 1, 'HTTP/2.0 403 Forbidden\n\n{"message":"HTTP 404"}', "")
        with mock.patch.object(helper.subprocess, "run", return_value=response):
            # Act / Assert
            with self.assertRaises(ValueError) as failure:
                helper.GitHub().request(route)

        self.assertNotIsInstance(failure.exception, helper.PolicyNotFound)

    def test_registered_maintenance_base_accepts_reviewed_manual_startup(self):
        """Reviewed same-repository startup retains the same exact-base path as forks."""
        # Arrange
        helper = load_helper()
        environment, event, api = manual_fixture()
        set_base(api, "maintenance/10.x")

        # Act
        context = helper.begin(api, environment, event)

        # Assert
        self.assertEqual("refs/heads/maintenance/10.x", context["base_ref"])
        self.assertTrue(context["ready"])

    def test_fork_capture_creates_check_on_exact_pr_head(self):
        """A fork needs no write token: the trusted receiver owns its head check."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()

        # Act
        context = helper.begin(api, environment, event)

        # Assert
        self.assertTrue(context["ready"])
        self.assertEqual("b" * 40, context["head_sha"])
        self.assertEqual("a" * 40, context["base_sha"])
        self.assertEqual("refs/pull/59/head", context["head_ref"])
        self.assertEqual("b" * 40, api.writes[0][2]["head_sha"])
        self.assertEqual("dependency-review", api.writes[0][2]["name"])

    def test_dependabot_capture_uses_same_authoritative_path(self):
        """Bot identity does not suppress capture, submission or the required verdict."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        api.responses["repos/owner/repository/pulls/59"]["user"] = {
            "login": "dependabot[bot]", "type": "Bot"}

        # Act
        context = helper.begin(api, environment, event)

        # Assert
        self.assertTrue(context["ready"])
        self.assertEqual(1, len(api.writes))

    def test_workflow_run_path_accepts_main_ref_suffix(self):
        """GitHub's documented @main path form preserves the exact workflow identity."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        set_run_paths(event, api, ".github/workflows/dependency-review.yml@main")

        # Act
        context = helper.begin(api, environment, event)

        # Assert
        self.assertTrue(context["ready"])
        self.assertEqual(1, len(api.writes))

    def test_workflow_run_path_accepts_pull_merge_ref_suffix(self):
        """A slash-containing ref suffix is metadata, not another workflow path."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        set_run_paths(event, api, ".github/workflows/dependency-review.yml@refs/pull/59/merge")

        # Act
        context = helper.begin(api, environment, event)

        # Assert
        self.assertTrue(context["ready"])
        self.assertEqual(1, len(api.writes))

    def test_workflow_run_path_rejects_different_file_with_ref_suffix(self):
        """A ref suffix cannot make a different workflow eligible for privileged handoff."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        set_run_paths(event, api, ".github/workflows/other.yml@main")

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.begin(api, environment, event)

        self.assertEqual([], api.writes)

    def test_workflow_run_path_rejects_empty_ref_suffix(self):
        """Only an actual bounded ref may follow the optional delimiter."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        set_run_paths(event, api, ".github/workflows/dependency-review.yml@")

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.begin(api, environment, event)

        self.assertEqual([], api.writes)

    def test_workflow_run_path_rejects_control_in_ref_suffix(self):
        """A suffix cannot smuggle line breaks into an accepted workflow identity."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        set_run_paths(event, api, ".github/workflows/dependency-review.yml@main\nforged")

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.begin(api, environment, event)

        self.assertEqual([], api.writes)

    def test_workflow_run_path_rejects_oversized_ref_suffix(self):
        """Unbounded identity strings are rejected even when their file prefix matches."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        set_run_paths(event, api, ".github/workflows/dependency-review.yml@" + "r" * 1025)

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.begin(api, environment, event)

        self.assertEqual([], api.writes)

    def test_wrong_event_repository_is_rejected_before_check_creation(self):
        """A source event cannot move the privileged receiver to another repository."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        event["repository"]["id"] = 101

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.begin(api, environment, event)

        self.assertEqual([], api.writes)

    def test_wrong_workflow_path_is_rejected_before_check_creation(self):
        """Matching display names cannot authorize a different workflow file."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        api.responses["repos/owner/repository/actions/workflows/dependency-review.yml"]["path"] = "other.yml"

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.begin(api, environment, event)

        self.assertEqual([], api.writes)

    def test_wrong_workflow_id_is_rejected_before_check_creation(self):
        """A copied path is insufficient when GitHub associates another workflow id."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        event["workflow_run"]["workflow_id"] = 301

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.begin(api, environment, event)

        self.assertEqual([], api.writes)

    def test_non_pr_source_event_is_rejected(self):
        """A dispatch or push cannot masquerade as the read-only PR capture."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        event["workflow_run"]["event"] = "workflow_dispatch"

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.begin(api, environment, event)

        self.assertEqual([], api.writes)

    def test_newer_attempt_rejects_old_completed_event(self):
        """A late event cannot finalize the result of an earlier attempt."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        api.responses["repos/owner/repository/actions/runs/400"]["run_attempt"] = 3

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.begin(api, environment, event)

        self.assertEqual([], api.writes)

    def test_changed_pr_head_is_rejected(self):
        """The current PR API, not a stale event, determines its head identity."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        api.responses["repos/owner/repository/pulls/59"]["head"]["sha"] = "c" * 40

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.begin(api, environment, event)

        self.assertEqual([], api.writes)

    def test_changed_pr_base_is_rejected(self):
        """A stale pull-request base cannot be submitted under the current main ref."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        api.responses["repos/owner/repository/git/ref/heads/main"]["object"]["sha"] = "c" * 40

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.begin(api, environment, event)

        self.assertEqual([], api.writes)

    def test_fork_repository_identity_is_required(self):
        """Equal commit hashes do not authorize a PR from a different fork identity."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        api.responses["repos/owner/repository/pulls/59"]["head"]["repo"]["id"] = 201

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.begin(api, environment, event)

        self.assertEqual([], api.writes)

    def test_ambiguous_pr_association_is_rejected(self):
        """A head shared by multiple eligible PRs has no implicit privileged target."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        route = "repos/owner/repository/commits/" + "b" * 40 + "/pulls?per_page=100"
        api.responses[route].append(copy.deepcopy(api.responses[route][0]))

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.begin(api, environment, event)

        self.assertEqual([], api.writes)

    def test_same_sha_prs_on_distinct_branches_select_source_branch(self):
        """Two legitimate branches may point to the same commit without making review ambiguous."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        route = "repos/owner/repository/commits/" + "b" * 40 + "/pulls?per_page=100"
        other = copy.deepcopy(api.responses[route][0])
        other["number"] = 60
        other["head"]["ref"] = "feature/other"
        api.responses[route].append(other)

        # Act
        context = helper.begin(api, environment, event)

        # Assert
        self.assertEqual(59, context["pr"])
        self.assertEqual("feature/dependencies", context["head_branch"])
        self.assertEqual(1, len(api.writes))

    def test_same_sha_newer_other_branch_run_does_not_supersede_source(self):
        """Run ordering is scoped to the PR branch rather than every use of its SHA."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        runs = api.responses[workflow_runs_route()]
        other = copy.deepcopy(runs["workflow_runs"][0])
        other["id"] = 401
        other["head_branch"] = "feature/other"
        runs["workflow_runs"].append(other)
        runs["total_count"] = 2

        # Act
        context = helper.begin(api, environment, event)

        # Assert
        self.assertEqual(400, context["source_run_id"])
        self.assertEqual("feature/dependencies", context["head_branch"])
        self.assertEqual(1, len(api.writes))

    def test_source_branch_mismatch_is_rejected(self):
        """Event and authoritative source attempts must name the same branch."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        event["workflow_run"]["head_branch"] = "feature/other"

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.begin(api, environment, event)

        self.assertEqual([], api.writes)

    def test_current_pr_branch_mismatch_is_rejected(self):
        """The fresh PR object must preserve the branch that produced the request."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        api.responses["repos/owner/repository/pulls/59"]["head"]["ref"] = "feature/other"

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.begin(api, environment, event)

        self.assertEqual([], api.writes)

    def test_empty_source_branch_is_rejected(self):
        """An absent branch cannot collapse independent same-SHA requests into one identity."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        api.responses["repos/owner/repository/actions/runs/400/attempts/2"]["head_branch"] = ""

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.begin(api, environment, event)

        self.assertEqual([], api.writes)

    def test_control_in_source_branch_is_rejected(self):
        """Source branch comparison never accepts an embedded control character."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        api.responses["repos/owner/repository/actions/runs/400/attempts/2"]["head_branch"] = "feature/\nforged"

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.begin(api, environment, event)

        self.assertEqual([], api.writes)

    def test_oversized_source_branch_is_rejected(self):
        """The authoritative identity still has a finite comparison and output bound."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        api.responses["repos/owner/repository/actions/runs/400/attempts/2"]["head_branch"] = "r" * 1025

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.begin(api, environment, event)

        self.assertEqual([], api.writes)

    def test_newer_source_run_rejects_old_attempt(self):
        """Independent reruns for the same head are ordered before accepting a verdict."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        runs = api.responses[workflow_runs_route()]
        newer = copy.deepcopy(runs["workflow_runs"][0])
        newer["id"] = 401
        runs["workflow_runs"].append(newer)
        runs["total_count"] = 2

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.begin(api, environment, event)

        self.assertEqual([], api.writes)

    def test_unordered_source_run_inventory_preserves_latest_identity(self):
        """Latest means greatest run id, not whichever record GitHub returns first."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        runs = api.responses[workflow_runs_route()]
        older = copy.deepcopy(runs["workflow_runs"][0])
        older["id"] = 399
        runs["workflow_runs"] = [older, runs["workflow_runs"][0]]
        runs["total_count"] = 2

        # Act
        context = helper.begin(api, environment, event)

        # Assert
        self.assertEqual(400, context["source_run_id"])
        self.assertEqual(1, len(api.writes))

    def test_incomplete_source_run_inventory_is_rejected(self):
        """A partial list cannot omit a newer matching source run."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        api.responses[workflow_runs_route()]["total_count"] = 2

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.begin(api, environment, event)

        self.assertEqual([], api.writes)

    def test_malformed_source_run_inventory_is_rejected(self):
        """An object cannot masquerade as the documented workflow_runs array."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        api.responses[workflow_runs_route()]["workflow_runs"] = {"400": {}}

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.begin(api, environment, event)

        self.assertEqual([], api.writes)

    def test_duplicate_source_run_inventory_is_rejected(self):
        """Repeated records cannot make an incomplete result appear complete."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        runs = api.responses[workflow_runs_route()]
        runs["workflow_runs"].append(copy.deepcopy(runs["workflow_runs"][0]))
        runs["total_count"] = 2

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.begin(api, environment, event)

        self.assertEqual([], api.writes)

    def test_cancelled_capture_has_explicit_non_ready_check(self):
        """Cancellation creates no successful skip for the required PR-head verdict."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        set_conclusion(event, api, "cancelled")

        # Act
        context = helper.begin(api, environment, event)

        # Assert
        self.assertFalse(context["ready"])
        self.assertEqual("completed", api.writes[0][2]["status"])
        self.assertEqual("failure", api.writes[0][2]["conclusion"])

    def test_missing_run_repository_identity_is_rejected(self):
        """Absent source repository metadata is not replaced by artifact assertions."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        del api.responses["repos/owner/repository/actions/runs/400"]["repository"]

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.begin(api, environment, event)

        self.assertEqual([], api.writes)

    def test_ready_current_complete_jobs_publish_success(self):
        """Only the trusted conjunction of every required result can green the head."""
        # Arrange
        helper, environment, event, api, context = started_fixture()

        # Act
        success = helper.finish(api, environment, event, context, success_results(), "success")

        # Assert
        self.assertTrue(success)
        self.assertEqual("PATCH", api.writes[-1][0])
        self.assertEqual("success", api.writes[-1][2]["conclusion"])

    def test_failed_job_cannot_publish_success(self):
        """The review step cannot override a failed snapshot submission."""
        # Arrange
        helper, environment, event, api, context = started_fixture()
        results = success_results()
        results["submit-nuget"] = "failure"

        # Act
        success = helper.finish(api, environment, event, context, results, "success")

        # Assert
        self.assertFalse(success)
        self.assertEqual("failure", api.writes[-1][2]["conclusion"])

    def test_failed_base_preparation_cannot_publish_success(self):
        """The trusted base restore is mandatory independently of the head and publisher."""
        # Arrange
        helper, environment, event, api, context = started_fixture()
        results = success_results()
        results["prepare-base"] = "failure"

        # Act
        success = helper.finish(api, environment, event, context, results, "success")

        # Assert
        self.assertFalse(success)
        self.assertEqual("failure", api.writes[-1][2]["conclusion"])

    def test_failed_head_source_producer_cannot_publish_success(self):
        """A successful base cannot conceal failure to restore the complete head graph."""
        # Arrange
        helper, environment, event, api, context = started_fixture()
        api.responses[source_jobs_route()]["jobs"][1]["conclusion"] = "failure"

        # Act
        success = helper.finish(api, environment, event, context, success_results(), "success")

        # Assert
        self.assertFalse(success)
        self.assertEqual("failure", api.writes[-1][2]["conclusion"])

    def test_cancelled_job_cannot_publish_success(self):
        """Cancellation remains non-success rather than a permissible upstream skip."""
        # Arrange
        helper, environment, event, api, context = started_fixture()
        results = success_results()
        results["submit-nuget"] = "cancelled"

        # Act
        success = helper.finish(api, environment, event, context, results, "success")

        # Assert
        self.assertFalse(success)
        self.assertEqual("failure", api.writes[-1][2]["conclusion"])

    def test_skipped_source_producer_cannot_publish_success(self):
        """Skipped upstream jobs are failures for this required review contract."""
        # Arrange
        helper, environment, event, api, context = started_fixture()
        api.responses[source_jobs_route()]["jobs"][1]["conclusion"] = "skipped"

        # Act
        success = helper.finish(api, environment, event, context, success_results(), "success")

        # Assert
        self.assertFalse(success)
        self.assertEqual("failure", api.writes[-1][2]["conclusion"])

    def test_legacy_combined_preparation_map_cannot_publish_success(self):
        """An obsolete combined result cannot stand in for the two independent restores."""
        # Arrange
        helper, environment, event, api, context = started_fixture()
        results = {"context": "success", "prepare-snapshots": "success", "submit-nuget": "success"}

        # Act
        success = helper.finish(api, environment, event, context, results, "success")

        # Assert
        self.assertFalse(success)
        self.assertEqual("failure", api.writes[-1][2]["conclusion"])

    def test_incomplete_job_result_map_cannot_publish_success(self):
        """A vacuously true empty conjunction cannot pass the required check."""
        # Arrange
        helper, environment, event, api, context = started_fixture()

        # Act
        success = helper.finish(api, environment, event, context, {}, "success")

        # Assert
        self.assertFalse(success)
        self.assertEqual("failure", api.writes[-1][2]["conclusion"])

    def test_failed_review_outcome_cannot_publish_success(self):
        """The Action and both explicit guards remain mandatory independent outcomes."""
        # Arrange
        helper, environment, event, api, context = started_fixture()

        # Act
        success = helper.finish(api, environment, event, context, success_results(), "failure")

        # Assert
        self.assertFalse(success)
        self.assertEqual("failure", api.writes[-1][2]["conclusion"])

    def test_changed_base_at_finish_cannot_publish_success(self):
        """A base advance during restore invalidates a previously valid context."""
        # Arrange
        helper, environment, event, api, context = started_fixture()
        api.responses["repos/owner/repository/git/ref/heads/main"]["object"]["sha"] = "c" * 40

        # Act
        success = helper.finish(api, environment, event, context, success_results(), "success")

        # Assert
        self.assertFalse(success)
        self.assertEqual("failure", api.writes[-1][2]["conclusion"])

    def test_failed_capture_cannot_publish_success(self):
        """Even fabricated success results cannot turn a failed source run green."""
        # Arrange
        helper, environment, event, api, context = started_fixture(conclusion="failure")

        # Act
        success = helper.finish(api, environment, event, context, success_results(), "success")

        # Assert
        self.assertFalse(success)
        self.assertEqual("failure", api.writes[-1][2]["conclusion"])

    def test_check_on_another_head_cannot_be_updated(self):
        """A changed context cannot patch a different PR or check run."""
        # Arrange
        helper, environment, event, api, context = started_fixture()
        api.responses["repos/owner/repository/check-runs/700"]["head_sha"] = "c" * 40
        writes_before = len(api.writes)

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.finish(api, environment, event, context, success_results(), "success")

        self.assertEqual(writes_before, len(api.writes))

    def test_check_from_another_application_cannot_be_updated(self):
        """A foreign application's identically named check is not owned by this handoff."""
        # Arrange
        helper, environment, event, api, context = started_fixture()
        api.responses["repos/owner/repository/check-runs/700"]["app"]["slug"] = "other-app"
        writes_before = len(api.writes)

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.finish(api, environment, event, context, success_results(), "success")

        self.assertEqual(writes_before, len(api.writes))

    def test_check_from_another_source_attempt_cannot_be_updated(self):
        """The external id binds otherwise identical check names to their source attempt."""
        # Arrange
        helper, environment, event, api, context = started_fixture()
        api.responses["repos/owner/repository/check-runs/700"]["external_id"] = "dependency-review:400:1:900:1"
        writes_before = len(api.writes)

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.finish(api, environment, event, context, success_results(), "success")

        self.assertEqual(writes_before, len(api.writes))

    def test_unconfirmed_receiving_attempt_cannot_update_check(self):
        """An environment attempt must agree with GitHub's current receiver run."""
        # Arrange
        helper, environment, event, api, context = started_fixture()
        environment["GITHUB_RUN_ATTEMPT"] = "2"
        writes_before = len(api.writes)

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.finish(api, environment, event, context, success_results(), "success")

        self.assertEqual(writes_before, len(api.writes))

    def test_partial_receiver_rerun_reuses_original_owned_check(self):
        """Re-run failed jobs may reuse context from an earlier successful context job."""
        # Arrange
        helper, environment, event, api, context = started_fixture()
        environment["GITHUB_RUN_ATTEMPT"] = "2"
        api.responses["repos/owner/repository/actions/runs/900"]["run_attempt"] = 2

        # Act
        success = helper.finish(api, environment, event, context, success_results(), "success")

        # Assert
        self.assertTrue(success)
        self.assertEqual(1, context["receiver_attempt"])
        self.assertEqual("dependency-review:400:2:900:1", helper.external_id(context))
        self.assertEqual("repos/owner/repository/check-runs/700", api.writes[-1][1])
        self.assertEqual("success", api.writes[-1][2]["conclusion"])

    def test_stale_older_receiver_attempt_cannot_update_check(self):
        """A late old receiver process cannot overwrite its newer attempt's verdict."""
        # Arrange
        helper, environment, event, api, context = started_fixture()
        api.responses["repos/owner/repository/actions/runs/900"]["run_attempt"] = 2
        writes_before = len(api.writes)

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.finish(api, environment, event, context, success_results(), "success")

        self.assertEqual(writes_before, len(api.writes))

    def test_receiver_context_from_future_attempt_cannot_update_check(self):
        """Context provenance cannot claim an attempt newer than the executing process."""
        # Arrange
        helper, environment, event, api, context = started_fixture()
        context["receiver_attempt"] = 2
        writes_before = len(api.writes)

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.finish(api, environment, event, context, success_results(), "success")

        self.assertEqual(writes_before, len(api.writes))

    def test_receiver_workflow_identity_cannot_be_substituted(self):
        """An identically numbered run from another workflow cannot authorize a patch."""
        # Arrange
        helper, environment, event, api, context = started_fixture()
        api.responses["repos/owner/repository/actions/runs/900"]["workflow_id"] = 999
        writes_before = len(api.writes)

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.finish(api, environment, event, context, success_results(), "success")

        self.assertEqual(writes_before, len(api.writes))

    def test_receiver_repository_identity_cannot_be_substituted(self):
        """Receiver attempt validation stays scoped to the authoritative repository id."""
        # Arrange
        helper, environment, event, api, context = started_fixture()
        api.responses["repos/owner/repository/actions/runs/900"]["repository"]["id"] = 101
        writes_before = len(api.writes)

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.finish(api, environment, event, context, success_results(), "success")

        self.assertEqual(writes_before, len(api.writes))

    def test_partial_rerun_cannot_accept_newer_source_attempt(self):
        """Receiver retries do not relax the original source-run attempt contract."""
        # Arrange
        helper, environment, event, api, context = started_fixture()
        environment["GITHUB_RUN_ATTEMPT"] = "2"
        api.responses["repos/owner/repository/actions/runs/900"]["run_attempt"] = 2
        api.responses["repos/owner/repository/actions/runs/400"]["run_attempt"] = 3

        # Act
        success = helper.finish(api, environment, event, context, success_results(), "success")

        # Assert
        self.assertFalse(success)
        self.assertEqual("failure", api.writes[-1][2]["conclusion"])

    def test_begin_cli_writes_only_validated_scalar_outputs(self):
        """The workflow receives canonical SHAs and refs, not raw event text."""
        # Arrange
        helper = load_helper()
        environment, event, api = fixture()
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            event_path, output_path, context_path = (directory / name for name in ("event", "output", "context"))
            event_path.write_text(json.dumps(event), encoding="ascii")
            environment.update({"GITHUB_EVENT_PATH": str(event_path), "GITHUB_OUTPUT": str(output_path)})
            arguments = [str(HELPER), "begin", "--context", str(context_path)]
            with mock.patch.dict(os.environ, environment, clear=True), mock.patch.object(sys, "argv", arguments), \
                    mock.patch.object(helper, "GitHub", return_value=api):
                # Act
                result = helper.main()

            # Assert
            self.assertEqual(0, result)
            self.assertEqual("b" * 40, json.loads(context_path.read_text())["head_sha"])
            self.assertEqual({
                "base_sha=" + "a" * 40, "head_sha=" + "b" * 40,
                "base_ref=refs/heads/main", "head_ref=refs/pull/59/head",
            "pr=59", "check_id=700", "ready=true", "source_run_id=400",
            "base_artifact_id=801", "head_artifact_id=802",
            }, set(output_path.read_text().splitlines()))

    def test_invalid_event_cli_has_no_output_or_check_mutation(self):
        """Malformed input fails closed without forwarding its payload to logs."""
        # Arrange
        helper = load_helper()
        environment, _, api = fixture()
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            event_path, output_path, context_path = (directory / name for name in ("event", "output", "context"))
            event_path.write_text('{"action":"completed","action":"private-payload"}', encoding="ascii")
            environment.update({"GITHUB_EVENT_PATH": str(event_path), "GITHUB_OUTPUT": str(output_path)})
            errors = io.StringIO()
            arguments = [str(HELPER), "begin", "--context", str(context_path)]
            with mock.patch.dict(os.environ, environment, clear=True), mock.patch.object(sys, "argv", arguments), \
                    mock.patch.object(helper, "GitHub", return_value=api), mock.patch.object(sys, "stderr", errors):
                # Act
                result = helper.main()

            # Assert
            self.assertEqual(1, result)
            self.assertEqual([], api.writes)
            self.assertFalse(output_path.exists())
            self.assertFalse(context_path.exists())
            self.assertNotIn("private-payload", errors.getvalue())

    def test_duplicate_context_json_is_rejected(self):
        """Duplicate JSON keys cannot let validator and consumer select different identities."""
        # Arrange
        helper = load_helper()
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "context.json"
            path.write_text('{"check_id":700,"check_id":701}', encoding="ascii")

            # Act / Assert
            with self.assertRaises(ValueError):
                helper.read_json(path)

    def test_removed_head_command_is_rejected_before_api_use(self):
        """The trusted receiver cannot accidentally consume the retired PR artifact protocol."""
        # Arrange
        helper = load_helper()
        arguments = [str(HELPER), "head", "--context", "unused-context.json"]
        errors = io.StringIO()
        with mock.patch.object(sys, "argv", arguments), mock.patch.object(sys, "stderr", errors), \
                mock.patch.object(helper, "GitHub") as api:
            # Act / Assert
            with self.assertRaises(SystemExit) as failure:
                helper.main()

        self.assertEqual(2, failure.exception.code)
        api.assert_not_called()

    def test_removed_archive_argument_is_rejected_before_api_use(self):
        """No dormant archive option remains on the begin/finish context helper."""
        # Arrange
        helper = load_helper()
        arguments = [str(HELPER), "begin", "--context", "unused-context.json", "--archive", "unused.zip"]
        errors = io.StringIO()
        with mock.patch.object(sys, "argv", arguments), mock.patch.object(sys, "stderr", errors), \
                mock.patch.object(helper, "GitHub") as api:
            # Act / Assert
            with self.assertRaises(SystemExit) as failure:
                helper.main()

        self.assertEqual(2, failure.exception.code)
        api.assert_not_called()

    def test_finish_cli_preserves_exact_success_conjunction(self):
        """The simplified command surface still completes only the receiver-owned head check."""
        # Arrange
        helper, environment, event, api, context = started_fixture()
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            event_path, context_path = (directory / name for name in ("event", "context"))
            event_path.write_text(json.dumps(event), encoding="ascii")
            context_path.write_text(json.dumps(context), encoding="ascii")
            environment.update({"GITHUB_EVENT_PATH": str(event_path),
                                "RESULTS_JSON": json.dumps(success_results()), "REVIEW_OUTCOME": "success"})
            arguments = [str(HELPER), "finish", "--context", str(context_path)]
            with mock.patch.dict(os.environ, environment, clear=True), mock.patch.object(sys, "argv", arguments), \
                    mock.patch.object(helper, "GitHub", return_value=api):
                # Act
                result = helper.main()

            # Assert
            self.assertEqual(0, result)
            self.assertEqual("PATCH", api.writes[-1][0])
            self.assertEqual("success", api.writes[-1][2]["conclusion"])


class ManualHandoffTests(unittest.TestCase):
    """Bind the existing CI dispatch to the same required PR-head review contract."""

    def test_current_reviewed_branch_uses_shared_source_and_receiver_contract(self):
        """Startup uses existing CI registration, never the not-yet-registered receiver file."""
        # Arrange
        helper = load_helper()
        environment, event, api = manual_fixture()
        del api.responses["repos/owner/repository/actions/workflows/dependency-review-results.yml"]

        # Act
        context = helper.begin(api, environment, event)

        # Assert
        self.assertEqual(59, context["pr"])
        self.assertEqual("workflow_dispatch", context["receiver_event"])
        self.assertEqual(".github/workflows/ci.yml", context["receiver_workflow_path"])
        self.assertEqual(400, context["source_run_id"])
        self.assertTrue(context["ready"])
        self.assertEqual("b" * 40, api.writes[0][2]["head_sha"])

    def test_manual_input_and_ref_guards_reject_before_check_creation(self):
        """Forwarded inputs cannot select a different PR, executable revision or ref."""
        for field, value in (
                ("DEPENDENCY_REVIEW_PR", ""), ("DEPENDENCY_REVIEW_PR", "60"),
                ("DEPENDENCY_REVIEW_PR", "059"), ("DEPENDENCY_REVIEW_PR", "0"),
                ("GITHUB_EVENT_NAME", "push"), ("GITHUB_SHA", "c" * 40),
                ("GITHUB_WORKFLOW_SHA", "c" * 40), ("GITHUB_WORKFLOW_SHA", ""),
                ("GITHUB_REF", "refs/heads/main"), ("GITHUB_REF", "refs/heads/other"),
                ("GITHUB_REF", "refs/tags/release"), ("GITHUB_REF_TYPE", "tag"),
                ("GITHUB_WORKFLOW_REF", "owner/repository/.github/workflows/other.yml"
                 "@refs/heads/feature/dependencies")):
            with self.subTest(field=field, value=value):
                # Arrange
                helper = load_helper()
                environment, event, api = manual_fixture()
                environment[field] = value

                # Act / Assert
                with self.assertRaises(ValueError):
                    helper.begin(api, environment, event)

                self.assertEqual([], api.writes)

    def test_manual_event_input_is_required_and_bounded(self):
        """Dispatch metadata and the reusable-workflow argument must independently agree."""
        for value in (None, "", "0", "059", "60", True, "9" * 19, "59\nprivate-payload"):
            with self.subTest(value=value):
                # Arrange
                helper = load_helper()
                environment, event, api = manual_fixture()
                event["inputs"]["dependency-review-pr"] = value

                # Act / Assert
                with self.assertRaises(ValueError):
                    helper.begin(api, environment, event)

                self.assertEqual([], api.writes)

    def test_manual_pr_and_repository_guards_reject_before_check_creation(self):
        """Startup cannot execute fork code, a closed PR or a non-main destination."""
        for field, value in (("number", 60), ("state", "closed"), ("head_repo", 200),
                             ("head_ref", "main"), ("base_ref", "release"), ("base_repo", 101),
                             ("event_repo", 101)):
            with self.subTest(field=field):
                # Arrange
                helper = load_helper()
                environment, event, api = manual_fixture()
                pr = api.responses["repos/owner/repository/pulls/59"]
                if field in {"number", "state"}:
                    pr[field] = value
                elif field == "event_repo":
                    event["repository"]["id"] = value
                elif field.endswith("_repo"):
                    pr[field.split("_")[0]]["repo"]["id"] = value
                else:
                    pr[field.split("_")[0]]["ref"] = value

                # Act / Assert
                with self.assertRaises(ValueError):
                    helper.begin(api, environment, event)

                self.assertEqual([], api.writes)

    def test_receiver_identity_is_validated_before_initial_check_post_in_both_modes(self):
        """A copied CI or receiver identity cannot even create an in-progress check."""
        for make_fixture in (fixture, manual_fixture):
            for field, value in (("id", 901), ("run_attempt", 2), ("workflow_id", 999),
                                 ("event", "push"), ("path", ".github/workflows/other.yml"),
                                 ("repository", {"id": 101}), ("status", "private-payload")):
                with self.subTest(mode=make_fixture.__name__, field=field):
                    # Arrange
                    helper = load_helper()
                    environment, event, api = make_fixture()
                    api.responses["repos/owner/repository/actions/runs/900"][field] = value

                    # Act / Assert
                    with self.assertRaises(ValueError):
                        helper.begin(api, environment, event)

                    self.assertEqual([], api.writes)

    def test_manual_receiver_head_and_registration_are_authoritative(self):
        """Even matching environment values need the existing CI run and workflow API proof."""
        for field, value in (("head_sha", "c" * 40), ("head_branch", "main"),
                             ("head_repository", {"id": 200}), ("workflow_path", "other.yml"),
                             ("workflow_state", "disabled_manually")):
            with self.subTest(field=field):
                # Arrange
                helper = load_helper()
                environment, event, api = manual_fixture()
                if field.startswith("workflow_"):
                    api.responses["repos/owner/repository/actions/workflows/ci.yml"][field[9:]] = value
                else:
                    api.responses["repos/owner/repository/actions/runs/900"][field] = value

                # Act / Assert
                with self.assertRaises(ValueError):
                    helper.begin(api, environment, event)

                self.assertEqual([], api.writes)

    def test_manual_latest_failed_or_cancelled_source_cannot_fall_back_to_old_success(self):
        """Discovery chooses the newest request even when an older result passed."""
        for conclusion in ("failure", "cancelled"):
            with self.subTest(conclusion=conclusion):
                # Arrange
                helper = load_helper()
                environment, event, api = manual_fixture()
                add_newer_source(api, conclusion=conclusion)

                # Act
                context = helper.begin(api, environment, event)

                # Assert
                self.assertEqual(401, context["source_run_id"])
                self.assertFalse(context["ready"])
                self.assertEqual(1, len(api.writes))

    def test_manual_pending_latest_source_rejects_older_completed_request(self):
        """A queued or executing replacement request cannot borrow its predecessor's verdict."""
        for status in ("queued", "in_progress"):
            with self.subTest(status=status):
                # Arrange
                helper = load_helper()
                environment, event, api = manual_fixture()
                add_newer_source(api, status=status, conclusion=None)

                # Act / Assert
                with self.assertRaises(ValueError):
                    helper.begin(api, environment, event)

                self.assertEqual([], api.writes)

    def test_manual_source_uses_common_attempt_association_and_inventory_guards(self):
        """Internal normalization does not bypass the ordinary source/PR trust boundary."""
        for change in ("attempt", "source_path", "source_repo", "association", "input_pr", "inventory"):
            with self.subTest(change=change):
                # Arrange
                helper = load_helper()
                environment, event, api = manual_fixture()
                if change == "attempt":
                    api.responses["repos/owner/repository/actions/runs/400/attempts/2"]["run_attempt"] = 1
                elif change in {"source_path", "source_repo"}:
                    field, value = (("path", "other.yml") if change == "source_path"
                                    else ("repository", {"id": 101}))
                    api.responses["repos/owner/repository/actions/runs/400"][field] = value
                elif change in {"association", "input_pr"}:
                    route = "repos/owner/repository/commits/" + "b" * 40 + "/pulls?per_page=100"
                    api.responses[route] = copy.deepcopy(api.responses[route])
                    pr = api.responses[route][0]
                    if change == "association":
                        pr["state"] = "closed"
                    else:
                        pr["number"] = 60
                        api.responses["repos/owner/repository/pulls/60"] = copy.deepcopy(pr)
                else:
                    api.responses[workflow_runs_route()]["total_count"] = 100

                # Act / Assert
                with self.assertRaises(ValueError):
                    helper.begin(api, environment, event)

                self.assertEqual([], api.writes)

    def test_manual_current_complete_jobs_publish_success(self):
        """The startup receiver completes exactly the same independently gated check."""
        # Arrange
        helper, environment, event, api, context = started_manual_fixture()

        # Act
        success = helper.finish(api, environment, event, context, success_results(), "success")

        # Assert
        self.assertTrue(success)
        self.assertEqual("success", api.writes[-1][2]["conclusion"])

    def test_manual_unsuccessful_jobs_or_guards_cannot_publish_success(self):
        """Startup is not an alternate way to bypass any snapshot or review outcome."""
        for result_name in (*success_results(), "review"):
            for outcome in ("failure", "cancelled", "skipped"):
                with self.subTest(result_name=result_name, outcome=outcome):
                    # Arrange
                    helper, environment, event, api, context = started_manual_fixture()
                    results, review_outcome = success_results(), "success"
                    if result_name == "review":
                        review_outcome = outcome
                    else:
                        results[result_name] = outcome

                    # Act
                    success = helper.finish(api, environment, event, context, results, review_outcome)

                    # Assert
                    self.assertFalse(success)
                    self.assertEqual("failure", api.writes[-1][2]["conclusion"])

    def test_manual_source_is_rediscovered_at_finish_without_success_fallback(self):
        """A newer failed or pending source cannot be hidden by stored startup context."""
        for status, conclusion in (("completed", "failure"), ("completed", "cancelled"),
                                   ("in_progress", None)):
            with self.subTest(status=status, conclusion=conclusion):
                # Arrange
                helper, environment, event, api, context = started_manual_fixture()
                add_newer_source(api, status=status, conclusion=conclusion)

                # Act
                success = helper.finish(api, environment, event, context, success_results(), "success")

                # Assert
                self.assertFalse(success)
                self.assertEqual("failure", api.writes[-1][2]["conclusion"])

    def test_manual_stale_executing_receiver_cannot_update_check(self):
        """A previously valid startup receiver loses write authority when a newer attempt begins."""
        # Arrange
        helper, environment, event, api, context = started_manual_fixture()
        api.responses["repos/owner/repository/actions/runs/900"]["run_attempt"] = 2

        # Act / Assert
        with self.assertRaises(ValueError):
            helper.finish(api, environment, event, context, success_results(), "success")

        self.assertEqual(1, len(api.writes))

    def test_manual_stale_head_base_source_or_workflow_cannot_publish_success(self):
        """The current PR, source attempt and workflow executable are reread at completion."""
        for change in ("head", "base", "source_attempt", "source_run", "workflow_sha", "closed"):
            with self.subTest(change=change):
                # Arrange
                helper, environment, event, api, context = started_manual_fixture()
                if change == "head":
                    api.responses["repos/owner/repository/pulls/59"]["head"]["sha"] = "c" * 40
                elif change == "base":
                    api.responses["repos/owner/repository/git/ref/heads/main"]["object"]["sha"] = "c" * 40
                elif change == "source_attempt":
                    current = api.responses["repos/owner/repository/actions/runs/400"]
                    current["run_attempt"] = 3
                    api.responses["repos/owner/repository/actions/runs/400/attempts/3"] = copy.deepcopy(current)
                elif change == "source_run":
                    add_newer_source(api)
                elif change == "workflow_sha":
                    environment["GITHUB_WORKFLOW_SHA"] = "c" * 40
                else:
                    api.responses["repos/owner/repository/pulls/59"]["state"] = "closed"

                # Act / Assert
                if change == "workflow_sha":
                    with self.assertRaises(ValueError):
                        helper.finish(api, environment, event, context, success_results(), "success")

                    self.assertEqual(1, len(api.writes))
                else:
                    success = helper.finish(api, environment, event, context, success_results(), "success")

                    self.assertFalse(success)
                    self.assertEqual("failure", api.writes[-1][2]["conclusion"])

    def test_manual_partial_receiver_rerun_reuses_original_check(self):
        """Only the receiver attempt may advance without changing the review's source context."""
        # Arrange
        helper, environment, event, api, context = started_manual_fixture()
        environment["GITHUB_RUN_ATTEMPT"] = "2"
        api.responses["repos/owner/repository/actions/runs/900"]["run_attempt"] = 2

        # Act
        success = helper.finish(api, environment, event, context, success_results(), "success")

        # Assert
        self.assertTrue(success)
        self.assertEqual("dependency-review:400:2:900:1", helper.external_id(context))
        self.assertEqual("success", api.writes[-1][2]["conclusion"])

    def test_context_cannot_cross_automatic_and_manual_receivers(self):
        """The context file cannot choose a privileged receiver mode for the executing process."""
        for make_fixture, other_fixture in ((started_fixture, manual_fixture), (started_manual_fixture, fixture)):
            with self.subTest(mode=make_fixture.__name__):
                # Arrange
                helper, _, _, api, context = make_fixture()
                environment, event, other_api = other_fixture()
                api.responses.update(other_api.responses)

                # Act / Assert
                with self.assertRaises(ValueError):
                    helper.finish(api, environment, event, context, success_results(), "success")

                self.assertEqual(1, len(api.writes))

    def test_manual_cli_uses_same_canonical_outputs_without_event_file_rewrite(self):
        """The reusable startup is not a second command, script or file-based protocol."""
        # Arrange
        helper = load_helper()
        environment, event, api = manual_fixture()
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            event_path, output_path, context_path = (directory / name for name in ("event", "output", "context"))
            original = json.dumps(event)
            event_path.write_text(original, encoding="ascii")
            environment.update({"GITHUB_EVENT_PATH": str(event_path), "GITHUB_OUTPUT": str(output_path)})
            arguments = [str(HELPER), "begin", "--context", str(context_path)]
            with mock.patch.dict(os.environ, environment, clear=True), mock.patch.object(sys, "argv", arguments), \
                    mock.patch.object(helper, "GitHub", return_value=api):
                # Act
                result = helper.main()

            # Assert
            self.assertEqual(0, result)
            self.assertEqual(original, event_path.read_text())
            self.assertEqual("b" * 40, json.loads(context_path.read_text())["head_sha"])
            self.assertIn("pr=59", output_path.read_text().splitlines())

    def test_manual_cli_rejects_input_payload_without_logging_or_check_creation(self):
        """The new entry point inherits bounded, payload-free validation failure output."""
        # Arrange
        helper = load_helper()
        environment, event, api = manual_fixture()
        event["inputs"]["dependency-review-pr"] = "59\nprivate-payload"
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            event_path, output_path, context_path = (directory / name for name in ("event", "output", "context"))
            event_path.write_text(json.dumps(event), encoding="ascii")
            environment.update({"GITHUB_EVENT_PATH": str(event_path), "GITHUB_OUTPUT": str(output_path)})
            arguments = [str(HELPER), "begin", "--context", str(context_path)]
            errors = io.StringIO()
            with mock.patch.dict(os.environ, environment, clear=True), mock.patch.object(sys, "argv", arguments), \
                    mock.patch.object(helper, "GitHub", return_value=api), mock.patch.object(sys, "stderr", errors):
                # Act
                result = helper.main()

            # Assert
            self.assertEqual(1, result)
            self.assertNotIn("private-payload", errors.getvalue())
            self.assertFalse(context_path.exists())
            self.assertFalse(output_path.exists())
            self.assertEqual([], api.writes)


def fixture():
    """Return a complete authoritative fork fixture with no event PR association."""
    environment = {
        "GITHUB_REPOSITORY": "owner/repository",
        "GITHUB_REPOSITORY_ID": "100",
        "GITHUB_RUN_ID": "900",
        "GITHUB_RUN_ATTEMPT": "1",
        "GITHUB_EVENT_NAME": "workflow_run",
    }
    repository = {"id": 100, "full_name": "owner/repository"}
    fork = {"id": 200, "full_name": "contributor/repository"}
    run = {
        "id": 400, "run_attempt": 2, "workflow_id": 300,
        "path": ".github/workflows/dependency-review.yml",
        "event": "pull_request", "status": "completed", "conclusion": "success",
        "head_sha": "b" * 40, "head_branch": "feature/dependencies",
        "head_repository": fork, "repository": repository,
        "run_started_at": "2026-10-21T12:00:00Z",
    }
    pr = {
        "number": 59, "state": "open",
        "user": {"login": "contributor", "type": "User"},
        "head": {"sha": "b" * 40, "ref": "feature/dependencies", "repo": fork},
        "base": {"sha": "a" * 40, "ref": "main", "repo": repository},
    }
    event = {"action": "completed", "repository": repository, "workflow_run": copy.deepcopy(run)}
    responses = {
        "repos/owner/repository/actions/workflows/dependency-review-results.yml": {
            "id": 301, "path": ".github/workflows/dependency-review-results.yml", "state": "active"},
        "repos/owner/repository/actions/runs/900": {
            "id": 900, "run_attempt": 1, "workflow_id": 301, "event": "workflow_run",
            "path": ".github/workflows/dependency-review-results.yml", "repository": copy.deepcopy(repository),
            "status": "in_progress"},
        "repos/owner/repository/actions/workflows/dependency-review.yml": {
            "id": 300, "path": ".github/workflows/dependency-review.yml", "state": "active"},
        "repos/owner/repository/actions/runs/400": run,
        "repos/owner/repository/actions/runs/400/attempts/2": copy.deepcopy(run),
        "repos/owner/repository/commits/" + "b" * 40 + "/pulls?per_page=100": [pr],
        "repos/owner/repository/pulls/59": pr,
        "repos/owner/repository/git/ref/heads/main": {"object": {"sha": "a" * 40}},
        "repos/owner/repository/actions/workflows/300/runs?event=pull_request&head_sha="
        + "b" * 40 + "&per_page=100": {"total_count": 1, "workflow_runs": [run]},
    }

    policy = {
        "schemaVersion": 1, "revision": "2026-10-25.1", "repository": "owner/repository",
        "authorityRef": "refs/heads/main", "lines": [
            {"sourceRef": "refs/heads/main", "role": "main", "packageMajor": 10,
             "channel": "stable-and-prerelease", "upstreamStage": "ga", "state": "active",
             "workflowPath": ".github/workflows/release-candidate.yml", "publisherEnvironment": "nuget"},
            {"sourceRef": "refs/heads/maintenance/10.x", "role": "maintenance", "packageMajor": 10,
             "channel": "stable-and-prerelease", "upstreamStage": "ga", "state": "inactive",
             "workflowPath": ".github/workflows/release-candidate.yml", "publisherEnvironment": "nuget"},
        ],
    }
    raw = json.dumps(policy).encode("ascii")
    responses["repos/owner/repository/branches/main"] = {
        "name": "main", "protected": True, "commit": {"sha": "d" * 40}}
    responses["repos/owner/repository/contents/eng/release/lines-policy.json?ref=" + "d" * 40] = {
        "type": "file", "path": "eng/release/lines-policy.json", "encoding": "base64",
        "content": base64.b64encode(raw).decode("ascii"),
        "sha": hashlib.sha1(b"blob " + str(len(raw)).encode("ascii") + b"\0" + raw).hexdigest(),
    }

    api = FakeApi(responses)
    add_source_input_evidence(api)

    return environment, event, api


def source_artifacts_route():
    """Read every source-run artifact before selecting either immutable producer ID."""
    return "repos/owner/repository/actions/runs/400/artifacts?per_page=100"


def source_jobs_route():
    """Read complete source history so retained successful producers survive partial reruns."""
    return "repos/owner/repository/actions/runs/400/jobs?filter=all&per_page=100"


def add_source_input_evidence(api):
    """Build actual API-shaped independent producer and immutable-artifact evidence."""
    source = api.responses["repos/owner/repository/actions/runs/400"]
    names = ("prepare-base / restore", "prepare-head / restore", "request-dependency-review")
    jobs = [{"id": 810 + index, "name": name, "run_id": 400, "run_attempt": 2,
             "head_sha": source["head_sha"], "status": "completed", "conclusion": "success"}
            for index, name in enumerate(names)]
    artifacts = [{"id": 801 + index, "name": f"dependency-inputs-{revision}-400-2",
                  "expired": False, "expires_at": "2099-01-01T00:00:00Z", "size_in_bytes": 1024,
                  "digest": "sha256:" + "e" * 64,
                  "workflow_run": {"id": 400, "repository_id": 100,
                                   "head_repository_id": source["head_repository"]["id"],
                                   "head_branch": source["head_branch"], "head_sha": source["head_sha"]}}
                 for index, revision in enumerate(("base", "head"))]
    api.responses[source_jobs_route()] = {"total_count": len(jobs), "jobs": jobs}
    api.responses[source_artifacts_route()] = {"total_count": len(artifacts), "artifacts": artifacts}


def set_base(api, branch):
    """Point shared authoritative PR fixtures at one exact registered base."""
    api.responses["repos/owner/repository/pulls/59"]["base"]["ref"] = branch
    api.responses["repos/owner/repository/git/ref/heads/" + branch.replace("/", "%2F")] = {
        "object": {"sha": "a" * 40}}


def legacy_fixture(helper, make_fixture=fixture):
    """Represent authenticated pre-policy main without consulting candidate source."""
    environment, event, api = make_fixture()
    api.responses["repos/owner/repository/branches/main"]["commit"]["sha"] = "a" * 40
    route = "repos/owner/repository/contents/eng/release/lines-policy.json?ref=" + "a" * 40
    api.responses[route] = helper.PolicyNotFound(route)

    return environment, event, api


def manual_fixture():
    """Use a reviewed same-repository branch dispatched through the existing CI workflow."""
    environment, _, api = fixture()
    repository = {"id": 100, "full_name": "owner/repository"}
    environment.update({
        "GITHUB_EVENT_NAME": "workflow_dispatch", "DEPENDENCY_REVIEW_PR": "59",
        "GITHUB_SHA": "b" * 40, "GITHUB_WORKFLOW_SHA": "b" * 40,
        "GITHUB_REF": "refs/heads/feature/dependencies", "GITHUB_REF_TYPE": "branch",
        "GITHUB_WORKFLOW_REF": "owner/repository/.github/workflows/ci.yml@refs/heads/feature/dependencies",
    })
    event = {"repository": copy.deepcopy(repository), "inputs": {"dependency-review-pr": "59"}}
    for route in ("repos/owner/repository/actions/runs/400",
                  "repos/owner/repository/actions/runs/400/attempts/2"):
        api.responses[route]["head_repository"] = copy.deepcopy(repository)

    for route in ("repos/owner/repository/pulls/59",
                  "repos/owner/repository/commits/" + "b" * 40 + "/pulls?per_page=100"):
        pr = api.responses[route] if isinstance(api.responses[route], dict) else api.responses[route][0]
        pr["head"]["repo"] = copy.deepcopy(repository)

    api.responses["repos/owner/repository/actions/workflows/ci.yml"] = {
        "id": 302, "path": ".github/workflows/ci.yml", "state": "active"}
    api.responses["repos/owner/repository/actions/runs/900"] = {
        "id": 900, "run_attempt": 1, "workflow_id": 302, "event": "workflow_dispatch",
        "path": ".github/workflows/ci.yml", "repository": copy.deepcopy(repository),
        "head_repository": copy.deepcopy(repository), "head_sha": "b" * 40,
        "head_branch": "feature/dependencies", "status": "in_progress",
    }
    add_source_input_evidence(api)

    return environment, event, api


def started_manual_fixture():
    """Create one startup-owned check before testing completion or an adversarial mutation."""
    helper = load_helper()
    environment, event, api = manual_fixture()
    context = helper.begin(api, environment, event)
    api.responses["repos/owner/repository/check-runs/700"] = {
        "name": "dependency-review", "head_sha": context["head_sha"],
        "external_id": helper.external_id(context), "app": {"slug": "github-actions"},
    }

    return helper, environment, event, api, context


def add_newer_source(api, status="completed", conclusion="success"):
    """Append a newer same-head request while retaining the older successful record."""
    current = copy.deepcopy(api.responses["repos/owner/repository/actions/runs/400"])
    current.update({"id": 401, "status": status, "conclusion": conclusion})
    api.responses["repos/owner/repository/actions/runs/401"] = current
    api.responses["repos/owner/repository/actions/runs/401/attempts/2"] = copy.deepcopy(current)
    inventory = api.responses[workflow_runs_route()]
    inventory["workflow_runs"].append(current)
    inventory["total_count"] += 1


def workflow_runs_route():
    """Return the fixed source-head run lookup used by fixtures."""
    return ("repos/owner/repository/actions/workflows/300/runs?event=pull_request&head_sha="
            + "b" * 40 + "&per_page=100")


def success_results():
    """Return all mandatory upstream job results, never a permissive subset."""
    return {"context": "success", "submit-nuget": "success"}


def set_conclusion(event, api, conclusion):
    """Keep event/current/attempt facts aligned for a non-success source result."""
    event["workflow_run"]["conclusion"] = conclusion
    api.responses["repos/owner/repository/actions/runs/400"]["conclusion"] = conclusion
    api.responses["repos/owner/repository/actions/runs/400/attempts/2"]["conclusion"] = conclusion


def set_run_paths(event, api, path):
    """Change only the three run objects; the workflow API keeps its exact bare path."""
    event["workflow_run"]["path"] = path
    api.responses["repos/owner/repository/actions/runs/400"]["path"] = path
    api.responses["repos/owner/repository/actions/runs/400/attempts/2"]["path"] = path


def started_fixture(conclusion="success"):
    """Create one scoped check as fixture setup, before the Act under test."""
    helper = load_helper()
    environment, event, api = fixture()
    set_conclusion(event, api, conclusion)
    context = helper.begin(api, environment, event)
    api.responses["repos/owner/repository/check-runs/700"] = {
        "name": "dependency-review", "head_sha": context["head_sha"],
        "external_id": helper.external_id(context), "app": {"slug": "github-actions"},
    }

    return helper, environment, event, api, context


class FakeApi:
    """Record writes and serve authoritative API facts without external effects."""

    def __init__(self, responses):
        self.responses = responses
        self.writes = []

    def request(self, route, method="GET", payload=None):
        if method != "GET":
            self.writes.append((method, route, copy.deepcopy(payload)))

            return {"id": 700}

        value = self.responses[route]
        if isinstance(value, Exception):
            raise value

        return copy.deepcopy(value)


if __name__ == "__main__":
    unittest.main()
