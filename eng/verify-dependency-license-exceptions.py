"""Enforce exact versions for the repository's two package-license exceptions."""

import json
import re
import sys
from pathlib import Path
from urllib.parse import unquote


APPROVED_VERSIONS = {
    "microsoft.data.sqlclient.sni.runtime": "6.0.2",
    "microsoft.identity.client.nativeinterop": "0.20.6",
}


def normalized_string(value: object) -> str:
    """Normalize a string without accepting missing or non-string metadata."""
    return value.lower() if isinstance(value, str) else ""


def action_nuget_name(purl: str) -> str | None:
    """Resolve the name with the pinned action's permissive PURL semantics."""
    package_type = re.search(r"pkg:([a-zA-Z0-9_-]+)/.*", purl)

    if not purl.startswith("pkg:") or package_type is None or package_type[1].lower() != "nuget":
        return None

    parts = purl.split("/")

    if len(parts) < 2 or not parts[1]:
        return None

    namespace = unquote(parts[1], errors="strict") if len(parts) > 2 else None
    name_plus_rest = "/".join(parts[2:]) if len(parts) > 2 else parts[1]

    # This intentionally mirrors the action's unanchored name regex and its
    # namespace-only fallback. A stricter parser would miss malformed PURLs
    # the action nevertheless exempts, bypassing our exact-version bound.
    name_match = re.search(r"([^@#?]+)[@#?]?.*", name_plus_rest)
    name = unquote(name_match[1], errors="strict") if name_match else None
    full_name = f"{namespace}/{name}" if namespace and name else name or namespace

    return full_name.lower() if full_name else None


def exception_identity(dependency: dict) -> tuple[str | None, str]:
    """Recognize all identities that the action can exempt, before validation."""
    name = normalized_string(dependency.get("name"))
    raw_purl = normalized_string(dependency.get("package_url"))
    action_name = action_nuget_name(raw_purl)
    purl = unquote(raw_purl, errors="strict").lower()

    if normalized_string(dependency.get("ecosystem")) == "nuget" and name in APPROVED_VERSIONS:
        return name, purl

    if action_name in APPROVED_VERSIONS:
        return action_name, purl

    return None, purl


def verify(pages: object) -> list[str]:
    """Validate every gh api --paginate --slurp page and return rejected versions."""
    if not isinstance(pages, list) or not pages:
        raise ValueError("expected at least one dependency comparison page")

    rejected = set()
    for page in pages:
        if not isinstance(page, list):
            raise ValueError("expected dependency comparison page arrays")

        for dependency in page:
            if not isinstance(dependency, dict) or dependency.get("change_type") not in ("added", "removed"):
                raise ValueError("invalid dependency comparison change")

            if dependency["change_type"] == "removed":
                continue

            name, purl = exception_identity(dependency)

            if name is None:
                if not normalized_string(dependency.get("name")):
                    raise ValueError("dependency comparison omitted package identity")

                continue

            version = APPROVED_VERSIONS[name]
            if (
                normalized_string(dependency.get("ecosystem")) != "nuget"
                or normalized_string(dependency.get("name")) != name
                or dependency.get("version") != version
                or purl != f"pkg:nuget/{name}@{version}"
            ):
                actual = dependency.get("version")
                rejected.add(
                    f"{name} {actual if actual is not None else '<missing version>'}: "
                    f"license exception requires NuGet {name} {version} with consistent package metadata"
                )

    return sorted(rejected)


def main() -> int:
    """Read the hosted comparison, fail closed on invalid input, and report scope."""
    if len(sys.argv) != 2:
        print("usage: verify-dependency-license-exceptions.py <paginated-dependency-diff.json>", file=sys.stderr)

        return 2

    response_path = Path(sys.argv[1])

    if not response_path.is_file():
        print(f"dependency comparison response does not exist: {response_path}", file=sys.stderr)

        return 1

    try:
        with response_path.open(encoding="utf-8") as response:
            rejected = verify(json.load(response))
    except (OSError, ValueError) as error:
        print(f"could not validate dependency-license exception versions: {error}", file=sys.stderr)

        return 1

    if rejected:
        print("\n".join(rejected), file=sys.stderr)

        return 1

    print("dependency-license exceptions are restricted to the approved versions")

    return 0


if __name__ == "__main__":
    sys.exit(main())
