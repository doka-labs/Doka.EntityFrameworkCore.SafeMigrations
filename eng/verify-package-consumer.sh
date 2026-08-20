#!/usr/bin/env bash

set -euo pipefail

usage() {
    echo "Usage: $0 --package-dir <path> --version <version> --doka-source <path-or-url>" >&2
}

package_dir=""
package_version=""
doka_source=""

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
        --doka-source)
            doka_source="${2:-}"
            shift 2
            ;;
        *)
            usage
            exit 2
            ;;
    esac
done

if [[ -z "$package_dir" || -z "$package_version" || -z "$doka_source" ]]; then
    usage
    exit 2
fi

package_dir="$(cd "$package_dir" && pwd -P)"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"

for package_id in \
    Doka.EntityFrameworkCore.SafeMigrations \
    Doka.EntityFrameworkCore.SafeMigrations.MySql \
    Doka.EntityFrameworkCore.SafeMigrations.PostgreSql; do
    test -f "$package_dir/$package_id.$package_version.nupkg"
    test -f "$package_dir/$package_id.$package_version.snupkg"
done

temporary_root="${TMPDIR:-/tmp}"
work_dir="$(mktemp -d "$temporary_root/safemigrations-consumer.XXXXXX")"
case "$work_dir" in
    "$temporary_root"/safemigrations-consumer.*) ;;
    *)
        echo "Unexpected temporary directory: $work_dir" >&2
        exit 1
        ;;
esac

cleanup() {
    rm -rf -- "$work_dir"
}
trap cleanup EXIT

verify_consumer() {
    local consumer_name="$1"
    local consumer_dir="$work_dir/$consumer_name"
    local assets_file="$consumer_dir/obj/project.assets.json"
    local -a restore_args

    mkdir -p "$consumer_dir"
    cp "$script_dir/package-consumer/$consumer_name/PackageConsumer.csproj" "$consumer_dir/"
    cp "$script_dir/package-consumer/$consumer_name/Program.cs" "$consumer_dir/"

    restore_args=(
        "$consumer_dir/PackageConsumer.csproj"
        --packages "$work_dir/packages"
        --source "$package_dir"
        --source "$doka_source"
        --source "https://api.nuget.org/v3/index.json"
        --use-lock-file
        --disable-parallel
        -p:SafeMigrationsPackageVersion="$package_version"
    )

    dotnet restore "${restore_args[@]}"
    dotnet restore "${restore_args[@]}" --locked-mode

    if grep -Fq '"type": "project"' "$assets_file"; then
        echo "$consumer_name package consumer unexpectedly resolved a ProjectReference." >&2
        exit 1
    fi

    grep -Fq 'Doka.EntityFrameworkCore.SafeMigrations/' "$assets_file"

    case "$consumer_name" in
        MySql)
            grep -Fq 'Doka.EntityFrameworkCore.SafeMigrations.MySql/' "$assets_file"
            grep -Fq 'Doka.EntityFrameworkCore.MySql/' "$assets_file"

            if grep -Fq 'Doka.EntityFrameworkCore.SafeMigrations.PostgreSql/' "$assets_file" \
                || grep -Fq 'Npgsql.EntityFrameworkCore.PostgreSQL/' "$assets_file"; then
                echo "MySQL/MariaDB consumer resolved PostgreSQL assets." >&2
                exit 1
            fi
            ;;
        PostgreSql)
            grep -Fq 'Doka.EntityFrameworkCore.SafeMigrations.PostgreSql/' "$assets_file"
            grep -Fq 'Npgsql.EntityFrameworkCore.PostgreSQL/' "$assets_file"

            if grep -Fq 'Doka.EntityFrameworkCore.SafeMigrations.MySql/' "$assets_file" \
                || grep -Fq 'Doka.EntityFrameworkCore.MySql/' "$assets_file"; then
                echo "PostgreSQL consumer resolved MySQL/MariaDB assets." >&2
                exit 1
            fi
            ;;
        *)
            echo "Unknown package consumer: $consumer_name" >&2
            exit 1
            ;;
    esac

    dotnet build "$consumer_dir/PackageConsumer.csproj" \
        --configuration Release \
        --no-restore \
        --disable-build-servers \
        -p:SafeMigrationsPackageVersion="$package_version"

    dotnet run \
        --project "$consumer_dir/PackageConsumer.csproj" \
        --configuration Release \
        --no-build \
        --no-restore \
        -p:SafeMigrationsPackageVersion="$package_version"
}

verify_consumer MySql
verify_consumer PostgreSql
