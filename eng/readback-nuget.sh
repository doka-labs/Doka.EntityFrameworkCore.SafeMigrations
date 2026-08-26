#!/usr/bin/env bash

set -euo pipefail

usage() {
    echo "Usage: $0 --package-dir <path> --version <version> [--timeout-seconds <seconds>]" >&2
}

package_dir=""
package_version=""
timeout_seconds=1200

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
        --timeout-seconds)
            timeout_seconds="${2:-}"
            shift 2
            ;;
        *)
            usage
            exit 2
            ;;
    esac
done

if [[ -z "$package_dir" || -z "$package_version" \
    || ! "$timeout_seconds" =~ ^[1-9][0-9]{0,3}$ || "$timeout_seconds" -gt 3600 ]]; then
    usage
    exit 2
fi

package_dir="$(cd "$package_dir" && pwd -P)"
temporary_root="$(cd "${TMPDIR:-/tmp}" && pwd -P)"
work_dir="$(mktemp -d "$temporary_root/safemigrations-readback.XXXXXX")"

cleanup() {
    rm -rf -- "$work_dir"
}
trap cleanup EXIT

package_ids=(
    Doka.EntityFrameworkCore.SafeMigrations
    Doka.EntityFrameworkCore.SafeMigrations.MySql
    Doka.EntityFrameworkCore.SafeMigrations.PostgreSql
)

compare_package_content() {
    local expected_package="$1"
    local published_package="$2"
    local expected_entries="$work_dir/expected-entries.txt"
    local published_entries="$work_dir/published-entries.txt"
    local entry

    unzip -Z1 "$expected_package" | LC_ALL=C sort >"$expected_entries"
    unzip -Z1 "$published_package" \
        | grep -Fvx '.signature.p7s' \
        | LC_ALL=C sort >"$published_entries"

    if [[ -n "$(unzip -Z1 "$expected_package" | LC_ALL=C sort | uniq -d)" \
        || -n "$(unzip -Z1 "$published_package" | LC_ALL=C sort | uniq -d)" ]]; then
        echo "NuGet package contains duplicate ZIP entries." >&2
        return 1
    fi

    cmp "$expected_entries" "$published_entries"

    while IFS= read -r entry; do
        cmp \
            <(unzip -p "$expected_package" "$entry") \
            <(unzip -p "$published_package" "$entry")
    done <"$expected_entries"
}

deadline=$((SECONDS + timeout_seconds))
for package_id in "${package_ids[@]}"; do
    lower_id="$(printf '%s' "$package_id" | tr '[:upper:]' '[:lower:]')"
    lower_version="$(printf '%s' "$package_version" | tr '[:upper:]' '[:lower:]')"
    expected_package="$package_dir/$package_id.$package_version.nupkg"
    published_package="$work_dir/$package_id.nupkg"
    package_url="https://api.nuget.org/v3-flatcontainer/$lower_id/$lower_version/$lower_id.$lower_version.nupkg"

    test -f "$expected_package"
    verified=false

    while ((SECONDS < deadline)); do
        if ! http_code="$(
            curl --silent --show-error --location \
                --connect-timeout 10 --max-time 60 \
                --output "$published_package" --write-out '%{http_code}' \
                "$package_url"
        )"; then
            echo "NuGet transport error while reading $package_id; retrying." >&2
            sleep 15
            continue
        fi

        case "$http_code" in
            200)
                if unzip -Z1 "$published_package" | grep -Fxq '.signature.p7s'; then
                    dotnet nuget verify "$published_package" --all
                    compare_package_content "$expected_package" "$published_package"
                    verified=true
                fi
                ;;
            404 | 408 | 429 | 5??) ;;
            *)
                echo "NuGet readback failed for $package_id with HTTP $http_code." >&2
                exit 1
                ;;
        esac

        if [[ "$verified" == true ]]; then
            break
        fi

        sleep 15
    done

    if [[ "$verified" != true ]]; then
        echo "NuGet readback timed out for $package_id $package_version." >&2
        exit 1
    fi

    echo "Verified published package: $package_id $package_version."
done

echo "NuGet repository signatures and package contents verified."
