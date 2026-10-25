"""Render active Dependabot targets from a reviewed, lifecycle-independent template.

The explicit template is actual input, not a second copy of hidden line state.
Keeping it separate allows every line to retire and later resume without dead
update targets or loss of reviewed grouping/cooldown settings.
"""

from __future__ import annotations

import re
from pathlib import Path


TEMPLATE_PATH = Path(__file__).resolve().parents[2] / ".github/dependabot-template.yml"
ECOSYSTEMS = frozenset(("nuget", "github-actions", "dotnet-sdk"))
ENTRY = re.compile(r"^  - package-ecosystem: ([a-z-]+)\s*$", re.MULTILINE)
TARGET = re.compile(r"^    target-branch: ([^\n]+)$", re.MULTILINE)
# WHY: These majors follow EF. SqlClient and other transport packages have
# independent version lines and must not inherit the framework-major ceiling.
FRAMEWORK_PACKAGES = ("Microsoft.EntityFrameworkCore.*", "Doka.EntityFrameworkCore.MySql",
                      "Npgsql.EntityFrameworkCore.PostgreSQL", "Microsoft.Extensions.*")


class DependencyPolicyError(ValueError):
    """Update targets or policies differ from their reviewed line/template contract."""


def _semantic(source: str) -> str:
    """Ignore comments/blank lines only, preserving indentation and every policy token."""
    return "\n".join(row for row in source.splitlines() if row.strip() and not row.lstrip().startswith("#"))


def entries(source: str) -> list[tuple[str, str, str]]:
    """Read the deliberately bounded entry shape without a general YAML dependency."""
    if _semantic(source) == "version: 2\nupdates: []":
        return []

    if not source.startswith("version: 2\nupdates:\n"):
        raise DependencyPolicyError("Dependabot requires the version 2 update-list contract.")

    matches = list(ENTRY.finditer(source))
    if (not matches or len(matches) != len(re.findall(r"package-ecosystem\s*:", source))
            or _semantic(source[:matches[0].start()]) != "version: 2\nupdates:"):
        raise DependencyPolicyError("Dependabot update entries use an unrecognized shape.")

    result = []
    for index, match in enumerate(matches):
        end = matches[index + 1].start() if index + 1 < len(matches) else len(source)
        body = source[match.start():end]
        targets = TARGET.findall(body)
        if len(targets) > 1 or len(targets) != len(re.findall(r"target-branch\s*:", body)):
            raise DependencyPolicyError("Dependabot repeats or obscures target-branch.")

        branch = targets[0].strip().strip("\"'") if targets else "main"
        if not re.fullmatch(r"(?:main|maintenance/[1-9][0-9]*\.x|preview/[1-9][0-9]*\.x)", branch):
            raise DependencyPolicyError("Dependabot target is not a canonical exact release branch.")

        result.append((match[1], branch, body))

    return result


def expected_targets(policy: dict) -> set[tuple[str, str]]:
    """Give every active line all three existing ecosystems, never Docker pin updates."""
    from eng.release.lines import validate_policy

    checked = validate_policy(policy)

    return {(ecosystem, line["sourceRef"].removeprefix("refs/heads/"))
            for line in checked["lines"] if line["state"] == "active" for ecosystem in ECOSYSTEMS}


def _templates(source: str) -> dict[str, str]:
    """Require one retained default template for each existing ecosystem."""
    blocks = entries(source)
    if (len(blocks) != len(ECOSYSTEMS) or any(branch != "main" for _, branch, _ in blocks)
            or {ecosystem for ecosystem, _, _ in blocks} != ECOSYSTEMS):
        raise DependencyPolicyError("The canonical template must retain exactly the three default ecosystems.")

    return {ecosystem: body for ecosystem, _, body in blocks}


def _generated_ignore(ecosystem: str, major: int) -> str:
    """Add only framework-major ceilings for additional maintained/preview targets."""
    if ecosystem == "dotnet-sdk":
        return ('    ignore:\n      - dependency-name: "*"\n'
                '        update-types:\n          - version-update:semver-major\n')

    if ecosystem != "nuget":
        return ""

    result = "    ignore:\n"
    for name in FRAMEWORK_PACKAGES:
        # WHY: -0 is SemVer's first prerelease. Dependabot's accepted leading-
        # hyphen identifiers sort below it lexically, so retain that floor too.
        result += (f'      - dependency-name: "{name}"\n'
                   f'        versions: [">={major + 1}.0.0-0", ">={major + 1}.0.0--"]\n')

    return result


def _target_block(template: str, ecosystem: str, branch: str) -> str:
    """Preserve main exactly; derive additional targets without copying stale prose."""
    if branch == "main":
        return template

    if re.search(r"^    (?:target-branch|ignore):", template, re.MULTILINE):
        raise DependencyPolicyError("Template branch/ignore additions require explicit renderer reconciliation.")

    body = "\n".join(row for row in template.splitlines() if not row.lstrip().startswith("#"))
    header, remainder = body.split("\n", 1)
    major = int(branch.split("/", 1)[1].removesuffix(".x"))

    return (f"\n{header}\n    target-branch: {branch}\n{remainder.rstrip()}\n"
            + _generated_ignore(ecosystem, major))


def _validate_existing(source: str, templates: dict[str, str]) -> list[tuple[str, str, str]]:
    """Never overwrite an unrecognized update policy while changing active targets."""
    blocks = entries(source)
    seen = set()
    for ecosystem, branch, body in blocks:
        identity = ecosystem, branch
        if identity in seen or ecosystem not in templates:
            raise DependencyPolicyError("Existing update targets are duplicated or use an unreviewed ecosystem.")

        seen.add(identity)
        if _semantic(body) != _semantic(_target_block(templates[ecosystem], ecosystem, branch)):
            raise DependencyPolicyError(
                "Update grouping, cooldown or major policy differs from the canonical template.")

    return blocks


def validate_configuration(policy: dict, source: str, *, template: str | None = None) -> None:
    """Validate all active targets and every retained policy against the actual template."""
    canonical = template if template is not None else TEMPLATE_PATH.read_text(encoding="ascii")
    blocks = _validate_existing(source, _templates(canonical))
    if {(ecosystem, branch) for ecosystem, branch, _ in blocks} != expected_targets(policy):
        raise DependencyPolicyError("Dependabot targets must match active release lines exactly once.")


def render_configuration(policy: dict, source: str, *, template: str | None = None) -> str:
    """Render a reviewable activation/retirement change, including a zero-active interval."""
    from eng.release.lines import validate_policy

    checked = validate_policy(policy)
    canonical = template if template is not None else TEMPLATE_PATH.read_text(encoding="ascii")
    templates = _templates(canonical)
    _validate_existing(source, templates)
    active = [line for line in checked["lines"] if line["state"] == "active"]
    if not active:
        return "version: 2\nupdates: []\n"

    result = canonical[:ENTRY.search(canonical).start()]
    for line in active:
        branch = line["sourceRef"].removeprefix("refs/heads/")
        for ecosystem in ("nuget", "github-actions", "dotnet-sdk"):
            result += _target_block(templates[ecosystem], ecosystem, branch)

    validate_configuration(checked, result, template=canonical)

    return result
