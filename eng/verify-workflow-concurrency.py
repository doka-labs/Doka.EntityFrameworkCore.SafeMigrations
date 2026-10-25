#!/usr/bin/env python3
"""Validate native workflow policy before adapting actionlint's older schema."""

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
# WHY: The same released linter predates GitHub's Ubuntu26 label and scoped cache policy.
# Only exact independently validated job fields qualify; all other diagnostics remain errors.
CACHE_DIAGNOSTIC = ('unexpected key "cache-mode" for "job" section. expected one of "concurrency", "container", '
                    '"continue-on-error", "defaults", "env", "environment", "if", "name", "needs", "outputs", '
                    '"permissions", "runs-on", "secrets", "services", "snapshot", "steps", "strategy", '
                    '"timeout-minutes", "uses", "with"')
RUNNER_DIAGNOSTIC_PREFIX = 'label "ubuntu-26.04" is unknown. available labels are '
RUNNER_DIAGNOSTIC_SUFFIX = ('. if it is a custom label for self-hosted runner, '
                            'set list of labels in actionlint.yaml config file')
RUNNER_DIAGNOSTIC = re.compile(re.escape(RUNNER_DIAGNOSTIC_PREFIX)
                               + r'"[A-Za-z0-9_.-]+"(?:, "[A-Za-z0-9_.-]+")*'
                               + re.escape(RUNNER_DIAGNOSTIC_SUFFIX))


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


def native_job_positions(source: str) -> tuple[set[int], set[int]]:
    """Prove canonical x64 runner and denied cache fields at their actual owning job.

    This is a bounded repository policy, not a replacement YAML parser. The
    linter still receives the original source and every unrelated diagnostic.
    """
    runner_positions, cache_positions = set(), set()
    jobs_section, owner = False, None
    seen = set()
    for index, row in enumerate(source.splitlines()):
        if not row.strip() or row.lstrip().startswith("#"):
            continue

        if not row.startswith((" ", "\t")):
            jobs_section, owner = re.fullmatch(r"jobs:\s*(?:#.*)?", row) is not None, None

        job = re.fullmatch(r"  ([A-Za-z_][A-Za-z0-9_-]*):\s*(?:#.*)?", row)
        if jobs_section and job is not None:
            owner = job[1]

        field = re.fullmatch(r"(\s*)(runs-on|cache-mode):\s*(.*?)\s*(?:#.*)?", row)
        if field is None:
            continue

        if not jobs_section or owner is None or field[1] != "    ":
            raise ValueError("Runner and cache policy require an explicit owning job field.")

        identity = (owner, field[2])
        if identity in seen:
            raise ValueError("Duplicate native job policy field.")

        seen.add(identity)
        expected = "ubuntu-26.04" if field[2] == "runs-on" else "none"
        if field[3] != expected:
            raise ValueError(f"{field[2]} must be literal {expected}.")

        positions = runner_positions if field[2] == "runs-on" else cache_positions
        positions.add(index + 1)

    return runner_positions, cache_positions


def compatible_diagnostic(diagnostic: dict, positions: tuple[set[int], set[int], set[int]]) -> bool:
    """Recognize only independently proven native fields missing from the old schema."""
    queues, runners, caches = positions
    line, kind, message = diagnostic["line"], diagnostic["kind"], diagnostic["message"]
    if kind == "syntax-check":
        return ((message == QUEUE_DIAGNOSTIC and line in queues)
                or (message == CACHE_DIAGNOSTIC and line in caches))

    # WHY: Only upstream's varying known-label list is variable. The actual
    # field, native label, message family and source position remain exact.
    return kind == "runner-label" and line in runners and RUNNER_DIAGNOSTIC.fullmatch(message) is not None


def verify(paths: list[Path], actionlint: str | None = None) -> int:
    """Keep all real lint errors; adapt only a separately proven unsupported field."""
    positions = {}
    for path in paths:
        source = path.read_text(encoding="ascii")
        runners, caches = native_job_positions(source)
        identity = str(path.resolve())
        if identity in positions:
            raise ValueError("Duplicate workflow source identity.")

        positions[identity] = queue_positions(source), runners, caches

    if actionlint is None:
        print("GitHub queue, runner and cache contracts passed.")

        return 0

    result = subprocess.run([actionlint, "-format", "{{json .}}", *map(str, paths)],
                            capture_output=True, text=True, check=False)
    if not result.stdout.strip():
        if result.returncode != 0 or result.stderr.strip():
            raise ValueError("actionlint failed without complete structured diagnostics.")

        print("actionlint and GitHub queue, runner and cache contracts passed.")

        return 0

    diagnostics = json.loads(result.stdout)
    if not isinstance(diagnostics, list) or not diagnostics or result.stderr.strip():
        raise ValueError("actionlint returned malformed or incomplete diagnostics.")

    adapted, failures = set(), []
    for diagnostic in diagnostics:
        if (not isinstance(diagnostic, dict) or not isinstance(diagnostic.get("filepath"), str)
                or type(diagnostic.get("line")) is not int or diagnostic["line"] < 1
                or not isinstance(diagnostic.get("kind"), str)
                or not isinstance(diagnostic.get("message"), str)):
            raise ValueError("actionlint diagnostic must have complete typed source identity.")

        # WHY: actionlint reports paths relative to its working directory even
        # when given absolute paths. Canonical identity must still bind the
        # diagnostic to the exact independently validated input file.
        position = (str(Path(diagnostic["filepath"]).resolve()), diagnostic["line"])
        if (compatible_diagnostic(diagnostic, positions.get(position[0], (set(), set(), set())))
                and position not in adapted):
            adapted.add(position)
        else:
            failures.append(diagnostic)

    if failures or result.returncode not in (0, 1):
        for failure in failures:
            print(json.dumps(failure, ensure_ascii=True), file=sys.stderr)

        return 1

    print("GitHub queue, runner and cache contracts passed; remaining actionlint diagnostics passed "
          f"({len(adapted)} explicitly validated native-schema compatibility diagnostic(s)).")

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
