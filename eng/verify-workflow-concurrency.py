#!/usr/bin/env python3
"""Validate GitHub's queue contract before adapting actionlint's older schema."""

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path


# WHY: actionlint 1.7.12 rejects this GitHub-supported field before other checks.
# Remove only this compatibility path once upstream parseConcurrency supports it.
QUEUE_DIAGNOSTIC = ('unexpected key "queue" for "concurrency" section. '
                    'expected one of "cancel-in-progress", "group"')


def queue_positions(source: str) -> set[int]:
    """Prove every adapted queue field's position, value and cancellation policy."""
    rows = source.splitlines()
    positions = set()
    for start, row in enumerate(rows):
        match = re.fullmatch(r"( {0}| {4})concurrency:\s*(?:#.*)?", row)
        if match is None:
            continue

        indentation = len(match[1]) + 2
        fields = {}
        for index in range(start + 1, len(rows)):
            line = rows[index]
            if not line.strip() or line.lstrip().startswith("#"):
                continue

            if len(line) - len(line.lstrip()) < indentation:
                break

            field = re.fullmatch(r" {" + str(indentation) + r"}([a-z-]+):\s*(.*?)\s*(?:#.*)?", line)
            if field is None:
                continue

            key, value = field[1], field[2].strip().strip("\"'")
            if key in fields:
                raise ValueError("Duplicate concurrency field.")

            fields[key] = value
            if key == "queue":
                if value not in {"single", "max"}:
                    raise ValueError("Concurrency queue must be literal single or max.")

                positions.add(index + 1)

        if fields.get("queue") == "max" and fields.get("cancel-in-progress") != "false":
            raise ValueError("A retained publisher queue requires literal cancel-in-progress: false.")

    return positions


def verify(paths: list[Path], actionlint: str | None = None) -> int:
    """Keep all real lint errors; adapt only a separately proven unsupported field."""
    positions = {str(path): queue_positions(path.read_text(encoding="ascii")) for path in paths}
    if actionlint is None:
        print("GitHub concurrency queue contracts passed.")

        return 0

    result = subprocess.run([actionlint, "-format", "{{json .}}", *map(str, paths)],
                            capture_output=True, text=True, check=False)
    if not result.stdout.strip():
        if result.returncode != 0 or result.stderr.strip():
            raise ValueError("actionlint failed without complete structured diagnostics.")

        print("actionlint and GitHub concurrency queue contracts passed.")

        return 0

    diagnostics = json.loads(result.stdout)
    if not isinstance(diagnostics, list) or not diagnostics or result.stderr.strip():
        raise ValueError("actionlint returned malformed or incomplete diagnostics.")

    adapted, failures = set(), []
    for diagnostic in diagnostics:
        if not isinstance(diagnostic, dict):
            raise ValueError("actionlint diagnostic must be an object.")

        position = (diagnostic.get("filepath"), diagnostic.get("line"))
        if (diagnostic.get("kind") == "syntax-check"
                and diagnostic.get("message") == QUEUE_DIAGNOSTIC
                and position[1] in positions.get(position[0], set())
                and position not in adapted):
            adapted.add(position)
        else:
            failures.append(diagnostic)

    if failures or result.returncode not in (0, 1):
        for failure in failures:
            print(json.dumps(failure, ensure_ascii=True), file=sys.stderr)

        return 1

    print("GitHub queue contracts passed; actionlint passed all other checks "
          f"({len(adapted)} explicitly validated queue-schema compatibility diagnostic(s)).")

    return 0


def main() -> int:
    """Validate supplied workflow files without modifying or normalizing their bytes."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("paths", nargs="*", type=Path)
    parser.add_argument("--actionlint")
    args = parser.parse_args()
    paths = args.paths or sorted(Path(".github/workflows").glob("*.yml"))
    try:
        return verify(paths, args.actionlint)
    except (OSError, ValueError, UnicodeError) as error:
        print(f"Workflow contract failure: {error}", file=sys.stderr)

        return 1


if __name__ == "__main__":
    raise SystemExit(main())
