#!/usr/bin/env bash

set -euo pipefail

readonly sbom_tool_version="4.1.5"

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

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
repo_root="$(cd "$script_dir/.." && pwd -P)"
package_dir="$(cd "$package_dir" && pwd -P)"
mkdir -p "$output_dir"
output_dir="$(cd "$output_dir" && pwd -P)"

if find "$output_dir" -mindepth 1 -maxdepth 1 -print -quit | grep -q .; then
    echo "SBOM output directory must be empty: $output_dir" >&2
    exit 1
fi

case "$(uname -s)-$(uname -m)" in
    Linux-x86_64)
        asset_name="sbom-tool-linux-x64"
        expected_sha256="bf5d4f99bc98c119d549d08fc02ae92598a7a42772f17317c01031a92632e05b"
        ;;
    Darwin-arm64)
        asset_name="sbom-tool-osx-arm64"
        expected_sha256="bb25842fd707fbe78d3ac9de0d2b27ee2f4a97764f3b8a5c2068c826e75f3535"
        ;;
    Darwin-x86_64)
        asset_name="sbom-tool-osx-x64"
        expected_sha256="e9a45e3ffdcab920c7bbd2987ce0a133f275241e080bb48c1a3dbe6b558e8ee6"
        ;;
    *)
        echo "Unsupported SBOM host: $(uname -s)-$(uname -m)" >&2
        exit 1
        ;;
esac

temporary_root="${TMPDIR:-/tmp}"
work_dir="$(mktemp -d "$temporary_root/safemigrations-sbom.XXXXXX")"
case "$work_dir" in
    "$temporary_root"/safemigrations-sbom.*) ;;
    *)
        echo "Unexpected temporary directory: $work_dir" >&2
        exit 1
        ;;
esac

cleanup() {
    rm -rf -- "$work_dir"
}
trap cleanup EXIT

tool_path="$work_dir/$asset_name"
tool_url="https://github.com/microsoft/sbom-tool/releases/download/v$sbom_tool_version/$asset_name"
curl --fail --silent --show-error --location --retry 3 "$tool_url" --output "$tool_path"
actual_sha256="$(shasum -a 256 "$tool_path" | awk '{print $1}')"
if [[ "$actual_sha256" != "$expected_sha256" ]]; then
    echo "SBOM tool checksum mismatch." >&2
    exit 1
fi
chmod 0755 "$tool_path"

drop_dir="$work_dir/drop"
mkdir -p "$drop_dir"
find "$package_dir" -maxdepth 1 -type f \
    \( -name '*.nupkg' -o -name '*.snupkg' -o -name 'SHA256SUMS' \) \
    -exec cp {} "$drop_dir" \;

component_root="$work_dir/source"
mkdir -p "$component_root"
rsync -a \
    --exclude '.fastembed_cache/' \
    --exclude '.git/' \
    --exclude 'artifacts/' \
    --exclude 'bin/' \
    --exclude 'obj/' \
    "$repo_root/" "$component_root/"

dotnet restore "$component_root/Doka.EntityFrameworkCore.SafeMigrations.slnx" \
    --locked-mode \
    --disable-parallel \
    --disable-build-servers \
    -m:1 \
    /nodeReuse:false

"$tool_path" generate \
    -b "$drop_dir" \
    -bc "$component_root" \
    -pn Doka.EntityFrameworkCore.SafeMigrations \
    -pv "$package_version" \
    -ps Doka-Labs \
    -nsb https://github.com/doka-labs/Doka.EntityFrameworkCore.SafeMigrations/sbom \
    -mi SPDX:2.2

validation_output="$work_dir/validation.json"
"$tool_path" validate \
    -b "$drop_dir" \
    -o "$validation_output" \
    -mi SPDX:2.2 \
    -n

manifest_path="$drop_dir/_manifest/spdx_2.2/manifest.spdx.json"
jq -e \
    --arg expected_name "Doka.EntityFrameworkCore.SafeMigrations $package_version" \
    '.spdxVersion == "SPDX-2.2"
        and .name == $expected_name
        and (.files | length) == 7
        and (.packages | length) > 0
        and any(.packages[]; .name == "Doka.EntityFrameworkCore.MySql")
        and any(.packages[]; .name == "Npgsql.EntityFrameworkCore.PostgreSQL")
        and any(.packages[]; .name == "Microsoft.EntityFrameworkCore.Relational")' \
    "$manifest_path" >/dev/null

cp -R "$drop_dir/_manifest" "$output_dir/"
jq \
    --arg tool_version "$sbom_tool_version" \
    '{
        schemaVersion: 1,
        tool: "Microsoft SBOM Tool",
        toolVersion: $tool_version,
        result: .Result,
        filesSuccessful: .Summary.ValidationTelemetery.FilesSuccessfulCount,
        filesInManifest: .Summary.ValidationTelemetery.TotalFilesInManifest,
        packagesInManifest: .Summary.ValidationTelemetery.TotalPackagesInManifest,
        filesFailed: .Summary.ValidationTelemetery.FilesFailedCount,
        validationErrors: .ValidationErrors.Count
    }' \
    "$validation_output" > "$output_dir/validation.json"
(
    cd "$output_dir"
    find . -type f ! -name SHA256SUMS -exec shasum -a 256 {} \; \
        | LC_ALL=C sort > SHA256SUMS
    shasum -a 256 -c SHA256SUMS
)

echo "SPDX 2.2 SBOM generated and validated with Microsoft SBOM Tool $sbom_tool_version."
