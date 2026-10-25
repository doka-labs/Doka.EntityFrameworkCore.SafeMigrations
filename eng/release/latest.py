"""Decide GitHub Latest promotion inside the existing shared publisher boundary."""

import argparse
import json
import os
import re
import subprocess
import sys
from collections.abc import Callable, Mapping
from typing import Any

from eng.release.lines import LinePolicyError, compare_versions, parse_version


LOCK_GROUP = "safe-migrations-publication"
SHA1 = re.compile(r"[0-9a-f]{40}\Z")
REPOSITORY = re.compile(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\Z")


class LatestError(RuntimeError):
    """Indicate that current release ordering cannot be proven safely."""


def require_publisher_lock(environment: Mapping[str, str]) -> None:
    """Require the workflow-owned shared-publication context before promotion."""
    if (
        environment.get("GITHUB_ACTIONS") != "true"
        or environment.get("SAFE_MIGRATIONS_PUBLICATION_LOCK_GROUP") != LOCK_GROUP
        or environment.get("SAFE_MIGRATIONS_PUBLICATION_LOCK_HELD") != "1"
    ):
        raise LatestError("Latest promotion requires the shared publication lock.")


def unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    """Reject contradictory duplicate API fields instead of choosing the last."""
    result: dict[str, Any] = {}

    for key, value in pairs:
        if key in result:
            raise LatestError("GitHub returned duplicate release metadata fields.")

        result[key] = value

    return result


class GitHubLatestClient:
    """Read only current Latest and its annotated tag through the platform CLI."""

    def __init__(self, repository: str) -> None:
        """Bind every request to the workflow's exact repository identity."""
        if not REPOSITORY.fullmatch(repository):
            raise LatestError("Latest promotion requires an exact repository identity.")

        self.repository = repository

    def read_json(self, suffix: str, *, allow_missing: bool = False) -> dict[str, Any] | None:
        """Distinguish an actual HTTP 404 from transport, permission or API failure."""
        try:
            result = subprocess.run(
                [
                    "gh", "api", "--include",
                    "-H", "Accept: application/vnd.github+json",
                    "-H", "X-GitHub-Api-Version: 2022-11-28",
                    f"/repos/{self.repository}/{suffix}",
                ],
                capture_output=True,
                text=True,
                check=False,
                timeout=60,
            )
        except (OSError, subprocess.TimeoutExpired) as exception:
            raise LatestError("GitHub Latest metadata could not be read.") from exception

        # WHY: Error-text substring matching can turn permission or transport
        # failures containing '404' into permission to promote a first release.
        response = result.stdout.replace("\r\n", "\n")
        headers, separator, body = response.partition("\n\n")
        status_match = re.fullmatch(r"HTTP/[0-9.]+ ([0-9]{3})(?: [^\n]*)?", headers.split("\n")[0])
        if not separator or status_match is None or len(response) > 2 * 1024 * 1024:
            raise LatestError("GitHub returned invalid Latest response metadata.")

        status = int(status_match[1])
        try:
            value = json.loads(body, object_pairs_hook=unique_object)
        except (json.JSONDecodeError, ValueError) as exception:
            raise LatestError("GitHub returned invalid Latest response JSON.") from exception

        if not isinstance(value, dict):
            raise LatestError("GitHub returned a non-object Latest response.")

        if (
            status == 404
            and allow_missing
            and value.get("message") == "Not Found"
            and result.returncode in (0, 1)
        ):
            return None

        if status != 200 or result.returncode != 0:
            raise LatestError("GitHub Latest metadata request failed.")

        return value

    def get_latest(self) -> dict[str, Any] | None:
        """Read the canonical Latest endpoint; only its actual 404 means absent."""
        return self.read_json("releases/latest", allow_missing=True)

    def resolve_tag(self, tag: str) -> str:
        """Resolve a bounded annotated-tag chain without trusting target_commitish."""
        reference = self.read_json(f"git/ref/tags/{tag}")
        target = reference.get("object") if reference is not None else None
        if not isinstance(target, dict) or target.get("type") != "tag":
            raise LatestError("Current Latest must identify an annotated release tag.")

        seen: set[str] = set()
        for _ in range(8):
            target_sha = target.get("sha")
            if not isinstance(target_sha, str) or not SHA1.fullmatch(target_sha) or target_sha in seen:
                raise LatestError("Current Latest tag chain is invalid or cyclic.")

            seen.add(target_sha)
            tag_object = self.read_json(f"git/tags/{target_sha}")
            target = tag_object.get("object") if tag_object is not None else None
            if not isinstance(target, dict):
                raise LatestError("Current Latest tag object is invalid.")

            if target.get("type") == "commit":
                commit = target.get("sha")
                if not isinstance(commit, str) or not SHA1.fullmatch(commit):
                    raise LatestError("Current Latest tag commit identity is invalid.")

                return commit

            if target.get("type") != "tag":
                raise LatestError("Current Latest tag must ultimately identify a commit.")

        raise LatestError("Current Latest tag chain exceeds the safety limit.")


def decide_latest(
    version: str,
    latest: dict[str, Any] | None,
    resolve_tag: Callable[[str], str],
) -> bool:
    """Promote only a newer canonical stable version, never a preview or equal draft."""
    candidate = parse_version(version)
    if latest is None:
        return not candidate.is_prerelease

    release_id = latest.get("id")
    tag = latest.get("tag_name")
    if (
        type(release_id) is not int
        or release_id <= 0
        or not isinstance(tag, str)
        or not tag.startswith("v")
        or latest.get("draft") is not False
        or latest.get("prerelease") is not False
        or latest.get("immutable") is not True
    ):
        raise LatestError("Current Latest release identity is invalid.")

    try:
        current = parse_version(tag[1:])
        order = compare_versions(version, tag[1:])
    except LinePolicyError as exception:
        raise LatestError("Current Latest release version is not canonical.") from exception

    if current.is_prerelease:
        raise LatestError("Current Latest release identity is a prerelease.")

    # WHY: GitHub's target_commitish can name a mutable branch. A stable
    # release that blocks promotion still needs a real immutable tag identity.
    commit = resolve_tag(tag)
    if not isinstance(commit, str) or not SHA1.fullmatch(commit):
        raise LatestError("Current Latest source commit identity is invalid.")

    if order == 0:
        # WHY: Matching immutable releases are reconciled without an edit by
        # the shell adapter. A draft with the published version is a conflict.
        raise LatestError("Current Latest conflicts with this draft release identity.")

    return not candidate.is_prerelease and order > 0


def main() -> int:
    """Emit one explicit boolean for the existing gh release edit invocation."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True)
    arguments = parser.parse_args()

    try:
        require_publisher_lock(os.environ)
        client = GitHubLatestClient(os.environ.get("GITHUB_REPOSITORY", ""))
        promote = decide_latest(arguments.version, client.get_latest(), client.resolve_tag)
    except (LatestError, LinePolicyError) as exception:
        print(f"GitHub Latest decision failed: {exception}", file=sys.stderr)

        return 1

    print("true" if promote else "false")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
