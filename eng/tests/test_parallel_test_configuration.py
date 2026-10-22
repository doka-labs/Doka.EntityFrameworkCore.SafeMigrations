"""Guard the shared repository configuration consumed by Rider and CLI test runs."""

import fnmatch
import json
import re
import shlex
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path, PurePosixPath


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
CONFIGURATION = REPOSITORY_ROOT / "tests/Shared/xunit.runner.json"
SETTINGS_KEY = "/Default/Environment/UnitTesting/ParallelProcessesCount/@EntryValue"
ENGINEERING_GUARD = "eng/tests/test_parallel_test_configuration.py"
NESTING_GROUP = "PartialFamilyNesting"


def partial_nesting(project: ET.Element, compile_paths: set[str]) -> dict[str, str]:
    """Validate bounded navigation metadata without replacing MSBuild's evaluated-item check."""
    result = {}

    for group in project.findall(f"./ItemGroup[@Label='{NESTING_GROUP}']"):
        for item in group:
            if item.tag != "Compile" or set(item.attrib) != {"Update"}:
                raise ValueError("Partial nesting must use Compile Update only.")

            metadata = list(item)
            if len(metadata) != 1 or metadata[0].tag != "DependentUpon":
                raise ValueError("Partial nesting must set only DependentUpon metadata.")

            parent = metadata[0].text or ""
            if not parent.endswith(".cs") or "/" in parent or "\\" in parent:
                raise ValueError("Partial nesting requires a real feature-local parent filename.")

            condition = metadata[0].attrib.get("Condition")
            excluded = set()
            if metadata[0].attrib:
                terms = (condition or "").split(" and ")
                exclusions = [re.fullmatch(r"'%\(Filename\)' != '([A-Za-z0-9_.]+)'", term) for term in terms]
                if set(metadata[0].attrib) != {"Condition"} or not all(exclusions):
                    raise ValueError("Only exact filename exclusions may condition partial metadata.")

                excluded = {match.group(1) for match in exclusions if match}

            matches = set()

            for expression in item.attrib["Update"].replace("\\", "/").split(";"):
                expression = expression.strip()
                pattern = PurePosixPath(expression)
                if not expression or ".." in pattern.parts or "**" in expression:
                    raise ValueError("Partial nesting must remain within one explicit source folder.")

                # WHY: MSBuild's single-star filename pattern is folder-local;
                # fnmatch alone would also match separators and hide a remote attachment.
                matches.update(path for path in compile_paths
                               if PurePosixPath(path).parent == pattern.parent
                               and fnmatch.fnmatchcase(PurePosixPath(path).name, pattern.name))

            if excluded:
                matches = {path for path in matches if PurePosixPath(path).stem not in excluded}

            if not matches:
                raise ValueError("Partial nesting must have a current source consumer.")

            for path in matches:
                parent_path = str(PurePosixPath(path).parent / parent)
                if parent_path == path:
                    raise ValueError("A partial file cannot depend upon itself.")

                if parent_path not in compile_paths:
                    raise ValueError("Partial nesting requires an existing compiled local parent.")

                if path in result:
                    raise ValueError("A physical partial file can receive only one parent.")

                result[path] = parent

    return result


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


