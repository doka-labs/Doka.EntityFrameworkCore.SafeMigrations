"""Behavioral contracts for exact, independently qualified release lines."""

import copy
import json
import tempfile
import unittest
from pathlib import Path

from eng.release import dependencies, lines


ROOT = Path(__file__).resolve().parents[2]


def policy():
    """Return the fixed main/10 scenario independently of checked-in activation."""
    return {"schemaVersion": 1, "revision": "test-main-10", "repository":
            "doka-labs/Doka.EntityFrameworkCore.SafeMigrations", "authorityRef": "refs/heads/main",
            "lines": [{"sourceRef": "refs/heads/main", "role": "main", "packageMajor": 10,
                       "channel": "stable-and-prerelease", "upstreamStage": "ga", "state": "active",
                       "workflowPath": lines.WORKFLOW_PATH, "publisherEnvironment": "nuget"},
                      {"sourceRef": "refs/heads/maintenance/10.x", "role": "maintenance", "packageMajor": 10,
                       "channel": "stable-and-prerelease", "upstreamStage": "ga", "state": "inactive",
                       "workflowPath": lines.WORKFLOW_PATH, "publisherEnvironment": "nuget"}]}


def toolchain(major=10):
    """Return a synthetic coherent GA toolchain, not the current source patches."""
    return {"approvedDotnetSdk": f"{major}.0.100", "targetFramework": f"net{major}.0",
            "efCoreVersion": f"[{major}.0.0,{major}.1.0)", "testRunner": "VSTest", "upstreamStage": "ga"}


def write_toolchain(root, major=10):
    """Own minimal source contracts and five product locks for the real inspector."""
    value = toolchain(major)
    (root / "global.json").write_text(json.dumps({"sdk": {
        "version": value["approvedDotnetSdk"], "rollForward": "disable", "allowPrerelease": False}}),
        encoding="ascii")
    for name, key, path in (("TargetFramework", "targetFramework", "Directory.Build.props"),
                            ("EfCorePackageVersion", "efCoreVersion", "Directory.Packages.props")):
        (root / path).write_text(f"<Project><PropertyGroup><{name}>{value[key]}</{name}>"
                                 "</PropertyGroup></Project>\n", encoding="ascii")

    packages = {name: {"resolved": f"{major}.0.15"} for name in (
        "Microsoft.EntityFrameworkCore.Design", "Microsoft.EntityFrameworkCore.Relational")}
    for name in ("Core", "MySql", "PostgreSql", "Sqlite", "SqlServer"):
        project = root / "src" / name
        project.mkdir(parents=True)
        (project / "packages.lock.json").write_text(json.dumps({
            "dependencies": {value["targetFramework"]: packages}}), encoding="ascii")


def inspect_declared_toolchains(root, actual, version):
    """Inspect source contracts without turning local lifecycle data into permission."""
    probe = copy.deepcopy(lines.validate_policy(actual))
    matching = [entry for entry in probe["lines"] if entry["packageMajor"] == lines.parse_version(version).major]
    for entry in matching:
        # WHY: Static preparation CI is legal for inactive lines. This private
        # test copy only creates inspector inputs, never hosted release authority.
        if entry["state"] == "inactive":
            entry["state"] = "prepared"

    return [lines.inspect_toolchain(root, lines.freeze_line(probe, entry["sourceRef"], version))
            for entry in matching]


