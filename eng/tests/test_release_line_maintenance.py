"""Behavioral checks for branch-native release-line maintenance without hosted writes."""

import copy
import base64
import hashlib
import json
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from eng.release import dependencies, lines, maintenance


ROOT = Path(__file__).resolve().parents[2]


def policy_fixture():
    """Return a synthetic future-main/maintained-10 pair without changing source."""
    policy = lines.load_policy()
    policy["repository"] = "owner/repository"
    policy["lines"][0]["packageMajor"] = 11
    policy["lines"][1]["state"] = "active"

    return policy


def hosted_environment(workflow="ci.yml", ref="refs/heads/maintenance/10.x"):
    """Build the native child identity rather than a merely matching user input."""
    return {"GITHUB_EVENT_NAME": "workflow_dispatch", "GITHUB_REPOSITORY": "owner/repository",
            "GITHUB_REF": ref, "GITHUB_SHA": "a" * 40,
            "GITHUB_WORKFLOW_REF": f"owner/repository/.github/workflows/{workflow}@{ref}",
            "INPUT_MAINTENANCE": "true", "INPUT_CORRELATION": "schedule-100-1",
            "INPUT_SOURCE_SHA": "a" * 40}


class FakeApi:
    """Retain the real adapter's route/method contract with independently mutable facts."""

    def __init__(self, responses):
        self.responses = responses
        self.calls = []

    def request(self, route, method="GET", payload=None):
        self.calls.append((route, method, payload))
        value = self.responses[route]
        if isinstance(value, Exception):
            raise value

        return copy.deepcopy(value)


class SequencedApi(FakeApi):
    """Expose a finite exact-run observation sequence without searching other runs."""

    def __init__(self, responses, route, observations):
        super().__init__(responses)
        self.route = route
        self.observations = iter(observations)

    def request(self, route, method="GET", payload=None):
        if route != self.route:
            return super().request(route, method, payload)

        self.calls.append((route, method, payload))
        value = next(self.observations)
        if isinstance(value, Exception):
            raise value

        return copy.deepcopy(value)


def file_response(path, text):
    """Authenticate fixture workflow bytes with the actual Git blob framing."""
    raw = text.encode("ascii")

    return {"type": "file", "path": path, "encoding": "base64",
            "content": base64.b64encode(raw).decode("ascii"),
            "sha": hashlib.sha1(b"blob " + str(len(raw)).encode("ascii") + b"\0" + raw).hexdigest()}


def dispatch_fixture(workflow="ci.yml"):
    """Provide a complete successful native dispatch and actual job evidence."""
    ref, commit = "refs/heads/maintenance/10.x", "a" * 40
    path = f".github/workflows/{workflow}"
    source = (ROOT / path).read_text(encoding="ascii")
    responses = {
        "repos/owner/repository/branches/maintenance%2F10.x": {
            "name": "maintenance/10.x", "protected": True, "commit": {"sha": commit}},
        f"repos/owner/repository/contents/{path}?ref={commit}": file_response(path, source),
        f"repos/owner/repository/actions/workflows/{workflow}/dispatches": {"workflow_run_id": 200},
        "repos/owner/repository/actions/runs/200": {
            "id": 200, "event": "workflow_dispatch", "path": path,
            "head_branch": "maintenance/10.x", "head_sha": commit,
            "display_title": "maintenance schedule-100-1", "status": "completed", "conclusion": "success"},
    }
    if workflow == "ci.yml":
        quality = (ROOT / ".github/workflows/quality-gates.yml").read_text(encoding="ascii")
        responses[f"repos/owner/repository/contents/.github/workflows/quality-gates.yml?ref={commit}"] = (
            file_response(".github/workflows/quality-gates.yml", quality))
        producers = maintenance.qualification_producers(quality)
    else:
        producers = ["schedule-identity", "dependency-vulnerability-audit"]

    responses["repos/owner/repository/actions/runs/200/jobs?per_page=100&page=1"] = {
        "total_count": len(producers), "jobs": [{"id": index + 1, "name": name,
                                                  "status": "completed", "conclusion": "success"}
                                                 for index, name in enumerate(producers)]}

    return FakeApi(responses), ref, commit


