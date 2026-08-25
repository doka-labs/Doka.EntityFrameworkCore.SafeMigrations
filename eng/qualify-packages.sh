#!/usr/bin/env bash

set -euo pipefail

usage() {
    echo "Usage: $0 --version <version> --output <path> --doka-source <path-or-url> [--require-stable-dependencies]" >&2
}

package_version=""
output_dir=""
doka_source=""
require_stable_dependencies=false

while (($# > 0)); do
    case "$1" in
        --version)
            package_version="${2:-}"
            shift 2
            ;;
        --output)
            output_dir="${2:-}"
            shift 2
            ;;
        --doka-source)
            doka_source="${2:-}"
            shift 2
            ;;
        --require-stable-dependencies)
            require_stable_dependencies=true
            shift
            ;;
        *)
            usage
            exit 2
            ;;
    esac
done

if [[ -z "$package_version" || -z "$output_dir" || -z "$doka_source" ]]; then
    usage
    exit 2
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
repo_root="$(cd "$script_dir/.." && pwd -P)"
mkdir -p "$output_dir"
output_dir="$(cd "$output_dir" && pwd -P)"

if find "$output_dir" -mindepth 1 -maxdepth 1 -print -quit | grep -q .; then
    echo "Output directory must be empty: $output_dir" >&2
    exit 1
fi

temporary_root="${TMPDIR:-/tmp}"
work_dir="$(mktemp -d "$temporary_root/safemigrations-pack.XXXXXX")"
case "$work_dir" in
    "$temporary_root"/safemigrations-pack.*) ;;
    *)
        echo "Unexpected temporary directory: $work_dir" >&2
        exit 1
        ;;
esac

cleanup() {
    rm -rf -- "$work_dir"
}
trap cleanup EXIT

first_pack="$work_dir/first"
second_pack="$work_dir/second"
mkdir -p "$first_pack" "$second_pack"

pack_once() {
    local destination="$1"

    dotnet pack "$repo_root/Doka.EntityFrameworkCore.SafeMigrations.slnx" \
        --configuration Release \
        --no-build \
        --no-restore \
        --output "$destination" \
        --disable-build-servers \
        -m:1 \
        /nodeReuse:false \
        -p:ContinuousIntegrationBuild=true \
        -p:PackageVersion="$package_version"
}

pack_once "$first_pack"
pack_once "$second_pack"

package_ids=(
    Doka.EntityFrameworkCore.SafeMigrations
    Doka.EntityFrameworkCore.SafeMigrations.MySql
    Doka.EntityFrameworkCore.SafeMigrations.PostgreSql
)

for package_id in "${package_ids[@]}"; do
    for extension in nupkg snupkg; do
        file_name="$package_id.$package_version.$extension"
        cmp "$first_pack/$file_name" "$second_pack/$file_name"
        install -m 0644 "$first_pack/$file_name" "$output_dir/$file_name"
    done
done

dotnet run \
    --project "$repo_root/eng/Doka.EntityFrameworkCore.SafeMigrations.NuGetSymbolReadback/Doka.EntityFrameworkCore.SafeMigrations.NuGetSymbolReadback.csproj" \
    --configuration Release \
    --no-build \
    --no-restore \
    -- \
    --package-dir "$output_dir" \
    --version "$package_version" \
    --output "$output_dir/SYMBOLS.json"

content_arguments=(
    --package-dir "$output_dir"
    --version "$package_version"
)
if [[ "$require_stable_dependencies" == true ]]; then
    content_arguments+=(--require-stable-dependencies)
fi
"$script_dir/verify-package-contents.sh" "${content_arguments[@]}"

"$script_dir/verify-package-consumer.sh" \
    --package-dir "$output_dir" \
    --version "$package_version" \
    --doka-source "$doka_source"

(
    cd "$output_dir"
    shasum -a 256 ./*.nupkg ./*.snupkg SYMBOLS.json | LC_ALL=C sort > SHA256SUMS
)

echo "SafeMigrations package qualification passed with byte-identical pack output."
