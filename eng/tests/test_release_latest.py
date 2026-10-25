"""Prove monotonic Latest ordering and fail-closed GitHub metadata handling."""

import json
import subprocess
import unittest
from unittest import mock

from eng.release import latest


def release(version: str = "10.6.0") -> dict:
    """Create detached immutable metadata without a real GitHub release."""
    return {
        "id": 42,
        "tag_name": f"v{version}",
        "draft": False,
        "prerelease": False,
        "immutable": True,
    }


def response(value: object, status: str = "200 OK", returncode: int = 0) -> subprocess.CompletedProcess:
    """Model gh --include so errors cannot be mistaken for an absent release."""
    return subprocess.CompletedProcess(
        ["gh"], returncode, f"HTTP/2.0 {status}\r\nX-Header: value\r\n\r\n{json.dumps(value)}", "")


class LatestOrderingTests(unittest.TestCase):
    """A release's stability does not entitle it to displace a higher version."""

    def test_first_stable_release_is_promoted(self):
        """An authoritative absence permits the first stable release."""
        # Arrange
        resolve = mock.Mock()

        # Act
        promote = latest.decide_latest("10.6.0", None, resolve)

        # Assert
        self.assertTrue(promote)
        resolve.assert_not_called()

    def test_first_preview_is_not_promoted(self):
        """No existing stable release does not make a preview Latest."""
        # Arrange
        resolve = mock.Mock()

        # Act
        promote = latest.decide_latest("10.6.0-rc.1", None, resolve)

        # Assert
        self.assertFalse(promote)
        resolve.assert_not_called()

    def test_stable_ordering_preserves_newer_majors_and_patches(self):
        """Out-of-order completion must not regress Latest within or across lines."""
        # Arrange
        candidates = ["9.9.99", "10.5.99", "10.6.1", "11.0.0", "11.0.0-rc.1"]
        observed = release()
        resolve = mock.Mock(return_value="a" * 40)

        # Act
        promotions = [latest.decide_latest(version, observed, resolve) for version in candidates]

        # Assert
        self.assertEqual([False, False, True, True, False], promotions)
        self.assertEqual([mock.call("v10.6.0")] * len(candidates), resolve.call_args_list)

    def test_equal_version_draft_conflicts_with_an_immutable_release(self):
        """The shell handles an exact immutable retry before consulting this draft decision."""
        # Arrange
        resolve = mock.Mock(return_value="a" * 40)

        # Act
        with self.assertRaisesRegex(latest.LatestError, "conflicts with this draft"):
            latest.decide_latest("10.6.0", release(), resolve)

        # Assert
        resolve.assert_called_once_with("v10.6.0")

    def test_malformed_latest_identity_is_rejected_before_tag_resolution(self):
        """Missing or coerced classification/identity fields cannot authorize promotion."""
        # Arrange
        mutations = [
            {"id": True}, {"id": 0}, {"id": "42"}, {"tag_name": "10.6.0"},
            {"tag_name": None}, {"draft": True}, {"draft": 0},
            {"prerelease": True}, {"immutable": False}, {"immutable": 1},
        ]
        resolve = mock.Mock(return_value="a" * 40)

        # Act
        for mutation in mutations:
            with self.subTest(mutation=mutation), self.assertRaises(latest.LatestError):
                latest.decide_latest("10.6.1", {**release(), **mutation}, resolve)

        # Assert
        resolve.assert_not_called()

    def test_noncanonical_latest_version_is_rejected(self):
        """Aliases, leading zeros and prerelease tags are not canonical stable Latest."""
        # Arrange
        versions = ["10.06.0", "10.6", "10.6.0+build", "10.6.0-RC.1", "10.6.0-rc.1"]
        resolve = mock.Mock(return_value="a" * 40)

        # Act
        for version in versions:
            with self.subTest(version=version), self.assertRaises(latest.LatestError):
                latest.decide_latest("10.6.1", release(version), resolve)

        # Assert
        resolve.assert_not_called()

    def test_source_commit_is_verified_even_when_newer_latest_prevents_promotion(self):
        """An older-major publication must not borrow unverifiable remote metadata."""
        # Arrange
        resolve = mock.Mock(return_value="not-a-commit")

        # Act
        with self.assertRaisesRegex(latest.LatestError, "source commit"):
            latest.decide_latest("9.9.9", release(), resolve)

        # Assert
        resolve.assert_called_once_with("v10.6.0")

    def test_shared_publisher_context_is_required(self):
        """Version-specific or merely truthy markers do not prove the shared boundary."""
        # Arrange
        environment = {
            "GITHUB_ACTIONS": "true",
            "SAFE_MIGRATIONS_PUBLICATION_LOCK_GROUP": latest.LOCK_GROUP,
            "SAFE_MIGRATIONS_PUBLICATION_LOCK_HELD": "1",
        }
        mutations = [
            {"GITHUB_ACTIONS": "false"},
            {"SAFE_MIGRATIONS_PUBLICATION_LOCK_GROUP": "version-10.6.0"},
            {"SAFE_MIGRATIONS_PUBLICATION_LOCK_HELD": "true"},
        ]

        # Act
        latest.require_publisher_lock(environment)
        for mutation in mutations:
            with self.subTest(mutation=mutation), self.assertRaises(latest.LatestError):
                latest.require_publisher_lock({**environment, **mutation})

        # Assert
        self.assertEqual("1", environment["SAFE_MIGRATIONS_PUBLICATION_LOCK_HELD"])


