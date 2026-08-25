#!/usr/bin/env python3

"""Validate and export the source-bound SafeMigrations release identity."""

from __future__ import annotations

import argparse
from dataclasses import dataclass
from datetime import date
import os
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ElementTree


VERSION_PATTERN = re.compile(
    r"^(0|[1-9][0-9]*)\."
    r"(0|[1-9][0-9]*)\."
    r"(0|[1-9][0-9]*)"
    r"(?:-([0-9a-z-]+(?:\.[0-9a-z-]+)*))?$"
)
MAX_NUGET_VERSION_LENGTH = 64


class VersionContractError(ValueError):
    """Indicates that a requested version is not a valid release identity."""


@dataclass(frozen=True)
class VersionContract:
    """Contains the canonical release values exported to GitHub Actions."""

    package_version: str
    prerelease: bool
    release_tag: str


def validate_version(
    package_version: str,
    version_prefix: str,
    changelog: str,
) -> VersionContract:
    """Validate a package version against NuGet and repository source contracts."""

    if len(package_version) > MAX_NUGET_VERSION_LENGTH:
        raise VersionContractError(
            f"Release version exceeds NuGet.org's {MAX_NUGET_VERSION_LENGTH}-character limit."
        )

    match = VERSION_PATTERN.fullmatch(package_version)
    if match is None:
        raise VersionContractError(
            f"Release version is not canonical for NuGet: {package_version}"
        )

    prerelease_value = match.group(4)
    if prerelease_value is not None:
        for identifier in prerelease_value.split("."):
            if identifier.isdigit() and len(identifier) > 1 and identifier.startswith("0"):
                raise VersionContractError(
                    f"Release version is not canonical for NuGet: {package_version}"
                )

    source_version = ".".join(match.groups(default="")[:3])
    if source_version != version_prefix:
        raise VersionContractError(
            f"Release version {package_version} is not on source release line {version_prefix}."
        )

    changelog_prefix = f"## [{package_version}] - "
    matching_entries = [
        line for line in changelog.splitlines() if line.startswith(changelog_prefix)
    ]
    if len(matching_entries) != 1:
        raise VersionContractError(
            f"CHANGELOG.md must contain exactly one dated [{package_version}] release entry."
        )

    release_date = matching_entries[0][len(changelog_prefix) :]
    try:
        date.fromisoformat(release_date)
    except ValueError as error:
        raise VersionContractError(
            f"CHANGELOG.md has an invalid release date for {package_version}: {release_date}"
        ) from error

    return VersionContract(
        package_version=package_version,
        prerelease=prerelease_value is not None,
        release_tag=f"v{package_version}",
    )


def read_version_prefix(path: Path) -> str:
    """Read the unique stable VersionPrefix from the production build properties."""

    root = ElementTree.parse(path).getroot()
    values = [
        element.text.strip()
        for element in root.findall(".//VersionPrefix")
        if element.text is not None and element.text.strip()
    ]
    if len(values) != 1 or VERSION_PATTERN.fullmatch(values[0]) is None or "-" in values[0]:
        raise VersionContractError(
            f"{path} must define exactly one canonical stable VersionPrefix."
        )

    return values[0]


def write_outputs(contract: VersionContract) -> None:
    """Write canonical values to GitHub Actions or standard output."""

    lines = [
        f"package-version={contract.package_version}",
        f"prerelease={str(contract.prerelease).lower()}",
        f"release-tag={contract.release_tag}",
    ]
    github_output = os.environ.get("GITHUB_OUTPUT")
    if github_output:
        with Path(github_output).open("a", encoding="utf-8", newline="\n") as output:
            output.write("\n".join(lines) + "\n")
    else:
        print("\n".join(lines))


def main(argv: list[str] | None = None) -> int:
    """Run the release-version validator."""

    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--version", required=True)
    parser.add_argument("--version-props", required=True, type=Path)
    parser.add_argument("--changelog", required=True, type=Path)
    arguments = parser.parse_args(argv)

    try:
        version_prefix = read_version_prefix(arguments.version_props)
        changelog = arguments.changelog.read_text(encoding="utf-8")
        contract = validate_version(arguments.version, version_prefix, changelog)
        write_outputs(contract)
    except (OSError, ElementTree.ParseError, VersionContractError) as error:
        print(str(error), file=sys.stderr)

        return 1

    print(f"Validated SafeMigrations release version {contract.package_version}.")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
