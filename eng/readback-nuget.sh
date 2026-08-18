#!/usr/bin/env bash

set -euo pipefail

usage() {
    echo "Usage: $0 --package-dir <path> --output <path> --version <version>" >&2
}

package_dir=""
output_dir=""
package_version=""

while (($# > 0)); do
    case "$1" in
        --package-dir)
            package_dir="${2:-}"
            shift 2
            ;;
        --output)
            output_dir="${2:-}"
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

if [[ -z "$package_dir" || -z "$output_dir" || -z "$package_version" ]]; then
    usage
    exit 2
fi

package_dir="$(cd "$package_dir" && pwd -P)"
mkdir -p "$output_dir"
output_dir="$(cd "$output_dir" && pwd -P)"
if find "$output_dir" -mindepth 1 -maxdepth 1 -print -quit | grep -q .; then
    echo "Readback output directory must be empty: $output_dir" >&2
    exit 1
fi

temporary_root="${TMPDIR:-/tmp}"
work_dir="$(mktemp -d "$temporary_root/safemigrations-readback.XXXXXX")"
case "$work_dir" in
    "$temporary_root"/safemigrations-readback.*) ;;
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

for package_id in "${package_ids[@]}"; do
    lower_id="$(printf '%s' "$package_id" | tr '[:upper:]' '[:lower:]')"
    lower_version="$(printf '%s' "$package_version" | tr '[:upper:]' '[:lower:]')"
    file_name="$package_id.$package_version.nupkg"
    published_package="$work_dir/$file_name"
    package_url="https://api.nuget.org/v3-flatcontainer/$lower_id/$lower_version/$lower_id.$lower_version.nupkg"

    downloaded=false
    for _ in {1..60}; do
        if curl --fail --silent --show-error --location \
            "$package_url" --output "$published_package"; then
            downloaded=true
            break
        fi
        sleep 10
    done
    if [[ "$downloaded" != true ]]; then
        echo "NuGet readback timed out for $package_id $package_version." >&2
        exit 1
    fi

    dotnet nuget verify "$published_package" --all

    expected_dir="$work_dir/expected-$lower_id"
    published_dir="$work_dir/published-$lower_id"
    mkdir -p "$expected_dir" "$published_dir"
    unzip -q "$package_dir/$file_name" -d "$expected_dir"
    unzip -q "$published_package" -d "$published_dir"

    signature_count="$(find "$published_dir" -type f -name '.signature.p7s' | wc -l | tr -d ' ')"
    if [[ "$signature_count" != "1" ]]; then
        echo "Expected exactly one NuGet repository signature in $file_name." >&2
        exit 1
    fi
    signature_path="$(find "$published_dir" -type f -name '.signature.p7s')"
    rm -- "$signature_path"
    diff -r "$expected_dir" "$published_dir"
    install -m 0644 "$published_package" "$output_dir/$file_name"
done

(
    cd "$output_dir"
    shasum -a 256 ./*.nupkg | LC_ALL=C sort > SIGNED_SHA256SUMS
)

echo "NuGet repository-signature and content readback verification passed."