class LatestApiTests(unittest.TestCase):
    """Only real canonical HTTP/API identities may affect release ordering."""

    def test_real_not_found_is_the_only_absence(self):
        """The status line, not stderr wording, distinguishes first publication."""
        # Arrange
        client = latest.GitHubLatestClient("owner/repository")
        missing = response({"message": "Not Found"}, "404 Not Found", 1)

        # Act
        with mock.patch.object(latest.subprocess, "run", return_value=missing) as run:
            observed = client.get_latest()

        # Assert
        self.assertIsNone(observed)
        self.assertIn("/repos/owner/repository/releases/latest", run.call_args.args[0])
        self.assertEqual(60, run.call_args.kwargs["timeout"])

    def test_non404_and_transport_failures_are_not_absence(self):
        """An error message mentioning 404 must never authorize a first release."""
        # Arrange
        client = latest.GitHubLatestClient("owner/repository")
        failures = [
            response({"message": "contains HTTP 404"}, "403 Forbidden", 1),
            response({"message": "Not Found"}, "500 Server Error", 1),
            subprocess.CompletedProcess(["gh"], 1, "", "HTTP 404 permission denied"),
            response(release(), "200 OK", 1),
            response({}, "404 Not Found", 1),
            response({"message": "Not Found"}, "404 Not Found", 4),
        ]

        # Act
        for failure in failures:
            with self.subTest(failure=failure), mock.patch.object(
                latest.subprocess, "run", return_value=failure
            ), self.assertRaises(latest.LatestError):
                client.get_latest()

        # Assert
        self.assertEqual(6, len(failures))

    def test_malformed_and_duplicate_json_is_rejected(self):
        """Successful HTTP status cannot make ambiguous or non-object payloads valid."""
        # Arrange
        client = latest.GitHubLatestClient("owner/repository")
        bodies = ['{"id":1,"id":2}', "[]", "null", "not-json"]

        # Act
        for body in bodies:
            result = subprocess.CompletedProcess(["gh"], 0, f"HTTP/2.0 200 OK\n\n{body}", "")
            with self.subTest(body=body), mock.patch.object(
                latest.subprocess, "run", return_value=result
            ), self.assertRaises(latest.LatestError):
                client.get_latest()

        # Assert
        self.assertEqual(4, len(bodies))

    def test_annotated_tag_is_resolved_without_mutable_target_commitish(self):
        """Nested annotated tags resolve to one validated lowercase commit identity."""
        # Arrange
        client = latest.GitHubLatestClient("owner/repository")
        objects = [
            {"object": {"type": "tag", "sha": "a" * 40}},
            {"object": {"type": "tag", "sha": "b" * 40}},
            {"object": {"type": "commit", "sha": "c" * 40}},
        ]

        # Act
        with mock.patch.object(client, "read_json", side_effect=objects) as read:
            commit = client.resolve_tag("v10.6.0")

        # Assert
        self.assertEqual("c" * 40, commit)
        self.assertEqual([
            mock.call("git/ref/tags/v10.6.0"),
            mock.call(f"git/tags/{'a' * 40}"),
            mock.call(f"git/tags/{'b' * 40}"),
        ], read.call_args_list)

    def test_cyclic_tag_chain_is_rejected(self):
        """A cycle cannot turn a bounded API read into an unbounded release wait."""
        # Arrange
        client = latest.GitHubLatestClient("owner/repository")
        cyclic = {"object": {"type": "tag", "sha": "a" * 40}}

        # Act
        with mock.patch.object(client, "read_json", return_value=cyclic) as read:
            with self.assertRaisesRegex(latest.LatestError, "cyclic"):
                client.resolve_tag("v10.6.0")

        # Assert
        self.assertEqual(2, read.call_count)

    def test_lightweight_tag_is_rejected(self):
        """Published release identity must retain the annotated-tag contract."""
        # Arrange
        client = latest.GitHubLatestClient("owner/repository")
        lightweight = {"object": {"type": "commit", "sha": "a" * 40}}

        # Act
        with mock.patch.object(client, "read_json", return_value=lightweight):
            with self.assertRaisesRegex(latest.LatestError, "annotated"):
                client.resolve_tag("v10.6.0")

        # Assert
        self.assertEqual("commit", lightweight["object"]["type"])

    def test_tag_chain_limit_is_enforced(self):
        """Distinct nested tags cannot evade the finite read budget."""
        # Arrange
        client = latest.GitHubLatestClient("owner/repository")
        chain = [{"object": {"type": "tag", "sha": f"{value:040x}"}} for value in range(1, 10)]

        # Act
        with mock.patch.object(client, "read_json", side_effect=chain) as read:
            with self.assertRaisesRegex(latest.LatestError, "safety limit"):
                client.resolve_tag("v10.6.0")

        # Assert
        self.assertEqual(9, read.call_count)

    def test_invalid_commit_and_noncommit_tag_targets_are_rejected(self):
        """A tag response cannot substitute an alias, uppercase SHA or tree object."""
        # Arrange
        client = latest.GitHubLatestClient("owner/repository")
        reference = {"object": {"type": "tag", "sha": "a" * 40}}
        targets = [
            {"object": {"type": "commit", "sha": "main"}},
            {"object": {"type": "commit", "sha": "A" * 40}},
            {"object": {"type": "tree", "sha": "b" * 40}},
            {"object": None},
        ]

        # Act
        for target in targets:
            with self.subTest(target=target), mock.patch.object(
                client, "read_json", side_effect=[reference, target]
            ), self.assertRaises(latest.LatestError):
                client.resolve_tag("v10.6.0")

        # Assert
        self.assertEqual(4, len(targets))

    def test_transport_timeout_is_typed_and_sanitized(self):
        """CLI exception payloads do not become public release diagnostic data."""
        # Arrange
        client = latest.GitHubLatestClient("owner/repository")
        failure = subprocess.TimeoutExpired("gh", 60, output="sensitive response")

        # Act
        with mock.patch.object(latest.subprocess, "run", side_effect=failure):
            with self.assertRaises(latest.LatestError) as caught:
                client.get_latest()

        # Assert
        self.assertNotIn("sensitive", str(caught.exception))


if __name__ == "__main__":
    unittest.main()
