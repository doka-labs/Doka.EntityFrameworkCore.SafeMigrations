from __future__ import annotations

import importlib.util
import sys
import tempfile
import unittest
from pathlib import Path


ENG_ROOT = Path(__file__).resolve().parent.parent
REPOSITORY_ROOT = ENG_ROOT.parent
MODULE_PATH = ENG_ROOT / "verify-project-boundaries.py"
MODULE_SPEC = importlib.util.spec_from_file_location("verify_project_boundaries", MODULE_PATH)
if MODULE_SPEC is None or MODULE_SPEC.loader is None:
    raise RuntimeError(f"Unable to load {MODULE_PATH}.")

boundaries = importlib.util.module_from_spec(MODULE_SPEC)
sys.modules[MODULE_SPEC.name] = boundaries
MODULE_SPEC.loader.exec_module(boundaries)


class ProjectBoundaryTests(unittest.TestCase):
    def test_repository_contract_is_complete(self) -> None:
        self.assertEqual([], boundaries.validate_repository(REPOSITORY_ROOT))

    def test_unexpected_provider_project_reference_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            project_path = root / "Sample.csproj"
            project_path.write_text(
                """<Project Sdk=\"Microsoft.NET.Sdk\">
  <ItemGroup>
    <ProjectReference Include=\"../Doka.EntityFrameworkCore.SafeMigrations.csproj\" />
    <ProjectReference Include=\"../Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.csproj\" />
  </ItemGroup>
</Project>
""",
                encoding="utf-8",
            )
            contract = boundaries.ProjectContract(
                "Sample.csproj",
                frozenset({boundaries.CORE_PROJECT}),
                frozenset(),
            )

            errors = boundaries.validate_project_contract(root, contract)

            self.assertEqual(1, len(errors))
            self.assertIn("ProjectReference set", errors[0])
            self.assertIn(boundaries.POSTGRESQL_PROJECT, errors[0])

    def test_unexpected_provider_package_reference_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            project_path = root / "Sample.csproj"
            project_path.write_text(
                """<Project Sdk=\"Microsoft.NET.Sdk\">
  <ItemGroup>
    <PackageReference Include=\"Doka.EntityFrameworkCore.SafeMigrations.MySql\" />
    <PackageReference Include=\"Doka.EntityFrameworkCore.SafeMigrations.PostgreSql\" />
  </ItemGroup>
</Project>
""",
                encoding="utf-8",
            )
            contract = boundaries.ProjectContract(
                "Sample.csproj",
                frozenset(),
                frozenset({boundaries.MYSQL_PROJECT}),
            )

            errors = boundaries.validate_project_contract(root, contract)

            self.assertEqual(1, len(errors))
            self.assertIn("PackageReference set", errors[0])
            self.assertIn(boundaries.POSTGRESQL_PROJECT, errors[0])

    def test_unexpected_provider_package_reference_in_role_props_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            props_path = root / "Directory.Build.props"
            props_path.write_text(
                """<Project>
  <ItemGroup>
    <PackageReference Include="Doka.EntityFrameworkCore.SafeMigrations.PostgreSql" />
  </ItemGroup>
</Project>
""",
                encoding="utf-8",
            )
            contract = boundaries.PropsContract(
                "Directory.Build.props",
                frozenset(),
                False,
            )

            errors = boundaries.validate_props_contract(root, contract)

            self.assertEqual(1, len(errors))
            self.assertIn("PackageReference set", errors[0])
            self.assertIn(boundaries.POSTGRESQL_PROJECT, errors[0])


if __name__ == "__main__":
    unittest.main()
