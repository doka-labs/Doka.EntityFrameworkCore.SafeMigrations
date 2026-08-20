#!/usr/bin/env python3

import argparse
import json
import re
import sys
import xml.etree.ElementTree as ElementTree
from dataclasses import dataclass
from pathlib import Path


CONDITION_COVERAGE = re.compile(r"\((\d+)/(\d+)\)")


@dataclass
class LineCoverage:
    covered: bool = False
    covered_branches: int = 0
    total_branches: int = 0


@dataclass(frozen=True)
class CoverageThreshold:
    assembly: str
    minimum_line_rate: float
    minimum_branch_rate: float


def parse_threshold(value: str) -> CoverageThreshold:
    parts = value.split(":")
    if len(parts) != 3:
        raise argparse.ArgumentTypeError(
            "A threshold must use ASSEMBLY:MINIMUM_LINE_RATE:MINIMUM_BRANCH_RATE."
        )

    assembly = parts[0].strip()
    try:
        minimum_line_rate = float(parts[1])
        minimum_branch_rate = float(parts[2])
    except ValueError as error:
        raise argparse.ArgumentTypeError("Coverage rates must be decimal numbers.") from error

    if not assembly or not 0 <= minimum_line_rate <= 1 or not 0 <= minimum_branch_rate <= 1:
        raise argparse.ArgumentTypeError("Coverage thresholds must name an assembly and be between zero and one.")

    return CoverageThreshold(assembly, minimum_line_rate, minimum_branch_rate)


def load_thresholds(path: Path) -> list[CoverageThreshold]:
    document = json.loads(path.read_text(encoding="utf-8"))
    if document.get("schemaVersion") != 1 or not isinstance(document.get("assemblies"), list):
        raise ValueError("The coverage-threshold document must use schemaVersion 1 and an assemblies array.")

    thresholds = []
    for assembly in document["assemblies"]:
        try:
            value = (
                f"{assembly['name']}:{assembly['minimumLineRate']}:"
                f"{assembly['minimumBranchRate']}"
            )
        except (KeyError, TypeError) as error:
            raise ValueError("Every coverage threshold must define name and both minimum rates.") from error

        thresholds.append(parse_threshold(value))

    if not thresholds:
        raise ValueError("At least one product-assembly coverage threshold is required.")

    return thresholds


def normalize_source(filename: str) -> str:
    normalized = filename.replace("\\", "/")
    source_marker = "/src/"
    marker_index = normalized.find(source_marker)

    return normalized[marker_index + 1 :] if marker_index >= 0 else normalized


def read_reports(
    reports_root: Path,
    thresholds: list[CoverageThreshold],
) -> dict[str, dict[tuple[str, str, int], LineCoverage]]:
    expected = {threshold.assembly for threshold in thresholds}
    merged: dict[str, dict[tuple[str, str, int], LineCoverage]] = {
        assembly: {} for assembly in expected
    }
    reports = sorted(reports_root.rglob("*.cobertura.xml"))
    if not reports:
        raise ValueError(f"No Cobertura reports were found below '{reports_root}'.")

    for report in reports:
        root = ElementTree.parse(report).getroot()
        for package in root.findall("./packages/package"):
            assembly = package.get("name", "")
            if assembly not in expected:
                continue

            lines = merged[assembly]
            for class_element in package.findall("./classes/class"):
                class_name = class_element.get("name", "")
                source = normalize_source(class_element.get("filename", ""))
                for line_element in class_element.findall("./lines/line"):
                    line_number = int(line_element.get("number", "0"))
                    key = (source, class_name, line_number)
                    line = lines.setdefault(key, LineCoverage())
                    line.covered |= int(line_element.get("hits", "0")) > 0

                    condition = line_element.get("condition-coverage")
                    if condition is None:
                        continue

                    match = CONDITION_COVERAGE.search(condition)
                    if match is None:
                        raise ValueError(
                            f"Invalid condition-coverage value '{condition}' in '{report}'."
                        )

                    line.covered_branches = max(line.covered_branches, int(match.group(1)))
                    line.total_branches = max(line.total_branches, int(match.group(2)))

    return merged


def verify(
    reports_root: Path,
    thresholds: list[CoverageThreshold],
) -> int:
    merged = read_reports(reports_root, thresholds)
    failed = False

    for threshold in thresholds:
        lines = merged[threshold.assembly]
        if not lines:
            print(f"Coverage assembly is missing: {threshold.assembly}", file=sys.stderr)
            failed = True
            continue

        covered_lines = sum(line.covered for line in lines.values())
        total_lines = len(lines)
        covered_branches = sum(line.covered_branches for line in lines.values())
        total_branches = sum(line.total_branches for line in lines.values())
        line_rate = covered_lines / total_lines
        branch_rate = covered_branches / total_branches if total_branches else 1.0

        print(
            f"{threshold.assembly}: lines {covered_lines}/{total_lines} ({line_rate:.4f}), "
            f"branches {covered_branches}/{total_branches} ({branch_rate:.4f})"
        )

        if line_rate < threshold.minimum_line_rate:
            print(
                f"Line coverage {line_rate:.4f} is below {threshold.minimum_line_rate:.4f} "
                f"for {threshold.assembly}.",
                file=sys.stderr,
            )
            failed = True

        if branch_rate < threshold.minimum_branch_rate:
            print(
                f"Branch coverage {branch_rate:.4f} is below {threshold.minimum_branch_rate:.4f} "
                f"for {threshold.assembly}.",
                file=sys.stderr,
            )
            failed = True

    return 1 if failed else 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Merge Microsoft Cobertura reports conservatively and enforce product-assembly thresholds."
    )
    parser.add_argument("--reports-root", required=True, type=Path)
    parser.add_argument("--thresholds-file", type=Path)
    parser.add_argument(
        "--threshold",
        action="append",
        type=parse_threshold,
        help="ASSEMBLY:MINIMUM_LINE_RATE:MINIMUM_BRANCH_RATE",
    )
    arguments = parser.parse_args(argv)

    if (arguments.thresholds_file is None) == (arguments.threshold is None):
        parser.error("Specify exactly one of --thresholds-file or --threshold.")

    try:
        thresholds = (
            load_thresholds(arguments.thresholds_file)
            if arguments.thresholds_file is not None
            else arguments.threshold
        )

        return verify(arguments.reports_root, thresholds)
    except (OSError, ValueError, ElementTree.ParseError) as error:
        print(f"Coverage verification failed: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
