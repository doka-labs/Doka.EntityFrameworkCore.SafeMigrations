#!/usr/bin/env bash

set -euo pipefail

usage() {
    echo "Usage: NUGET_API_KEY=<key> $0 --package-dir <path> --version <version>" >&2
}

package_dir=""
package_version=""

while (($# > 0)); do
    case "$1" in
        --package-dir)
            package_dir="${2:-}"
            shift 2
            ;;
        --version)
            package_version="${2:-}"
            shift 2
            ;;
        *)
            usage
            exit 2
            ;;
    esac
done

if [[ -z "$package_dir" || -z "$package_version" || -z "${NUGET_API_KEY:-}" ]]; then
    usage
    exit 2
fi

package_dir="$(cd "$package_dir" && pwd -P)"
temporary_root="$(cd "${TMPDIR:-/tmp}" && pwd -P)"
work_dir="$(mktemp -d "$temporary_root/safemigrations-publish.XXXXXX")"
case "$work_dir" in
    "$temporary_root"/safemigrations-publish.*) ;;
    *)
        echo "Unexpected temporary directory: $work_dir" >&2
        exit 1
        ;;
esac

cleanup() {
    rm -rf -- "$work_dir"
}
trap cleanup EXIT

package_ids=(
    Doka.EntityFrameworkCore.SafeMigrations
    Doka.EntityFrameworkCore.SafeMigrations.MySql
    Doka.EntityFrameworkCore.SafeMigrations.PostgreSql
)

verify_existing_package() {
    local package_id="$1"
    local lower_id
    local lower_version
    local file_name
    local downloaded_package
    local package_url
    local status
    local expected_dir
    local published_dir
    local signature_count
    local signature_path

    lower_id="$(printf '%s' "$package_id" | tr '[:upper:]' '[:lower:]')"
    lower_version="$(printf '%s' "$package_version" | tr '[:upper:]' '[:lower:]')"
    file_name="$package_id.$package_version.nupkg"
    downloaded_package="$work_dir/published-$lower_id.nupkg"
    package_url="https://api.nuget.org/v3-flatcontainer/$lower_id/$lower_version/$lower_id.$lower_version.nupkg"
    status="$(curl --silent --show-error --location --output "$downloaded_package" \
        --write-out '%{http_code}' "$package_url")"

    case "$status" in
        404)
            return 1
            ;;
        200) ;;
        *)
            echo "NuGet returned HTTP $status while checking $package_id." >&2
            exit 1
            ;;
    esac

    dotnet nuget verify "$downloaded_package" --all
    expected_dir="$work_dir/expected-$lower_id"
    published_dir="$work_dir/published-$lower_id"
    mkdir -p "$expected_dir" "$published_dir"
    unzip -q "$package_dir/$file_name" -d "$expected_dir"
    unzip -q "$downloaded_package" -d "$published_dir"
    signature_count="$(find "$published_dir" -type f -name '.signature.p7s' | wc -l | tr -d ' ')"
    if [[ "$signature_count" != "1" ]]; then
        echo "Existing $file_name has no unique NuGet repository signature." >&2
        exit 1
    fi
    signature_path="$(find "$published_dir" -type f -name '.signature.p7s')"
    rm -- "$signature_path"
    if ! diff -r "$expected_dir" "$published_dir"; then
        echo "Existing $file_name differs from the qualified package." >&2
        exit 1
    fi
    echo "Existing NuGet package matches qualified bytes: $file_name"
}

for package_id in "${package_ids[@]}"; do
    if verify_existing_package "$package_id"; then
        continue
    fi

    dotnet nuget push \
        "$package_dir/$package_id.$package_version.nupkg" \
        --api-key "$NUGET_API_KEY" \
        --source https://api.nuget.org/v3/index.json
done

echo "NuGet publication completed without accepting unverified duplicates."
