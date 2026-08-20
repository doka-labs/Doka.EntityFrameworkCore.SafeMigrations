#!/usr/bin/env bash

set -euo pipefail

usage() {
    echo "Usage: $0 --package-dir <path> --version <version> [--require-stable-dependencies]" >&2
}

package_dir=""
package_version=""
require_stable_dependencies=false

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

if [[ -z "$package_dir" || -z "$package_version" ]]; then
    usage
    exit 2
fi

package_dir="$(cd "$package_dir" && pwd -P)"
package_ids=(
    Doka.EntityFrameworkCore.SafeMigrations
    Doka.EntityFrameworkCore.SafeMigrations.MySql
    Doka.EntityFrameworkCore.SafeMigrations.PostgreSql
)

expected_files=()
for package_id in "${package_ids[@]}"; do
    expected_files+=(
        "$package_id.$package_version.nupkg"
        "$package_id.$package_version.snupkg"
    )
done

actual_files="$(
    find "$package_dir" -maxdepth 1 -type f \
        \( -name '*.nupkg' -o -name '*.snupkg' \) \
        -exec basename {} \; | LC_ALL=C sort
)"
sorted_expected="$(printf '%s\n' "${expected_files[@]}" | LC_ALL=C sort)"

if [[ "$actual_files" != "$sorted_expected" ]]; then
    echo "Package set differs from the exact release contract." >&2
    printf 'Expected:\n%s\n' "$sorted_expected" >&2
    printf 'Actual:\n%s\n' "$actual_files" >&2
    exit 1
fi

for package_id in "${package_ids[@]}"; do
    nupkg="$package_dir/$package_id.$package_version.nupkg"
    snupkg="$package_dir/$package_id.$package_version.snupkg"
    nuspec="$package_id.nuspec"

    unzip -tq "$nupkg"
    unzip -tq "$snupkg"

    nupkg_entries="$(unzip -Z1 "$nupkg")"
    snupkg_entries="$(unzip -Z1 "$snupkg")"
    nuspec_content="$(unzip -p "$nupkg" "$nuspec")"

    grep -Fxq "$nuspec" <<<"$nupkg_entries"
    grep -Fxq "README.md" <<<"$nupkg_entries"
    grep -Fxq "LICENSE" <<<"$nupkg_entries"
    grep -Fxq "lib/net10.0/$package_id.dll" <<<"$nupkg_entries"
    grep -Fxq "lib/net10.0/$package_id.xml" <<<"$nupkg_entries"
    grep -Fxq "lib/net10.0/$package_id.pdb" <<<"$snupkg_entries"

    grep -Fq "<id>$package_id</id>" <<<"$nuspec_content"
    grep -Fq "<version>$package_version</version>" <<<"$nuspec_content"
    grep -Fq '<group targetFramework="net10.0">' <<<"$nuspec_content"
    grep -Fq '<repository type="git" url="https://github.com/doka-labs/Doka.EntityFrameworkCore.SafeMigrations"' \
        <<<"$nuspec_content"

    if grep -Eq '<dependency id="(Pomelo\.EntityFrameworkCore\.MySql|Doka\.EntityFrameworkCore\.SafeMigrations\.MariaDb)"' \
        <<<"$nuspec_content"; then
        echo "Legacy provider dependency found in $nupkg." >&2
        exit 1
    fi
done

core_package="$package_dir/Doka.EntityFrameworkCore.SafeMigrations.$package_version.nupkg"
core_entries="$(unzip -Z1 "$core_package")"
core_nuspec="$(unzip -p "$core_package" Doka.EntityFrameworkCore.SafeMigrations.nuspec)"
grep -Fxq "schemas/safe-migration-run-report-v1.schema.json" <<<"$core_entries"

if grep -Eq '<dependency id="(Doka\.EntityFrameworkCore\.MySql|Npgsql\.EntityFrameworkCore\.PostgreSQL|Doka\.EntityFrameworkCore\.SafeMigrations\.(MySql|PostgreSql))"' \
    <<<"$core_nuspec"; then
    echo "Core package resolved a provider-specific dependency." >&2
    exit 1
fi

mysql_nuspec="$(unzip -p \
    "$package_dir/Doka.EntityFrameworkCore.SafeMigrations.MySql.$package_version.nupkg" \
    Doka.EntityFrameworkCore.SafeMigrations.MySql.nuspec)"
grep -Fq '<dependency id="Doka.EntityFrameworkCore.MySql"' <<<"$mysql_nuspec"
grep -Fq '<dependency id="Doka.EntityFrameworkCore.SafeMigrations"' <<<"$mysql_nuspec"
if grep -Eq '<dependency id="(Npgsql\.EntityFrameworkCore\.PostgreSQL|Doka\.EntityFrameworkCore\.SafeMigrations\.PostgreSql)"' \
    <<<"$mysql_nuspec"; then
    echo "MySQL/MariaDB package resolved a PostgreSQL dependency." >&2
    exit 1
fi

if [[ "$require_stable_dependencies" == true ]]; then
    doka_version="$(
        grep -o '<dependency id="Doka.EntityFrameworkCore.MySql" version="[^"]*"' \
            <<<"$mysql_nuspec" \
            | sed -E 's/.* version="([^"]*)"/\1/'
    )"
    if [[ -z "$doka_version" || "$doka_version" == *-* ]]; then
        echo "Stable release requires a stable Doka.EntityFrameworkCore.MySql dependency." >&2
        exit 1
    fi
fi

postgres_nuspec="$(unzip -p \
    "$package_dir/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.$package_version.nupkg" \
    Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.nuspec)"
grep -Fq '<dependency id="Npgsql.EntityFrameworkCore.PostgreSQL"' <<<"$postgres_nuspec"
grep -Fq '<dependency id="Doka.EntityFrameworkCore.SafeMigrations"' <<<"$postgres_nuspec"
if grep -Eq '<dependency id="(Doka\.EntityFrameworkCore\.MySql|Doka\.EntityFrameworkCore\.SafeMigrations\.MySql)"' \
    <<<"$postgres_nuspec"; then
    echo "PostgreSQL package resolved a MySQL/MariaDB dependency." >&2
    exit 1
fi

echo "SafeMigrations package contents verified."
