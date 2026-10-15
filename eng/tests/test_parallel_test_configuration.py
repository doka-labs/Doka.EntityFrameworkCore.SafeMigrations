"""Guard the shared repository configuration consumed by Rider and CLI test runs."""

import json
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
CONFIGURATION = REPOSITORY_ROOT / "tests/Shared/xunit.runner.json"
SETTINGS_KEY = "/Default/Environment/UnitTesting/ParallelProcessesCount/@EntryValue"


def require_parallel_configuration(configuration: dict[str, object]) -> None:
    """Reject misspelled or disabled scheduling options instead of accepting an ignored knob."""
    if configuration.get("parallelizeAssembly") is not True:
        raise ValueError("Assembly participation must use xUnit's singular parallelizeAssembly key.")

    if configuration.get("parallelizeTestCollections") is not True:
        raise ValueError("Independent test collections must remain parallel.")

    if configuration.get("parallelAlgorithm") != "conservative":
        raise ValueError("The bounded conservative scheduling contract must remain explicit.")

    if "parallelizeAssemblies" in configuration:
        raise ValueError("The plural parallelizeAssemblies key is not a runner JSON setting.")


class ParallelTestConfigurationTests(unittest.TestCase):
    """Verify configuration spelling, output propagation and the shared IDE setting."""

    def test_shared_runner_configuration_enables_both_levels(self) -> None:
        """Read the actual copied configuration rather than an independent positive fixture."""
        configuration = json.loads(CONFIGURATION.read_text(encoding="ascii"))

        result = require_parallel_configuration(configuration)

        self.assertIsNone(result)
        self.assertNotIn("maxParallelThreads", configuration)

    def test_plural_assembly_option_is_rejected(self) -> None:
        """Prove the guard detects the tempting but silently ignored plural spelling."""
        configuration = {
            "parallelizeAssemblies": True,
            "parallelizeTestCollections": True,
            "parallelAlgorithm": "conservative",
        }

        with self.assertRaisesRegex(ValueError, "singular"):
            require_parallel_configuration(configuration)

    def test_disabled_collection_scheduling_is_rejected(self) -> None:
        """Prevent configuration from reinstating blanket intra-assembly serialization."""
        configuration = json.loads(CONFIGURATION.read_text(encoding="ascii"))
        configuration["parallelizeTestCollections"] = False

        with self.assertRaisesRegex(ValueError, "collections"):
            require_parallel_configuration(configuration)

    def test_correct_key_does_not_hide_unknown_plural_key(self) -> None:
        """Keep an unused declaration visible even if a second, correct key is present."""
        configuration = json.loads(CONFIGURATION.read_text(encoding="ascii"))
        configuration["parallelizeAssemblies"] = True

        with self.assertRaisesRegex(ValueError, "plural"):
            require_parallel_configuration(configuration)

    def test_all_five_projects_inherit_shared_output_configuration(self) -> None:
        """Pin the common test-directory import that places configuration beside each assembly."""
        properties = ET.parse(REPOSITORY_ROOT / "tests/Directory.Build.props").getroot()
        projects = sorted((REPOSITORY_ROOT / "tests").glob("*/*.csproj"))

        entries = properties.findall("./ItemGroup/None") + properties.findall("./ItemGroup/Content")
        configuration_entries = [
            entry for entry in entries if entry.attrib.get("Include", "").endswith("Shared/xunit.runner.json")
        ]

        self.assertEqual(5, len(projects))
        self.assertEqual(1, len(configuration_entries))
        self.assertEqual("xunit.runner.json", configuration_entries[0].attrib.get("Link"))
        self.assertEqual("PreserveNewest", configuration_entries[0].attrib.get("CopyToOutputDirectory"))
        for project in projects:
            self.assertEqual("Microsoft.NET.Sdk", ET.parse(project).getroot().attrib.get("Sdk"))

    def test_shared_solution_setting_allows_every_test_assembly(self) -> None:
        """Use the tracked solution layer, not an untracked personal settings file."""
        settings = ET.parse(REPOSITORY_ROOT / "Doka.EntityFrameworkCore.SafeMigrations.sln.DotSettings").getroot()
        key_name = "{http://schemas.microsoft.com/winfx/2006/xaml}Key"
        projects = list((REPOSITORY_ROOT / "tests").glob("*/*.csproj"))

        matching = [entry for entry in settings if entry.attrib.get(key_name) == SETTINGS_KEY]

        self.assertEqual(1, len(matching))
        self.assertGreaterEqual(int(matching[0].text or "0"), len(projects))


if __name__ == "__main__":
    unittest.main()