def require_engineering_guard_invocation(workflow: str) -> None:
    """Require an executable guard command inside the existing engineering-script step."""
    step_indent = None
    run_indent = None

    for line in workflow.splitlines():
        stripped = line.strip()
        indent = len(line) - len(line.lstrip())

        if not stripped or stripped.startswith("#"):
            continue

        if stripped == "- name: Verify engineering scripts":
            step_indent = indent
            continue

        if step_indent is None:
            continue

        if indent <= step_indent:
            break

        if run_indent is None:
            if stripped == "run: |":
                run_indent = indent

            continue

        if indent <= run_indent:
            break

        # WHY: Commented commands and echo arguments must not satisfy executable workflow wiring.
        command = shlex.split(stripped, comments=True)
        if command[:3] == ["python3", "-m", "unittest"] and ENGINEERING_GUARD in command[3:]:
            return

    raise ValueError("The engineering step must execute the parallel test configuration guard.")


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

    def test_quality_workflow_executes_parallel_configuration_guard(self) -> None:
        """Keep the repository guard in the actual blocking engineering step."""
        workflow = (REPOSITORY_ROOT / ".github/workflows/quality-gates.yml").read_text(encoding="ascii")

        result = require_engineering_guard_invocation(workflow)

        self.assertIsNone(result)

    def test_missing_guard_invocation_is_rejected(self) -> None:
        """Detect removal instead of relying only on a matching positive fixture."""
        workflow = """\
      - name: Verify engineering scripts
        run: |
          python3 -m unittest eng/tests/test_verify_coverage.py -v
      - name: Build
        run: dotnet build
"""

        with self.assertRaisesRegex(ValueError, "must execute"):
            require_engineering_guard_invocation(workflow)

    def test_comment_and_echo_do_not_invoke_guard(self) -> None:
        """Reject inert text even when it contains the exact expected command."""
        workflow = f"""\
      - name: Verify engineering scripts
        run: |
          # python3 -m unittest {ENGINEERING_GUARD} -v
          echo python3 -m unittest {ENGINEERING_GUARD} -v
"""

        with self.assertRaisesRegex(ValueError, "must execute"):
            require_engineering_guard_invocation(workflow)

    def test_guard_in_another_step_is_rejected(self) -> None:
        """Do not accept a call outside the step responsible for engineering checks."""
        workflow = f"""\
      - name: Verify engineering scripts
        run: |
          python3 -m unittest eng/tests/test_verify_coverage.py -v
      - name: Not the engineering gate
        run: |
          python3 -m unittest {ENGINEERING_GUARD} -v
"""

        with self.assertRaisesRegex(ValueError, "must execute"):
            require_engineering_guard_invocation(workflow)

    def test_partial_nesting_metadata_has_real_local_consumers(self) -> None:
        """Inspect repository-owned rules without adding or removing any Compile item."""
        nested_projects = set()

        for area in ("src", "tests", "benchmarks"):
            for project_path in (REPOSITORY_ROOT / area).glob("*/*.csproj"):
                compile_paths = {
                    path.relative_to(project_path.parent).as_posix()
                    for path in project_path.parent.rglob("*.cs")
                    if not {"bin", "obj"}.intersection(path.relative_to(project_path.parent).parts)
                }
                mapping = partial_nesting(ET.parse(project_path).getroot(), compile_paths)
                if mapping:
                    nested_projects.add(project_path.parent.name)

        expected = {
            "Doka.EntityFrameworkCore.SafeMigrations",
            "Doka.EntityFrameworkCore.SafeMigrations.MySql",
            "Doka.EntityFrameworkCore.SafeMigrations.PostgreSql",
            "Doka.EntityFrameworkCore.SafeMigrations.Sqlite",
            "Doka.EntityFrameworkCore.SafeMigrations.SqlServer",
            "Doka.EntityFrameworkCore.SafeMigrations.Tests",
            "Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests",
            "Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests",
            "Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests",
            "Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests",
            "Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks",
        }

        self.assertEqual(expected, nested_projects)

    def test_partial_wildcard_supports_future_suffix_but_not_unrelated_prefix(self) -> None:
        """A new responsibility partial follows the rule without capturing a different family."""
        project = ET.fromstring("""\
<Project><ItemGroup Label="PartialFamilyNesting">
  <Compile Update="Feature/Family.*.cs"><DependentUpon>Family.cs</DependentUpon></Compile>
</ItemGroup></Project>
""")
        compile_paths = {"Feature/Family.cs", "Feature/Family.Future.cs", "Feature/FamilyExtra.Future.cs"}

        mapping = partial_nesting(project, compile_paths)

        self.assertEqual({"Feature/Family.Future.cs": "Family.cs"}, mapping)

    def test_partial_nesting_rejects_compile_include(self) -> None:
        """Navigation metadata must never duplicate an SDK implicit source item."""
        project = ET.fromstring("""\
<Project><ItemGroup Label="PartialFamilyNesting">
  <Compile Include="Family.Part.cs"><DependentUpon>Family.cs</DependentUpon></Compile>
</ItemGroup></Project>
""")

        with self.assertRaisesRegex(ValueError, "Update only"):
            partial_nesting(project, {"Family.cs", "Family.Part.cs"})

    def test_feature_local_suffix_anchor_excludes_itself(self) -> None:
        """One local subgroup can retain legacy suffixes and accept new same-family parts."""
        project = ET.fromstring("""\
<Project><ItemGroup Label="PartialFamilyNesting">
  <Compile Update="Feature/Family.*.cs">
    <DependentUpon Condition="'%(Filename)' != 'Family.Columns'">Family.Columns.cs</DependentUpon>
  </Compile>
</ItemGroup></Project>
""")
        compile_paths = {"Feature/Family.Columns.cs", "Feature/Family.Future.cs", "Family.cs"}

        mapping = partial_nesting(project, compile_paths)

        self.assertEqual({"Feature/Family.Future.cs": "Family.Columns.cs"}, mapping)

    def test_partial_nesting_rejects_configuration_dependent_rules(self) -> None:
        """Navigation must not silently change with a build profile or an arbitrary expression."""
        project = ET.fromstring("""\
<Project><ItemGroup Label="PartialFamilyNesting">
  <Compile Update="Family.*.cs">
    <DependentUpon Condition="'$(Configuration)' == 'Debug'">Family.cs</DependentUpon>
  </Compile>
</ItemGroup></Project>
""")

        with self.assertRaisesRegex(ValueError, "Only exact filename exclusions"):
            partial_nesting(project, {"Family.cs", "Family.Part.cs"})

    def test_partial_nesting_rejects_self_parent(self) -> None:
        """An overbroad rule must not attach its meaningful anchor to itself."""
        project = ET.fromstring("""\
<Project><ItemGroup Label="PartialFamilyNesting">
  <Compile Update="Family.cs"><DependentUpon>Family.cs</DependentUpon></Compile>
</ItemGroup></Project>
""")

        with self.assertRaisesRegex(ValueError, "itself"):
            partial_nesting(project, {"Family.cs"})

    def test_partial_nesting_rejects_duplicate_parent_assignment(self) -> None:
        """Two metadata declarations cannot hide a physical file's ownership choice."""
        project = ET.fromstring("""\
<Project><ItemGroup Label="PartialFamilyNesting">
  <Compile Update="Family.*.cs"><DependentUpon>Family.cs</DependentUpon></Compile>
  <Compile Update="Family.Part.cs"><DependentUpon>Other.cs</DependentUpon></Compile>
</ItemGroup></Project>
""")

        with self.assertRaisesRegex(ValueError, "only one parent"):
            partial_nesting(project, {"Family.cs", "Family.Part.cs", "Other.cs"})

    def test_partial_nesting_rejects_missing_parent(self) -> None:
        """XML alone cannot prove that Rider has a real compiled anchor to display."""
        project = ET.fromstring("""\
<Project><ItemGroup Label="PartialFamilyNesting">
  <Compile Update="Family.*.cs"><DependentUpon>Family.cs</DependentUpon></Compile>
</ItemGroup></Project>
""")

        with self.assertRaisesRegex(ValueError, "existing compiled local parent"):
            partial_nesting(project, {"Family.Part.cs"})

    def test_partial_nesting_rejects_remote_parent(self) -> None:
        """Feature locations stay visible instead of collapsing into a remote global file."""
        project = ET.fromstring("""\
<Project><ItemGroup Label="PartialFamilyNesting">
  <Compile Update="Feature/Family.*.cs"><DependentUpon>../Family.cs</DependentUpon></Compile>
</ItemGroup></Project>
""")

        with self.assertRaisesRegex(ValueError, "feature-local"):
            partial_nesting(project, {"Family.cs", "Feature/Family.Part.cs"})

    def test_postgresql_runnable_classes_do_not_nest_under_abstract_helper(self) -> None:
        """A shared filename prefix is not evidence that two runnable types are partials."""
        project_path = REPOSITORY_ROOT / (
            "tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests/"
            "Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests.csproj")
        compile_paths = {
            path.relative_to(project_path.parent).as_posix()
            for path in project_path.parent.rglob("*.cs")
            if not {"bin", "obj"}.intersection(path.relative_to(project_path.parent).parts)
        }

        mapping = partial_nesting(ET.parse(project_path).getroot(), compile_paths)

        excluded = (
            "Integration/Features/Columns/PostgreSqlSafeMigrationIntegrationTests.Columns.cs",
            "Integration/Features/Lifecycle/PostgreSqlSafeMigrationIntegrationTests.Lifecycle.cs",
        )
        self.assertTrue(set(excluded).issubset(compile_paths))
        self.assertTrue(set(excluded).isdisjoint(mapping))

    def test_unrelated_allocation_class_does_not_inherit_partial_prefix_parent(self) -> None:
        """A legacy filename prefix must not hide a distinct runnable Core test class."""
        project_path = REPOSITORY_ROOT / (
            "tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/"
            "Doka.EntityFrameworkCore.SafeMigrations.Tests.csproj")
        compile_paths = {
            path.relative_to(project_path.parent).as_posix()
            for path in project_path.parent.rglob("*.cs")
            if not {"bin", "obj"}.intersection(path.relative_to(project_path.parent).parts)
        }

        mapping = partial_nesting(ET.parse(project_path).getroot(), compile_paths)

        excluded = "Unit/Features/Lifecycle/SafeMigrationPreflightProjectionTests.Allocations.cs"
        self.assertIn(excluded, compile_paths)
        self.assertNotIn(excluded, mapping)


if __name__ == "__main__":
    unittest.main()
