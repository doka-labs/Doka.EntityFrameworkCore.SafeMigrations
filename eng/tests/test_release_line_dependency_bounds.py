"""Guard generated NuGet ceilings against stable and prerelease major upgrades."""

import copy
import itertools
import json
import re
import unittest
from pathlib import Path

from eng.release import dependencies, lines


ROOT = Path(__file__).resolve().parents[2]


def target_policy(role: str, major: int) -> tuple[dict, str]:
    """Prepare synthetic active targets without activating any repository line."""
    policy = copy.deepcopy(lines.load_policy())
    policy["lines"][0]["packageMajor"] = major + 1
    entry = copy.deepcopy(policy["lines"][1])
    entry.update(sourceRef=f"refs/heads/{role}/{major}.x", role=role, packageMajor=major,
                 channel="prerelease" if role == "preview" else "stable-and-prerelease",
                 upstreamStage="rc" if role == "preview" else "ga", state="active")
    policy["lines"] = [policy["lines"][0], entry]

    return lines.validate_policy(policy), f"{role}/{major}.x"


def framework_requirements(rendered: str, branch: str) -> dict[str, list[str]]:
    """Read actual generated ignore entries rather than constructing test bounds."""
    block = next(body for ecosystem, target, body in dependencies.entries(rendered)
                 if ecosystem == "nuget" and target == branch)
    entries = re.findall(r'^      - dependency-name: "([^"\n]+)"\n        versions: (\[[^\n]+\])$',
                         block, re.MULTILINE)

    return {name: json.loads(requirements) for name, requirements in entries}


def nuget_compare(left: str, right: str) -> int:
    """Model Dependabot's verified NuGet ordering, not RubyGems prerelease order.

    WHY: Upstream Nuget::Version compares numeric release tuples, then integer
    identifier pairs or case-insensitive strings. A leading hyphen therefore
    sorts below numeric zero. Actual upstream Version/Requirement execution is
    retained separately; this dependency-free oracle pins those semantics.
    """
    first, _, first_pre = left.partition("-")
    second, _, second_pre = right.partition("-")
    release = tuple(map(int, first.split("."))), tuple(map(int, second.split(".")))
    if release[0] != release[1]:
        return (release[0] > release[1]) - (release[0] < release[1])

    if not first_pre or not second_pre:
        return int(not first_pre) - int(not second_pre)

    for first_part, second_part in itertools.zip_longest(first_pre.split("."), second_pre.split(".")):
        if first_part is None or second_part is None:
            return int(first_part is not None) - int(second_part is not None)

        if first_part.isdigit() and second_part.isdigit():
            first_value, second_value = int(first_part), int(second_part)
        else:
            first_value, second_value = first_part.upper(), second_part.upper()

        if first_value != second_value:
            return (first_value > second_value) - (first_value < second_value)

    return 0


def ignored(requirements: list[str], version: str) -> bool:
    """Evaluate the generated OR-list with the verified NuGet >= operator."""
    floors = []
    for requirement in requirements:
        match = re.fullmatch(r">=(.+)", requirement)
        if match is None:
            raise AssertionError("Expected a generated canonical lower bound.")

        floors.append(match[1])

    return any(nuget_compare(version, floor) >= 0 for floor in floors)


class DependencyPrereleaseBoundTests(unittest.TestCase):
    """Every generated framework target must cover the whole following major."""

    def test_next_major_prereleases_and_stable_versions_are_ignored_for_every_framework(self):
        for role, major in itertools.product(("maintenance", "preview"), (10, 11, 12)):
            with self.subTest(role=role, major=major):
                # Arrange
                policy, branch = target_policy(role, major)
                source = (ROOT / ".github/dependabot.yml").read_text(encoding="ascii")
                next_major = major + 1
                versions = [f"{next_major}.0.0{suffix}" for suffix in
                            ("-0", "-0.0", "-alpha", "-BETA.1", "-rc.1", "--", "--x", "")]
                versions += [f"{next_major}.0.1--x", f"{next_major + 1}.0.0-0"]

                # Act
                rendered = dependencies.render_configuration(policy, source)
                actual = framework_requirements(rendered, branch)

                # Assert
                self.assertEqual(set(dependencies.FRAMEWORK_PACKAGES), set(actual))
                for package, requirements in actual.items():
                    self.assertEqual([f">={next_major}.0.0-0", f">={next_major}.0.0--"], requirements)
                    for version in versions:
                        self.assertTrue(ignored(requirements, version), (package, requirements, version))

    def test_current_major_prereleases_stable_and_servicing_remain_available(self):
        for role, major in itertools.product(("maintenance", "preview"), (10, 11, 12)):
            with self.subTest(role=role, major=major):
                # Arrange
                policy, branch = target_policy(role, major)
                source = (ROOT / ".github/dependabot.yml").read_text(encoding="ascii")
                versions = [f"{major}.0.0-0", f"{major}.0.0-alpha", f"{major}.0.0-rc.2",
                            f"{major}.0.0", f"{major}.0.13", f"{major}.999.999"]

                # Act
                actual = framework_requirements(dependencies.render_configuration(policy, source), branch)

                # Assert
                self.assertEqual(4, len(actual))
                for package, requirements in actual.items():
                    for version in versions:
                        self.assertFalse(ignored(requirements, version), (package, requirements, version))

    def test_gate_rejects_old_stable_only_ceiling_and_incomplete_prerelease_floor(self):
        for replacement in ('[">=12.0.0"]', '[">=12.0.0-0"]', '[">=12.0.0--"]'):
            with self.subTest(replacement=replacement):
                # Arrange
                policy, _ = target_policy("preview", 11)
                source = (ROOT / ".github/dependabot.yml").read_text(encoding="ascii")
                rendered = dependencies.render_configuration(policy, source)
                tampered = rendered.replace('[">=12.0.0-0", ">=12.0.0--"]', replacement, 1)

                # Act / Assert
                with self.assertRaises(dependencies.DependencyPolicyError):
                    dependencies.validate_configuration(policy, tampered)

    def test_current_configuration_remains_identical_and_transport_versions_are_not_capped(self):
        # Arrange
        source = (ROOT / ".github/dependabot.yml").read_text(encoding="ascii")
        policy, branch = target_policy("maintenance", 10)

        # Act
        current = dependencies.render_configuration(lines.load_policy(), source)
        actual = framework_requirements(dependencies.render_configuration(policy, source), branch)

        # Assert
        self.assertEqual(source, current)
        self.assertNotIn("MySqlConnector", actual)
        self.assertNotIn("Microsoft.Data.SqlClient", actual)
        self.assertNotIn("System.Security.Cryptography.Pkcs", actual)

    def test_nuget_oracle_distinguishes_stable_and_earliest_prerelease_floors(self):
        # Arrange
        version = "12.0.0--x"

        # Act
        stable_only = ignored([">=12.0.0"], "12.0.0-rc.1")
        canonical_only = ignored([">=12.0.0-0"], version)
        paired = ignored([">=12.0.0-0", ">=12.0.0--"], version)

        # Assert
        self.assertFalse(stable_only)
        self.assertFalse(canonical_only)
        self.assertTrue(paired)


if __name__ == "__main__":
    unittest.main()
