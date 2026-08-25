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
symbol_manifest="$package_dir/SYMBOLS.json"

if [[ ! -f "$symbol_manifest" ]]; then
    echo "Qualified symbol manifest is missing: $symbol_manifest" >&2
    exit 1
fi

mkdir -p "$output_dir/symbols"
readback_deadline=$((SECONDS + 3600))

for package_id in "${package_ids[@]}"; do
    lower_id="$(printf '%s' "$package_id" | tr '[:upper:]' '[:lower:]')"
    lower_version="$(printf '%s' "$package_version" | tr '[:upper:]' '[:lower:]')"
    file_name="$package_id.$package_version.nupkg"
    published_package="$work_dir/$file_name"
    package_url="https://api.nuget.org/v3-flatcontainer/$lower_id/$lower_version/$lower_id.$lower_version.nupkg"

    downloaded=false
    while ((SECONDS < readback_deadline)); do
        if curl --fail --silent --show-error --location \
            --connect-timeout 10 \
            --max-time 60 \
            "$package_url" --output "$published_package"; then
            downloaded=true
            break
        fi

        if ((SECONDS < readback_deadline)); then
            sleep 10
        fi
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
    if ! diff -r "$expected_dir" "$published_dir"; then
        echo "Published $file_name differs from the qualified package." >&2
        exit 1
    fi
    install -m 0644 "$published_package" "$output_dir/$file_name"

    symbol_entry="$(
        jq -r \
            --arg package_id "$package_id" \
            '.symbols[]
                | select(.packageId == $package_id)
                | [.pdbName, .symbolUrl, .checksumHeader, .sha256]
                | @tsv' \
            "$symbol_manifest"
    )"
    if [[ -z "$symbol_entry" ]]; then
        echo "Qualified symbol manifest omits $package_id." >&2
        exit 1
    fi

    IFS=$'\t' read -r pdb_name symbol_url checksum_header expected_symbol_sha256 \
        <<<"$symbol_entry"
    published_symbol="$work_dir/$pdb_name"
    symbol_downloaded=false

    while ((SECONDS < readback_deadline)); do
        if ! status="$(
            curl --silent --show-error --location \
                --connect-timeout 10 \
                --max-time 60 \
                --header "SymbolChecksumValidationSupported: 1" \
                --header "SymbolChecksum: $checksum_header" \
                --output "$published_symbol" \
                --write-out '%{http_code}' \
                "$symbol_url"
        )"; then
            if ((SECONDS < readback_deadline)); then
                sleep 10
            fi

            continue
        fi

        case "$status" in
            200)
                actual_symbol_sha256="$(shasum -a 256 "$published_symbol" | awk '{print $1}')"
                if [[ "$(head -c 4 "$published_symbol")" != "BSJB"
                    || "$actual_symbol_sha256" != "$expected_symbol_sha256" ]]; then
                    echo "NuGet returned conflicting symbols for $package_id." >&2
                    exit 1
                fi

                symbol_downloaded=true
                break
                ;;
            404|408|429|5??)
                if ((SECONDS < readback_deadline)); then
                    sleep 10
                fi
                ;;
            *)
                echo "NuGet returned HTTP $status while reading symbols for $package_id." >&2
                exit 1
                ;;
        esac
    done

    if [[ "$symbol_downloaded" != true ]]; then
        echo "NuGet symbol readback timed out for $package_id $package_version." >&2
        exit 1
    fi

    install -m 0644 "$published_symbol" "$output_dir/symbols/$pdb_name"
done

checksum_file="$work_dir/SIGNED_SHA256SUMS"
(
    cd "$output_dir"
    find . -type f -exec shasum -a 256 {} \; | LC_ALL=C sort > "$checksum_file"
)
install -m 0644 "$checksum_file" "$output_dir/SIGNED_SHA256SUMS"

echo "NuGet package, repository-signature, and symbol readback verification passed."
