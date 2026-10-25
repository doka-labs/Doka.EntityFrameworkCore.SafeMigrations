"""Pure exact-line contracts; a frozen qualification never grants current rights."""

from __future__ import annotations

import argparse
import copy
import hashlib
import json
import re
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any
from xml.etree import ElementTree


POLICY_PATH = Path(__file__).resolve().with_name("lines-policy.json")
AUTHORITY_REF = "refs/heads/main"
WORKFLOW_PATH = ".github/workflows/release-candidate.yml"
POLICY_FIELDS = {"schemaVersion", "revision", "repository", "authorityRef", "lines"}
LINE_FIELDS = {"sourceRef", "role", "packageMajor", "channel", "upstreamStage",
               "state", "workflowPath", "publisherEnvironment"}
SNAPSHOT_FIELDS = {"schemaVersion", "repository", "sourceRef", "role", "packageMajor", "channel",
                   "upstreamStage", "stateAtQualification", "workflowPath", "publisherEnvironment",
                   "authorizationRevision", "authorizationDigest"}
TOOLCHAIN_FIELDS = {"approvedDotnetSdk", "targetFramework", "efCoreVersion", "testRunner", "upstreamStage"}
VERSION = re.compile(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9a-z.-]+))?\Z")
REPOSITORY = re.compile(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\Z")
DIGEST = re.compile(r"[0-9a-f]{64}\Z")


class LinePolicyError(RuntimeError):
    """An exact line, policy, version or branch toolchain cannot be proven."""


@dataclass(frozen=True)
class ReleaseVersion:
    """Canonical NuGet identity used for numeric SemVer precedence."""

    major: int
    minor: int
    patch: int
    prerelease: tuple[str, ...]

    @property
    def is_prerelease(self) -> bool:
        """Return whether this identity precedes its corresponding stable version."""
        return bool(self.prerelease)


def parse_version(value: str) -> ReleaseVersion:
    """Reject aliases/build metadata that NuGet would normalize to another identity."""
    match = VERSION.fullmatch(value) if isinstance(value, str) and len(value) <= 64 else None
    if match is None:
        raise LinePolicyError("Release version must be canonical lowercase three-part NuGet/SemVer.")

    identifiers = tuple(match[4].split(".")) if match[4] else ()
    if any(not part or part.startswith("-") or part.endswith("-")
           or (part.isdigit() and len(part) > 1 and part[0] == "0") for part in identifiers):
        raise LinePolicyError("Release version has a noncanonical prerelease identifier.")

    return ReleaseVersion(int(match[1]), int(match[2]), int(match[3]), identifiers)


def compare_versions(left: str, right: str) -> int:
    """Compare two canonical identities by SemVer precedence, returning -1/0/1."""
    first, second = parse_version(left), parse_version(right)
    a, b = (first.major, first.minor, first.patch), (second.major, second.minor, second.patch)
    if a != b:
        return (a > b) - (a < b)

    if not first.prerelease or not second.prerelease:
        return int(not first.prerelease) - int(not second.prerelease)

    for a, b in zip(first.prerelease, second.prerelease):
        if a == b:
            continue

        if a.isdigit() and b.isdigit():
            return (int(a) > int(b)) - (int(a) < int(b))

        if a.isdigit() != b.isdigit():
            return -1 if a.isdigit() else 1

        return (a > b) - (a < b)

    return (len(first.prerelease) > len(second.prerelease)) - (len(first.prerelease) < len(second.prerelease))


def exact_object(value: Any, fields: set[str], label: str) -> dict:
    """Reject unknown or absent fields instead of silently accepting unused knobs."""
    if not isinstance(value, dict) or set(value) != fields:
        raise LinePolicyError(f"{label} requires exactly: {', '.join(sorted(fields))}.")

    return value


def unique_object(pairs: list[tuple[str, Any]]) -> dict:
    """Retain JSON ambiguity as an error before ordinary dict parsing loses it."""
    value = {}
    for key, entry in pairs:
        if key in value:
            raise LinePolicyError(f"Duplicate JSON property: {key}.")

        value[key] = entry

    return value


def read_json(path: Path) -> Any:
    """Read bounded regular JSON without following a substituted symbolic link."""
    if path.is_symlink() or not path.is_file() or path.stat().st_size > 1024 * 1024:
        raise LinePolicyError(f"JSON evidence must be a bounded regular file: {path.name}.")

    try:
        return json.loads(path.read_bytes(), object_pairs_hook=unique_object)
    except (ValueError, UnicodeError, OSError) as error:
        raise LinePolicyError(f"Cannot read JSON evidence: {path.name}.") from error


def _choice(value: Any, choices: tuple[str, ...], label: str) -> str:
    """Validate strict scalar enums without truthiness or string coercion."""
    if not isinstance(value, str) or value not in choices:
        raise LinePolicyError(f"{label} must be one of {', '.join(choices)}.")

    return value


def _identity(line: dict) -> None:
    """Require the exact canonical role/ref/workflow/environment association."""
    role = _choice(line["role"], ("main", "maintenance", "preview"), "Line role")
    major = line["packageMajor"]
    if type(major) is not int or major < 1:
        raise LinePolicyError("Line packageMajor must be a positive integer.")

    expected = AUTHORITY_REF if role == "main" else f"refs/heads/{role}/{major}.x"
    if line["sourceRef"] != expected:
        raise LinePolicyError(f"Line sourceRef must be exactly {expected}.")

    stage = _choice(line["upstreamStage"], ("ga", "preview", "rc"), "Upstream stage")
    if role != "preview" and stage != "ga":
        raise LinePolicyError("Main and maintenance lines require GA upstream dependencies.")

    if line["workflowPath"] != WORKFLOW_PATH or line["publisherEnvironment"] != "nuget":
        raise LinePolicyError("Every line must retain the existing workflow and nuget environment.")


def validate_policy(value: Any) -> dict:
    """Validate reviewed policy data; callers separately establish trusted authority."""
    value = exact_object(value, POLICY_FIELDS, "Release-line policy")
    if type(value["schemaVersion"]) is not int or value["schemaVersion"] != 1:
        raise LinePolicyError("Release-line policy requires integer schemaVersion 1.")

    if not isinstance(value["repository"], str) or REPOSITORY.fullmatch(value["repository"]) is None:
        raise LinePolicyError("Release-line repository must be an exact owner/name.")

    if value["authorityRef"] != AUTHORITY_REF:
        raise LinePolicyError("Current release authority must remain protected main.")

    if (not isinstance(value["revision"], str)
            or re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}", value["revision"]) is None):
        raise LinePolicyError("Policy revision must be a canonical ASCII token.")

    entries = value["lines"]
    if not isinstance(entries, list) or not entries:
        raise LinePolicyError("Release-line policy requires nonempty lines.")

    refs, owners, main_count = set(), set(), 0
    for line in entries:
        exact_object(line, LINE_FIELDS, "Release line")
        _identity(line)
        state = _choice(line["state"], ("inactive", "prepared", "active"), "Line state")
        channel = _choice(line["channel"], ("stable-and-prerelease", "prerelease"), "Line channel")
        if (line["role"] == "preview") != (channel == "prerelease"):
            raise LinePolicyError("Only preview roles use the prerelease-only channel.")

        if line["sourceRef"] in refs:
            raise LinePolicyError("Duplicate exact release-line sourceRef.")

        refs.add(line["sourceRef"])
        main_count += int(line["role"] == "main")
        if state == "active":
            if line["packageMajor"] in owners:
                raise LinePolicyError("Only one active publisher may own a package major.")

            owners.add(line["packageMajor"])

    if main_count != 1:
        raise LinePolicyError("Policy must retain exactly one main identity.")

    return copy.deepcopy(value)


def parse_policy(value: str | bytes) -> dict:
    """Validate downloaded policy with duplicate-key detection and strict shape."""
    try:
        return validate_policy(json.loads(value, object_pairs_hook=unique_object))
    except (ValueError, UnicodeError, TypeError) as error:
        raise LinePolicyError("Downloaded release-line policy is not valid JSON.") from error


def load_policy(path: Path | None = None) -> dict:
    """Load policy for inspection; a checkout does not thereby gain publish rights."""
    return validate_policy(read_json(path or POLICY_PATH))


def policy_digest(policy: dict) -> str:
    """Bind the complete reviewed policy independent of JSON presentation."""
    data = json.dumps(validate_policy(policy), sort_keys=True, separators=(",", ":"), ensure_ascii=True)

    return hashlib.sha256(data.encode("ascii")).hexdigest()


def _channel(line: dict, version: str) -> str:
    """Bind the exact package major and prerelease/stable channel."""
    parsed = parse_version(version)
    if parsed.major != line["packageMajor"]:
        raise LinePolicyError("Version major does not match its exact authorized line.")

    if line["role"] == "preview" and not parsed.is_prerelease:
        raise LinePolicyError("Preview lines cannot publish stable packages.")

    return "prerelease" if parsed.is_prerelease else "stable"


def select_line(policy: dict, source_ref: str, version: str | None = None, *,
                purpose: str = "qualification") -> dict:
    """Select an exact registered ref without converting qualification into rights."""
    allowed = {"inspect": ("inactive", "prepared", "active"),
               "qualification": ("prepared", "active"), "publication": ("active",)}
    if not isinstance(purpose, str) or purpose not in allowed:
        raise LinePolicyError("Selection purpose must be inspect, qualification or publication.")

    value = validate_policy(policy)
    selected = next((line for line in value["lines"] if line["sourceRef"] == source_ref), None)
    if selected is None or selected["state"] not in allowed[purpose]:
        raise LinePolicyError(f"Exact source ref is not eligible for {purpose}: {source_ref}.")

    if version is not None:
        _channel(selected, version)

    return selected


def freeze_line(policy: dict, source_ref: str, version: str, *, workflow_path: str | None = None) -> dict:
    """Freeze qualification evidence while retaining current-authority separation."""
    selected = select_line(policy, source_ref, version)
    if workflow_path is not None and workflow_path != selected["workflowPath"]:
        raise LinePolicyError("Qualification workflow does not match its registered entry.")

    value = {key: selected[key] for key in LINE_FIELDS - {"state", "channel"}}
    value.update(schemaVersion=1, repository=policy["repository"], channel=_channel(selected, version),
                 stateAtQualification=selected["state"], authorizationRevision=policy["revision"],
                 authorizationDigest=policy_digest(policy))

    return validate_snapshot(value, source_ref=source_ref, version=version)


def validate_snapshot(value: Any, *, source_ref: str | None = None, version: str | None = None,
                      workflow_path: str | None = None, repository: str | None = None) -> dict:
    """Verify historical frozen identity without requiring continued active status."""
    value = exact_object(value, SNAPSHOT_FIELDS, "Frozen release-line snapshot")
    if type(value["schemaVersion"]) is not int or value["schemaVersion"] != 1:
        raise LinePolicyError("Frozen snapshot requires schemaVersion 1.")

    _identity(value)
    if not isinstance(value["repository"], str) or REPOSITORY.fullmatch(value["repository"]) is None:
        raise LinePolicyError("Frozen repository identity is invalid.")

    _choice(value["channel"], ("stable", "prerelease"), "Frozen channel")
    _choice(value["stateAtQualification"], ("prepared", "active"), "Frozen lifecycle")
    if (not isinstance(value["authorizationRevision"], str)
            or re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}", value["authorizationRevision"]) is None):
        raise LinePolicyError("Frozen authorization revision is missing.")

    if not isinstance(value["authorizationDigest"], str) or DIGEST.fullmatch(value["authorizationDigest"]) is None:
        raise LinePolicyError("Frozen authorization digest is invalid.")

    if value["role"] == "preview" and value["channel"] != "prerelease":
        raise LinePolicyError("Frozen preview source cannot represent a stable package.")

    if version is not None and _channel(value, version) != value["channel"]:
        raise LinePolicyError("Frozen channel does not match its version.")

    expected = {"sourceRef": source_ref, "workflowPath": workflow_path, "repository": repository}
    if any(item is not None and value[key] != item for key, item in expected.items()):
        raise LinePolicyError("Frozen snapshot differs from the expected qualification identity.")

    return copy.deepcopy(value)


def _property(path: Path, name: str, *, allow_default: bool = False) -> str:
    """Read a single unambiguous source property, not an environment override."""
    if path.is_symlink() or not path.is_file():
        raise LinePolicyError(f"Toolchain file must be regular: {path.name}.")

    try:
        root = ElementTree.parse(path).getroot()
    except (ElementTree.ParseError, OSError) as error:
        raise LinePolicyError(f"Cannot parse toolchain contract: {path.name}.") from error

    values = list(root.iter(name))
    if len(values) != 1 or not values[0].text or not values[0].text.strip():
        raise LinePolicyError(f"Toolchain contract must define one {name}.")

    parents = {child: parent for parent in root.iter() for child in parent}
    current = values[0]
    while current is not None:
        condition = current.get("Condition")
        if condition and not (current is values[0] and allow_default and condition == f"'$({name})' == ''"):
            raise LinePolicyError(f"Toolchain property {name} has an unreviewed condition.")

        current = parents.get(current)

    if name == "TargetFramework" and list(root.iter("TargetFrameworks")):
        raise LinePolicyError("A release line must select one target framework.")

    return values[0].text.strip()


def validate_toolchain(value: Any, snapshot: dict, *, resolved_packages: dict[str, str] | None = None) -> dict:
    """Reject foreign SDK/TFM/EF majors and unreviewed upstream stages."""
    frozen = validate_snapshot(snapshot)
    value = exact_object(value, TOOLCHAIN_FIELDS, "Frozen branch toolchain")
    major, stage = frozen["packageMajor"], frozen["upstreamStage"]
    sdk = parse_version(value["approvedDotnetSdk"])
    sdk_stage = sdk.prerelease[0] if sdk.prerelease else "ga"
    if sdk.major != major or sdk_stage != stage or value["upstreamStage"] != stage:
        raise LinePolicyError("SDK identity differs from the line major/upstream stage.")

    if value["targetFramework"] != f"net{major}.0":
        raise LinePolicyError("Target framework differs from the selected line major.")

    _choice(value["testRunner"], ("VSTest", "Microsoft.Testing.Platform"), "Test runner")
    if not isinstance(value["efCoreVersion"], str):
        raise LinePolicyError("Branch EF policy must be a canonical version or interval.")

    interval = re.fullmatch(r"\[([^,\[\]]+), *([^,\[\]]+)\)", value["efCoreVersion"])
    upper = None
    if interval:
        lower, upper = interval.groups()
        minimum, maximum = parse_version(lower), parse_version(upper)
        if (stage != "ga" or minimum.major != major or minimum.is_prerelease or maximum.is_prerelease
                or maximum.major not in (major, major + 1)
                or (maximum.major == major + 1 and (maximum.minor, maximum.patch) != (0, 0))
                or compare_versions(lower, upper) >= 0):
            raise LinePolicyError("EF bounds must retain the selected stable line major.")
    else:
        lower = value["efCoreVersion"]
        exact = parse_version(lower)
        if exact.major != major or (exact.prerelease[0] if exact.prerelease else "ga") != stage:
            raise LinePolicyError("Exact EF identity differs from the selected line.")

    if resolved_packages is not None:
        if (not isinstance(resolved_packages, dict)
                or not {"Microsoft.EntityFrameworkCore.Design", "Microsoft.EntityFrameworkCore.Relational"}
                .issubset(resolved_packages)):
            raise LinePolicyError("Resolved EF graph must include Design and Relational identities.")

        versions = set()
        for package, version in resolved_packages.items():
            if not isinstance(package, str) or not isinstance(version, str):
                raise LinePolicyError("Resolved packages must map exact names to exact versions.")

            if not package.startswith("Microsoft.EntityFrameworkCore"):
                continue

            parsed = parse_version(version)
            if parsed.major != major or (parsed.prerelease[0] if parsed.prerelease else "ga") != stage:
                raise LinePolicyError("Resolved EF package differs from the selected line major/stage.")

            if (compare_versions(version, lower) < 0 or (upper is None and version != lower)
                    or (upper is not None and compare_versions(version, upper) >= 0)):
                raise LinePolicyError("Resolved EF package is outside the reviewed branch bounds.")

            versions.add(version)

        # WHY: individually legal ranges do not make a mixed EF assembly graph
        # coherent. Each branch qualifies one actual resolved EF patch identity.
        if len(versions) != 1:
            raise LinePolicyError("Resolved EF packages must share one exact version.")

    return copy.deepcopy(value)


def inspect_toolchain(repo: Path, snapshot: dict) -> dict:
    """Read branch-owned SDK, framework and EF contracts without installing tools."""
    sdk_data = read_json(repo / "global.json")
    sdk = sdk_data.get("sdk") if isinstance(sdk_data, dict) else None
    frozen = validate_snapshot(snapshot)
    if (not isinstance(sdk, dict) or sdk.get("rollForward") != "disable"
            or sdk.get("allowPrerelease") is not (frozen["upstreamStage"] != "ga")):
        raise LinePolicyError("Branch SDK must disable roll-forward and match its upstream stage.")

    test = sdk_data.get("test", {})
    if not isinstance(test, dict):
        raise LinePolicyError("Branch test runner configuration must be an object.")

    value = {"approvedDotnetSdk": sdk.get("version"),
             "targetFramework": _property(repo / "Directory.Build.props", "TargetFramework"),
             "efCoreVersion": _property(repo / "Directory.Packages.props", "EfCorePackageVersion", allow_default=True),
             "testRunner": test.get("runner", "VSTest"), "upstreamStage": frozen["upstreamStage"]}

    resolved = {}
    locks = sorted((repo / "src").glob("*/packages.lock.json"))
    if len(locks) != 5:
        raise LinePolicyError("Qualification requires all five branch-owned product lockfiles.")

    for lock in locks:
        graph = read_json(lock)
        dependencies = graph.get("dependencies") if isinstance(graph, dict) else None
        if not isinstance(dependencies, dict) or set(dependencies) != {value["targetFramework"]}:
            raise LinePolicyError("Product lockfile does not match the branch target framework.")

        packages = dependencies[value["targetFramework"]]
        if not isinstance(packages, dict):
            raise LinePolicyError("Product lockfile package inventory is invalid.")

        for package, metadata in packages.items():
            if not package.startswith("Microsoft.EntityFrameworkCore"):
                continue

            version = metadata.get("resolved") if isinstance(metadata, dict) else None
            if not isinstance(version, str) or (package in resolved and resolved[package] != version):
                raise LinePolicyError("Product locks contain inconsistent EF package identities.")

            resolved[package] = version

    return validate_toolchain(value, frozen, resolved_packages=resolved)


def main() -> int:
    """Provide the existing pre-tag script one nonpublishing exact-line inspection."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("inspect",))
    parser.add_argument("--source-ref", required=True)
    parser.add_argument("--version")
    parser.add_argument("--purpose", choices=("inspect", "qualification", "publication"), default="inspect")
    args = parser.parse_args()
    try:
        selected = select_line(load_policy(), args.source_ref, args.version, purpose=args.purpose)
        print(f"Release line verified: {selected['sourceRef']} ({selected['state']}).")
    except (LinePolicyError, OSError) as error:
        print(f"Release-line validation failed: {error}", file=sys.stderr)

        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