class ReleaseLineTests(unittest.TestCase):
    """Exercise positive identities and fail-closed aliases/lifecycle changes."""

    def test_single_cutover_policy_assigns_distinct_majors_and_six_dependency_targets(self):
        """The checked-in target transfers both majors together without an intermediate authority PR."""
        # Arrange
        actual = lines.load_policy()
        template = (ROOT / ".github/dependabot-template.yml").read_text(encoding="ascii")

        # Act
        rendered = dependencies.render_configuration(actual, template)
        selected = [lines.select_line(actual, ref, version) for ref, version in
                    (("refs/heads/main", "11.0.0"), ("refs/heads/maintenance/10.x", "10.7.4-rc.1"))]

        # Assert
        self.assertEqual([(11, "active"), (10, "active")],
                         [(entry["packageMajor"], entry["state"]) for entry in selected])
        self.assertEqual(2, len(actual["lines"]))
        self.assertEqual({(ecosystem, branch) for ecosystem in dependencies.ECOSYSTEMS
                          for branch in ("main", "maintenance/10.x")},
                         {(ecosystem, branch) for ecosystem, branch, _ in dependencies.entries(rendered)})

    def test_single_cutover_does_not_allow_cross_major_qualification(self):
        """A paired active policy must never let either branch borrow the other framework major."""
        for ref, version in (("refs/heads/main", "10.7.4-rc.1"),
                             ("refs/heads/maintenance/10.x", "11.0.0")):
            with self.subTest(ref=ref, version=version):
                # Arrange
                actual = lines.load_policy()

                # Act / Assert
                with self.assertRaises(lines.LinePolicyError):
                    lines.freeze_line(actual, ref, version)

    def test_initial_scenario_keeps_only_main_active(self):
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

    def test_checked_in_policy_and_toolchain_match_actual_source(self):
        """Real source remains checked without pinning tests to today's lifecycle."""
        # Arrange
        root = Path(__file__).resolve().parents[2]
        actual = lines.load_policy()
        version = lines._property(root / "src/Directory.Build.props", "VersionPrefix")
        major = lines.parse_version(version).major

        # Act
        results = inspect_declared_toolchains(root, actual, version)

        # Assert
        self.assertTrue(results, "Actual source requires a registered own-major line.")
        for result in results:
            self.assertEqual(f"net{major}.0", result["targetFramework"])
            self.assertEqual(lines.read_json(root / "global.json")["sdk"]["version"],
                             result["approvedDotnetSdk"])

    def test_inactive_preparation_inspects_own_source_without_changing_policy(self):
        # Arrange
        value = policy()
        value["lines"][0]["packageMajor"] = 11
        original = copy.deepcopy(value)
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        root = Path(directory.name)
        write_toolchain(root)

        # Act
        observed = inspect_declared_toolchains(root, value, "10.7.4")

        # Assert
        self.assertEqual([toolchain()], observed)
        self.assertEqual(original, value)

    def test_inactive_preparation_never_qualifies_or_publishes_under_future_main(self):
        for purpose in ("qualification", "publication"):
            with self.subTest(purpose=purpose):
                # Arrange
                value = policy()
                value["lines"][0]["packageMajor"] = 11

                # Act / Assert
                with self.assertRaises(lines.LinePolicyError):
                    lines.select_line(value, "refs/heads/maintenance/10.x", "10.7.4", purpose=purpose)

    def test_future_main_and_maintenance_keep_independent_toolchains(self):
        # Arrange
        value = policy()
        value["lines"][0]["packageMajor"] = 11
        value["lines"][1]["state"] = "active"

        # Act
        observed = [lines.validate_toolchain(toolchain(major), lines.freeze_line(value, ref, version))
                    for major, ref, version in ((11, "refs/heads/main", "11.0.0"),
                                                (10, "refs/heads/maintenance/10.x", "10.7.4"))]

        # Assert
        self.assertEqual(["net11.0", "net10.0"], [item["targetFramework"] for item in observed])

    def test_main_toolchain_cannot_substitute_for_maintenance(self):
        # Arrange
        value = policy()
        value["lines"][0]["packageMajor"] = 11
        value["lines"][1]["state"] = "active"
        frozen = lines.freeze_line(value, "refs/heads/maintenance/10.x", "10.7.4")

        # Act / Assert
        with self.assertRaises(lines.LinePolicyError):
            lines.validate_toolchain(toolchain(11), frozen)

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
        frozen = lines.freeze_line(policy(), "refs/heads/main", "10.6.0")
        branch_toolchain = toolchain()
        graph = {"Microsoft.EntityFrameworkCore.Design": "10.0.15",
                 "Microsoft.EntityFrameworkCore.Relational": "10.0.14"}

        # Act / Assert
        with self.assertRaises(lines.LinePolicyError):
            lines.validate_toolchain(branch_toolchain, frozen, resolved_packages=graph)

    def test_coherent_resolved_ef_graph_matches_branch_bounds(self):
        # Arrange
        frozen = lines.freeze_line(policy(), "refs/heads/main", "10.6.0")
        branch_toolchain = toolchain()
        graph = {"Microsoft.EntityFrameworkCore.Design": "10.0.15",
                 "Microsoft.EntityFrameworkCore.Relational": "10.0.15"}

        # Act
        observed = lines.validate_toolchain(branch_toolchain, frozen, resolved_packages=graph)

        # Assert
        self.assertEqual(branch_toolchain, observed)

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
