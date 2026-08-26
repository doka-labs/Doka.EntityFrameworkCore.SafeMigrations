#!/usr/bin/env bash

set -euo pipefail

usage() {
    echo "Usage: $0 --package-dir <path> --output <path> --version <version>" >&2
    echo "    [--timeout-seconds <1-3600>] [--poll-interval-seconds <1-60>]" >&2
}

package_dir=""
output_dir=""
package_version=""
timeout_seconds=3600
poll_interval_seconds=10

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

if [[ -z "$package_dir" || -z "$output_dir" || -z "$package_version" ]]; then
    usage
    exit 2
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
# shellcheck source=eng/release/nuget-observation.sh
source "$script_dir/release/nuget-observation.sh"
nuget_validate_polling "$timeout_seconds" "$poll_interval_seconds"

package_dir="$(cd "$package_dir" && pwd -P)"
mkdir -p "$output_dir"
output_dir="$(cd "$output_dir" && pwd -P)"
if ! existing_entry="$(find "$output_dir" -mindepth 1 -maxdepth 1 -print -quit)"; then
    echo "Cannot inspect readback output directory: $output_dir" >&2
    exit 1
fi
if [[ -n "$existing_entry" ]]; then
    echo "Readback output directory must be empty: $output_dir" >&2
    exit 1
fi

mkdir -p "$output_dir/diagnostics" "$output_dir/symbols"
nuget_observation_log="$output_dir/observations.log"

record_result() {
    local exit_code=$?
    printf 'exit_code=%s\n' "$exit_code" > "$output_dir/result.txt"
}
trap record_result EXIT

# Wait for diagnostic stderr to flush while preserving the live stdout stream.
exec 3>&1
(
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

    readback_deadline=$((SECONDS + timeout_seconds))
    verified_payloads=()

    for package_id in "${package_ids[@]}"; do
        lower_id="$(printf '%s' "$package_id" | tr '[:upper:]' '[:lower:]')"
        lower_version="$(printf '%s' "$package_version" | tr '[:upper:]' '[:lower:]')"
        file_name="$package_id.$package_version.nupkg"
        package_url="https://api.nuget.org/v3-flatcontainer/$lower_id/$lower_version/$lower_id.$lower_version.nupkg"

        downloaded=false
        attempt=0
        while ((SECONDS < readback_deadline)); do
            attempt=$((attempt + 1))
            published_package="$output_dir/diagnostics/$package_id.attempt-$attempt.nupkg"
            nuget_request "$package_id" "$package_url" "$published_package" "$readback_deadline"
            if [[ "$nuget_http_state" == available ]]; then
                if ! nuget_compare_package "$package_dir/$file_name" "$published_package" \
                    "$published_package.verify.log"; then
                    nuget_record "$package_id" conflict
                    exit 1
                fi
                nuget_record "$package_id" "$nuget_package_state"
                if [[ "$nuget_package_state" == matching-signed ]]; then
                    install -m 0644 "$published_package" "$output_dir/$file_name"
                    verified_payloads+=("./$file_name")
                    downloaded=true
                    break
                fi
            fi
            nuget_wait "$readback_deadline" "$poll_interval_seconds" || break
        done
        if [[ "$downloaded" != true ]]; then
            echo "NuGet readback timed out for $package_id $package_version; a matching signed package is required." >&2
            exit 1
        fi

        symbol_entry="$(nuget_symbol_entry "$symbol_manifest" "$package_id")"
        IFS=$'\t' read -r pdb_name symbol_url checksum_header expected_symbol_sha256 <<<"$symbol_entry"
        symbol_downloaded=false
        attempt=0

        while ((SECONDS < readback_deadline)); do
            attempt=$((attempt + 1))
            published_symbol="$output_dir/diagnostics/$package_id.attempt-$attempt.pdb"
            nuget_request "$pdb_name" "$symbol_url" "$published_symbol" "$readback_deadline" \
                --header "SymbolChecksumValidationSupported: 1" \
                --header "SymbolChecksum: $checksum_header"
            if [[ "$nuget_http_state" == available ]]; then
                if ! nuget_compare_symbol "$published_symbol" "$expected_symbol_sha256" "$package_id"; then
                    nuget_record "$pdb_name" conflict
                    exit 1
                fi
                install -m 0644 "$published_symbol" "$output_dir/symbols/$pdb_name"
                verified_payloads+=("./symbols/$pdb_name")
                nuget_record "$pdb_name" matching-symbols
                symbol_downloaded=true
                break
            fi
            nuget_wait "$readback_deadline" "$poll_interval_seconds" || break
        done
        if [[ "$symbol_downloaded" != true ]]; then
            echo "NuGet symbol readback timed out for $package_id $package_version." >&2
            exit 1
        fi
    done

    checksum_file="$output_dir/diagnostics/SIGNED_SHA256SUMS.pending"
    (
        cd "$output_dir"
        for payload in "${verified_payloads[@]}"; do
            shasum -a 256 "$payload"
        done
    ) | LC_ALL=C sort > "$checksum_file"
    mv -- "$checksum_file" "$output_dir/SIGNED_SHA256SUMS"

    echo "NuGet package, repository-signature, and symbol readback verification passed."
) 2>&1 1>&3 | tee -a "$output_dir/diagnostics/errors.log" >&2
exec 3>&-
