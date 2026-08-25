#!/usr/bin/env python3

from __future__ import annotations

import argparse
import sys
import xml.etree.ElementTree as element_tree
from dataclasses import dataclass
from pathlib import Path


CORE_PROJECT = "Doka.EntityFrameworkCore.SafeMigrations"
MYSQL_PROJECT = "Doka.EntityFrameworkCore.SafeMigrations.MySql"
POSTGRESQL_PROJECT = "Doka.EntityFrameworkCore.SafeMigrations.PostgreSql"
SYMBOL_READBACK_PROJECT = "Doka.EntityFrameworkCore.SafeMigrations.NuGetSymbolReadback"
MYSQL_TESTCONTAINERS_PACKAGE = "Testcontainers.MySql"
POSTGRESQL_TESTCONTAINERS_PACKAGE = "Testcontainers.PostgreSql"


@dataclass(frozen=True)
class ProjectContract:
    path: str
    project_references: frozenset[str]
    package_references: frozenset[str]
    included_in_solution: bool = True


@dataclass(frozen=True)
class PropsContract:
    path: str
    package_references: frozenset[str]
    imports_repository_props: bool


PROJECT_CONTRACTS = (
    ProjectContract(
        f"src/{CORE_PROJECT}/{CORE_PROJECT}.csproj",
        frozenset(),
        frozenset({"Microsoft.EntityFrameworkCore.Relational"}),
    ),
    ProjectContract(
        f"src/{MYSQL_PROJECT}/{MYSQL_PROJECT}.csproj",
        frozenset({CORE_PROJECT}),
        frozenset(
            {
                "Doka.EntityFrameworkCore.MySql",
                "Microsoft.Extensions.DependencyInjection.Abstractions",
            }
        ),
    ),
    ProjectContract(
        f"src/{POSTGRESQL_PROJECT}/{POSTGRESQL_PROJECT}.csproj",
        frozenset({CORE_PROJECT}),
        frozenset(
            {
                "Microsoft.Extensions.DependencyInjection.Abstractions",
                "Npgsql.EntityFrameworkCore.PostgreSQL",
            }
        ),
    ),
    ProjectContract(
        f"tests/{CORE_PROJECT}.Tests/{CORE_PROJECT}.Tests.csproj",
        frozenset({CORE_PROJECT, "Doka.EntityFrameworkCore.SafeMigrations.NuGetSymbolReadback"}),
        frozenset({"Microsoft.NET.Test.Sdk", "xunit", "xunit.runner.visualstudio"}),
    ),
    ProjectContract(
        f"tests/{MYSQL_PROJECT}.Tests/{MYSQL_PROJECT}.Tests.csproj",
        frozenset({CORE_PROJECT, MYSQL_PROJECT}),
        frozenset(
            {
                "Microsoft.EntityFrameworkCore.Design",
                "Microsoft.NET.Test.Sdk",
                MYSQL_TESTCONTAINERS_PACKAGE,
                "xunit",
                "xunit.runner.visualstudio",
            }
        ),
    ),
    ProjectContract(
        f"tests/{POSTGRESQL_PROJECT}.Tests/{POSTGRESQL_PROJECT}.Tests.csproj",
        frozenset({CORE_PROJECT, POSTGRESQL_PROJECT}),
        frozenset(
            {
                "Microsoft.EntityFrameworkCore.Design",
                "Microsoft.NET.Test.Sdk",
                POSTGRESQL_TESTCONTAINERS_PACKAGE,
                "xunit",
                "xunit.runner.visualstudio",
            }
        ),
    ),
    ProjectContract(
        f"benchmarks/{CORE_PROJECT}.Benchmarks/{CORE_PROJECT}.Benchmarks.csproj",
        frozenset({CORE_PROJECT}),
        frozenset(),
    ),
    ProjectContract(
        f"benchmarks/{MYSQL_PROJECT}.Benchmarks/{MYSQL_PROJECT}.Benchmarks.csproj",
        frozenset({CORE_PROJECT, MYSQL_PROJECT}),
        frozenset(),
    ),
    ProjectContract(
        f"benchmarks/{POSTGRESQL_PROJECT}.Benchmarks/{POSTGRESQL_PROJECT}.Benchmarks.csproj",
        frozenset({CORE_PROJECT, POSTGRESQL_PROJECT}),
        frozenset(),
    ),
    ProjectContract(
        f"samples/{CORE_PROJECT}.Sample/{CORE_PROJECT}.Sample.csproj",
        frozenset({CORE_PROJECT, MYSQL_PROJECT, POSTGRESQL_PROJECT}),
        frozenset(),
    ),
    ProjectContract(
        f"eng/{SYMBOL_READBACK_PROJECT}/{SYMBOL_READBACK_PROJECT}.csproj",
        frozenset(),
        frozenset(),
    ),
    ProjectContract(
        "eng/package-consumer/MySql/PackageConsumer.csproj",
        frozenset(),
        frozenset({MYSQL_PROJECT}),
        False,
    ),
    ProjectContract(
        "eng/package-consumer/PostgreSql/PackageConsumer.csproj",
        frozenset(),
        frozenset({POSTGRESQL_PROJECT}),
        False,
    ),
)