class MaintenanceTests(unittest.TestCase):
    """Reject substituted evidence rather than trusting dispatch or process success."""

    def test_clean_audit_covers_the_complete_project_set(self):
        """A clean .NET report intentionally omits framework finding arrays."""
        # Arrange
        report = {"version": 1, "parameters": "--vulnerable --include-transitive",
                  "sources": ["https://api.nuget.org/v3/index.json"],
                  "projects": [{"path": "/repo/a.csproj"}]}

        # Act
        result = maintenance.validate_audit(report, ["/repo/a.csproj"])

        # Assert
        self.assertEqual(1, result["projectCount"])

    def test_vulnerability_is_rejected_even_with_valid_process_output(self):
        """Exit zero is not evidence that the transitive graph is clean."""
        # Arrange
        package = {"id": "Example", "resolvedVersion": "1.0.0",
                   "vulnerabilities": [{"severity": "High", "advisoryurl": "https://example.test/advisory"}]}

        report = {"version": 1, "parameters": "--vulnerable --include-transitive",
                  "sources": ["https://api.nuget.org/v3/index.json"], "projects": [
                      {"path": "/repo/a.csproj", "frameworks": [{"framework": "net10.0",
                                                               "transitivePackages": [package]}]}]}

        # Act / Assert
        with self.assertRaises(maintenance.MaintenanceError):
            maintenance.validate_audit(report, ["/repo/a.csproj"])

    def test_ordinary_ci_guard_is_nonpublishing_and_does_not_read_authority(self):
        """Feature CI must remain independent of release-line activation."""
        # Arrange
        environment = {"INPUT_MAINTENANCE": "false", "GITHUB_EVENT_NAME": "pull_request"}

        # Act
        result = maintenance.validate_child(None, "ci.yml", environment)

        # Assert
        self.assertFalse(result)

    def test_active_native_guard_accepts_exact_source_and_correlation(self):
        """The source ref, workflow, commit and active major are separately bound."""
        # Arrange
        policy, environment = policy_fixture(), hosted_environment()

        # Act
        result = maintenance.validate_child(policy, "ci.yml", environment)

        # Assert
        self.assertTrue(result)

    def test_native_guard_rejects_changed_or_inactive_identity(self):
        """No caller input can borrow a different active producer's authorization."""
        for field, value in (("GITHUB_SHA", "b" * 40), ("GITHUB_REF", "refs/heads/feature/unsafe"),
                             ("INPUT_CORRELATION", "schedule-0-1"), ("GITHUB_EVENT_NAME", "push"),
                             ("GITHUB_WORKFLOW_REF", "owner/repository/.github/workflows/other.yml@refs/heads/main"),
                             ("INPUT_MAINTENANCE", "yes")):
            with self.subTest(field=field):
                # Arrange
                policy, environment = policy_fixture(), hosted_environment()
                environment[field] = value

                # Act / Assert
                with self.assertRaises(maintenance.MaintenanceError):
                    maintenance.validate_child(policy, "ci.yml", environment)

    def test_inactive_and_prepared_lines_are_not_scheduled(self):
        """Being registered for preparation grants no recurring workload."""
        for state in ("inactive", "prepared"):
            with self.subTest(state=state):
                # Arrange
                policy = policy_fixture()
                policy["lines"][1]["state"] = state

                # Act
                selected = maintenance.active_lines(policy)

                # Assert
                self.assertEqual(["refs/heads/main"], [line["sourceRef"] for line in selected])

    def test_native_producers_expand_the_actual_complete_matrix(self):
        """Patch versions come from the branch-owned file rather than constants."""
        # Arrange
        source = (ROOT / ".github/workflows/quality-gates.yml").read_text(encoding="ascii")

        # Act
        names = maintenance.qualification_producers(source)

        # Assert
        self.assertEqual(19, len(names))
        self.assertIn("Full qualification / Required", names)
        self.assertIn("Full qualification / mariadb 12.3.3", names)
        self.assertIn("Full qualification / SQL Server 2025", names)

    def test_native_matrix_change_is_detected_without_a_duplicate_patch_list(self):
        """A declared source patch change changes the observed required producer."""
        # Arrange
        source = (ROOT / ".github/workflows/quality-gates.yml").read_text(encoding="ascii")
        changed = source.replace("version: 12.3.3", "version: 12.3.99")

        # Act
        names = maintenance.qualification_producers(changed)

        # Assert
        self.assertIn("Full qualification / mariadb 12.3.99", names)
        self.assertNotIn("Full qualification / mariadb 12.3.3", names)

    def test_missing_native_matrix_cannot_shrink_the_required_workload(self):
        """An empty native matrix is not accepted as successful qualification."""
        # Arrange
        source = (ROOT / ".github/workflows/quality-gates.yml").read_text(encoding="ascii")
        source = source.replace("          - engine:", "          - hidden-engine:")

        # Act / Assert
        with self.assertRaises(maintenance.MaintenanceError):
            maintenance.qualification_producers(source)

    def test_exact_native_dispatch_retains_id_before_success(self):
        """The real route sequence observes the same immutable child and every producer."""
        # Arrange
        api, ref, commit = dispatch_fixture()
        observations = []

        # Act
        result = maintenance.dispatch_line(api, "owner/repository", "ci.yml", ref,
                                           "schedule-100-1", record=observations.append)

        # Assert
        self.assertEqual("success", result["verificationStatus"])
        self.assertEqual(200, observations[1]["runId"])
        self.assertEqual(commit, api.calls[3][2]["inputs"]["schedule-source-sha"])

    def test_successful_run_without_actual_required_producer_is_rejected(self):
        """A green overall workflow cannot conceal a skipped or removed native job."""
        for mutation in ("missing", "skipped", "failure", "duplicate"):
            with self.subTest(mutation=mutation):
                # Arrange
                api, ref, _ = dispatch_fixture()
                listing = api.responses["repos/owner/repository/actions/runs/200/jobs?per_page=100&page=1"]
                if mutation == "missing":
                    listing["jobs"].pop()
                    listing["total_count"] -= 1
                elif mutation == "duplicate":
                    extra = copy.deepcopy(listing["jobs"][-1])
                    extra["id"] = 900
                    listing["jobs"].append(extra)
                    listing["total_count"] += 1
                else:
                    listing["jobs"][-1]["conclusion"] = mutation

                # Act / Assert
                with self.assertRaises(maintenance.MaintenanceError):
                    maintenance.dispatch_line(api, "owner/repository", "ci.yml", ref, "schedule-100-1")

    def test_cancelled_or_substituted_child_keeps_the_dispatched_identity(self):
        """Failure cannot erase which run received the intended native work."""
        for field, value in (("head_sha", "b" * 40), ("head_branch", "main"),
                             ("path", ".github/workflows/other.yml"), ("display_title", "maintenance forged"),
                             ("conclusion", "cancelled")):
            with self.subTest(field=field):
                # Arrange
                api, ref, _ = dispatch_fixture()
                api.responses["repos/owner/repository/actions/runs/200"][field] = value
                observations = []

                # Act / Assert
                with mock.patch.object(maintenance.time, "monotonic", side_effect=[0, 120]):
                    with self.assertRaises(maintenance.MaintenanceError):
                        maintenance.dispatch_line(api, "owner/repository", "ci.yml", ref,
                                                   "schedule-100-1", record=observations.append)

                self.assertEqual(200, observations[1]["runId"])

    def test_dispatch_does_not_search_for_a_run_after_an_empty_legacy_response(self):
        """A same-title list search would race unrelated branch-native dispatches."""
        # Arrange
        api, ref, _ = dispatch_fixture()
        api.responses["repos/owner/repository/actions/workflows/ci.yml/dispatches"] = None

        # Act / Assert
        with self.assertRaises(maintenance.MaintenanceError):
            maintenance.dispatch_line(api, "owner/repository", "ci.yml", ref, "schedule-100-1")

        self.assertFalse(any("/runs?" in call[0] for call in api.calls))

    def test_native_audit_dispatch_uses_its_specific_producer(self):
        """The audit workload cannot be replaced by a green qualification dispatcher."""
        # Arrange
        api, ref, _ = dispatch_fixture(maintenance.MAINTENANCE_WORKFLOW)

        # Act
        result = maintenance.dispatch_line(api, "owner/repository", maintenance.MAINTENANCE_WORKFLOW,
                                           ref, "schedule-100-1")

        # Assert
        self.assertEqual("success", result["verificationStatus"])
        self.assertEqual(maintenance.MAINTENANCE_WORKFLOW, result["workflow"])

    def test_job_pages_reject_changed_total_or_duplicate_ids(self):
        """Pagination cannot silently truncate a busy native workflow's evidence."""
        for mutation in ("total", "duplicate", "empty"):
            with self.subTest(mutation=mutation):
                # Arrange
                first = [{"id": number, "name": "unrelated", "status": "completed", "conclusion": "success"}
                         for number in range(1, 101)]
                second = [{"id": 101, "name": "required", "status": "completed", "conclusion": "success"}]
                api = FakeApi({"repos/owner/repository/actions/runs/200/jobs?per_page=100&page=1": {
                    "total_count": 101, "jobs": first},
                    "repos/owner/repository/actions/runs/200/jobs?per_page=100&page=2": {
                        "total_count": 102 if mutation == "total" else 101,
                        "jobs": [] if mutation == "empty" else second}})
                if mutation == "duplicate":
                    second[0]["id"] = 1

                # Act / Assert
                with self.assertRaises(maintenance.MaintenanceError):
                    maintenance.verify_jobs(api, "owner/repository", 200, ["required"])

    def test_complete_paginated_jobs_accept_the_actual_final_producer(self):
        """Required evidence beyond the first hundred jobs remains observable."""
        # Arrange
        api = FakeApi({"repos/owner/repository/actions/runs/200/jobs?per_page=100&page=1": {
            "total_count": 101, "jobs": [{"id": number, "name": "other", "conclusion": "success"}
                                         for number in range(1, 101)]},
            "repos/owner/repository/actions/runs/200/jobs?per_page=100&page=2": {
                "total_count": 101, "jobs": [{"id": 101, "name": "required", "status": "completed",
                                             "conclusion": "success"}]}})

        # Act
        result = maintenance.verify_jobs(api, "owner/repository", 200, ["required"])

        # Assert
        self.assertIsNone(result)
        self.assertEqual(2, len(api.calls))

    def test_exact_full_page_requires_a_terminating_empty_page(self):
        """A total of one hundred is not itself proof that pagination is complete."""
        # Arrange
        jobs = [{"id": number, "name": "required" if number == 1 else "other",
                 "status": "completed", "conclusion": "success"} for number in range(1, 101)]
        api = FakeApi({"repos/owner/repository/actions/runs/200/jobs?per_page=100&page=1": {
            "total_count": 100, "jobs": jobs},
            "repos/owner/repository/actions/runs/200/jobs?per_page=100&page=2": {
                "total_count": 100, "jobs": []}})

        # Act
        maintenance.verify_jobs(api, "owner/repository", 200, ["required"])

        # Assert
        self.assertEqual(2, len(api.calls))

    def test_exact_full_page_cannot_hide_stale_or_changed_following_inventory(self):
        """A stale first-page total cannot conceal another unsuccessful producer."""
        for mutation in ("extra", "changed-total", "duplicate"):
            with self.subTest(mutation=mutation):
                # Arrange
                first = [{"id": number, "name": "required" if number == 1 else "other",
                          "status": "completed", "conclusion": "success"} for number in range(1, 101)]
                second = [{"id": 1 if mutation == "duplicate" else 101,
                           "name": "required", "status": "completed", "conclusion": "failure"}]
                api = FakeApi({"repos/owner/repository/actions/runs/200/jobs?per_page=100&page=1": {
                    "total_count": 100, "jobs": first},
                    "repos/owner/repository/actions/runs/200/jobs?per_page=100&page=2": {
                        "total_count": 101 if mutation == "changed-total" else 100, "jobs": second}})

                # Act / Assert
                with self.assertRaises(maintenance.MaintenanceError):
                    maintenance.verify_jobs(api, "owner/repository", 200, ["required"])

    def test_coordinator_retains_successful_sibling_when_another_line_fails(self):
        """Independent maintained-line failures do not hide completed work."""
        # Arrange
        policy = policy_fixture()
        authority = {"policy": policy, "controlCommit": "a" * 40}
        environment = hosted_environment(maintenance.MAINTENANCE_WORKFLOW, "refs/heads/main")
        environment.update({"GITHUB_RUN_ID": "100", "GITHUB_RUN_ATTEMPT": "1"})
        def child(api, repository, workflow, ref, correlation, record):
            record({"runId": 200 if ref.endswith("main") else 201})
            if ref.endswith("main"):
                return {"verificationStatus": "success"}

            raise maintenance.MaintenanceError("isolated negative")

        with tempfile.TemporaryDirectory() as temporary, mock.patch.object(maintenance, "dispatch_line", child):
            path = Path(temporary) / "observed.json"
            # Act
            report = maintenance.coordinate_lines(None, authority, "audit", environment, path)
            retained = json.loads(path.read_text(encoding="ascii"))

        # Assert
        self.assertEqual("failure", report["conclusion"])
        self.assertEqual(["success", "failed"], [item["verificationStatus"] for item in retained["children"]])
        self.assertEqual([200, 201], [item["runId"] for item in retained["children"]])

    def test_initial_exact_run_404_is_retried_before_full_producer_verification(self):
        """A successful dispatch need not be immediately visible to its first GET."""
        # Arrange
        original, ref, commit = dispatch_fixture()
        route = "repos/owner/repository/actions/runs/200"
        completed = original.responses[route]
        api = SequencedApi(original.responses, route, [
            maintenance.MaintenanceError("private body", code="github_http_error", http_status=404), completed])
        observations = []

        # Act
        with mock.patch.object(maintenance.time, "sleep"):
            result = maintenance.dispatch_line(api, "owner/repository", "ci.yml", ref,
                                               "schedule-100-1", record=observations.append)

        # Assert
        self.assertEqual("success", result["verificationStatus"])
        self.assertEqual(commit, result["sourceCommit"])
        self.assertEqual(2, sum(call[0] == route for call in api.calls))
        self.assertTrue(any(update.get("observationCode") == "child_not_visible" for update in observations))
        self.assertTrue(any("/jobs?" in call[0] for call in api.calls))
        self.assertFalse(any("/runs?" in call[0] for call in api.calls))

    def test_initial_queued_identity_may_finish_materializing_before_execution(self):
        """A queued placeholder is not accepted as proof and must become exact."""
        # Arrange
        original, ref, _ = dispatch_fixture()
        route = "repos/owner/repository/actions/runs/200"
        completed = original.responses[route]
        queued = {**completed, "status": "queued", "conclusion": None,
                  "display_title": "CI", "head_sha": None}
        api = SequencedApi(original.responses, route, [queued, completed])
        observations = []

        # Act
        with mock.patch.object(maintenance.time, "sleep"):
            result = maintenance.dispatch_line(api, "owner/repository", "ci.yml", ref,
                                               "schedule-100-1", record=observations.append)

        # Assert
        self.assertEqual("success", result["verificationStatus"])
        self.assertTrue(any(update.get("observationCode") == "child_identity_pending" for update in observations))
        self.assertEqual(2, sum(call[0] == route for call in api.calls))

    def test_initial_visibility_does_not_accept_contradictory_or_completed_identity(self):
        """Missing queued fields differ from a wrong identity or completed placeholder."""
        for field, value in (("id", 201), ("event", "push"), ("head_sha", "b" * 40),
                             ("head_branch", "main"), ("path", ".github/workflows/other.yml")):
            with self.subTest(field=field):
                # Arrange
                original, ref, _ = dispatch_fixture()
                route = "repos/owner/repository/actions/runs/200"
                completed = original.responses[route]
                wrong = {**completed, "status": "queued", "conclusion": None, field: value}
                api = SequencedApi(original.responses, route, [wrong, completed])

                # Act
                with self.assertRaises(maintenance.MaintenanceError) as failure:
                    maintenance.dispatch_line(api, "owner/repository", "ci.yml", ref, "schedule-100-1")

                # Assert
                self.assertEqual("child_identity_mismatch", failure.exception.code)
                self.assertEqual(field, failure.exception.identity_field)
                self.assertEqual(1, sum(call[0] == route for call in api.calls))
                self.assertFalse(any("/jobs?" in call[0] for call in api.calls))

    def test_initial_title_materialization_keeps_exact_run_and_producer_checks(self):
        """Static names and other initial titles are pending, never execution proof."""
        workflows = (("ci.yml", "CI"), (maintenance.MAINTENANCE_WORKFLOW, "Release line maintenance"))
        for workflow, name in workflows:
            for status in ("queued", "pending", "requested", "waiting", "in_progress"):
                for title in (None, "", name, "do-not-log-secret initial title"):
                    with self.subTest(workflow=workflow, status=status, title=title):
                        # Arrange
                        original, ref, commit = dispatch_fixture(workflow)
                        route = "repos/owner/repository/actions/runs/200"
                        completed = original.responses[route]
                        pending = {**completed, "status": status, "conclusion": None, "display_title": title}
                        api = SequencedApi(original.responses, route, [pending, completed])
                        observations = []

                        # Act
                        with mock.patch.object(maintenance.time, "sleep"), \
                                mock.patch.object(maintenance.time, "monotonic", return_value=0):
                            result = maintenance.dispatch_line(
                                api, "owner/repository", workflow, ref, "schedule-100-1",
                                record=observations.append)

                        # Assert
                        self.assertEqual("success", result["verificationStatus"])
                        self.assertEqual(commit, result["sourceCommit"])
                        self.assertEqual(200, result["runId"])
                        pending_updates = [item for item in observations
                                           if item.get("observationCode") == "child_identity_pending"]
                        self.assertEqual(1, len(pending_updates))
                        self.assertEqual("different" if title else "missing" if title is None else "empty",
                                         pending_updates[0]["titleState"])
                        self.assertEqual("child_observed", observations[-1]["observationCode"])
                        self.assertEqual("matching", observations[-1]["titleState"])
                        run_calls = [call for call in api.calls if call[0] == route or "/jobs?" in call[0]]
                        self.assertEqual([route, route], [call[0] for call in run_calls[:2]])
                        self.assertIn("/jobs?", run_calls[2][0])
                        self.assertTrue(all(call[1] == "GET" for call in run_calls))
                        self.assertNotIn("do-not-log-secret", json.dumps(observations))

    def test_initial_title_can_resolve_after_execution_begins(self):
        """Queue progress cannot force a display title to materialize atomically."""
        for workflow in ("ci.yml", maintenance.MAINTENANCE_WORKFLOW):
            with self.subTest(workflow=workflow):
                # Arrange
                original, ref, commit = dispatch_fixture(workflow)
                route = "repos/owner/repository/actions/runs/200"
                completed = original.responses[route]
                queued = {**completed, "status": "queued", "conclusion": None, "display_title": "initial"}
                executing = {**queued, "status": "in_progress"}
                api = SequencedApi(original.responses, route, [queued, executing, completed])

                # Act
                with mock.patch.object(maintenance.time, "sleep"), \
                        mock.patch.object(maintenance.time, "monotonic", return_value=0):
                    result = maintenance.wait_for_child(
                        api, "owner/repository", 200, workflow, ref, commit, "schedule-100-1",
                        maintenance.producer_names(api, "owner/repository", workflow, commit))

                # Assert
                self.assertEqual("success", result["verificationStatus"])
                self.assertEqual(3, sum(call[0] == route for call in api.calls))

    def test_initial_title_can_resolve_after_successful_completion(self):
        """Completed work may still await presentation metadata, never its source proof."""
        for workflow in ("ci.yml", maintenance.MAINTENANCE_WORKFLOW):
            for first_status in ("queued", "in_progress", "completed"):
                with self.subTest(workflow=workflow, first_status=first_status):
                    # Arrange
                    original, ref, commit = dispatch_fixture(workflow)
                    route = "repos/owner/repository/actions/runs/200"
                    completed = original.responses[route]
                    pending = {**completed, "display_title": "initial"}
                    first = {**pending, "status": first_status,
                             "conclusion": "success" if first_status == "completed" else None}
                    api = SequencedApi(original.responses, route, [first, pending, completed])
                    observations = []

                    # Act
                    with mock.patch.object(maintenance.time, "sleep"), \
                            mock.patch.object(maintenance.time, "monotonic", return_value=0):
                        result = maintenance.dispatch_line(
                            api, "owner/repository", workflow, ref, "schedule-100-1",
                            record=observations.append)

                    # Assert
                    self.assertEqual("success", result["verificationStatus"])
                    self.assertEqual(commit, result["sourceCommit"])
                    pending_updates = [item for item in observations
                                       if item.get("observationCode") == "child_identity_pending"]
                    self.assertEqual(2, len(pending_updates))
                    self.assertEqual("completed", pending_updates[-1]["status"])
                    self.assertEqual("different", pending_updates[-1]["titleState"])
                    self.assertEqual("matching", observations[-1]["titleState"])
                    run_calls = [call for call in api.calls if call[0] == route or "/jobs?" in call[0]]
                    self.assertEqual([route] * 3, [call[0] for call in run_calls[:3]])
                    self.assertIn("/jobs?", run_calls[3][0])

    def test_completed_failure_does_not_wait_for_an_initial_title(self):
        """An unsuccessful child is final evidence, regardless of delayed presentation."""
        for conclusion in ("failure", "cancelled", "skipped", "timed_out", "neutral", "action_required"):
            with self.subTest(conclusion=conclusion):
                # Arrange
                api, ref, commit = dispatch_fixture()
                api.responses["repos/owner/repository/actions/runs/200"].update({
                    "conclusion": conclusion, "display_title": "initial"})
                observations = []

                # Act
                with mock.patch.object(maintenance.time, "sleep") as sleep:
                    with self.assertRaises(maintenance.MaintenanceError) as failure:
                        maintenance.wait_for_child(
                            api, "owner/repository", 200, "ci.yml", ref, commit, "schedule-100-1", [],
                            record=observations.append)

                # Assert
                self.assertEqual("child_failed", failure.exception.code)
                self.assertEqual(1, len(api.calls))
                self.assertEqual("completed", observations[-1]["status"])
                self.assertEqual("different", observations[-1]["titleState"])
                sleep.assert_not_called()

    def test_completed_missing_conclusion_resolves_before_producer_checks(self):
        """Result metadata may settle on first read or after a long proven execution."""
        for workflow in ("ci.yml", maintenance.MAINTENANCE_WORKFLOW):
            for initialized, delayed_title in ((False, False), (False, True), (True, False)):
                with self.subTest(workflow=workflow, initialized=initialized, delayed_title=delayed_title):
                    # Arrange
                    original, ref, commit = dispatch_fixture(workflow)
                    route = "repos/owner/repository/actions/runs/200"
                    completed = original.responses[route]
                    pending = {**completed, "conclusion": None}
                    first = {**pending, "display_title": "initial" if delayed_title else completed["display_title"]}
                    sequence = [first, pending, completed]
                    if initialized:
                        sequence.insert(0, {**completed, "status": "in_progress", "conclusion": None})

                    api = SequencedApi(original.responses, route, sequence)
                    clock, observations = [0], []

                    def advance(delay):
                        clock[0] += 300 if initialized and clock[0] == 0 else delay

                    # Act
                    with mock.patch.object(maintenance.time, "monotonic", side_effect=lambda: clock[0]), \
                            mock.patch.object(maintenance.time, "sleep", side_effect=advance):
                        result = maintenance.dispatch_line(
                            api, "owner/repository", workflow, ref, "schedule-100-1", record=observations.append)

                    # Assert
                    self.assertEqual("success", result["verificationStatus"])
                    self.assertEqual(commit, result["sourceCommit"])
                    pending_updates = [item for item in observations
                                       if item.get("observationCode") == "child_result_pending"]
                    self.assertTrue(pending_updates)
                    self.assertTrue(all(item["conclusion"] is None for item in pending_updates))
                    self.assertEqual("matching", observations[-1]["titleState"])
                    self.assertEqual("success", observations[-1]["conclusion"])
                    calls = [call[0] for call in api.calls if call[0] == route or "/jobs?" in call[0]]
                    self.assertEqual([route] * len(sequence), calls[:-1])
                    self.assertIn("/jobs?", calls[-1])

    def test_missing_conclusion_never_resets_result_or_workload_deadline(self):
        """A missing result has one fixed grace period and cannot extend the workload."""
        cases = ((False, 14400, 120, "child_result_timeout"),
                 (True, 14400, 420, "child_result_timeout"),
                 (True, 310, 310, "child_wait_timeout"),
                 (False, 7, 7, "child_wait_timeout"))
        for workflow in ("ci.yml", maintenance.MAINTENANCE_WORKFLOW):
            for initialized, timeout, bound, code in cases:
                with self.subTest(workflow=workflow, initialized=initialized, timeout=timeout):
                    # Arrange
                    original, ref, commit = dispatch_fixture(workflow)
                    route = "repos/owner/repository/actions/runs/200"
                    completed = original.responses[route]
                    pending = {**completed, "conclusion": None}
                    sequence = [pending] * 10
                    if initialized:
                        sequence.insert(0, {**completed, "status": "in_progress", "conclusion": None})

                    api = SequencedApi(original.responses, route, sequence)
                    clock, observations = [0], []

                    def advance(delay):
                        clock[0] += 300 if initialized and clock[0] == 0 else delay

                    # Act
                    with mock.patch.object(maintenance.time, "monotonic", side_effect=lambda: clock[0]), \
                            mock.patch.object(maintenance.time, "sleep", side_effect=advance):
                        with self.assertRaises(maintenance.MaintenanceError) as failure:
                            maintenance.wait_for_child(
                                api, "owner/repository", 200, workflow, ref, commit, "schedule-100-1", [],
                                timeout=timeout, record=observations.append)

                    # Assert
                    self.assertEqual(code, failure.exception.code)
                    self.assertEqual(bound, clock[0])
                    self.assertTrue(any(item.get("observationCode") == "child_result_pending"
                                        for item in observations))
                    self.assertFalse(any("/jobs?" in call[0] for call in api.calls))

    def test_late_success_cannot_bypass_the_missing_result_deadline(self):
        """Even an exact successful readback is too late at the fixed result boundary."""
        for initialized in (False, True):
            with self.subTest(initialized=initialized):
                # Arrange
                original, ref, commit = dispatch_fixture()
                route = "repos/owner/repository/actions/runs/200"
                completed = original.responses[route]
                sequence = [{**completed, "conclusion": None}, completed]
                if initialized:
                    sequence.insert(0, {**completed, "status": "in_progress", "conclusion": None})

                api = SequencedApi(original.responses, route, sequence)
                clock = [0]

                def wake_at_bound(_):
                    clock[0] = 300 if initialized and clock[0] == 0 else 420 if initialized else 120

                # Act
                with mock.patch.object(maintenance.time, "monotonic", side_effect=lambda: clock[0]), \
                        mock.patch.object(maintenance.time, "sleep", side_effect=wake_at_bound):
                    with self.assertRaises(maintenance.MaintenanceError) as failure:
                        maintenance.wait_for_child(
                            api, "owner/repository", 200, "ci.yml", ref, commit, "schedule-100-1", [])

                # Assert
                self.assertEqual("child_result_timeout", failure.exception.code)
                self.assertEqual(420 if initialized else 120, clock[0])
                self.assertFalse(any("/jobs?" in call[0] for call in api.calls))

    def test_status_changes_cannot_reset_the_missing_result_deadline(self):
        """An intervening execution status cannot renew a latched result window."""
        for status in ("queued", "in_progress"):
            with self.subTest(status=status):
                # Arrange
                original, ref, commit = dispatch_fixture()
                route = "repos/owner/repository/actions/runs/200"
                completed = original.responses[route]
                executing = {**completed, "status": "in_progress", "conclusion": None}
                pending = {**completed, "conclusion": None}
                interim = {**executing, "status": status}
                api = SequencedApi(original.responses, route, [executing, pending, interim, pending, completed])
                clock, delays = [0], iter((300, 15, 15, 90))

                def advance(_):
                    clock[0] += next(delays)

                # Act
                with mock.patch.object(maintenance.time, "monotonic", side_effect=lambda: clock[0]), \
                        mock.patch.object(maintenance.time, "sleep", side_effect=advance):
                    with self.assertRaises(maintenance.MaintenanceError) as failure:
                        maintenance.wait_for_child(
                            api, "owner/repository", 200, "ci.yml", ref, commit, "schedule-100-1", [])

                # Assert
                self.assertEqual("child_result_timeout", failure.exception.code)
                self.assertEqual(420, clock[0])
                self.assertEqual(5, len(api.calls))
                self.assertFalse(any("/jobs?" in call[0] for call in api.calls))

    def test_missing_conclusion_cannot_bypass_source_title_result_or_job_guards(self):
        """Result settling never admits substituted evidence or unsuccessful producers."""
        defects = (("id", 201, "child_identity_mismatch"),
                   ("event", "push", "child_identity_mismatch"),
                   ("head_sha", "b" * 40, "child_identity_mismatch"),
                   ("head_sha", None, "child_identity_mismatch"),
                   ("head_branch", "other", "child_identity_mismatch"),
                   ("path", ".github/workflows/other.yml", "child_identity_mismatch"),
                   ("display_title", "maintenance schedule-101-1", "child_identity_mismatch"),
                   ("display_title", [], "child_identity_mismatch"),
                   ("conclusion", "failure", "child_failed"),
                   ("conclusion", "unknown", "child_state_invalid"),
                   ("job", "skipped", "producer_unsuccessful"),
                   ("job", "missing", "producer_unsuccessful"))
        for workflow in ("ci.yml", maintenance.MAINTENANCE_WORKFLOW):
            for field, value, code in defects:
                with self.subTest(workflow=workflow, field=field, value=value):
                    # Arrange
                    original, ref, _ = dispatch_fixture(workflow)
                    route = "repos/owner/repository/actions/runs/200"
                    completed = original.responses[route]
                    pending = {**completed, "conclusion": None}
                    changed = {**completed}
                    if field == "job":
                        jobs = original.responses[route + "/jobs?per_page=100&page=1"]
                        if value == "missing":
                            jobs["jobs"].pop()
                            jobs["total_count"] -= 1
                        else:
                            jobs["jobs"][0]["conclusion"] = value

                    else:
                        changed[field] = value

                    api = SequencedApi(original.responses, route, [pending, changed])

                    # Act
                    with mock.patch.object(maintenance.time, "monotonic", return_value=0), \
                            mock.patch.object(maintenance.time, "sleep"):
                        with self.assertRaises(maintenance.MaintenanceError) as failure:
                            maintenance.dispatch_line(api, "owner/repository", workflow, ref, "schedule-100-1")

                    # Assert
                    self.assertEqual(code, failure.exception.code)
                    self.assertEqual(2, sum(call[0] == route for call in api.calls))
                    self.assertEqual(1 if field == "job" else 0, sum("/jobs?" in call[0] for call in api.calls))

    def test_intermediate_status_wait_is_capped_by_the_result_deadline(self):
        """A nonfinal status must not sleep beyond the already latched result bound."""
        # Arrange
        original, ref, commit = dispatch_fixture()
        route = "repos/owner/repository/actions/runs/200"
        completed = original.responses[route]
        executing = {**completed, "status": "in_progress", "conclusion": None}
        api = SequencedApi(original.responses, route, [
            executing, {**completed, "conclusion": None}, executing, completed])
        clock, delays = [0], []

        def advance(delay):
            delays.append(delay)
            clock[0] = 300 if len(delays) == 1 else 419 if len(delays) == 2 else clock[0] + delay

        # Act
        with mock.patch.object(maintenance.time, "monotonic", side_effect=lambda: clock[0]), \
                mock.patch.object(maintenance.time, "sleep", side_effect=advance):
            with self.assertRaises(maintenance.MaintenanceError) as failure:
                maintenance.wait_for_child(
                    api, "owner/repository", 200, "ci.yml", ref, commit, "schedule-100-1", [])

        # Assert
        self.assertEqual("child_result_timeout", failure.exception.code)
        self.assertEqual([15, 15, 1], delays)
        self.assertEqual(420, clock[0])
        self.assertFalse(any("/jobs?" in call[0] for call in api.calls))

    def test_coordinator_retains_categories_without_raw_remote_metadata(self):
        """Actual failed child polling retains safe diagnoses in the persisted report."""
        cases = (("do-not-log-secret", "completed", "success", "different", "child_visibility_timeout"),
                 ("maintenance schedule-100-1", "completed", None, "matching", "child_result_timeout"),
                 ({"payload": "do-not-log-secret"}, "completed", "success", "invalid_type",
                  "child_identity_mismatch"),
                 (None, "do-not-log-secret", "success", "missing", "child_state_invalid"),
                 ("", "queued", "do-not-log-secret", "empty", "child_state_invalid"))
        for title, status, conclusion, category, failure_code in cases:
            with self.subTest(category=category, failure=failure_code):
                # Arrange
                api, _, commit = dispatch_fixture(maintenance.MAINTENANCE_WORKFLOW)
                api.responses["repos/owner/repository/branches/main"] = {
                    "name": "main", "protected": True, "commit": {"sha": commit}}
                api.responses["repos/owner/repository/actions/runs/200"].update({
                    "head_branch": "main", "display_title": title, "status": status, "conclusion": conclusion})
                policy = policy_fixture()
                policy["lines"][1]["state"] = "inactive"
                authority = {"policy": policy, "controlCommit": commit}
                environment = hosted_environment(maintenance.MAINTENANCE_WORKFLOW, "refs/heads/main")
                environment.update({"GITHUB_RUN_ID": "100", "GITHUB_RUN_ATTEMPT": "1"})
                clock = [0]

                def advance(delay):
                    clock[0] += delay

                with tempfile.TemporaryDirectory() as temporary:
                    path = Path(temporary) / "observed.json"

                    # Act
                    with mock.patch.object(maintenance.time, "monotonic", side_effect=lambda: clock[0]), \
                            mock.patch.object(maintenance.time, "sleep", side_effect=advance):
                        report = maintenance.coordinate_lines(api, authority, "audit", environment, path)
                        retained = path.read_text(encoding="ascii")

                # Assert
                self.assertEqual("failure", report["conclusion"])
                self.assertEqual(failure_code, report["children"][0]["failureCode"])
                self.assertEqual(category, report["children"][0]["titleState"])
                self.assertEqual(status if status != "do-not-log-secret" else "unknown",
                                 report["children"][0]["status"])
                self.assertEqual(conclusion if conclusion != "do-not-log-secret" else "unknown",
                                 report["children"][0]["conclusion"])
                self.assertEqual(report, json.loads(retained))
                self.assertNotIn("do-not-log-secret", retained)

    def test_pending_title_does_not_defer_wrong_immutable_identity(self):
        """A provisional title never grants tolerance to substituted run/source facts."""
        for field, value in (("id", 201), ("event", "push"), ("head_sha", "b" * 40),
                             ("head_branch", "other"), ("path", ".github/workflows/other.yml")):
            with self.subTest(field=field):
                # Arrange
                api, ref, commit = dispatch_fixture()
                api.responses["repos/owner/repository/actions/runs/200"].update({
                    "status": "in_progress", "conclusion": None, "display_title": "initial", field: value})

                # Act
                with self.assertRaises(maintenance.MaintenanceError) as failure:
                    maintenance.wait_for_child(
                        api, "owner/repository", 200, "ci.yml", ref, commit, "schedule-100-1", [])

                # Assert
                self.assertEqual("child_identity_mismatch", failure.exception.code)
                self.assertEqual(field, failure.exception.identity_field)
                self.assertEqual(1, len(api.calls))

    def test_executing_child_cannot_defer_missing_source_fields_with_its_title(self):
        """Only the presentation title can settle after execution has started."""
        for field in ("event", "head_sha", "head_branch", "path"):
            with self.subTest(field=field):
                # Arrange
                api, ref, commit = dispatch_fixture()
                api.responses["repos/owner/repository/actions/runs/200"].update({
                    "status": "in_progress", "conclusion": None, "display_title": "initial", field: None})

                # Act
                with self.assertRaises(maintenance.MaintenanceError) as failure:
                    maintenance.wait_for_child(
                        api, "owner/repository", 200, "ci.yml", ref, commit, "schedule-100-1", [])

                # Assert
                self.assertEqual("child_identity_mismatch", failure.exception.code)
                self.assertEqual(field, failure.exception.identity_field)
                self.assertEqual(1, len(api.calls))

    def test_malformed_initial_title_is_not_materialization(self):
        """Non-string JSON values are corrupt metadata, not unfinished presentation."""
        for title in (0, False, [], {}):
            with self.subTest(title=title):
                # Arrange
                api, ref, commit = dispatch_fixture()
                api.responses["repos/owner/repository/actions/runs/200"].update({
                    "status": "queued", "conclusion": None, "display_title": title})

                # Act
                with self.assertRaises(maintenance.MaintenanceError) as failure:
                    maintenance.wait_for_child(
                        api, "owner/repository", 200, "ci.yml", ref, commit, "schedule-100-1", [])

                # Assert
                self.assertEqual("child_identity_mismatch", failure.exception.code)
                self.assertEqual("display_title", failure.exception.identity_field)
                self.assertEqual(1, len(api.calls))

    def test_unresolved_completed_title_expires_without_polling_jobs(self):
        """Neither a placeholder nor another correlation can qualify completed work."""
        for workflow in ("ci.yml", maintenance.MAINTENANCE_WORKFLOW):
            for title in (None, "", "CI", "Release line maintenance", "maintenance schedule-101-1"):
                with self.subTest(workflow=workflow, title=title):
                    # Arrange
                    api, ref, commit = dispatch_fixture(workflow)
                    api.responses["repos/owner/repository/actions/runs/200"]["display_title"] = title
                    observations = []

                    # Act
                    with mock.patch.object(maintenance.time, "monotonic", side_effect=[0, 120]):
                        with self.assertRaises(maintenance.MaintenanceError) as failure:
                            maintenance.wait_for_child(
                                api, "owner/repository", 200, workflow, ref, commit, "schedule-100-1", [],
                                record=observations.append)

                    # Assert
                    self.assertEqual("child_visibility_timeout", failure.exception.code)
                    self.assertEqual("completed", observations[-1]["status"])
                    self.assertEqual("child_identity_pending", observations[-1]["observationCode"])
                    self.assertEqual(1, len(api.calls))

    def test_initialized_title_cannot_regress_to_pending(self):
        """An established exact correlation cannot borrow initial visibility tolerance."""
        cases = ((title, status) for title in (None, "", "CI", "maintenance schedule-101-1")
                 for status in ("in_progress", "completed"))
        for title, status in cases:
            with self.subTest(title=title, status=status):
                # Arrange
                original, ref, commit = dispatch_fixture()
                route = "repos/owner/repository/actions/runs/200"
                executing = {**original.responses[route], "status": "in_progress", "conclusion": None}
                changed = {**executing, "status": status, "display_title": title,
                           "conclusion": "success" if status == "completed" else None}
                api = SequencedApi(original.responses, route, [executing, changed])

                # Act
                with mock.patch.object(maintenance.time, "sleep"), \
                        mock.patch.object(maintenance.time, "monotonic", return_value=0):
                    with self.assertRaises(maintenance.MaintenanceError) as failure:
                        maintenance.wait_for_child(
                            api, "owner/repository", 200, "ci.yml", ref, commit, "schedule-100-1", [])

                # Assert
                self.assertEqual("child_identity_mismatch", failure.exception.code)
                self.assertEqual("display_title", failure.exception.identity_field)
                self.assertEqual(2, len(api.calls))

    def test_pending_title_cannot_extend_visibility_or_workload_deadline(self):
        """Repeated nonfinal titles share one absolute bound, not a fresh retry budget."""
        for workflow in ("ci.yml", maintenance.MAINTENANCE_WORKFLOW):
            for status in ("queued", "in_progress", "completed"):
                for timeout, bound in ((14400, 120), (7, 7)):
                    with self.subTest(workflow=workflow, status=status, timeout=timeout):
                        # Arrange
                        api, ref, commit = dispatch_fixture(workflow)
                        api.responses["repos/owner/repository/actions/runs/200"].update({
                            "status": status, "conclusion": "success" if status == "completed" else None,
                            "display_title": "initial"})
                        clock, delays, observations = [0], [], []

                        def advance(delay):
                            clock[0] += delay
                            delays.append(delay)

                        # Act
                        with mock.patch.object(maintenance.time, "monotonic", side_effect=lambda: clock[0]), \
                                mock.patch.object(maintenance.time, "sleep", side_effect=advance):
                            with self.assertRaises(maintenance.MaintenanceError) as failure:
                                maintenance.wait_for_child(
                                    api, "owner/repository", 200, workflow, ref, commit, "schedule-100-1", [],
                                    timeout=timeout, record=observations.append)

                        # Assert
                        self.assertEqual("child_visibility_timeout", failure.exception.code)
                        self.assertEqual(bound, clock[0])
                        self.assertEqual(bound, sum(delays))
                        self.assertTrue(all(0 < delay <= 15 for delay in delays))
                        self.assertEqual((bound + 14) // 15 + 1, len(api.calls))
                        pending_updates = [item for item in observations
                                           if item["observationCode"] == "child_identity_pending"]
                        self.assertEqual(len(api.calls), len(pending_updates))
                        self.assertTrue(all(item["titleState"] == "different" for item in observations))
                        self.assertFalse(any("/jobs?" in call[0] for call in api.calls))

    def test_title_materialization_after_visibility_deadline_cannot_qualify(self):
        """An exact late title cannot bypass the deadline on its successful readback."""
        for timeout, bound in ((14400, 120), (7, 7)):
            with self.subTest(timeout=timeout):
                # Arrange
                original, ref, commit = dispatch_fixture()
                route = "repos/owner/repository/actions/runs/200"
                completed = original.responses[route]
                pending = {**completed, "display_title": "initial"}
                api = SequencedApi(original.responses, route, [pending, completed])
                clock = [0]

                def wake_at_deadline(_):
                    clock[0] = bound

                # Act
                with mock.patch.object(maintenance.time, "monotonic", side_effect=lambda: clock[0]), \
                        mock.patch.object(maintenance.time, "sleep", side_effect=wake_at_deadline):
                    with self.assertRaises(maintenance.MaintenanceError) as failure:
                        maintenance.wait_for_child(
                            api, "owner/repository", 200, "ci.yml", ref, commit, "schedule-100-1", [],
                            timeout=timeout)

                # Assert
                self.assertEqual("child_visibility_timeout", failure.exception.code)
                self.assertEqual(2, len(api.calls))
                self.assertFalse(any("/jobs?" in call[0] for call in api.calls))

    def test_delayed_title_never_replaces_a_successful_child_conclusion(self):
        """Resolved presentation metadata cannot turn unsuccessful work into evidence."""
        for workflow in ("ci.yml", maintenance.MAINTENANCE_WORKFLOW):
            for conclusion in ("failure", "cancelled", "skipped", "timed_out", "neutral", "action_required"):
                with self.subTest(workflow=workflow, conclusion=conclusion):
                    # Arrange
                    original, ref, commit = dispatch_fixture(workflow)
                    route = "repos/owner/repository/actions/runs/200"
                    failed = {**original.responses[route], "conclusion": conclusion}
                    pending = {**failed, "status": "in_progress", "conclusion": None, "display_title": "initial"}
                    api = SequencedApi(original.responses, route, [pending, failed])

                    # Act
                    with mock.patch.object(maintenance.time, "sleep"), \
                            mock.patch.object(maintenance.time, "monotonic", return_value=0):
                        with self.assertRaises(maintenance.MaintenanceError) as failure:
                            maintenance.wait_for_child(
                                api, "owner/repository", 200, workflow, ref, commit, "schedule-100-1", [])

                    # Assert
                    self.assertEqual("child_failed", failure.exception.code)
                    self.assertEqual(2, len(api.calls))
                    self.assertFalse(any("/jobs?" in call[0] for call in api.calls))

    def test_delayed_title_still_requires_every_successful_producer(self):
        """Green wrappers cannot hide skipped, missing, duplicate or unsuccessful jobs."""
        for workflow in ("ci.yml", maintenance.MAINTENANCE_WORKFLOW):
            for defect in ("skipped", "failure", "cancelled", "in_progress", "missing", "duplicate"):
                with self.subTest(workflow=workflow, defect=defect):
                    # Arrange
                    original, ref, commit = dispatch_fixture(workflow)
                    route = "repos/owner/repository/actions/runs/200"
                    jobs = original.responses[route + "/jobs?per_page=100&page=1"]
                    expected = [job["name"] for job in jobs["jobs"]]
                    if defect == "missing":
                        jobs["jobs"].pop()
                    elif defect == "duplicate":
                        jobs["jobs"].append({**jobs["jobs"][0], "id": 100})
                    elif defect == "in_progress":
                        jobs["jobs"][0].update({"status": "in_progress", "conclusion": None})
                    else:
                        jobs["jobs"][0]["conclusion"] = defect

                    jobs["total_count"] = len(jobs["jobs"])
                    completed = original.responses[route]
                    pending = {**completed, "display_title": "initial"}
                    api = SequencedApi(original.responses, route, [pending, completed])

                    # Act
                    with mock.patch.object(maintenance.time, "sleep"), \
                            mock.patch.object(maintenance.time, "monotonic", return_value=0):
                        with self.assertRaises(maintenance.MaintenanceError) as failure:
                            maintenance.wait_for_child(
                                api, "owner/repository", 200, workflow, ref, commit, "schedule-100-1", expected)

                    # Assert
                    self.assertEqual("producer_unsuccessful", failure.exception.code)
                    self.assertEqual(2, sum(call[0] == route for call in api.calls))
                    self.assertEqual(1, sum("/jobs?" in call[0] for call in api.calls))

    def test_completed_identity_cannot_use_missing_initial_fields(self):
        """Completion never converts an incomplete exact-source observation to success."""
        for field in ("event", "head_sha", "head_branch", "path"):
            with self.subTest(field=field):
                # Arrange
                api, ref, _ = dispatch_fixture()
                api.responses["repos/owner/repository/actions/runs/200"][field] = None

                # Act
                with self.assertRaises(maintenance.MaintenanceError) as failure:
                    maintenance.dispatch_line(api, "owner/repository", "ci.yml", ref, "schedule-100-1")

                # Assert
                self.assertEqual("child_identity_mismatch", failure.exception.code)
                self.assertEqual(field, failure.exception.identity_field)

    def test_visibility_failure_is_bounded_and_retains_the_exact_http_status(self):
        """An unavailable exact run remains a failure, never synthetic qualification."""
        # Arrange
        original, ref, commit = dispatch_fixture()
        route = "repos/owner/repository/actions/runs/200"
        api = SequencedApi(original.responses, route, [
            maintenance.MaintenanceError("private body", code="github_http_error", http_status=404)])
        observations = []

        # Act
        with self.assertRaises(maintenance.MaintenanceError) as failure:
            maintenance.wait_for_child(api, "owner/repository", 200, "ci.yml", ref,
                                       commit, "schedule-100-1", [], timeout=0, record=observations.append)

        # Assert
        self.assertEqual("child_visibility_timeout", failure.exception.code)
        self.assertEqual(404, failure.exception.http_status)
        self.assertEqual(1, len(api.calls))
        self.assertFalse(any("/jobs?" in call[0] for call in api.calls))

    def test_initial_visibility_uses_a_shorter_bound_than_the_workload(self):
        """An unobservable dispatch cannot consume the entire four-hour qualification wait."""
        # Arrange
        original, ref, commit = dispatch_fixture()
        route = "repos/owner/repository/actions/runs/200"
        api = SequencedApi(original.responses, route, [
            maintenance.MaintenanceError("private body", code="github_http_error", http_status=404)])

        # Act
        with mock.patch.object(maintenance.time, "monotonic", side_effect=[0, 121]):
            with self.assertRaises(maintenance.MaintenanceError) as failure:
                maintenance.wait_for_child(api, "owner/repository", 200, "ci.yml", ref,
                                           commit, "schedule-100-1", [], timeout=14400)

        # Assert
        self.assertEqual("child_visibility_timeout", failure.exception.code)
        self.assertEqual(1, len(api.calls))

    def test_late_wakeup_cannot_accept_a_newly_visible_completed_child(self):
        """A delayed poll cannot turn expired initial visibility into successful proof."""
        # Arrange
        original, ref, commit = dispatch_fixture()
        route = "repos/owner/repository/actions/runs/200"
        api = SequencedApi(original.responses, route, [
            maintenance.MaintenanceError("private body", code="github_http_error", http_status=404),
            original.responses[route]])
        clock = [0]

        def wake_late(_):
            clock[0] = 121

        # Act
        with mock.patch.object(maintenance.time, "monotonic", side_effect=lambda: clock[0]), \
                mock.patch.object(maintenance.time, "sleep", wake_late):
            with self.assertRaises(maintenance.MaintenanceError) as failure:
                maintenance.wait_for_child(api, "owner/repository", 200, "ci.yml", ref,
                                           commit, "schedule-100-1", [])

        # Assert
        self.assertEqual("child_visibility_timeout", failure.exception.code)
        self.assertEqual(2, len(api.calls))
        self.assertFalse(any("/jobs?" in call[0] for call in api.calls))

    def test_late_wakeup_cannot_accept_completion_after_the_workload_deadline(self):
        """An initialized run still has to complete within the existing workload bound."""
        # Arrange
        original, ref, commit = dispatch_fixture()
        route = "repos/owner/repository/actions/runs/200"
        completed = original.responses[route]
        queued = {**completed, "status": "queued", "conclusion": None}
        api = SequencedApi(original.responses, route, [queued, completed])
        clock = [0]

        def wake_late(_):
            clock[0] = 14401

        # Act
        with mock.patch.object(maintenance.time, "monotonic", side_effect=lambda: clock[0]), \
                mock.patch.object(maintenance.time, "sleep", wake_late):
            with self.assertRaises(maintenance.MaintenanceError) as failure:
                maintenance.wait_for_child(api, "owner/repository", 200, "ci.yml", ref,
                                           commit, "schedule-100-1", [])

        # Assert
        self.assertEqual("child_wait_timeout", failure.exception.code)
        self.assertEqual(2, len(api.calls))
        self.assertFalse(any("/jobs?" in call[0] for call in api.calls))

    def test_missing_queued_metadata_cannot_outlive_the_initial_visibility_bound(self):
        """A permanently incomplete queued identity is retained but never qualified."""
        # Arrange
        api, ref, commit = dispatch_fixture()
        api.responses["repos/owner/repository/actions/runs/200"].update({
            "status": "queued", "conclusion": None, "display_title": "CI"})
        observations = []

        # Act
        with mock.patch.object(maintenance.time, "monotonic", side_effect=[0, 121]):
            with self.assertRaises(maintenance.MaintenanceError) as failure:
                maintenance.wait_for_child(api, "owner/repository", 200, "ci.yml", ref,
                                           commit, "schedule-100-1", [], record=observations.append)

        # Assert
        self.assertEqual("child_visibility_timeout", failure.exception.code)
        self.assertEqual("child_identity_pending", observations[-1]["observationCode"])

    def test_a_previously_visible_child_cannot_disappear_and_reappear(self):
        """Initial visibility tolerance is not a general retry for deleted/substituted runs."""
        # Arrange
        original, ref, commit = dispatch_fixture()
        route = "repos/owner/repository/actions/runs/200"
        completed = original.responses[route]
        queued = {**completed, "status": "queued", "conclusion": None}
        api = SequencedApi(original.responses, route, [queued,
            maintenance.MaintenanceError("private body", code="github_http_error", http_status=404), completed])

        # Act
        with mock.patch.object(maintenance.time, "sleep"):
            with self.assertRaises(maintenance.MaintenanceError) as failure:
                maintenance.wait_for_child(api, "owner/repository", 200, "ci.yml", ref,
                                           commit, "schedule-100-1", [])

        # Assert
        self.assertEqual("github_http_error", failure.exception.code)
        self.assertEqual(2, len(api.calls))

    def test_permissions_are_not_treated_as_initial_visibility(self):
        """A new exact run cannot justify retrying unauthorized or forbidden requests."""
        for status in (401, 403, 429, 500):
            with self.subTest(status=status):
                # Arrange
                original, ref, _ = dispatch_fixture()
                route = "repos/owner/repository/actions/runs/200"
                api = SequencedApi(original.responses, route, [
                    maintenance.MaintenanceError("secret body", code="github_http_error", http_status=status),
                    original.responses[route]])

                # Act
                with self.assertRaises(maintenance.MaintenanceError) as failure:
                    maintenance.dispatch_line(api, "owner/repository", "ci.yml", ref, "schedule-100-1")

                # Assert
                self.assertEqual(status, failure.exception.http_status)
                self.assertEqual(1, sum(call[0] == route for call in api.calls))

    def test_noncompleted_run_cannot_already_claim_a_conclusion(self):
        """Contradictory run state is not pending visibility or successful qualification."""
        for status in ("queued", "pending", "requested", "waiting", "in_progress"):
            with self.subTest(status=status):
                # Arrange
                api, ref, commit = dispatch_fixture()
                api.responses["repos/owner/repository/actions/runs/200"].update({
                    "status": status, "conclusion": "success"})

                # Act
                with self.assertRaises(maintenance.MaintenanceError) as failure:
                    maintenance.wait_for_child(api, "owner/repository", 200, "ci.yml", ref,
                                               commit, "schedule-100-1", [], timeout=0)

                # Assert
                self.assertEqual("child_state_invalid", failure.exception.code)
                self.assertEqual(1, len(api.calls))

    def test_coordinator_retains_only_allowlisted_failure_details(self):
        """Raw process/API payloads must not enter retained coordinator reports."""
        # Arrange
        policy = policy_fixture()
        authority = {"policy": policy, "controlCommit": "a" * 40}
        environment = hosted_environment(maintenance.MAINTENANCE_WORKFLOW, "refs/heads/main")
        environment.update({"GITHUB_RUN_ID": "100", "GITHUB_RUN_ATTEMPT": "1"})

        def child(api, repository, workflow, ref, correlation, record):
            record({"runId": 200})
            raise maintenance.MaintenanceError("do-not-log-secret", code="github_http_error", http_status=403)

        with tempfile.TemporaryDirectory() as temporary, mock.patch.object(maintenance, "dispatch_line", child):
            path = Path(temporary) / "observed.json"
            # Act
            report = maintenance.coordinate_lines(None, authority, "audit", environment, path)
            retained = path.read_text(encoding="ascii")

        # Assert
        self.assertEqual("failure", report["conclusion"])
        self.assertTrue(all(item["failureCode"] == "github_http_error" for item in report["children"]))
        self.assertTrue(all(item["httpStatus"] == 403 for item in report["children"]))
        self.assertNotIn("do-not-log-secret", retained)

    def test_github_adapter_retains_http_status_without_response_payload(self):
        """Read the real CLI response framing instead of guessing status from stderr."""
        # Arrange
        response = subprocess.CompletedProcess(
            ["gh"], 1, stdout='HTTP/2.0 404 Not Found\r\nContent-Type: application/json\r\n\r\n'
                              '{"message":"do-not-log-secret"}', stderr="do-not-log-secret")

        # Act
        with mock.patch.object(maintenance.subprocess, "run", return_value=response) as execute:
            with self.assertRaises(maintenance.MaintenanceError) as failure:
                maintenance.GitHub().request("repos/owner/repository/actions/runs/200")

        # Assert
        self.assertIn("--include", execute.call_args.args[0])
        self.assertEqual("github_http_error", failure.exception.code)
        self.assertEqual(404, failure.exception.http_status)
        self.assertNotIn("do-not-log-secret", str(failure.exception))

    def test_github_adapter_reads_successful_header_framed_json(self):
        """Status framing must preserve ordinary JSON consumers and exact body values."""
        # Arrange
        response = subprocess.CompletedProcess(
            ["gh"], 0, stdout='HTTP/2.0 200 OK\nContent-Type: application/json\n\n{"id":200}', stderr="")

        # Act
        with mock.patch.object(maintenance.subprocess, "run", return_value=response):
            result = maintenance.GitHub().request("repos/owner/repository/actions/runs/200")

        # Assert
        self.assertEqual({"id": 200}, result)

    def test_github_adapter_does_not_parse_http_status_from_process_error_text(self):
        """A private diagnostic mentioning 404 must not authorize a visibility retry."""
        # Arrange
        response = subprocess.CompletedProcess(
            ["gh"], 1, stdout="", stderr="secret transport failure mentions HTTP 404")

        # Act
        with mock.patch.object(maintenance.subprocess, "run", return_value=response):
            with self.assertRaises(maintenance.MaintenanceError) as failure:
                maintenance.GitHub().request("repos/owner/repository/actions/runs/200")

        # Assert
        self.assertEqual("github_request_failed", failure.exception.code)
        self.assertIsNone(failure.exception.http_status)
        self.assertNotIn("secret", str(failure.exception))

    def test_github_adapter_rejects_invalid_or_duplicated_json_after_valid_headers(self):
        """Framing does not weaken strict JSON or duplicate-key validation."""
        for body in ("not json", '{"id":200,"id":201}'):
            with self.subTest(body=body):
                # Arrange
                response = subprocess.CompletedProcess(
                    ["gh"], 0, stdout="HTTP/2.0 200 OK\nContent-Type: application/json\n\n" + body, stderr="")

                # Act
                with mock.patch.object(maintenance.subprocess, "run", return_value=response):
                    with self.assertRaises(maintenance.MaintenanceError) as failure:
                        maintenance.GitHub().request("repos/owner/repository/actions/runs/200")

                # Assert
                self.assertEqual("github_invalid_response", failure.exception.code)

    def test_github_adapter_rejects_oversized_json_without_disclosing_body(self):
        """HTTP framing keeps the original bounded JSON contract intact."""
        # Arrange
        response = subprocess.CompletedProcess(
            ["gh"], 0, stdout='HTTP/2.0 200 OK\n\n{"private":"' + "x" * 65 + '"}', stderr="")

        # Act
        with mock.patch.object(maintenance.subprocess, "run", return_value=response), \
                mock.patch.object(maintenance, "MAX_JSON_BYTES", 64):
            with self.assertRaises(maintenance.MaintenanceError) as failure:
                maintenance.GitHub().request("repos/owner/repository/actions/runs/200")

        # Assert
        self.assertEqual("github_response_limit", failure.exception.code)
        self.assertNotIn("private", str(failure.exception))

    def test_audit_rejects_missing_transitive_advisory_and_project_coverage(self):
        """Empty results, advisory warnings and duplicate identities are not clean scans."""
        for mutation in ("schema", "transitive", "sources", "problems", "missing", "duplicate", "framework"):
            with self.subTest(mutation=mutation):
                # Arrange
                report = {"version": 1, "parameters": "--vulnerable --include-transitive",
                          "sources": ["https://api.nuget.org/v3/index.json"],
                          "projects": [{"path": "/repo/a.csproj"}]}
                if mutation == "schema":
                    report["version"] = True
                elif mutation == "transitive":
                    report["parameters"] = "--vulnerable"
                elif mutation == "sources":
                    report["sources"] = []
                elif mutation == "problems":
                    report["problems"] = [{"severity": "warning", "text": "advisory unavailable"}]
                elif mutation == "missing":
                    report["projects"] = []
                elif mutation == "duplicate":
                    report["projects"] *= 2
                else:
                    report["projects"][0]["frameworks"] = "not-a-list"

                # Act / Assert
                with self.assertRaises(maintenance.MaintenanceError):
                    maintenance.validate_audit(report, ["/repo/a.csproj"])

    def test_audit_adapter_requires_fresh_fatal_transitive_restore_and_json(self):
        """The executable adapter must request the complete audit contract, not just parse a fixture."""
        # Arrange
        observed = []
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "src").mkdir()
            project = root / "src/A.csproj"
            project.write_text("<Project />", encoding="ascii")
            (root / "Doka.EntityFrameworkCore.SafeMigrations.slnx").write_text(
                '<Solution><Project Path="src/A.csproj" /></Solution>', encoding="ascii")

            def execute(command, **options):
                observed.append((command, options))
                if command[1:3] == ["package", "list"]:
                    json.dump({"version": 1, "parameters": "--vulnerable --include-transitive",
                               "sources": ["https://api.nuget.org/v3/index.json"],
                               "projects": [{"path": str(project)}]}, options["stdout"])

                return subprocess.CompletedProcess(command, 0)

            with mock.patch.object(maintenance.subprocess, "run", execute):
                # Act
                summary = maintenance.run_audit(root, root / "evidence")

            retained = json.loads((root / "evidence/summary.json").read_text(encoding="ascii"))

        # Assert
        self.assertEqual("success", summary["conclusion"])
        self.assertEqual(1, retained["projectCount"])
        self.assertIn("--locked-mode", observed[0][0])
        self.assertIn("--no-http-cache", observed[0][0])
        self.assertIn("-p:NuGetAuditMode=all", observed[0][0])
        self.assertIn("-p:TreatWarningsAsErrors=true", observed[0][0])
        self.assertIn("-p:WarningsNotAsErrors=", observed[0][0])
        self.assertIn("--include-transitive", observed[1][0])
        self.assertIn("--no-restore", observed[1][0])

    def test_audit_adapter_does_not_accept_zero_exit_after_advisory_failure(self):
        """Known CLI success-after-warning behavior cannot produce a success summary."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "src").mkdir()
            project = root / "src/A.csproj"
            project.write_text("<Project />", encoding="ascii")
            (root / "Doka.EntityFrameworkCore.SafeMigrations.slnx").write_text(
                '<Solution><Project Path="src/A.csproj" /></Solution>', encoding="ascii")

            def execute(command, **options):
                if command[1:3] == ["package", "list"]:
                    json.dump({"version": 1, "parameters": "--vulnerable --include-transitive",
                               "sources": ["https://api.nuget.org/v3/index.json"],
                               "projects": [{"path": str(project)}],
                               "problems": [{"severity": "Warning", "text": "advisory unavailable"}]},
                              options["stdout"])

                return subprocess.CompletedProcess(command, 0)

            with mock.patch.object(maintenance.subprocess, "run", execute):
                # Act / Assert
                with self.assertRaises(maintenance.MaintenanceError):
                    maintenance.run_audit(root, root / "evidence")

            self.assertFalse((root / "evidence/summary.json").exists())

    def test_audit_cache_is_outside_artifacts_and_cleaned_after_success(self):
        """Real NuGet colon-bearing filenames are disposable, not upload evidence."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            output = root / "evidence"
            output.mkdir()
            (root / "src").mkdir()
            project = root / "src/A.csproj"
            project.write_text("<Project />", encoding="ascii")
            (root / "Doka.EntityFrameworkCore.SafeMigrations.slnx").write_text(
                '<Solution><Project Path="src/A.csproj" /></Solution>', encoding="ascii")
            caches = []

            def execute(command, **options):
                cache = Path(options["env"]["NUGET_HTTP_CACHE_PATH"])
                caches.append(cache)
                cache.mkdir(parents=True, exist_ok=True)
                (cache / "list_example:pkg.dat").write_text("cache", encoding="ascii")
                if command[1:3] == ["package", "list"]:
                    json.dump({"version": 1, "parameters": "--vulnerable --include-transitive",
                               "sources": ["https://api.nuget.org/v3/index.json"],
                               "projects": [{"path": str(project)}]}, options["stdout"])

                return subprocess.CompletedProcess(command, 0)

            with mock.patch.object(maintenance.subprocess, "run", execute), \
                    mock.patch.object(tempfile, "tempdir", str(output)):
                # Act
                summary = maintenance.run_audit(root, output)

            retained = {path.name for path in output.rglob("*")}
            # Assert
            self.assertEqual("success", summary["conclusion"])
            self.assertEqual(2, len(caches))
            self.assertEqual(caches[0], caches[1])
            self.assertFalse(caches[0].is_relative_to(output))
            self.assertFalse(caches[0].exists())
            self.assertEqual({"restore.log", "audit.json", "audit.log", "summary.json"}, retained)
            self.assertFalse(any(":" in name for name in retained))

    def test_audit_failure_retains_logs_but_not_disposable_cache(self):
        """Failed restore, listing and advisory evidence cannot retain upload-invalid cache data."""
        for stage in ("restore", "listing", "warning"):
            with self.subTest(stage=stage), tempfile.TemporaryDirectory() as temporary:
                # Arrange
                root = Path(temporary)
                output = root / "evidence"
                (root / "src").mkdir()
                project = root / "src/A.csproj"
                project.write_text("<Project />", encoding="ascii")
                (root / "Doka.EntityFrameworkCore.SafeMigrations.slnx").write_text(
                    '<Solution><Project Path="src/A.csproj" /></Solution>', encoding="ascii")
                caches = []

                def execute(command, **options):
                    cache = Path(options["env"]["NUGET_HTTP_CACHE_PATH"])
                    caches.append(cache)
                    cache.mkdir(parents=True, exist_ok=True)
                    (cache / "list_example:pkg.dat").write_text("cache", encoding="ascii")
                    options["stdout"].write("retained diagnostic")
                    if command[1:3] == ["package", "list"] and stage == "warning":
                        options["stderr"].write("advisory unavailable")

                    failed = (stage == "restore" and command[1] == "restore"
                              or stage == "listing" and command[1:3] == ["package", "list"])

                    return subprocess.CompletedProcess(command, 1 if failed else 0)

                with mock.patch.object(maintenance.subprocess, "run", execute):
                    # Act
                    with self.assertRaises(maintenance.MaintenanceError):
                        maintenance.run_audit(root, output)

                # Assert
                self.assertTrue((output / "restore.log").exists())
                self.assertFalse((output / "summary.json").exists())
                self.assertTrue(caches)
                self.assertTrue(all(not cache.exists() for cache in caches))
                self.assertFalse(any(":" in path.name for path in output.rglob("*")))

    def test_audit_process_timeout_releases_only_its_own_http_cache(self):
        """Exceptional process exits retain diagnostics and clean only owned disposable state."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            output = root / "evidence"
            (root / "src").mkdir()
            project = root / "src/A.csproj"
            project.write_text("<Project />", encoding="ascii")
            (root / "Doka.EntityFrameworkCore.SafeMigrations.slnx").write_text(
                '<Solution><Project Path="src/A.csproj" /></Solution>', encoding="ascii")
            unrelated = root / "unrelated-cache"
            unrelated.mkdir()
            (unrelated / "keep.dat").write_text("owned by another run", encoding="ascii")
            caches = []

            def execute(command, **options):
                cache = Path(options["env"]["NUGET_HTTP_CACHE_PATH"])
                caches.append(cache)
                (cache / "list_example:pkg.dat").write_text("cache", encoding="ascii")
                options["stdout"].write("retained timeout diagnostic")
                raise subprocess.TimeoutExpired(command, 1200)

            with mock.patch.object(maintenance.subprocess, "run", execute):
                # Act
                with self.assertRaises(subprocess.TimeoutExpired):
                    maintenance.run_audit(root, output)

            # Assert
            self.assertEqual("retained timeout diagnostic", (output / "restore.log").read_text(encoding="ascii"))
            self.assertFalse((output / "summary.json").exists())
            self.assertEqual(1, len(caches))
            self.assertFalse(caches[0].exists())
            self.assertTrue((unrelated / "keep.dat").exists())

    def test_audit_cache_cleanup_failure_cannot_publish_a_success_summary(self):
        """A valid scan does not become a clean audit before owned cleanup succeeds."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            output = root / "evidence"
            (root / "src").mkdir()
            project = root / "src/A.csproj"
            project.write_text("<Project />", encoding="ascii")
            (root / "Doka.EntityFrameworkCore.SafeMigrations.slnx").write_text(
                '<Solution><Project Path="src/A.csproj" /></Solution>', encoding="ascii")
            original_exit = tempfile.TemporaryDirectory.__exit__

            def fail_cleanup(instance, *failure):
                original_exit(instance, *failure)
                raise OSError("owned cleanup failed")

            def execute(command, **options):
                if command[1:3] == ["package", "list"]:
                    json.dump({"version": 1, "parameters": "--vulnerable --include-transitive",
                               "sources": ["https://api.nuget.org/v3/index.json"],
                               "projects": [{"path": str(project)}]}, options["stdout"])

                return subprocess.CompletedProcess(command, 0)

            with mock.patch.object(maintenance.subprocess, "run", execute), \
                    mock.patch.object(tempfile.TemporaryDirectory, "__exit__", fail_cleanup):
                # Act
                with self.assertRaises(OSError):
                    maintenance.run_audit(root, output)

            # Assert
            self.assertTrue((output / "audit.json").exists())
            self.assertFalse((output / "summary.json").exists())

    def test_source_workflow_blob_tampering_is_rejected_before_dispatch(self):
        """A matching filename and commit query cannot authenticate replaced source bytes."""
        # Arrange
        api, ref, commit = dispatch_fixture()
        route = f"repos/owner/repository/contents/.github/workflows/ci.yml?ref={commit}"
        api.responses[route]["sha"] = "b" * 40

        # Act / Assert
        with self.assertRaises(maintenance.MaintenanceError):
            maintenance.dispatch_line(api, "owner/repository", "ci.yml", ref, "schedule-100-1")

        self.assertFalse(any(call[1] == "POST" for call in api.calls))

    def test_wait_timeout_is_a_failure_not_completed_qualification(self):
        """A queued child remains visible evidence but never an artificial success."""
        # Arrange
        api, ref, commit = dispatch_fixture()
        api.responses["repos/owner/repository/actions/runs/200"].update({
            "status": "queued", "conclusion": None})

        # Act / Assert
        with self.assertRaises(maintenance.MaintenanceError):
            maintenance.wait_for_child(api, "owner/repository", 200, "ci.yml", ref,
                                       commit, "schedule-100-1", [], timeout=0)


