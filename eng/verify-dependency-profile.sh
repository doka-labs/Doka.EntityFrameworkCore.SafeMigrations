#!/usr/bin/env bash

set -euo pipefail

usage() {
    echo "Usage: $0 --ef-version <version> --npgsql-version <version> --di-version <version> --doka-source <path-or-url>" >&2
}

ef_version=""
npgsql_version=""
di_version=""
doka_source=""

while (($# > 0)); do
    case "$1" in
        --ef-version)
            ef_version="${2:-}"
            shift 2
            ;;
        --npgsql-version)
            npgsql_version="${2:-}"
            shift 2
            ;;
        --di-version)
            di_version="${2:-}"
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

if [[ -z "$ef_version" || -z "$npgsql_version" || -z "$di_version" || -z "$doka_source" ]]; then
    usage
    exit 2
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
repo_root="$(cd "$script_dir/.." && pwd -P)"
temporary_root="$(cd "${TMPDIR:-/tmp}" && pwd -P)"
work_dir="$(mktemp -d "$temporary_root/safemigrations-dependencies.XXXXXX")"
case "$work_dir" in
    "$temporary_root"/safemigrations-dependencies.*) ;;
    *)
        echo "Unexpected temporary directory: $work_dir" >&2
        exit 1
        ;;
esac

cleanup() {
    rm -rf -- "$work_dir"
}
trap cleanup EXIT

snapshot="$work_dir/source"
mkdir -p "$snapshot"
rsync -a \
    --exclude '.fastembed_cache/' \
    --exclude '.git/' \
    --exclude 'artifacts/' \
    --exclude 'bin/' \
    --exclude 'obj/' \
    "$repo_root/" "$snapshot/"

common_properties=(
    -p:EfCorePackageVersion="$ef_version"
    -p:NpgsqlPackageVersion="$npgsql_version"
    -p:DependencyInjectionPackageVersion="$di_version"
)

dotnet restore "$snapshot/Doka.EntityFrameworkCore.SafeMigrations.slnx" \
    --force-evaluate \
    --disable-parallel \
    --disable-build-servers \
    --source "$doka_source" \
    --source https://api.nuget.org/v3/index.json \
    -p:RestoreLockedMode=false \
    "${common_properties[@]}" \
    -m:1 \
    /nodeReuse:false

postgres_lock="$snapshot/tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests/packages.lock.json"
jq -e \
    --arg ef "$ef_version" \
    --arg npgsql "$npgsql_version" \
    '.dependencies["net10.0"]["Microsoft.EntityFrameworkCore.Relational"].resolved == $ef
        and .dependencies["net10.0"]["Npgsql.EntityFrameworkCore.PostgreSQL"].resolved == $npgsql' \
    "$postgres_lock" >/dev/null

dotnet build "$snapshot/Doka.EntityFrameworkCore.SafeMigrations.slnx" \
    --configuration Release \
    --no-restore \
    --disable-build-servers \
    "${common_properties[@]}" \
    -m:1 \
    /nodeReuse:false

for project in \
    tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Doka.EntityFrameworkCore.SafeMigrations.Tests.csproj \
    tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests.csproj \
    tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests.csproj; do
    dotnet test "$snapshot/$project" \
        --configuration Release \
        --no-build \
        --no-restore \
        --disable-build-servers \
        "${common_properties[@]}" \
        -m:1 \
        /nodeReuse:false
done

echo "Dependency profile verified: EF $ef_version, Npgsql $npgsql_version, DI $di_version."
