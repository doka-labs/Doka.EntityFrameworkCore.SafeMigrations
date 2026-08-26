#!/usr/bin/env bash

set -euo pipefail

package_version="${1:-}"
if [[ -z "$package_version" || $# -ne 1 ]]; then
    echo "Usage: $0 <package-version>" >&2
    exit 2
fi

if ((${#package_version} > 64)) \
    || [[ ! "$package_version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9a-z-]+(\.[0-9a-z-]+)*)?$ ]]; then
    echo "Package version must be a canonical lowercase NuGet version without a leading v." >&2
    exit 1
fi

if [[ "$package_version" == *-* ]]; then
    prerelease="${package_version#*-}"
    IFS='.' read -r -a identifiers <<<"$prerelease"

    for identifier in "${identifiers[@]}"; do
        if [[ -z "$identifier" || "$identifier" == -* || "$identifier" == *- \
            || ("$identifier" =~ ^[0-9]+$ && "$identifier" == 0[0-9]*) ]]; then
            echo "Package version contains a non-canonical prerelease identifier." >&2
            exit 1
        fi
    done
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
version_prefix="$(
    sed -n 's:.*<VersionPrefix>\([^<]*\)</VersionPrefix>.*:\1:p' \
        "$repo_root/src/Directory.Build.props"
)"

if [[ -z "$version_prefix" \
    || ("$package_version" != "$version_prefix" && "$package_version" != "$version_prefix"-*) ]]; then
    echo "Package version must belong to the source release line $version_prefix." >&2
    exit 1
fi

changelog_entries="$(
    grep -Ec "^## \\[$package_version\\] - [0-9]{4}-[0-9]{2}-[0-9]{2}$" \
        "$repo_root/CHANGELOG.md" || true
)"
if [[ "$changelog_entries" != 1 ]]; then
    echo "CHANGELOG.md must contain exactly one dated entry for $package_version." >&2
    exit 1
fi

if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    {
        echo "package-version=$package_version"
        echo "release-tag=v$package_version"
    } >>"$GITHUB_OUTPUT"
fi

echo "Release version verified: $package_version."