class DependencyTargetsTests(unittest.TestCase):
    """Preserve existing grouping while adding only reviewed active-line targets."""

    def test_current_main_only_configuration_remains_byte_identical(self):
        """Registering an inactive line must not create more Dependabot PR targets."""
        # Arrange
        policy = lines.load_policy()
        source = (ROOT / ".github/dependabot.yml").read_text(encoding="ascii")

        # Act
        rendered = dependencies.render_configuration(policy, source)

        # Assert
        self.assertEqual(source, rendered)
        self.assertEqual(3, len(dependencies.entries(rendered)))

    def test_activation_renders_three_native_targets_with_major_boundaries(self):
        """Maintenance reads its own manifests without importing main's framework major."""
        # Arrange
        policy = policy_fixture()
        source = (ROOT / ".github/dependabot.yml").read_text(encoding="ascii")

        # Act
        rendered = dependencies.render_configuration(policy, source)
        dependencies.validate_configuration(policy, rendered)

        # Assert
        self.assertEqual(6, len(dependencies.entries(rendered)))
        self.assertNotIn("docker", rendered)
        self.assertIn('versions: [">=11.0.0-0", ">=11.0.0--"]', rendered)
        self.assertIn('dependency-name: "Npgsql.EntityFrameworkCore.PostgreSQL"', rendered)
        self.assertEqual(6, rendered.count("default-days: 3"))

    def test_maintained_npgsql_provider_cannot_escape_its_ef_major(self):
        """The PostgreSQL EF adapter is bounded independently of its transport driver."""
        # Arrange
        policy = policy_fixture()
        source = (ROOT / ".github/dependabot.yml").read_text(encoding="ascii")
        rendered = dependencies.render_configuration(policy, source)
        rendered = rendered.replace('      - dependency-name: "Npgsql.EntityFrameworkCore.PostgreSQL"\n'
                                    '        versions: [">=11.0.0-0", ">=11.0.0--"]\n', "")

        # Act / Assert
        self.assertNotIn('dependency-name: "Npgsql.EntityFrameworkCore.PostgreSQL"', rendered)
        with self.assertRaises(dependencies.DependencyPolicyError):
            dependencies.validate_configuration(policy, rendered)

    def test_templates_survive_main_inactive_then_new_main_major_roundtrip(self):
        """A valid maintenance-only stage must not lose the next activation's settings."""
        # Arrange
        source = (ROOT / ".github/dependabot.yml").read_text(encoding="ascii")
        initial = lines.load_policy()
        maintenance_only = copy.deepcopy(initial)
        maintenance_only["lines"][0]["state"] = "inactive"
        maintenance_only["lines"][1]["state"] = "active"
        next_main = policy_fixture()

        # Act
        first = dependencies.render_configuration(initial, source)
        second = dependencies.render_configuration(maintenance_only, first)
        second_repeat = dependencies.render_configuration(maintenance_only, second)
        third = dependencies.render_configuration(next_main, second)
        third_repeat = dependencies.render_configuration(next_main, third)

        # Assert
        dependencies.validate_configuration(maintenance_only, second)
        dependencies.validate_configuration(next_main, third)
        self.assertEqual(3, len(dependencies.entries(second)))
        self.assertEqual(6, len(dependencies.entries(third)))
        self.assertTrue(all(branch == "maintenance/10.x" for _, branch, _ in dependencies.entries(second)))
        self.assertEqual(second, second_repeat)
        self.assertEqual(third, third_repeat)
        self.assertEqual(3, second.count("default-days: 3"))
        self.assertEqual(6, third.count("default-days: 3"))
        self.assertEqual(2, third.count("nuget-dependencies:"))

    def test_template_recovery_rejects_unknown_maintenance_policy_changes(self):
        """Recovery may strip its own fields, never an independent reviewed update rule."""
        for mutation in ("extra-ignore", "changed-ceiling", "conflicting-group"):
            with self.subTest(mutation=mutation):
                # Arrange
                source = (ROOT / ".github/dependabot.yml").read_text(encoding="ascii")
                policy = policy_fixture()
                rendered = dependencies.render_configuration(policy, source)
                if mutation == "extra-ignore":
                    rendered += '      - dependency-name: "unrelated"\n        versions: ["1.0.0"]\n'
                elif mutation == "changed-ceiling":
                    rendered = rendered.replace('versions: [">=11.0.0-0", ">=11.0.0--"]',
                                                'versions: [">=12.0.0"]')
                else:
                    head, tail = rendered.split("    target-branch: maintenance/10.x", 1)
                    rendered = head + "    target-branch: maintenance/10.x" + tail.replace(
                        "nuget-dependencies:", "different-group:", 1)

                # Act / Assert
                if mutation == "changed-ceiling":
                    self.assertIn('versions: [">=12.0.0"]', rendered)

                with self.assertRaises(dependencies.DependencyPolicyError):
                    dependencies.render_configuration(policy, rendered)

    def test_all_inactive_retirement_can_resume_without_dead_update_targets(self):
        """An empty generated update list cannot destroy its independent source template."""
        # Arrange
        source = (ROOT / ".github/dependabot.yml").read_text(encoding="ascii")
        retired = lines.load_policy()
        for line in retired["lines"]:
            line["state"] = "inactive"

        resumed = policy_fixture()

        # Act
        empty = dependencies.render_configuration(retired, source)
        empty_repeat = dependencies.render_configuration(retired, empty)
        restored = dependencies.render_configuration(resumed, empty)
        restored_repeat = dependencies.render_configuration(resumed, restored)

        # Assert
        dependencies.validate_configuration(retired, empty)
        dependencies.validate_configuration(resumed, restored)
        self.assertEqual("version: 2\nupdates: []\n", empty)
        self.assertEqual([], dependencies.entries(empty))
        self.assertEqual(empty, empty_repeat)
        self.assertEqual(restored, restored_repeat)
        self.assertEqual(6, len(dependencies.entries(restored)))
        self.assertEqual(6, restored.count("default-days: 3"))

    def test_render_and_gate_consume_the_canonical_template_settings(self):
        """Grouping/cooldown input is executable policy, not an inert duplicate file."""
        # Arrange
        policy = lines.load_policy()
        template = (ROOT / ".github/dependabot-template.yml").read_text(encoding="ascii")
        changed = template.replace("default-days: 3", "default-days: 5")

        # Act
        rendered = dependencies.render_configuration(policy, changed, template=changed)
        dependencies.validate_configuration(policy, rendered, template=changed)

        # Assert
        self.assertEqual(changed, rendered)
        self.assertEqual(3, rendered.count("default-days: 5"))

    def test_canonical_template_drift_cannot_silently_change_existing_targets(self):
        """Review must reconcile changes rather than silently overwrite current policy."""
        # Arrange
        policy = lines.load_policy()
        source = (ROOT / ".github/dependabot.yml").read_text(encoding="ascii")
        changed = source.replace("default-days: 3", "default-days: 5")

        # Act / Assert
        with self.assertRaises(dependencies.DependencyPolicyError):
            dependencies.validate_configuration(policy, source, template=changed)

    def test_missing_inactive_duplicate_or_foreign_targets_are_rejected(self):
        """A three-ecosystem declaration must cover exactly the active line set."""
        for mutation in ("missing", "inactive", "duplicate", "foreign", "boundary"):
            with self.subTest(mutation=mutation):
                # Arrange
                policy = policy_fixture()
                source = (ROOT / ".github/dependabot.yml").read_text(encoding="ascii")
                rendered = dependencies.render_configuration(policy, source)
                if mutation == "missing":
                    rendered = source
                elif mutation == "inactive":
                    policy["lines"][1]["state"] = "inactive"
                elif mutation == "duplicate":
                    rendered += "\n  - package-ecosystem: nuget\n    directory: /\n"
                elif mutation == "foreign":
                    rendered = rendered.replace("maintenance/10.x", "feature/unsafe")
                else:
                    rendered = rendered.replace('versions: [">=11.0.0-0", ">=11.0.0--"]',
                                                'versions: [">=12.0.0"]')

                # Act / Assert
                if mutation == "boundary":
                    self.assertIn('versions: [">=12.0.0"]', rendered)

                with self.assertRaises(dependencies.DependencyPolicyError):
                    dependencies.validate_configuration(policy, rendered)