PROPS_CONTRACTS = (
    PropsContract("Directory.Build.props", frozenset(), False),
    PropsContract(
        "src/Directory.Build.props",
        frozenset({"Microsoft.CodeAnalysis.PublicApiAnalyzers"}),
        True,
    ),
    PropsContract("tests/Directory.Build.props", frozenset(), True),
    PropsContract("benchmarks/Directory.Build.props", frozenset(), True),
    PropsContract("samples/Directory.Build.props", frozenset(), True),
)


def local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def reference_names(project_path: Path, item_name: str) -> frozenset[str]:
    root = element_tree.parse(project_path).getroot()
    names: set[str] = set()

    for element in root.iter():
        if local_name(element.tag) != item_name:
            continue

        include = element.get("Include")
        if include is None:
            continue

        if item_name == "ProjectReference":
            include = Path(include.replace("\\", "/")).stem

        names.add(include)

    return frozenset(names)


def validate_project_contract(
    repository_root: Path,
    contract: ProjectContract,
) -> list[str]:
    project_path = repository_root / contract.path
    if not project_path.is_file():
        return [f"Missing contracted project: {contract.path}"]

    errors: list[str] = []
    actual_projects = reference_names(project_path, "ProjectReference")
    actual_packages = reference_names(project_path, "PackageReference")

    if actual_projects != contract.project_references:
        errors.append(
            f"{contract.path}: ProjectReference set {sorted(actual_projects)} "
            f"does not equal {sorted(contract.project_references)}"
        )

    if actual_packages != contract.package_references:
        errors.append(
            f"{contract.path}: PackageReference set {sorted(actual_packages)} "
            f"does not equal {sorted(contract.package_references)}"
        )

    return errors


def validate_props_contract(
    repository_root: Path,
    contract: PropsContract,
) -> list[str]:
    props_path = repository_root / contract.path
    if not props_path.is_file():
        return [f"Missing contracted props file: {contract.path}"]

    errors: list[str] = []
    actual_packages = reference_names(props_path, "PackageReference")

    if actual_packages != contract.package_references:
        errors.append(
            f"{contract.path}: PackageReference set {sorted(actual_packages)} "
            f"does not equal {sorted(contract.package_references)}"
        )

    if contract.imports_repository_props:
        imports = [
            element.get("Project", "")
            for element in element_tree.parse(props_path).getroot().iter()
            if local_name(element.tag) == "Import"
        ]
        if not any("GetPathOfFileAbove('Directory.Build.props'" in value for value in imports):
            errors.append(f"{contract.path}: repository-root Directory.Build.props is not imported")

    return errors


def validate_solution(repository_root: Path) -> list[str]:
    solution_path = repository_root / "Doka.EntityFrameworkCore.SafeMigrations.slnx"
    if not solution_path.is_file():
        return ["Missing solution file: Doka.EntityFrameworkCore.SafeMigrations.slnx"]

    solution_root = element_tree.parse(solution_path).getroot()
    solution_projects = {
        element.get("Path", "").replace("\\", "/")
        for element in solution_root.iter()
        if local_name(element.tag) == "Project"
    }
    required_projects = {
        contract.path
        for contract in PROJECT_CONTRACTS
        if contract.included_in_solution
    }
    missing = sorted(required_projects - solution_projects)

    return [f"Solution omits contracted project: {path}" for path in missing]


def validate_repository(repository_root: Path) -> list[str]:
    errors: list[str] = []

    for contract in PROJECT_CONTRACTS:
        errors.extend(validate_project_contract(repository_root, contract))

    for contract in PROPS_CONTRACTS:
        errors.extend(validate_props_contract(repository_root, contract))

    errors.extend(validate_solution(repository_root))

    return errors


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Verify SafeMigrations project and provider boundaries.")
    parser.add_argument(
        "--repository-root",
        type=Path,
        default=Path(__file__).resolve().parent.parent,
    )
    return parser.parse_args()


def main() -> int:
    arguments = parse_arguments()
    repository_root = arguments.repository_root.resolve()
    errors = validate_repository(repository_root)

    if errors:
        for error in errors:
            print(error, file=sys.stderr)

        return 1

    print("SafeMigrations project and provider boundaries verified.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
