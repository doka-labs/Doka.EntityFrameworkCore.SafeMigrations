#!/usr/bin/env bash

set -euo pipefail

package_version="${1:-}"
qualification_only=false
if [[ $# -eq 3 && "$2" == --qualification-only && ("$3" == true || "$3" == false) ]]; then
    qualification_only="$3"
elif [[ $# -ne 1 ]]; then
    echo "Usage: $0 <package-version> [--qualification-only <true|false>]" >&2
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

if [[ ! "$version_prefix" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]; then
    echo "Source must define one canonical stable VersionPrefix." >&2
    exit 1
fi

# WHY: A released snapshot needs an unused version for nonpublishing qualification.
# Only that explicit intent may decouple the patch/changelog. Protected authority,
# branch/toolchain identity and public tag/package absence are still checked later.
if [[ "$qualification_only" == true ]]; then
    if [[ "${package_version%%.*}" != "${version_prefix%%.*}" ]]; then
        echo "Qualification version must belong to source major ${version_prefix%%.*}." >&2
        exit 1
    fi
elif [[ "$package_version" != "$version_prefix" && "$package_version" != "$version_prefix"-* ]]; then
    echo "Package version must belong to the source release line $version_prefix." >&2
    exit 1
fi

if [[ "$qualification_only" == false ]]; then
    changelog_entries="$(
        grep -Ec "^## \\[$package_version\\] - [0-9]{4}-[0-9]{2}-[0-9]{2}$" \
            "$repo_root/CHANGELOG.md" || true
    )"
    if [[ "$changelog_entries" != 1 ]]; then
        echo "CHANGELOG.md must contain exactly one dated entry for $package_version." >&2
        exit 1
    fi
fi

if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    {
        echo "package-version=$package_version"
        echo "release-tag=v$package_version"
    } >>"$GITHUB_OUTPUT"
fi

echo "Release version verified: $package_version."
