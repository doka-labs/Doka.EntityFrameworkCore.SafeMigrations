#!/usr/bin/env bash

set -euo pipefail

usage() {
    echo "Usage: $0 --package-dir <path> --version <version> --mode <preflight|publish>" >&2
    echo "    [--timeout-seconds <1-3600>] [--poll-interval-seconds <1-60>]" >&2
}

package_dir=""
package_version=""
mode=""
timeout_seconds=300
poll_interval_seconds=10

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
        --timeout-seconds)
            timeout_seconds="${2:-}"
            shift 2
            ;;
        --poll-interval-seconds)
            poll_interval_seconds="${2:-}"
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

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
# shellcheck source=eng/release/nuget-observation.sh
source "$script_dir/release/nuget-observation.sh"
nuget_validate_polling "$timeout_seconds" "$poll_interval_seconds"

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

    lower_id="$(printf '%s' "$package_id" | tr '[:upper:]' '[:lower:]')"
    lower_version="$(printf '%s' "$package_version" | tr '[:upper:]' '[:lower:]')"
    file_name="$package_id.$package_version.nupkg"
    downloaded_package="$work_dir/published-$lower_id.nupkg"
    package_url="https://api.nuget.org/v3-flatcontainer/$lower_id/$lower_version/$lower_id.$lower_version.nupkg"

    while ((SECONDS < observation_deadline)); do
        nuget_request "$package_id" "$package_url" "$downloaded_package" "$observation_deadline"
        case "$nuget_http_state" in
            absent) return ;;
            available)
                if ! nuget_compare_package "$package_dir/$file_name" "$downloaded_package" \
                    "$work_dir/$lower_id.verify.log"; then
                    nuget_record "$package_id" conflict
                    exit 1
                fi
                existing_package_matches=true
                nuget_record "$package_id" "$nuget_package_state"
                return
                ;;
        esac
        nuget_wait "$observation_deadline" "$poll_interval_seconds" || break
    done
    echo "NuGet preflight timed out for $package_id $package_version." >&2
    exit 1
}

verify_existing_symbols() {
    local package_id="$1"
    local entry
    local symbol_url
    local checksum_header
    local expected_sha256
    local published_symbol
    local pdb_name

    entry="$(nuget_symbol_entry "$symbol_manifest" "$package_id")"
    IFS=$'\t' read -r pdb_name symbol_url checksum_header expected_sha256 <<<"$entry"
    published_symbol="$work_dir/published-$package_id.pdb"
    while ((SECONDS < observation_deadline)); do
        nuget_request "$pdb_name" "$symbol_url" "$published_symbol" "$observation_deadline" \
            --header "SymbolChecksumValidationSupported: 1" \
            --header "SymbolChecksum: $checksum_header"
        case "$nuget_http_state" in
            absent) return ;;
            available)
                if ! nuget_compare_symbol "$published_symbol" "$expected_sha256" "$package_id"; then
                    nuget_record "$pdb_name" conflict
                    exit 1
                fi
                existing_symbols_match=true
                nuget_record "$pdb_name" matching-symbols
                return
                ;;
        esac
        nuget_wait "$observation_deadline" "$poll_interval_seconds" || break
    done
    echo "NuGet symbol preflight timed out for $package_id $package_version." >&2
    exit 1
}

publication_required=false
missing_payloads=()
observation_deadline=$((SECONDS + timeout_seconds))

for package_id in "${package_ids[@]}"; do
    existing_package_matches=false
    verify_existing_package "$package_id"
    existing_symbols_match=false
    verify_existing_symbols "$package_id"

    if [[ "$existing_package_matches" != true || "$existing_symbols_match" != true ]]; then
        publication_required=true
    fi

    if [[ "$existing_package_matches" != true ]]; then
        missing_payloads+=("$package_dir/$package_id.$package_version.nupkg")
    fi
    if [[ "$existing_symbols_match" != true ]]; then
        missing_payloads+=("$package_dir/$package_id.$package_version.snupkg")
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
    if [[ "$publication_required" == true ]]; then
        require_api_key
        for payload in "${missing_payloads[@]}"; do
            if [[ "$payload" == *.nupkg ]]; then
                dotnet nuget push "$payload" \
                    --api-key "$NUGET_API_KEY" \
                    --source https://api.nuget.org/v3/index.json \
                    --no-symbols --skip-duplicate
            else
                dotnet nuget push "$payload" \
                    --api-key "$NUGET_API_KEY" \
                    --source https://api.nuget.org/v3/index.json \
                    --skip-duplicate
            fi
        done
    fi
    echo "NuGet package and symbol publication requests completed; final readback remains authoritative."
fi
