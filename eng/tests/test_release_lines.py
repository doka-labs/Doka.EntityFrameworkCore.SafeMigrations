"""Behavioral contracts for exact, independently qualified release lines."""

import json
import unittest
from pathlib import Path

from eng.release import lines


def policy():
    """Return independent policy data, never mutate the checked-in authority."""
    return lines.load_policy()


class ReleaseLineTests(unittest.TestCase):
    """Exercise positive identities and fail-closed aliases/lifecycle changes."""

    def test_initial_policy_keeps_only_main_active(self):
        # Arrange
        value = policy()

        # Act
        active = [entry["sourceRef"] for entry in value["lines"] if entry["state"] == "active"]

        # Assert
        self.assertEqual(["refs/heads/main"], active)
        self.assertEqual("inactive", value["lines"][1]["state"])

    def test_inactive_line_is_inspectable_for_preparation_pr(self):
        # Arrange
        value = policy()

        # Act
        selected = lines.select_line(value, "refs/heads/maintenance/10.x", purpose="inspect")

        # Assert
        self.assertEqual("inactive", selected["state"])

    def test_inactive_line_cannot_qualify(self):
        # Arrange
        value = policy()

        # Act / Assert
        with self.assertRaises(lines.LinePolicyError):
            lines.select_line(value, "refs/heads/maintenance/10.x", "10.6.0")

    def test_prepared_line_qualifies_without_publication(self):
        # Arrange
        value = policy()
        value["lines"][1]["state"] = "prepared"

        # Act
        frozen = lines.freeze_line(value, "refs/heads/maintenance/10.x", "10.6.0")

        # Assert
        self.assertEqual("prepared", frozen["stateAtQualification"])
        with self.assertRaises(lines.LinePolicyError):
            lines.select_line(value, frozen["sourceRef"], "10.6.0", purpose="publication")

    def test_two_active_owners_of_same_major_are_rejected(self):
        # Arrange
        value = policy()
        value["lines"][1]["state"] = "active"

        # Act / Assert
        with self.assertRaises(lines.LinePolicyError):
            lines.validate_policy(value)

    def test_duplicate_json_properties_are_rejected(self):
        # Arrange
        encoded = json.dumps(policy()).replace('"schemaVersion": 1', '"schemaVersion": 1, "schemaVersion": 1')

        # Act / Assert
        with self.assertRaises(lines.LinePolicyError):
            lines.parse_policy(encoded)

    def test_wildcard_line_ref_is_rejected(self):
        # Arrange
        value = policy()
        value["lines"][1]["sourceRef"] = "refs/heads/maintenance/*"

        # Act / Assert
        with self.assertRaises(lines.LinePolicyError):
            lines.validate_policy(value)

    def test_snapshot_remains_verifiable_after_deactivation(self):
        # Arrange
        value = policy()
        frozen = lines.freeze_line(value, "refs/heads/main", "10.6.0")
        value["lines"][0]["state"] = "inactive"

        # Act
        result = lines.validate_snapshot(frozen, source_ref="refs/heads/main", version="10.6.0")

        # Assert
        self.assertEqual(frozen, result)
        with self.assertRaises(lines.LinePolicyError):
            lines.select_line(value, "refs/heads/main", "10.6.0", purpose="publication")

    def test_snapshot_mutation_does_not_alias_policy(self):
        # Arrange
        value = policy()
        frozen = lines.freeze_line(value, "refs/heads/main", "10.6.0")

        # Act
        frozen["sourceRef"] = "refs/heads/other"

        # Assert
        self.assertEqual("refs/heads/main", value["lines"][0]["sourceRef"])
        with self.assertRaises(lines.LinePolicyError):
            lines.validate_snapshot(frozen)

    def test_wrong_major_is_rejected(self):
        # Arrange
        value = policy()

        # Act / Assert
        with self.assertRaises(lines.LinePolicyError):
            lines.select_line(value, "refs/heads/main", "12.0.0")

    def test_semver_precedence_preserves_patch_and_numeric_prerelease_order(self):
        # Arrange
        pairs = [("10.6.2", "10.6.10"), ("10.6.0-rc.2", "10.6.0-rc.10"),
                 ("10.6.0-rc.10", "10.6.0"), ("10.99.0", "12.0.0")]

        # Act
        results = [lines.compare_versions(left, right) for left, right in pairs]

        # Assert
        self.assertEqual([-1] * len(pairs), results)

    def test_noncanonical_versions_are_rejected(self):
        # Arrange
        values = ["v10.6.0", "10.06.0", "10.6.0+build", "10.6.0-RC.1", "10.6.0-rc.01",
                  "10.6.0--bad", "10.6.0-bad-", "10.6.0..", None]

        # Act / Assert
        for value in values:
            with self.subTest(value=value), self.assertRaises(lines.LinePolicyError):
                lines.parse_version(value)

    def test_real_branch_toolchain_is_net10(self):
        # Arrange
        root = Path(__file__).resolve().parents[2]
        frozen = lines.freeze_line(policy(), "refs/heads/main", "10.6.0")

        # Act
        result = lines.inspect_toolchain(root, frozen)

        # Assert
        self.assertEqual("net10.0", result["targetFramework"])
        self.assertEqual("10.0.402", result["approvedDotnetSdk"])

    def test_foreign_toolchain_major_is_rejected(self):
        # Arrange
        frozen = lines.freeze_line(policy(), "refs/heads/main", "10.6.0")
        toolchain = {"approvedDotnetSdk": "12.0.100", "targetFramework": "net12.0",
                     "efCoreVersion": "[12.0.0,12.1.0)", "testRunner": "VSTest", "upstreamStage": "ga"}

        # Act / Assert
        with self.assertRaises(lines.LinePolicyError):
            lines.validate_toolchain(toolchain, frozen)

    def test_mixed_resolved_ef_versions_are_rejected(self):
        # Arrange
        root = Path(__file__).resolve().parents[2]
        frozen = lines.freeze_line(policy(), "refs/heads/main", "10.6.0")
        toolchain = lines.inspect_toolchain(root, frozen)
        graph = {"Microsoft.EntityFrameworkCore.Design": "10.0.13",
                 "Microsoft.EntityFrameworkCore.Relational": "10.0.12"}

        # Act / Assert
        with self.assertRaises(lines.LinePolicyError):
            lines.validate_toolchain(toolchain, frozen, resolved_packages=graph)

    def test_coherent_resolved_ef_graph_matches_branch_bounds(self):
        # Arrange
        root = Path(__file__).resolve().parents[2]
        frozen = lines.freeze_line(policy(), "refs/heads/main", "10.6.0")
        toolchain = lines.inspect_toolchain(root, frozen)
        graph = {"Microsoft.EntityFrameworkCore.Design": "10.0.13",
                 "Microsoft.EntityFrameworkCore.Relational": "10.0.13"}

        # Act
        observed = lines.validate_toolchain(toolchain, frozen, resolved_packages=graph)

        # Assert
        self.assertEqual(toolchain, observed)

    def test_schema_bool_is_not_integer(self):
        # Arrange
        value = policy()
        value["schemaVersion"] = True

        # Act / Assert
        with self.assertRaises(lines.LinePolicyError):
            lines.validate_policy(value)

    def test_unknown_properties_are_rejected(self):
        # Arrange
        value = policy()
        value["allowBypass"] = True

        # Act / Assert
        with self.assertRaises(lines.LinePolicyError):
            lines.validate_policy(value)


if __name__ == "__main__":
    unittest.main()
