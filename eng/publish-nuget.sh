#!/usr/bin/env bash

set -euo pipefail

usage() {
    echo "Usage: $0 --package-dir <path> --version <version> --mode <preflight|publish>" >&2
}

package_dir=""
package_version=""
mode=""

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
        --mode)
            mode="${2:-}"
            shift 2
            ;;
        *)
            usage
            exit 2
            ;;
    esac
done

if [[ -z "$package_dir"
    || -z "$package_version"
    || ("$mode" != "preflight" && "$mode" != "publish") ]]; then
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
symbol_manifest="$package_dir/SYMBOLS.json"

if [[ ! -f "$symbol_manifest" ]]; then
    echo "Qualified symbol manifest is missing: $symbol_manifest" >&2
    exit 1
fi

require_api_key() {
    if [[ -z "${NUGET_API_KEY:-}" ]]; then
        echo "NUGET_API_KEY is required only while missing packages are published." >&2
        exit 2
    fi
}

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
    status="$(curl --silent --show-error --location --connect-timeout 10 --max-time 60 \
        --output "$downloaded_package" \
        --write-out '%{http_code}' "$package_url")"

    case "$status" in
        404)
            return
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
    existing_package_matches=true
    echo "Existing NuGet package matches qualified bytes: $file_name"
}

verify_existing_symbols() {
    local package_id="$1"
    local entry
    local symbol_url
    local checksum_header
    local expected_sha256
    local published_symbol
    local status
    local actual_sha256

    entry="$(
        jq -r \
            --arg package_id "$package_id" \
            '.symbols[]
                | select(.packageId == $package_id)
                | [.symbolUrl, .checksumHeader, .sha256]
                | @tsv' \
            "$symbol_manifest"
    )"
    if [[ -z "$entry" ]]; then
        echo "Qualified symbol manifest omits $package_id." >&2
        exit 1
    fi

    IFS=$'\t' read -r symbol_url checksum_header expected_sha256 <<<"$entry"
    published_symbol="$work_dir/published-$package_id.pdb"
    status="$(
        curl --silent --show-error --location \
            --connect-timeout 10 \
            --max-time 60 \
            --header "SymbolChecksumValidationSupported: 1" \
            --header "SymbolChecksum: $checksum_header" \
            --output "$published_symbol" \
            --write-out '%{http_code}' \
            "$symbol_url"
    )"

    case "$status" in
        404)
            return
            ;;
        200) ;;
        *)
            echo "NuGet returned HTTP $status while checking symbols for $package_id." >&2
            exit 1
            ;;
    esac

    actual_sha256="$(shasum -a 256 "$published_symbol" | awk '{print $1}')"
    if [[ "$(head -c 4 "$published_symbol")" != "BSJB"
        || "$actual_sha256" != "$expected_sha256" ]]; then
        echo "Existing NuGet symbols differ from the qualified Portable PDB: $package_id" >&2
        exit 1
    fi

    existing_symbols_match=true
    echo "Existing NuGet symbols match qualified bytes: $package_id"
}

publication_required=false

for package_id in "${package_ids[@]}"; do
    existing_package_matches=false
    verify_existing_package "$package_id"
    existing_symbols_match=false
    verify_existing_symbols "$package_id"

    if [[ "$existing_package_matches" != true || "$existing_symbols_match" != true ]]; then
        publication_required=true
    fi

    if [[ "$mode" == "preflight" ]]; then
        continue
    fi

    if [[ "$existing_package_matches" != true ]]; then
        require_api_key
        dotnet nuget push \
            "$package_dir/$package_id.$package_version.nupkg" \
            --api-key "$NUGET_API_KEY" \
            --source https://api.nuget.org/v3/index.json \
            --no-symbols \
            --skip-duplicate
    fi

    if [[ "$existing_symbols_match" != true ]]; then
        require_api_key
        dotnet nuget push \
            "$package_dir/$package_id.$package_version.snupkg" \
            --api-key "$NUGET_API_KEY" \
            --source https://api.nuget.org/v3/index.json \
            --skip-duplicate
    fi
done

if [[ "$mode" == "preflight" ]]; then
    if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
        echo "publication_required=$publication_required" >> "$GITHUB_OUTPUT"
    else
        echo "publication_required=$publication_required"
    fi

    echo "NuGet publication preflight completed."
else
    echo "NuGet package and symbol publication requests completed; final readback remains authoritative."
fi
